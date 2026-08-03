using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using DevBridge.Models;
using DevBridge.Options;
using DevBridge.Services;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;
using QRCoder;

var builder = WebApplication.CreateBuilder(args);
builder.Services.Configure<DevBridgeOptions>(builder.Configuration.GetSection(DevBridgeOptions.SectionName));
var maximumUploadBytes = builder.Configuration
    .GetSection(DevBridgeOptions.SectionName)
    .GetValue<int>(nameof(DevBridgeOptions.MaximumApkMegabytes), 500) * 1024L * 1024L;
builder.Services.Configure<FormOptions>(o => o.MultipartBodyLengthLimit = maximumUploadBytes);
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = maximumUploadBytes);
builder.Services.AddApplicationInsightsTelemetry();
builder.Services.AddSingleton<TokenService>();
builder.Services.AddSingleton<DiagnosticArchiveService>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<DiagnosticArchiveService>());
builder.Services.AddSingleton<SessionPersistenceService>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<SessionPersistenceService>());
builder.Services.AddSingleton<SessionStore>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<SessionStore>());
builder.Services.AddSingleton<NetworkAddressService>();
builder.Services.AddHostedService<SessionCleanupService>();

var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapGet("/health", (DiagnosticArchiveService archive, SessionPersistenceService persistence) => Results.Ok(new
{
    status = "healthy",
    utc = DateTimeOffset.UtcNow,
    diagnosticsArchive = new
    {
        archive.Enabled,
        archive.Initialized,
        archive.ArchivedCount,
        archive.FailedCount,
        archive.LastFailureAt
    },
    sessionPersistence = new
    {
        persistence.Enabled,
        persistence.Initialized,
        persistence.SavedCount,
        persistence.FailedCount,
        persistence.RestoredCount,
        persistence.LoadFailureCount
    }
}));

app.MapPost("/api/sessions", (CreateSessionRequest body, HttpRequest request, SessionStore store,
    TokenService tokens, NetworkAddressService addresses, IOptions<DevBridgeOptions> options) =>
{
    var requiredAccessKey = options.Value.DeveloperAccessKey;
    if (!string.IsNullOrWhiteSpace(requiredAccessKey) &&
        !tokens.Verify(request.Headers["X-DevBridge-Access-Key"].FirstOrDefault(), tokens.Hash(requiredAccessKey)))
    {
        return Results.Unauthorized();
    }

    var name = body.ProjectName?.Trim();
    if (string.IsNullOrWhiteSpace(name) || name.Length > 80)
        return Results.BadRequest(new { error = "Project name is required and must be 80 characters or fewer." });
    var appName = body.AppName?.Trim();
    if (string.IsNullOrWhiteSpace(appName) || appName.Length > 80)
        return Results.BadRequest(new { error = "App name is required and must be 80 characters or fewer." });
    var packageName = body.PackageName?.Trim();
    if (packageName is { Length: > 150 })
        return Results.BadRequest(new { error = "Package name must be 150 characters or fewer." });
    var now = DateTimeOffset.UtcNow;
    var pairingToken = tokens.CreateToken();
    var dashboardToken = tokens.CreateToken();
    var session = new TestSession
    {
        Id = Guid.NewGuid(),
        ProjectName = name,
        AppName = appName,
        PackageName = string.IsNullOrWhiteSpace(packageName) ? null : packageName,
        PairingTokenHash = tokens.Hash(pairingToken),
        DashboardTokenHash = tokens.Hash(dashboardToken),
        PairingExpiresAt = now.AddMinutes(options.Value.PairingLifetimeMinutes),
        ExpiresAt = now.AddHours(options.Value.SessionLifetimeHours)
    };
    store.Add(session);
    var pairingUrl = $"{addresses.Resolve(request)}/pair.html?session={session.Id:D}&token={Uri.EscapeDataString(pairingToken)}";
    using var qrData = QRCodeGenerator.GenerateQrCode(pairingUrl, QRCodeGenerator.ECCLevel.Q);
    using var renderer = new SvgQRCode(qrData);
    return Results.Ok(new CreateSessionResponse(session.Id, dashboardToken, pairingUrl,
        renderer.GetGraphic(8, "#101827", "#ffffff", true), session.PairingExpiresAt, session.ExpiresAt));
});

