// Unofficial .NET port of rtk-ai/rtk src/core/toml_filter.rs (Apache-2.0).
using System.Text.RegularExpressions;

namespace RtkFilters;

public sealed record ReplaceRule(Regex Pattern, string Replacement);

public sealed record MatchOutputRule(Regex Pattern, string Message, Regex? Unless);

/// <summary>A compiled declarative filter (one <c>[filters.NAME]</c> table).</summary>
public sealed class TomlFilter
{
    public required string Name { get; init; }
    public string? Description { get; init; }
    public required Regex MatchCommand { get; init; }
    public bool StripAnsi { get; init; }
    public IReadOnlyList<ReplaceRule> Replace { get; init; } = [];
    public IReadOnlyList<MatchOutputRule> MatchOutput { get; init; } = [];
    public IReadOnlyList<Regex> StripLines { get; init; } = [];
    public IReadOnlyList<Regex> KeepLines { get; init; } = [];
    public int? TruncateLinesAt { get; init; }
    public int? HeadLines { get; init; }
    public int? TailLines { get; init; }
    public int? MaxLines { get; init; }
    public string? OnEmpty { get; init; }
    public bool FilterStderr { get; init; }

    public bool Matches(string command) => MatchCommand.IsMatch(command);

    /// <summary>
    /// Applies the 8-stage pipeline in upstream order:
    /// strip_ansi → replace → match_output → strip/keep → truncate → head/tail → max_lines → on_empty.
    /// </summary>
    public string Apply(string stdout)
    {
        var lines = TextUtil.Lines(stdout);

        // 1. strip_ansi
        if (StripAnsi) lines = lines.Select(TextUtil.StripAnsi).ToList();

        // 2. replace — per line, rules chained
        if (Replace.Count > 0)
            lines = lines.Select(l => Replace.Aggregate(l, (acc, r) => r.Pattern.Replace(acc, r.Replacement))).ToList();

        // 3. match_output — short-circuit on whole blob, first rule wins, `unless` vetoes
        if (MatchOutput.Count > 0)
        {
            var blob = string.Join("\n", lines);
            foreach (var rule in MatchOutput)
            {
                if (!rule.Pattern.IsMatch(blob)) continue;
                if (rule.Unless is not null && rule.Unless.IsMatch(blob)) continue;
                return rule.Message;
            }
        }

        // 4. strip OR keep
        if (StripLines.Count > 0) lines = lines.Where(l => !StripLines.Any(r => r.IsMatch(l))).ToList();
        else if (KeepLines.Count > 0) lines = lines.Where(l => KeepLines.Any(r => r.IsMatch(l))).ToList();

        // 5. truncate_lines_at
        if (TruncateLinesAt is int maxChars) lines = lines.Select(l => TextUtil.Truncate(l, maxChars)).ToList();

        // 6. head + tail
        int total = lines.Count;
        if (HeadLines is int head && TailLines is int tail)
        {
            if (total > head + tail)
            {
                var result = lines.Take(head).ToList();
                result.Add($"... ({total - head - tail} lines omitted)");
                result.AddRange(lines.Skip(total - tail));
                lines = result;
            }
        }
        else if (HeadLines is int h)
        {
            if (total > h)
            {
                lines = lines.Take(h).ToList();
                lines.Add($"... ({total - h} lines omitted)");
            }
        }
        else if (TailLines is int t && total > t)
        {
            int omitted = total - t;
            lines = lines.Skip(omitted).ToList();
            lines.Insert(0, $"... ({omitted} lines omitted)");
        }

        // 7. max_lines — absolute cap including omit messages
        if (MaxLines is int max && lines.Count > max)
        {
            int dropped = lines.Count - max;
            lines = lines.Take(max).ToList();
            lines.Add($"... ({dropped} lines truncated)");
        }

        // 8. on_empty
        var output = string.Join("\n", lines);
        if (output.Trim().Length == 0 && OnEmpty is not null) return OnEmpty;
        return output;
    }
}

/// <summary>One inline test from a <c>[[tests.NAME]]</c> array.</summary>
public sealed record FilterTestCase(string FilterName, string Name, string Input, string Expected);

public sealed record FilterTestOutcome(FilterTestCase Case, string Actual, bool Passed);
