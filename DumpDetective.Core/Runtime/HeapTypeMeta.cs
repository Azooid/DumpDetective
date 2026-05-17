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

    /// <summary><c>System.Threading.Thread</c> — used by <c>ThreadNameConsumer</c>.</summary>
    public bool     IsThread     { get; init; }
    /// <summary><c>System.Threading.Tasks.Task</c> or derived — used by <c>ThreadPoolConsumer</c>.</summary>
    public bool     IsTask       { get; init; }
    /// <summary>Thread-pool work-item types — used by <c>ThreadPoolConsumer</c>.</summary>
    public bool     IsWorkItem   { get; init; }
    /// <summary>HTTP client/request/response types — used by <c>HttpRequestsConsumer</c>.</summary>
    public bool     IsHttp       { get; init; }
    /// <summary><c>System.Runtime.CompilerServices.ConditionalWeakTable*</c> — used by <c>ConditionalWeakTableConsumer</c>.</summary>
    public bool     IsCwt        { get; init; }

    /// <summary>
    /// <c>true</c> for <c>System.Data.DataTable</c> and all subclasses (e.g. TypedDataSet
    /// generated inner tables such as <c>FooDataSet+FooDataTable</c>).
    /// </summary>
    public bool     IsDataTable  { get; init; }

    /// <summary>
    /// The <c>nextRowID</c> or <c>_nextRowID</c> field resolved by walking the base-type
    /// chain from the concrete type.  Non-null only when <see cref="IsDataTable"/> is
    /// <c>true</c> and the field was found in the hierarchy.
    /// </summary>
    public ClrInstanceField? DataTableNextRowIdField { get; init; }

    /// <summary>
    /// The <c>rowCollection</c> or <c>_rowCollection</c> field on <c>System.Data.DataTable</c>
    /// (resolved via base-type chain).  Reading this field gives a <c>DataRowCollection</c>
    /// object; its base type <c>InternalDataCollectionBase</c> holds the actual ArrayList
    /// in a field named <c>list</c>.
    /// Non-null only when <see cref="IsDataTable"/> is <c>true</c> and the field was found.
    /// </summary>
    public ClrInstanceField? DataTableRowCollField { get; init; }

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
