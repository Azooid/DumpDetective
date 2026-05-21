using DumpDetective.Core.Runtime;

namespace Example.DumpDetective;

internal sealed record ThreadHotspotThread(int ManagedThreadId, uint OsThreadId, string? TopFrame);
internal sealed record ThreadHotspotThreadDetails(int ManagedThreadId, uint OsThreadId, IReadOnlyList<string> Frames);

internal sealed class ThreadHotspotsCache
{
    public ThreadHotspotsCache(IReadOnlyList<ThreadHotspotThread> Threads)
        => this.Threads = Threads;

    public IReadOnlyList<ThreadHotspotThread> Threads { get; }

    public static ThreadHotspotsCache Build(DumpContext ctx)
    {
        var threads = new List<ThreadHotspotThread>();

        foreach (var thread in ctx.Runtime.Threads)
        {
            string? topFrame = null;
            foreach (var frame in thread.EnumerateStackTrace(includeContext: false))
            {
                string? method = frame.Method?.Signature ?? frame.Method?.Name;
                if (method is not null)
                {
                    topFrame = method;
                    break;
                }
            }

            threads.Add(new ThreadHotspotThread(thread.ManagedThreadId, thread.OSThreadId, topFrame));
        }

        return new ThreadHotspotsCache(threads);
    }
}

internal sealed class ThreadHotspotDetailsCache
{
    public ThreadHotspotDetailsCache(IReadOnlyList<ThreadHotspotThreadDetails> Threads)
        => this.Threads = Threads;

    public IReadOnlyList<ThreadHotspotThreadDetails> Threads { get; }

    public static ThreadHotspotDetailsCache Build(DumpContext ctx)
    {
        var threads = new List<ThreadHotspotThreadDetails>();

        foreach (var thread in ctx.Runtime.Threads)
        {
            var frames = new List<string>();
            foreach (var frame in thread.EnumerateStackTrace(includeContext: false))
            {
                string? method = frame.Method?.Signature ?? frame.Method?.Name;
                if (method is not null)
                    frames.Add(method);
            }

            threads.Add(new ThreadHotspotThreadDetails(thread.ManagedThreadId, thread.OSThreadId, frames));
        }

        return new ThreadHotspotDetailsCache(threads);
    }
}