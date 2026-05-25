using DumpDetective.Core.Models.CommandData;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;
using static DumpDetective.Core.Tracing.TraceEventKind;


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

    private static readonly Dictionary<string, byte> EvKind = new(StringComparer.OrdinalIgnoreCase);

    private sealed class Consumer(string? processFilter) : ITraceEventConsumer
    {
        private readonly string? _processFilter = processFilter;
        internal readonly Dictionary<string, (double StartMs, string Op, string File)> Pending = new(StringComparer.Ordinal);
        internal readonly Dictionary<string, FileAcc> ByFile = new(StringComparer.OrdinalIgnoreCase);
        internal readonly List<FileIoSlowOp> SlowOps = new();
        internal readonly Dictionary<int, long> PerSecond = new();
        internal int Reads, Writes, Other;
        internal long ReadBytes, WriteBytes;

        public void Consume(TraceEvent ev, in TraceEventMeta meta, string processName, double timestampMs, int threadId)
        {
            if (_processFilter is not null &&
                !processName.Contains(_processFilter, StringComparison.OrdinalIgnoreCase))
                return;

            if (!EvKind.TryGetValue(meta.EventName, out byte kind))
                EvKind[meta.EventName] = kind =
                    meta.EventName.StartsWith("FileIO",   StringComparison.OrdinalIgnoreCase) ? (byte)1 :
                    meta.EventName.StartsWith("File/",    StringComparison.OrdinalIgnoreCase) ? (byte)1 :
                    meta.EventName.Contains("KernelFile", StringComparison.OrdinalIgnoreCase) ? (byte)1 :
                    (byte)0;
            if (kind == 0) return;

            string filePath = SafeStr(ev, "FileName");
            if (filePath.Length == 0) filePath = SafeStr(ev, "OpenPath");
            if (filePath.Length == 0) filePath = "(unknown)";

            long bytes = SafeLong(ev, "IoSize");
            if (bytes <= 0) bytes = SafeLong(ev, "Size");

            string key = $"{threadId}-{SafeStr(ev, "IrpPtr")}";

            bool isRead   = meta.EventName.Contains("Read",   StringComparison.OrdinalIgnoreCase);
            bool isWrite  = meta.EventName.Contains("Write",  StringComparison.OrdinalIgnoreCase);
            bool isCreate = meta.EventName.Contains("Create", StringComparison.OrdinalIgnoreCase);
            bool isClose  = meta.EventName.Contains("Close",  StringComparison.OrdinalIgnoreCase);

            if (!isRead && !isWrite) { if (isCreate || isClose) Other++; return; }

            string opType = isRead ? "Read" : "Write";

            double elapsed = SafeDouble(ev, "ElapsedTime");
            if (elapsed > 0)
            {
                if (isRead)  { Reads++;  ReadBytes  += bytes; }
                else         { Writes++; WriteBytes += bytes; }

                if (!ByFile.TryGetValue(filePath, out var acc))
                    ByFile[filePath] = acc = new FileAcc();
                if (isRead) { acc.Reads++; acc.ReadBytes += bytes; acc.TotalMs += elapsed; }
                else        { acc.Writes++; acc.WriteBytes += bytes; acc.TotalMs += elapsed; }

                if (elapsed >= SlowIoMs)
                    SlowOps.Add(new FileIoSlowOp(filePath, opType, elapsed, bytes,
                        timestampMs));

                int bucket = (int)(timestampMs / 1000.0);
                PerSecond.TryGetValue(bucket, out long bv);
                PerSecond[bucket] = bv + bytes;
            }
            else
            {
                bool isEnd = meta.EventName.Contains("End",  StringComparison.OrdinalIgnoreCase) ||
                             meta.EventName.Contains("Stop", StringComparison.OrdinalIgnoreCase);
                if (!isEnd)
                {
                    Pending[key] = (timestampMs, opType, filePath);
                }
                else if (Pending.TryGetValue(key, out var startInfo))
                {
                    Pending.Remove(key);
                    double ms = timestampMs - startInfo.StartMs;
                    string fp = startInfo.File.Length > 1 ? startInfo.File : filePath;

                    if (startInfo.Op == "Read") { Reads++;  ReadBytes  += bytes; }
                    else                         { Writes++; WriteBytes += bytes; }

                    if (!ByFile.TryGetValue(fp, out var acc))
                        ByFile[fp] = acc = new FileAcc();
                    if (startInfo.Op == "Read") { acc.Reads++; acc.ReadBytes += bytes; acc.TotalMs += ms; }
                    else                         { acc.Writes++; acc.WriteBytes += bytes; acc.TotalMs += ms; }

                    if (ms >= SlowIoMs)
                        SlowOps.Add(new FileIoSlowOp(fp, startInfo.Op, ms, bytes,
                            startInfo.StartMs));

                    int bucket = (int)(timestampMs / 1000.0);
                    PerSecond.TryGetValue(bucket, out long bv);
                    PerSecond[bucket] = bv + bytes;
                }
            }
        }

        public bool WantsEvent(in TraceEventMeta meta)
        {
            if (!EvKind.TryGetValue(meta.EventName, out byte v))
                EvKind[meta.EventName] = v = meta.Kind switch
                {
                    _ when meta.Kind == FileRead || meta.Kind == FileWrite || meta.Kind == FileCreate || meta.Kind == FileClose || meta.Kind == FileFlush => 1,
                    _ when meta.IsKnown                                           => 0,
                    _ => meta.EventName.StartsWith("FileIO",     StringComparison.OrdinalIgnoreCase) ||
                         meta.EventName.StartsWith("File/",      StringComparison.OrdinalIgnoreCase) ||
                         meta.EventName.Contains("KernelFile",   StringComparison.OrdinalIgnoreCase) ? (byte)1
                       : (byte)0
                };
            return v != 0;
        }

        public void OnComplete() { }
    }

    public ITraceEventConsumer CreateConsumer(string? processFilter = null)
        => new Consumer(processFilter);

    public FileIoTraceData BuildResult(ITraceEventConsumer consumer, string traceFileName, int top = 20,
                                        string? processFilter = null)
    {
        var c = (Consumer)consumer;
        if (c.Reads == 0 && c.Writes == 0)
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

        var topFiles = c.ByFile
            .OrderByDescending(kv => kv.Value.ReadBytes + kv.Value.WriteBytes)
            .Take(top)
            .Select(kv => new FileIoFileSummary(kv.Key,
                kv.Value.Reads, kv.Value.Writes,
                kv.Value.ReadBytes + kv.Value.WriteBytes, kv.Value.TotalMs))
            .ToList();

        var topSlow = c.SlowOps.OrderByDescending(o => o.DurationMs).Take(top).ToList();

        IReadOnlyList<double>? timeline = null;
        if (c.PerSecond.Count > 1)
        {
            int minB = c.PerSecond.Keys.Min(), maxB = c.PerSecond.Keys.Max();
            var tl = new double[maxB - minB + 1];
            foreach (var kv in c.PerSecond) tl[kv.Key - minB] = kv.Value / 1024.0; // KB/s
            timeline = tl;
        }

        string info = $"{traceFileName}" +
                      (processFilter is not null ? $"  |  process: {processFilter}" : "") +
                      $"  |  {c.Reads:N0} reads  •  {c.Writes:N0} writes  •  {topSlow.Count} slow ops";

        return new FileIoTraceData(info, processFilter,
            c.Reads, c.Writes, c.Other, c.ReadBytes, c.WriteBytes, topSlow.Count,
            topSlow, topFiles, timeline, HasData: true);
    }

    public FileIoTraceData Analyze(TraceLog trace, string traceFileName, int top = 20,
                                    string? processFilter = null, Action<string>? progress = null)
    {
        try
        {
            var c = CreateConsumer(processFilter);
            TraceEventDispatcher.Dispatch(trace, c,
                progress);
            return BuildResult(c, traceFileName, top, processFilter);
        }
        catch (Exception ex)
        {
            return new FileIoTraceData($"Failed: {ex.Message}", processFilter,
                0, 0, 0, 0, 0, 0, [], [], null, false);
        }
    }


    private sealed class FileAcc
    {
        public int Reads, Writes;
        public long ReadBytes, WriteBytes;
        public double TotalMs;
    }
}
