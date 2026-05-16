using DumpDetective.Core.Models.CommandData;
using DumpDetective.Core.Runtime;

namespace DumpDetective.Analysis.Memory.Analyzers;

/// <summary>
/// Classifies the workload kind of a process from its loaded modules and thread names.
/// The classifier uses a scored-signal approach: each detected module/type adds points
/// to one or more WorkloadKind buckets, and the highest-scoring bucket wins.
/// </summary>
public sealed class WorkloadProfileClassifier
{
    public WorkloadProfileData Classify(DumpContext ctx)
    {
        if (ctx.GetAnalysis<WorkloadProfileData>() is { } cached) return cached;

        var signals  = new List<string>();
        var scores   = new Dictionary<WorkloadKind, int>();

        void Add(WorkloadKind k, int pts, string signal)
        {
            scores[k] = scores.GetValueOrDefault(k) + pts;
            if (!signals.Contains(signal)) signals.Add(signal);
        }

        // ── Module signals ────────────────────────────────────────────────
        var moduleNames = ctx.Runtime.AppDomains
            .SelectMany(d => d.Modules)
            .Select(m => System.IO.Path.GetFileNameWithoutExtension(m.Name ?? ""))
            .Where(n => n.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // ASP.NET Core
        if (moduleNames.Contains("Microsoft.AspNetCore.Hosting"))
        { Add(WorkloadKind.AspNetCore, 50, "Module: Microsoft.AspNetCore.Hosting"); }
        if (moduleNames.Contains("Microsoft.AspNetCore.Server.Kestrel.Core"))
        { Add(WorkloadKind.AspNetCore, 30, "Module: Kestrel"); }
        if (moduleNames.Contains("Microsoft.AspNetCore.Mvc.Core"))
        { Add(WorkloadKind.AspNetCore, 20, "Module: ASP.NET Core MVC"); }

        // gRPC
        if (moduleNames.Contains("Grpc.AspNetCore.Server") || moduleNames.Contains("Grpc.Core"))
        { Add(WorkloadKind.GrpcService, 50, "Module: gRPC"); Add(WorkloadKind.AspNetCore, 15, "Module: gRPC (partial ASP.NET)"); }

        // Background Worker / Hosted Service
        if (moduleNames.Contains("Microsoft.Extensions.Hosting"))
        { Add(WorkloadKind.BackgroundWorker, 30, "Module: Microsoft.Extensions.Hosting"); }
        if (moduleNames.Contains("Microsoft.Extensions.Hosting.Abstractions"))
        { Add(WorkloadKind.BackgroundWorker, 20, "Module: IHostedService abstractions"); }

        // Data Service (heavy DB usage)
        if (moduleNames.Contains("Microsoft.EntityFrameworkCore"))
        { Add(WorkloadKind.DataService, 40, "Module: EF Core"); }
        if (moduleNames.Contains("Microsoft.Data.SqlClient") || moduleNames.Contains("System.Data.SqlClient"))
        { Add(WorkloadKind.DataService, 30, "Module: SqlClient"); }
        if (moduleNames.Contains("Dapper"))
        { Add(WorkloadKind.DataService, 30, "Module: Dapper"); }

        // Orleans
        if (moduleNames.Any(n => n.StartsWith("Orleans.", StringComparison.OrdinalIgnoreCase)))
        { Add(WorkloadKind.Orleans, 60, "Module: Orleans (actor framework)"); }

        // WinForms
        if (moduleNames.Contains("System.Windows.Forms"))
        { Add(WorkloadKind.WinForms, 60, "Module: System.Windows.Forms"); }

        // WPF
        if (moduleNames.Contains("PresentationFramework") || moduleNames.Contains("PresentationCore"))
        { Add(WorkloadKind.Wpf, 60, "Module: WPF PresentationFramework"); }

        // ── Thread name signals ──────────────────────────────────────────
        // Thread names are stored on the heap Thread object, not on ClrThread directly.
        // Re-use the cached ThreadNameMap if available (built by ThreadAnalysisAnalyzer),
        // otherwise skip — thread-name signals are minor contributors (≤ 20 pts).
        var threadNameMap = ctx.GetAnalysis<DumpDetective.Core.Runtime.ThreadNameMap>();
        if (threadNameMap is not null)
        {
            var threadNames = threadNameMap.Values.ToList();

            if (threadNames.Any(n => n.Contains("Kestrel", StringComparison.OrdinalIgnoreCase) ||
                                      n.Contains("Server", StringComparison.OrdinalIgnoreCase)))
            { Add(WorkloadKind.AspNetCore, 20, "Thread: Kestrel/Server thread name"); }

            if (threadNames.Any(n => n.Contains("Worker", StringComparison.OrdinalIgnoreCase)))
            { Add(WorkloadKind.BackgroundWorker, 15, "Thread: Worker thread name"); }

            if (threadNames.Any(n => n.Contains("Orleans", StringComparison.OrdinalIgnoreCase)))
            { Add(WorkloadKind.Orleans, 20, "Thread: Orleans thread name"); }
        }
        // ── Type presence signals (live heap objects) ────────────────────
        try
        {
            var typeNames = ctx.Heap.EnumerateObjects()
                .Select(o => o.Type?.Name ?? "")
                .Where(n => n.Length > 0)
                .Distinct()
                .Take(500)  // sample first 500 unique types to avoid full walk
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (typeNames.Any(n => n.Contains("HttpContext", StringComparison.OrdinalIgnoreCase)))
            { Add(WorkloadKind.AspNetCore, 25, "Heap type: HttpContext"); }
            if (typeNames.Any(n => n.Contains("IHostedService", StringComparison.OrdinalIgnoreCase)))
            { Add(WorkloadKind.BackgroundWorker, 20, "Heap type: IHostedService"); }
            if (typeNames.Any(n => n.Contains("DbContext", StringComparison.OrdinalIgnoreCase)))
            { Add(WorkloadKind.DataService, 20, "Heap type: DbContext"); }
        }
        catch { /* heap enumeration may fail on corrupted dumps */ }

        // ── Pick winner ───────────────────────────────────────────────────
        if (scores.Count == 0)
        {
            var result = new WorkloadProfileData(WorkloadKind.Unknown, "Unknown", 
                "No recognizable framework modules or thread signals detected.",
                0, [], ["heap-stats", "thread-analysis", "exception-analysis"]);
            ctx.SetAnalysis(result);
            return result;
        }

        var winner = scores.OrderByDescending(kv => kv.Value).First();
        int totalPossible = 100;
        int confidence = Math.Min(winner.Value * 100 / totalPossible, 99);

        var recommendations = winner.Key switch
        {
            WorkloadKind.AspNetCore      => (IReadOnlyList<string>)["http-requests", "thread-analysis", "async-stacks", "connection-pool", "heap-stats"],
            WorkloadKind.GrpcService     => ["thread-analysis", "async-stacks", "connection-pool", "threadpool-starvation", "heap-stats"],
            WorkloadKind.BackgroundWorker=> ["thread-analysis", "async-stacks", "timer-leaks", "memory-leak", "exception-analysis"],
            WorkloadKind.DataService     => ["connection-pool", "wcf-channels", "thread-analysis", "memory-leak", "string-duplicates"],
            WorkloadKind.Orleans         => ["thread-analysis", "async-stacks", "memory-leak", "heap-stats"],
            WorkloadKind.WinForms        => ["thread-analysis", "heap-stats", "finalizer-queue", "handle-table"],
            WorkloadKind.Wpf             => ["thread-analysis", "heap-stats", "finalizer-queue", "handle-table"],
            _                            => ["heap-stats", "thread-analysis", "exception-analysis"],
        };

        string label = winner.Key.ToString();
        string rationale = $"Detected {signals.Count} signal(s) pointing to {label} workload (score: {winner.Value}).";

        var profile = new WorkloadProfileData(winner.Key, label, rationale, confidence, signals, recommendations);
        ctx.SetAnalysis(profile);
        return profile;
    }
}
