using Spectre.Console;

using System.Diagnostics;

namespace DumpDetective.Helpers;

/// <summary>
/// Lightweight peak-memory tracker for the DumpDetective tool process itself.
/// Start tracking with <see cref="Start"/>, then call <see cref="PrintSummary"/>
/// at the end of the run to display the peak working-set, managed heap, and
/// private-bytes observed across all internal <see cref="Sample"/> calls plus
/// an automatic background poller.
/// </summary>
internal static class ToolMemoryDiagnostic
{
    static long   _peakWorkingSet;
    static long   _peakManaged;
    static long   _peakPrivate;
    static string _peakWorkingSetAt = "(start)";
    static string _peakManagedAt    = "(start)";
    static string _peakPrivateAt    = "(start)";
    static long   _startWorkingSet;
    static long   _startManaged;
    static long   _startPrivate;

    static Timer? _pollTimer;
    static int    _pollTick;

    // ── Per-dump scope tracking ───────────────────────────────────────────────
    static string _scopeLabel     = "";
    static long   _scopeStartWs;
    static long   _scopePeakWs;
    static long   _scopePeakManaged;
    static long   _scopeStartMgd;
    static string _scopePeakWsAt     = "";
    static string _scopePeakManagedAt = "";
    static bool   _inScope;

    private sealed record DumpScope(
        string Label,
        long   StartWs,   long PeakWs,   string PeakWsAt,
        long   StartMgd,  long PeakMgd,  string PeakManagedAt,
        long   FinalWs,   long FinalMgd);

    static readonly List<DumpScope> _dumpScopes = [];

    /// <summary>When <c>true</c> (set by <c>--memory</c> flag), memory tables are printed.</summary>
    internal static bool ShowMemory { get; set; }

    /// <summary>
    /// Records the baseline and starts a background poll every 500 ms so peaks
    /// caused by sub-command heap walks are captured even without explicit <see cref="Sample"/> calls.
    /// </summary>
    internal static void Start()
    {
        var proc = Process.GetCurrentProcess();
        proc.Refresh();

        _startWorkingSet = proc.WorkingSet64;
        _startManaged    = GC.GetTotalMemory(false);
        _startPrivate    = proc.PrivateMemorySize64;

        _peakWorkingSet = _startWorkingSet;
        _peakManaged    = _startManaged;
        _peakPrivate    = _startPrivate;

        // Poll every 500 ms so peaks inside sub-commands are captured automatically.
        _pollTick = 0;
        _pollTimer = new Timer(_ => Sample($"poll-{++_pollTick}"), null,
            TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(500));
    }

    /// <summary>
    /// Takes a named memory reading and updates peak values if the reading is higher.
    /// Call this at interesting checkpoints (e.g. before/after a heavy command).
    /// </summary>
    internal static void Sample(string label)
    {
        var proc = Process.GetCurrentProcess();
        proc.Refresh();

        long ws   = proc.WorkingSet64;
        long mgd  = GC.GetTotalMemory(false);
        long priv = proc.PrivateMemorySize64;

        // Update global peaks
        if (ws   > _peakWorkingSet) { _peakWorkingSet = ws;   _peakWorkingSetAt = label; }
        if (mgd  > _peakManaged)    { _peakManaged    = mgd;  _peakManagedAt    = label; }
        if (priv > _peakPrivate)    { _peakPrivate    = priv; _peakPrivateAt    = label; }

        // Update scope-local peaks when a dump scope is active
        if (_inScope)
        {
            if (ws  > _scopePeakWs)      { _scopePeakWs      = ws;  _scopePeakWsAt      = label; }
            if (mgd > _scopePeakManaged) { _scopePeakManaged = mgd; _scopePeakManagedAt = label; }
        }
    }

    /// <summary>
    /// Marks the start of a per-dump processing scope (call before opening each dump).
    /// </summary>
    internal static void BeginDumpScope(string dumpLabel)
    {
        var proc = Process.GetCurrentProcess();
        proc.Refresh();

        _scopeLabel        = dumpLabel;
        _scopeStartWs      = proc.WorkingSet64;
        _scopePeakWs       = _scopeStartWs;
        _scopePeakWsAt     = $"{dumpLabel} (start)";
        _scopeStartMgd     = GC.GetTotalMemory(false);
        _scopePeakManaged  = _scopeStartMgd;
        _scopePeakManagedAt = $"{dumpLabel} (start)";
        _inScope           = true;
    }

    /// <summary>
    /// Marks the end of a per-dump scope (call after GC cleanup for a dump).
    /// Stores the peak result for <see cref="PrintDumpScopes"/>.
    /// </summary>
    internal static void EndDumpScope()
    {
        if (!_inScope) return;
        _inScope = false;

        var proc = Process.GetCurrentProcess();
        proc.Refresh();

        _dumpScopes.Add(new DumpScope(
            _scopeLabel,
            _scopeStartWs,  _scopePeakWs,      _scopePeakWsAt,
            _scopeStartMgd, _scopePeakManaged,  _scopePeakManagedAt,
            proc.WorkingSet64, GC.GetTotalMemory(false)));
    }

    /// <summary>
    /// Prints the per-dump peak working-set and managed-heap table.
    /// Only has output when at least one dump scope was recorded.
    /// </summary>
    /// <summary>Stops the background poller without printing anything.</summary>
    internal static void StopPoller()
    {
        _pollTimer?.Dispose();
        _pollTimer = null;
    }

