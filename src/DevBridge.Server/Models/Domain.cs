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

public sealed record DeviceEvent(Guid Id, Guid DeviceId, string DeviceName, string Level, string Category, string Message, string? Details, DateTimeOffset OccurredAt, DateTimeOffset ReceivedAt);
public sealed record DownloadTicket(Guid SessionId, Guid BuildId, Guid DeviceId, DateTimeOffset ExpiresAt);
