namespace DumpDetective.Analysis.Trace;

/// <summary>
/// Per-analyzer frame string interner.
/// Deduplicates frame strings (method names, module names) so that identical strings
/// share a single heap allocation across all events in a trace.
///
/// In a typical CPU trace, the same 50–200 method names appear across millions of
/// samples. Without interning, each <c>FullMethodName</c> allocation is a separate
/// heap object. With interning, the hot-path allocates nothing after warm-up.
///
/// Usage:
/// <code>
///   var interner = new FrameInterner();
///   // inside the event loop:
///   string frame = interner.Intern(ev.CodeAddress?.FullMethodName ?? "");
/// </code>
///
/// Not thread-safe — create one instance per analyzer invocation.
/// </summary>
public sealed class FrameInterner
{
    private readonly Dictionary<string, string> _cache;

    /// <param name="initialCapacity">
    /// Expected number of unique frame strings. 512 covers most real-world traces.
    /// Large CPU traces with many distinct methods may benefit from 2048+.
    /// </param>
    public FrameInterner(int initialCapacity = 512)
    {
        _cache = new Dictionary<string, string>(initialCapacity, StringComparer.Ordinal);
    }

    /// <summary>
    /// Returns a canonical string instance for <paramref name="frame"/>.
    /// The first call with a given value stores it; subsequent calls return the stored instance.
    /// </summary>
    public string Intern(string frame)
    {
        if (frame.Length == 0) return string.Empty;
        if (_cache.TryGetValue(frame, out string? existing))
            return existing;
        _cache[frame] = frame;
        return frame;
    }

    /// <summary>
    /// Returns a canonical instance for <paramref name="frame"/>,
    /// truncating to <paramref name="maxLength"/> before interning.
    /// Avoids storing extremely long generated-method names verbatim.
    /// </summary>
    public string InternTruncated(string frame, int maxLength = 200)
    {
        if (frame.Length > maxLength)
            frame = frame[..maxLength] + "…";
        return Intern(frame);
    }

    /// <summary>Number of unique strings currently cached.</summary>
    public int UniqueCount => _cache.Count;

    /// <summary>Clears all cached strings (allows GC of the interned strings).</summary>
    public void Clear() => _cache.Clear();
}
