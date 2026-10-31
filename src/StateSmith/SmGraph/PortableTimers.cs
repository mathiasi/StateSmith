#nullable enable

using System.Collections.Generic;

namespace StateSmith.SmGraph;

/// <summary>
/// EXPERIMENTAL (north-star). Timers found in portable code guards: `after(X)` and `elapsed(T) >= X`.
/// Used to generate `next_deadline_ms()`, so a host can sleep until the next timer instead of polling `do`.
/// </summary>
public class PortableTimers
{
    /// <param name="state">state whose guard waits on the timer (the timer only matters while it is active)</param>
    /// <param name="start">portable code expression for the start timestamp</param>
    /// <param name="duration">portable code expression for the duration in ms</param>
    /// <param name="strict">`elapsed(T) > X` (deadline is X + 1) instead of `>=`</param>
    public record Timer(NamedVertex state, string start, string duration, bool strict);

    public readonly List<Timer> timers = new();

    public void Add(Timer timer)
    {
        if (!timers.Contains(timer))
            timers.Add(timer);
    }
}
