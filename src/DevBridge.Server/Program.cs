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
builder.Services.AddHttpClient<AiIssueAnalysisService>(client => client.Timeout = TimeSpan.FromSeconds(90));
builder.Services.AddHostedService<SessionCleanupService>();

var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapGet("/health", (DiagnosticArchiveService archive, SessionPersistenceService persistence,
    AiIssueAnalysisService ai) => Results.Ok(new
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
        },
        aiAnalysis = new { ai.Enabled }
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
            latest = group.OrderByDescending(x => x.OccurredAt).First(),
            analysis = session.AiAnalyses.GetValueOrDefault(group.Key)
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
        testCases = session.TestCases.Values.OrderByDescending(x => x.CreatedAt),
        testRuns = session.TestRuns.Values.OrderByDescending(x => x.CreatedAt).Select(run => ToTestRun(session, run)),
        quality = QualityReport(session),
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

app.MapPost("/api/sessions/{sessionId:guid}/issues/{fingerprint}/analysis", async (Guid sessionId,
    string fingerprint, bool? refresh, HttpRequest request, SessionStore store, TokenService tokens,
    AiIssueAnalysisService ai, CancellationToken cancellationToken) =>
{
    if (!store.TryGet(sessionId, out var session)) return Results.NotFound();
    if (!tokens.Verify(request.Headers["X-Dashboard-Token"].FirstOrDefault(), session.DashboardTokenHash))
        return Results.Unauthorized();
    if (string.IsNullOrWhiteSpace(fingerprint) || fingerprint.Length > 160)
        return Results.BadRequest(new { error = "A valid issue fingerprint is required." });
    try
    {
        var analysis = await ai.AnalyzeAsync(session, fingerprint, refresh ?? false, cancellationToken);
        store.Touch(session);
        return Results.Ok(analysis);
    }
    catch (KeyNotFoundException)
    {
        return Results.NotFound(new { error = "Issue evidence was not found." });
    }
    catch (AiAnalysisUnavailableException exception)
    {
        return Results.Problem(exception.Message, statusCode: StatusCodes.Status503ServiceUnavailable,
            title: "AI analysis unavailable");
    }
});

app.MapPost("/api/sessions/{sessionId:guid}/test-cases", (Guid sessionId, CreateTestCaseRequest body,
    HttpRequest request, SessionStore store, TokenService tokens) =>
{
    if (!TryDashboard(sessionId, request, store, tokens, out var session)) return Results.Unauthorized();
    var title = Truncate(body.Title?.Trim(), 120);
    var instructions = Truncate(body.Instructions?.Trim(), 2_000);
    var expected = Truncate(body.ExpectedResult?.Trim(), 1_000);
    if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(instructions) || string.IsNullOrWhiteSpace(expected))
        return Results.BadRequest(new { error = "Title, instructions, and expected result are required." });
    var priority = Clean(body.Priority, "normal", 12);
    if (priority is not ("critical" or "high" or "normal" or "low")) priority = "normal";
    var automationSteps = CleanAutomationSteps(body.AutomationSteps);
    if (body.AutomationSteps is { Count: > 0 } && automationSteps.Count != body.AutomationSteps.Count)
        return Results.BadRequest(new { error = "One or more automation steps are invalid or exceed the 60-step limit." });
    var testCase = new TestCase
    {
        Id = Guid.NewGuid(),
        Title = title,
        Instructions = instructions,
        ExpectedResult = expected,
        Priority = priority,
        AiGenerated = false,
        AutomationSteps = automationSteps
    };
    session.TestCases[testCase.Id] = testCase;
    store.Touch(session);
    return Results.Ok(testCase);
});

app.MapPost("/api/sessions/{sessionId:guid}/automation/enrollment", (Guid sessionId, HttpRequest request,
    SessionStore store, TokenService tokens, NetworkAddressService addresses) =>
{
    if (!TryDashboard(sessionId, request, store, tokens, out var session)) return Results.Unauthorized();
    var rawToken = tokens.CreateToken();
    var expiresAt = DateTimeOffset.UtcNow.AddMinutes(10);
    store.AddAutomationTicket(rawToken, new AutomationHandoffTicket(session.Id, expiresAt));
    return Results.Ok(new
    {
        enrollmentToken = rawToken,
        expiresAt,
        exchangeUrl = $"{addresses.Resolve(request)}/api/automation/exchange"
    });
});