    internal static void PrintDumpScopes()
    {
        if (!ShowMemory || _dumpScopes.Count == 0) return;

        AnsiConsole.WriteLine();

        var table = new Table()
            .Title("[bold]Peak Memory — Per Dump[/]")
            .BorderColor(Color.Grey)
            .AddColumn(new TableColumn("[bold]Dump[/]").Centered())
            .AddColumn(new TableColumn("[bold]Start Wkg Set[/]").RightAligned())
            .AddColumn(new TableColumn("[bold]Peak Wkg Set[/]").RightAligned())
            .AddColumn(new TableColumn("[bold]Δ Wkg Set[/]").RightAligned())
            .AddColumn(new TableColumn("[bold]Peak Managed Heap[/]").RightAligned())
            .AddColumn(new TableColumn("[bold]Δ Managed[/]").RightAligned())
            .AddColumn(new TableColumn("[bold]Peak At[/]"));

        foreach (var s in _dumpScopes)
        {
            long deltaWs  = s.PeakWs  - s.StartWs;
            long deltaMgd = s.PeakMgd - s.StartMgd;

            string wsColor  = deltaWs  > 500_000_000L ? "red" : deltaWs  > 100_000_000L ? "yellow" : "green";
            string mgdColor = deltaMgd > 200_000_000L ? "red" : deltaMgd > 50_000_000L  ? "yellow" : "green";

            table.AddRow(
                $"[bold]{Markup.Escape(s.Label)}[/]",
                $"[dim]{Markup.Escape(FormatSize(s.StartWs))}[/]",
                $"[{wsColor}]{Markup.Escape(FormatSize(s.PeakWs))}[/]",
                $"[{wsColor}]+{Markup.Escape(FormatSize(deltaWs))}[/]",
                $"[{mgdColor}]{Markup.Escape(FormatSize(s.PeakMgd))}[/]",
                $"[{mgdColor}]+{Markup.Escape(FormatSize(deltaMgd))}[/]",
                $"[dim]{Markup.Escape(s.PeakManagedAt)}[/]");
        }

        AnsiConsole.Write(table);
    }

    /// <summary>
    /// Stops the background poller and prints the peak memory summary to the console.
    /// </summary>
    internal static void PrintSummary()
    {
        _pollTimer?.Dispose();
        _pollTimer = null;

        // Take a final sample after the poller is stopped
        Sample("end");

        long deltaWs  = _peakWorkingSet - _startWorkingSet;
        long deltaMgd = _peakManaged    - _startManaged;
        long deltaPrv = _peakPrivate    - _startPrivate;

        AnsiConsole.WriteLine();

        var table = new Table()
            .Title("[bold]Tool Peak Memory Usage[/]")
            .BorderColor(Color.Grey)
            .AddColumn(new TableColumn("[bold]Metric[/]"))
            .AddColumn(new TableColumn("[bold]Start[/]").RightAligned())
            .AddColumn(new TableColumn("[bold]Peak[/]").RightAligned())
            .AddColumn(new TableColumn("[bold]Δ (growth)[/]").RightAligned())
            .AddColumn(new TableColumn("[bold]Peak At[/]"));

        string WsColor(long d)  => d > 500_000_000L ? "red" : d > 100_000_000L ? "yellow" : "green";
        string MgdColor(long d) => d > 200_000_000L ? "red" : d > 50_000_000L  ? "yellow" : "green";

        table.AddRow(
            "Working Set",
            $"[dim]{Markup.Escape(FormatSize(_startWorkingSet))}[/]",
            $"[{WsColor(deltaWs)}]{Markup.Escape(FormatSize(_peakWorkingSet))}[/]",
            $"[{WsColor(deltaWs)}]+{Markup.Escape(FormatSize(deltaWs))}[/]",
            $"[dim]{Markup.Escape(_peakWorkingSetAt)}[/]");

        table.AddRow(
            "Managed Heap",
            $"[dim]{Markup.Escape(FormatSize(_startManaged))}[/]",
            $"[{MgdColor(deltaMgd)}]{Markup.Escape(FormatSize(_peakManaged))}[/]",
            $"[{MgdColor(deltaMgd)}]+{Markup.Escape(FormatSize(deltaMgd))}[/]",
            $"[dim]{Markup.Escape(_peakManagedAt)}[/]");

        table.AddRow(
            "Private Bytes",
            $"[dim]{Markup.Escape(FormatSize(_startPrivate))}[/]",
            $"[{WsColor(deltaPrv)}]{Markup.Escape(FormatSize(_peakPrivate))}[/]",
            $"[{WsColor(deltaPrv)}]+{Markup.Escape(FormatSize(deltaPrv))}[/]",
            $"[dim]{Markup.Escape(_peakPrivateAt)}[/]");

        AnsiConsole.Write(table);
    }

    static string FormatSize(long bytes)
    {
        if (bytes < 0) return "0 B";
        return bytes switch
        {
            < 1024L               => $"{bytes} B",
            < 1024L * 1024        => $"{bytes / 1024.0:F1} KB",
            < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1} MB",
            _                     => $"{bytes / (1024.0 * 1024 * 1024):F2} GB",
        };
    }
}
