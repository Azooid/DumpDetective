using DumpDetective.Collectors;
using DumpDetective.Core;
using DumpDetective.Helpers;
using DumpDetective.Models;
using DumpDetective.Output;
using Spectre.Console;

namespace DumpDetective.Commands;

internal static class TrendAnalysisCommand
{
    private const string Help = """
        Usage: DumpDetective trend-analysis <dump1> <dump2> [<dump3> ...] [options]
               DumpDetective trend-analysis <dump-directory> [options]
               DumpDetective trend-analysis --list <paths.txt> [options]

        Analyzes multiple dumps and reports memory/leak trends over time.
        Dumps are sorted by file modification time to establish the timeline.

        Options:
          --list <file>          Read dump paths from a text file (one path per line)
                                 Entries can be dump files or directories.
          --full                 Run full collection per dump (includes event leaks,
                                 string duplicates — slower but more data)
          --ignore-event <type>  Exclude publisher types whose name contains <type>
                                 from the Event Leak Analysis table. Repeatable.
                                 Example: --ignore-event SNINativeMethodWrapper
          -o, --output <f>       Write report to file (.md / .html / .txt)
          -h, --help             Show this help

        Example:
          DumpDetective trend-analysis d1.dmp d2.dmp d3.dmp --output trends.html
          DumpDetective trend-analysis D:\\dumps --output trends.html
          DumpDetective trend-analysis --list dumps.txt --full --output report.md
          DumpDetective trend-analysis d1.dmp d2.dmp --full \\
              --ignore-event SNINativeMethodWrapper --ignore-event System.Data
        """;

    public static int Run(string[] args)
    {
        if (args.Length == 0 || args.Any(a => a is "--help" or "-h"))
        {
            Console.WriteLine(Help);
            return 0;
        }

        var    inputs      = new List<string>();
        bool   full        = false;
        string? output     = null;
        string? listFile   = null;
        var ignoreEvents   = new List<string>();

        for (int i = 0; i < args.Length; i++)
        {
            if ((args[i] is "--output" or "-o") && i + 1 < args.Length)
                output = args[++i];
            else if (args[i] == "--full")
                full = true;
            else if (args[i] == "--list" && i + 1 < args.Length)
                listFile = args[++i];
            else if (args[i] == "--ignore-event" && i + 1 < args.Length)
                ignoreEvents.Add(args[++i]);
            else if (!args[i].StartsWith('-'))
                inputs.Add(args[i]);
        }

        // Load from list file
        if (listFile is not null)
        {
            if (!File.Exists(listFile))
            {
                Console.Error.WriteLine($"Error: list file not found: {listFile}");
                return 1;
            }
            inputs.AddRange(
                File.ReadAllLines(listFile)
                    .Select(l => l.Trim())
                    .Where(l => l.Length > 0 && !l.StartsWith('#')));
        }

        var dumpPaths = ExpandDumpInputs(inputs, out var missingPaths, out var invalidDumpFiles);

        if (missingPaths.Count > 0)
        {
            foreach (var p in missingPaths)
                Console.Error.WriteLine($"Error: file or directory not found: {p}");
            return 1;
        }

        if (invalidDumpFiles.Count > 0)
        {
            foreach (var p in invalidDumpFiles)
                Console.Error.WriteLine($"Error: not a dump file (.dmp/.mdmp): {p}");
            return 1;
        }

        if (dumpPaths.Count < 2)
        {
            Console.Error.WriteLine("Error: at least 2 dump files are required for trend analysis (after directory expansion).");
            Console.Error.WriteLine(Help);
            return 1;
        }

        // Sort by file modification time to establish timeline
        dumpPaths = [.. dumpPaths.OrderBy(File.GetLastWriteTime)];

        AnsiConsole.MarkupLine($"[bold]Trend analysis:[/] {dumpPaths.Count} dump(s)  [[{(full ? "full" : "lightweight")} mode]]");
        AnsiConsole.WriteLine();

        var snapshots = new List<DumpDetective.Models.DumpSnapshot>();

        for (int i = 0; i < dumpPaths.Count; i++)
        {
            var label    = $"D{i + 1}";
            var path     = dumpPaths[i];
            var dispName = ShortName(path);
            DumpSnapshot? snap = null;

            AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .SpinnerStyle(Style.Parse("blue"))
                .Start($"[bold]{label}[/]  {Markup.Escape(dispName)}  opening...", ctx =>
                {
                    void Upd(string msg) =>
                        ctx.Status($"[bold]{label}[/]  [dim]{Markup.Escape(dispName)}[/]  {Markup.Escape(msg)}");
                    snap = full
                        ? DumpCollector.CollectFull(path, Upd)
                        : DumpCollector.CollectLightweight(path, Upd);
                });

            snapshots.Add(snap!);
            var sc = snap!.HealthScore >= 70 ? "green" : snap.HealthScore >= 40 ? "yellow" : "red";
            AnsiConsole.MarkupLine(
                $"  [green]✓[/]  [bold]{label}[/]  [dim]{Markup.Escape(dispName)}[/]  " +
                $"[{sc}]{snap.HealthScore}/100  {ScoreLabel(snap.HealthScore)}[/]");

            // Non-blocking sweep between dumps — releases typeStats, stringValues,
            // delFieldsCache etc. while the next dump file is being opened (I/O time).
            // No compaction: avoids the multi-second STW pause from moving objects.
            GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(1, GCCollectionMode.Forced, blocking: true, compacting: true);
        }

        AnsiConsole.WriteLine();

        using var sink = IRenderSink.Create(output);
        RenderTrend(snapshots, sink, ignoreEvents);

        if (sink.IsFile && sink.FilePath is not null)
            AnsiConsole.MarkupLine($"\n[dim]→ Written to:[/] {Markup.Escape(sink.FilePath)}");
        return 0;
    }

