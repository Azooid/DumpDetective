namespace DumpDetective.Core.Models.CommandData;

/// <summary>Raw data collected by <c>ModuleListAnalyzer</c>.</summary>
public sealed record ModuleListData(IReadOnlyList<ModuleItem> Modules);

public sealed record ModuleItem(
    string  Path,
    string  FileName,
    string  Kind,
    long    Size,
    /// <summary>PDB file path embedded in the PE debug directory (null = no PDB info).</summary>
    string? PdbPath    = null,
    /// <summary>PDB GUID used for symbol server lookup (null = no PDB info).</summary>
    string? PdbGuid    = null,
    /// <summary>PDB revision/age used for symbol server lookup (0 = unknown).</summary>
    int     PdbAge     = 0,
    /// <summary>True when the PDB path stored in the PE matches an existing local file.</summary>
    bool    PdbPresent = false);
