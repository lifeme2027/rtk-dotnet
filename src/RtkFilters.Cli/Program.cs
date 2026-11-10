// rtk-dotnet — unofficial .NET port of the Apache-2.0 filter layer of rtk-ai/rtk.
using System.Diagnostics;
using System.Reflection;
using RtkFilters;

return Cli.Run(args);

public static class Cli
{
    public const string Version = "0.1.0";

    const string Usage = """
        rtk-dotnet — unofficial .NET port of rtk-ai/rtk TOML output filters (Apache-2.0 subset)

        Usage:
          rtk-dotnet list                               List built-in filters
          rtk-dotnet verify [--filter NAME] [-v]        Run upstream inline filter tests
          rtk-dotnet filter [--command "CMD"|--name NAME] [FILE]
                                                        Filter captured output (FILE or stdin)
          rtk-dotnet run [--stats] -- CMD [ARGS...]     Run CMD, filter stdout, keep exit code
          rtk-dotnet --version                          Print 0.1.0

        `run` always preserves the child's exit code. Stderr is forwarded unfiltered
        unless the matching filter sets filter_stderr = true (then stdout+stderr are
        merged and filtered, matching upstream). Empty filtered output prints nothing.
        """;

    public static int Run(string[] args) => Run(args, Console.Out, Console.Error, Console.In);

