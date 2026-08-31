using System.Runtime.CompilerServices;

namespace ConditioningControlPanel.Services.Integrations.Chaster
{
    internal static class ChasterBootstrap
    {
        [ModuleInitializer]
        internal static void Initialize()
        {
            try { ChasterCcpClient.Instance.Initialize(); }
            catch { }
        }
    }
}
