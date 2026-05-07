using DumpDetective.Core.Interfaces;
using DumpDetective.Core.Models;
using DumpDetective.Core.Models.CommandData;
using DumpDetective.Core.Utilities;

namespace DumpDetective.Reporting.Reports;

public sealed class JitTraceReport
{
    public void Render(JitTraceData data, IRenderSink sink, int top = 30)
    {
        sink.Explain(
            what: "JIT compilation trace analysis — which methods were compiled at runtime, how long JIT took, and which modules drove the most compilation.",
            why: "Every method in .NET is compiled from IL to native code the first time it is called. Large JIT workloads slow startup, and repeated compilations of dynamic methods (emit, Expression.Compile) create continuous overhead.",
            impact: "High total JIT time → slow cold-start or warmup period. Modules with many methods → consider ReadyToRun (R2R) pre-compilation. Dynamic/emit methods JIT-compiled repeatedly → allocation and CPU waste.",
            bullets: [
                "Total JIT time — wall-clock cost of compilation during the trace",
                "Methods by time — largest individual compilation cost (complex generics, large methods)",
                "Methods by count — repeated compilations (dynamic methods, Expression.Compile, re-JIT)",
                "Top modules — which assemblies drove the most JIT activity"
            ],
            action: "Use ReadyToRun (publish with -r <rid>) to pre-compile hot assemblies. Avoid Expression.Compile or Emit in hot paths — compile once and cache. Profile startup with 'dotnet-trace --profile startup' for JIT breakdown."
        );

        sink.Section("JIT Summary", "jit-summary");
        sink.KeyValues([
            ("Trace",                data.TraceInfo),
            ("Process filter",       data.FilteredProcess ?? "(all processes)"),
            ("Methods JIT-compiled", data.TotalMethodsJitted.ToString("N0")),
            ("Unique methods",       data.TopByTime.Count.ToString()),
            ("Total JIT time",       data.TimingAvailable ? $"{data.TotalJitTimeMs:F1} ms" : "N/A (no timing events)"),
            ("Max single JIT",       data.TimingAvailable ? $"{data.MaxJitTimeMs:F1} ms" : "N/A"),
            ("Avg JIT time",         data.TimingAvailable ? $"{data.AvgJitTimeMs:F2} ms" : "N/A"),
        ]);

        if (data.TotalMethodsJitted == 0)
        {
            sink.Alert(AlertLevel.Warning, "No JIT events found in trace.",
                "To capture JIT events, re-collect with one of the following:\n\n" +
                "dotnet-trace:\n" +
                "  dotnet-trace collect --profile startup\n" +
                "  dotnet-trace collect --providers 'Microsoft-Windows-DotNETRuntime:0x10:5'\n\n" +
                "PerfView (ClrEvents keyword value includes JIT-related flags):\n" +
                "  PerfView.exe /ClrEvents:JITSymbols,Compilation,JitTracing,Default /JITInlining /NoGui collect");
            return;
        }

        if (!data.TimingAvailable)
        {
            sink.Alert(AlertLevel.Info,
                "JIT method counts available but per-method timing is missing.",
                "Timing requires verbose method events (MethodLoadVerbose).\n\n" +
                "dotnet-trace:\n" +
                "  dotnet-trace collect --providers 'Microsoft-Windows-DotNETRuntime:0x10:5'\n\n" +
                "PerfView:\n" +
                "  PerfView.exe /ClrEvents:JITSymbols,Compilation,JitTracing,Default /JITInlining /NoGui collect\n\n" +
                "Counts below still show which modules were JIT-compiled.");
        }

        // ── Top by time ───────────────────────────────────────────────────────
        if (data.TopByTime.Count > 0)
        {
            sink.Section("Slowest Methods to JIT", "jit-by-time");

            // Stacked bar: JIT time by module
            var moduleSegs = data.TopModules
                .Where(m => m.TotalJitTimeMs > 0)
                .Take(8)
                .Select(m => (Label: TrimModule(m.ModuleName, 30), Value: m.TotalJitTimeMs))
                .ToList();
            if (moduleSegs.Count > 0)
                sink.StackedBar(moduleSegs, " ms", "Total JIT time by module");

            var rows = data.TopByTime.Take(top).Select(m => new[]
            {
                TraceReportHelpers.CleanIlMethod(m.MethodName),
                TrimModule(m.ModuleName, 30),
                $"{m.TotalJitTimeMs:F1} ms",
                m.MaxJitTimeMs > 0 ? $"{m.MaxJitTimeMs:F1} ms" : "—",
                m.CompileCount.ToString("N0"),
                m.ILSize > 0 ? $"{m.ILSize:N0} B" : "—",
                m.NativeSize > 0 ? $"{m.NativeSize:N0} B" : "—",
            }).ToList();
            sink.Table(
                ["Method", "Module", "Total JIT time", "Max JIT time", "Compile count", "IL size", "Native size"],
                rows,
                $"Top {rows.Count} methods by JIT compile time");

            // Alert on very slow individual JITs
            if (data.MaxJitTimeMs > 500)
                sink.Alert(AlertLevel.Warning,
                    $"Single method took {data.MaxJitTimeMs:F1} ms to JIT-compile.",
                    "Methods taking > 500 ms are typically very large or have extreme generic complexity. " +
                    "Consider splitting the method or simplifying generic constraints.");
        }

        // ── Top by count ───────────────────────────────────────────────────────
        if (data.TopByCount.Count > 0)
        {
            sink.Section("Most Frequently JIT-Compiled Methods", "jit-by-count");

            var rows = data.TopByCount.Take(top)
                .Where(m => m.CompileCount > 1)  // skip single-compile entries — only interesting if repeated
                .Select(m => new[]
                {
                    TraceReportHelpers.CleanIlMethod(m.MethodName),
                    TrimModule(m.ModuleName, 30),
                    m.CompileCount.ToString("N0"),
                    m.TotalJitTimeMs > 0 ? $"{m.TotalJitTimeMs:F1} ms" : "—",
                }).ToList();

            if (rows.Count > 0)
            {
                sink.Table(
                    ["Method", "Module", "Compile count", "Total JIT time"],
                    rows,
                    "Methods compiled more than once — dynamic methods, Expression.Compile, or ReJIT");

                var maxCount = data.TopByCount.Max(m => m.CompileCount);
                if (maxCount >= 10)
                    sink.Alert(AlertLevel.Warning,
                        $"Method compiled {maxCount:N0} times — likely Expression.Compile or Emit in a hot path.",
                        "Repeated dynamic compilation creates GC pressure and CPU overhead. " +
                        "Cache compiled delegates/expressions at application startup.");
            }
            else
            {
                sink.Alert(AlertLevel.Info, "All methods compiled exactly once — no dynamic/repeated JIT activity detected.");
            }
        }

        // ── Module summary ────────────────────────────────────────────────────
        if (data.TopModules.Count > 0)
        {
            sink.Section("JIT Activity by Module", "jit-modules");

            // Donut: methods compiled by module
            var modDonutSegs = data.TopModules.Take(8)
                .Select(m => (Label: TrimModule(m.ModuleName, 30), Value: (double)m.MethodCount))
                .ToList();
            if (modDonutSegs.Count > 0)
                sink.DonutChart(modDonutSegs, "Methods JIT-compiled by module",
                    $"{data.TotalMethodsJitted:N0}\ntotal");

            var modRows = data.TopModules.Take(20).Select(m => new[]
            {
                TrimModule(m.ModuleName, 50),
                m.MethodCount.ToString("N0"),
                m.TotalJitTimeMs > 0 ? $"{m.TotalJitTimeMs:F1} ms" : "—",
            }).ToList();
            sink.Table(
                ["Module", "Methods compiled", "Total JIT time"],
                modRows,
                "Modules with the highest JIT compilation load");

            // Suggest ReadyToRun for modules with many methods
            var heavyModule = data.TopModules.FirstOrDefault(m => m.MethodCount >= 100);
            if (heavyModule is not null)
                sink.Alert(AlertLevel.Info,
                    $"'{heavyModule.ModuleName}' had {heavyModule.MethodCount:N0} methods JIT-compiled.",
                    "Consider publishing with ReadyToRun (dotnet publish -r <rid> --no-self-contained) " +
                    "or using NativeAOT to pre-compile to native code and eliminate startup JIT cost.");
        }
    }

    private static string TrimModule(string s, int max) =>
        s.Length <= max ? s : "…" + s[^(max - 1)..];
}
