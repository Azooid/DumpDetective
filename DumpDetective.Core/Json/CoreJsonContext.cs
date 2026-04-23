using System.Text.Json.Serialization;
using DumpDetective.Core.Models;

namespace DumpDetective.Core.Json;

/// <summary>
/// AOT source-generation context for all types that must be JSON-serialisable.
/// No runtime reflection — required for <c>PublishAot=true</c>.
/// </summary>
[JsonSerializable(typeof(DumpSnapshot))]
[JsonSerializable(typeof(Finding))]
[JsonSerializable(typeof(List<Finding>))]
[JsonSerializable(typeof(ReportDoc))]
[JsonSerializable(typeof(ReportExplain))]
[JsonSerializable(typeof(DumpReportEnvelope))]
[JsonSerializable(typeof(ThresholdConfig))]
[JsonSerializable(typeof(NameCount))]
[JsonSerializable(typeof(TypeStat))]
[JsonSerializable(typeof(EventLeakStat))]
[JsonSerializable(typeof(StringDuplicateStat))]
[JsonSerializable(typeof(RootedHandleStat))]
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
public partial class CoreJsonContext : JsonSerializerContext { }
