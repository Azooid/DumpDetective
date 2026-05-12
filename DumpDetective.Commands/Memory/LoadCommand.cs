using System.Diagnostics;
using System.Runtime;
using DumpDetective.Analysis.Memory;
using DumpDetective.Analysis.Memory.Analyzers;
using DumpDetective.Analysis.Memory.Consumers;
using DumpDetective.Commands.Trace;
using DumpDetective.Core.Interfaces;
using DumpDetective.Core.Models.CommandData;
using DumpDetective.Core.Runtime;
using DumpDetective.Core.Utilities;
using Microsoft.Diagnostics.Runtime;
using Spectre.Console;

namespace DumpDetective.Commands.Memory;

/// <summary>
/// Pre-builds all analysis caches for a dump so that subsequent <c>analyze --full</c>
/// runs complete as fast as possible.
///
/// Caches built (in order):
///   1. Heap walk (HeapWalker)     → stringGroups.bin, typeStats (in-memory)
///   2. Heap fragmentation scan    → fragmentation.bin
///   3. BFS index build (3 passes) → .ddcache\&lt;name&gt;\&lt;name&gt;.bfs.idx
///   4. Parent map (from BFS)      → .ddcache\&lt;name&gt;\&lt;name&gt;.parent.map
///   5. HotAddrTypes (from BFS)    → hot-addr-types.bin
///   6. GC roots enumeration       → gc-roots.bin
///   7. Static field scan          → static-roots.bin
///   8. Finalizer queue scan       → finalizer-queue.bin
///   9. Event analysis scan        → event-analysis.bin
///
/// Already-valid caches are skipped.  Use --force to rebuild all.
/// </summary>
public sealed class LoadCommand : ICommand
{
    public string Name               => "load";
    public string Description        => "Pre-build all analysis caches for a dump file.";
    public bool   IncludeInFullAnalyze => false;

    private const string Help = """
        Usage: DumpDetective load <dump-file-or-directory> [options]

        Pre-builds all analysis caches so subsequent 'analyze --full' runs are fast.
        When a directory is given, every .dmp/.mdmp and .etl file in it is processed in order.
        ETL trace files (.etl) are converted to .etlx alongside the original file.

        Caches written to .ddcache\<dump-name>\ alongside the dump:
          stringGroups.bin     — string-duplicates data
          fragmentation.bin    — heap fragmentation segments and distribution
          gc-roots.bin         — all GC root addresses and types
          static-roots.bin     — statically-rooted object addresses
          hot-addr-types.bin   — referencing-type breakdown for top-30 objects
          finalizer-queue.bin  — finalizer queue per-type stats and thread info

        Also written alongside the dump:
          <dump>.bfs.idx       — compressed forward-reference BFS graph
          <dump>.parent.map    — child-to-parent address map (1.2-1.3 GB typical)

        Use 'DumpDetective close <dump-file>' to delete all of the above.

        Options:
          --force, -f    Rebuild all caches even if valid ones already exist
          -h, --help     Show this help
        """;

