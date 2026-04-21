using Spectre.Console;
using System.Diagnostics;
using System.Threading;

namespace DumpDetective.Core.Utilities;

/// <summary>
/// Timestamped console progress logger for long-running dump analysis operations.
/// Progress protocol — DumpCollector/HeapWalker emit special prefixed strings:
///   [SCAN]label|count|ms  — scan complete: clears any live line, prints checkmark with rate
///   Walking heap objects — ...  — live in-place spinner update
///   anything else — timestamped info line (indented)
/// </summary>
public sealed class ProgressLogger
{
    private static readonly char[] SpinnerFrames = ['⠋', '⠙', '⠹', '⠸', '⠼', '⠴', '⠦', '⠧', '⠇', '⠏'];

    private bool    _hasLive;
    private string? _lastLiveMsg;
    private int     _liveLineCount;   // number of live lines currently on screen
    private int     _spinIdx;

    // ── Parallel batch tracking ───────────────────────────────────────────────
    private readonly object _pLock      = new();
    private int             _pTotal;
    private int             _pCompleted;
    private Stopwatch?      _pSw;
    private Timer?          _pTimer;
    private readonly Dictionary<string, long> _pRunning = new(16);

    // ── Section headers ───────────────────────────────────────────────────────

    public void SectionHeader(string title)
    {
        FinishLive();
        int w      = Console.WindowWidth > 20 ? Console.WindowWidth - 1 : 100;
        int dashes = Math.Max(4, w - title.Length - 4);
        AnsiConsole.MarkupLine($"[bold]── {Markup.Escape(title)} {new string('─', dashes)}[/]");
    }

    // ── Fixed milestone lines ─────────────────────────────────────────────────

    public void Info(string msg, bool indent = false)    => Print("ℹ",  "blue",        msg, indent);
    public void Success(string msg, bool indent = false) => Print("✅", "green",       msg, indent);
    public void Check(string msg, bool indent = false)   => Print("✓",  "green",       msg, indent);
    public void Stage(string msg, bool indent = false)   => Print("▶",  "bold yellow", msg, indent);
    public void Warn(string msg, bool indent = false)    => Print("⚠",  "yellow",      msg, indent);

    /// <summary>Same as <see cref="Success"/> but <paramref name="markup"/> is pre-built Spectre markup — not escaped.</summary>
    public void SuccessM(string markup, bool indent = false) => PrintM("✅", "green",       markup, indent);
    /// <summary>Same as <see cref="Check"/> but <paramref name="markup"/> is pre-built Spectre markup — not escaped.</summary>
    public void CheckM(string markup, bool indent = false)   => PrintM("✓",  "green",       markup, indent);
    /// <summary>Same as <see cref="Info"/> but <paramref name="markup"/> is pre-built Spectre markup — not escaped.</summary>
    public void InfoM(string markup, bool indent = false)    => PrintM("ℹ",  "blue",        markup, indent);

    public void SubStep(string msg)
    {
        FinishLive();
        AnsiConsole.MarkupLine($"[dim][[{Now}]][/]     [green]✓[/] {Markup.Escape(msg)}");
    }

    public void Blank()
    {
        FinishLive();
        AnsiConsole.WriteLine();
    }

    // ── Parallel batch API ────────────────────────────────────────────────────

    /// <summary>Start a parallel batch. Call before <c>Parallel.For</c>.</summary>
    public void BeginParallelBatch(string label, int total, bool indent = false)
    {
        lock (_pLock)
        {
            _pTotal     = total;
            _pCompleted = 0;
            _pRunning.Clear();
            _pSw = Stopwatch.StartNew();
            // Background timer: redraw the live line every 100ms so elapsed times tick live
            _pTimer = new Timer(_ => { lock (_pLock) { if (_pSw is not null) RedrawParallelLive(); } },
                                null, 100, 100);
        }
        Stage($"{label}  ({total} in parallel)...", indent);
    }

    /// <summary>Call at the start of each parallel worker (inside the parallel body).</summary>
    public void StartParallelItem(string name)
    {
        lock (_pLock)
        {
            _pRunning[name] = _pSw?.ElapsedMilliseconds ?? 0;
        }
    }

    /// <summary>Call when a parallel worker finishes. Prints a permanent ✓ line and refreshes the live status.</summary>
    public void CompleteParallelItem(string name, long elapsedMs, string[]? details = null)
    {
        lock (_pLock)
        {
            _pRunning.Remove(name);
            _pCompleted++;
            int    n       = _pCompleted;
            string elapsed = elapsedMs < 1000 ? $"{elapsedMs}ms" : $"{elapsedMs / 1000.0:F1}s";
            ClearLive();
            _lastLiveMsg = null;
            AnsiConsole.MarkupLine($"[dim][[{Now}]][/]     [green]✓[/] {n}/{_pTotal}: {Markup.Escape(name)}  ({elapsed})");
            if (details is not null)
                foreach (var d in details)
                    AnsiConsole.MarkupLine($"               [dim]└─ {Markup.Escape(d)}[/]");
            if (n < _pTotal) RedrawParallelLive();
        }
    }

    /// <summary>Call after <c>Parallel.For</c> completes.</summary>
    public void EndParallelBatch(bool indent = false)
    {
        // Stop the timer first (outside the lock to avoid deadlock with timer callback)
        Timer? t;
        lock (_pLock) { t = _pTimer; _pTimer = null; }
        t?.Dispose();

        lock (_pLock)
        {
            ClearLive();
            _lastLiveMsg = null;
            double secs = _pSw?.Elapsed.TotalSeconds ?? 0;
            _pSw = null;
            _pRunning.Clear();
            Success($"All {_pTotal} sub-reports complete  ({secs:F1}s)", indent);
        }
    }

