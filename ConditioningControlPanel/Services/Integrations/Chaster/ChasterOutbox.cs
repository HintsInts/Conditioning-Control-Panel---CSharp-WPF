using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ConditioningControlPanel.Services.Integrations.Chaster
{
    internal sealed class ChasterOutbox
    {
        private readonly string _outboxPath;
        private readonly string _deadLetterPath;
        private readonly object _writeGate = new();
        private readonly JsonSerializerOptions _json = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = false
        };

        public ChasterOutbox(string rootPath)
        {
            _outboxPath = Path.Combine(rootPath, "outbox");
            _deadLetterPath = Path.Combine(rootPath, "dead-letter");
            Directory.CreateDirectory(_outboxPath);
            Directory.CreateDirectory(_deadLetterPath);
        }

        public int PendingCount => SafeCount(_outboxPath, "*.json");
        public int DeadLetterCount => SafeCount(_deadLetterPath, "*.json");

        public void Enqueue(ChasterCcpEvent evt, string connectionKey)
        {
            var ticks = DateTime.UtcNow.Ticks.ToString("D19");
            var finalName = $"{ticks}-{evt.EventId}.json";
            var finalPath = Path.Combine(_outboxPath, finalName);
            var tempPath = finalPath + ".tmp-" + Guid.NewGuid().ToString("N");
            var payload = JsonSerializer.Serialize(new QueuedChasterEvent { ConnectionKey = connectionKey, Event = evt }, _json);

            lock (_writeGate)
            {
                File.WriteAllText(tempPath, payload, new UTF8Encoding(false));
                File.Move(tempPath, finalPath, false);
            }
        }

        public IReadOnlyList<string> ListPendingFiles()
        {
            try
            {
                return Directory.EnumerateFiles(_outboxPath, "*.json", SearchOption.TopDirectoryOnly)
                    .OrderBy(Path.GetFileName, StringComparer.Ordinal)
                    .ToArray();
            }
            catch
            {
                return Array.Empty<string>();
            }
        }

        public QueuedChasterEvent? Read(string path)
        {
            try
            {
                return JsonSerializer.Deserialize<QueuedChasterEvent>(File.ReadAllText(path), _json);
            }
            catch (Exception ex)
            {
                ChasterIntegrationLog.Write($"Outbox read failed for {Path.GetFileName(path)}: {ex.Message}");
                MoveToDeadLetter(path, "Unreadable outbox JSON: " + ex.Message);
                return null;
            }
        }

        public void Delete(string path)
        {
            try { File.Delete(path); }
            catch (Exception ex) { ChasterIntegrationLog.Write("Could not delete delivered outbox event: " + ex.Message); }
        }

        public void MoveToDeadLetter(string path, string reason)
        {
            try
            {
                if (!File.Exists(path)) return;
                var name = Path.GetFileName(path);
                var destination = Path.Combine(_deadLetterPath, name);
                if (File.Exists(destination)) destination = Path.Combine(_deadLetterPath, Guid.NewGuid().ToString("N") + "-" + name);
                File.Move(path, destination);
                File.WriteAllText(destination + ".error.txt", reason, new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                ChasterIntegrationLog.Write("Could not dead-letter outbox event: " + ex.Message);
            }
        }

        private static int SafeCount(string directory, string pattern)
        {
            try { return Directory.EnumerateFiles(directory, pattern, SearchOption.TopDirectoryOnly).Count(); }
            catch { return 0; }
        }
    }
}
