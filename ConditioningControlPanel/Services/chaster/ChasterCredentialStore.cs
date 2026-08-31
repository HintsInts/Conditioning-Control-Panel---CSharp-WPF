using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ConditioningControlPanel.Services.Integrations.Chaster
{
    internal sealed class ChasterCredentialStore
    {
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("CCP.Chaster.DeviceCredential.v1");
        private readonly string _connectionPath;
        private readonly JsonSerializerOptions _json = new() { WriteIndented = true };

        public ChasterCredentialStore(string rootPath)
        {
            Directory.CreateDirectory(rootPath);
            _connectionPath = Path.Combine(rootPath, "connection.json");
        }

        public ActiveConnection? Load()
        {
            try
            {
                if (!File.Exists(_connectionPath)) return null;
                var stored = JsonSerializer.Deserialize<StoredConnection>(File.ReadAllText(_connectionPath), _json);
                if (stored == null || string.IsNullOrWhiteSpace(stored.BaseUrl) || string.IsNullOrWhiteSpace(stored.ProtectedToken))
                    return null;

                var encrypted = Convert.FromBase64String(stored.ProtectedToken);
                var clear = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
                var token = Encoding.UTF8.GetString(clear);
                CryptographicOperations.ZeroMemory(clear);
                if (string.IsNullOrWhiteSpace(token)) return null;

                return new ActiveConnection
                {
                    BaseUrl = stored.BaseUrl,
                    DeviceToken = token,
                    DeviceName = string.IsNullOrWhiteSpace(stored.DeviceName) ? "Conditioning Control Panel" : stored.DeviceName,
                    ConnectionKey = ChasterCcpClient.ParseConnectionKey(token)
                };
            }
            catch (Exception ex)
            {
                ChasterIntegrationLog.Write("Failed to load Chaster credential: " + ex.Message);
                return null;
            }
        }

        public void Save(string baseUrl, string token, string deviceName)
        {
            if (string.IsNullOrWhiteSpace(token)) throw new ArgumentException("Device token is empty.", nameof(token));
            var clear = Encoding.UTF8.GetBytes(token);
            try
            {
                var encrypted = ProtectedData.Protect(clear, Entropy, DataProtectionScope.CurrentUser);
                var stored = new StoredConnection
                {
                    BaseUrl = baseUrl,
                    ProtectedToken = Convert.ToBase64String(encrypted),
                    DeviceName = deviceName
                };
                WriteAtomic(_connectionPath, JsonSerializer.Serialize(stored, _json));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(clear);
            }
        }

        public void Clear()
        {
            try
            {
                if (File.Exists(_connectionPath)) File.Delete(_connectionPath);
            }
            catch (Exception ex)
            {
                ChasterIntegrationLog.Write("Failed to clear Chaster credential: " + ex.Message);
            }
        }

        private static void WriteAtomic(string path, string content)
        {
            var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
            File.WriteAllText(temp, content, new UTF8Encoding(false));
            File.Move(temp, path, true);
        }
    }
}
