# Changelog

All notable changes to this unofficial port are recorded here.

## 0.1.0 — 2026-11-10

First locally packable release of RtkFilters.Net / `rtk-dotnet`.

### Added

- `rtk-dotnet --version` (prints `0.1.0`).
- `rtk-dotnet filter --name NAME` (also accepted as `--filter`) so a filter can
  be applied without a command line to match.
- CLI `run` always captures and forwards stderr unfiltered unless the filter
  sets `filter_stderr`, and always returns the child's exit code. A command
  that cannot be started exits 127.
- Output termination matches upstream `runner::terminated`: empty bodies print
  nothing; non-empty bodies have exactly one trailing newline.
- NOTICE file with Apache-2.0 attribution. Packable 0.1.0 metadata for both the
  `rtk-dotnet` dotnet tool and the `RtkFilters.Net` library (`dotnet pack`).
  Package descriptions start with the `Unofficial` prefix.
- `tools/parity-rs` + `tools/parity.py`: compare this engine with a standalone
  Rust copy of the Apache-2.0 TOML pipeline (no BUSL code). 160/160 cases
  consistent at upstream pin `08a261a`.
- `tools/measure-tokens.py` and `tools/token-savings.md` for real token-savings
  measurements (`ceil(utf8_bytes / 4)`).
- Offline `samples/dotnet-build-failure.log` and `samples/failing-app`.
- Additional engine and CLI tests (estimate-tokens, replace chaining,
  backreferences, head/tail, keep-lines, git/dotnet-test passthrough, exit
  codes, failure output retained).

### Fixed

- Token estimates now use upstream `ceil(utf8_byte_length / 4)` instead of
  truncating integer division.

### Not in this release

- Publishing to nuget.org, a public GitHub repository, or tags.
- Upstream dedicated command modules, hooks, trust store, tee/recovery, and
  the BUSL-licensed engine/proxy.
