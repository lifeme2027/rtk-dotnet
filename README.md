# RtkFilters.Net (`rtk-dotnet`)

> **Unofficial .NET 8 port** of the Apache-2.0 TOML output-filter layer of
> [rtk-ai/rtk](https://github.com/rtk-ai/rtk) ("Rust Token Killer").
> Not affiliated with or endorsed by RTK AI Labs.

rtk compresses shell command output before an AI coding agent reads it. This port
implements rtk's declarative filter engine in C# and ships the upstream built-in
filters unchanged, so .NET developers can use and extend them with a `dotnet tool`.

This is the local **v0.1.0** release. Packages are produced with `dotnet pack` and
are intended for local `dotnet tool install --add-source` only; they are **not**
published to nuget.org by this tree.

## Upstream and license

- Upstream: https://github.com/rtk-ai/rtk, pinned at commit
  `08a261a0d081ffe11a32986e2f4d76f3fdc33abb` (see `upstream/UPSTREAM_COMMIT`).
- Ported parts are from upstream's **Apache-2.0** CLI crate only:
  `src/core/toml_filter.rs` (filter pipeline), `text/src/lib.rs` (`strip_ansi`,
  `truncate`, `estimate_tokens`), and the 62 filter definitions in
  `src/filters/*.toml` (copied verbatim to `upstream/filters/`).
- **Not ported / not referenced:** upstream `engine/` and `proxy/`, which are
  licensed **BUSL-1.1**. This repository contains no code derived from them.
- This port is licensed under Apache-2.0; see [LICENSE](LICENSE) (copied from
  upstream) and [NOTICE](NOTICE). The C# sources are translations of the
  upstream files listed above. Upstream did not ship a NOTICE file at the
  pinned commit.

## Install (local nupkg, not nuget.org)

Build and pack from a clone:

```bash
dotnet build -c Release
dotnet test -c Release
dotnet pack -c Release -o ./nupkg
```

Install the CLI as a .NET tool from that folder (adjust the path):

```bash
dotnet tool install rtk-dotnet --add-source ./nupkg --tool-path ./tools/bin
./tools/bin/rtk-dotnet --version    # 0.1.0
./tools/bin/rtk-dotnet verify
```

Or, user-wide:

```bash
dotnet tool install rtk-dotnet --add-source ./nupkg -g
```

To reference the library from another project:

```bash
dotnet add package RtkFilters.Net --source ./nupkg
```

Uninstall later with `dotnet tool uninstall rtk-dotnet -g` (or `--tool-path`).

## Usage

```bash
rtk-dotnet list
rtk-dotnet verify [--filter NAME] [-v]
rtk-dotnet filter --command "dotnet build" samples/dotnet-build-success.log
rtk-dotnet filter --name dotnet-build samples/dotnet-build-failure.log
rtk-dotnet run --stats -- dotnet build -c Release
rtk-dotnet run -- git status
```

`run` always returns the child's exit code. Stderr is forwarded unfiltered
unless the matching filter sets `filter_stderr = true` (then stdout and stderr
are merged and filtered, matching upstream). Empty filtered output prints
nothing. When no built-in filter matches (for example `git status`, `git diff`,
`git log`, `dotnet test`), the output is passed through unchanged.

From source, without installing the tool:

```bash
dotnet run --project src/RtkFilters.Cli -c Release -- verify
dotnet run --project src/RtkFilters.Cli -c Release -- filter --command "dotnet build" samples/dotnet-build-success.log
dotnet run --project src/RtkFilters.Cli -c Release -- run --stats -- dotnet build src/RtkFilters -c Release
```

Pipeline order matches upstream: `strip_ansi → replace → match_output →
strip/keep lines → truncate_lines_at → head/tail → max_lines → on_empty`.
Token estimates use upstream `rtk-text` semantics: `ceil(utf8_bytes / 4)`.

## Differences from upstream

| Topic | Upstream `rtk` (08a261a) | This port (v0.1.0) |
|---|---|---|
| Scope | Full CLI, hooks, dedicated Rust command modules, optional BUSL engine | Apache-2.0 TOML filter engine + 62 built-in filters only |
| `git`, `dotnet test`, `cargo`, … | Dedicated Rust modules (not TOML) | No TOML filter; `rtk-dotnet run` is a passthrough that keeps the exit code and stderr |
| Project / user `filters.toml`, trust store | Supported | Not implemented |
| Hooks, tee / recovery files, `gain` dashboard | Supported | Not implemented |
| Regex engine | Rust `regex` crate | .NET `Regex`, with `(?P<name>)` and `$name` replacements translated |
| Token estimate | `ceil(len / 4)` on UTF-8 bytes | Same, after a v0.1.0 fix (the scaffold used integer floor division) |
| Missing `schema_version` | `parse_and_compile` rejects the file | Treated as version 1 so a single upstream filter file can be loaded; built-in loading still injects `schema_version = 1` as `build.rs` does |
| Official `rtk` binary | Requires Rust 1.91 | Not built here (this box has rustc 1.85.1). Parity uses `tools/parity-rs`, a standalone Apache-2.0 extract of `toml_filter` + `rtk-text` |

## Parity results

`python3 tools/parity.py` compares every upstream inline TOML test and a set of
engine fixtures against `tools/parity-rs` (Rust `regex` + the upstream pipeline).

- Upstream pin: `08a261a0d081ffe11a32986e2f4d76f3fdc33abb`
- 151 inline tests + 12 extra engine fixtures = 163 cases
- Rust vs .NET consistent: **163**
- Rust vs .NET inconsistent: **0**
- Mismatches against the TOML `expected` values: **0**

See [tools/parity-report.md](tools/parity-report.md). Re-run with
`python3 tools/parity.py`.

## Token savings

Counting method: `ceil(utf8_byte_length / 4)`, matching
`rtk-text::estimate_tokens`. The raw side is the child's stdout. The filtered
side is what `rtk-dotnet` writes to stdout (a trailing newline is added when the
body is non-empty, same as upstream `terminated`). The diagnostic line on
stderr is not counted.

Numbers below were measured on this repository (Asia/Shanghai, 2026-11-10)
with `python3 tools/measure-tokens.py` after a `dotnet clean` + `dotnet build`
of the tree, plus the checked-in sample logs and the broken
`samples/failing-app` project. Live restore/elapsed lines can move a live
build's token count by a few tokens; the sample logs are reproducible.

| Scenario | Filter matched | Raw tokens | Filtered tokens | Saved | % saved |
|---|---|---:|---:|---:|---:|
| This repo: `dotnet build -c Release` | yes (`dotnet-build`) | 122 | 6 | 116 | 95.1% |
| `samples/failing-app` live `dotnet build` (failure) | yes (`dotnet-build`) | 134 | 124 | 10 | 7.5% |
| `samples/dotnet-build-success.log` | yes (`dotnet-build`) | 81 | 6 | 75 | 92.6% |
| `samples/dotnet-build-failure.log` | yes (`dotnet-build`) | 77 | 39 | 38 | 49.4% |
| This repo: `dotnet test` | no (passthrough) | (varies) | same as raw | 0 | 0% |
| `git status` / `git diff` / `git log` | no (passthrough) | (varies) | same as raw | 0 | 0% |

A successful `dotnet build` short-circuits to `ok (build succeeded)`. A failed
build keeps the compiler error lines, the `Build FAILED.` summary, and the
warning/error counts; only banners and blank lines are stripped, so the savings
are smaller on purpose. See [tools/token-savings.md](tools/token-savings.md)
for the exact captured output of a run.

## Known limitations

- No dedicated command modules, hooks, trust store, tee/recovery files, or
  `gain` dashboard.
- Rust `regex` and .NET `Regex` can still differ on untested edge cases. None
  showed up in the 163 parity cases at this pin.
- `git`, `dotnet test`, and other commands that upstream handles in Rust have
  no TOML filter here.
- Custom project/user filter files are not loaded.
- Packages produced by this tree are for local install only; publishing to
  nuget.org is out of scope.

## Layout

```
RtkFilters.Net.sln
src/RtkFilters/          library: TOML parsing, 8-stage filter pipeline, Rust→.NET regex shim
src/RtkFilters.Cli/      rtk-dotnet CLI (list / verify / filter / run)
tests/RtkFilters.Tests/  xUnit: every upstream inline test, engine and CLI tests
upstream/filters/        verbatim upstream filter TOML files (embedded as resources)
samples/                 offline logs and a deliberately broken build sample
tools/parity-rs/         Rust oracle used by tools/parity.py
```
