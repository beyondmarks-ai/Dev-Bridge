using System.Collections.Concurrent;
using DevBridge.Models;

namespace DevBridge.Services;

public sealed class SessionStore : IHostedService
{
    private readonly DiagnosticArchiveService _archive;
    private readonly SessionPersistenceService _persistence;
    private readonly ConcurrentDictionary<Guid, TestSession> _sessions = new();
    private readonly ConcurrentDictionary<string, DownloadTicket> _tickets = new();
    private readonly ConcurrentDictionary<string, SdkHandoffTicket> _sdkTickets = new();
    public SessionStore(DiagnosticArchiveService archive, SessionPersistenceService persistence)
    {
        _archive = archive;
        _persistence = persistence;
    }
    public void Add(TestSession session) { _sessions[session.Id] = session; Touch(session); }
    public void Touch(TestSession session) => _persistence.QueueSave(session);
    public bool TryGet(Guid id, out TestSession session)
    {
        if (_sessions.TryGetValue(id, out session!) && session.ExpiresAt > DateTimeOffset.UtcNow) return true;
        session = null!; return false;
    }
    public void AddEvent(TestSession session, DeviceEvent item)
    {
        session.Events.Enqueue(item);
        _archive.Enqueue(session.Id, item);
        while (session.Events.Count > 1_000) session.Events.TryDequeue(out _);
    }
    public void AddTicket(string token, DownloadTicket ticket) => _tickets[token] = ticket;
    public void AddSdkTicket(string token, SdkHandoffTicket ticket) => _sdkTickets[token] = ticket;
    public bool TryConsumeSdkTicket(string token, out SdkHandoffTicket ticket)
    {
        if (_sdkTickets.TryRemove(token, out ticket!) && ticket.ExpiresAt > DateTimeOffset.UtcNow) return true;
        ticket = null!; return false;
    }
    public bool TryConsumeTicket(string token, out DownloadTicket ticket)
    {
        if (_tickets.TryRemove(token, out ticket!) && ticket.ExpiresAt > DateTimeOffset.UtcNow) return true;
        ticket = null!; return false;
    }
    public void RemoveExpired()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var x in _sessions.Where(x => x.Value.ExpiresAt <= now))
        {
            if (!_sessions.TryRemove(x.Key, out var expired)) continue;
            _persistence.QueueDelete(expired.Id);
            foreach (var build in expired.Builds.Values)
            {
                try
                {
                    if (File.Exists(build.StoredPath)) File.Delete(build.StoredPath);
                }
                catch (IOException)
                {
                    // A concurrent download can briefly hold the artifact open.
                }
            }
        }
        foreach (var x in _tickets.Where(x => x.Value.ExpiresAt <= now)) _tickets.TryRemove(x.Key, out _);
        foreach (var x in _sdkTickets.Where(x => x.Value.ExpiresAt <= now)) _sdkTickets.TryRemove(x.Key, out _);
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var session in await _persistence.LoadAsync(cancellationToken))
        {
            foreach (var item in await _archive.LoadRecentAsync(session.Id, 300, cancellationToken)) session.Events.Enqueue(item);
            _sessions[session.Id] = session;
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