    public int Run(string[] args)
    {
        if (CommandBase.TryHelp(args, Help)) return 0;

        var a = CliArgs.Parse(args);
        string? target = a.DumpPath;
        bool    force  = a.HasFlag("force") || a.HasFlag("f");

        if (target is null) { AnsiConsole.MarkupLine("[bold red]✗[/] dump file or directory path required."); return 1; }

        target = Path.GetFullPath(target);

        // ── Directory mode: process every .dmp / .mdmp / .etl in the folder ──
        if (Directory.Exists(target))
        {
            var dumps = Directory.EnumerateFiles(target, "*.dmp",  SearchOption.TopDirectoryOnly)
                .Concat(Directory.EnumerateFiles(target, "*.mdmp", SearchOption.TopDirectoryOnly))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();

            // Exclude companion ETL files (*.kernel.etl, *.clrRundown.etl, etc.) —
            // TraceLog merges them automatically from the base file.
            var etls = Directory.EnumerateFiles(target, "*.etl", SearchOption.TopDirectoryOnly)
                .Where(f => EtlPathHelper.ResolveToBase(f) == f)
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (dumps.Count == 0 && etls.Count == 0)
            {
                AnsiConsole.MarkupLine($"[yellow]⚠[/] No .dmp, .mdmp, or .etl files found in: {Markup.Escape(target)}");
                return 0;
            }

            int exitCode = 0;

            if (dumps.Count > 0)
            {
                AnsiConsole.MarkupLine($"[bold]Found {dumps.Count} dump file(s) in[/] {Markup.Escape(target)}");
                AnsiConsole.WriteLine();

                for (int i = 0; i < dumps.Count; i++)
                {
                    AnsiConsole.MarkupLine($"[bold dim]── [[{i + 1}/{dumps.Count}]] {Markup.Escape(Path.GetFileName(dumps[i]))} ──[/]");
                    int result = RunSingle(dumps[i], force);
                    if (result != 0) exitCode = result;
                    AnsiConsole.WriteLine();
                }

                AnsiConsole.MarkupLine(exitCode == 0
                    ? $"[green]✓[/] All {dumps.Count} dump(s) cached."
                    : $"[yellow]⚠[/] Completed with errors ({dumps.Count} dump(s) processed).");
            }

            if (etls.Count > 0)
            {
                AnsiConsole.MarkupLine($"[bold]Found {etls.Count} ETL trace file(s) in[/] {Markup.Escape(target)}");
                AnsiConsole.WriteLine();

                for (int i = 0; i < etls.Count; i++)
                {
                    AnsiConsole.MarkupLine($"[bold dim]── [[{i + 1}/{etls.Count}]] {Markup.Escape(Path.GetFileName(etls[i]))} ──[/]");
                    int result = ConvertEtl(etls[i], force);
                    if (result != 0) exitCode = result;
                    AnsiConsole.WriteLine();
                }

                AnsiConsole.MarkupLine(exitCode == 0
                    ? $"[green]✓[/] All {etls.Count} ETL file(s) converted."
                    : $"[yellow]⚠[/] Completed with errors ({etls.Count} ETL file(s) processed).");
            }

            return exitCode;
        }

        // ── Single-file mode ──────────────────────────────────────────────────
        if (!File.Exists(target))
        {
            AnsiConsole.MarkupLine($"[bold red]✗[/] path not found: {Markup.Escape(target)}");
            return 1;
        }

        if (target.EndsWith(".etl", StringComparison.OrdinalIgnoreCase))
            return ConvertEtl(target, force);

        return RunSingle(target, force);
    }

