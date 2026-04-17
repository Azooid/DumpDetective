namespace DumpDetective.Core.Utilities;

/// <summary>
/// Shared CLI argument parser used by all commands.
///
/// Conventions:
///   - First non-flag positional that ends with <c>.dmp</c>/<c>.mdmp</c> → <see cref="DumpPath"/>
///     (or the <c>DD_DUMP</c> env var as fallback).
///   - All non-flag positionals (in order) → <see cref="Positionals"/>.
///   - <c>--output</c> / <c>-o</c> → <see cref="OutputPath"/>.
///   - <c>--help</c> / <c>-h</c> → <see cref="Help"/> = <see langword="true"/>.
///   - Repeatable options (e.g. <c>--ignore-event foo --ignore-event bar</c>) →
///     <see cref="GetAll"/>.
///   - Single-dash short options with a following value (e.g. <c>-x 0xABCD</c>) are
///     stored as options and retrievable via <see cref="GetOption"/>.
///   - Unknown flags are silently ignored.
/// </summary>
public sealed class CliArgs
{
    private readonly Dictionary<string, string>       _options;
    private readonly Dictionary<string, List<string>> _multi;
    private readonly HashSet<string>                  _flags;
    private readonly List<string>                     _positionals;

    public string?               DumpPath    { get; }
    public string?               OutputPath  { get; }
    public bool                  Help        { get; }

    /// <summary>All non-flag positional arguments, in the order they appeared.</summary>
    public IReadOnlyList<string> Positionals => _positionals;

    private CliArgs(string? dumpPath, string? outputPath, bool help,
                    Dictionary<string, string> options,
                    Dictionary<string, List<string>> multi,
                    HashSet<string> flags,
                    List<string> positionals)
    {
        DumpPath     = dumpPath;
        OutputPath   = outputPath;
        Help         = help;
        _options     = options;
        _multi       = multi;
        _flags       = flags;
        _positionals = positionals;
    }

    /// <summary>Returns <see langword="true"/> if the flag (e.g. <c>"--blocked-only"</c>) was present.</summary>
    public bool HasFlag(string name) => _flags.Contains(NormKey(name));

    /// <summary>
    /// <see langword="true"/> if <c>--addresses</c> or <c>-a</c> was supplied.
    /// Conventional flag for commands that optionally print object addresses.
    /// </summary>
    public bool ShowAddresses => HasFlag("addresses") || HasFlag("a");

    /// <summary>
    /// Returns the value of <c>--filter</c> / <c>-f</c>, or <see langword="null"/> if absent.
    /// Conventional option for commands that accept a type/name filter string.
    /// </summary>
    public string? Filter => GetOption("filter") ?? GetOption("f");

    /// <summary>Returns the raw string value of a named option, or <see langword="null"/>.</summary>
    public string? GetOption(string name) =>
        _options.TryGetValue(NormKey(name), out var v) ? v : null;

    /// <summary>
    /// Returns all values supplied for a repeatable option
    /// (e.g. <c>--ignore-event foo --ignore-event bar</c> → <c>["foo", "bar"]</c>).
    /// Returns an empty list if the option was never supplied.
    /// </summary>
    public IReadOnlyList<string> GetAll(string name) =>
        _multi.TryGetValue(NormKey(name), out var v) ? v : [];

    /// <summary>Returns the integer value of a named option, or <paramref name="default"/> if absent or unparseable.</summary>
    public int GetInt(string name, int @default) =>
        int.TryParse(GetOption(name), out var v) ? v : @default;

    /// <summary>Returns the string value of a named option, or <paramref name="default"/> if absent.</summary>
    public string GetString(string name, string @default) =>
        GetOption(name) ?? @default;

    /// <summary>
    /// Parses <paramref name="args"/> into a <see cref="CliArgs"/> instance.
    /// If no positional dump path is found, falls back to the <c>DD_DUMP</c>
    /// environment variable.
    /// </summary>
    public static CliArgs Parse(string[] args)
    {
        string? dumpPath   = null;
        string? outputPath = null;
        bool    help       = false;
        var     options    = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var     multi      = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var     flags      = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var     positionals = new List<string>();

        void StoreOption(string key, string value)
        {
            options[key] = value;
            if (!multi.TryGetValue(key, out var list))
                multi[key] = list = [];
            list.Add(value);
        }

        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i];

            if (a is "--help" or "-h")
            {
                help = true;
                continue;
            }

            if (a is "--output" or "-o")
            {
                if (i + 1 < args.Length) outputPath = args[++i];
                continue;
            }

            // --key=value
            if (a.StartsWith("--") && a.Contains('='))
            {
                var idx = a.IndexOf('=');
                StoreOption(a[2..idx], a[(idx + 1)..]);
                continue;
            }

            // --key value  (value does not start with -)
            if (a.StartsWith("--") && i + 1 < args.Length && !args[i + 1].StartsWith('-'))
            {
                StoreOption(a[2..], args[++i]);
                continue;
            }

            // -k value  (single-char short option with following value)
            if (a.StartsWith('-') && a.Length == 2 &&
                i + 1 < args.Length && !args[i + 1].StartsWith('-'))
            {
                StoreOption(a[1..], args[++i]);
                continue;
            }

            // standalone flag  (-flag  or  --flag-with-no-value)
            if (a.StartsWith('-'))
            {
                flags.Add(a.TrimStart('-'));
                continue;
            }

            // Positional
            positionals.Add(a);
            if (dumpPath is null &&
                (a.EndsWith(".dmp",  StringComparison.OrdinalIgnoreCase) ||
                 a.EndsWith(".mdmp", StringComparison.OrdinalIgnoreCase)))
            {
                dumpPath = a;
            }
        }

        // Fall back to DD_DUMP environment variable
        dumpPath ??= Environment.GetEnvironmentVariable("DD_DUMP");

        return new CliArgs(dumpPath, outputPath, help, options, multi, flags, positionals);
    }

    private static string NormKey(string name) =>
        name.TrimStart('-').ToLowerInvariant();
}