app.MapPost("/api/automation/exchange", (AutomationExchangeRequest body, HttpRequest request,
    SessionStore store, TokenService tokens, NetworkAddressService addresses) =>
{
    if (string.IsNullOrWhiteSpace(body.EnrollmentToken) ||
        !store.TryConsumeAutomationTicket(body.EnrollmentToken, out var ticket) ||
        !store.TryGet(ticket.SessionId, out var session)) return Results.Unauthorized();
    var name = Truncate(body.AgentName?.Trim(), 100);
    if (string.IsNullOrWhiteSpace(name)) return Results.BadRequest(new { error = "A runner name is required." });
    if (session.Devices.Values.Count(x => x.Platform == "Automation") >= 20)
        return Results.Conflict(new { error = "This session already has the maximum number of automation runners." });
    var rawToken = tokens.CreateToken();
    var agent = new ConnectedDevice
    {
        Id = Guid.NewGuid(),
        Name = name,
        TokenHash = tokens.Hash(rawToken),
        Platform = "Automation",
        PlatformVersion = Truncate(body.PlatformVersion?.Trim(), 100),
        Context = CleanContext(body.Capabilities)
    };
    session.Devices[agent.Id] = agent;
    AddEvent(store, session, agent, "info", "automation", "Automation runner enrolled", null);
    store.Touch(session);
    return Results.Ok(new AutomationExchangeResponse(addresses.Resolve(request), session.Id, agent.Id, rawToken,
        session.PackageName, session.ExpiresAt));
});

app.MapPost("/api/sessions/{sessionId:guid}/test-cases/generate", async (Guid sessionId,
    GenerateTestCasesRequest body, HttpRequest request, SessionStore store, TokenService tokens,
    AiIssueAnalysisService ai, CancellationToken cancellationToken) =>
{
    if (!TryDashboard(sessionId, request, store, tokens, out var session)) return Results.Unauthorized();
    var feature = Truncate(body.Feature?.Trim(), 2_000);
    if (string.IsNullOrWhiteSpace(feature)) return Results.BadRequest(new { error = "Describe the feature to test." });
    try
    {
        var cases = await ai.GenerateTestCasesAsync(session, feature, body.Count ?? 6, cancellationToken);
        store.Touch(session);
        return Results.Ok(new { testCases = cases });
    }
    catch (AiAnalysisUnavailableException exception)
    {
        return Results.Problem(exception.Message, statusCode: StatusCodes.Status503ServiceUnavailable,
            title: "AI test generation unavailable");
    }
});

app.MapPost("/api/sessions/{sessionId:guid}/test-runs", (Guid sessionId, ScheduleTestRunRequest body,
    HttpRequest request, SessionStore store, TokenService tokens) =>
{
    if (!TryDashboard(sessionId, request, store, tokens, out var session)) return Results.Unauthorized();
    if (!session.TestCases.ContainsKey(body.TestCaseId)) return Results.BadRequest(new { error = "Test case not found." });
    if (!session.Devices.ContainsKey(body.DeviceId)) return Results.BadRequest(new { error = "Test device not found." });
    if (session.Devices[body.DeviceId].Platform == "Automation" &&
        session.TestCases[body.TestCaseId].AutomationSteps.Count == 0)
        return Results.BadRequest(new { error = "This test case has no executable automation steps." });
    if (body.BuildId is not null && !session.Builds.ContainsKey(body.BuildId.Value))
        return Results.BadRequest(new { error = "Build not found." });
    var run = new TestRun
    {
        Id = Guid.NewGuid(),
        TestCaseId = body.TestCaseId,
        DeviceId = body.DeviceId,
        BuildId = body.BuildId,
        Status = "ready"
    };
    session.TestRuns[run.Id] = run;
    store.Touch(session);
    return Results.Ok(ToTestRun(session, run));
});

app.MapPost("/api/sessions/{sessionId:guid}/test-runs/{runId:guid}/start", (Guid sessionId, Guid runId,
    StartTestRunRequest body, HttpRequest request, SessionStore store, TokenService tokens) =>
{
    if (!AuthenticateDevice(sessionId, request, store, tokens, out var session, out var device)) return Results.Unauthorized();
    if (!session.TestRuns.TryGetValue(runId, out var run) || run.DeviceId != device.Id) return Results.NotFound();
    if (run.Status != "ready") return Results.Conflict(new { error = "Only a ready test can be started." });
    var captureId = Truncate(body.CaptureId?.Trim(), 100);
    if (string.IsNullOrWhiteSpace(captureId)) return Results.BadRequest(new { error = "A capture ID is required." });
    run.CaptureId = captureId;
    run.Status = "running";
    run.StartedAt = DateTimeOffset.UtcNow;
    if (session.TestCases.TryGetValue(run.TestCaseId, out var testCase)) run.TotalSteps = testCase.AutomationSteps.Count;
    store.Touch(session);
    return Results.Ok(ToTestRun(session, run));
});