app.MapPost("/api/sessions/{sessionId:guid}/pair", (Guid sessionId, PairDeviceRequest body,
    SessionStore store, TokenService tokens) =>
{
    if (!store.TryGet(sessionId, out var session)) return Results.NotFound();
    lock (session)
    {
        if (session.PairingConsumed || session.PairingExpiresAt <= DateTimeOffset.UtcNow || !tokens.Verify(body.PairingToken, session.PairingTokenHash))
            return Results.Unauthorized();
        var name = body.DeviceName?.Trim();
        if (string.IsNullOrWhiteSpace(name) || name.Length > 100) return Results.BadRequest(new { error = "A valid device name is required." });
        var rawToken = tokens.CreateToken();
        var device = new ConnectedDevice
        {
            Id = Guid.NewGuid(),
            Name = name,
            TokenHash = tokens.Hash(rawToken),
            Platform = string.IsNullOrWhiteSpace(body.Platform) ? "Android" : body.Platform.Trim(),
            PlatformVersion = body.PlatformVersion?.Trim(),
            Context = CleanContext(body.Context)
        };
        session.Devices[device.Id] = device;
        session.PairingConsumed = true;
        AddEvent(store, session, device, "info", "connection", "Device paired", null);
        store.Touch(session);
        return Results.Ok(new PairDeviceResponse(device.Id, rawToken, session.Id));
    }
});

app.MapGet("/api/sessions/{sessionId:guid}/dashboard", (Guid sessionId, HttpRequest request, SessionStore store, TokenService tokens) =>
{
    if (!store.TryGet(sessionId, out var session)) return Results.NotFound();
    if (!tokens.Verify(request.Headers["X-Dashboard-Token"].FirstOrDefault(), session.DashboardTokenHash)) return Results.Unauthorized();
    var events = session.Events.ToArray();
    var issues = events
        .Where(x => x.Level is "error" or "fatal" || x.Category is "crash" or "anr" or "performance" or "installation")
        .GroupBy(x => string.IsNullOrWhiteSpace(x.Fingerprint) ? $"{x.Category}:{x.EventType}:{x.Message}" : x.Fingerprint)
        .Select(group => new
        {
            fingerprint = group.Key,
            level = group.Any(x => x.Level == "fatal") ? "fatal" : group.Any(x => x.Level == "error") ? "error" : group.First().Level,
            category = group.First().Category,
            eventType = group.First().EventType,
            message = group.First().Message,
            count = group.Count(),
            affectedDevices = group.Select(x => x.DeviceId).Distinct().Count(),
            firstSeenAt = group.Min(x => x.OccurredAt),
            lastSeenAt = group.Max(x => x.OccurredAt),
            latest = group.OrderByDescending(x => x.OccurredAt).First()
        })
        .OrderByDescending(x => x.lastSeenAt)
        .Take(100);
    return Results.Ok(new
    {
        session.Id,
        session.ProjectName,
        session.AppName,
        session.PackageName,
        session.CreatedAt,
        session.PairingExpiresAt,
        session.ExpiresAt,
        session.PairingConsumed,
        devices = session.Devices.Values.OrderByDescending(x => x.LastSeenAt).Select(x => new { x.Id, x.Name, x.Platform, x.PlatformVersion, x.Context, x.PairedAt, x.LastSeenAt }),
        builds = session.Builds.Values.OrderByDescending(x => x.UploadedAt).Select(ToBuild),
        events = events.Reverse().Take(300),
        issues,
        summary = new
        {
            crashes = events.Count(x => x.Category == "crash"),
            anrs = events.Count(x => x.Category == "anr"),
            performanceAlerts = events.Count(x => x.Category == "performance"),
            installFailures = events.Count(x => x.Category == "installation" && x.Level == "error")
        }
    });
});

app.MapGet("/api/sessions/{sessionId:guid}/device", (Guid sessionId, HttpRequest request, SessionStore store, TokenService tokens) =>
{
    if (!AuthenticateDevice(sessionId, request, store, tokens, out var session, out var device)) return Results.Unauthorized();
    device.LastSeenAt = DateTimeOffset.UtcNow;
    return Results.Ok(new { session.Id, session.ProjectName, session.AppName, session.PackageName, session.ExpiresAt, device = new { device.Id, device.Name }, builds = session.Builds.Values.OrderByDescending(x => x.UploadedAt).Select(ToBuild) });
});

