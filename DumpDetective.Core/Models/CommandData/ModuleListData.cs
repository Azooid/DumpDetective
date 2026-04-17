namespace DumpDetective.Core.Models.CommandData;

/// <summary>Raw data collected by <c>ModuleListAnalyzer</c>.</summary>
public sealed record ModuleListData(IReadOnlyList<ModuleItem> Modules);

public sealed record ModuleItem(string Path, string FileName, string Kind, long Size);
