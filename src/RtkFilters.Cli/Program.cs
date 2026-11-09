// rtk-dotnet — unofficial .NET port of the Apache-2.0 filter layer of rtk-ai/rtk.
using System.Diagnostics;
using RtkFilters;

return Cli.Run(args);

static class Cli
{
    const string Usage = """
        rtk-dotnet — unofficial .NET port of rtk-ai/rtk TOML output filters (Apache-2.0 subset)

        Usage:
          rtk-dotnet list                               List built-in filters
          rtk-dotnet verify [--filter NAME] [-v]        Run upstream inline filter tests
          rtk-dotnet filter --command "CMD" [FILE]      Filter captured output (FILE or stdin) as if produced by CMD
          rtk-dotnet run [--stats] -- CMD [ARGS...]     Run CMD, filter its stdout, keep its exit code
        """;

    public static int Run(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help") { Console.WriteLine(Usage); return 0; }
        var reg = FilterRegistry.LoadBuiltin();
        foreach (var w in reg.Warnings) Console.Error.WriteLine($"[rtk-dotnet] warning: {w}");
        return args[0] switch
        {
            "list" => List(reg),
            "verify" => Verify(reg, args[1..]),
            "filter" => Filter(reg, args[1..]),
            "run" => RunCommand(reg, args[1..]),
            _ => Fail($"unknown command '{args[0]}'\n\n{Usage}"),
        };
    }

    static int Fail(string msg) { Console.Error.WriteLine(msg); return 2; }

    static int List(FilterRegistry reg)
    {
        foreach (var f in reg.Filters) Console.WriteLine($"{f.Name,-24} {f.Description}");
        Console.WriteLine($"{reg.Filters.Count} filters");
        return 0;
    }

    static int Verify(FilterRegistry reg, string[] a)
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
            Console.WriteLine($"{(o.Passed ? "PASS" : "FAIL")} {o.Case.FilterName} :: {o.Case.Name}");
            if (!o.Passed)
            {
                Console.WriteLine("  expected: " + o.Case.Expected.TrimEnd('\n').Replace("\n", "\\n"));
                Console.WriteLine("  actual:   " + o.Actual.Replace("\n", "\\n"));
            }
        }
        int passed = outcomes.Count(o => o.Passed);
        Console.WriteLine($"{passed}/{outcomes.Count} inline tests passed across {outcomes.Select(o => o.Case.FilterName).Distinct().Count()} filters");
        return passed == outcomes.Count ? 0 : 1;
    }

    static int Filter(FilterRegistry reg, string[] a)
    {
        string? command = null, file = null;
        for (int i = 0; i < a.Length; i++)
        {
            if (a[i] == "--command" && i + 1 < a.Length) command = a[++i];
            else file = a[i];
        }
        if (command is null) return Fail("filter: --command is required");
        var input = file is null ? Console.In.ReadToEnd() : File.ReadAllText(file);
        var f = reg.FindMatching(command);
        if (f is null) { Console.Write(input); Console.Error.WriteLine("[rtk-dotnet] no filter matched; passthrough"); return 0; }
        var output = f.Apply(input);
        Console.WriteLine(output);
        ReportStats(f.Name, input, output);
        return 0;
    }

    static int RunCommand(FilterRegistry reg, string[] a)
    {
        bool stats = false; int i = 0;
        for (; i < a.Length; i++)
        {
            if (a[i] == "--stats") stats = true;
            else if (a[i] == "--") { i++; break; }
            else break;
        }
        if (i >= a.Length) return Fail("run: missing command");
        var cmd = a[i]; var cmdArgs = a[(i + 1)..];
        var commandLine = string.Join(' ', a[i..]);
        var f = reg.FindMatching(commandLine);

        var psi = new ProcessStartInfo(cmd) { RedirectStandardOutput = true, RedirectStandardError = f?.FilterStderr == true, UseShellExecute = false };
        foreach (var x in cmdArgs) psi.ArgumentList.Add(x);
        using var p = Process.Start(psi)!;
        var errTask = psi.RedirectStandardError ? p.StandardError.ReadToEndAsync() : Task.FromResult("");
        var stdout = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        var raw = stdout + errTask.Result;
        if (f is null) { Console.Write(raw); return p.ExitCode; }
        var output = f.Apply(raw);
        Console.WriteLine(output);
        if (stats) ReportStats(f.Name, raw, output);
        return p.ExitCode;
    }

    static void ReportStats(string name, string input, string output)
    {
        int before = TextUtil.EstimateTokens(input), after = TextUtil.EstimateTokens(output);
        double pct = before == 0 ? 0 : 100.0 * (before - after) / before;
        Console.Error.WriteLine($"[rtk-dotnet] filter={name} tokens(est. bytes/4) {before} -> {after} ({pct:F1}% saved)");
    }
}
