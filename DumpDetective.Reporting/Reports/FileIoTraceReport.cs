using DumpDetective.Core.Interfaces;
using DumpDetective.Core.Models.CommandData;

namespace DumpDetective.Reporting.Reports;

public sealed class FileIoTraceReport
{
    public void Render(FileIoTraceData data, IRenderSink sink, int top = 20)
    {
        sink.Explain(
            what: "File I/O analysis from kernel FileIO events — identifies slow synchronous file reads and writes, high-throughput files, and I/O latency hot spots.",
            why: "Synchronous file I/O on the thread pool blocks threads during disk wait, directly reducing available concurrency for request handling.",
            impact: "A single synchronous file read waiting 50 ms on a cold disk can hold a ThreadPool thread for that entire duration, starving other work.",
            bullets: [
                "Slow operations     — individual reads/writes exceeding 10 ms",
                "Top files by bytes  — files with highest total I/O throughput",
                "Byte timeline       — KB/s of file I/O activity over time"
            ],
            action: "Replace synchronous File.ReadAllBytes/WriteAllBytes with async equivalents. " +
                    "Use FileOptions.Asynchronous when opening FileStream for async I/O. " +
                    "Consider memory-mapped files for random-access read patterns."
        );

        sink.Section("Trace Summary", "fileio-summary");
        sink.KeyValues([
            ("Trace",             data.TraceInfo),
            ("Total reads",       data.TotalReads.ToString("N0")),
            ("Total writes",      data.TotalWrites.ToString("N0")),
            ("Total read bytes",  $"{data.TotalReadBytes / 1024:N0} KB"),
            ("Total write bytes", $"{data.TotalWriteBytes / 1024:N0} KB"),
            ("Slow ops (>10ms)",  data.SlowOpCount.ToString("N0")),
            ("Process filter",    data.FilteredProcess ?? "(all processes)"),
        ]);

        if (!data.HasData)
        {
            sink.Alert(AlertLevel.Info, "No file I/O events found.",
                "File I/O events require kernel provider (ETL traces only, not .nettrace).",
                "PerfView: enable 'FileIOReadWrite' in collection. " +
                "xperf: -on PROC_THREAD+LOADER+FileIO");
            return;
        }

        if (data.SlowOpCount > 10)
            sink.Alert(AlertLevel.Warning,
                $"{data.SlowOpCount} slow file I/O operation(s) detected (>10 ms).",
                "Slow synchronous I/O blocks thread pool threads and reduces request throughput.");

        if (data.BytesTimeline is { Count: > 2 } tl)
            sink.Sparkline(tl, "File I/O throughput over time (KB/s)", " KB/s");

        if (data.SlowOps.Count > 0)
        {
            sink.Section("Slowest File Operations", "fileio-slow");
            var rows = new List<string[]>(Math.Min(top, data.SlowOps.Count));
            foreach (var op in data.SlowOps.Take(top))
                rows.Add([op.FilePath.Length > 60 ? "…" + op.FilePath[^57..] : op.FilePath,
                           op.OperationType, $"{op.DurationMs:F1} ms",
                           $"{op.Bytes / 1024:N0} KB", $"{op.TimeMs:F0} ms"]);
            sink.Table(
                ["File", "Op", "Duration", "Size", "Start Offset"],
                rows, "Slowest operations ordered by duration");
        }

        if (data.TopFiles.Count > 0)
        {
            sink.Section("Top Files by Total Bytes", "fileio-topfiles");
            var rows = new List<string[]>(Math.Min(top, data.TopFiles.Count));
            foreach (var f in data.TopFiles.Take(top))
                rows.Add([f.FilePath.Length > 60 ? "…" + f.FilePath[^57..] : f.FilePath,
                           f.ReadCount.ToString("N0"), f.WriteCount.ToString("N0"),
                           $"{f.TotalBytes / 1024:N0} KB", $"{f.TotalMs:F1} ms"]);
            sink.Table(
                ["File", "Reads", "Writes", "Total Bytes", "Total Time"],
                rows, "Files ordered by total bytes transferred");
        }
    }
}
