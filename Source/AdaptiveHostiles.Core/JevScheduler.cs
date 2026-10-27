namespace AdaptiveHostiles.Core;

/// <summary>
/// Off-thread request throttling for Jev decisions. The game thread calls
/// <see cref="TryBegin"/> and <see cref="TryTake"/>; the network work runs on the thread pool
/// under a hard deadline. One request per key, a global concurrency cap, exponential backoff
/// after failures, and stale-result invalidation via <see cref="Reset"/>.
/// </summary>
public sealed class JevScheduler<T> : IDisposable
{
    private sealed class InFlight { }
    private sealed record Completed(T Value, DateTime At);

    private readonly object _gate = new();
    private readonly Dictionary<string, InFlight> _inFlight = new();
    private readonly Dictionary<string, Completed> _done = new();
    private readonly int _maxConcurrent;
    private readonly TimeSpan _deadline;
    private readonly Func<DateTime> _now;
    private readonly Func<T, bool> _isModelResult;
    private readonly TimeSpan _baseBackoff;
    private readonly TimeSpan _maxBackoff;
    private CancellationTokenSource _cts = new();
    private long _generation;
    private int _failures;
    private DateTime _blockedUntil = DateTime.MinValue;

    public JevScheduler(
        AdaptiveHostilesSettings? settings = null,
        Func<T, bool>? isModelResult = null,
        Func<DateTime>? now = null,
        TimeSpan? baseBackoff = null,
        TimeSpan? maxBackoff = null)
    {
        settings ??= new AdaptiveHostilesSettings();
        _maxConcurrent = Math.Max(1, settings.MaxConcurrentRequests);
        _deadline = settings.NetworkDeadline;
        _isModelResult = isModelResult ?? (_ => true);
        _now = now ?? (() => DateTime.UtcNow);
        _baseBackoff = baseBackoff ?? TimeSpan.FromSeconds(5);
        _maxBackoff = maxBackoff ?? TimeSpan.FromMinutes(5);
    }

    public int InFlightCount { get { lock (_gate) return _inFlight.Count; } }
    public int ConsecutiveFailures { get { lock (_gate) return _failures; } }

    /// <summary>True when <see cref="TryBegin"/> would start work now, so callers can skip preparing it otherwise.</summary>
    public bool CanBegin(string key)
    {
        lock (_gate)
            return !_inFlight.ContainsKey(key) && _inFlight.Count < _maxConcurrent && _now() >= _blockedUntil;
    }

    /// <summary>Starts <paramref name="work"/> unless the key is busy, the cap is reached or we are backing off.</summary>
    public bool TryBegin(string key, Func<CancellationToken, Task<T>> work)
    {
        InFlight mine;
        CancellationToken token;
        long generation;
        lock (_gate)
        {
            if (_inFlight.ContainsKey(key) || _inFlight.Count >= _maxConcurrent || _now() < _blockedUntil)
                return false;
            mine = new InFlight();
            _inFlight[key] = mine;
            generation = _generation;
            token = _cts.Token;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(token);
                linked.CancelAfter(_deadline);
                var value = await work(linked.Token).ConfigureAwait(false);
                Finish(key, mine, generation, value, hasValue: true);
            }
            catch (Exception)
            {
                // Timeouts, HTTP errors and cancellation all degrade to "no answer".
                Finish(key, mine, generation, default, hasValue: false);
            }
        });
        return true;
    }

    private void Finish(string key, InFlight mine, long generation, T? value, bool hasValue)
    {
        lock (_gate)
        {
            if (_inFlight.TryGetValue(key, out var current) && ReferenceEquals(current, mine))
                _inFlight.Remove(key);
            if (generation != _generation) return; // stale: discarded

            if (hasValue && _isModelResult(value!))
            {
                _failures = 0;
                _blockedUntil = DateTime.MinValue;
            }
            else
            {
                _failures++;
                var seconds = Math.Min(_maxBackoff.TotalSeconds,
                    _baseBackoff.TotalSeconds * Math.Pow(2, Math.Min(_failures - 1, 20)));
                _blockedUntil = _now().AddSeconds(seconds);
            }
            if (hasValue) _done[key] = new Completed(value!, _now());
        }
    }

    /// <summary>Takes a finished result on the main thread; results older than <paramref name="maxAge"/> are dropped.</summary>
    public bool TryTake(string key, TimeSpan maxAge, out T result)
    {
        lock (_gate)
        {
            if (_done.Remove(key, out var done) && _now() - done.At <= maxAge)
            {
                result = done.Value;
                return true;
            }
        }
        result = default!;
        return false;
    }

    /// <summary>Cancels in-flight work and discards pending results (call on save/load or map change).</summary>
    public void Reset()
    {
        CancellationTokenSource old;
        lock (_gate)
        {
            _generation++;
            _inFlight.Clear();
            _done.Clear();
            old = _cts;
            _cts = new CancellationTokenSource();
        }
        old.Cancel();
        old.Dispose();
    }

    public void Dispose() => Reset();
}

/// <summary>Short-lived cache so raid evaluation can be computed ahead of the incident.</summary>
public sealed class DecisionCache<T>(TimeSpan ttl, Func<DateTime>? now = null)
{
    private readonly Dictionary<string, (T Value, DateTime At)> _items = new();
    private readonly Func<DateTime> _now = now ?? (() => DateTime.UtcNow);

    public void Put(string key, T value) { lock (_items) _items[key] = (value, _now()); }

    public bool TryGet(string key, out T value)
    {
        lock (_items)
        {
            if (_items.TryGetValue(key, out var item) && _now() - item.At <= ttl)
            {
                value = item.Value;
                return true;
            }
        }
        value = default!;
        return false;
    }
}
