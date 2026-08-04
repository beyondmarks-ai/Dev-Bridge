namespace DevBridge.Models;

public sealed record CreateSessionRequest(string ProjectName, string? AppName, string? PackageName);
public sealed record PairDeviceRequest(string PairingToken, string DeviceName, string? Platform, string? PlatformVersion,
    Dictionary<string, string>? Context = null);
public sealed record DeviceEventRequest(string Level, string Category, string Message, string? Details, DateTimeOffset? OccurredAt);
public sealed record DiagnosticEventRequest(string? Level, string? Category, string? EventType, string Message,
    string? Details, string? StackTrace, string? Fingerprint, string? Screen, Guid? BuildId,
    Dictionary<string, double>? Metrics, Dictionary<string, string>? Context, DateTimeOffset? OccurredAt);
public sealed record DiagnosticBatchRequest(IReadOnlyList<DiagnosticEventRequest> Events);
public sealed record InstallationResultRequest(Guid BuildId, string Status, string Message, string? PackageName,
    int? StatusCode, string? Details, DateTimeOffset? OccurredAt);
public sealed record FeedbackRequest(string Message, string? Screen, string? Kind);
public sealed record CreateSessionResponse(Guid SessionId, string DashboardToken, string PairingUrl, string QrSvg, DateTimeOffset PairingExpiresAt, DateTimeOffset SessionExpiresAt);
public sealed record PairDeviceResponse(Guid DeviceId, string DeviceToken, Guid SessionId);
public sealed record BuildResponse(Guid BuildId, string FileName, string VersionName, long VersionCode, long SizeBytes, string Sha256, DateTimeOffset UploadedAt);
public sealed record DownloadTicketResponse(string DownloadUrl, DateTimeOffset ExpiresAt);
public sealed record SdkHandoffResponse(string HandoffToken, DateTimeOffset ExpiresAt);
public sealed record SdkExchangeRequest(string HandoffToken);
public sealed record SdkExchangeResponse(string BaseUrl, Guid SessionId, Guid DeviceId, Guid BuildId, string SdkToken, DateTimeOffset SessionExpiresAt);
public sealed record AttachmentResponse(Guid AttachmentId, string Kind, string FileName, string ContentType, long SizeBytes, DateTimeOffset CreatedAt);
public sealed record CreateTestCaseRequest(string Title, string Instructions, string ExpectedResult, string? Priority,
    IReadOnlyList<AutomationStep>? AutomationSteps = null);
public sealed record GenerateTestCasesRequest(string Feature, int? Count);
public sealed record ScheduleTestRunRequest(Guid TestCaseId, Guid DeviceId, Guid? BuildId);
public sealed record StartTestRunRequest(string CaptureId);
public sealed record CompleteTestRunRequest(string Verdict, string? Notes, string? CaptureId);
public sealed record TestRunResult(TestRun Run, AiIssueAnalysis? Analysis, string? AnalysisMessage = null);
public sealed record AutomationExchangeRequest(string EnrollmentToken, string AgentName, string? PlatformVersion,
    Dictionary<string, string>? Capabilities);
public sealed record AutomationExchangeResponse(string BaseUrl, Guid SessionId, Guid AgentId, string AgentToken,
    string? PackageName, DateTimeOffset SessionExpiresAt);
public sealed record AutomationProgressRequest(int StepIndex, int TotalSteps, string Status, string? Message,
    string? Fingerprint);
