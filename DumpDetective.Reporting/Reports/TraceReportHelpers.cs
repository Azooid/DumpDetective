namespace DumpDetective.Reporting.Reports;

/// <summary>
/// Shared IL-cleanup and frame-trimming helpers used by all trace report renderers.
/// </summary>
internal static class TraceReportHelpers
{
    /// <summary>
    /// Strips IL noise from a method name:
    /// [Assembly.Qualifier] tokens, 'class '/'valuetype ' keywords,
    /// !!N generic params → T/T1/T2, and backtick arities List`1 → List.
    /// </summary>
    internal static string CleanIlMethod(string method)
    {
        if (method.Length == 0) return method;
        var sb = new System.Text.StringBuilder(method.Length);
        int i = 0;
        while (i < method.Length)
        {
            // Skip [Assembly.Qualifier] tokens
            if (method[i] == '[')
            {
                int close = method.IndexOf(']', i + 1);
                if (close >= 0) { i = close + 1; continue; }
            }
            // Replace !!N with T, T1, T2 …
            if (i + 1 < method.Length && method[i] == '!' && method[i + 1] == '!')
            {
                i += 2;
                int numStart = i;
                while (i < method.Length && char.IsDigit(method[i])) i++;
                int n = numStart < i && int.TryParse(method[numStart..i], out int v) ? v : 0;
                sb.Append(n == 0 ? "T" : "T" + n);
                continue;
            }
            // Skip IL keyword 'class ' and 'valuetype '
            if (method.AsSpan(i).StartsWith("class ")) { i += 6; continue; }
            if (method.AsSpan(i).StartsWith("valuetype ")) { i += 10; continue; }
            // Strip backtick arity: List`1 → List
            if (method[i] == '`')
            {
                i++;
                while (i < method.Length && char.IsDigit(method[i])) i++;
                continue;
            }
            sb.Append(method[i++]);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Cleans a frame string and trims it to <paramref name="maxLen"/> characters.
    /// Prefers right-side truncation of namespace prefixes over left-truncation,
    /// so the class+method name is preserved rather than the namespace.
    /// </summary>
    internal static string TrimFrame(string s, int maxLen)
    {
        s = CleanIlMethod(s);
        if (s.Length <= maxLen) return s;

        // Strategy: keep ClassName.MethodName(…) — drop namespace prefix.
        int parenIdx    = s.IndexOf('(');
        string namePart = parenIdx > 0 ? s[..parenIdx] : s;
        string paramPart = parenIdx > 0 ? s[parenIdx..] : "";

        int lastDot       = namePart.LastIndexOf('.');
        int secondLastDot = lastDot > 0 ? namePart.LastIndexOf('.', lastDot - 1) : -1;
        int nameStart     = secondLastDot >= 0 ? secondLastDot + 1 : (lastDot >= 0 ? lastDot + 1 : 0);
        string shortName  = namePart[nameStart..];

        if (paramPart.Length > 0)
        {
            string candidate = shortName + paramPart;
            if (candidate.Length <= maxLen) return candidate;
            string withEllipsis = shortName + "(…)";
            if (withEllipsis.Length <= maxLen) return withEllipsis;
        }

        return shortName.Length <= maxLen ? shortName : shortName[..(maxLen - 1)] + "…";
    }
}
