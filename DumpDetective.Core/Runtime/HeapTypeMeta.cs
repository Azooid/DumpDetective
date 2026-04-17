using Microsoft.Diagnostics.Runtime;

namespace DumpDetective.Core.Runtime;

/// <summary>
/// Per-MethodTable metadata computed once and cached by <c>HeapWalker</c>
/// to avoid repeated ClrMD field enumeration for objects of the same type.
/// </summary>
public sealed class HeapTypeMeta
{
    public string   Name         { get; init; } = "";
    public ulong    MT           { get; init; }
    public bool     IsException  { get; init; }

    /// <summary>
    /// When non-null, this type is an async state machine and the value is
    /// the extracted outer method name (e.g. <c>MyService.DoWorkAsync</c>).
    /// </summary>
    public string?  AsyncMethod  { get; init; }
    public bool     IsTimer      { get; init; }
    public bool     IsWcf        { get; init; }
    public bool     IsConnection { get; init; }

    /// <summary>
    /// Delegate-typed instance fields on this type (potential event subscribers).
    /// Empty for system types and types with no delegate fields.
    /// </summary>
    public DelegateFieldMeta[] DelegateFields { get; init; } = [];
}

/// <summary>A delegate-typed instance field discovered via ClrMD reflection.</summary>
public readonly record struct DelegateFieldMeta(ClrInstanceField Field, string Name);
