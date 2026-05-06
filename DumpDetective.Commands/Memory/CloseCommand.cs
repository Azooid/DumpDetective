using DumpDetective.Analysis.Memory;
using DumpDetective.Core.Runtime;
using DumpDetective.Core.Utilities;
using Spectre.Console;

namespace DumpDetective.Commands.Memory;

/// <summary>
/// Deletes all analysis cache files created by the <c>load</c> command or by
/// <c>analyze --full</c> for a given dump file.
/// </summary>
public sealed class CloseCommand : ICommand
{
    public string Name               => "close";
    public string Description        => "Delete all analysis cache files for a dump file.";
    public bool   IncludeInFullAnalyze => false;

    private const string Help = """
        Usage: DumpDetective close <dump-file-or-directory> [options]

        Deletes all analysis cache files created by 'load' or 'analyze --full'.
        When a directory is given, caches for every .dmp and .mdmp file in it are removed.


          .ddcache\<dump-name>\     — entire cache directory (stringGroups.bin,
                                      fragmentation.bin, gc-roots.bin,
                                      static-roots.bin, hot-addr-types.bin,
                                      <name>.bfs.idx, <name>.parent.map)

        Options:
          --dry-run   Show what would be deleted without deleting
          -h, --help  Show this help
        """;

    public int Run(string[] args)
    {
        if (CommandBase.TryHelp(args, Help)) return 0;

        var     a      = CliArgs.Parse(args);
        string? target = a.DumpPath;
        bool    dryRun = a.HasFlag("dry-run");

        if (target is null) { AnsiConsole.MarkupLine("[bold red]✗[/] dump file or directory path required."); return 1; }

        target = Path.GetFullPath(target);

        // ── Directory mode ────────────────────────────────────────────────────
        if (Directory.Exists(target))
        {
            var dumps = Directory.EnumerateFiles(target, "*.dmp",  SearchOption.TopDirectoryOnly)
                .Concat(Directory.EnumerateFiles(target, "*.mdmp", SearchOption.TopDirectoryOnly))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (dumps.Count == 0)
            {
                AnsiConsole.MarkupLine($"[yellow]⚠[/] No .dmp or .mdmp files found in: {Markup.Escape(target)}");
                return 0;
            }

            AnsiConsole.MarkupLine($"[bold]Found {dumps.Count} dump file(s) in[/] {Markup.Escape(target)}");
            AnsiConsole.WriteLine();

            long totalBytes = 0;
            int  exitCode   = 0;
            for (int i = 0; i < dumps.Count; i++)
            {
                AnsiConsole.MarkupLine($"[bold dim]── [[{i + 1}/{dumps.Count}]] {Markup.Escape(Path.GetFileName(dumps[i]))} ──[/]");
                int result = RunSingle(dumps[i], dryRun, out long freed);
                totalBytes += freed;
                if (result != 0) exitCode = result;
                AnsiConsole.WriteLine();
            }

            AnsiConsole.MarkupLine(exitCode == 0
                ? $"[green]✓[/] Done. Freed {FormatBytes(totalBytes)} across {dumps.Count} dump(s)."
                : $"[yellow]⚠[/] Completed with errors ({dumps.Count} dump(s) processed).");
            return exitCode;
        }

        // ── Single-file mode ──────────────────────────────────────────────────
        if (!File.Exists(target))
        {
            AnsiConsole.MarkupLine($"[bold red]✗[/] path not found: {Markup.Escape(target)}");
            return 1;
        }
        return RunSingle(target, dryRun, out _);
    }

    private static int RunSingle(string dumpPath, bool dryRun, out long freedBytes)
    {
        string dumpDir  = Path.GetDirectoryName(dumpPath)!;
        string dumpName = Path.GetFileNameWithoutExtension(dumpPath);
        string cacheDir = Path.Combine(dumpDir, ".ddcache", dumpName);

        if (dryRun)
            AnsiConsole.MarkupLine("[dim]-- dry run: nothing will be deleted --[/]");

        long totalBytes = 0;
        int  deleted    = 0;

        // ── .ddcache directory ────────────────────────────────────────────────
        if (Directory.Exists(cacheDir))
        {
            long dirBytes = DirSize(cacheDir);
            if (dryRun)
                AnsiConsole.MarkupLine($"  [yellow]would delete[/] {Markup.Escape(cacheDir)}  ({FormatBytes(dirBytes)})");
            else
            {
                try
                {
                    Directory.Delete(cacheDir, recursive: true);
                    AnsiConsole.MarkupLine($"  [green]✓[/] deleted {Markup.Escape(cacheDir)}  ({FormatBytes(dirBytes)})");
                    totalBytes += dirBytes;
                    deleted++;
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine($"  [red]✗[/] failed to delete {Markup.Escape(cacheDir)}: {Markup.Escape(ex.Message)}");
                }
            }

            // Try to remove the parent .ddcache dir too if it's now empty.
            if (!dryRun)
            {
                string parentDdcache = Path.GetDirectoryName(cacheDir)!;
                try
                {
                    if (Directory.Exists(parentDdcache) &&
                        !Directory.EnumerateFileSystemEntries(parentDdcache).Any())
                        Directory.Delete(parentDdcache);
                }
                catch { }
            }
        }
        else
        {
            AnsiConsole.MarkupLine($"  [dim]no cache dir: {Markup.Escape(cacheDir)}[/]");
        }

        AnsiConsole.WriteLine();
        if (dryRun)
            AnsiConsole.MarkupLine($"[dim]Dry run complete — {deleted + (Directory.Exists(cacheDir) ? 1 : 0)} item(s) would be removed[/]");
        else if (deleted > 0)
            AnsiConsole.MarkupLine($"[green]Done.[/] Freed {FormatBytes(totalBytes)} across {deleted} item(s).");
        else
            AnsiConsole.MarkupLine("[dim]Nothing to delete.[/]");

        freedBytes = totalBytes;
        return 0;
    }

    public void Render(DumpContext ctx, IRenderSink sink)
        => throw new NotSupportedException("close does not produce a report.");

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static void DeleteFile(string path, string label, bool dryRun,
        ref long totalBytes, ref int deleted)
    {
        if (!File.Exists(path))
        {
            AnsiConsole.MarkupLine($"  [dim]no {Markup.Escape(label)}: {Markup.Escape(path)}[/]");
            return;
        }

        long size = new FileInfo(path).Length;
        if (dryRun)
        {
            AnsiConsole.MarkupLine($"  [yellow]would delete[/] {Markup.Escape(path)}  ({FormatBytes(size)})");
            return;
        }

        try
        {
            File.Delete(path);
            AnsiConsole.MarkupLine($"  [green]✓[/] deleted {Markup.Escape(path)}  ({FormatBytes(size)})");
            totalBytes += size;
            deleted++;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"  [red]✗[/] failed to delete {Markup.Escape(path)}: {Markup.Escape(ex.Message)}");
        }
    }

    private static long DirSize(string dir)
    {
        long total = 0;
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                try { total += new FileInfo(f).Length; } catch { }
        }
        catch { }
        return total;
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1_073_741_824L => $"{bytes / 1_073_741_824.0:F2} GB",
        >= 1_048_576L     => $"{bytes / 1_048_576.0:F1} MB",
        >= 1_024L         => $"{bytes / 1_024.0:F0} KB",
        _                 => $"{bytes} B",
    };
}
