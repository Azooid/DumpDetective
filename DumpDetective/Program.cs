using System.Text;
using DumpDetective.Commands;

Console.OutputEncoding = Encoding.UTF8;

Environment.SetEnvironmentVariable("DD_DUMP", @"D:\Dump\WCF_dump_final\senerio2_dump\w3wp.exe__BALLOADTEST__PID__4672__Date__04_06_2026__Time_06_43_20PM__186__Manual Dump.dmp");

if (args.Length == 0 || args[0] is "--help" or "-h")
{
    PrintHelp();
    return 0;
}

var commandArgs = InjectDumpPath(args[1..]);

return args[0] switch
{
    "event-analysis"     => EventAnalysisCommand.Run(commandArgs),
    "heap-stats"         => HeapStatsCommand.Run(commandArgs),
    "large-objects"      => LargeObjectsCommand.Run(commandArgs),
    "string-duplicates"  => StringDuplicatesCommand.Run(commandArgs),
    "thread-analysis"    => ThreadAnalysisCommand.Run(commandArgs),
    "deadlock-detection" => DeadlockDetectionCommand.Run(commandArgs),
    "exception-analysis" => ExceptionAnalysisCommand.Run(commandArgs),
    "gc-roots"           => GcRootsCommand.Run(commandArgs),
    "static-refs"        => StaticRefsCommand.Run(commandArgs),
    "http-requests"      => HttpRequestsCommand.Run(commandArgs),
    "timer-leaks"        => TimerLeaksCommand.Run(commandArgs),
    "finalizer-queue"    => FinalizerQueueCommand.Run(commandArgs),
    "handle-table"       => HandleTableCommand.Run(commandArgs),
    "pinned-objects"     => PinnedObjectsCommand.Run(commandArgs),
    "gen-summary"        => GenSummaryCommand.Run(commandArgs),
    "heap-fragmentation" => HeapFragmentationCommand.Run(commandArgs),
    "async-stacks"       => AsyncStacksCommand.Run(commandArgs),
    "thread-pool"        => ThreadPoolCommand.Run(commandArgs),
    "object-inspect"     => ObjectInspectCommand.Run(commandArgs),
    "type-instances"     => TypeInstancesCommand.Run(commandArgs),
    "weak-refs"          => WeakRefsCommand.Run(commandArgs),
    "wcf-channels"       => WcfChannelsCommand.Run(commandArgs),
    "connection-pool"    => ConnectionPoolCommand.Run(commandArgs),
    "high-refs"          => HighRefsCommand.Run(commandArgs),
    "module-list"        => ModuleListCommand.Run(commandArgs),
    "analyze"            => AnalyzeCommand.Run(commandArgs),
    "trend-analysis"     => TrendAnalysisCommand.Run(commandArgs),
    _                    => UnknownCommand(args[0])
};

static void PrintHelp()
{
    Console.WriteLine("DumpDetective  —  .NET memory dump analysis tool");
    Console.WriteLine();
    Console.WriteLine("  DumpDetective <command> [options]");
    Console.WriteLine();
    Console.WriteLine("Commands:");
    Console.WriteLine("  event-analysis     Detect event handler leaks");
    Console.WriteLine("  heap-stats         Heap object counts and sizes by type");
    Console.WriteLine("  large-objects      Large objects on LOH / POH / Gen heap");
    Console.WriteLine("  string-duplicates  Find duplicate strings and wasted memory");
    Console.WriteLine("  thread-analysis    Thread states, blocking objects, stack traces");
    Console.WriteLine("  deadlock-detection Detect deadlock cycles in the wait graph");
    Console.WriteLine("  exception-analysis Exception objects on heap and active threads");
    Console.WriteLine("  gc-roots           GC roots and referrers for a given type");
    Console.WriteLine("  static-refs        Non-null static reference fields");
    Console.WriteLine("  http-requests      In-flight HTTP request objects");
    Console.WriteLine("  timer-leaks        Timer objects and their callback targets");
    Console.WriteLine("  finalizer-queue    Objects waiting in the finalizer queue");
    Console.WriteLine("  handle-table       GC handles grouped by kind");
    Console.WriteLine("  pinned-objects     Pinned GC handles causing heap fragmentation");
    Console.WriteLine("  gen-summary        Object counts and sizes by GC generation");
    Console.WriteLine("  heap-fragmentation Segment free space and fragmentation percentage");
    Console.WriteLine("  async-stacks       Suspended async state machines at await points");
    Console.WriteLine("  thread-pool        ThreadPool state and queued work items");
    Console.WriteLine("  object-inspect     Dump all field values of an object by address");
    Console.WriteLine("  type-instances     List all instances of a given type");
    Console.WriteLine("  weak-refs          WeakReference handles — alive vs collected");
    Console.WriteLine("  wcf-channels       WCF service/channel objects and their state");
    Console.WriteLine("  connection-pool    Database connection objects and leak detection");
    Console.WriteLine("  high-refs          Highly-referenced \"hub\" objects — retention, caches, shared state");
    Console.WriteLine("  module-list        Loaded assemblies with path and size");
    Console.WriteLine("  analyze            Full scored health report for a single dump");
    Console.WriteLine("  trend-analysis     Trend report across multiple dumps");
    Console.WriteLine();
    Console.WriteLine("Run 'DumpDetective <command> --help' for command-specific help.");
    Console.WriteLine();
    Console.WriteLine("Env vars:");
    Console.WriteLine("  DD_DUMP   Default dump file path used when none is given on the command line");
}

static string[] InjectDumpPath(string[] commandArgs)
{
    if (commandArgs.Any(IsDumpArg)) return commandArgs;
    var envDump = Environment.GetEnvironmentVariable("DD_DUMP");
    return envDump is not null ? [envDump, ..commandArgs] : commandArgs;
}

static bool IsDumpArg(string a) =>
    !a.StartsWith('-') &&
    (a.EndsWith(".dmp",  StringComparison.OrdinalIgnoreCase) ||
     a.EndsWith(".mdmp", StringComparison.OrdinalIgnoreCase));

static int UnknownCommand(string name)
{
    Console.Error.WriteLine($"Unknown command: '{name}'");
    Console.Error.WriteLine("Run 'DumpDetective --help' to see available commands.");
    return 1;
}

