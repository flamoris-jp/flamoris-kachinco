using System.Diagnostics;

namespace Kachinco.Infrastructure;

public sealed record PreviewWorkStatistics(long Count, long CacheHits, long CacheMisses,
    double AverageMilliseconds, double RecentP95Milliseconds, double MaximumMilliseconds,
    double CacheHitAverageMilliseconds, double CacheMissAverageMilliseconds);

// Constant storage; p95 describes the latest 128 requests, average/max the lifetime.
public sealed class PreviewWorkMetrics
{
    private readonly object gate = new();
    private readonly double[] recent = new double[128];
    private long count, hits, misses;
    private double total, maximum, hitTotal, missTotal;
    private long lastReport = Stopwatch.GetTimestamp();
    public void Record(double milliseconds, bool? cacheHit = null)
    {
        lock (gate)
        {
            recent[count % recent.Length] = milliseconds; count++;
            total += milliseconds; maximum = Math.Max(maximum, milliseconds);
            if (cacheHit == true) { hits++; hitTotal += milliseconds; }
            else if (cacheHit == false) { misses++; missTotal += milliseconds; }
        }
    }
    public PreviewWorkStatistics Statistics
    {
        get
        {
            lock (gate)
            {
                int n = (int)Math.Min(count, recent.Length);
                var sorted = recent.AsSpan(0, n).ToArray(); Array.Sort(sorted);
                return new(count, hits, misses, count == 0 ? 0 : total / count,
                    n == 0 ? 0 : sorted[(int)Math.Ceiling(n * .95) - 1], maximum,
                    hits == 0 ? 0 : hitTotal / hits, misses == 0 ? 0 : missTotal / misses);
            }
        }
    }
    internal bool ShouldReport()
    {
        lock (gate)
        {
            long now = Stopwatch.GetTimestamp();
            if (Stopwatch.GetElapsedTime(lastReport, now).TotalSeconds < 1) return false;
            lastReport = now; return true;
        }
    }
}