app.MapPost("/api/sessions/{sessionId:guid}/events", (Guid sessionId, DeviceEventRequest body, HttpRequest request, SessionStore store, TokenService tokens) =>
{
    if (!AuthenticateDevice(sessionId, request, store, tokens, out var session, out var device)) return Results.Unauthorized();
    if (string.IsNullOrWhiteSpace(body.Message) || body.Message.Length > 4_000) return Results.BadRequest(new { error = "Message is required and cannot exceed 4,000 characters." });
    device.LastSeenAt = DateTimeOffset.UtcNow;
    store.AddEvent(session, BuildEvent(device, body.Level, body.Category, body.Category, body.Message, body.Details,
        null, null, null, null, null, null, body.OccurredAt));
    return Results.Accepted();
});

app.MapPost("/api/sessions/{sessionId:guid}/diagnostics/batch", (Guid sessionId, DiagnosticBatchRequest body,
    HttpRequest request, SessionStore store, TokenService tokens) =>
{
    if (!AuthenticateTelemetry(sessionId, request, store, tokens, out var session, out var device)) return Results.Unauthorized();
    if (body.Events is null || body.Events.Count is 0 or > 100)
        return Results.BadRequest(new { error = "A batch must contain between 1 and 100 diagnostic events." });
    foreach (var item in body.Events)
    {
        if (string.IsNullOrWhiteSpace(item.Message) || item.Message.Length > 4_000)
            return Results.BadRequest(new { error = "Every event needs a message of 4,000 characters or fewer." });
    }
    device.LastSeenAt = DateTimeOffset.UtcNow;
    foreach (var item in body.Events)
    {
        store.AddEvent(session, BuildEvent(device, item.Level, item.Category, item.EventType, item.Message,
            item.Details, item.StackTrace, item.Fingerprint, item.Screen, item.BuildId, item.Metrics, item.Context,
            item.OccurredAt));
    }
    return Results.Accepted(value: new { accepted = body.Events.Count });
});

app.MapPost("/api/sessions/{sessionId:guid}/installations", (Guid sessionId, InstallationResultRequest body,
    HttpRequest request, SessionStore store, TokenService tokens) =>
{
    if (!AuthenticateDevice(sessionId, request, store, tokens, out var session, out var device)) return Results.Unauthorized();
    if (!session.Builds.ContainsKey(body.BuildId)) return Results.NotFound(new { error = "Build not found." });
    var status = Clean(body.Status, "unknown", 40);
    var success = status is "success" or "pending_user_action" or "permission_required" or "started";
    var context = new Dictionary<string, string>
    {
        ["status"] = status,
        ["packageName"] = Truncate(body.PackageName, 150) ?? string.Empty,
        ["statusCode"] = body.StatusCode?.ToString() ?? string.Empty
    };
    store.AddEvent(session, BuildEvent(device, success ? "info" : "error", "installation", status,
        body.Message, body.Details, null, $"installation:{status}:{body.PackageName}", null, body.BuildId,
        null, context, body.OccurredAt));
    return Results.Accepted();
});

app.MapPost("/api/sessions/{sessionId:guid}/feedback", (Guid sessionId, FeedbackRequest body, HttpRequest request, SessionStore store, TokenService tokens) =>
{
    if (!AuthenticateDevice(sessionId, request, store, tokens, out var session, out var device)) return Results.Unauthorized();
    if (string.IsNullOrWhiteSpace(body.Message) || body.Message.Length > 4_000) return Results.BadRequest(new { error = "Feedback is required and cannot exceed 4,000 characters." });
    AddEvent(store, session, device, "feedback", Clean(body.Kind, "feedback", 40), body.Message.Trim(),
        string.IsNullOrWhiteSpace(body.Screen) ? null : $"Screen: {body.Screen.Trim()}");
    return Results.Accepted();
});