    private static List<string> ExpandDumpInputs(
        IEnumerable<string> inputs,
        out List<string> missingPaths,
        out List<string> invalidDumpFiles)
    {
        var dumps = new List<string>();
        missingPaths = [];
        invalidDumpFiles = [];

        foreach (var input in inputs)
        {
            if (Directory.Exists(input))
            {
                dumps.AddRange(Directory.EnumerateFiles(input, "*.dmp", SearchOption.AllDirectories));
                dumps.AddRange(Directory.EnumerateFiles(input, "*.mdmp", SearchOption.AllDirectories));
                continue;
            }

            if (File.Exists(input))
            {
                if (IsDumpFile(input))
                    dumps.Add(input);
                else
                    invalidDumpFiles.Add(input);
                continue;
            }

            missingPaths.Add(input);
        }

        return [.. dumps.Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    private static bool IsDumpFile(string path) =>
        path.EndsWith(".dmp", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".mdmp", StringComparison.OrdinalIgnoreCase);

    // ── Renderer ──────────────────────────────────────────────────────────────

    internal static void RenderTrend(List<DumpSnapshot> snaps, IRenderSink sink,
                                     IReadOnlyList<string>? ignoreEventTypes = null)
    {
        var s0    = snaps[0];
        var sN    = snaps[^1];
        bool full = snaps.Any(s => s.IsFullMode);

        // Assign D1 … Dn labels used throughout the report
        var labels = snaps.Select((_, i) => $"D{i + 1}").ToArray();

        sink.Header(
            "Dump Detective — Trend Analysis Report",
            $"{snaps.Count} dumps  |  {s0.FileTime:yyyy-MM-dd HH:mm} → {sN.FileTime:yyyy-MM-dd HH:mm}  |  {(full ? "Full" : "Lightweight")} mode");

        // ── 1. Dump Timeline ──────────────────────────────────────────────────
        sink.Section("1. Dump Timeline");
        sink.Table(
            ["Dump", "File", "File Size", "Time", "Threads (Total / Alive)", "Health"],
            snaps.Select((s, i) => new[]
            {
                labels[i],
                Path.GetFileName(s.DumpPath),
                DumpHelpers.FormatSize(s.DumpFileSizeBytes),
                s.FileTime.ToString("HH:mm"),
                $"{s.ThreadCount} / {s.AliveThreadCount}",
                $"{s.HealthScore}/100  {ScoreLabel(s.HealthScore)}",
            }).ToList());

        // ── 2. Overall Growth Summary ─────────────────────────────────────────
        sink.Section("2. Overall Growth Summary");
        var growthCols = new[] { "Metric" }.Concat(labels).Append("Trend").ToArray();
        var growthRows = new List<string[]>();

        void AddRow(string label, Func<DumpSnapshot, double> sel,
                    Func<DumpSnapshot, string> fmt, bool higherIsBad = true)
        {
            var vals = snaps.Select(s => fmt(s)).ToArray();
            growthRows.Add([label, .. vals, Trend(sel(s0), sel(sN), higherIsBad)]);
        }

        long SohBytes(DumpSnapshot s) =>
            s.TotalHeapBytes - s.LohBytes - s.PohBytes - s.FrozenBytes;

        AddRow("Total Objects",      s => s.TotalObjectCount,  s => s.TotalObjectCount.ToString("N0"));
        AddRow("Heap — SOH",         s => SohBytes(s),         s => DumpHelpers.FormatSize(SohBytes(s)));
        AddRow("Heap — LOH",         s => s.LohBytes,          s => DumpHelpers.FormatSize(s.LohBytes));
        AddRow("Heap — Total",       s => s.TotalHeapBytes,    s => DumpHelpers.FormatSize(s.TotalHeapBytes));
        AddRow("LOH Object Count",   s => s.LohObjectCount,    s => s.LohObjectCount.ToString("N0"));
        AddRow("Finalize Queue",     s => s.FinalizerQueueDepth, s => s.FinalizerQueueDepth.ToString("N0"));
        AddRow("Unique Strings",     s => s.UniqueStringCount, s => s.UniqueStringCount.ToString("N0"));
        AddRow("Total String Mem",   s => s.StringTotalBytes,  s => DumpHelpers.FormatSize(s.StringTotalBytes));
        AddRow("Event Instances",    s => s.EventSubscriberTotal, s => s.EventSubscriberTotal.ToString("N0"));
        AddRow("Event Types",        s => s.EventLeakFieldCount,  s => s.EventLeakFieldCount.ToString("N0"));
        sink.Table(growthCols, growthRows);

        // ── 4. Event Leak Analysis ────────────────────────────────────────────
        sink.Section("4. Event Leak Analysis");
        if (!full)
        {
            sink.Text("Not collected — re-run with --full to include event leak detail.");
        }
        else
        {
            // Summary row per dump
            sink.Table(
                ["Dump", "Total Instances", "Distinct Event Types", "Max on Single Field"],
                snaps.Select((s, i) => new[]
                {
                    labels[i],
                    s.EventSubscriberTotal.ToString("N0"),
                    s.EventLeakFieldCount.ToString("N0"),
                    s.EventLeakMaxOnField.ToString("N0"),
                }).ToList());

            // Top event fields — build a union across all dumps so each appears once
            var allFields = snaps
                .SelectMany(s => s.TopEventLeaks)
                .Select(e => (e.PublisherType, e.FieldName))
                .Distinct()
                .ToList();

            if (ignoreEventTypes is { Count: > 0 })
            {
                var before = allFields.Count;
                allFields = allFields
                    .Where(f => !ignoreEventTypes.Any(ig =>
                        f.PublisherType.Contains(ig, StringComparison.OrdinalIgnoreCase)))
                    .ToList();
                int removed = before - allFields.Count;
                if (removed > 0)
                    sink.Text($"Filtered out {removed} event field(s) matching: "
                        + string.Join(", ", ignoreEventTypes));
            }

            if (allFields.Count > 0)
            {
                var eventCols = new[] { "Event Type / Field" }.Concat(labels).ToArray();
                var eventRows = allFields
                    .Select(key =>
                    {
                        var perDump = snaps.Select(s =>
                        {
                            var stat = s.TopEventLeaks
                                .FirstOrDefault(e => e.PublisherType == key.PublisherType
                                                  && e.FieldName     == key.FieldName);
                            return stat is null ? "—" : stat.Subscribers.ToString("N0");
                        }).ToArray();
                        return (string[])[$"{key.PublisherType}.{key.FieldName}", .. perDump];
                    })
                    .OrderByDescending(r =>
                    {
                        int max = 0;
                        for (int i = 1; i < r.Length; i++)
                            if (int.TryParse(r[i].Replace(",", ""), out int v) && v > max) max = v;
                        return max;
                    })
                    .Take(15)
                    .ToList();
                sink.Table(eventCols, eventRows, "Top event fields across all dumps");
            }
        }

        // ── 5. Finalize Queue Detail ──────────────────────────────────────────
        sink.Section("5. Finalize Queue Detail");
        {
            // Header: Dump | Total
            sink.Table(
                ["Dump", "Total in Queue"],
                snaps.Select((s, i) => new[] { labels[i], s.FinalizerQueueDepth.ToString("N0") }).ToList());

            // Cross-dump breakdown: union of all types seen
            var allFinTypes = snaps
                .SelectMany(s => s.TopFinalizerTypes.Select(t => t.Type))
                .Distinct()
                .ToList();

            if (allFinTypes.Count > 0)
            {
                var finCols = new[] { "Type" }.Concat(labels).ToArray();
                var finRows = allFinTypes
                    .Select(typeName =>
                    {
                        var counts = snaps.Select(s =>
                        {
                            var t = s.TopFinalizerTypes.FirstOrDefault(x => x.Type == typeName);
                            return t == default ? "—" : t.Count.ToString("N0");
                        }).ToArray();
                        return (string[])[typeName, .. counts];
                    })
                    .OrderByDescending(r =>
                    {
                        int max = 0;
                        for (int i = 1; i < r.Length; i++)
                            if (int.TryParse(r[i].Replace(",", ""), out int v) && v > max) max = v;
                        return max;
                    })
                    .Take(15)
                    .ToList();
                sink.Table(finCols, finRows, "Top types by peak count");
            }
        }

        // ── 6. Highly Referenced Objects ──────────────────────────────────────
        sink.Section("6. Highly Referenced Objects");
        sink.Text("Reference graph analysis requires a live debugging session or WinDbg/SOS.");
        sink.Text("Use: !gcroot <address>  or  DumpDetective gc-roots <dump> to inspect specific objects.");
        sink.Text("Top types by instance count (proxy for high-fanout objects):");
        {
            var allTopTypes = snaps
                .SelectMany(s => s.TopTypes.Select(t => t.Name))
                .Distinct()
                .ToList();

            var typeCols = new[] { "Type" }.Concat(labels.Select(l => $"{l} Count")).ToArray();
            var typeRows = allTopTypes
                .Select(name =>
                {
                    var counts = snaps.Select(s =>
                    {
                        var t = s.TopTypes.FirstOrDefault(x => x.Name == name);
                        return t is null ? "—" : t.Count.ToString("N0");
                    }).ToArray();
                    return (string[])[name, .. counts];
                })
                .OrderByDescending(r =>
                {
                    long max = 0;
                    for (int i = 1; i < r.Length; i++)
                        if (long.TryParse(r[i].Replace(",", ""), out long v) && v > max) max = v;
                    return max;
                })
                .Take(15)
                .ToList();

            if (typeRows.Count > 0)
                sink.Table(typeCols, typeRows, "Top 15 types by peak instance count across dumps");
        }

        // ── 7. Rooted Objects Analysis ────────────────────────────────────────
        sink.Section("7. Rooted Objects Analysis");
        {
            // Summary of handle-kind totals
            sink.Table(
                ["Dump", "Strong", "Pinned", "Weak", "Total"],
                snaps.Select((s, i) => new[]
                {
                    labels[i],
                    s.StrongHandleCount.ToString("N0"),
                    s.PinnedHandleCount.ToString("N0"),
                    s.WeakHandleCount.ToString("N0"),
                    s.TotalHandleCount.ToString("N0"),
                }).ToList(), "Handle counts per dump");

            // Per-(kind, type) cross-dump breakdown
            var allRootKeys = snaps
                .SelectMany(s => s.TopRootedTypes.Select(r => (r.HandleKind, r.TypeName)))
                .Distinct()
                .ToList();

            if (allRootKeys.Count > 0)
            {
                var rootCols = new[] { "Root Type (Handle Kind)" }.Concat(labels).ToArray();
                var rootRows = allRootKeys
                    .Select(key =>
                    {
                        var counts = snaps.Select(s =>
                        {
                            var r = s.TopRootedTypes
                                .FirstOrDefault(x => x.HandleKind == key.HandleKind
                                                  && x.TypeName   == key.TypeName);
                            return r is null ? "—"
                                : $"{r.Count:N0} / {DumpHelpers.FormatSize(r.TotalBytes)}";
                        }).ToArray();
                        return (string[])[$"{key.TypeName} ({key.HandleKind})", .. counts];
                    })
                    .OrderByDescending(r =>
                    {
                        int max = 0;
                        for (int i = 1; i < r.Length; i++)
                        {
                            var cell = r[i];
                            var slash = cell.IndexOf('/');
                            var numPart = slash >= 0 ? cell[..slash].Trim() : cell;
                            if (int.TryParse(numPart.Replace(",", ""), out int v) && v > max) max = v;
                        }
                        return max;
                    })
                    .Take(15)
                    .ToList();
                sink.Table(rootCols, rootRows, "Top rooted types by peak count  (count / total size)");
            }
        }

        // ── 8. Duplicate String Analysis ─────────────────────────────────────
        sink.Section("8. Duplicate String Analysis");
        if (!full)
        {
            sink.Text("Not collected — re-run with --full to include string duplicate detail.");
        }
        else
        {
            // Summary
            sink.Table(
                ["Dump", "Unique Strings", "Duplicate Groups", "Wasted Memory", "Total String Mem"],
                snaps.Select((s, i) => new[]
                {
                    labels[i],
                    s.UniqueStringCount.ToString("N0"),
                    s.StringDuplicateGroups.ToString("N0"),
                    DumpHelpers.FormatSize(s.StringWastedBytes),
                    DumpHelpers.FormatSize(s.StringTotalBytes),
                }).ToList());

            // Top duplicated strings across all dumps — union by value
            var allStringVals = snaps
                .SelectMany(s => s.TopStringDuplicates.Select(d => d.Value))
                .Distinct()
                .ToList();

            if (allStringVals.Count > 0)
            {
                var strCols = new[] { "String Value" }
                    .Concat(labels.Select(l => $"{l} Count"))
                    .Concat(labels.Select(l => $"{l} Wasted"))
                    .ToArray();

                var strRows = allStringVals
                    .Select(val =>
                    {
                        var counts  = snaps.Select(s =>
                        {
                            var d = s.TopStringDuplicates.FirstOrDefault(x => x.Value == val);
                            return d is null ? "—" : d.Count.ToString("N0");
                        }).ToArray();
                        var wasted  = snaps.Select(s =>
                        {
                            var d = s.TopStringDuplicates.FirstOrDefault(x => x.Value == val);
                            return d is null ? "—" : DumpHelpers.FormatSize(d.WastedBytes);
                        }).ToArray();
                        string display = val.Length > 60 ? val[..60] + "…" : val;
                        display = display.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
                        return (string[])[$"\"{display}\"", .. counts, .. wasted];
                    })
                    .OrderByDescending(r =>
                    {
                        // Sort by max count across dumps
                        int max = 0;
                        for (int i = 1; i <= snaps.Count; i++)
                            if (int.TryParse(r[i].Replace(",", ""), out int v) && v > max) max = v;
                        return max;
                    })
                    .Take(15)
                    .ToList();

                sink.Table(strCols, strRows, "Top duplicated strings across all dumps");
            }
        }
    }

    static string ScoreLabel(int s) => s >= 70 ? "HEALTHY" : s >= 40 ? "DEGRADED" : "CRITICAL";

    // Truncates to ≤42 chars so status lines never overflow and cause Spectre box rendering.
    static string ShortName(string path, int max = 42)
    {
        var name = Path.GetFileName(path);
        if (name.Length <= max) return name;
        var ext      = Path.GetExtension(name);
        int keepStem = Math.Max(1, max - ext.Length - 1);
        return name[..keepStem] + "\u2026" + ext;   // e.g. "very-long-dump-filena\u2026.dmp"
    }

    static string Trend(double first, double last, bool higherIsBad)
    {
        if (first <= 0) return "~";
        double pct   = (last - first) / first * 100;
        string arrow = pct > 50 ? "↑↑" : pct > 10 ? "↑" : pct < -10 ? "↓" : "~";
        if (higherIsBad && pct > 50) arrow += " ↑↑";
        else if (higherIsBad && pct > 10) arrow += " ↑";
        return arrow;
    }
}