    private static int RunSingle(string dumpPath, bool force)
    {
        var log  = new ProgressLogger();
        log.SectionHeader($"DumpDetective load  {AppInfo.Version}");
        log.Info($"Dump: {Path.GetFileName(dumpPath)}");

        // ── Pre-check all cache states before opening the dump ───────────────
        string cacheDir         = Path.Combine(Path.GetDirectoryName(dumpPath)!, ".ddcache", Path.GetFileNameWithoutExtension(dumpPath));
        string stringGroupsBin  = Path.Combine(cacheDir, "stringGroups.bin");
        string fragPath         = FragmentationCache.CachePath(dumpPath);
        string bfsCachePath     = BfsIndexCache.CachePath(dumpPath);
        string parentMapPath    = Path.Combine(cacheDir, Path.GetFileNameWithoutExtension(dumpPath) + ".parent.map");
        string hotTypesPath     = HotAddrTypesCache.CachePath(dumpPath);
        string gcRootCachePath  = GcRootsCache.CachePath(dumpPath);
        string staticCachePath  = StaticRootsCache.CachePath(dumpPath);
        string eventCachePath   = EventAnalysisCache.CachePath(dumpPath);

        log.InfoM($"Cache dir: {cacheDir}", indent: true);

        bool fragOk       = !force && FragmentationCache.IsValid(fragPath, dumpPath);
        bool bfsOk        = !force && BfsIndexCache.IsValid(bfsCachePath, dumpPath);
        bool parentMapOk  = !force && File.Exists(parentMapPath);
        bool hotTypesOk   = !force && HotAddrTypesCache.IsValid(hotTypesPath, dumpPath);
        bool gcRootsOk    = !force && GcRootsCache.IsValid(gcRootCachePath, dumpPath);
        bool staticOk     = !force && StaticRootsCache.IsValid(staticCachePath, dumpPath);
        string finQueuePath = FinalizerQueueCache.CachePath(dumpPath);
        bool finQueueOk   = !force && FinalizerQueueCache.IsValid(finQueuePath, dumpPath);
        bool eventOk      = !force && EventAnalysisCache.IsValid(eventCachePath, dumpPath);
        // CollectFull (heap walk) is needed when: parent map or hot types will be rebuilt,
        // or stringGroups.bin doesn't exist yet.
        bool needsReferrerMaps = !parentMapOk || !hotTypesOk;
        bool needsCollect      = needsReferrerMaps || !File.Exists(stringGroupsBin);

        if (!needsCollect && fragOk && bfsOk && gcRootsOk && staticOk && finQueueOk && eventOk)
        {
            log.CheckM("All caches are already up to date.");
            log.InfoM($"Run 'DumpDetective analyze {Markup.Escape(Path.GetFileName(dumpPath))} --full' to analyze.", indent: true);
            return 0;
        }

        var totalSw = Stopwatch.StartNew();
        try
        {
            log.Info("Loading dump file...", indent: true);
            using var ctx = DumpContext.Open(dumpPath);
            log.Success($"Dump loaded  |  CLR {ctx.ClrVersion ?? "unknown"}", indent: true);
            if (ctx.ArchWarning is not null)
                log.Warn(ctx.ArchWarning, indent: true);
            log.Blank();

            // ── Step 1: Heap walk (+ optional merged extras) ────────────────────
            // When fragmentation or BFS pass 1 also need building, add their consumers
            // to CollectFull so they run in the same single heap walk.
            log.SectionHeader("Building Caches");
            FragmentationConsumer?  fragConsumer  = null;
            BfsPass1Consumer?       bfsP1Consumer = null;
            FinalizerQueueConsumer? finQConsumer  = null;

            if (needsCollect)
            {
                if (!fragOk)     fragConsumer  = new FragmentationConsumer(ctx.Heap, ctx.Heap.Segments, ctx.Heap.FreeType);
                if (!bfsOk)      bfsP1Consumer = new BfsPass1Consumer();
                if (!finQueueOk) finQConsumer  = new FinalizerQueueConsumer();

                var extras = new List<IHeapObjectConsumer>();
                if (fragConsumer  is not null) extras.Add(fragConsumer);
                if (bfsP1Consumer is not null) extras.Add(bfsP1Consumer);

                var finalizableExtras = finQConsumer is not null
                    ? (IReadOnlyList<IFinalizableObjectConsumer>)[finQConsumer]
                    : null;

                var mergedNote = (extras.Count, finQConsumer is not null) switch
                {
                    (0, false) => "",
                    (0, true)  => " + finalizer-queue",
                    (1, false) => fragConsumer is not null ? " + fragmentation" : " + BFS-pass1",
                    (1, true)  => fragConsumer is not null ? " + fragmentation + finalizer-queue" : " + BFS-pass1 + finalizer-queue",
                    _          => " + fragmentation + BFS-pass1 + finalizer-queue",
                };
                log.Stage($"[1/9] Heap walk (typeStats + stringGroups{mergedNote})...");
                var sw1 = Stopwatch.StartNew();
                DumpCollector.CollectForLoad(ctx, extras, log.OnProgress, finalizableExtras);
                log.Check($"[1/9] Heap walk  ({sw1.Elapsed.TotalSeconds:F1}s)");
            }
            else
                log.CheckM("  [[1/9]] stringGroups.bin  [dim]already cached[/]");

            // ── Step 2: Heap fragmentation ───────────────────────────────────────
            if (fragOk)
                log.CheckM("  [[2/9]] heap-fragmentation  [dim]already cached[/]");
            else if (fragConsumer is not null)
            {
                // Already collected in step 1 — apply pinned counts and save cache.
                var fragData = HeapFragmentationAnalyzer.BuildFromConsumer(ctx.Runtime, ctx.Heap, fragConsumer);
                try { FragmentationCache.Save(fragPath, dumpPath, fragData); } catch { }
                log.Check("  [2/9] Heap fragmentation saved (merged with heap walk)");
            }
            else
                Step(2, 9, "Heap fragmentation scan", () =>
                    new HeapFragmentationAnalyzer().Analyze(ctx));

            // ── Step 3: BFS index ────────────────────────────────────────────────
            BfsIndexCache? bfsReady = null;

            if (bfsOk)
            {
                CommandBase.RunStatus("Loading BFS index...", update =>
                    bfsReady = BfsIndexCache.TryLoad(dumpPath, update));
                log.CheckM($"  [[3/9]] BFS index  [dim]already cached ({bfsReady?.NodeCount:N0} nodes)[/]");
            }
            else
            {
                BfsPass1State? p1 = null;
                BfsPass2State? p2 = null;
                var bfsSw = Stopwatch.StartNew();

                if (bfsP1Consumer is not null)
                {
                    // Pass 1 data collected in step 1 — extract without a heap walk.
                    // GetPass1State() converts the 8 per-bucket chunk arrays into a single
                    // contiguous IndexToAddr + Sizes + SortedIdxMap (1.73 GB total).
                    // Chunk arrays and sortedAddrs temp (~2 GB) become dead LOH; they will
                    // be collected by the naturally-triggered GC during BFS pass 2 when
                    // childCounts (346 MB) pushes past the committed-free-LOH budget.
                    // An explicit blocking GC here was tried and caused +175s regression:
                    // a 4 GB LOH GC on this machine takes 30–60s AND then the finalizer
                    // queue and BFS passes lose CPU/TLB warmth. Not worth it — the dead
                    // 2 GB stays as committed-free LOH pages (invisible to the OS page
                    // allocator) and does not cause physical page pressure.
                    p1 = bfsP1Consumer.GetPass1State();
                    bfsP1Consumer = null;

                    log.Check($"  BFS pass 1/3 merged — {p1.NodeCount:N0} nodes");
                }
                else
                {
                    CommandBase.RunStatus("  BFS pass 1/3 — enumerating objects...",
                        update => p1 = BfsIndexBuilder.BuildPass1(ctx.Heap, update));
                }

                CommandBase.RunStatus($"  BFS pass 2/3 — counting edges ({p1!.NodeCount:N0} nodes)...",
                    update => p2 = BfsIndexBuilder.BuildPass2(ctx.Heap, p1, update));
                CommandBase.RunStatus($"  BFS pass 3/3 — filling {p2!.TotalEdges:N0} edges...",
                    update => bfsReady = BfsIndexBuilder.BuildPass3(ctx.Heap, p2, update));
                CommandBase.RunStatus("  Saving BFS index...",
                    update => bfsReady!.Save(bfsCachePath, dumpPath, update));
                log.Check($"  [3/9] BFS index built  ({bfsSw.Elapsed.TotalSeconds:F1}s  |  {bfsReady!.NodeCount:N0} nodes, {bfsReady.EdgeCount:N0} edges)");

                // SortedIdxMap is reused by BfsIndexCache (no re-sort needed).
                // Null p1/p2 and collect dead BFS temporaries (writeCursor ~440 MB).
                // No LOH compaction here — live BfsIndexCache is 3.8 GB; moving it
                // would invalidate caches before the 85s SharedReferrerCache.Build.
                p2 = null;
                p1 = null;
                GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: false);
            }

            ctx.PreloadAnalysis(new BfsCacheBox(bfsReady));

            // ── Step 4+5: Parent map + HotAddrTypes (both from BFS, no heap walk) ─
            if (parentMapOk && hotTypesOk)
            {
                log.CheckM("  [[4/9]] parent map  [dim]already cached[/]");
                log.CheckM("  [[5/9]] hot-addr-types  [dim]already cached[/]");
            }
            else
            {
                // --force: delete stale files so SharedReferrerCache.Build doesn't reuse them.
                if (force) { try { File.Delete(parentMapPath); } catch { } }
                if (force) { try { File.Delete(hotTypesPath);  } catch { } }

                var mapSw = Stopwatch.StartNew();
                CommandBase.RunStatus("  Building parent map + hot-addr-types from BFS...",
                    update => LoadHelper.BuildReferrerCache(ctx, update));
                log.Check($"  [4/9] Parent map built  ({mapSw.Elapsed.TotalSeconds:F1}s)");
                log.Check($"  [5/9] Hot-addr-types built");
            }

            // BFS index is not needed for steps 6–8. Release it now so ~3.8 GB
            // (IndexToAddr + Sizes + Offsets + Children + _sortedIdxMap) is GC-eligible
            // before the long GC-roots enumeration (step 6, typically 150–200s).
            // This is the ONE place where LOH compaction is cheap: only ~200 MB of live
            // data exists here (ctx/DumpContext small objects), so the GC moves ~200 MB
            // and returns the entire 3.8 GB BfsIndexCache to the OS in a few seconds.
            // Step 6 then runs with near-zero committed managed heap.
            bfsReady = null;
            ctx.ReplaceAnalysis(new BfsCacheBox(null));
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);

