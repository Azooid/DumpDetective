using DumpDetective.Core.Utilities;
using Microsoft.Diagnostics.Tracing.Etlx;

namespace DumpDetective.Commands.Trace;

/// <summary>
/// Opens (or converts) a trace file and surfaces <c>TraceLog.ConversionLog</c>
/// messages in the active Spectre spinner so the user sees live progress.
///
/// Used by all trace commands — standalone and orchestrators alike.
/// </summary>
internal static class TraceOpener
{
    /// <summary>
    /// Returns the path where the converted .etlx will be stored:
    /// <c>&lt;etl-dir&gt;\.ddcache\&lt;stem&gt;\&lt;stem&gt;.etlx</c>.
    /// </summary>
    internal static string CachedEtlxPath(string etlPath)
    {
        string dir  = Path.GetDirectoryName(Path.GetFullPath(etlPath))!;
        string stem = Path.GetFileNameWithoutExtension(etlPath);
        return Path.Combine(dir, ".ddcache", stem, stem + ".etlx");
    }

    /// <summary>
    /// Opens the trace file, converting ETL → ETLX into the
    /// <c>.ddcache\&lt;stem&gt;\</c> folder if the cached file does not yet exist
    /// or is older than the source ETL.
    ///
    /// Forwards conversion log lines to <paramref name="statusUpdate"/> so the
    /// Spectre spinner shows live progress. Pass <see langword="null"/> to discard.
    /// </summary>
    internal static TraceLog Open(string tracePath, Action<string>? statusUpdate = null)
    {
        var opts = new TraceLogOptions
        {
            ConversionLog = statusUpdate is not null
                ? new StatusTextWriter(statusUpdate)
                : TextWriter.Null
        };

        // .etlx — open directly (already converted)
        if (tracePath.EndsWith(".etlx", StringComparison.OrdinalIgnoreCase))
            return new TraceLog(tracePath);

        // .nettrace — EventPipe format from .NET Core / dotnet-trace.
        // Convert to .etlx in .ddcache so subsequent opens skip conversion.
        if (tracePath.EndsWith(".nettrace", StringComparison.OrdinalIgnoreCase))
        {
            string cachedEtlx = CachedEtlxPath(tracePath);

            bool cacheValid = File.Exists(cachedEtlx) &&
                              File.GetLastWriteTimeUtc(cachedEtlx) >= File.GetLastWriteTimeUtc(tracePath);

            if (!cacheValid)
            {
                statusUpdate?.Invoke("Converting .nettrace → .etlx…");
                Directory.CreateDirectory(Path.GetDirectoryName(cachedEtlx)!);
                TraceLog.CreateFromEventPipeDataFile(tracePath, cachedEtlx, opts);
            }
            else
            {
                statusUpdate?.Invoke("Loading cached .etlx from .ddcache…");
            }

            return new TraceLog(cachedEtlx);
        }

        // .etl — resolve companions → base, then convert into .ddcache
        if (tracePath.EndsWith(".etl", StringComparison.OrdinalIgnoreCase))
        {
            string basePath    = EtlPathHelper.ResolveToBase(tracePath);
            string cachedEtlx  = CachedEtlxPath(basePath);

            bool cacheValid = File.Exists(cachedEtlx) &&
                              File.GetLastWriteTimeUtc(cachedEtlx) >= File.GetLastWriteTimeUtc(basePath);

            if (!cacheValid)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(cachedEtlx)!);
                TraceLog.CreateFromEventTraceLogFile(basePath, cachedEtlx, opts);
            }
            else
            {
                statusUpdate?.Invoke("Loading cached .etlx from .ddcache…");
            }

