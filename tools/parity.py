#!/usr/bin/env python3
"""Compare RtkFilters.Net with a standalone Rust copy of rtk's Apache-2.0
TOML filter pipeline (upstream commit 08a261a). Does not invoke BUSL code.

Usage (from repo root):
  python3 tools/parity.py
  python3 tools/parity.py --report tools/parity-report.md
"""
from __future__ import annotations

import argparse
import json
import os
import subprocess
import sys
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
FILTERS_DIR = ROOT / "upstream" / "filters"
PARITY_RS = ROOT / "tools" / "parity-rs"
UPSTREAM_COMMIT = (ROOT / "upstream" / "UPSTREAM_COMMIT").read_text().strip()


def run(cmd, *, cwd=None, input_text=None, check=True):
    result = subprocess.run(
        cmd,
        cwd=cwd,
        input=input_text,
        text=True,
        capture_output=True,
        check=False,
    )
    if check and result.returncode != 0:
        raise SystemExit(
            f"command failed ({result.returncode}): {' '.join(cmd)}\n"
            f"stdout:\n{result.stdout}\nstderr:\n{result.stderr}"
        )
    return result


def build_rust() -> Path:
    print("Building rtk-parity (Rust, Apache-2.0 subset)...", flush=True)
    run(["cargo", "build", "--release"], cwd=PARITY_RS)
    exe = PARITY_RS / "target" / "release" / "rtk-parity"
    if not exe.exists():
        raise SystemExit(f"missing {exe}")
    return exe


def build_dotnet() -> Path:
    print("Building rtk-dotnet (Release)...", flush=True)
    run(["dotnet", "build", "-c", "Release", "--nologo", "src/RtkFilters.Cli/RtkFilters.Cli.csproj"], cwd=ROOT)
    exe = ROOT / "src" / "RtkFilters.Cli" / "bin" / "Release" / "net8.0" / "rtk-dotnet"
    if not exe.exists():
        raise SystemExit(f"missing {exe}")
    return exe


def load_inline_tests() -> list[dict]:
    """Parse [[tests.*]] from the concatenated builtin TOML via rtk-dotnet verify -v is
    not enough; we extract cases from the built-in registry with a tiny helper.
    """
    # Use a one-off `dotnet exec` isn't available; instead parse TOML in Python 3.11+.
    import tomllib

    cases = []
    for path in sorted(FILTERS_DIR.glob("*.toml")):
        data = tomllib.loads(path.read_text())
        tests = data.get("tests") or {}
        for filter_name, arr in tests.items():
            for t in arr:
                cases.append(
                    {
                        "filter": filter_name,
                        "name": t["name"],
                        "input": t["input"],
                        "expected": t["expected"],
                        "source": path.name,
                    }
                )
    return cases


def apply_engine(exe: Path, kind: str, filter_name: str, input_text: str) -> str:
    if kind == "rust":
        cmd = [str(exe), "apply", "--filters-dir", str(FILTERS_DIR), "--name", filter_name]
    else:
        cmd = [str(exe), "filter", "--name", filter_name]
    result = run(cmd, input_text=input_text, check=False)
    if result.returncode != 0:
        raise SystemExit(
            f"{kind} apply failed for {filter_name}: {result.returncode}\n{result.stderr}"
        )
    return result.stdout


