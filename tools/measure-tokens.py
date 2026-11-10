#!/usr/bin/env python3
"""Measure token savings of rtk-dotnet on real command output.

Token estimate matches upstream rtk-text: ceil(utf8_byte_length / 4).
The raw side is the child's stdout (stderr is only included when the matching
filter sets filter_stderr). The filtered side is the text rtk-dotnet writes to
stdout; the `[rtk-dotnet] filter=...` diagnostic on stderr is not counted.
"""
from __future__ import annotations

import math
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
DOTNET = ROOT / "src" / "RtkFilters.Cli" / "bin" / "Release" / "net8.0" / "rtk-dotnet"


def estimate(text: str) -> int:
    n = len(text.encode("utf-8"))
    return math.ceil(n / 4.0) if n else 0


def run_capture(cmd: list[str], *, cwd: Path) -> tuple[int, str, str]:
    r = subprocess.run(cmd, cwd=cwd, text=True, capture_output=True)
    return r.returncode, r.stdout, r.stderr


def filter_text(command: str, raw: str) -> tuple[str, str]:
    r = subprocess.run(
        [str(DOTNET), "filter", "--command", command],
        input=raw,
        text=True,
        capture_output=True,
        cwd=ROOT,
    )
    if r.returncode != 0:
        raise SystemExit(f"filter failed for {command}: {r.stderr}")
    return r.stdout, r.stderr


def row(name: str, command: str, raw: str, note: str) -> dict:
    filtered, stats = filter_text(command, raw)
    # rtk-dotnet writes a trailing newline for non-empty bodies; count the
    # exact stdout bytes it would emit to an agent.
    before = estimate(raw)
    after = estimate(filtered)
    saved = before - after
    pct = (100.0 * saved / before) if before else 0.0
    matched = "no filter matched" not in stats
    return {
        "name": name,
        "command": command,
        "matched": matched,
        "exit_note": note,
        "raw_bytes": len(raw.encode("utf-8")),
        "filtered_bytes": len(filtered.encode("utf-8")),
        "before": before,
        "after": after,
        "saved": saved,
        "pct": pct,
        "raw": raw,
        "filtered": filtered,
        "stats": stats.strip(),
    }


def main() -> int:
    if not DOTNET.exists():
        raise SystemExit(f"missing {DOTNET}; build Release first")

    rows = []

    # 1) This repo, a real rebuild so the log is not "up to date and empty".
    print("dotnet clean + build (this repo)...", flush=True)
    subprocess.run(["dotnet", "clean", "-c", "Release", "--nologo"], cwd=ROOT, check=False, capture_output=True)
    code, out, err = run_capture(
        ["dotnet", "build", "-c", "Release", "--nologo"],
        cwd=ROOT,
    )
    raw = out + (err if err and "error" in err.lower() else "")
    # MSBuild writes the useful log to stdout; keep stdout as the raw for the
    # dotnet-build filter (filter_stderr is false).
    raw = out
    rows.append(row("this repo: dotnet build -c Release", "dotnet build -c Release", raw, f"exit {code}"))

    # 2) Failed build example (the checked-in broken sample).
    print("dotnet build (failing-app sample)...", flush=True)
    fail_dir = ROOT / "samples" / "failing-app"
    code, out, err = run_capture(
        ["dotnet", "build", "-c", "Release", "--nologo"],
        cwd=fail_dir,
    )
    rows.append(
        row(
            "failing-app sample: dotnet build (expected failure)",
            "dotnet build -c Release",
            out,
            f"exit {code}",
        )
    )

    # 3) Commands with no TOML filter (passthrough, 0% saved). Use --porcelain
    # / a bounded log so the committed report does not include a dirty worktree.
    print("dotnet test (this repo)...", flush=True)
    code, out, err = run_capture(
        ["dotnet", "test", "-c", "Release", "--nologo", "--no-build"],
        cwd=ROOT,
    )
    rows.append(row("this repo: dotnet test -c Release --no-build", "dotnet test -c Release --nologo --no-build", out, f"exit {code}"))

    print("git passthrough probes...", flush=True)
    for name, cmd in [
        ("this repo: git status --porcelain", ["git", "status", "--porcelain"]),
        ("this repo: git log -1 --oneline", ["git", "log", "-1", "--oneline"]),
        ("this repo: git diff --stat", ["git", "diff", "--stat"]),
    ]:
        code, out, err = run_capture(cmd, cwd=ROOT)
        rows.append(row(name, " ".join(cmd), out, f"exit {code}"))

    # Also report the checked-in sample logs (stable, reproducible numbers).
    for label, path, command in [
        ("sample log: dotnet-build-success.log", ROOT / "samples" / "dotnet-build-success.log", "dotnet build"),
        ("sample log: dotnet-build-failure.log", ROOT / "samples" / "dotnet-build-failure.log", "dotnet build"),
    ]:
        raw = path.read_text()
        rows.append(row(label, command, raw, "offline fixture"))

    # Markdown table
    lines = [
        "# Token savings",
        "",
        "Counting method: `ceil(utf8_byte_length / 4)`, matching upstream",
        "`rtk-text::estimate_tokens`. Raw text is the child's stdout.",
        "Filtered text is `rtk-dotnet` stdout (a single trailing newline is",
        "added when the body is non-empty, same as upstream `terminated`).",
        "The `[rtk-dotnet] ... tokens ...` line on stderr is not counted.",
        "",
        "| Scenario | Filter matched | Raw tokens | Filtered tokens | Saved | % saved | Notes |",
        "|---|---|---:|---:|---:|---:|---|",
    ]
    for r in rows:
        matched = r["command"].split()[0]  # placeholder
        matched = "yes" if r["matched"] else "no (passthrough)"
        lines.append(
            f"| {r['name']} | {matched} | {r['before']} | {r['after']} | {r['saved']} | {r['pct']:.1f}% | {r['exit_note']} |"
        )
    lines.append("")
    lines.append("## Per-scenario filtered output (truncated to 20 lines)")
    lines.append("")
    for r in rows:
        lines.append(f"### {r['name']}")
        lines.append("")
        lines.append(f"- Command line used for lookup: `{r['command']}`")
        lines.append(f"- {r['stats'] or 'passthrough (no diagnostic)'}")
        lines.append("")
        preview = r["filtered"] if r["filtered"] else "(empty)"
        preview_lines = preview.splitlines()
        if len(preview_lines) > 20:
            preview = "\n".join(preview_lines[:20]) + f"\n... ({len(preview_lines) - 20} more lines)"
        lines.append("```")
        lines.append(preview.rstrip("\n"))
        lines.append("```")
        lines.append("")

    report = "\n".join(lines) + "\n"
    out_path = ROOT / "tools" / "token-savings.md"
    out_path.write_text(report)
    print(report)
    print(f"Wrote {out_path}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
