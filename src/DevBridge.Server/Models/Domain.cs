using System.Collections.Concurrent;

namespace DevBridge.Models;

public sealed class TestSession
{
    public required Guid Id { get; init; }
    public required string ProjectName { get; init; }
    public required string AppName { get; init; }
    public string? PackageName { get; init; }
    public required string DashboardTokenHash { get; init; }
    public required string PairingTokenHash { get; init; }
    public required DateTimeOffset PairingExpiresAt { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public bool PairingConsumed { get; set; }
    public ConcurrentDictionary<Guid, ConnectedDevice> Devices { get; } = new();
    public ConcurrentDictionary<Guid, PublishedBuild> Builds { get; } = new();
    public ConcurrentQueue<DeviceEvent> Events { get; } = new();
}

public sealed class ConnectedDevice
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public required string TokenHash { get; init; }
    public string Platform { get; init; } = "Android";
    public string? PlatformVersion { get; init; }
    public IReadOnlyDictionary<string, string> Context { get; init; } = new Dictionary<string, string>();
    public DateTimeOffset PairedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastSeenAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class PublishedBuild
{
    public required Guid Id { get; init; }
    public required string OriginalFileName { get; init; }
    public required string StoredPath { get; init; }
    public required string VersionName { get; init; }
    public required long VersionCode { get; init; }
    public required long SizeBytes { get; init; }
    public required string Sha256 { get; init; }
    public DateTimeOffset UploadedAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed class DeviceEvent
{
    public required Guid Id { get; init; }
    public required Guid DeviceId { get; init; }
    public required string DeviceName { get; init; }
    public required string Level { get; init; }
    public required string Category { get; init; }
    public required string EventType { get; init; }
    public required string Message { get; init; }
    public string? Details { get; init; }
    public string? StackTrace { get; init; }
    public string? Fingerprint { get; init; }
    public string? Screen { get; init; }
    public Guid? BuildId { get; init; }
    public IReadOnlyDictionary<string, double> Metrics { get; init; } = new Dictionary<string, double>();
    public IReadOnlyDictionary<string, string> Context { get; init; } = new Dictionary<string, string>();
    public required DateTimeOffset OccurredAt { get; init; }
    public required DateTimeOffset ReceivedAt { get; init; }
}
public sealed record DownloadTicket(Guid SessionId, Guid BuildId, Guid DeviceId, DateTimeOffset ExpiresAt);
