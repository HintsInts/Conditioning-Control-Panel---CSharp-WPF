using System;
using System.IO;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace ConditioningControlPanel.Services.Integrations.Chaster
{
    public sealed class ChasterCcpClient : IDisposable
    {
        private static readonly Lazy<ChasterCcpClient> LazyInstance = new(() => new ChasterCcpClient());
        public static ChasterCcpClient Instance => LazyInstance.Value;

        private readonly string _rootPath;
        private readonly ChasterCredentialStore _credentials;
        private readonly ChasterOutbox _outbox;
        private readonly HttpClient _http;
        private readonly JsonSerializerOptions _json = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
        private readonly CancellationTokenSource _shutdown = new();
        private readonly SemaphoreSlim _flushGate = new(1, 1);
        private readonly SemaphoreSlim _wake = new(0, 1);
        private readonly object _sessionGate = new();
        private ActiveConnection? _connection;
        private Task? _senderLoop;
        private int _initialized;
        private string? _activeCcSessionId;
        private string? _activeConnectionKey;
        private string _status = "Not connected";
        private DateTimeOffset? _lastSuccessfulContact;

        public event Action? StateChanged;

        private ChasterCcpClient()
        {
            _rootPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ConditioningControlPanel", "chaster");
            Directory.CreateDirectory(_rootPath);
            _credentials = new ChasterCredentialStore(_rootPath);
            _outbox = new ChasterOutbox(_rootPath);
            _connection = _credentials.Load();
            _status = _connection == null ? "Not connected" : "Paired — checking server";
            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        }

        public void Initialize()
        {
            if (Interlocked.Exchange(ref _initialized, 1) != 0) return;
            _senderLoop = Task.Run(SenderLoopAsync);
            SignalSender();
        }

        public ChasterClientSnapshot GetSnapshot()
        {
            var connection = _connection;
            return new ChasterClientSnapshot
            {
                IsPaired = connection != null,
                BaseUrl = connection?.BaseUrl ?? string.Empty,
                Status = _status,
                PendingEvents = _outbox.PendingCount,
                DeadLetterEvents = _outbox.DeadLetterCount,
                LastSuccessfulContact = _lastSuccessfulContact
            };
        }

        public async Task PairAsync(string rawBaseUrl, string rawCode, string deviceName = "Conditioning Control Panel")
        {
            Initialize();
            var baseUrl = NormalizeBaseUrl(rawBaseUrl);
            var code = NormalizePairCode(rawCode);
            if (string.IsNullOrWhiteSpace(deviceName)) deviceName = "Conditioning Control Panel";

            SetStatus("Pairing…");
            lock (_sessionGate)
            {
                if (_activeCcSessionId != null)
                    throw new InvalidOperationException("End the currently running CCP session before changing the Chaster pairing.");
            }

            var request = new PairRequest { Code = code, DeviceName = deviceName };
            using var response = await SendJsonAsync(HttpMethod.Post, BuildUri(baseUrl, "api/ccp/pair"), request, null, CancellationToken.None);
            var responseText = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                SetStatus($"Pairing failed ({(int)response.StatusCode})");
                throw new InvalidOperationException(ExtractError(responseText, response.StatusCode));
            }

            var pair = JsonSerializer.Deserialize<PairResponse>(responseText, _json);
            if (pair == null || string.IsNullOrWhiteSpace(pair.DeviceToken))
                throw new InvalidOperationException("Pairing succeeded but the server returned no device token.");

            var connectionKey = ParseConnectionKey(pair.DeviceToken);
            _credentials.Save(baseUrl, pair.DeviceToken, deviceName);
            _connection = new ActiveConnection { BaseUrl = baseUrl, DeviceToken = pair.DeviceToken, DeviceName = deviceName, ConnectionKey = connectionKey };
            _lastSuccessfulContact = DateTimeOffset.Now;
            SetStatus("Connected");
            SignalSender();
        }

        public async Task<bool> CheckConnectionAsync()
        {
            Initialize();
            var connection = _connection;
            if (connection == null)
            {
                SetStatus("Not connected");
                return false;
            }

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, BuildUri(connection.BaseUrl, "api/ccp/status"));
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", connection.DeviceToken);
                using var response = await _http.SendAsync(request);
                if (response.IsSuccessStatusCode)
                {
                    _lastSuccessfulContact = DateTimeOffset.Now;
                    SetStatus("Connected");
                    SignalSender();
                    return true;
                }
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    SetStatus("Pairing expired or revoked — pair again");
                    return false;
                }
                SetStatus($"Server reachable, status check failed ({(int)response.StatusCode})");
                return false;
            }
            catch (Exception ex)
            {
                SetStatus("Offline — events will retry automatically");
                ChasterIntegrationLog.Write("Status check failed: " + ex.Message);
                return false;
            }
        }

        public async Task DisconnectAsync()
        {
            Initialize();
            var connection = _connection;
            if (connection == null)
            {
                _credentials.Clear();
                SetStatus("Not connected");
                return;
            }

            lock (_sessionGate)
            {
                if (_activeCcSessionId != null)
                    throw new InvalidOperationException("End the currently running CCP session before disconnecting Chaster.");
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, BuildUri(connection.BaseUrl, "api/ccp/disconnect"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", connection.DeviceToken);
            using var response = await _http.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync();
                throw new InvalidOperationException(ExtractError(body, response.StatusCode));
            }

            lock (_sessionGate) { _activeCcSessionId = null; _activeConnectionKey = null; }
            _connection = null;
            _credentials.Clear();
            SetStatus("Not connected");
        }

        /// <summary>
        /// Called only after CCP has successfully entered a real running session.
        /// This method performs a durable local write but never waits on the network.
        /// </summary>
        public void ReportSessionStarted(string? sessionName, double plannedMinutes)
        {
            Initialize();
            if (_connection == null) return;

            var connection = _connection;
            if (connection == null) return;

            lock (_sessionGate)
            {
                if (_activeCcSessionId != null)
                {
                    ChasterIntegrationLog.Write("Ignored duplicate session start because a CCP session is already active.");
                    return;
                }

                var ccSessionId = Guid.NewGuid().ToString();
                var evt = new ChasterCcpEvent
                {
                    EventId = Guid.NewGuid().ToString(),
                    Type = "session_started",
                    CcSessionId = ccSessionId,
                    OccurredAt = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
                    SessionName = string.IsNullOrWhiteSpace(sessionName) ? null : sessionName,
                    PlannedDurationSeconds = Math.Max(0, (int)Math.Round(plannedMinutes * 60.0))
                };

                // The logical session becomes active only after its start fact is durable on disk.
                if (!QueueEvent(evt, connection.ConnectionKey)) return;
                _activeCcSessionId = ccSessionId;
                _activeConnectionKey = connection.ConnectionKey;
            }
        }

        /// <summary>
        /// Called when CCP's own SessionEngine has authoritatively decided how the running session ended.
        /// </summary>
        public void ReportSessionEnded(bool completed, bool suppressAbandonTracking)
        {
            Initialize();
            if (_connection == null) return;

            lock (_sessionGate)
            {
                var ccSessionId = _activeCcSessionId;
                var connectionKey = _activeConnectionKey;
                if (ccSessionId == null || string.IsNullOrWhiteSpace(connectionKey))
                {
                    ChasterIntegrationLog.Write("Session end had no Chaster-tracked start; nothing was sent.");
                    return;
                }

                var outcome = completed ? "completed" : suppressAbandonTracking ? "cancelled" : "abandoned";
                var evt = new ChasterCcpEvent
                {
                    EventId = Guid.NewGuid().ToString(),
                    Type = "session_ended",
                    CcSessionId = ccSessionId,
                    OccurredAt = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
                    Outcome = outcome
                };

                // Never forget the active logical session until the terminal fact is durable.
                if (!QueueEvent(evt, connectionKey)) return;
                _activeCcSessionId = null;
                _activeConnectionKey = null;
            }
        }

        public Task FlushNowAsync()
        {
            Initialize();
            return FlushOutboxAsync(_shutdown.Token);
        }

        private bool QueueEvent(ChasterCcpEvent evt, string connectionKey)
        {
            try
            {
                _outbox.Enqueue(evt, connectionKey);
                ChasterIntegrationLog.Write($"Queued {evt.Type} {evt.EventId} for CCP session {evt.CcSessionId}.");
                StateChanged?.Invoke();
                SignalSender();
                return true;
            }
            catch (Exception ex)
            {
                ChasterIntegrationLog.Write("CRITICAL: could not persist Chaster event: " + ex);
                SetStatus("Chaster event could not be saved locally — check disk/log");
                return false;
            }
        }

        private async Task SenderLoopAsync()
        {
            while (!_shutdown.IsCancellationRequested)
            {
                try
                {
                    await _wake.WaitAsync(TimeSpan.FromSeconds(10), _shutdown.Token);
                }
                catch (OperationCanceledException) { break; }
                catch { }

                try { await FlushOutboxAsync(_shutdown.Token); }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { ChasterIntegrationLog.Write("Outbox flush failed: " + ex); }
            }
        }

        private async Task FlushOutboxAsync(CancellationToken cancellationToken)
        {
            if (!await _flushGate.WaitAsync(0, cancellationToken)) return;
            try
            {
                var connection = _connection;
                if (connection == null) return;

                foreach (var path in _outbox.ListPendingFiles())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var queued = _outbox.Read(path);
                    if (queued == null) continue;
                    if (!string.Equals(queued.ConnectionKey, connection.ConnectionKey, StringComparison.Ordinal))
                    {
                        _outbox.MoveToDeadLetter(path, "This event belongs to a previous Chaster session/device pairing and was intentionally not replayed through the current credential.");
                        ChasterIntegrationLog.Write($"Quarantined stale queued event {queued.Event.EventId} after Chaster re-pairing.");
                        continue;
                    }
                    var evt = queued.Event;

                    var disposition = await SendOutboxEventAsync(connection, evt, cancellationToken);
                    if (disposition == OutboxSendDisposition.Sent)
                    {
                        _outbox.Delete(path);
                        _lastSuccessfulContact = DateTimeOffset.Now;
                        SetStatus("Connected");
                        continue;
                    }
                    if (disposition == OutboxSendDisposition.DeadLetter)
                    {
                        _outbox.MoveToDeadLetter(path, "Server permanently rejected this event. See integration.log for response details.");
                        continue;
                    }
                    if (disposition == OutboxSendDisposition.AuthenticationRequired)
                    {
                        SetStatus("Pairing expired or revoked — pair again");
                        break;
                    }

                    // Preserve strict start-before-end ordering. If an earlier event could not be
                    // acknowledged, never jump ahead to a later event from the same CCP process.
                    SetStatus("Offline — queued events will retry");
                    break;
                }
            }
            finally
            {
                _flushGate.Release();
                StateChanged?.Invoke();
            }
        }

        private async Task<OutboxSendDisposition> SendOutboxEventAsync(ActiveConnection connection, ChasterCcpEvent evt, CancellationToken cancellationToken)
        {
            try
            {
                using var response = await SendJsonAsync(HttpMethod.Post, BuildUri(connection.BaseUrl, "api/ccp/events"), evt, connection.DeviceToken, cancellationToken);
                var body = await response.Content.ReadAsStringAsync(cancellationToken);

                if (response.IsSuccessStatusCode)
                    return OutboxSendDisposition.Sent;

                if (response.StatusCode == HttpStatusCode.Conflict && BodySaysRetryable(body))
                {
                    ChasterIntegrationLog.Write($"Server asked to retry {evt.EventId}: {body}");
                    return OutboxSendDisposition.RetryLater;
                }
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                    return OutboxSendDisposition.AuthenticationRequired;
                if (response.StatusCode == (HttpStatusCode)429 || (int)response.StatusCode >= 500)
                    return OutboxSendDisposition.RetryLater;

                if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound or HttpStatusCode.Gone or HttpStatusCode.UnprocessableEntity)
                {
                    ChasterIntegrationLog.Write($"Permanent server rejection for {evt.EventId}: HTTP {(int)response.StatusCode} {body}");
                    return OutboxSendDisposition.DeadLetter;
                }

                ChasterIntegrationLog.Write($"Retrying unexpected HTTP {(int)response.StatusCode} for {evt.EventId}: {body}");
                return OutboxSendDisposition.RetryLater;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return OutboxSendDisposition.RetryLater;
            }
            catch (HttpRequestException ex)
            {
                ChasterIntegrationLog.Write("Network error sending event: " + ex.Message);
                return OutboxSendDisposition.RetryLater;
            }
        }

        private async Task<HttpResponseMessage> SendJsonAsync(HttpMethod method, Uri uri, object body, string? bearer, CancellationToken cancellationToken)
        {
            using var request = new HttpRequestMessage(method, uri);
            if (!string.IsNullOrWhiteSpace(bearer))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
            request.Content = new StringContent(JsonSerializer.Serialize(body, _json), Encoding.UTF8, "application/json");
            return await _http.SendAsync(request, cancellationToken);
        }

        private static string NormalizeBaseUrl(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) throw new ArgumentException("Enter the Chaster extension server URL.");
            raw = raw.Trim();
            if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri)) throw new ArgumentException("The server URL is invalid.");
            if (uri.Scheme != Uri.UriSchemeHttps)
            {
                var local = uri.Scheme == Uri.UriSchemeHttp && (uri.IsLoopback || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase));
                if (!local) throw new ArgumentException("Use HTTPS. Plain HTTP is allowed only for localhost testing.");
            }
            var builder = new UriBuilder(uri) { Query = string.Empty, Fragment = string.Empty };
            var normalized = builder.Uri.ToString();
            return normalized.EndsWith("/", StringComparison.Ordinal) ? normalized : normalized + "/";
        }

        private static string NormalizePairCode(string raw)
        {
            var code = (raw ?? string.Empty).Trim().ToUpperInvariant();
            if (!Regex.IsMatch(code, "^[A-Z0-9]{4}-[A-Z0-9]{4}$"))
                throw new ArgumentException("Connection code must look like ABCD-EFGH.");
            return code;
        }

        private static Uri BuildUri(string baseUrl, string relative) => new(new Uri(baseUrl, UriKind.Absolute), relative);

        private static bool BodySaysRetryable(string body)
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                return doc.RootElement.TryGetProperty("retryable", out var retryable) && retryable.ValueKind == JsonValueKind.True;
            }
            catch { return false; }
        }

        private static string ExtractError(string body, HttpStatusCode status)
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                foreach (var key in new[] { "error", "message" })
                    if (doc.RootElement.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String)
                        return value.GetString() ?? $"HTTP {(int)status}";
            }
            catch { }
            return string.IsNullOrWhiteSpace(body) ? $"HTTP {(int)status}" : body;
        }

        private void SignalSender()
        {
            try { if (_wake.CurrentCount == 0) _wake.Release(); }
            catch (SemaphoreFullException) { }
        }

        private void SetStatus(string status)
        {
            _status = status;
            StateChanged?.Invoke();
        }

        internal static string ParseConnectionKey(string token)
        {
            try
            {
                var body = token.Split('.')[0];
                body = body.Replace('-', '+').Replace('_', '/');
                body = body.PadRight(body.Length + ((4 - body.Length % 4) % 4), '=');
                using var doc = JsonDocument.Parse(Convert.FromBase64String(body));
                var root = doc.RootElement;
                var sessionId = root.GetProperty("sessionId").GetString();
                var deviceId = root.GetProperty("deviceId").GetString();
                if (!string.IsNullOrWhiteSpace(sessionId) && !string.IsNullOrWhiteSpace(deviceId))
                    return sessionId + "|" + deviceId;
            }
            catch (Exception ex)
            {
                ChasterIntegrationLog.Write("Could not parse device routing identity; using credential fingerprint: " + ex.Message);
            }

            // Fallback is deliberately token-specific: it is safer to quarantine pending events
            // after a re-pair than to risk replaying them into a different Chaster lock.
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
            return Convert.ToHexString(hash);
        }

        public void Dispose()
        {
            _shutdown.Cancel();
            _http.Dispose();
            _shutdown.Dispose();
            _flushGate.Dispose();
            _wake.Dispose();
        }
    }
}
