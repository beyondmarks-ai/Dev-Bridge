using System.Collections.Concurrent;
using DevBridge.Models;

namespace DevBridge.Services;

public sealed class SessionStore
{
    private readonly ConcurrentDictionary<Guid, TestSession> _sessions = new();
    private readonly ConcurrentDictionary<string, DownloadTicket> _tickets = new();
    public void Add(TestSession session) => _sessions[session.Id] = session;
    public bool TryGet(Guid id, out TestSession session)
    {
        if (_sessions.TryGetValue(id, out session!) && session.ExpiresAt > DateTimeOffset.UtcNow) return true;
        session = null!; return false;
    }
    public void AddEvent(TestSession session, DeviceEvent item)
    {
        session.Events.Enqueue(item);
        while (session.Events.Count > 1_000) session.Events.TryDequeue(out _);
    }
    public void AddTicket(string token, DownloadTicket ticket) => _tickets[token] = ticket;
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
    }
}