            // ── Step 6: GC roots ─────────────────────────────────────────────────
            if (gcRootsOk)
                log.CheckM("  [[6/9]] gc-roots  [dim]already cached[/]");
            else
            {
                // Use RunStatus with a live-update callback so the spinner text reflects
                // progress instead of showing a frozen label for the full ~2–3 min scan.
                CommandBase.RunStatus("  [6/8] Enumerating GC roots...", update =>
                {
                    var rootMap = new Dictionary<ulong, (string Kind, string? ObjType)>();
                    int n = 0;
                    foreach (var root in ctx.Heap.EnumerateRoots())
                    {
                        if (root.Object == 0 || rootMap.ContainsKey(root.Object)) continue;
                        string kind = root.RootKind switch
                        {
                            ClrRootKind.Stack             => "Stack (thread local)",
                            ClrRootKind.StrongHandle      => "GC Handle — Strong",
                            ClrRootKind.PinnedHandle      => "GC Handle — Pinned",
                            ClrRootKind.AsyncPinnedHandle => "GC Handle — Async-Pinned",
                            ClrRootKind.RefCountedHandle  => "GC Handle — RefCount",
                            ClrRootKind.FinalizerQueue    => "Finalizer Queue",
                            _                             => root.RootKind.ToString(),
                        };
                        var obj = ctx.Heap.GetObject(root.Object);
                        rootMap[root.Object] = (kind, obj.IsValid ? obj.Type?.Name : null);
                        n++;
                        if ((n & 0x3FFF) == 0)
                            update($"[6/9] {n:N0} GC roots scanned");
                    }
                    GcRootsCache.Save(gcRootCachePath, dumpPath, rootMap);
                    // Emit final count as [SCAN] token so PrintDone appends it to the ✓ line.
                    update($"[SCAN]GC roots|{rootMap.Count}|0"); // step 6/9
                });
            }

