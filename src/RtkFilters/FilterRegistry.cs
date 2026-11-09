// Unofficial .NET port of rtk-ai/rtk src/core/toml_filter.rs (Apache-2.0).
using System.Reflection;
using Tomlyn;
using Tomlyn.Model;

namespace RtkFilters;

/// <summary>Loads, compiles and looks up rtk TOML filters.</summary>
public sealed class FilterRegistry
{
    private static readonly HashSet<string> KnownFields =
    [
        "description", "match_command", "strip_ansi", "replace", "match_output",
        "strip_lines_matching", "keep_lines_matching", "truncate_lines_at",
        "head_lines", "tail_lines", "max_lines", "on_empty", "filter_stderr",
    ];

    public IReadOnlyList<TomlFilter> Filters { get; }
    public IReadOnlyList<FilterTestCase> Tests { get; }
    public IReadOnlyList<string> Warnings { get; }

    private FilterRegistry(List<TomlFilter> filters, List<FilterTestCase> tests, List<string> warnings)
    {
        Filters = filters; Tests = tests; Warnings = warnings;
    }

    /// <summary>
    /// Loads the built-in filters copied from upstream <c>src/filters/*.toml</c>. As in upstream
    /// <c>build.rs</c>, files are sorted by name and concatenated under <c>schema_version = 1</c>.
    /// </summary>
    public static FilterRegistry LoadBuiltin()
    {
        var asm = typeof(FilterRegistry).Assembly;
        var names = asm.GetManifestResourceNames()
            .Where(n => n.EndsWith(".toml", StringComparison.Ordinal))
            .OrderBy(n => n, StringComparer.Ordinal);
        var parts = names.Select(n =>
        {
            using var s = asm.GetManifestResourceStream(n)!;
            using var r = new StreamReader(s);
            return r.ReadToEnd().TrimStart('\uFEFF');
        });
        return Parse("schema_version = 1\n\n" + string.Join("\n\n", parts), "builtin");
    }

    /// <summary>Parses a filters file. A missing <c>schema_version</c> is treated as 1 for single upstream files.</summary>
    public static FilterRegistry Parse(string content, string source)
    {
        content = content.TrimStart('\uFEFF');
        var warnings = new List<string>();
        var doc = Toml.Parse(content, source);
        if (doc.HasErrors)
            throw new FormatException($"TOML parse error in {source}: {string.Join("; ", doc.Diagnostics)}");
        var model = doc.ToModel();

        long schema = model.TryGetValue("schema_version", out var sv) ? Convert.ToInt64(sv) : 1;
        if (schema != 1) throw new FormatException($"unsupported schema_version {schema} in {source} (expected 1)");

        var filters = new List<TomlFilter>();
        if (model.TryGetValue("filters", out var fobj) && fobj is TomlTable ftable)
        {
            foreach (var name in ftable.Keys.OrderBy(k => k, StringComparer.Ordinal)) // BTreeMap order
            {
                try
                {
                    var def = (TomlTable)ftable[name];
                    var match = (string)def["match_command"];
                    if (!RustRegex.IsFullyAnchored(match))
                    {
                        warnings.Add($"filter '{name}' in {source}: match_command '{match}' is not fully anchored; ignored");
                        continue;
                    }
                    filters.Add(Compile(name, def));
                }
                catch (Exception e)
                {
                    warnings.Add($"filter '{name}' in {source}: {e.Message}");
                }
            }
        }

        var tests = new List<FilterTestCase>();
        if (model.TryGetValue("tests", out var tobj) && tobj is TomlTable ttable)
        {
            foreach (var fname in ttable.Keys.OrderBy(k => k, StringComparer.Ordinal))
            {
                if (ttable[fname] is not TomlTableArray arr) continue;
                foreach (var t in arr)
                    tests.Add(new FilterTestCase(fname, (string)t["name"], (string)t["input"], (string)t["expected"]));
            }
        }
        return new FilterRegistry(filters, tests, warnings);
    }

