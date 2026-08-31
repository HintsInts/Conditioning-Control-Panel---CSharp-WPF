using System;
using System.IO;
using System.Text;

namespace ConditioningControlPanel.Services.Integrations.Chaster
{
    internal static class ChasterIntegrationLog
    {
        private static readonly object Gate = new();
        private static readonly string Root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ConditioningControlPanel", "chaster");
        private static readonly string LogPath = Path.Combine(Root, "integration.log");

        public static void Write(string message)
        {
            try
            {
                lock (Gate)
                {
                    Directory.CreateDirectory(Root);
                    File.AppendAllText(LogPath, $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}", new UTF8Encoding(false));
                }
            }
            catch { }
        }
    }
}
