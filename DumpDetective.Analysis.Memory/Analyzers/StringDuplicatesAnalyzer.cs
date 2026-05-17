using DumpDetective.Core.Models.CommandData;
using DumpDetective.Core.Runtime;
using DumpDetective.Core.Utilities;
using Microsoft.Diagnostics.Runtime;

namespace DumpDetective.Analysis.Memory.Analyzers;

/// <summary>
/// Finds duplicate string values on the managed heap, reporting wasted memory
/// from interning opportunities.
/// Optionally detects duplicate byte[] content (opt-in via <c>includeArrays</c>)
/// and string encoding waste (opt-in via <c>includeEncoding</c>).
/// </summary>
public sealed class StringDuplicatesAnalyzer
{
    public StringDuplicatesData Analyze(DumpContext ctx,
        bool includeArrays = false, bool includeEncoding = false)
    {
        IReadOnlyList<StringDupGroup> groups;
        long totalStrings, totalSize;

        // Fast path — reuse HeapSnapshot when available
        if (ctx.Snapshot is { } snap)
        {
            var groupList = new List<StringDupGroup>(snap.StringGroupsCount);
            foreach (var kv in snap.StreamStringGroups())
                groupList.Add(new StringDupGroup(kv.Key, kv.Value.Count, kv.Value.TotalSize));
            groups = groupList;
            totalStrings = snap.TotalStringCount;
            totalSize    = snap.TotalStringSize;
            snap.ReleaseStringGroups();
        }
        else
        {
            // Slow path — own heap walk
            var dict = new Dictionary<string, StringGroupStats>(StringComparer.Ordinal);
            totalStrings = 0; totalSize = 0;

            CommandBase.RunStatus("Scanning strings...", update =>
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                foreach (var obj in ctx.Heap.EnumerateObjects())
                {
                    if (!obj.IsValid || obj.Type?.Name != "System.String") continue;
                    totalStrings++;
                    if ((totalStrings & 0x3FF) == 0 && sw.ElapsedMilliseconds >= 200)
                    {
                        update($"Scanning strings \u2014 {totalStrings:N0} strings  \u2022  {dict.Count:N0} unique...");
                        sw.Restart();
                    }
                    long sz   = (long)obj.Size;
                    totalSize += sz;
                    var val   = obj.AsString(maxLength: 512) ?? string.Empty;
                    if (dict.TryGetValue(val, out var e)) dict[val] = new StringGroupStats(e.Count + 1, e.TotalSize + sz);
                    else                                  dict[val] = new StringGroupStats(1, sz);
                }
            });

            var gList = new List<StringDupGroup>(dict.Count);
            foreach (var kv in dict) gList.Add(new StringDupGroup(kv.Key, kv.Value.Count, kv.Value.TotalSize));
            groups = gList;
        }

        // Optional byte[] duplicate detection
        IReadOnlyList<ByteArrayDupGroup>? byteGroups = includeArrays
            ? DetectByteArrayDuplicates(ctx) : null;

        // Optional encoding waste analysis on top groups
        IReadOnlyList<StringEncodingGroup>? encodingGroups = includeEncoding
            ? AnalyzeEncoding(groups) : null;

        return new StringDuplicatesData(groups, totalStrings, totalSize, byteGroups, encodingGroups);
    }

    private static IReadOnlyList<ByteArrayDupGroup> DetectByteArrayDuplicates(DumpContext ctx)
    {
        const int MaxArrayLen = 4096;
        const int MaxSamples = 200_000;

        // Collect all small byte[] arrays; group by hash of contents
        var byHash = new Dictionary<ulong, (int Count, long TotalSize, int Len, byte[] Sample)>();
        int sampled = 0;

        CommandBase.RunStatus("Scanning byte[] duplicates...", update =>
        {
            long scanned = 0;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            foreach (var obj in ctx.Heap.EnumerateObjects())
            {
                if (!obj.IsValid || obj.Type?.Name != "System.Byte[]") continue;
                scanned++;
                if ((scanned & 0x3FF) == 0 && sw.ElapsedMilliseconds >= 200)
                {
                    update($"Scanning byte[] \u2014 {scanned:N0} arrays  \u2022  {byHash.Count:N0} unique...");
                    sw.Restart();
                }
                int len = 0;
                try { len = obj.Type!.StaticSize > 0 ? (int)((obj.Size - (ulong)obj.Type.StaticSize) / (ulong)obj.Type.ComponentSize) : 0; }
                catch { continue; }
                if (len <= 0 || len > MaxArrayLen) continue;
                if (++sampled > MaxSamples) break;

                byte[]? bytes = null;
                try
                {
                    bytes = new byte[len];
                    int bytesRead = ctx.Runtime.DataTarget.DataReader.Read(obj.Address + (ulong)obj.Type!.StaticSize, bytes);
                    if (bytesRead < len) continue;
                }
                catch { continue; }

                // FNV-1a hash
                ulong hash = 14695981039346656037UL;
                foreach (var b in bytes)
                {
                    hash ^= b;
                    hash *= 1099511628211UL;
                }
                // Composite key: hash | (ulong)len<<48 to reduce collisions
                ulong key = hash ^ ((ulong)(uint)len << 32);
                if (byHash.TryGetValue(key, out var entry))
                    byHash[key] = (entry.Count + 1, entry.TotalSize + (long)obj.Size, len, entry.Sample);
                else
                    byHash[key] = (1, (long)obj.Size, len, bytes);
            }
        });

        return byHash.Values
            .Where(v => v.Count > 1)
            .OrderByDescending(v => v.TotalSize)
            .Take(50)
            .Select(v =>
            {
                var preview = BitConverter.ToString(v.Sample[..Math.Min(16, v.Sample.Length)]).Replace("-", " ");
                return new ByteArrayDupGroup(v.Count, v.TotalSize, v.Len, preview);
            })
            .ToList();
    }

    private static IReadOnlyList<StringEncodingGroup> AnalyzeEncoding(IReadOnlyList<StringDupGroup> groups)
    {
        // Analyze top 100 groups by wasted size for encoding characteristics
        var result = new List<StringEncodingGroup>();
        foreach (var g in groups.OrderByDescending(g => g.TotalSize).Take(100))
        {
            if (g.Value.Length == 0) continue;
            bool allAscii  = true;
            bool allLatin1 = true;
            foreach (char c in g.Value)
            {
                if (c > 0xFF) { allAscii = false; allLatin1 = false; break; }
                if (c > 0x7F) allAscii = false;
            }

            // .NET strings are UTF-16 (2 bytes/char). If all ASCII, UTF-8 would use 1 byte/char.
            // Waste = (g.Count * g.Value.Length * 2) - (g.Count * g.Value.Length * 1)
            long utf8WasteBytes = allAscii
                ? (long)g.Count * g.Value.Length   // save 1 byte/char
                : allLatin1
                    ? (long)g.Count * g.Value.Length / 2  // save ~0.5 bytes/char average
                    : 0;

            if (utf8WasteBytes > 0)
                result.Add(new StringEncodingGroup(g.Value.Length > 50 ? g.Value[..50] + "…" : g.Value,
                    g.Count, g.TotalSize, allAscii, allLatin1, utf8WasteBytes));
        }
        result.Sort((a, b) => b.Utf8WasteBytes.CompareTo(a.Utf8WasteBytes));
        return result;
    }
}
