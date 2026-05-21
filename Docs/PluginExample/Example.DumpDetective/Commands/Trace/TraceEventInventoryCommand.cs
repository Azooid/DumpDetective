using DumpDetective.Core.Interfaces;
using DumpDetective.Core.Models;
using DumpDetective.Core.Runtime;
using DumpDetective.Core.Utilities;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;
using Spectre.Console;

namespace Example.DumpDetective;

/// <summary>
/// Opens any .nettrace or .etl file and inventories every event provider and
/// event name it contains — total events, trace duration, and the top N providers
/// ranked by volume, each with their most-frequent event names.
///
/// The first question when receiving an unknown trace is "what's in here?" —
/// this command answers that in seconds without any prior knowledge of what was
/// being collected.
///
/// Standalone: opens the trace file directly via EventPipeEventSource / ETWTraceEventSource.
/// Sub-analyzer (trace-analyze --with-plugins): receives the already-open TraceLog
/// via ITracePlugin.Analyze — no Commands or Reporting reference required.
/// </summary>
public sealed class TraceEventInventoryCommand : ICommand, ITracePlugin
{
    public string Name                 => "trace-inventory";
    public string Description          => "Inventory all event providers and top event names in a .nettrace or .etl file.";
    public bool   IncludeInFullAnalyze => false; // trace command — requires a trace file, not a dump
    public string Category             => "Example Plugin";
    public CommandKind Kind            => CommandKind.Trace;

    // ITracePlugin — participates in 'trace-analyze --with-plugins'
    // Only needs DumpDetective.Core (no Commands or Reporting reference required).
    public string Key          => Name;
    public string SectionTitle => "Trace Event Inventory";

    private const string Help = """
        Usage: DumpDetective trace-inventory <trace-file> [options]

        Opens a .nettrace or .etl file once and counts every event by provider
        and event name.  Useful as a first pass when you receive an unknown trace
        to understand which providers were active and what volume of data exists.

        Output:
          • Total event count and trace duration
          • Top N providers ranked by event volume (% of trace)
          • For each top provider: its most-frequent individual event names

        Also participates in 'trace-analyze --with-plugins' as a sub-analyzer.

        Supported formats:
          .nettrace  EventPipe trace (dotnet-trace, BenchmarkDotNet, etc.)
          .etl       Windows ETW trace (PerfView, WPR)

        Options:
          -n, --top <N>    Top N providers / event names per provider (default: 20)
          -o, --output <f> Write report to file (.html / .md / .txt / .json)
          -h, --help       Show this help

        Examples:
          DumpDetective trace-inventory app.nettrace
          DumpDetective trace-inventory perf.etl --top 30 -o providers.html
        """;

    public int Run(string[] args)
    {
        if (CommandBase.TryHelp(args, Help)) return 0;

        var     a         = CliArgs.Parse(args);
        int     top       = a.GetInt("top", 20);
        string? tracePath = a.DumpPath ?? a.Positionals.FirstOrDefault();

        if (tracePath is null)
        {
            AnsiConsole.MarkupLine("[bold red]✗[/] Trace file path required (.nettrace or .etl).");
            AnsiConsole.MarkupLine(Markup.Escape(Help));
            return 1;
        }
        if (!File.Exists(tracePath))
        {
            AnsiConsole.MarkupLine($"[bold red]✗[/] File not found: {Markup.Escape(tracePath)}");
            return 1;
        }
        if (!CliArgs.IsTraceFile(tracePath))
        {
            AnsiConsole.MarkupLine($"[bold red]✗[/] Expected .nettrace or .etl: {Markup.Escape(Path.GetFileName(tracePath))}");
            return 1;
        }

        // Default to an HTML file next to the trace when no -o is specified.
        var outputPaths = a.EffectiveOutputPaths.Count > 0
            ? a.EffectiveOutputPaths
            : (IReadOnlyList<string>)[CommandBase.DefaultOutputPath(tracePath, ".html")];

        try
        {
            using var sink = SinkFactory.CreateMulti(outputPaths);
            CollectStandalone(tracePath, top, sink);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[bold red]✗[/] {Markup.Escape(ex.Message)}");
            return 1;
        }

        foreach (var p in outputPaths)
            AnsiConsole.MarkupLine($"[dim]→ Written to:[/] {Markup.Escape(p)}");

        return 0;
    }

    // Trace commands cannot analyze a memory dump.
    public void Render(DumpContext ctx, IRenderSink sink) =>
        sink.Alert(AlertLevel.Info,
            "trace-inventory requires a trace file (.nettrace or .etl), not a memory dump.");