    public static int Run(string[] args, TextWriter stdout, TextWriter stderr, TextReader stdin)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help") { stdout.WriteLine(Usage); return 0; }
        if (args[0] is "-V" or "--version") { stdout.WriteLine(GetVersion()); return 0; }
        var reg = FilterRegistry.LoadBuiltin();
        foreach (var w in reg.Warnings) stderr.WriteLine($"[rtk-dotnet] warning: {w}");
        return args[0] switch
        {
            "list" => List(reg, stdout),
            "verify" => Verify(reg, args[1..], stdout),
            "filter" => Filter(reg, args[1..], stdout, stderr, stdin),
            "run" => RunCommand(reg, args[1..], stdout, stderr),
            _ => Fail(stderr, $"unknown command '{args[0]}'\n\n{Usage}"),
        };
    }

    static string GetVersion()
    {
        var info = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return string.IsNullOrWhiteSpace(info) ? Version : info.Split('+')[0];
    }

    static int Fail(TextWriter stderr, string msg) { stderr.WriteLine(msg); return 2; }

    /// <summary>
    /// Matches upstream <c>runner::terminated</c>: empty text prints nothing;
    /// otherwise exactly one trailing newline is present.
    /// </summary>
    public static void WriteTerminated(TextWriter writer, string text)
    {
        if (text.Length == 0) return;
        writer.Write(text);
        if (!text.EndsWith('\n')) writer.Write('\n');
    }

    static int List(FilterRegistry reg, TextWriter stdout)
    {
        foreach (var f in reg.Filters) stdout.WriteLine($"{f.Name,-24} {f.Description}");
        stdout.WriteLine($"{reg.Filters.Count} filters");
        return 0;
    }

    static int Verify(FilterRegistry reg, string[] a, TextWriter stdout)
    {
        string? only = null; bool verbose = false;
        for (int i = 0; i < a.Length; i++)
        {
            if (a[i] == "--filter" && i + 1 < a.Length) only = a[++i];
            else if (a[i] is "-v" or "--verbose") verbose = true;
        }
        var outcomes = reg.RunTests(only);
        foreach (var o in outcomes.Where(o => !o.Passed || verbose))
        {
            stdout.WriteLine($"{(o.Passed ? "PASS" : "FAIL")} {o.Case.FilterName} :: {o.Case.Name}");
            if (!o.Passed)
            {
                stdout.WriteLine("  expected: " + o.Case.Expected.TrimEnd('\n').Replace("\n", "\\n"));
                stdout.WriteLine("  actual:   " + o.Actual.Replace("\n", "\\n"));
            }
        }
        int passed = outcomes.Count(o => o.Passed);
        stdout.WriteLine($"{passed}/{outcomes.Count} inline tests passed across {outcomes.Select(o => o.Case.FilterName).Distinct().Count()} filters");
        return passed == outcomes.Count ? 0 : 1;
    }

    static int Filter(FilterRegistry reg, string[] a, TextWriter stdout, TextWriter stderr, TextReader stdin)
    {
        string? command = null, name = null, file = null;
        for (int i = 0; i < a.Length; i++)
        {
            if (a[i] is "--command" && i + 1 < a.Length) command = a[++i];
            else if ((a[i] is "--name" or "--filter") && i + 1 < a.Length) name = a[++i];
            else if (a[i].StartsWith('-')) return Fail(stderr, $"filter: unknown option '{a[i]}'");
            else file = a[i];
        }
        if (command is null && name is null)
            return Fail(stderr, "filter: --command or --name is required");
        var input = file is null ? stdin.ReadToEnd() : File.ReadAllText(file);
        TomlFilter? f = name is not null ? reg.Get(name) : reg.FindMatching(command!);
        if (name is not null && f is null) return Fail(stderr, $"filter: unknown filter '{name}'");
        if (f is null)
        {
            WriteTerminated(stdout, input);
            stderr.WriteLine("[rtk-dotnet] no filter matched; passthrough");
            return 0;
        }
        var output = f.Apply(input);
        WriteTerminated(stdout, output);
        ReportStats(stderr, f.Name, input, output);
        return 0;
    }

    static int RunCommand(FilterRegistry reg, string[] a, TextWriter stdout, TextWriter stderr)
    {
        bool stats = false; int i = 0;
        for (; i < a.Length; i++)
        {
            if (a[i] == "--stats") stats = true;
            else if (a[i] == "--") { i++; break; }
            else break;
        }
        if (i >= a.Length) return Fail(stderr, "run: missing command");
        var cmd = a[i];
        var cmdArgs = a[(i + 1)..];
        var commandLine = string.Join(' ', a[i..]);
        var f = reg.FindMatching(commandLine);

        var psi = new ProcessStartInfo(cmd)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var x in cmdArgs) psi.ArgumentList.Add(x);

        Process p;
        try
        {
            p = Process.Start(psi) ?? throw new InvalidOperationException($"failed to start '{cmd}'");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException or InvalidOperationException)
        {
            stderr.WriteLine($"[rtk-dotnet] failed to start '{cmd}': {ex.Message}");
            return 127;
        }

        using (p)
        {
            var errTask = p.StandardError.ReadToEndAsync();
            var rawStdout = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            var rawStderr = errTask.GetAwaiter().GetResult();

            if (f is null)
            {
                WriteTerminated(stdout, rawStdout);
                WriteTerminated(stderr, rawStderr);
                return p.ExitCode;
            }

            // When filter_stderr is set, merge streams then filter (upstream).
            // Otherwise filter stdout only and always forward stderr unfiltered
            // so a failing command keeps its error messages.
            var raw = f.FilterStderr ? rawStdout + rawStderr : rawStdout;
            var output = f.Apply(raw);
            WriteTerminated(stdout, output);
            if (!f.FilterStderr) WriteTerminated(stderr, rawStderr);
            if (stats) ReportStats(stderr, f.Name, raw, output);
            return p.ExitCode;
        }
    }

    /// <summary>Body as an agent would see it: empty stays empty, otherwise one trailing newline.</summary>
    public static string Terminated(string text) =>
        text.Length == 0 || text.EndsWith('\n') ? text : text + "\n";

    static void ReportStats(TextWriter stderr, string name, string input, string output)
    {
        // Count the terminated form that WriteTerminated actually emits.
        int before = TextUtil.EstimateTokens(input), after = TextUtil.EstimateTokens(Terminated(output));
        double pct = before == 0 ? 0 : 100.0 * (before - after) / before;
        stderr.WriteLine($"[rtk-dotnet] filter={name} tokens(est. ceil(utf8_bytes/4)) {before} -> {after} ({pct:F1}% saved)");
    }
}
