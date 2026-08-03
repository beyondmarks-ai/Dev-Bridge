using System.Security.Cryptography;
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
builder.Services.AddSingleton<TokenService>();
builder.Services.AddSingleton<SessionStore>();
builder.Services.AddSingleton<NetworkAddressService>();
builder.Services.AddHostedService<SessionCleanupService>();

var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapGet("/health", () => Results.Ok(new { status = "healthy", utc = DateTimeOffset.UtcNow }));

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
            PlatformVersion = body.PlatformVersion?.Trim()
        };
        session.Devices[device.Id] = device;
        session.PairingConsumed = true;
        AddEvent(store, session, device, "info", "connection", "Device paired", null);
        return Results.Ok(new PairDeviceResponse(device.Id, rawToken, session.Id));
    }
});

app.MapGet("/api/sessions/{sessionId:guid}/dashboard", (Guid sessionId, HttpRequest request, SessionStore store, TokenService tokens) =>
{
    if (!store.TryGet(sessionId, out var session)) return Results.NotFound();
    if (!tokens.Verify(request.Headers["X-Dashboard-Token"].FirstOrDefault(), session.DashboardTokenHash)) return Results.Unauthorized();
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
        devices = session.Devices.Values.OrderByDescending(x => x.LastSeenAt).Select(x => new { x.Id, x.Name, x.Platform, x.PlatformVersion, x.PairedAt, x.LastSeenAt }),
        builds = session.Builds.Values.OrderByDescending(x => x.UploadedAt).Select(ToBuild),
        events = session.Events.Reverse().Take(200)
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
    store.AddEvent(session, new DeviceEvent(Guid.NewGuid(), device.Id, device.Name, Clean(body.Level, "info", 20), Clean(body.Category, "app", 40),
        body.Message.Trim(), Truncate(body.Details, 20_000), body.OccurredAt ?? DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
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
static void AddEvent(SessionStore store, TestSession session, ConnectedDevice device, string level, string category, string message, string? details) =>
    store.AddEvent(session, new DeviceEvent(Guid.NewGuid(), device.Id, device.Name, level, category, message, details, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
static BuildResponse ToBuild(PublishedBuild x) => new(x.Id, x.OriginalFileName, x.VersionName, x.VersionCode, x.SizeBytes, x.Sha256, x.UploadedAt);
static string Clean(string? value, string fallback, int max) { var result = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim().ToLowerInvariant(); return result.Length <= max ? result : result[..max]; }
static string? Truncate(string? value, int max) => value is { Length: > 0 } ? value[..Math.Min(value.Length, max)] : value;

public partial class Program;