    // Caller must hold _pLock
    private void RedrawParallelLive()
    {
        long   nowMs       = _pSw?.ElapsedMilliseconds ?? 0;
        double overallSecs = nowMs / 1000.0;
        char   spin        = NextSpinner();

        int filled = _pTotal > 0 ? (int)(20.0 * _pCompleted / _pTotal) : 0;
        string bar = new string('█', filled) + new string('░', 20 - filled);

        var result = new List<string>();
        result.Add($"[{Now}]   {spin} {bar} {_pCompleted}/{_pTotal}  {overallSecs:F1}s");

        if (_pRunning.Count > 0)
        {
            var items = _pRunning
                .OrderBy(kv => kv.Value)   // oldest first
                .Select(kv =>
                {
                    long   ms = nowMs - kv.Value;
                    string t  = ms < 1000 ? $"{ms}ms" : $"{ms / 1000.0:F1}s";
                    return $"{kv.Key}({t})";
                })
                .ToList();

            int    w    = Math.Max(60, Console.WindowWidth - 1);
            string pfx  = "               ▸  ";
            string cont = "                  ";
            var    sb   = new System.Text.StringBuilder(pfx);
            bool   firstItem = true;

            foreach (var item in items)
            {
                string sep = firstItem ? "" : "   ";
                if (!firstItem && sb.Length + sep.Length + item.Length > w)
                {
                    result.Add(sb.ToString());
                    sb.Clear().Append(cont);
                    sep       = "";
                    firstItem = true;
                }
                sb.Append(sep).Append(item);
                firstItem = false;
            }
            if (!firstItem) result.Add(sb.ToString());
        }

        WriteLiveLines(result.ToArray());
    }

    // ── Progress callback ─────────────────────────────────────────────────────

    public void OnProgress(string msg)
    {
        if (msg.StartsWith("[SCAN]", StringComparison.Ordinal))
        {
            ClearLive();
            _lastLiveMsg = null;
            var inner = msg[6..];
            int p1    = inner.IndexOf('|');
            int p2    = p1 >= 0 ? inner.IndexOf('|', p1 + 1) : -1;
            if (p1 > 0 && p2 > p1
                && long.TryParse(inner[(p1 + 1)..p2], out long count)
                && long.TryParse(inner[(p2 + 1)..],   out long ms))
            {
                double secs    = ms / 1000.0;
                long   rate    = secs > 0 ? (long)(count / secs) : 0;
                string elapsed = ms < 1000 ? $"{ms}ms" : $"{secs:F1}s";
                AnsiConsole.MarkupLine(
                    $"[dim][[{Now}]][/]   [green]✓[/] {Markup.Escape(inner[..p1])}: " +
                    $"{count:N0} objs  •  {elapsed}  •  ~{rate:N0}/s");
            }
            else
            {
                AnsiConsole.MarkupLine($"[dim][[{Now}]][/]   [green]✓[/] {Markup.Escape(inner)}");
            }
        }
        else if (msg.StartsWith("Walking heap objects", StringComparison.Ordinal))
        {
            _lastLiveMsg = msg;
            WriteLive($"[{Now}]   {NextSpinner()} {msg}");
        }
        else
        {
            FinishLive();
            Info(msg, indent: true);
        }
    }

    // ── Internal helpers ──────────────────────────────────────────────────────

    private void Print(string icon, string style, string msg, bool indent)
    {
        FinishLive();
        var pfx = indent ? "  " : "";
        AnsiConsole.MarkupLine($"[dim][[{Now}]][/] {pfx}[{style}]{icon}[/] {Markup.Escape(msg)}");
    }

    private void PrintM(string icon, string style, string markup, bool indent)
    {
        FinishLive();
        var pfx = indent ? "  " : "";
        AnsiConsole.MarkupLine($"[dim][[{Now}]][/] {pfx}[{style}]{icon}[/] {markup}");
    }

    private void WriteLive(string text) => WriteLiveLines([text]);

    private void WriteLiveLines(string[] lines)
    {
        ClearLive();
        int w = Math.Max(40, Console.WindowWidth - 1);
        for (int i = 0; i < lines.Length; i++)
        {
            // Pad / truncate each line to terminal width so overwriting works cleanly
            string ln = lines[i].Length < w ? lines[i].PadRight(w) : lines[i][..w];
            if (i < lines.Length - 1)
                Console.WriteLine(ln);
            else
                Console.Write(ln); // no newline on last — cursor stays on it
        }
        _hasLive       = true;
        _liveLineCount = lines.Length;
    }

    private void ClearLive()
    {
        if (!_hasLive) return;
        int w     = Math.Max(40, Console.WindowWidth - 1);
        string bl = new string(' ', w);
        // Clear the current (bottom) line
        Console.Write('\r' + bl + '\r');
        // Move cursor up and clear each preceding line
        for (int i = 1; i < _liveLineCount; i++)
            Console.Write("\x1b[A\r" + bl + '\r');
        _hasLive       = false;
        _liveLineCount = 0;
    }

    private void FinishLive()
    {
        if (_hasLive && _lastLiveMsg is not null)
        {
            ClearLive();
            AnsiConsole.MarkupLine($"[dim][[{Now}]][/]   [green]✓[/] {Markup.Escape(_lastLiveMsg)}");
            _lastLiveMsg = null;
        }
        else
        {
            ClearLive();
        }
    }

    private char NextSpinner()
    {
        var c = SpinnerFrames[_spinIdx % SpinnerFrames.Length];
        _spinIdx++;
        return c;
    }

    private static string Now => DateTime.Now.ToString("HH:mm:ss");
}
