using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace GlamByMariya.Infrastructure.Services;

/// <summary>
/// Account-aware brute-force protection for the login endpoint: at most
/// <see cref="Limit"/> failures per IP+email per minute. State is per-process and
/// in-memory only, so it is reset by a restart, redeploy or additional instance.
///
/// Memory safety: a periodic sweep removes expired windows and a hard key ceiling
/// keeps the dictionary bounded. Both paths use conditional removal, so a window
/// refreshed concurrently by another request is never discarded by a sweep.
/// See docs/rate-limiting.md for the operational limits.
/// </summary>
public sealed class LoginAttemptLimiter
{
    public const int Limit = 10;
    public const int MaxTrackedKeys = 20_000;
    internal static readonly TimeSpan Window = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromMinutes(1);
    private const int MinimumEvictionBatch = MaxTrackedKeys / 10;

    private readonly ConcurrentDictionary<string, WindowState> windows = new();
    private readonly TimeProvider clock;
    private long nextCleanupTicks;

    public LoginAttemptLimiter() : this(TimeProvider.System) { }

    public LoginAttemptLimiter(TimeProvider clock)
    {
        this.clock = clock;
        nextCleanupTicks = clock.GetUtcNow().UtcTicks + CleanupInterval.Ticks;
    }

    /// <summary>Tracked IP+email windows; diagnostics and tests only.</summary>
    public int TrackedKeyCount => windows.Count;

    public bool TryAcquire(string email, string? remoteIp)
    {
        var key = Hash((remoteIp ?? "unknown") + "\n" + email.Trim().ToLowerInvariant());
        var now = clock.GetUtcNow();
        var acquired = TryAcquire(key, now);
        MaybeSweep(now);
        return acquired;
    }

    private bool TryAcquire(string key, DateTimeOffset now)
    {
        while (true)
        {
            if (!windows.TryGetValue(key, out var existing))
            {
                var fresh = new WindowState(now, 0);
                if (!windows.TryAdd(key, fresh)) continue;
                existing = fresh;
                TrimToCap(key);
            }
            if (now - existing.Start >= Window)
            {
                if (windows.TryUpdate(key, new WindowState(now, 1), existing)) return true;
                continue;
            }
            if (existing.Count >= Limit) return false;
            if (windows.TryUpdate(key, new WindowState(existing.Start, existing.Count + 1), existing)) return true;
        }
    }

    private void MaybeSweep(DateTimeOffset now)
    {
        var next = Interlocked.Read(ref nextCleanupTicks);
        if (now.UtcTicks < next) return;
        // Exactly one caller sweeps per interval; the rest keep serving traffic.
        if (Interlocked.CompareExchange(ref nextCleanupTicks, now.UtcTicks + CleanupInterval.Ticks, next) != next) return;
        foreach (var entry in windows)
            if (now - entry.Value.Start >= Window) RemoveIfUnchanged(entry);
        TrimToCap();
    }

    /// <summary>
    /// Keeps the dictionary near <see cref="MaxTrackedKeys"/> under sustained
    /// attack. Eviction is batched so the sort is amortised rather than paid on
    /// every insert.
    /// </summary>
    private void TrimToCap(string? keepKey = null)
    {
        var excess = windows.Count - MaxTrackedKeys;
        if (excess <= 0) return;
        var remove = Math.Max(excess, MinimumEvictionBatch);
        foreach (var entry in windows
                     .Where(x => keepKey == null || x.Key != keepKey)
                     .OrderBy(x => x.Value.Start)
                     .Take(remove))
            RemoveIfUnchanged(entry);
    }

    // Conditional removal: only drop the exact value we inspected, never a window
    // that another request refreshed between inspection and removal.
    private void RemoveIfUnchanged(KeyValuePair<string, WindowState> entry) =>
        ((ICollection<KeyValuePair<string, WindowState>>)windows).Remove(entry);

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..24];
    private sealed record WindowState(DateTimeOffset Start, int Count);
}