app.MapPost("/api/sessions/{sessionId:guid}/test-runs/{runId:guid}/complete", async (Guid sessionId, Guid runId,
    CompleteTestRunRequest body, HttpRequest request, SessionStore store, TokenService tokens,
    AiIssueAnalysisService ai, CancellationToken cancellationToken) =>
{
    if (!AuthenticateDevice(sessionId, request, store, tokens, out var session, out var device)) return Results.Unauthorized();
    if (!session.TestRuns.TryGetValue(runId, out var run) || run.DeviceId != device.Id) return Results.NotFound();
    if (run.Status != "running") return Results.Conflict(new { error = "Only a running test can be completed." });
    var verdict = body.Verdict?.Trim().ToLowerInvariant();
    if (verdict is not ("passed" or "failed" or "blocked"))
        return Results.BadRequest(new { error = "Verdict must be passed, failed, or blocked." });
    run.Status = verdict;
    run.Notes = Truncate(body.Notes?.Trim(), 2_000);
    run.CaptureId ??= Truncate(body.CaptureId?.Trim(), 100);
    run.CompletedAt = DateTimeOffset.UtcNow;
    AiIssueAnalysis? analysis = null;
    string? analysisMessage = null;
    if (verdict == "failed" && !string.IsNullOrWhiteSpace(run.CaptureId))
    {
        var issue = session.Events.Where(x => x.DeviceId == device.Id && x.Category != "attachment" &&
                                               x.Context.GetValueOrDefault("captureId") == run.CaptureId &&
                                               (x.Level is "error" or "fatal" || x.Category is "crash" or "anr" or "performance"))
            .OrderByDescending(x => x.OccurredAt).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x.Fingerprint));
        if (issue is not null)
        {
            run.FailureFingerprint = issue.Fingerprint;
            try { analysis = await ai.AnalyzeAsync(session, issue.Fingerprint!, false, cancellationToken); }
            catch (AiAnalysisUnavailableException exception) { analysisMessage = exception.Message; }
        }
        else analysisMessage = "No instrumented failure was found for this capture. The recording remains available for manual review.";
    }
    store.Touch(session);
    return Results.Ok(new TestRunResult(run, analysis, analysisMessage));
});

app.MapPost("/api/sessions/{sessionId:guid}/test-runs/{runId:guid}/progress", (Guid sessionId, Guid runId,
    AutomationProgressRequest body, HttpRequest request, SessionStore store, TokenService tokens) =>
{
    if (!AuthenticateDevice(sessionId, request, store, tokens, out var session, out var device)) return Results.Unauthorized();
    if (!session.TestRuns.TryGetValue(runId, out var run) || run.DeviceId != device.Id) return Results.NotFound();
    if (run.Status != "running") return Results.Conflict(new { error = "Only a running test accepts progress." });
    if (body.StepIndex < 0 || body.TotalSteps is < 1 or > 100 || body.StepIndex > body.TotalSteps)
        return Results.BadRequest(new { error = "Invalid automation step progress." });
    var status = Clean(body.Status, "running", 20);
    if (status is not ("running" or "passed" or "failed" or "skipped"))
        return Results.BadRequest(new { error = "Invalid automation step status." });
    run.CurrentStep = body.StepIndex;
    run.TotalSteps = body.TotalSteps;
    run.LastStepMessage = Truncate(Redact(body.Message), 1_000);
    var context = new Dictionary<string, string>
    {
        ["captureId"] = run.CaptureId ?? string.Empty,
        ["testRunId"] = run.Id.ToString("D"),
        ["stepIndex"] = body.StepIndex.ToString(),
        ["stepStatus"] = status
    };
    store.AddEvent(session, BuildEvent(device, status == "failed" ? "error" : "info", "automation",
        $"step_{status}", run.LastStepMessage ?? $"Automation step {body.StepIndex} {status}.", null, null,
        body.Fingerprint, null, run.BuildId, null, context, null));
    store.Touch(session);
    return Results.Accepted();
});

