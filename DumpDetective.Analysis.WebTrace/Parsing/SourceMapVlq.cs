namespace DumpDetective.Analysis.WebTrace.Parsing;

/// <summary>One decoded mapping segment: a generated (bundled) position plus its original source position.</summary>
public readonly record struct MappingSegment(int GeneratedColumn, int SourceIndex, int SourceLine, int SourceColumn);

/// <summary>
/// A decoded source map for one bundled file — the "mappings" VLQ string turned into,
/// per generated line, a column-sorted list of segments so a (line, column) from the V8
/// CPU profiler can be resolved back to (original file, original line, original column)
/// with a binary search. Decoded once per unique bundle URL and reused for every hotspot
/// that resolves against it — see <see cref="WebTraceCache.Cache"/> notes on why this
/// (VLQ decoding) is exactly the kind of work that belongs in the cache, not re-run per query.
/// </summary>
public sealed class DecodedSourceMap
{
    public required string[]                     Sources { get; init; }
    public required List<MappingSegment>[]        Lines   { get; init; }

    public (string File, int Line, int Column)? Resolve(int line, int column)
    {
        if (line < 0 || line >= Lines.Length) return null;
        var segs = Lines[line];
        if (segs.Count == 0) return null;

        int lo = 0, hi = segs.Count - 1, ans = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (segs[mid].GeneratedColumn <= column) { ans = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        if (ans < 0) return null;

        var seg = segs[ans];
        if (seg.SourceIndex < 0 || seg.SourceIndex >= Sources.Length) return null;
        return (Sources[seg.SourceIndex], seg.SourceLine, seg.SourceColumn);
    }
}

/// <summary>Standard base64-VLQ decoder for the source-map "mappings" field (source-map spec v3).</summary>
public static class SourceMapVlq
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
    private static readonly sbyte[] CharToDigit = BuildLookup();

    private static sbyte[] BuildLookup()
    {
        var t = new sbyte[128];
        Array.Fill(t, (sbyte)-1);
        for (int i = 0; i < Alphabet.Length; i++) t[Alphabet[i]] = (sbyte)i;
        return t;
    }

    /// <summary>Decodes a "mappings" string into per-generated-line, column-sorted segment lists.</summary>
    public static DecodedSourceMap Decode(string mappings, string[] sources)
    {
        var lines = new List<List<MappingSegment>>();
        var current = new List<MappingSegment>();

        int genColumn = 0, sourceIndex = 0, sourceLine = 0, sourceColumn = 0;
        int i = 0, n = mappings.Length;
        Span<int> vals = stackalloc int[5];

        while (i < n)
        {
            char c = mappings[i];
            if (c == ';')
            {
                current.Sort(static (a, b) => a.GeneratedColumn.CompareTo(b.GeneratedColumn));
                lines.Add(current);
                current = [];
                genColumn = 0;
                i++;
                continue;
            }
            if (c == ',') { i++; continue; }

            int count = 0;
            while (count < 5 && i < n && mappings[i] != ',' && mappings[i] != ';')
                vals[count++] = DecodeOne(mappings, ref i);

            if (count >= 4)
            {
                genColumn    += vals[0];
                sourceIndex  += vals[1];
                sourceLine   += vals[2];
                sourceColumn += vals[3];
                current.Add(new MappingSegment(genColumn, sourceIndex, sourceLine, sourceColumn));
            }
            else if (count == 1)
            {
                genColumn += vals[0]; // generated-only segment, no source mapping
            }
        }
        current.Sort(static (a, b) => a.GeneratedColumn.CompareTo(b.GeneratedColumn));
        lines.Add(current);

        return new DecodedSourceMap { Sources = sources, Lines = [.. lines] };
    }

    private static int DecodeOne(string s, ref int i)
    {
        int result = 0, shift = 0;
        bool more;
        do
        {
            sbyte digit = s[i] < 128 ? CharToDigit[s[i]] : (sbyte)-1;
            i++;
            if (digit < 0) return 0; // malformed — bail out of this segment rather than throw
            more = (digit & 32) != 0;
            result += (digit & 31) << shift;
            shift += 5;
        } while (more);

        bool negative = (result & 1) == 1;
        result >>= 1;
        return negative ? -result : result;
    }
}
