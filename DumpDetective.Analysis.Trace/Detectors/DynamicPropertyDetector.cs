using DumpDetective.Core.Models;
using DumpDetective.Core.Tracing;

namespace DumpDetective.Analysis.Trace.Detectors;

/// <summary>
/// Detects metadata-driven / dynamic property access systems consuming significant CPU.
/// Common in DevExpress XPO, legacy property grid frameworks, WPF data binding,
/// and custom entity systems that dispatch through GetPropertyValue/SetPropertyValue.
/// </summary>
public sealed class DynamicPropertyDetector : ITracePatternDetector
{
    public string Name => "Dynamic Property Access";
    public double MinInclusivePct => 2.0;

    private static readonly string[] Patterns =
    [
        "GetPropertyValue",
        "SetPropertyValue",
        "ApplyDataTemplate",
        "RuntimeBinderController",
        "DynamicObject.TryGetMember",
        "DynamicObject.TrySetMember",
        "PropertyDescriptor.GetValue",
        "PropertyDescriptor.SetValue",
        "TypeDescriptor.GetProperties",
        "ReflectionPropertyAccessor",
        "FastMember.ObjectAccessor",
        "ILEmit.PropertyGetter",
    ];

    public bool IsMatch(CallTreeNode node)
    {
        var method = node.Method;
        for (int i = 0; i < Patterns.Length; i++)
            if (method.Contains(Patterns[i], StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    public TraceFinding Analyze(CallTreeNode node)
    {
        var evidence = CollectEvidence(node);
        bool hasDictDispatch = evidence.Any(e =>
            e.Contains("Dictionary", StringComparison.OrdinalIgnoreCase) ||
            e.Contains("Hashtable", StringComparison.OrdinalIgnoreCase));

        int score = node.InclusivePct switch
        {
            >= 15 => 92,
            >= 8  => 82,
            >= 3  => 72,
            _     => 62
        };

        string detail = hasDictDispatch
            ? "Dynamic property access is routing through dictionary or hashtable lookups per property read/write. " +
              "This pattern is O(1) per access but accumulates significant overhead at high call rates."
            : "Metadata-driven property dispatch is consuming CPU. " +
              "Each property access involves runtime lookup rather than direct field access.";

        return new TraceFinding(
            Severity:       FindingSeverity.Warning,
            Category:       "Dynamic Metadata",
            Headline:       "Dynamic metadata-driven property access is a significant CPU consumer",
            Detail:         detail,
            Advice:         "Consider caching PropertyDescriptor/accessor instances. " +
                            "Use compiled expression trees or source-generated accessors (e.g. FastMember) " +
                            "to replace runtime dispatch with direct calls. " +
                            "Profile whether the bottleneck is lookup or the downstream setter logic.",
            Score:          score,
            Confidence:     Math.Min(1.0f, (float)(evidence.Count / 2.0)),
            EvidenceFrames: [.. evidence]);
    }

    private static List<string> CollectEvidence(CallTreeNode node)
    {
        var evidence = new List<string>(4);
        CollectEvidenceRecursive(node, evidence, depth: 0);
        return evidence;
    }

    private static void CollectEvidenceRecursive(CallTreeNode node, List<string> evidence, int depth)
    {
        if (depth > 3) return;
        for (int i = 0; i < Patterns.Length; i++)
        {
            if (node.Method.Contains(Patterns[i], StringComparison.OrdinalIgnoreCase)
                && !evidence.Contains(node.Method))
            {
                evidence.Add(node.Method);
                break;
            }
        }
        for (int c = 0; c < node.Children.Count && evidence.Count < 6; c++)
            CollectEvidenceRecursive(node.Children[c], evidence, depth + 1);
    }
}
