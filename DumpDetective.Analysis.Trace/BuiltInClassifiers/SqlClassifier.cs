using DumpDetective.Core.Tracing;
using static DumpDetective.Core.Tracing.TraceEventKind;
using static DumpDetective.Analysis.Trace.BuiltInClassifiers.ClassifierMatch;

namespace DumpDetective.Analysis.Trace.BuiltInClassifiers;

/// <summary>
/// Classifies ADO.NET and SQL client command/connection events from:
///   Microsoft-AdoNet-SystemData, System.Data.SqlClient.EventSource,
///   Microsoft.Data.SqlClient.EventSource.
/// </summary>
internal sealed class SqlClassifier : IProviderScopedClassifier
{
    public IReadOnlyList<string> Providers { get; } =
    [
        "Microsoft-AdoNet-SystemData",
        "System.Data.SqlClient.EventSource",
        "Microsoft.Data.SqlClient.EventSource",
    ];

    public TraceEventKind Classify(string provider, string name)
    {
        // ADO.NET provider-level events (Microsoft-AdoNet-SystemData style)
        if (Contains(name, "AdoNet") || Contains(name, "SystemData") || Contains(name, "Ado-Net"))
        {
            if (Contains(name, "BeginExecute")) return SqlCommandStart;
            if (Contains(name, "EndExecute"))   return SqlCommandStop;
        }

        // SqlCommand / CommandExecut* patterns
        if (Contains(name, "SqlCommand") || Contains(name, "CommandExecut"))
        {
            if (Contains(name, "Start") || Contains(name, "Begin") || Contains(name, "Executing")) return SqlCommandStart;
            if (Contains(name, "Stop")  || Contains(name, "End")   || Contains(name, "Executed") ||
                Contains(name, "Error"))                                                             return SqlCommandStop;
        }

        // SqlConnection patterns
        if (Contains(name, "SqlConnection"))
        {
            if (Contains(name, "Open"))  return SqlConnectionOpen;
            if (Contains(name, "Close")) return SqlConnectionClose;
        }

        // Bare BeginExecute / EndExecute (emitted by Microsoft-AdoNet-SystemData as unqualified names)
        if (Contains(name, "BeginExecute")) return SqlCommandStart;
        if (Contains(name, "EndExecute"))   return SqlCommandStop;

        return Unknown;
    }
}
