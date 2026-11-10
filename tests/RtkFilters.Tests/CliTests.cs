using System.Text;
using RtkFilters;

namespace RtkFilters.Tests;

public class CliTests
{
    private static (int Exit, string Stdout, string Stderr) Invoke(params string[] args) =>
        InvokeWithStdin("", args);

    private static (int Exit, string Stdout, string Stderr) InvokeWithStdin(string stdin, params string[] args)
    {
        var outSb = new StringBuilder();
        var errSb = new StringBuilder();
        using var stdout = new StringWriter(outSb);
        using var stderr = new StringWriter(errSb);
        using var input = new StringReader(stdin);
        int exit = Cli.Run(args, stdout, stderr, input);
        stdout.Flush();
        stderr.Flush();
        return (exit, outSb.ToString(), errSb.ToString());
    }

    [Fact]
    public void Version_prints_0_1_0()
    {
        var (exit, stdout, _) = Invoke("--version");
        Assert.Equal(0, exit);
        Assert.Equal("0.1.0", stdout.Trim());
    }

    [Fact]
    public void Verify_passes_all_inline_tests()
    {
        var (exit, stdout, stderr) = Invoke("verify");
        Assert.Equal(0, exit);
        Assert.Contains("inline tests passed", stdout);
        Assert.DoesNotContain("FAIL", stdout);
        Assert.Equal("", stderr);
    }

    [Fact]
    public void Filter_by_command_short_circuits_successful_dotnet_build()
    {
        var root = FindRepoRoot();
        var sample = Path.Combine(root, "samples/dotnet-build-success.log");
        Assert.True(File.Exists(sample), sample);
        var (exit, stdout, stderr) = Invoke("filter", "--command", "dotnet build", sample);
        Assert.Equal(0, exit);
        Assert.Equal("ok (build succeeded)\n", stdout);
        Assert.Contains("filter=dotnet-build", stderr);
        Assert.Contains("ceil(utf8_bytes/4)", stderr);
    }

    [Fact]
    public void Filter_by_name_keeps_build_errors()
    {
        var root = FindRepoRoot();
        var sample = Path.Combine(root, "samples/dotnet-build-failure.log");
        Assert.True(File.Exists(sample), sample);
        var (exit, stdout, _) = Invoke("filter", "--name", "dotnet-build", sample);
        Assert.Equal(0, exit);
        Assert.Contains("error CS1002", stdout);
        Assert.Contains("Build FAILED.", stdout);
        Assert.DoesNotContain("ok (build succeeded)", stdout);
        Assert.False(stdout.EndsWith("\n\n"), "must not add an extra trailing newline");
    }

    [Fact]
    public void Filter_empty_output_prints_nothing()
    {
        var (exit, stdout, _) = InvokeWithStdin("", "filter", "--name", "dotnet-build");
        Assert.Equal(0, exit);
        Assert.Equal("", stdout);
    }

    [Fact]
    public void Run_preserves_exit_code_and_stderr_on_failure()
    {
        var (exit, stdout, stderr) = Invoke("run", "--", "dotnet", "build", "/tmp/rtk-dotnet-does-not-exist.csproj", "-nologo");
        Assert.NotEqual(0, exit);
        var combined = stdout + stderr;
        Assert.True(combined.Contains("error", StringComparison.OrdinalIgnoreCase) ||
                    combined.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
                    combined.Contains("MSB", StringComparison.Ordinal),
            "expected an error message, got stdout=" + stdout + " stderr=" + stderr);
    }

    [Fact]
    public void Run_passthrough_git_and_false()
    {
        Assert.Null(FilterRegistry.LoadBuiltin().FindMatching("git status"));
        var (okExit, okOut, _) = Invoke("run", "--", "true");
        Assert.Equal(0, okExit);
        Assert.Equal("", okOut);
        var (failExit, _, failErr) = Invoke("run", "--", "bash", "-c", "echo err-msg >&2; exit 17");
        Assert.Equal(17, failExit);
        Assert.Contains("err-msg", failErr);
    }

    [Fact]
    public void Unknown_command_exits_2()
    {
        var (exit, _, stderr) = Invoke("nope");
        Assert.Equal(2, exit);
        Assert.Contains("unknown command", stderr);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "RtkFilters.Net.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate RtkFilters.Net.sln from " + AppContext.BaseDirectory);
    }
}
