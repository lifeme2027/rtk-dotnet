using RtkFilters;

namespace RtkFilters.Tests;

public class EngineTests
{
    private static TomlFilter One(string toml) => FilterRegistry.Parse(toml, "test").Filters.Single();

    [Fact]
    public void Dotnet_build_success_short_circuits()
    {
        var f = FilterRegistry.LoadBuiltin().FindMatching("dotnet build -c Release");
        Assert.Equal("dotnet-build", f?.Name);
        var log = "Build succeeded.\n    0 Warning(s)\n    0 Error(s)\n";
        Assert.Equal("ok (build succeeded)", f!.Apply(log));
    }

    [Fact]
    public void Strip_ansi_removes_codes() => Assert.Equal("Error", TextUtil.StripAnsi("\x1b[31mError\x1b[0m"));

    [Theory]
    [InlineData("hello", 10, "hello")]
    [InlineData("hello world", 8, "hello...")]
    [InlineData("hello", 2, "...")]
    [InlineData("日本語テキスト", 5, "日本...")]
    public void Truncate_matches_upstream(string s, int n, string expected) => Assert.Equal(expected, TextUtil.Truncate(s, n));

    [Fact]
    public void Lines_behaves_like_rust_str_lines()
    {
        Assert.Equal(new[] { "a", "b" }, TextUtil.Lines("a\r\nb\n"));
        Assert.Empty(TextUtil.Lines(""));
        Assert.Equal(new[] { "a", "" }, TextUtil.Lines("a\n\n"));
    }

    [Fact]
    public void Head_and_tail_insert_omit_marker()
    {
        var f = One("[filters.x]\nmatch_command = \"^x\"\nhead_lines = 1\ntail_lines = 1\n");
        Assert.Equal("1\n... (2 lines omitted)\n4", f.Apply("1\n2\n3\n4"));
    }

    [Fact]
    public void Max_lines_counts_truncation()
    {
        var f = One("[filters.x]\nmatch_command = \"^x\"\nmax_lines = 2\n");
        Assert.Equal("1\n2\n... (2 lines truncated)", f.Apply("1\n2\n3\n4"));
    }

    [Fact]
    public void On_empty_when_everything_stripped()
    {
        var f = One("[filters.x]\nmatch_command = \"^x\"\nstrip_lines_matching = [\".*\"]\non_empty = \"x: ok\"\n");
        Assert.Equal("x: ok", f.Apply("noise\nmore noise"));
    }

    [Fact]
    public void Match_output_unless_vetoes_short_circuit()
    {
        var f = One("[filters.x]\nmatch_command = \"^x\"\nmatch_output = [{ pattern = \"done\", message = \"ok\", unless = \"error\" }]\n");
        Assert.Equal("ok", f.Apply("done"));
        Assert.Equal("error\ndone", f.Apply("error\ndone"));
    }

    [Fact]
    public void Rust_replacement_syntax_is_translated()
    {
        var f = One("[filters.x]\nmatch_command = \"^x\"\nreplace = [{ pattern = \"(?P<n>\\\\d+) files\", replacement = \"$n f\" }]\n");
        Assert.Equal("12 f", f.Apply("12 files"));
    }

    [Fact]
    public void Unanchored_match_command_is_ignored()
    {
        var reg = FilterRegistry.Parse("[filters.x]\nmatch_command = \"^a|b\"\n", "test");
        Assert.Empty(reg.Filters);
        Assert.Single(reg.Warnings);
    }

    [Fact]
    public void Strip_and_keep_are_mutually_exclusive()
    {
        var reg = FilterRegistry.Parse("[filters.x]\nmatch_command = \"^x\"\nstrip_lines_matching = [\"a\"]\nkeep_lines_matching = [\"b\"]\n", "test");
        Assert.Empty(reg.Filters);
        Assert.Contains("mutually exclusive", reg.Warnings.Single());
    }

    [Fact]
    public void Unknown_field_is_rejected()
    {
        var reg = FilterRegistry.Parse("[filters.x]\nmatch_command = \"^x\"\nmax_line = 3\n", "test");
        Assert.Empty(reg.Filters);
    }

    [Fact]
    public void Unsupported_schema_version_throws() =>
        Assert.Throws<FormatException>(() => FilterRegistry.Parse("schema_version = 2\n", "test"));
}