            return new TraceLog(cachedEtlx);
        }

        // Other formats (.nettrace etc.) — fall back to OpenOrConvert
        return TraceLog.OpenOrConvert(tracePath, opts);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // StatusTextWriter — forwards ConversionLog lines to the spinner callback.
    //
    // SHOW (reformatted):
    //   [ELAPSED … READ … WRITTEN …]   → "Converting…  518s  •  116M events  •  12,663MB written"
    //   [Conversion complete N events. Conversion took N sec.]  → "Converted N events in N sec"
    //   ETL Size … ETLX Size …         → "ETL 27,687 MB  →  ETLX 13,966 MB"
    //   Started: X                     → "X…"
    //   Completed: X (Elapsed …)       → "✓ X  (N sec)"
    //   [Found N Records…]             → "Found N events"
    //
    // SUPPRESS:
    //   WARNING: …                     (kernel stack merge noise — thousands of identical lines)
    //   Opened X.etlx                  (redundant)
    //   There were N address that did not resolve…  (per-process noise)
    //   N distinct processes.          (noise)
    //   Totals / indented stats        (verbose block)
    //   Histogram: …                   (internal ETW histogram)
    //   A total of N symbolic…         (lookup stats)
    //   Addresses outside any module…  (lookup stats)
    //   Done with symbolic lookup.     (noise)
    // ─────────────────────────────────────────────────────────────────────────
    private sealed class StatusTextWriter : TextWriter
    {
        private readonly Action<string> _update;
        private readonly System.Text.StringBuilder _buf = new();
        private bool _inTotalsBlock; // true while inside the verbose "Totals" indented block

        internal StatusTextWriter(Action<string> update) => _update = update;

        public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;

        public override void Write(char value)
        {
            if (value == '\n')      Flush();
            else if (value != '\r') _buf.Append(value);
        }

        public override void Write(string? value)
        {
            if (value is null) return;
            foreach (char c in value) Write(c);
        }

        public override void WriteLine(string? value)
        {
            if (value is not null) _buf.Append(value);
            Flush();
        }

        public override void Flush()
        {
            string raw  = _buf.ToString();
            string line = raw.Trim();
            _buf.Clear();
            if (line.Length == 0) return;

            // ── WARNING: kernel-stack merge noise (thousands of lines) ────
            if (line.StartsWith("WARNING:", StringComparison.OrdinalIgnoreCase)) return;

            // ── "Opened X.etlx" — redundant ──────────────────────────────
            if (line.StartsWith("Opened", StringComparison.OrdinalIgnoreCase) &&
                line.Contains(".etlx", StringComparison.OrdinalIgnoreCase)) return;

            // ── "There were N address that did not resolve…" ──────────────
            if (line.StartsWith("There were ", StringComparison.OrdinalIgnoreCase) &&
                line.Contains("did not resolve", StringComparison.OrdinalIgnoreCase)) return;

            // ── "N distinct processes." ───────────────────────────────────
            if (line.EndsWith("distinct processes.", StringComparison.OrdinalIgnoreCase)) return;

            // ── "Totals" header + following indented stats block ──────────
            if (line.Equals("Totals", StringComparison.OrdinalIgnoreCase))
            {
                _inTotalsBlock = true;
                return;
            }
            if (_inTotalsBlock)
            {
                // Indented lines stay in the block; un-indented line exits it
                if (raw.Length > 0 && (raw[0] == ' ' || raw[0] == '\t')) return;
                _inTotalsBlock = false;
                // Fall through — process this non-indented line normally
            }

            // ── "Histogram: …" ────────────────────────────────────────────
            if (line.StartsWith("Histogram:", StringComparison.OrdinalIgnoreCase)) return;

            // ── Symbolic lookup noise ─────────────────────────────────────
            if (line.StartsWith("A total of", StringComparison.OrdinalIgnoreCase) &&
                line.Contains("symbolic", StringComparison.OrdinalIgnoreCase)) return;
            if (line.StartsWith("Addresses outside any module", StringComparison.OrdinalIgnoreCase)) return;
            if (line.StartsWith("Done with symbolic lookup", StringComparison.OrdinalIgnoreCase)) return;

            // ─────────────────────────────────────────────────────────────
            // Lines we DO want — format them compactly
            // ─────────────────────────────────────────────────────────────

            // ── [ELAPSED … READ … WRITTEN …] ──────────────────────────────
            if (line.StartsWith("[ELAPSED", StringComparison.OrdinalIgnoreCase))
            {
                _update(FormatElapsed(line));
                return;
            }

            // ── [Conversion complete N events. Conversion took N sec.] ────
            if (line.StartsWith("[Conversion complete", StringComparison.OrdinalIgnoreCase))
            {
                _update(FormatConversionComplete(line));
                return;
            }

            // ── ETL Size N MB  ETLX Size N MB ────────────────────────────
            if (line.StartsWith("ETL Size", StringComparison.OrdinalIgnoreCase))
            {
                _update(FormatFileSizes(line));
                return;
            }

            // ── Started: X ────────────────────────────────────────────────
            if (line.StartsWith("Started:", StringComparison.OrdinalIgnoreCase))
            {
                string phase = line[8..].Trim();
                _update($"{phase}…");
                return;
            }

            // ── Completed: X (Elapsed Time: N sec) ───────────────────────
            if (line.StartsWith("Completed:", StringComparison.OrdinalIgnoreCase))
            {
                _update(FormatCompleted(line));
                return;
            }

            // ── [Found N Records. N total events.] ───────────────────────
            if (line.StartsWith("[Found ", StringComparison.OrdinalIgnoreCase))
            {
                _update(FormatFound(line));
                return;
            }

            // ── Everything else — shorten paths, truncate ─────────────────
            line = ShortenPaths(line);
            if (line.Length > 100) line = line[..97] + "…";
            _update(line);
        }

        // [ELAPSED 518 seconds.  READ 116,654,080 events.  WRITTEN 12,663MB.]
        // → "Converting…  518 seconds  •  116,654,080 events  •  12,663MB written"
        private static string FormatElapsed(string line)
        {
            static string? Val(string src, string key, string unitPat)
            {
                var m = System.Text.RegularExpressions.Regex.Match(
                    src,
                    key + @"\s+([\d,]+(?:\.\d+)?\s*" + unitPat + ")",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                return m.Success ? m.Groups[1].Value.Trim().TrimEnd('.') : null;
            }

            var parts = new List<string>(3);
            string? elapsed = Val(line, "ELAPSED", "seconds");
            string? events  = Val(line, "READ",    "events");
            string? written = Val(line, "WRITTEN", @"MB");

            if (elapsed is not null) parts.Add(elapsed);
            if (events  is not null) parts.Add(events);
            if (written is not null) parts.Add($"{written} written");

            return parts.Count > 0
                ? "Converting…  " + string.Join("  •  ", parts)
                : "Converting…";
        }

        // [Conversion complete 73,542,577 events.  Conversion took 538 sec.]
        // → "Converted  73,542,577 events  •  538 sec"
        private static string FormatConversionComplete(string line)
        {
            var mEv  = System.Text.RegularExpressions.Regex.Match(
                line, @"complete\s+([\d,]+)\s+events", RegexOpts);
            var mSec = System.Text.RegularExpressions.Regex.Match(
                line, @"took\s+([\d,]+(?:\.\d+)?)\s+sec", RegexOpts);

            string events = mEv.Success  ? mEv.Groups[1].Value  : "?";
            string secs   = mSec.Success ? mSec.Groups[1].Value : "?";
            return $"Converted  {events} events  •  {secs} sec";
        }

        // ETL Size 27,687.453 MB ETLX Size 13,966.980 MB
        // → "ETL 27,687 MB  →  ETLX 13,966 MB"
        private static string FormatFileSizes(string line)
        {
            var mEtl  = System.Text.RegularExpressions.Regex.Match(
                line, @"ETL\s+Size\s+([\d,]+(?:\.\d+)?)\s*MB", RegexOpts);
            var mEtlx = System.Text.RegularExpressions.Regex.Match(
                line, @"ETLX\s+Size\s+([\d,]+(?:\.\d+)?)\s*MB", RegexOpts);

            string etl  = mEtl.Success  ? $"{mEtl.Groups[1].Value} MB"  : "?";
            string etlx = mEtlx.Success ? $"{mEtlx.Groups[1].Value} MB" : "?";
            return $"ETL {etl}  →  ETLX {etlx}";
        }

        // Completed: Opening HighCPU_01.etl (unmerged)   (Elapsed Time: 560.208 sec)
        // → "✓ Opening HighCPU_01.etl  (560 sec)"
        private static string FormatCompleted(string line)
        {
            string rest = line[10..].Trim(); // strip "Completed:"
            var mSec = System.Text.RegularExpressions.Regex.Match(
                rest, @"\(Elapsed\s+Time:\s*([\d,]+(?:\.\d+)?)\s*sec\)", RegexOpts);

            // Remove the "(Elapsed Time: …)" part from the label
            string label = mSec.Success
                ? rest[..mSec.Index].Trim().TrimEnd('(').Trim()
                : rest;
            label = ShortenPaths(label);
            string suffix = mSec.Success ? $"  ({mSec.Groups[1].Value} sec)" : "";
            return $"✓ {label}{suffix}";
        }

        // [Found 7089 Records.  7,089 total events.]
        // → "Found 7,089 events"
        private static string FormatFound(string line)
        {
            var m = System.Text.RegularExpressions.Regex.Match(
                line, @"([\d,]+)\s+total\s+events", RegexOpts);
            if (!m.Success)
            {
                m = System.Text.RegularExpressions.Regex.Match(
                    line, @"Found\s+([\d,]+)\s+Records", RegexOpts);
            }
            return m.Success ? $"Found {m.Groups[1].Value} events" : line;
        }

        private static string ShortenPaths(string text) =>
            System.Text.RegularExpressions.Regex.Replace(
                text, @"[A-Za-z]:\\[^\s""']+", m => Path.GetFileName(m.Value));

        private static readonly System.Text.RegularExpressions.RegexOptions RegexOpts =
            System.Text.RegularExpressions.RegexOptions.IgnoreCase;
    }
}
