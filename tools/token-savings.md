# Token savings

Counting method: `ceil(utf8_byte_length / 4)`, matching upstream
`rtk-text::estimate_tokens`. Raw text is the child's stdout.
Filtered text is `rtk-dotnet` stdout (a single trailing newline is
added when the body is non-empty, same as upstream `terminated`).
The `[rtk-dotnet] ... tokens ...` line on stderr is not counted.

Measured in Asia/Shanghai on 2026-11-10 with `python3 tools/measure-tokens.py`.
Live `dotnet build` timings (restore/elapsed) can shift the token count by a
few tokens from run to run; the checked-in sample logs are reproducible.
`dotnet test` and `git status` / `git diff` / `git log` have no TOML filter, so
`rtk-dotnet run` is a passthrough (0% saved) and is omitted from the table.

| Scenario | Filter matched | Raw tokens | Filtered tokens | Saved | % saved | Notes |
|---|---|---:|---:|---:|---:|---|
| this repo: `dotnet build -c Release` (after `dotnet clean`) | yes (`dotnet-build`) | 122 | 6 | 116 | 95.1% | exit 0 |
| `samples/failing-app` live `dotnet build` (failure) | yes (`dotnet-build`) | 134 | 124 | 10 | 7.5% | exit 1 |
| `samples/dotnet-build-success.log` | yes (`dotnet-build`) | 81 | 6 | 75 | 92.6% | offline fixture |
| `samples/dotnet-build-failure.log` | yes (`dotnet-build`) | 77 | 39 | 38 | 49.4% | offline fixture |

## Filtered output

### this repo: dotnet build -c Release

- `[rtk-dotnet] filter=dotnet-build tokens(est. ceil(utf8_bytes/4)) 122 -> 6 (95.1% saved)`

```
ok (build succeeded)
```

### samples/failing-app live dotnet build

Error lines, `Build FAILED.`, and the warning/error counts are retained.
Banners and blank lines are stripped. Restore / elapsed lines vary slightly
between runs.

```
  All projects are up-to-date for restore.
samples/failing-app/Program.cs(2,10): error CS1003: Syntax error, ',' expected [samples/failing-app/FailingApp.csproj]
Build FAILED.
samples/failing-app/Program.cs(2,10): error CS1003: Syntax error, ',' expected [samples/failing-app/FailingApp.csproj]
    0 Warning(s)
    1 Error(s)
Time Elapsed 00:00:00.67
```

### sample log: dotnet-build-success.log

- `[rtk-dotnet] filter=dotnet-build tokens(est. ceil(utf8_bytes/4)) 81 -> 6 (92.6% saved)`

```
ok (build succeeded)
```

### sample log: dotnet-build-failure.log

- `[rtk-dotnet] filter=dotnet-build tokens(est. ceil(utf8_bytes/4)) 77 -> 39 (49.4% saved)`

```
src/Program.cs(10,5): error CS1002: ; expected [/tmp/failing-app/FailingApp.csproj]
Build FAILED.
    0 Warning(s)
    1 Error(s)
Time Elapsed 00:00:00.42
```