app.MapGet("/api/sessions/{sessionId:guid}/report.md", (Guid sessionId, HttpRequest request,
    SessionStore store, TokenService tokens) =>
{
    if (!TryDashboard(sessionId, request, store, tokens, out var session)) return Results.Unauthorized();
    var markdown = BuildMarkdownReport(session);
    return Results.File(Encoding.UTF8.GetBytes(markdown), "text/markdown; charset=utf-8",
        $"devbridge-{session.Id:N}-report.md");
});

app.MapGet("/api/sessions/{sessionId:guid}/device", (Guid sessionId, HttpRequest request, SessionStore store, TokenService tokens) =>
{
    if (!AuthenticateDevice(sessionId, request, store, tokens, out var session, out var device)) return Results.Unauthorized();
    device.LastSeenAt = DateTimeOffset.UtcNow;
    return Results.Ok(new { session.Id, session.ProjectName, session.AppName, session.PackageName, session.ExpiresAt, device = new { device.Id, device.Name }, builds = session.Builds.Values.OrderByDescending(x => x.UploadedAt).Select(ToBuild), testRuns = session.TestRuns.Values.Where(x => x.DeviceId == device.Id && x.Status is "ready" or "running").OrderBy(x => x.CreatedAt).Select(run => ToTestRun(session, run)) });
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
    var relatedFingerprint = Truncate(form["fingerprint"].ToString().Trim(), 160);
    var captureId = Truncate(form["captureId"].ToString().Trim(), 100);
    DateTimeOffset? capturedAt = DateTimeOffset.TryParse(form["capturedAt"], out var parsedCapturedAt)
        ? parsedCapturedAt : null;
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
        SizeBytes = file.Length,
        RelatedFingerprint = string.IsNullOrWhiteSpace(relatedFingerprint) ? null : relatedFingerprint,
        CaptureId = string.IsNullOrWhiteSpace(captureId) ? null : captureId,
        CapturedAt = capturedAt
    };
    session.Attachments[id] = attachment;
    store.Touch(session);
    store.AddEvent(session, BuildEvent(device, "feedback", "attachment", $"{kind}_attached",
        $"Tester attached {kind} evidence.", null, null, attachment.RelatedFingerprint ?? $"attachment:{id:N}", null, buildId,
        new Dictionary<string, double> { ["sizeBytes"] = file.Length },
        new Dictionary<string, string>
        {
            ["attachmentId"] = id.ToString("D"),
            ["contentType"] = contentType,
            ["captureId"] = attachment.CaptureId ?? string.Empty
        }, attachment.CapturedAt));
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
static bool TryDashboard(Guid id, HttpRequest request, SessionStore store, TokenService tokens, out TestSession session) =>
    store.TryGet(id, out session!) &&
    tokens.Verify(request.Headers["X-Dashboard-Token"].FirstOrDefault(), session.DashboardTokenHash);
static object ToTestRun(TestSession session, TestRun run)
{
    session.TestCases.TryGetValue(run.TestCaseId, out var testCase);
    return new
    {
        run.Id,
        run.TestCaseId,
        run.DeviceId,
        run.BuildId,
        run.Status,
        run.CaptureId,
        run.Notes,
        run.FailureFingerprint,
        run.CurrentStep,
        run.TotalSteps,
        run.LastStepMessage,
        run.CreatedAt,
        run.StartedAt,
        run.CompletedAt,
        testCase
    };
}
static object QualityReport(TestSession session)
{
    var runs = session.TestRuns.Values.ToArray();
    var passed = runs.Count(x => x.Status == "passed");
    var failed = runs.Count(x => x.Status == "failed");
    var blocked = runs.Count(x => x.Status == "blocked");
    var running = runs.Count(x => x.Status == "running");
    var ready = runs.Count(x => x.Status == "ready");
    var executed = passed + failed + blocked;
    var verdict = failed > 0 ? "failed" : blocked > 0 ? "blocked" : ready + running > 0 || executed == 0 ? "incomplete" : "passed";
    return new
    {
        verdict,
        total = runs.Length,
        executed,
        passed,
        failed,
        blocked,
        running,
        ready,
        passRate = executed == 0 ? 0 : Math.Round(passed * 100d / executed, 1),
        diagnosedFailures = runs.Count(x => x.FailureFingerprint is not null && session.AiAnalyses.ContainsKey(x.FailureFingerprint))
    };
}
static string BuildMarkdownReport(TestSession session)
{
    var quality = session.TestRuns.Values.ToArray();
    var passed = quality.Count(x => x.Status == "passed");
    var failed = quality.Count(x => x.Status == "failed");
    var blocked = quality.Count(x => x.Status == "blocked");
    var executed = passed + failed + blocked;
    var verdict = failed > 0 ? "FAILED" : blocked > 0 ? "BLOCKED" : quality.Any(x => x.Status is "ready" or "running") || executed == 0 ? "INCOMPLETE" : "PASSED";
    var text = new StringBuilder();
    text.AppendLine($"# DevBridge test report — {session.AppName}").AppendLine();
    text.AppendLine($"- Project: {session.ProjectName}");
    text.AppendLine($"- Package: {session.PackageName ?? "Not supplied"}");
    text.AppendLine($"- Generated: {DateTimeOffset.UtcNow:O}");
    text.AppendLine($"- Release verdict: **{verdict}**");
    text.AppendLine($"- Results: {passed} passed, {failed} failed, {blocked} blocked, {quality.Length - executed} pending/running").AppendLine();
    text.AppendLine("## Test runs").AppendLine();
    if (quality.Length == 0) text.AppendLine("No test runs were scheduled.");
    foreach (var run in quality.OrderBy(x => x.CreatedAt))
    {
        session.TestCases.TryGetValue(run.TestCaseId, out var testCase);
        session.Devices.TryGetValue(run.DeviceId, out var device);
        text.AppendLine($"### [{run.Status.ToUpperInvariant()}] {testCase?.Title ?? "Deleted test case"}");
        text.AppendLine($"- Device: {device?.Name ?? run.DeviceId.ToString("D")}");
        text.AppendLine($"- Expected: {testCase?.ExpectedResult ?? "Unavailable"}");
        if (!string.IsNullOrWhiteSpace(run.Notes)) text.AppendLine($"- Tester notes: {run.Notes.ReplaceLineEndings(" ")}");
        if (run.FailureFingerprint is not null && session.AiAnalyses.TryGetValue(run.FailureFingerprint, out var analysis))
        {
            text.AppendLine($"- AI diagnosis: {analysis.Summary.ReplaceLineEndings(" ")}");
            text.AppendLine($"- Problem location: {analysis.ProblemLocation.ReplaceLineEndings(" ")}");
            text.AppendLine($"- Suggested fix: {analysis.SuggestedFix.ReplaceLineEndings(" ")}");
            text.AppendLine($"- Confidence: {analysis.Confidence}%");
        }
        text.AppendLine();
    }
    text.AppendLine("## Evidence limitations").AppendLine();
    text.AppendLine("A pass/fail verdict reflects the scheduled runs and captured evidence in this session. It does not prove coverage of unscheduled behavior.");
    return text.ToString();
}
static string Clean(string? value, string fallback, int max) { var result = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim().ToLowerInvariant(); return result.Length <= max ? result : result[..max]; }
static IReadOnlyList<AutomationStep> CleanAutomationSteps(IReadOnlyList<AutomationStep>? source)
{
    if (source is null or { Count: 0 } || source.Count > 60) return [];
    var actions = new HashSet<string>(StringComparer.Ordinal)
    {
        "resetApp", "launchApp", "tap", "type", "clear", "scroll", "swipe", "back", "waitFor",
        "assertVisible", "assertText", "assertNotVisible", "assertEnabled", "takeScreenshot", "stop"
    };
    var result = new List<AutomationStep>(source.Count);
    for (var index = 0; index < source.Count; index++)
    {
        var item = source[index];
        if (!actions.Contains(item.Action) || item.TimeoutSeconds is < 1 or > 30 ||
            item.Risk is not ("safe" or "controlled" or "forbidden")) return [];
        var secret = item.Secret?.Trim().ToUpperInvariant();
        if (secret is not null && (!secret.StartsWith("TEST_", StringComparison.Ordinal) ||
                                   secret.Any(x => !char.IsAsciiLetterUpper(x) && !char.IsAsciiDigit(x) && x != '_')))
            return [];
        result.Add(new AutomationStep(index + 1, item.Action, Truncate(item.Target?.Trim(), 200),
            Truncate(item.Value?.Trim(), 500), Truncate(secret, 100), Truncate(item.Expected?.Trim(), 500),
            item.TimeoutSeconds, item.Risk));
    }
    return result;
}
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