app.MapPost("/api/sessions/{sessionId:guid}/builds", async (Guid sessionId, HttpRequest request, SessionStore store,
    TokenService tokens, IOptions<DevBridgeOptions> options, IWebHostEnvironment env, CancellationToken ct) =>
{
    if (!store.TryGet(sessionId, out var session)) return Results.NotFound();
    if (!tokens.Verify(request.Headers["X-Dashboard-Token"].FirstOrDefault(), session.DashboardTokenHash)) return Results.Unauthorized();
    if (!request.HasFormContentType) return Results.BadRequest(new { error = "Multipart form data is required." });
    var form = await request.ReadFormAsync(ct);
    var file = form.Files.GetFile("apk");
    if (file is null || file.Length == 0 || !string.Equals(Path.GetExtension(file.FileName), ".apk", StringComparison.OrdinalIgnoreCase))
        return Results.BadRequest(new { error = "A non-empty .apk file is required." });
    if (file.Length > options.Value.MaximumApkMegabytes * 1024L * 1024L) return Results.BadRequest(new { error = "APK is too large." });
    if (!long.TryParse(form["versionCode"], out var versionCode) || versionCode < 1) return Results.BadRequest(new { error = "versionCode must be positive." });
    var versionName = form["versionName"].ToString().Trim();
    if (versionName.Length is 0 or > 50) return Results.BadRequest(new { error = "versionName is required and must be 50 characters or fewer." });

    var root = Path.GetFullPath(options.Value.ArtifactPath, env.ContentRootPath);
    Directory.CreateDirectory(root);
    var buildId = Guid.NewGuid();
    var path = Path.Combine(root, $"{buildId:N}.apk");
    await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true)) await file.CopyToAsync(output, ct);
    var signature = new byte[4];
    await using (var signatureInput = File.OpenRead(path))
        _ = await signatureInput.ReadAsync(signature, ct);
    if (!signature.AsSpan().SequenceEqual(new byte[] { 0x50, 0x4B, 0x03, 0x04 }))
    {
        File.Delete(path);
        return Results.BadRequest(new { error = "The uploaded file is not a valid APK/ZIP package." });
    }
    string hash;
    await using (var input = File.OpenRead(path)) hash = Convert.ToHexString(await SHA256.HashDataAsync(input, ct));
    var build = new PublishedBuild { Id = buildId, OriginalFileName = Path.GetFileName(file.FileName), StoredPath = path, VersionName = versionName, VersionCode = versionCode, SizeBytes = file.Length, Sha256 = hash };
    session.Builds[build.Id] = build;
    store.Touch(session);
    foreach (var device in session.Devices.Values) AddEvent(store, session, device, "info", "build", $"Build {versionName} is available", null);
    return Results.Ok(ToBuild(build));
}).DisableAntiforgery();

app.MapPost("/api/sessions/{sessionId:guid}/builds/{buildId:guid}/ticket", (Guid sessionId, Guid buildId, HttpRequest request,
    SessionStore store, TokenService tokens, NetworkAddressService addresses) =>
{
    if (!AuthenticateDevice(sessionId, request, store, tokens, out var session, out var device)) return Results.Unauthorized();
    if (!session.Builds.ContainsKey(buildId)) return Results.NotFound();
    var ticket = tokens.CreateToken();
    var expires = DateTimeOffset.UtcNow.AddMinutes(2);
    store.AddTicket(ticket, new DownloadTicket(sessionId, buildId, device.Id, expires));
    return Results.Ok(new DownloadTicketResponse($"{addresses.Resolve(request)}/api/downloads/{Uri.EscapeDataString(ticket)}", expires));
});

app.MapPost("/api/sessions/{sessionId:guid}/builds/{buildId:guid}/sdk-ticket", (Guid sessionId, Guid buildId,
    HttpRequest request, SessionStore store, TokenService tokens) =>
{
    if (!AuthenticateDevice(sessionId, request, store, tokens, out var session, out var device)) return Results.Unauthorized();
    if (!session.Builds.ContainsKey(buildId)) return Results.NotFound();
    var raw = tokens.CreateToken();
    var expires = DateTimeOffset.UtcNow.AddMinutes(2);
    store.AddSdkTicket(raw, new SdkHandoffTicket(session.Id, buildId, device.Id, expires));
    return Results.Ok(new SdkHandoffResponse(raw, expires));
});

app.MapPost("/api/sdk/exchange", (SdkExchangeRequest body, HttpRequest request, SessionStore store,
    TokenService tokens, NetworkAddressService addresses) =>
{
    if (string.IsNullOrWhiteSpace(body.HandoffToken) ||
        !store.TryConsumeSdkTicket(body.HandoffToken, out var ticket) ||
        !store.TryGet(ticket.SessionId, out var session) ||
        !session.Devices.TryGetValue(ticket.DeviceId, out var device) ||
        !session.Builds.ContainsKey(ticket.BuildId)) return Results.Unauthorized();
    var rawToken = tokens.CreateToken();
    var client = new SdkClient
    {
        Id = Guid.NewGuid(),
        DeviceId = device.Id,
        BuildId = ticket.BuildId,
        TokenHash = tokens.Hash(rawToken)
    };
    session.SdkClients[client.Id] = client;
    store.Touch(session);
    return Results.Ok(new SdkExchangeResponse(addresses.Resolve(request), session.Id, device.Id, ticket.BuildId,
        rawToken, session.ExpiresAt));
});

