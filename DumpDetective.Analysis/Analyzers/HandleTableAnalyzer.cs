using DumpDetective.Core.Models.CommandData;
using DumpDetective.Core.Runtime;
using DumpDetective.Core.Utilities;
using Microsoft.Diagnostics.Runtime;

namespace DumpDetective.Analysis.Analyzers;

public sealed class HandleTableAnalyzer
{
    public HandleTableData Analyze(DumpContext ctx, string? filter = null)
    {
        var byKind = new Dictionary<string, (int Count, long TotalSize, Dictionary<string, (int Count, long Size)> Types)>(StringComparer.Ordinal);
        int total  = 0;

        CommandBase.RunStatus("Scanning GC handles...", update =>
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            foreach (var h in ctx.Runtime.EnumerateHandles())
            {
                total++;
                if ((total & 0xFF) == 0 && sw.ElapsedMilliseconds >= 200)
                {
                    update($"Scanning GC handles — {total:N0} handles  •  {byKind.Count} kinds...");
                    sw.Restart();
                }
                var kind = h.HandleKind.ToString();
                if (filter is not null && !kind.Contains(filter, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!byKind.TryGetValue(kind, out var info))
                    info = (0, 0, new Dictionary<string, (int, long)>(StringComparer.Ordinal));

                info.Count++;
                if (h.Object != 0)
                {
                    try
                    {
                        var obj = ctx.Heap.GetObject(h.Object);
                        if (obj.IsValid)
                        {
                            long   size     = (long)obj.Size;
                            string typeName = obj.Type?.Name ?? "<unknown>";
                            info.TotalSize += size;

                            info.Types.TryGetValue(typeName, out var ts);
                            info.Types[typeName] = (ts.Count + 1, ts.Size + size);
                        }
                    }
                    catch { }
                }

                byKind[kind] = info;
            }
        });

        // Convert internal tuples to named records
        var result = new Dictionary<string, HandleKindInfo>(byKind.Count, StringComparer.Ordinal);
        foreach (var (kind, info) in byKind)
        {
            var types = new Dictionary<string, HandleTypeStats>(info.Types.Count, StringComparer.Ordinal);
            foreach (var (typeName, ts) in info.Types)
                types[typeName] = new HandleTypeStats(ts.Count, ts.Size);
            result[kind] = new HandleKindInfo(info.Count, info.TotalSize, types);
        }

        return new HandleTableData(result, total);
    }
}
