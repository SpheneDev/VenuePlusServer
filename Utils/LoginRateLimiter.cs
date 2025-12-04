using System;
using System.Collections.Concurrent;

namespace VenuePlus.Server;

public static class LoginRateLimiter
{
    private sealed class Rate
    {
        public DateTimeOffset WindowStart;
        public int Count;
    }
    private static readonly ConcurrentDictionary<string, Rate> Map = new(StringComparer.Ordinal);
    private const int Limit = 5;
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);
    public static bool Allow(string key)
    {
        var now = DateTimeOffset.UtcNow;
        var r = Map.GetOrAdd(key, _ => new Rate { WindowStart = now, Count = 0 });
        if ((now - r.WindowStart) > Window)
        {
            r.WindowStart = now;
            r.Count = 0;
        }
        r.Count++;
        return r.Count <= Limit;
    }
}
