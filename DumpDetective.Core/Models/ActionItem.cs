namespace DumpDetective.Core.Models;

/// <summary>Triage bucket for an <see cref="ActionItem"/>, mirroring a ranked incident queue.</summary>
public enum ActionBucket { Now, Next, Watch }

/// <summary>
/// One <see cref="Finding"/> promoted into a ranked, actionable queue item — a priority
/// label, a 0-100 score, a suggested owner team, and (when resolvable) the CLI command
/// whose sub-report holds the supporting evidence for a one-click jump.
/// </summary>
public sealed record ActionItem(
    string          Priority,
    int             Score,
    ActionBucket    Bucket,
    FindingSeverity Severity,
    string          Category,
    string          Headline,
    string?         Detail,
    string?         Advice,
    string          SuggestedOwner,
    string?         TargetCommand);
