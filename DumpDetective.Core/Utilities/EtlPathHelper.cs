namespace DumpDetective.Core.Utilities;

/// <summary>
/// Resolves unmerged ETL companion-file paths to the base ETL path that
/// <c>TraceLog.OpenOrConvert</c> expects.
///
/// <para>
/// PerfView / xperf produce a set of companion files alongside the main ETL:
/// <code>
///   HighCPU_01.etl            ← base — pass this to OpenOrConvert
///   HighCPU_01.kernel.etl     ← kernel events  (auto-merged by TraceEvent)
///   HighCPU_01.clrRundown.etl ← CLR rundown    (auto-merged by TraceEvent)
/// </code>
/// When the user passes a companion file, this helper normalises the path back
/// to the base so that <c>TraceLog.OpenOrConvert</c> can auto-detect and merge
/// all companions in the same directory.
/// </para>
/// </summary>
public static class EtlPathHelper
{
    // Known PerfView / xperf companion suffixes (the segment inserted before .etl).
    // Listed longest-first to avoid prefix ambiguity.
    private static readonly string[] s_companionSegments =
    [
        ".clrRundown", ".clrNgenPdbs", ".kernel", ".heap",
        ".user_relogger", ".merged",
    ];

    /// <summary>
    /// If <paramref name="path"/> points to a companion ETL file
    /// (e.g. <c>trace.kernel.etl</c>, <c>trace.clrRundown.etl</c>),
    /// returns the corresponding base ETL path (<c>trace.etl</c>) when it
    /// exists on disk. Returns the original path unchanged when it is already
    /// the base, does not end with <c>.etl</c>, or the base cannot be found.
    /// </summary>
    public static string ResolveToBase(string path)
    {
        if (!path.EndsWith(".etl", StringComparison.OrdinalIgnoreCase))
            return path;  // .nettrace or unknown — leave as-is

        foreach (var seg in s_companionSegments)
        {
            string companionTail = seg + ".etl";
            if (path.EndsWith(companionTail, StringComparison.OrdinalIgnoreCase))
            {
                string basePath = path[..^companionTail.Length] + ".etl";
                if (File.Exists(basePath))
                    return basePath;
            }
        }

        return path;
    }

    /// <summary>
    /// Returns the file names (not full paths) of companion ETL files found in
    /// the same directory as <paramref name="basePath"/>.
    /// Returns an empty list for non-ETL paths or when no companions exist.
    /// </summary>
    public static IReadOnlyList<string> FindCompanionNames(string basePath)
    {
        if (!basePath.EndsWith(".etl", StringComparison.OrdinalIgnoreCase))
            return [];

        string dir  = Path.GetDirectoryName(Path.GetFullPath(basePath)) ?? ".";
        string stem = Path.GetFileNameWithoutExtension(basePath);

        var result = new List<string>(s_companionSegments.Length);
        foreach (var seg in s_companionSegments)
        {
            string name = stem + seg + ".etl";
            if (File.Exists(Path.Combine(dir, name)))
                result.Add(name);
        }

        return result;
    }
}
