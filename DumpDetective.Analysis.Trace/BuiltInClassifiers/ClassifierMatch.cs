namespace DumpDetective.Analysis.Trace.BuiltInClassifiers;

/// <summary>Shared OrdinalIgnoreCase helpers used by every built-in classifier.</summary>
internal static class ClassifierMatch
{
    internal static bool Contains(string s, string v)  => s.Contains(v,  StringComparison.OrdinalIgnoreCase);
    internal static bool EndsWith(string s,  string v) => s.EndsWith(v,  StringComparison.OrdinalIgnoreCase);
    internal static bool StartsWith(string s, string v) => s.StartsWith(v, StringComparison.OrdinalIgnoreCase);
}