    // ── ITracePlugin ──────────────────────────────────────────────────────
    // Called by trace-analyze / trace-dump-analyze when --with-plugins is set.
    // The orchestrator provides a CaptureSink; we just write to the supplied sink.
    // The already-open TraceLog avoids re-opening the file.
    public string? Analyze(TraceLog trace, string traceFileName, int top, string? processFilter, IRenderSink sink)
    {
        var providerCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var eventCounts    = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        long totalEvents   = 0;
        double durationMs  = 0;
        long nextTick      = Environment.TickCount64 + 200;

        foreach (TraceEvent e in trace.Events)
        {
            totalEvents++;
            if (e.TimeStampRelativeMSec > durationMs) durationMs = e.TimeStampRelativeMSec;

            providerCounts.TryGetValue(e.ProviderName, out int pc);
            providerCounts[e.ProviderName] = pc + 1;

            string key = $"{e.ProviderName}\x1F{e.EventName}";
            eventCounts.TryGetValue(key, out int ec);
            eventCounts[key] = ec + 1;

            if (Environment.TickCount64 >= nextTick)
            {
                // No progress callback in ITracePlugin — the orchestrator shows a spinner.
                nextTick = Environment.TickCount64 + 200;
            }
        }

        RenderInventory(sink, traceFileName, totalEvents, durationMs, providerCounts, eventCounts, top);
        return $"{providerCounts.Count} providers  ·  {totalEvents:N0} events  ·  {durationMs / 1_000:F2}s";
    }

    // ── standalone analysis ───────────────────────────────────────────────────

    private static void CollectStandalone(string tracePath, int top, IRenderSink sink)
    {
        var providerCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var eventCounts    = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        long totalEvents   = 0;
        double durationMs  = 0;
        long nextTick      = Environment.TickCount64 + 200;

        // Open with the right source type; both inherit from TraceEventDispatcher
        // which defines Process(). ExcludeAssets="runtime" in the csproj means the
        // TraceEvent DLLs are NOT deployed with the plugin — the host's copies are
        // used at runtime because "Microsoft.Diagnostics." is in the host prefix list.
        using TraceEventDispatcher source = tracePath.EndsWith(".nettrace", StringComparison.OrdinalIgnoreCase)
            ? new EventPipeEventSource(tracePath)
            : new ETWTraceEventSource(tracePath);

        CommandBase.RunStatus("Reading trace events...", update =>
        {
            source.Dynamic.All += e =>
            {
                totalEvents++;
                if (e.TimeStampRelativeMSec > durationMs) durationMs = e.TimeStampRelativeMSec;

                providerCounts.TryGetValue(e.ProviderName, out int pc);
                providerCounts[e.ProviderName] = pc + 1;

                string key = $"{e.ProviderName}\x1F{e.EventName}";
                eventCounts.TryGetValue(key, out int ec);
                eventCounts[key] = ec + 1;

                if (Environment.TickCount64 >= nextTick)
                {
                    update($"Reading events...  {totalEvents:N0}  •  {providerCounts.Count} providers");
                    nextTick = Environment.TickCount64 + 200;
                }
            };

            source.Process();
            update($"[SCAN]trace-inventory|{totalEvents}|0");
        });

        sink.Header("Trace Event Inventory", Path.GetFileName(tracePath), navLevel: 2);
        RenderInventory(sink, Path.GetFileName(tracePath), totalEvents, durationMs, providerCounts, eventCounts, top);
    }

    // ── shared render ─────────────────────────────────────────────────────────

    private static void RenderInventory(
        IRenderSink sink, string traceFileName,
        long totalEvents, double durationMs,
        Dictionary<string, int> providerCounts,
        Dictionary<string, int> eventCounts,
        int top)
    {
        sink.KeyValues(
        [
            ("Trace file",   traceFileName),
            ("Duration",     durationMs >= 1_000 ? $"{durationMs / 1_000:F2} s" : $"{durationMs:F0} ms"),
            ("Total events", totalEvents.ToString("N0")),
            ("Providers",    providerCounts.Count.ToString("N0")),
        ]);

        var topProviders = providerCounts
            .OrderByDescending(kv => kv.Value)
            .Take(top)
            .ToList();

        sink.Section($"Top {Math.Min(top, providerCounts.Count)} Providers by Event Count");
        sink.Table(
            ["Provider", "Events", "% of Total"],
            topProviders.Select(kv => new[]
            {
                kv.Key,
                kv.Value.ToString("N0"),
                totalEvents > 0 ? $"{kv.Value * 100.0 / totalEvents:F1}%" : "—",
            }).ToList(),
            caption: $"{providerCounts.Count} provider(s) found in this trace");

        // Top event names per provider, collapsed by default.
        foreach (var (provider, _) in topProviders.Take(10))
        {
            string prefix = provider + "\x1F";
            var topEvents = eventCounts
                .Where(kv => kv.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(kv => kv.Value)
                .Take(10)
                .Select(kv => new[] { kv.Key[prefix.Length..], kv.Value.ToString("N0") })
                .ToList();

            if (topEvents.Count == 0) continue;

            sink.BeginDetails($"{provider}  —  {topEvents.Count} event name(s)", open: false);
            sink.Table(["Event Name", "Count"], topEvents);
            sink.EndDetails();
        }
    }
}
