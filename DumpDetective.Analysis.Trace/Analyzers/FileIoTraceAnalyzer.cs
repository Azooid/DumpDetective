using DumpDetective.Core.Models.CommandData;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;

namespace DumpDetective.Analysis.Trace.Analyzers;

/// <summary>
/// Parses Microsoft-Windows-Kernel-File provider events to detect slow file I/O,
/// excessive sync reads/writes, and I/O bottlenecks per file path.
/// Note: kernel file events are only available in ETL traces, not .nettrace.
/// </summary>
public sealed class FileIoTraceAnalyzer
{
    private const double SlowIoMs = 10.0; // 10 ms threshold for slow sync I/O

    public FileIoTraceData Analyze(string tracePath, int top = 20, string? processFilter = null)
    {
        try
        {
            using var trace = TraceLog.OpenOrConvert(tracePath,
                new TraceLogOptions { ConversionLog = TextWriter.Null });
            return Analyze(trace, Path.GetFileName(tracePath), top, processFilter);
        }
        catch (Exception ex)
        {
            return new FileIoTraceData($"Failed: {ex.Message}", processFilter,
                0, 0, 0, 0, 0, 0, [], [], null, false);
        }
    }

    public FileIoTraceData Analyze(TraceLog trace, string traceFileName, int top = 20,
                                    string? processFilter = null, Action<string>? progress = null)
    {
        // pending I/O: key = (ThreadID, IrpPtr) → (startMs, opType, filePath)
        var pending = new Dictionary<string, (double StartMs, string Op, string File)>(StringComparer.Ordinal);
        var byFile  = new Dictionary<string, FileAcc>(StringComparer.OrdinalIgnoreCase);
        var slowOps = new List<FileIoSlowOp>();
        var perSecond = new Dictionary<int, long>();

        int reads = 0, writes = 0, other = 0;
        long readBytes = 0, writeBytes = 0;
        long total = trace.EventCount;
        long processed = 0;
        long lastProgressMs = 0;

        try
        {
            foreach (var ev in trace.Events)
            {
                processed++;
                if (progress is not null && Environment.TickCount64 - lastProgressMs >= 200)
                {
                    progress($"{reads:N0} reads  \u2022  {writes:N0} writes");
                    lastProgressMs = Environment.TickCount64;
                }
                if (processFilter is not null &&
                    !ev.ProcessName.Contains(processFilter, StringComparison.OrdinalIgnoreCase))
                    continue;

                string evName = ev.EventName ?? "";
                bool isFileEvent =
                    evName.StartsWith("FileIO",   StringComparison.OrdinalIgnoreCase) ||
                    evName.StartsWith("File/",    StringComparison.OrdinalIgnoreCase) ||
                    evName.Contains("KernelFile", StringComparison.OrdinalIgnoreCase);
                if (!isFileEvent) continue;

                string filePath = SafeStr(ev, "FileName");
                if (filePath.Length == 0) filePath = SafeStr(ev, "OpenPath");
                if (filePath.Length == 0) filePath = "(unknown)";

                long bytes = SafeLong(ev, "IoSize");
                if (bytes <= 0) bytes = SafeLong(ev, "Size");

                string key = $"{ev.ThreadID}-{SafeStr(ev, "IrpPtr")}";

                bool isRead   = evName.Contains("Read",   StringComparison.OrdinalIgnoreCase);
                bool isWrite  = evName.Contains("Write",  StringComparison.OrdinalIgnoreCase);
                bool isCreate = evName.Contains("Create", StringComparison.OrdinalIgnoreCase);
                bool isClose  = evName.Contains("Close",  StringComparison.OrdinalIgnoreCase);

                // Attempt to pair Begin→End for latency measurement
                if (!isRead && !isWrite) { if (isCreate || isClose) other++; continue; }

                string opType = isRead ? "Read" : "Write";

                // Use elapsed field if present (some providers directly emit ElapsedTime)
                double elapsed = SafeDouble(ev, "ElapsedTime");
                if (elapsed > 0)
                {
                    if (isRead)  { reads++;  readBytes  += bytes; }
                    else         { writes++; writeBytes += bytes; }

                    if (!byFile.TryGetValue(filePath, out var acc))
                        byFile[filePath] = acc = new FileAcc();
                    if (isRead) { acc.Reads++; acc.ReadBytes += bytes; acc.TotalMs += elapsed; }
                    else        { acc.Writes++; acc.WriteBytes += bytes; acc.TotalMs += elapsed; }

                    if (elapsed >= SlowIoMs)
                        slowOps.Add(new FileIoSlowOp(filePath, opType, elapsed, bytes,
                            ev.TimeStampRelativeMSec));

                    int bucket = (int)(ev.TimeStampRelativeMSec / 1000.0);
                    perSecond.TryGetValue(bucket, out long bv);
                    perSecond[bucket] = bv + bytes;
                }
                else
                {
                    // Half-duplex: track start/stop pairs
                    bool isEnd = evName.Contains("End",  StringComparison.OrdinalIgnoreCase) ||
                                 evName.Contains("Stop", StringComparison.OrdinalIgnoreCase);
                    if (!isEnd)
                    {
                        pending[key] = (ev.TimeStampRelativeMSec, opType, filePath);
                    }
                    else if (pending.TryGetValue(key, out var startInfo))
                    {
                        pending.Remove(key);
                        double ms = ev.TimeStampRelativeMSec - startInfo.StartMs;
                        string fp = startInfo.File.Length > 1 ? startInfo.File : filePath;

                        if (startInfo.Op == "Read") { reads++;  readBytes  += bytes; }
                        else                         { writes++; writeBytes += bytes; }

                        if (!byFile.TryGetValue(fp, out var acc))
                            byFile[fp] = acc = new FileAcc();
                        if (startInfo.Op == "Read") { acc.Reads++; acc.ReadBytes += bytes; acc.TotalMs += ms; }
                        else                         { acc.Writes++; acc.WriteBytes += bytes; acc.TotalMs += ms; }

                        if (ms >= SlowIoMs)
                            slowOps.Add(new FileIoSlowOp(fp, startInfo.Op, ms, bytes,
                                startInfo.StartMs));

                        int bucket = (int)(ev.TimeStampRelativeMSec / 1000.0);
                        perSecond.TryGetValue(bucket, out long bv);
                        perSecond[bucket] = bv + bytes;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            return new FileIoTraceData($"Failed: {ex.Message}", processFilter,
                0, 0, 0, 0, 0, 0, [], [], null, false);
        }

        if (reads == 0 && writes == 0)
        {
            bool isNetTrace = traceFileName.EndsWith(".nettrace", StringComparison.OrdinalIgnoreCase);
            string guidance = isNetTrace
                ? "Kernel file I/O events are only available in ETL traces (not .nettrace). " +
                  "Collect with PerfView or 'dotnet-trace collect --format netperf' on Windows with Kernel providers."
                : "No file I/O events found. Collect with the Microsoft-Windows-Kernel-File provider:\n" +
                  "  PerfView: enable 'FileIOReadWrite' in collection dialog\n" +
                  "  xperf: -on PROC_THREAD+LOADER+FileIO";
            return new FileIoTraceData(
                $"{traceFileName}  |  0 file I/O events",
                processFilter, 0, 0, 0, 0, 0, 0, [], [], null, false);
        }

        var topFiles = byFile
            .OrderByDescending(kv => kv.Value.ReadBytes + kv.Value.WriteBytes)
            .Take(top)
            .Select(kv => new FileIoFileSummary(kv.Key,
                kv.Value.Reads, kv.Value.Writes,
                kv.Value.ReadBytes + kv.Value.WriteBytes, kv.Value.TotalMs))
            .ToList();

        var topSlow = slowOps.OrderByDescending(o => o.DurationMs).Take(top).ToList();

        IReadOnlyList<double>? timeline = null;
        if (perSecond.Count > 1)
        {
            int minB = perSecond.Keys.Min(), maxB = perSecond.Keys.Max();
            var tl = new double[maxB - minB + 1];
            foreach (var kv in perSecond) tl[kv.Key - minB] = kv.Value / 1024.0; // KB/s
            timeline = tl;
        }

        string info = $"{traceFileName}" +
                      (processFilter is not null ? $"  |  process: {processFilter}" : "") +
                      $"  |  {reads:N0} reads  •  {writes:N0} writes  •  {topSlow.Count} slow ops";

        return new FileIoTraceData(info, processFilter,
            reads, writes, other, readBytes, writeBytes, topSlow.Count,
            topSlow, topFiles, timeline, HasData: true);
    }

    private static string SafeStr(TraceEvent ev, string field)
    {
        try { return ev.PayloadByName(field)?.ToString() ?? ""; } catch { return ""; }
    }

    private static long SafeLong(TraceEvent ev, string field)
    {
        try { return (long)Convert.ChangeType(ev.PayloadByName(field), typeof(long)); } catch { return 0; }
    }

    private static double SafeDouble(TraceEvent ev, string field)
    {
        try { return (double)Convert.ChangeType(ev.PayloadByName(field), typeof(double)); } catch { return 0; }
    }

    private sealed class FileAcc
    {
        public int Reads, Writes;
        public long ReadBytes, WriteBytes;
        public double TotalMs;
    }
}
