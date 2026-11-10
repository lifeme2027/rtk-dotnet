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

    [Theory]
    [InlineData("", 0)]
    [InlineData("abcd", 1)]
    [InlineData("abcde", 2)]
    [InlineData("a", 1)]
    [InlineData("12345678", 2)]
    [InlineData("hello world", 3)]
    public void Estimate_tokens_matches_upstream_ceil(string text, int expected) =>
        Assert.Equal(expected, TextUtil.EstimateTokens(text));

    [Fact]
    public void Estimate_tokens_uses_utf8_byte_length()
    {
        // "你好" is 6 UTF-8 bytes -> ceil(6/4) = 2
        Assert.Equal(2, TextUtil.EstimateTokens("你好"));
    }

    [Fact]
    public void Replace_all_occurrences_and_chaining()
    {
        var f = One("""
            [filters.x]
            match_command = "^x"
            replace = [{ pattern = "foo", replacement = "bar" }]
            """);
        Assert.Equal("bar baz bar\nbar", f.Apply("foo baz foo\nfoo"));
        var chained = One("""
            [filters.x]
            match_command = "^x"
            replace = [
              { pattern = "aaa", replacement = "bbb" },
              { pattern = "bbb", replacement = "ccc" },
            ]
            """);
        Assert.Equal("ccc", chained.Apply("aaa"));
    }

    [Fact]
    public void Replace_backreferences()
    {
        var f = One("""
            [filters.x]
            match_command = "^x"
            replace = [{ pattern = "(\\w+):(\\w+)", replacement = "$2:$1" }]
            """);
        Assert.Equal("world:hello", f.Apply("hello:world"));
    }

    [Fact]
    public void Head_only_and_tail_only_insert_omit_marker()
    {
        var head = One("[filters.x]\nmatch_command = \"^x\"\nhead_lines = 2\n");
        Assert.Equal("1\n2\n... (2 lines omitted)", head.Apply("1\n2\n3\n4"));
        var tail = One("[filters.x]\nmatch_command = \"^x\"\ntail_lines = 2\n");
        Assert.Equal("... (2 lines omitted)\n3\n4", tail.Apply("1\n2\n3\n4"));
    }

    [Fact]
    public void Keep_lines_matching_basic()
    {
        var f = One("""
            [filters.x]
            match_command = "^x"
            keep_lines_matching = ["error", "warn"]
            """);
        Assert.Equal("error: a\nwarn: b", f.Apply("info\nerror: a\nwarn: b\nok"));
    }

    [Fact]
    public void Empty_input_is_empty_output()
    {
        var f = One("[filters.x]\nmatch_command = \"^x\"\nreplace = [{ pattern = \"foo\", replacement = \"bar\" }]\n");
        Assert.Equal("", f.Apply(""));
    }

    [Fact]
    public void Builtin_dotnet_build_keeps_errors()
    {
        var f = FilterRegistry.LoadBuiltin().Get("dotnet-build");
        Assert.NotNull(f);
        var log = "src/Program.cs(10,5): error CS1002: ; expected\nBuild FAILED.\n    0 Warning(s)\n    1 Error(s)\n";
        var outp = f!.Apply(log);
        Assert.Contains("error CS1002", outp);
        Assert.Contains("Build FAILED.", outp);
        Assert.DoesNotContain("ok (build succeeded)", outp);
    }

    [Fact]
    public void Git_and_dotnet_test_have_no_toml_filter()
    {
        var reg = FilterRegistry.LoadBuiltin();
        Assert.Null(reg.FindMatching("git status"));
        Assert.Null(reg.FindMatching("git diff"));
        Assert.Null(reg.FindMatching("git log -1"));
        Assert.Null(reg.FindMatching("dotnet test"));
        Assert.Equal("dotnet-build", reg.FindMatching("dotnet build -c Release")?.Name);
    }
}