            // ── Step 7: Static roots ─────────────────────────────────────────────
            if (staticOk)
                log.CheckM("  [[7/9]] static-roots  [dim]already cached[/]");
            else
            {
                (int addrCount, int skippedMods) staticResult = default;
                var sw7 = Stopwatch.StartNew();
                CommandBase.RunStatus("  Scanning static fields...",
                    () => staticResult = LoadHelper.BuildStaticRootsCache(ctx));
                log.Check($"  [7/9] Static roots scanned  ({sw7.Elapsed.TotalSeconds:F1}s  |  {staticResult.addrCount:N0} addresses, {staticResult.skippedMods} modules skipped)");
            }

            // ── Step 8: Finalizer queue ──────────────────────────────────────────
            if (finQueueOk)
                log.CheckM("  [[8/9]] finalizer-queue  [dim]already cached[/]");
            else if (finQConsumer is not null && finQConsumer.Stats is not null)
            {
                // Consumer ran during step 1 — only resurrection check + thread info remain.
                var sw8 = Stopwatch.StartNew();
                int resurrect = 0;
                CommandBase.RunStatus("  Checking resurrection candidates...",
                    () => resurrect = (int)ctx.Runtime.EnumerateHandles()
                        .Count(h => h.HandleKind != ClrHandleKind.WeakShort
                                 && h.HandleKind != ClrHandleKind.WeakLong
                                 && finQConsumer.FinalizableAddresses.Contains(h.Object)));

                var finThread = ctx.Runtime.Threads.FirstOrDefault(t => t.IsFinalizer);
                var frames    = finThread?.EnumerateStackTrace()
                    .Select(f => f.FrameName ?? f.Method?.Signature ?? "")
                    .Where(f => f.Length > 0)
                    .Take(30)
                    .ToList() ?? (IReadOnlyList<string>)[];
                bool blocked = frames.Any(f =>
                    f.Contains("WaitOne",          StringComparison.OrdinalIgnoreCase) ||
                    f.Contains("Sleep",            StringComparison.OrdinalIgnoreCase) ||
                    f.Contains("ManualResetEvent", StringComparison.OrdinalIgnoreCase) ||
                    f.Contains("Monitor.Wait",     StringComparison.OrdinalIgnoreCase) ||
                    f.Contains("SemaphoreSlim",    StringComparison.OrdinalIgnoreCase) ||
                    f.Contains("SpinWait",         StringComparison.OrdinalIgnoreCase));

                var finQData = new FinalizerQueueData(
                    finQConsumer.Stats, finQConsumer.Total, finQConsumer.TotalSize,
                    blocked, frames, resurrect,
                    finThread?.ManagedThreadId ?? 0, finThread?.OSThreadId ?? 0);

                try { FinalizerQueueCache.Save(finQueuePath, dumpPath, finQData); } catch { }
                log.Check($"  [8/9] Finalizer queue cached  ({sw8.Elapsed.TotalSeconds:F1}s  |  {finQData.Total:N0} objects, {finQData.Stats.Count} types)");
            }
            else
                // Fallback: heap walk was skipped (all other caches were valid) — run dedicated pass.
                Step(8, 9, "Finalizer queue scan", () =>
                {
                    var analyzer = new FinalizerQueueAnalyzer();
                    var finQData = analyzer.Analyze(ctx);
                    try { FinalizerQueueCache.Save(finQueuePath, dumpPath, finQData); } catch { }
                });

