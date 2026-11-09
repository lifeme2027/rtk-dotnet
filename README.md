# RtkFilters.Net (`rtk-dotnet`)

> **Unofficial .NET 8 port** of the Apache-2.0 TOML output-filter layer of
> [rtk-ai/rtk](https://github.com/rtk-ai/rtk) ("Rust Token Killer").
> Not affiliated with or endorsed by RTK AI Labs.

rtk compresses shell command output before an AI coding agent reads it. This port
implements rtk's declarative filter engine in C# and ships the upstream built-in
filters unchanged, so .NET developers can use and extend them with a `dotnet tool`.

## Upstream and license

- Upstream: https://github.com/rtk-ai/rtk, pinned at commit
  `08a261a0d081ffe11a32986e2f4d76f3fdc33abb` (see `upstream/UPSTREAM_COMMIT`).
- Ported parts are from upstream's **Apache-2.0** CLI crate only:
  `src/core/toml_filter.rs` (filter pipeline), `text/src/lib.rs` (`strip_ansi`, `truncate`),
  and the 62 filter definitions in `src/filters/*.toml` (copied verbatim to `upstream/filters/`).
- **Not ported / not referenced:** upstream `engine/` and `proxy/`, which are licensed
  **BUSL-1.1**. This repository contains no code derived from them.
- This port is licensed under Apache-2.0; see [LICENSE](LICENSE) (copied from upstream).
  The C# sources are modifications/translations of the upstream files listed above.
  Upstream did not ship a NOTICE file at the pinned commit.

## Layout

```
RtkFilters.Net.sln
src/RtkFilters/          library: TOML parsing, 8-stage filter pipeline, Rust→.NET regex shim
src/RtkFilters.Cli/      rtk-dotnet CLI (list / verify / filter / run)
tests/RtkFilters.Tests/  xUnit: every upstream inline test + engine unit tests
upstream/filters/        verbatim upstream filter TOML files (embedded as resources)
samples/                 offline sample logs
```

## Quick start (offline)

```bash
dotnet build -c Release
dotnet test -c Release
dotnet run --project src/RtkFilters.Cli -c Release -- verify
dotnet run --project src/RtkFilters.Cli -c Release -- filter --command "dotnet build" samples/dotnet-build-success.log
dotnet run --project src/RtkFilters.Cli -c Release -- run --stats -- dotnet build src/RtkFilters -c Release
```

Pipeline order matches upstream: `strip_ansi → replace → match_output → strip/keep lines →
truncate_lines_at → head/tail → max_lines → on_empty`. Token estimates use upstream's
documented `bytes / 4` heuristic.

## Status / not yet implemented

- Upstream's dedicated Rust command modules (git, cargo, pytest, `dotnet test` TRX/binlog, …), hooks,
  trust store for project/user filter files, tee/recovery files and the `gain` dashboard.
- Rust `regex` and .NET `Regex` differ in edge cases; only `(?P<name>)` groups and `$name`
  replacements are translated today. All 151 upstream inline tests pass at the pinned commit.
