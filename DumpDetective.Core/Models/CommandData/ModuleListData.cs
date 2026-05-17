namespace DumpDetective.Core.Models.CommandData;

/// <summary>Raw data collected by <c>ModuleListAnalyzer</c>.</summary>
public sealed record ModuleListData(
    IReadOnlyList<ModuleItem> Modules,
    /// <summary>
    /// Assembly Load Contexts detected from heap objects (requires a walkable heap).
    /// Empty when heap is not walkable or no ALC instances were found.
    /// </summary>
    IReadOnlyList<AlcEntry> AssemblyLoadContexts = default!);

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

/// <summary>Represents one detected <c>AssemblyLoadContext</c> instance.</summary>
public sealed record AlcEntry(
    ulong   Address,
    string  Name,
    bool    IsCollectible,
    int     AssemblyCount);