def extra_fixtures() -> list[dict]:
    """Engine cases taken from upstream src/core/toml_filter.rs unit tests, plus
    command-matching checks that do not live in the TOML files.
    """
    return [
        {
            "filter": "_engine:strip_ansi",
            "name": "strip_ansi",
            "toml": '[filters.x]\nmatch_command = "^x"\nstrip_ansi = true\n',
            "input": "\x1b[31mError\x1b[0m",
            "expected": "Error",
        },
        {
            "filter": "_engine:replace-chain",
            "name": "replace-chain",
            "toml": '[filters.x]\nmatch_command = "^x"\nreplace = [{ pattern = "aaa", replacement = "bbb" }, { pattern = "bbb", replacement = "ccc" }]\n',
            "input": "aaa",
            "expected": "ccc",
        },
        {
            "filter": "_engine:backref",
            "name": "replace-backref",
            "toml": '[filters.x]\nmatch_command = "^x"\nreplace = [{ pattern = "(\\\\w+):(\\\\w+)", replacement = "$2:$1" }]\n',
            "input": "hello:world",
            "expected": "world:hello",
        },
        {
            "filter": "_engine:head-tail",
            "name": "head-and-tail",
            "toml": '[filters.x]\nmatch_command = "^x"\nhead_lines = 1\ntail_lines = 1\n',
            "input": "1\n2\n3\n4",
            "expected": "1\n... (2 lines omitted)\n4",
        },
        {
            "filter": "_engine:max-lines",
            "name": "max-lines",
            "toml": '[filters.x]\nmatch_command = "^x"\nmax_lines = 2\n',
            "input": "1\n2\n3\n4",
            "expected": "1\n2\n... (2 lines truncated)",
        },
        {
            "filter": "_engine:on-empty",
            "name": "on-empty",
            "toml": '[filters.x]\nmatch_command = "^x"\nstrip_lines_matching = [".*"]\non_empty = "x: ok"\n',
            "input": "noise\nmore noise",
            "expected": "x: ok",
        },
        {
            "filter": "_engine:unless",
            "name": "unless-veto",
            "toml": '[filters.x]\nmatch_command = "^x"\nmatch_output = [{ pattern = "done", message = "ok", unless = "error" }]\n',
            "input": "error\ndone",
            "expected": "error\ndone",
        },
        {
            "filter": "_engine:unless",
            "name": "unless-allows",
            "toml": '[filters.x]\nmatch_command = "^x"\nmatch_output = [{ pattern = "done", message = "ok", unless = "error" }]\n',
            "input": "done",
            "expected": "ok",
        },
        {
            "filter": "_engine:truncate-cjk",
            "name": "truncate-cjk",
            "toml": '[filters.x]\nmatch_command = "^x"\ntruncate_lines_at = 5\n',
            "input": "日本語テキスト",
            "expected": "日本...",
        },
    ]


def apply_custom(rust: Path, dotnet: Path, toml: str, input_text: str) -> tuple[str, str]:
    """Spin a one-filter TOML file and run both engines against it.

    The Rust harness only loads a directory of builtin-style files, so we write a
    temporary filters dir with schema-less single file (header injected).
    The .NET side uses FilterRegistry.Parse via a tiny stdin protocol: we reuse
    `rtk-dotnet filter --name x` after placing the filter... but rtk-dotnet only
    loads builtins. Instead compare by compiling both against a temp dir for Rust
    and by embedding the same pipeline in a second invocation of a small C# snippet
    — simpler: write the custom TOML into a temp dir and also concatenate it as a
    builtin override is hard.

    For custom fixtures we compare Rust output to the documented expected value
    AND .NET output (via a `dotnet exec` helper printed below using the library
    through a `tools` approach): call `rtk-dotnet` cannot load custom files yet.

    We therefore evaluate .NET custom fixtures by writing a short `dotnet script`
    alternative: invoke the already-tested library through `csharp` isn't available.

    Fallback: run expected vs each engine. For .NET custom cases, shell out to
    `dotnet test` is already covering them; here we still run Rust vs expected
    and, when possible, .NET via a temp project... Too heavy.

    Practical approach: a hidden `--from-toml FILE` would be a CLI feature.
    Added below only if the CLI supports it. We pass the TOML as a file to
    a new `--toml` option if present; otherwise we only compare Rust vs expected
    and record .NET custom results from a small helper process using `dotnet run`
    of a one-file program.

    This function is replaced by apply_custom_via_helper once the helper exists.
    """
    raise NotImplementedError


