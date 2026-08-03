using System.Text.Json;
using System.Threading.Channels;
using Azure.Data.Tables;
using Azure.Identity;
using DevBridge.Models;
using DevBridge.Options;
using Microsoft.Extensions.Options;

namespace DevBridge.Services;

public sealed record ArchivedDiagnostic(Guid SessionId, DeviceEvent Event);

public sealed class DiagnosticArchiveService : BackgroundService
{
    private readonly Channel<ArchivedDiagnostic> _queue = Channel.CreateBounded<ArchivedDiagnostic>(
        new BoundedChannelOptions(2_000) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    private readonly TableClient? _table;
    private readonly ILogger<DiagnosticArchiveService> _logger;
    private long _archivedCount;
    private long _failedCount;

    public bool Enabled => _table is not null;
    public bool Initialized { get; private set; }
    public long ArchivedCount => Interlocked.Read(ref _archivedCount);
    public long FailedCount => Interlocked.Read(ref _failedCount);
    public DateTimeOffset? LastFailureAt { get; private set; }

    public DiagnosticArchiveService(IOptions<DevBridgeOptions> options, ILogger<DiagnosticArchiveService> logger)
    {
        _logger = logger;
        if (string.IsNullOrWhiteSpace(options.Value.StorageAccountName)) return;
        var endpoint = new Uri($"https://{options.Value.StorageAccountName}.table.core.windows.net");
        _table = new TableClient(endpoint, options.Value.DiagnosticTableName, new DefaultAzureCredential());
    }

    public void Enqueue(Guid sessionId, DeviceEvent item) => _queue.Writer.TryWrite(new ArchivedDiagnostic(sessionId, item));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_table is null) return;
        try
        {
            await _table.CreateIfNotExistsAsync(stoppingToken);
            Initialized = true;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "DevBridge diagnostics archive could not initialize.");
            return;
        }

        await foreach (var archived in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                var item = archived.Event;
                var entity = new TableEntity(archived.SessionId.ToString("N"),
                    $"{DateTimeOffset.MaxValue.Ticks - item.OccurredAt.Ticks:D19}-{item.Id:N}")
                {
                    ["EventId"] = item.Id.ToString("D"),
                    ["DeviceId"] = item.DeviceId.ToString("D"),
                    ["DeviceName"] = item.DeviceName,
                    ["Level"] = item.Level,
                    ["Category"] = item.Category,
                    ["EventType"] = item.EventType,
                    ["Message"] = item.Message,
                    ["Details"] = item.Details ?? string.Empty,
                    ["StackTrace"] = item.StackTrace ?? string.Empty,
                    ["Fingerprint"] = item.Fingerprint ?? string.Empty,
                    ["Screen"] = item.Screen ?? string.Empty,
                    ["BuildId"] = item.BuildId?.ToString("D") ?? string.Empty,
                    ["MetricsJson"] = JsonSerializer.Serialize(item.Metrics),
                    ["ContextJson"] = JsonSerializer.Serialize(item.Context),
                    ["OccurredAt"] = item.OccurredAt,
                    ["ReceivedAt"] = item.ReceivedAt
                };
                await _table.UpsertEntityAsync(entity, TableUpdateMode.Replace, stoppingToken);
                Interlocked.Increment(ref _archivedCount);
            }
            catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
            {
                Interlocked.Increment(ref _failedCount);
                LastFailureAt = DateTimeOffset.UtcNow;
                _logger.LogWarning(exception, "A diagnostic event could not be archived to Azure Table Storage.");
            }
        }
    }
}