app.MapPost("/api/sessions/{sessionId:guid}/attachments", async (Guid sessionId, HttpRequest request,
    SessionStore store, TokenService tokens, SessionPersistenceService persistence,
    IOptions<DevBridgeOptions> options, CancellationToken cancellationToken) =>
{
    if (!AuthenticateTelemetry(sessionId, request, store, tokens, out var session, out var device)) return Results.Unauthorized();
    if (!request.HasFormContentType) return Results.BadRequest(new { error = "Multipart form data is required." });
    var form = await request.ReadFormAsync(cancellationToken);
    var file = form.Files.GetFile("file");
    if (file is null || file.Length == 0) return Results.BadRequest(new { error = "A non-empty attachment is required." });
    if (file.Length > options.Value.MaximumAttachmentMegabytes * 1024L * 1024L)
        return Results.BadRequest(new { error = "Attachment is too large." });
    var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
    var allowed = new Dictionary<string, string>
    {
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".mp4"] = "video/mp4",
        [".webm"] = "video/webm",
        [".txt"] = "text/plain"
    };
    if (!allowed.TryGetValue(extension, out var contentType))
        return Results.BadRequest(new { error = "Only PNG, JPEG, MP4, WebM, and text attachments are supported." });
    Guid? buildId = Guid.TryParse(form["buildId"], out var parsedBuild) ? parsedBuild : null;
    if (buildId is not null && !session.Builds.ContainsKey(buildId.Value)) return Results.BadRequest(new { error = "Build not found." });
    var id = Guid.NewGuid();
    var kind = Clean(form["kind"], extension is ".mp4" or ".webm" ? "recording" : "screenshot", 30);
    var blobName = $"attachments/{session.Id:N}/{id:N}{extension}";
    await using (var stream = file.OpenReadStream())
        await persistence.UploadAttachmentAsync(blobName, stream, contentType, cancellationToken);
    var attachment = new DiagnosticAttachment
    {
        Id = id,
        DeviceId = device.Id,
        BuildId = buildId,
        Kind = kind,
        FileName = Path.GetFileName(file.FileName),
        BlobName = blobName,
        ContentType = contentType,
        SizeBytes = file.Length
    };
    session.Attachments[id] = attachment;
    store.Touch(session);
    store.AddEvent(session, BuildEvent(device, "feedback", "attachment", $"{kind}_attached",
        $"Tester attached {kind} evidence.", null, null, $"attachment:{id:N}", null, buildId,
        new Dictionary<string, double> { ["sizeBytes"] = file.Length },
        new Dictionary<string, string> { ["attachmentId"] = id.ToString("D"), ["contentType"] = contentType }, null));
    return Results.Ok(new AttachmentResponse(id, kind, attachment.FileName, contentType, file.Length, attachment.CreatedAt));
}).DisableAntiforgery();

app.MapGet("/api/sessions/{sessionId:guid}/attachments/{attachmentId:guid}", async (Guid sessionId, Guid attachmentId,
    HttpRequest request, SessionStore store, TokenService tokens, SessionPersistenceService persistence,
    CancellationToken cancellationToken) =>
{
    if (!store.TryGet(sessionId, out var session) ||
        !tokens.Verify(request.Headers["X-Dashboard-Token"].FirstOrDefault(), session.DashboardTokenHash))
        return Results.Unauthorized();
    if (!session.Attachments.TryGetValue(attachmentId, out var attachment)) return Results.NotFound();
    var content = await persistence.OpenAttachmentAsync(attachment.BlobName, cancellationToken);
    return content is null ? Results.NotFound() : Results.Stream(content.Value.Content, content.Value.ContentType,
        attachment.FileName, enableRangeProcessing: true);
});

app.MapGet("/api/downloads/{ticket}", (string ticket, SessionStore store) =>
{
    if (!store.TryConsumeTicket(ticket, out var item) || !store.TryGet(item.SessionId, out var session) ||
        !session.Builds.TryGetValue(item.BuildId, out var build) || !File.Exists(build.StoredPath)) return Results.NotFound();
    return Results.File(build.StoredPath, "application/vnd.android.package-archive", build.OriginalFileName, enableRangeProcessing: true);
});

app.MapFallbackToFile("index.html");
app.Run();