def write_custom_runner(path: Path) -> None:
    path.write_text(
        """
using RtkFilters;
var toml = File.ReadAllText(args[0]);
var input = Console.In.ReadToEnd();
var reg = FilterRegistry.Parse(toml, "parity-custom");
if (reg.Warnings.Count > 0) { foreach (var w in reg.Warnings) Console.Error.WriteLine(w); Environment.Exit(3); }
var f = reg.Filters.Single();
var output = f.Apply(input);
if (output.Length > 0) {
    Console.Write(output);
    if (!output.EndsWith("\\n")) Console.Write("\\n");
}
""".replace("\\n", "\\n")
    )
    # The above replace is confusing; write raw in caller instead.


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--report", type=Path, default=ROOT / "tools" / "parity-report.md")
    parser.add_argument("--skip-build", action="store_true")
    args = parser.parse_args()

    rust = PARITY_RS / "target" / "release" / "rtk-parity"
    dotnet = ROOT / "src" / "RtkFilters.Cli" / "bin" / "Release" / "net8.0" / "rtk-dotnet"
    if not args.skip_build or not rust.exists():
        rust = build_rust()
    if not args.skip_build or not dotnet.exists():
        dotnet = build_dotnet()

    cases = load_inline_tests()
    print(f"Loaded {len(cases)} upstream inline tests from {FILTERS_DIR}")

    mismatches = []
    rust_expected_fail = []
    dotnet_expected_fail = []
    rust_dotnet_fail = []
    compared = 0

    for i, case in enumerate(cases, 1):
        rust_out = apply_engine(rust, "rust", case["filter"], case["input"])
        dotnet_out = apply_engine(dotnet, "dotnet", case["filter"], case["input"])
        # Compare the apply() result, not the CLI terminator. Both CLIs add a
        # trailing newline when the body is non-empty, so compare trimmed-end.
        r = rust_out.removesuffix("\n") if rust_out.endswith("\n") else rust_out
        d = dotnet_out.removesuffix("\n") if dotnet_out.endswith("\n") else dotnet_out
        e = case["expected"].rstrip("\n")
        compared += 1
        if r != e:
            rust_expected_fail.append(case | {"rust": r, "expected_cmp": e})
        if d != e:
            dotnet_expected_fail.append(case | {"dotnet": d, "expected_cmp": e})
        if r != d:
            rust_dotnet_fail.append(case | {"rust": r, "dotnet": d})
        if i % 40 == 0:
            print(f"  ... {i}/{len(cases)}", flush=True)

    # Custom engine fixtures: Rust temp dir + a tiny .NET helper compiled once.
    custom = extra_fixtures()
    custom_rd_fail = []
    custom_ok = 0
    with tempfile.TemporaryDirectory() as td:
        td_path = Path(td)
        helper_cs = td_path / "Helper.csproj"
        # Use a throwaway project referencing the already-built library.
        lib_csproj = ROOT / "src" / "RtkFilters" / "RtkFilters.csproj"
        (td_path / "Helper.csproj").write_text(
            f"""<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <TreatWarningsAsErrors>false</TreatWarningsAsErrors>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="{lib_csproj}" />
  </ItemGroup>
</Project>
"""
        )
        # Prevent repo Directory.Build.props from breaking the helper
        (td_path / "Directory.Build.props").write_text("<Project />\n")
        (td_path / "Program.cs").write_text(
            r"""using RtkFilters;
var toml = File.ReadAllText(args[0]);
var input = Console.In.ReadToEnd();
var reg = FilterRegistry.Parse(toml, "parity-custom");
if (reg.Filters.Count != 1) { Console.Error.WriteLine("expected 1 filter, got " + reg.Filters.Count + " warnings=" + string.Join(";", reg.Warnings)); Environment.Exit(3); }
var output = reg.Filters[0].Apply(input);
if (output.Length == 0) return;
Console.Write(output);
if (!output.EndsWith("\n")) Console.Write('\n');
"""
        )
        run(["dotnet", "build", "-c", "Release", "--nologo"], cwd=td_path)
        helper = td_path / "bin" / "Release" / "net8.0" / "Helper"
        rust_filters = td_path / "rust-filters"
        for fx in custom:
            rust_filters.mkdir(exist_ok=True)
            # isolate one filter per run
            for p in rust_filters.glob("*.toml"):
                p.unlink()
            (rust_filters / "x.toml").write_text(fx["toml"])
            r_res = run(
                [str(rust), "apply", "--filters-dir", str(rust_filters), "--name", "x"],
                input_text=fx["input"],
                check=False,
            )
            d_res = run([str(helper)], input_text=fx["input"], check=False)
            # helper takes toml path
            d_res = run(
                [str(helper), str(rust_filters / "x.toml")],
                input_text=fx["input"],
                check=False,
            )
            if r_res.returncode != 0 or d_res.returncode != 0:
                custom_rd_fail.append(
                    fx
                    | {
                        "rust": r_res.stderr or r_res.stdout,
                        "dotnet": d_res.stderr or d_res.stdout,
                        "note": f"exit rust={r_res.returncode} dotnet={d_res.returncode}",
                    }
                )
                continue
            r = r_res.stdout.removesuffix("\n") if r_res.stdout.endswith("\n") else r_res.stdout
            d = d_res.stdout.removesuffix("\n") if d_res.stdout.endswith("\n") else d_res.stdout
            e = fx["expected"].rstrip("\n")
            compared += 1
            if r != d or r != e or d != e:
                custom_rd_fail.append(fx | {"rust": r, "dotnet": d, "expected_cmp": e})
            else:
                custom_ok += 1

    consistent = compared - len(rust_dotnet_fail) - len(custom_rd_fail)
    # custom_rd_fail already counted in compared; rust_dotnet_fail is inline only
    inline_consistent = len(cases) - len(rust_dotnet_fail)
    custom_consistent = len(custom) - len(custom_rd_fail)
    consistent = inline_consistent + custom_consistent
    inconsistent = len(rust_dotnet_fail) + len(custom_rd_fail)

    lines = []
    lines.append("# rtk-dotnet parity report")
    lines.append("")
    lines.append(f"- Date (Asia/Shanghai): generated by `tools/parity.py`")
    lines.append(f"- Upstream pin: `{UPSTREAM_COMMIT}`")
    lines.append("- Rust oracle: `tools/parity-rs` (copy of Apache-2.0 `toml_filter` + `rtk-text` only; no BUSL code).")
    lines.append("- Official `rtk` binary was not built: upstream requires Rust 1.91 and this box has rustc 1.85.1.")
    lines.append(f"- Upstream inline tests: {len(cases)}")
    lines.append(f"- Extra engine fixtures: {len(custom)}")
    lines.append(f"- Compared: {compared}")
    lines.append(f"- Rust vs .NET consistent: {consistent}")
    lines.append(f"- Rust vs .NET inconsistent: {inconsistent}")
    lines.append(f"- Rust vs TOML expected mismatches: {len(rust_expected_fail)}")
    lines.append(f"- .NET vs TOML expected mismatches: {len(dotnet_expected_fail)}")
    lines.append("")
    if not rust_dotnet_fail and not custom_rd_fail:
        lines.append("All compared cases produced identical output (after trimming a single trailing newline).")
    else:
        lines.append("## Remaining differences")
        for item in rust_dotnet_fail + custom_rd_fail:
            lines.append(f"### {item.get('filter')} :: {item.get('name')}")
            lines.append("")
            lines.append("```")
            lines.append("rust:   " + repr(item.get("rust")))
            lines.append("dotnet: " + repr(item.get("dotnet")))
            if "expected_cmp" in item:
                lines.append("expect: " + repr(item.get("expected_cmp")))
            if "note" in item:
                lines.append("note:   " + item["note"])
            lines.append("```")
            lines.append("")
    report = "\n".join(lines) + "\n"
    args.report.write_text(report)
    print(report)
    print(f"Wrote {args.report}")
    return 0 if inconsistent == 0 and not rust_expected_fail and not dotnet_expected_fail else 1


if __name__ == "__main__":
    sys.exit(main())
