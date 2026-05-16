using DumpDetective.Core.Interfaces;
using DumpDetective.Core.Models.CommandData;
using DumpDetective.Core.Utilities;

namespace DumpDetective.Reporting.Reports;

public sealed class NativeInteropReport
{
    public void Render(NativeInteropData data, IRenderSink sink)
    {
        sink.Section("Summary");
        sink.Explain(
            what: "Identifies managed threads currently executing inside native/runtime frames — " +
                  "P/Invoke transitions, CLR helper stubs, GC coordination frames, and interop call sites.",
            why:  "Threads blocked at the same native call site share a common native bottleneck: " +
                  "a file, pipe, socket, mutex, or native library call that is not returning promptly.",
            impact: "Threads in native code cannot be interrupted by managed synchronization primitives. " +
                    "They hold their managed locks, inflate thread pool usage, and may not participate " +
                    "correctly in GC suspension.",
            action: "Focus on the top native call sites that appear across many threads. " +
                    "Compare the surrounding managed frames to identify which managed code is driving the call."
        );

        sink.KeyValues([
            ("Threads with native frames",  data.TotalThreadsWithNativeFrames.ToString("N0")),
            ("Distinct native call sites",  data.TopNativeCallSites.Count.ToString("N0")),
        ]);

        if (data.TotalThreadsWithNativeFrames == 0)
        {
            sink.Alert(AlertLevel.Info,
                "No threads with native/runtime frames found.",
                "All threads are executing in managed code at this point in time.");
            return;
        }

        if (data.TotalThreadsWithNativeFrames > 20)
            sink.Alert(AlertLevel.Warning,
                $"{data.TotalThreadsWithNativeFrames} threads are currently in native frames.",
                "A large number of threads simultaneously in native code indicates a native-level bottleneck.",
                "Review the top native call sites below and identify the shared native resource being contested.");

        // ── Top native call sites ─────────────────────────────────────────────
        sink.Section("Top Native Call Sites");
        sink.Table(
            ["Native Frame", "Thread Count"],
            data.TopNativeCallSites
                .Select(f => new[] { DumpHelpers.SanitizeFrame(f.FrameName), f.ThreadCount.ToString("N0") })
                .ToList(),
            $"{data.TopNativeCallSites.Count} distinct call site(s) across {data.TotalThreadsWithNativeFrames} thread(s)");

        // ── Per-thread detail ─────────────────────────────────────────────────
        sink.Section("Threads With Native Frames");
        foreach (var thread in data.Threads.Take(50))
        {
            string summary = $"T{thread.ManagedId} ({thread.Category})  OS:{thread.OSThreadId}  —  " +
                             $"{thread.NativeFrameCount} native frame(s)  ·  {DumpHelpers.SanitizeFrame(thread.DeepestNativeFrame)}";
            sink.BeginDetails(summary, open: false);
            sink.Table(
                ["Kind", "Frame"],
                thread.Frames
                    .Select(f => new[] { f.IsNative ? "NATIVE" : "managed", DumpHelpers.SanitizeFrame(f.FrameName) })
                    .ToList());
            sink.EndDetails();
        }

        if (data.Threads.Count > 50)
            sink.Text($"… and {data.Threads.Count - 50} more thread(s) with native frames (truncated for brevity).");
    }
}