static bool AuthenticateDevice(Guid id, HttpRequest request, SessionStore store, TokenService tokens, out TestSession session, out ConnectedDevice device)
{
    device = null!;
    if (!store.TryGet(id, out session!)) return false;
    var auth = request.Headers.Authorization.FirstOrDefault();
    var raw = auth?.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) == true ? auth[7..] : null;
    device = session.Devices.Values.FirstOrDefault(x => tokens.Verify(raw, x.TokenHash))!;
    return device is not null;
}
static bool AuthenticateTelemetry(Guid id, HttpRequest request, SessionStore store, TokenService tokens,
    out TestSession session, out ConnectedDevice device)
{
    if (AuthenticateDevice(id, request, store, tokens, out session, out device)) return true;
    device = null!;
    if (!store.TryGet(id, out session!)) return false;
    var auth = request.Headers.Authorization.FirstOrDefault();
    var raw = auth?.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) == true ? auth[7..] : null;
    var sdk = session.SdkClients.Values.FirstOrDefault(x => tokens.Verify(raw, x.TokenHash));
    if (sdk is null || !session.Devices.TryGetValue(sdk.DeviceId, out device!)) return false;
    sdk.LastSeenAt = DateTimeOffset.UtcNow;
    return true;
}
static void AddEvent(SessionStore store, TestSession session, ConnectedDevice device, string level, string category, string message, string? details) =>
    store.AddEvent(session, BuildEvent(device, level, category, category, message, details, null, null, null, null, null, null, null));
static BuildResponse ToBuild(PublishedBuild x) => new(x.Id, x.OriginalFileName, x.VersionName, x.VersionCode, x.SizeBytes, x.Sha256, x.UploadedAt);
static string Clean(string? value, string fallback, int max) { var result = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim().ToLowerInvariant(); return result.Length <= max ? result : result[..max]; }
static string? Truncate(string? value, int max) => value is { Length: > 0 } ? value[..Math.Min(value.Length, max)] : value;
static IReadOnlyDictionary<string, string> CleanContext(IReadOnlyDictionary<string, string>? source) =>
    source?.Take(40).ToDictionary(x => Truncate(x.Key, 60) ?? string.Empty, x => Truncate(Redact(x.Value), 500) ?? string.Empty)
    ?? new Dictionary<string, string>();
static IReadOnlyDictionary<string, double> CleanMetrics(IReadOnlyDictionary<string, double>? source) =>
    source?.Where(x => double.IsFinite(x.Value)).Take(40).ToDictionary(x => Truncate(x.Key, 60) ?? string.Empty, x => x.Value)
    ?? new Dictionary<string, double>();
static DeviceEvent BuildEvent(ConnectedDevice device, string? level, string? category, string? eventType,
    string message, string? details, string? stackTrace, string? fingerprint, string? screen, Guid? buildId,
    IReadOnlyDictionary<string, double>? metrics, IReadOnlyDictionary<string, string>? context, DateTimeOffset? occurredAt)
{
    var cleanCategory = Clean(category, "app", 40);
    var cleanType = Clean(eventType, cleanCategory, 60);
    var cleanMessage = Truncate(Redact(message.Trim()), 4_000) ?? "Diagnostic event";
    var cleanStack = Truncate(Redact(stackTrace), 30_000);
    var computedFingerprint = string.IsNullOrWhiteSpace(fingerprint)
        ? Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{cleanCategory}|{cleanType}|{cleanMessage}|{cleanStack?.Split('\n').FirstOrDefault()}")))[..24].ToLowerInvariant()
        : Clean(fingerprint, "event", 160);
    return new DeviceEvent
    {
        Id = Guid.NewGuid(),
        DeviceId = device.Id,
        DeviceName = device.Name,
        Level = Clean(level, "info", 20),
        Category = cleanCategory,
        EventType = cleanType,
        Message = cleanMessage,
        Details = Truncate(Redact(details), 20_000),
        StackTrace = cleanStack,
        Fingerprint = computedFingerprint,
        Screen = Truncate(screen, 200),
        BuildId = buildId,
        Metrics = CleanMetrics(metrics),
        Context = CleanContext(context),
        OccurredAt = occurredAt ?? DateTimeOffset.UtcNow,
        ReceivedAt = DateTimeOffset.UtcNow
    };
}
static string? Redact(string? value)
{
    if (string.IsNullOrEmpty(value)) return value;
    return Regex.Replace(value, "(?i)(authorization|cookie|token|password|secret|api[-_]?key)(\\s*[:=]\\s*)([^\\s,;]+)", "$1$2[REDACTED]");
}

public partial class Program;