            // ── Step 9: Event analysis ──────────────────────────────────────────
            // Depends on static-roots.bin (step 7) being built first so that
            // StaticRootAddresses.Build fast-paths from disk instead of scanning static fields again.
            if (eventOk)
                log.CheckM("  [[9/9]] event-analysis  [dim]already cached[/]");
            else
            {
                var sw9 = Stopwatch.StartNew();
                new EventAnalysisAnalyzer().Analyze(ctx);
                log.Check($"  [9/9] Event analysis cached  ({sw9.Elapsed.TotalSeconds:F1}s)");
            }

            log.Blank();
            log.Check($"All caches built  ({totalSw.Elapsed.TotalSeconds:F1}s total)");
            log.InfoM($"Run 'DumpDetective analyze {Markup.Escape(Path.GetFileName(dumpPath))} --full' to analyze.", indent: true);
            return 0;
        }
        catch (InvalidOperationException ex)
        {
            AnsiConsole.MarkupLine($"[bold red]✗ Error:[/] {Markup.Escape(ex.Message)}");
            return 1;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[bold red]✗ Unexpected error:[/] {Markup.Escape(ex.Message)}");
            return 1;
        }
    }

    public void Render(DumpContext ctx, IRenderSink sink)
        => throw new NotSupportedException("load does not produce a report.");

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static void Step(int n, int total, string label, Action body)
        => CommandBase.RunStatus($"  [{n}/{total}] {label}...", body);

    private static int ConvertEtl(string etlPath, bool force)
    {
        string etlxPath = TraceOpener.CachedEtlxPath(etlPath);

        bool cacheValid = !force &&
                          File.Exists(etlxPath) &&
                          File.GetLastWriteTimeUtc(etlxPath) >= File.GetLastWriteTimeUtc(etlPath);

        if (cacheValid)
        {
            AnsiConsole.MarkupLine($"  [dim].etlx already cached — {Markup.Escape(Path.GetFileName(etlxPath))}[/]");
            return 0;
        }

        var log = new ProgressLogger();
        log.SectionHeader($"DumpDetective load (trace)  {AppInfo.Version}");
        log.Info($"ETL: {Path.GetFileName(etlPath)}");
        log.InfoM($"Cache: {etlxPath}", indent: true);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        CommandBase.RunStatus("Converting ETL → ETLX…", update =>
        {
            using var trace = TraceOpener.Open(etlPath, update);
        });
        log.Check($"Converted → {Path.GetFileName(etlxPath)}  ({sw.Elapsed.TotalSeconds:F1}s)");
        return 0;
    }
}
