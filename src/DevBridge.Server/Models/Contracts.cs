namespace DevBridge.Models;

public sealed record CreateSessionRequest(string ProjectName, string? AppName, string? PackageName);
public sealed record PairDeviceRequest(string PairingToken, string DeviceName, string? Platform, string? PlatformVersion);
public sealed record DeviceEventRequest(string Level, string Category, string Message, string? Details, DateTimeOffset? OccurredAt);
public sealed record FeedbackRequest(string Message, string? Screen, string? Kind);
public sealed record CreateSessionResponse(Guid SessionId, string DashboardToken, string PairingUrl, string QrSvg, DateTimeOffset PairingExpiresAt, DateTimeOffset SessionExpiresAt);
public sealed record PairDeviceResponse(Guid DeviceId, string DeviceToken, Guid SessionId);
public sealed record BuildResponse(Guid BuildId, string FileName, string VersionName, long VersionCode, long SizeBytes, string Sha256, DateTimeOffset UploadedAt);
public sealed record DownloadTicketResponse(string DownloadUrl, DateTimeOffset ExpiresAt);
