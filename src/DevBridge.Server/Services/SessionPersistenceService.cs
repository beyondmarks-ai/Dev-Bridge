using System.Text.Json;
using System.Threading.Channels;
using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using DevBridge.Models;
using DevBridge.Options;
using Microsoft.Extensions.Options;

namespace DevBridge.Services;

public sealed class SessionPersistenceService : BackgroundService
{
    private readonly BlobContainerClient? _container;
    private readonly Channel<PersistenceCommand> _queue = Channel.CreateBounded<PersistenceCommand>(
        new BoundedChannelOptions(500) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    private readonly ILogger<SessionPersistenceService> _logger;

    public bool Enabled => _container is not null;
    public bool Initialized { get; private set; }
    public long SavedCount { get; private set; }
    public long FailedCount { get; private set; }
    public long RestoredCount { get; private set; }
    public long LoadFailureCount { get; private set; }

    public SessionPersistenceService(IOptions<DevBridgeOptions> options, ILogger<SessionPersistenceService> logger)
    {
        _logger = logger;
        if (string.IsNullOrWhiteSpace(options.Value.StorageAccountName)) return;
        var endpoint = new Uri($"https://{options.Value.StorageAccountName}.blob.core.windows.net/{options.Value.StorageContainerName}");
        _container = new BlobContainerClient(endpoint, new DefaultAzureCredential());
    }

    public void QueueSave(TestSession session) => _queue.Writer.TryWrite(new SaveCommand(SessionSnapshot.From(session)));
    public void QueueDelete(Guid sessionId) => _queue.Writer.TryWrite(new DeleteCommand(sessionId));

    public async Task<IReadOnlyList<TestSession>> LoadAsync(CancellationToken cancellationToken)
    {
        if (_container is null) return [];
        await _container.CreateIfNotExistsAsync(PublicAccessType.None, cancellationToken: cancellationToken);
        Initialized = true;
        var sessions = new List<TestSession>();
        await foreach (var blob in _container.GetBlobsAsync(
            BlobTraits.None, BlobStates.None, "sessions/", cancellationToken))
        {
            if (!blob.Name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                var content = await _container.GetBlobClient(blob.Name).DownloadContentAsync(cancellationToken);
                var snapshot = content.Value.Content.ToObjectFromJson<SessionSnapshot>();
                if (snapshot is not null && snapshot.ExpiresAt > DateTimeOffset.UtcNow)
                {
                    sessions.Add(snapshot.ToDomain());
                    RestoredCount++;
                }
            }
            catch (Exception exception)
            {
                LoadFailureCount++;
                _logger.LogWarning(exception, "Could not restore DevBridge session snapshot {BlobName}.", blob.Name);
            }
        }
        return sessions;
    }

    public async Task UploadAttachmentAsync(string blobName, Stream content, string contentType, CancellationToken cancellationToken)
    {
        if (_container is null) throw new InvalidOperationException("Azure attachment storage is not configured.");
        await _container.GetBlobClient(blobName).UploadAsync(content, new BlobUploadOptions
        {
            HttpHeaders = new BlobHttpHeaders { ContentType = contentType }
        }, cancellationToken);
    }

    public async Task<(Stream Content, string ContentType)?> OpenAttachmentAsync(string blobName, CancellationToken cancellationToken)
    {
        if (_container is null) return null;
        var blob = _container.GetBlobClient(blobName);
        if (!await blob.ExistsAsync(cancellationToken)) return null;
        var response = await blob.DownloadStreamingAsync(cancellationToken: cancellationToken);
        return (response.Value.Content, response.Value.Details.ContentType ?? "application/octet-stream");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_container is null) return;
        try
        {
            await _container.CreateIfNotExistsAsync(PublicAccessType.None, cancellationToken: stoppingToken);
            Initialized = true;
        }
        catch (Exception exception)
        {
            FailedCount++;
            _logger.LogError(exception, "DevBridge session persistence could not initialize.");
            return;
        }

        await foreach (var command in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                switch (command)
                {
                    case SaveCommand save:
                        await _container.GetBlobClient(Name(save.Session.Id)).UploadAsync(
                            BinaryData.FromObjectAsJson(save.Session), overwrite: true, cancellationToken: stoppingToken);
                        SavedCount++;
                        break;
                    case DeleteCommand delete:
                        await _container.GetBlobClient(Name(delete.SessionId)).DeleteIfExistsAsync(cancellationToken: stoppingToken);
                        break;
                }
            }
            catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
            {
                FailedCount++;
                _logger.LogWarning(exception, "A DevBridge session snapshot could not be persisted.");
            }
        }
    }

    private static string Name(Guid id) => $"sessions/{id:N}.json";

    private abstract record PersistenceCommand;
    private sealed record SaveCommand(SessionSnapshot Session) : PersistenceCommand;
    private sealed record DeleteCommand(Guid SessionId) : PersistenceCommand;

    private sealed record SessionSnapshot(
        Guid Id, string ProjectName, string AppName, string? PackageName, string DashboardTokenHash,
        string PairingTokenHash, DateTimeOffset PairingExpiresAt, DateTimeOffset ExpiresAt,
        DateTimeOffset CreatedAt, bool PairingConsumed, List<DeviceSnapshot> Devices,
        List<BuildSnapshot> Builds, List<SdkClientSnapshot> SdkClients, List<AttachmentSnapshot> Attachments,
        List<AiAnalysisSnapshot>? AiAnalyses, List<TestCaseSnapshot>? TestCases = null,
        List<TestRunSnapshot>? TestRuns = null)
    {
        public static SessionSnapshot From(TestSession session) => new(
            session.Id, session.ProjectName, session.AppName, session.PackageName, session.DashboardTokenHash,
            session.PairingTokenHash, session.PairingExpiresAt, session.ExpiresAt, session.CreatedAt,
            session.PairingConsumed, session.Devices.Values.Select(DeviceSnapshot.From).ToList(),
            session.Builds.Values.Select(BuildSnapshot.From).ToList(),
            session.SdkClients.Values.Select(SdkClientSnapshot.From).ToList(),
            session.Attachments.Values.Select(AttachmentSnapshot.From).ToList(),
            session.AiAnalyses.Values.Select(AiAnalysisSnapshot.From).ToList(),
            session.TestCases.Values.Select(TestCaseSnapshot.From).ToList(),
            session.TestRuns.Values.Select(TestRunSnapshot.From).ToList());

        public TestSession ToDomain()
        {
            var session = new TestSession
            {
                Id = Id,
                ProjectName = ProjectName,
                AppName = AppName,
                PackageName = PackageName,
                DashboardTokenHash = DashboardTokenHash,
                PairingTokenHash = PairingTokenHash,
                PairingExpiresAt = PairingExpiresAt,
                ExpiresAt = ExpiresAt,
                CreatedAt = CreatedAt,
                PairingConsumed = PairingConsumed
            };
            foreach (var item in Devices) session.Devices[item.Id] = item.ToDomain();
            foreach (var item in Builds) session.Builds[item.Id] = item.ToDomain();
            foreach (var item in SdkClients) session.SdkClients[item.Id] = item.ToDomain();
            foreach (var item in Attachments) session.Attachments[item.Id] = item.ToDomain();
            foreach (var item in AiAnalyses ?? []) session.AiAnalyses[item.Fingerprint] = item.ToDomain();
            foreach (var item in TestCases ?? []) session.TestCases[item.Id] = item.ToDomain();
            foreach (var item in TestRuns ?? []) session.TestRuns[item.Id] = item.ToDomain();
            return session;
        }
    }

    private sealed record DeviceSnapshot(Guid Id, string Name, string TokenHash, string Platform, string? PlatformVersion,
        Dictionary<string, string> Context, DateTimeOffset PairedAt, DateTimeOffset LastSeenAt)
    {
        public static DeviceSnapshot From(ConnectedDevice value) => new(value.Id, value.Name, value.TokenHash,
            value.Platform, value.PlatformVersion, new(value.Context), value.PairedAt, value.LastSeenAt);
        public ConnectedDevice ToDomain() => new()
        {
            Id = Id,
            Name = Name,
            TokenHash = TokenHash,
            Platform = Platform,
            PlatformVersion = PlatformVersion,
            Context = Context,
            PairedAt = PairedAt,
            LastSeenAt = LastSeenAt
        };
    }

    private sealed record BuildSnapshot(Guid Id, string OriginalFileName, string StoredPath, string VersionName,
        long VersionCode, long SizeBytes, string Sha256, DateTimeOffset UploadedAt)
    {
        public static BuildSnapshot From(PublishedBuild value) => new(value.Id, value.OriginalFileName, value.StoredPath,
            value.VersionName, value.VersionCode, value.SizeBytes, value.Sha256, value.UploadedAt);
        public PublishedBuild ToDomain() => new()
        {
            Id = Id,
            OriginalFileName = OriginalFileName,
            StoredPath = StoredPath,
            VersionName = VersionName,
            VersionCode = VersionCode,
            SizeBytes = SizeBytes,
            Sha256 = Sha256,
            UploadedAt = UploadedAt
        };
    }

    private sealed record SdkClientSnapshot(Guid Id, Guid DeviceId, Guid BuildId, string TokenHash,
        DateTimeOffset CreatedAt, DateTimeOffset LastSeenAt)
    {
        public static SdkClientSnapshot From(SdkClient value) => new(value.Id, value.DeviceId, value.BuildId,
            value.TokenHash, value.CreatedAt, value.LastSeenAt);
        public SdkClient ToDomain() => new()
        {
            Id = Id,
            DeviceId = DeviceId,
            BuildId = BuildId,
            TokenHash = TokenHash,
            CreatedAt = CreatedAt,
            LastSeenAt = LastSeenAt
        };
    }

    private sealed record AttachmentSnapshot(Guid Id, Guid DeviceId, Guid? BuildId, string Kind, string FileName,
        string BlobName, string ContentType, long SizeBytes, DateTimeOffset CreatedAt,
        string? RelatedFingerprint = null, string? CaptureId = null, DateTimeOffset? CapturedAt = null)
    {
        public static AttachmentSnapshot From(DiagnosticAttachment value) => new(value.Id, value.DeviceId, value.BuildId,
            value.Kind, value.FileName, value.BlobName, value.ContentType, value.SizeBytes, value.CreatedAt,
            value.RelatedFingerprint, value.CaptureId, value.CapturedAt);
        public DiagnosticAttachment ToDomain() => new()
        {
            Id = Id,
            DeviceId = DeviceId,
            BuildId = BuildId,
            Kind = Kind,
            FileName = FileName,
            BlobName = BlobName,
            ContentType = ContentType,
            SizeBytes = SizeBytes,
            RelatedFingerprint = RelatedFingerprint,
            CaptureId = CaptureId,
            CapturedAt = CapturedAt,
            CreatedAt = CreatedAt
        };
    }

    private sealed record AiAnalysisSnapshot(Guid Id, string Fingerprint, string Summary, string UserImpact,
        string ProbableCause, string ProblemLocation, string SuggestedFix, int Confidence, List<string> Evidence,
        List<string> ReproductionSteps, List<string> Limitations, string Model, string? ProviderResponseId,
        int? InputTokens, int? OutputTokens, DateTimeOffset AnalyzedAt)
    {
        public static AiAnalysisSnapshot From(AiIssueAnalysis value) => new(value.Id, value.Fingerprint,
            value.Summary, value.UserImpact, value.ProbableCause, value.ProblemLocation, value.SuggestedFix,
            value.Confidence, [.. value.Evidence], [.. value.ReproductionSteps], [.. value.Limitations],
            value.Model, value.ProviderResponseId, value.InputTokens, value.OutputTokens, value.AnalyzedAt);
        public AiIssueAnalysis ToDomain() => new()
        {
            Id = Id,
            Fingerprint = Fingerprint,
            Summary = Summary,
            UserImpact = UserImpact,
            ProbableCause = ProbableCause,
            ProblemLocation = ProblemLocation,
            SuggestedFix = SuggestedFix,
            Confidence = Confidence,
            Evidence = Evidence,
            ReproductionSteps = ReproductionSteps,
            Limitations = Limitations,
            Model = Model,
            ProviderResponseId = ProviderResponseId,
            InputTokens = InputTokens,
            OutputTokens = OutputTokens,
            AnalyzedAt = AnalyzedAt
        };
    }

    private sealed record TestCaseSnapshot(Guid Id, string Title, string Instructions, string ExpectedResult,
        string Priority, bool AiGenerated, DateTimeOffset CreatedAt, List<AutomationStep>? AutomationSteps = null)
    {
        public static TestCaseSnapshot From(TestCase value) => new(value.Id, value.Title, value.Instructions,
            value.ExpectedResult, value.Priority, value.AiGenerated, value.CreatedAt, [.. value.AutomationSteps]);
        public TestCase ToDomain() => new()
        {
            Id = Id,
            Title = Title,
            Instructions = Instructions,
            ExpectedResult = ExpectedResult,
            Priority = Priority,
            AiGenerated = AiGenerated,
            CreatedAt = CreatedAt,
            AutomationSteps = AutomationSteps ?? []
        };
    }

    private sealed record TestRunSnapshot(Guid Id, Guid TestCaseId, Guid DeviceId, Guid? BuildId, string Status,
        string? CaptureId, string? Notes, string? FailureFingerprint, DateTimeOffset CreatedAt,
        DateTimeOffset? StartedAt, DateTimeOffset? CompletedAt, int CurrentStep = 0, int TotalSteps = 0,
        string? LastStepMessage = null)
    {
        public static TestRunSnapshot From(TestRun value) => new(value.Id, value.TestCaseId, value.DeviceId,
            value.BuildId, value.Status, value.CaptureId, value.Notes, value.FailureFingerprint, value.CreatedAt,
            value.StartedAt, value.CompletedAt, value.CurrentStep, value.TotalSteps, value.LastStepMessage);
        public TestRun ToDomain() => new()
        {
            Id = Id,
            TestCaseId = TestCaseId,
            DeviceId = DeviceId,
            BuildId = BuildId,
            Status = Status,
            CaptureId = CaptureId,
            Notes = Notes,
            FailureFingerprint = FailureFingerprint,
            CurrentStep = CurrentStep,
            TotalSteps = TotalSteps,
            LastStepMessage = LastStepMessage,
            CreatedAt = CreatedAt,
            StartedAt = StartedAt,
            CompletedAt = CompletedAt
        };
    }
}