    public static TomlFilter Compile(string name, TomlTable def)
    {
        foreach (var key in def.Keys)
            if (!KnownFields.Contains(key)) throw new FormatException($"unknown field '{key}'");

        var strip = Strings(def, "strip_lines_matching");
        var keep = Strings(def, "keep_lines_matching");
        if (strip.Count > 0 && keep.Count > 0)
            throw new FormatException("strip_lines_matching and keep_lines_matching are mutually exclusive");

        var replace = new List<ReplaceRule>();
        if (def.TryGetValue("replace", out var ro) && ro is TomlArray rarr)
            foreach (TomlTable r in rarr.OfType<TomlTable>())
                replace.Add(new ReplaceRule(RustRegex.Compile((string)r["pattern"]),
                    RustRegex.TranslateReplacement((string)r["replacement"])));
        if (def.TryGetValue("replace", out var ro2) && ro2 is TomlTableArray rta)
            foreach (var r in rta)
                replace.Add(new ReplaceRule(RustRegex.Compile((string)r["pattern"]),
                    RustRegex.TranslateReplacement((string)r["replacement"])));

        var matchOutput = new List<MatchOutputRule>();
        IEnumerable<TomlTable> moTables = def.TryGetValue("match_output", out var mo)
            ? mo switch { TomlArray a => a.OfType<TomlTable>(), TomlTableArray ta => ta, _ => [] }
            : [];
        foreach (var m in moTables)
            matchOutput.Add(new MatchOutputRule(RustRegex.Compile((string)m["pattern"]), (string)m["message"],
                m.TryGetValue("unless", out var u) ? RustRegex.Compile((string)u) : null));

        return new TomlFilter
        {
            Name = name,
            Description = def.TryGetValue("description", out var d) ? (string)d : null,
            MatchCommand = RustRegex.Compile((string)def["match_command"]),
            StripAnsi = def.TryGetValue("strip_ansi", out var sa) && (bool)sa,
            Replace = replace,
            MatchOutput = matchOutput,
            StripLines = strip.Select(RustRegex.Compile).ToList(),
            KeepLines = keep.Select(RustRegex.Compile).ToList(),
            TruncateLinesAt = Int(def, "truncate_lines_at"),
            HeadLines = Int(def, "head_lines"),
            TailLines = Int(def, "tail_lines"),
            MaxLines = Int(def, "max_lines"),
            OnEmpty = def.TryGetValue("on_empty", out var oe) ? (string)oe : null,
            FilterStderr = def.TryGetValue("filter_stderr", out var fs) && (bool)fs,
        };
    }

    private static List<string> Strings(TomlTable t, string key) =>
        t.TryGetValue(key, out var v) && v is TomlArray a ? a.Select(x => (string)x!).ToList() : [];

    private static int? Int(TomlTable t, string key) =>
        t.TryGetValue(key, out var v) ? checked((int)Convert.ToInt64(v)) : null;

    /// <summary>First matching filter wins (upstream <c>find_filter_in</c>).</summary>
    public TomlFilter? FindMatching(string command) => Filters.FirstOrDefault(f => f.Matches(command));

    public TomlFilter? Get(string name) => Filters.FirstOrDefault(f => f.Name == name);

    /// <summary>Runs inline tests like <c>rtk verify</c>; trailing newlines are trimmed before comparing.</summary>
    public List<FilterTestOutcome> RunTests(string? onlyFilter = null)
    {
        var outcomes = new List<FilterTestOutcome>();
        foreach (var tc in Tests)
        {
            if (onlyFilter is not null && tc.FilterName != onlyFilter) continue;
            var f = Get(tc.FilterName);
            if (f is null) { outcomes.Add(new(tc, "<filter not compiled>", false)); continue; }
            var actual = f.Apply(tc.Input).TrimEnd('\n');
            outcomes.Add(new(tc, actual, actual == tc.Expected.TrimEnd('\n')));
        }
        return outcomes;
    }
}
