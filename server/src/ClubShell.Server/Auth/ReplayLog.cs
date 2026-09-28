using System.Collections.Concurrent;

namespace ClubShell.Server.Auth;

/// <summary>
/// Remembers signature tuples <c>(pcId, X-Timestamp, X-Signature)</c> for twice the signature window (DESIGN §3.3, D-4).
/// A repeat is never rejected — two identical GETs in one second, a retry after refresh without secret rotation and
/// old agents' retries all repeat legitimately; money is protected by idempotency keys and unique indexes. The caller
/// only logs repeated POST/PATCH without <c>Idempotency-Key</c>. In memory: a restart forgets, which is fine for a log.
/// </summary>
public sealed class ReplayLog(TimeProvider clock, AuthOptions options)
{
    private readonly ConcurrentDictionary<(Guid, string, string), DateTimeOffset> _seen = new();
    private long _nextSweep;

    /// <summary>Records the tuple; <c>true</c> when it was already seen within the TTL.</summary>
    public bool Seen(Guid pcId, string timestamp, string signature)
    {
        var now = clock.GetUtcNow();
        var ttl = TimeSpan.FromSeconds(2 * options.SignatureWindowSec);
        Sweep(now, ttl);
        var key = (pcId, timestamp, signature.ToLowerInvariant());
        var seen = _seen.TryGetValue(key, out var expires) && expires > now;
        _seen[key] = now + ttl;
        return seen;
    }

    private void Sweep(DateTimeOffset now, TimeSpan ttl)
    {
        var next = Interlocked.Read(ref _nextSweep);
        if (now.UtcTicks < next || Interlocked.CompareExchange(ref _nextSweep, (now + ttl).UtcTicks, next) != next)
        {
            return;
        }

        foreach (var (key, expires) in _seen)
        {
            if (expires <= now)
            {
                _seen.TryRemove(key, out _);
            }
        }
    }
}
