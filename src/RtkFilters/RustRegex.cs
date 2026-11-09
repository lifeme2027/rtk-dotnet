// Unofficial .NET port of rtk-ai/rtk (Apache-2.0) regex handling.
using System.Text;
using System.Text.RegularExpressions;

namespace RtkFilters;

/// <summary>Translates the subset of Rust <c>regex</c> syntax used by rtk filters into .NET syntax.</summary>
public static class RustRegex
{
    public static Regex Compile(string pattern) =>
        new(TranslatePattern(pattern), RegexOptions.CultureInvariant);

    /// <summary>Rust named groups <c>(?P&lt;name&gt;...)</c> become <c>(?&lt;name&gt;...)</c>.</summary>
    public static string TranslatePattern(string pattern) => pattern.Replace("(?P<", "(?<");

    /// <summary>
    /// Rust replacement strings treat <c>$name</c> as the longest run of [A-Za-z0-9_];
    /// .NET only understands <c>$123</c> and <c>${name}</c>, so normalise to braces.
    /// </summary>
    public static string TranslateReplacement(string replacement)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < replacement.Length; i++)
        {
            char c = replacement[i];
            if (c != '$') { sb.Append(c); continue; }
            if (i + 1 < replacement.Length && replacement[i + 1] == '$') { sb.Append("$$"); i++; continue; }
            if (i + 1 < replacement.Length && replacement[i + 1] == '{')
            {
                int end = replacement.IndexOf('}', i + 2);
                if (end > 0) { sb.Append(replacement, i, end - i + 1); i = end; continue; }
                sb.Append("$$"); continue;
            }
            int j = i + 1;
            while (j < replacement.Length && (char.IsAsciiLetterOrDigit(replacement[j]) || replacement[j] == '_')) j++;
            if (j == i + 1) { sb.Append("$$"); continue; } // lone '$' is literal in Rust
            sb.Append("${").Append(replacement, i + 1, j - i - 1).Append('}');
            i = j - 1;
        }
        return sb.ToString();
    }

    /// <summary>Port of upstream <c>is_fully_anchored</c>: every top-level branch must start with '^'.</summary>
    public static bool IsFullyAnchored(string pattern) =>
        TopLevelBranches(pattern).All(b => StripInlineFlags(b).StartsWith('^'));

    private static string StripInlineFlags(string branch)
    {
        if (!branch.StartsWith("(?")) return branch;
        var rest = branch[2..];
        int end = rest.IndexOf(')');
        if (end < 0) return branch;
        return rest[..end].All(ch => "imsuxU-".Contains(ch)) ? rest[(end + 1)..] : branch;
    }

    private static List<string> TopLevelBranches(string pattern)
    {
        var branches = new List<string>();
        int depth = 0, start = 0;
        bool escaped = false, inClass = false;
        for (int idx = 0; idx < pattern.Length; idx++)
        {
            char ch = pattern[idx];
            if (escaped) { escaped = false; continue; }
            switch (ch)
            {
                case '\\': escaped = true; break;
                case '[' when !inClass: inClass = true; break;
                case ']' when inClass: inClass = false; break;
                case '(' when !inClass: depth++; break;
                case ')' when !inClass: depth = Math.Max(0, depth - 1); break;
                case '|' when !inClass && depth == 0:
                    branches.Add(pattern[start..idx]); start = idx + 1; break;
            }
        }
        branches.Add(pattern[start..]);
        return branches;
    }
}
