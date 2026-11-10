// Ported from rtk-ai/rtk text/src/lib.rs and src/core/toml_filter.rs (Apache-2.0).
// Unofficial .NET port; see repository README and LICENSE.
using System.Text;
using System.Text.RegularExpressions;

namespace RtkFilters;

/// <summary>Text helpers that mirror the semantics of rtk's Rust helpers.</summary>
public static class TextUtil
{
    private static readonly Regex AnsiRe = new(@"\x1b\[[0-9;]*[a-zA-Z]", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Removes ANSI CSI escape sequences (same regex as upstream <c>strip_ansi</c>).</summary>
    public static string StripAnsi(string text) => AnsiRe.Replace(text, "");

    /// <summary>
    /// Truncates to <paramref name="maxLen"/> Unicode scalar values, appending "..." (upstream <c>truncate</c>).
    /// </summary>
    public static string Truncate(string s, int maxLen)
    {
        var runes = s.EnumerateRunes().ToList();
        if (runes.Count <= maxLen) return s;
        if (maxLen < 3) return "...";
        var sb = new StringBuilder();
        foreach (var r in runes.Take(maxLen - 3)) sb.Append(r.ToString());
        sb.Append("...");
        return sb.ToString();
    }

    /// <summary>
    /// Splits like Rust's <c>str::lines</c>: on '\n', drops one trailing '\r' per line,
    /// and does not yield a final empty line when the text ends with a newline.
    /// </summary>
    public static List<string> Lines(string text)
    {
        var result = new List<string>();
        if (text.Length == 0) return result;
        var parts = text.Split('\n');
        int count = parts.Length;
        if (text.EndsWith('\n')) count--;
        for (int i = 0; i < count; i++)
        {
            var p = parts[i];
            if (p.EndsWith('\r')) p = p[..^1];
            result.Add(p);
        }
        return result;
    }

    /// <summary>
    /// Estimated token count matching upstream <c>rtk-text::estimate_tokens</c>:
    /// <c>ceil(utf8_byte_len / 4.0)</c>. Uses UTF-8 byte length, same as Rust <c>str::len</c>.
    /// </summary>
    public static int EstimateTokens(string text) => EstimateTokensFromLen(Encoding.UTF8.GetByteCount(text));

    /// <summary>Upstream <c>estimate_tokens_from_len</c>.</summary>
    public static int EstimateTokensFromLen(int len) => (int)Math.Ceiling(len / 4.0);
}
