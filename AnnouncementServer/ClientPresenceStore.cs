using System.Collections.Concurrent;

namespace NteGuitar.AnnouncementServer;

public sealed class ClientPresenceStore
{
    private static readonly TimeSpan InactiveAfter = TimeSpan.FromMinutes(6);
    private readonly ConcurrentDictionary<Guid, ClientPresenceSession> _sessions = new();
    private readonly TimeProvider _timeProvider;
    private long _lastPruneUtcTicks;

    public ClientPresenceStore(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public void Heartbeat(Guid sessionId, string version)
    {
        var now = _timeProvider.GetUtcNow();
        PruneExpired(now, onlyIfDue: true);
        _sessions[sessionId] = new ClientPresenceSession(version, now);
    }

    public ClientPresenceSnapshot Snapshot()
    {
        var now = _timeProvider.GetUtcNow();
        PruneExpired(now, onlyIfDue: false);

        var versions = _sessions.Values
            .GroupBy(session => session.Version, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new ClientPresenceVersionCount(group.Key, group.Count()))
            .ToArray();

        return new ClientPresenceSnapshot(now, versions);
    }

    private void PruneExpired(DateTimeOffset now, bool onlyIfDue)
    {
        if (onlyIfDue)
        {
            var ticks = now.UtcTicks;
            while (true)
            {
                var previous = Interlocked.Read(ref _lastPruneUtcTicks);
                if (ticks - previous < TimeSpan.TicksPerMinute) return;
                if (Interlocked.CompareExchange(ref _lastPruneUtcTicks, ticks, previous) == previous) break;
            }
        }

        var cutoff = now - InactiveAfter;
        var sessionsCollection = (ICollection<KeyValuePair<Guid, ClientPresenceSession>>)_sessions;
        foreach (var entry in _sessions)
        {
            if (entry.Value.LastSeenUtc <= cutoff)
                sessionsCollection.Remove(entry);
        }
    }
}

public sealed record ClientPresenceSession(string Version, DateTimeOffset LastSeenUtc);
public sealed record ClientPresenceVersionCount(string Version, int ActiveInstances);
public sealed record ClientPresenceSnapshot(DateTimeOffset GeneratedAtUtc, IReadOnlyList<ClientPresenceVersionCount> Versions);
public sealed record ClientPresenceHeartbeatRequest(Guid SessionId, string? Version);
