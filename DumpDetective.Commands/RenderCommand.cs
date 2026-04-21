namespace DumpDetective.Commands;

/// <summary>
/// "render" is an alias for "trend-render" — replays a previously saved JSON report.
/// </summary>
public sealed class RenderCommand : ICommand
{
    private readonly TrendRenderCommand _inner = new();

    public string Name               => "render";
    public string Description        => "Replay a saved JSON report to any output format without re-analyzing.";
    public bool   IncludeInFullAnalyze => false;

    public int Run(string[] args) => _inner.Run(args);

    public void Render(DumpContext ctx, IRenderSink sink) => _inner.Render(ctx, sink);

}
