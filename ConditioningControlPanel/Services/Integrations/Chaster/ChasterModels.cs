using System;
using System.Text.Json.Serialization;

namespace ConditioningControlPanel.Services.Integrations.Chaster
{
    internal sealed class ChasterCcpEvent
    {
        public string EventId { get; set; } = Guid.NewGuid().ToString();
        public string Type { get; set; } = string.Empty;
        public string CcSessionId { get; set; } = string.Empty;
        public string OccurredAt { get; set; } = string.Empty;
        public string? SessionName { get; set; }
        public int? PlannedDurationSeconds { get; set; }
        public string? Outcome { get; set; }
    }

    internal sealed class PairRequest
    {
        public string Code { get; set; } = string.Empty;
        public string DeviceName { get; set; } = "Conditioning Control Panel";
    }

    internal sealed class PairResponse
    {
        public string DeviceToken { get; set; } = string.Empty;
        public PairConnectionState? Connection { get; set; }
    }

    internal sealed class PairConnectionState
    {
        public bool CcpPaired { get; set; }
    }

    internal sealed class StoredConnection
    {
        public string BaseUrl { get; set; } = string.Empty;
        public string ProtectedToken { get; set; } = string.Empty;
        public string DeviceName { get; set; } = "Conditioning Control Panel";
    }

    internal sealed class ActiveConnection
    {
        public string BaseUrl { get; init; } = string.Empty;
        public string DeviceToken { get; init; } = string.Empty;
        public string DeviceName { get; init; } = "Conditioning Control Panel";
        // Non-secret routing identity parsed from the signed device token payload.
        // It prevents queued events from one Chaster lock/device being replayed through another.
        public string ConnectionKey { get; init; } = string.Empty;
    }

    internal sealed class QueuedChasterEvent
    {
        public string ConnectionKey { get; set; } = string.Empty;
        public ChasterCcpEvent Event { get; set; } = new();
    }

    public sealed class ChasterClientSnapshot
    {
        public bool IsPaired { get; init; }
        public string BaseUrl { get; init; } = string.Empty;
        public string Status { get; init; } = "Not connected";
        public int PendingEvents { get; init; }
        public int DeadLetterEvents { get; init; }
        public DateTimeOffset? LastSuccessfulContact { get; init; }
    }

    internal enum OutboxSendDisposition
    {
        Sent,
        RetryLater,
        AuthenticationRequired,
        DeadLetter
    }
}
