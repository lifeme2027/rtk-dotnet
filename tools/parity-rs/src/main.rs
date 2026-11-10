//! Standalone copy of the Apache-2.0 TOML filter pipeline from rtk-ai/rtk
//! commit 08a261a0d081ffe11a32986e2f4d76f3fdc33abb (src/core/toml_filter.rs
//! + text/src/lib.rs). Used only to compare behaviour with RtkFilters.Net.
//! Does not include any BUSL-1.1 engine/ or proxy/ code.

use regex::{Regex, RegexSet};
use serde::Deserialize;
use std::collections::BTreeMap;
use std::io::{Read, Write};
use std::path::Path;

fn main() {
    let mut args = std::env::args().skip(1).collect::<Vec<_>>();
    if args.is_empty() || args[0] == "-h" || args[0] == "--help" {
        eprintln!(
            "rtk-parity — upstream-faithful Apache-2.0 TOML filter engine\n\
             \n\
             Usage:\n\
               rtk-parity apply --filters-dir DIR --name NAME < input\n\
               rtk-parity verify --filters-dir DIR"
        );
        std::process::exit(0);
    }
    let cmd = args.remove(0);
    let mut filters_dir = None;
    let mut name = None;
    let mut i = 0;
    while i < args.len() {
        if args[i] == "--filters-dir" && i + 1 < args.len() {
            filters_dir = Some(args[i + 1].clone());
            i += 2;
        } else if args[i] == "--name" && i + 1 < args.len() {
            name = Some(args[i + 1].clone());
            i += 2;
        } else {
            eprintln!("unknown option {}", args[i]);
            std::process::exit(2);
        }
    }
    let dir = filters_dir.expect("--filters-dir is required");
    let (filters, tests) = load_builtin(&dir);
    match cmd.as_str() {
        "apply" => {
            let name = name.expect("apply: --name is required");
            let filter = filters
                .iter()
                .find(|f| f.name == name)
                .unwrap_or_else(|| panic!("unknown filter {name}"));
            let mut input = String::new();
            std::io::stdin().read_to_string(&mut input).unwrap();
            let output = apply_filter(filter, &input);
            write_terminated(&output);
        }
        "verify" => {
            let mut passed = 0;
            let mut failed = 0;
            for tc in &tests {
                let Some(filter) = filters.iter().find(|f| f.name == tc.filter_name) else {
                    eprintln!("FAIL {} :: {} (filter not compiled)", tc.filter_name, tc.name);
                    failed += 1;
                    continue;
                };
                let actual = apply_filter(filter, &tc.input);
                let actual_cmp = actual.trim_end_matches('\n');
                let expected_cmp = tc.expected.trim_end_matches('\n');
                if actual_cmp == expected_cmp {
                    passed += 1;
                } else {
                    failed += 1;
                    eprintln!("FAIL {} :: {}", tc.filter_name, tc.name);
                    eprintln!("  expected: {}", expected_cmp.replace('\n', "\\n"));
                    eprintln!("  actual:   {}", actual_cmp.replace('\n', "\\n"));
                }
            }
            println!(
                "{}/{} inline tests passed across {} filters",
                passed,
                passed + failed,
                tests
                    .iter()
                    .map(|t| t.filter_name.as_str())
                    .collect::<std::collections::BTreeSet<_>>()
                    .len()
            );
            std::process::exit(if failed == 0 { 0 } else { 1 });
        }
        other => {
            eprintln!("unknown command {other}");
            std::process::exit(2);
        }
    }
}

fn write_terminated(text: &str) {
    if text.is_empty() {
        return;
    }
    let mut out = std::io::stdout().lock();
    out.write_all(text.as_bytes()).unwrap();
    if !text.ends_with('\n') {
        out.write_all(b"\n").unwrap();
    }
}

// --- rtk-text primitives (text/src/lib.rs) ---

fn truncate(s: &str, max_len: usize) -> String {
    let char_count = s.chars().count();
    if char_count <= max_len {
        s.to_string()
    } else if max_len < 3 {
        "...".to_string()
    } else {
        format!("{}...", s.chars().take(max_len - 3).collect::<String>())
    }
}

fn strip_ansi(text: &str) -> String {
    let re = Regex::new(r"\x1b\[[0-9;]*[a-zA-Z]").unwrap();
    re.replace_all(text, "").to_string()
}

// --- deserialization (toml_filter.rs) ---

#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct MatchOutputRule {
    pattern: String,
    message: String,
    #[serde(default)]
    unless: Option<String>,
}

#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct ReplaceRule {
    pattern: String,
    replacement: String,
}

#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct TomlFilterTestDef {
    name: String,
    input: String,
    expected: String,
}

#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct TomlFilterDef {
    description: Option<String>,
    match_command: String,
    #[serde(default)]
    strip_ansi: bool,
    #[serde(default)]
    replace: Vec<ReplaceRule>,
    #[serde(default)]
    match_output: Vec<MatchOutputRule>,
    #[serde(default)]
    strip_lines_matching: Vec<String>,
    #[serde(default)]
    keep_lines_matching: Vec<String>,
    truncate_lines_at: Option<usize>,
    head_lines: Option<usize>,
    tail_lines: Option<usize>,
    max_lines: Option<usize>,
    on_empty: Option<String>,
    #[serde(default)]
    filter_stderr: bool,
}

#[derive(Deserialize)]
struct TomlFilterFile {
    schema_version: u32,
    #[serde(default)]
    filters: BTreeMap<String, TomlFilterDef>,
    #[serde(default)]
    tests: BTreeMap<String, Vec<TomlFilterTestDef>>,
}

struct CompiledMatchOutputRule {
    pattern: Regex,
    message: String,
    unless: Option<Regex>,
}

struct CompiledReplaceRule {
    pattern: Regex,
    replacement: String,
}

enum LineFilter {
    None,
    Strip(RegexSet),
    Keep(RegexSet),
}

struct CompiledFilter {
    name: String,
    #[allow(dead_code)]
    description: Option<String>,
    strip_ansi: bool,
    replace: Vec<CompiledReplaceRule>,
    match_output: Vec<CompiledMatchOutputRule>,
    line_filter: LineFilter,
    truncate_lines_at: Option<usize>,
    head_lines: Option<usize>,
    tail_lines: Option<usize>,
    max_lines: Option<usize>,
    on_empty: Option<String>,
    #[allow(dead_code)]
    filter_stderr: bool,
}

struct FilterTestCase {
    filter_name: String,
    name: String,
    input: String,
    expected: String,
}

fn load_builtin(dir: &str) -> (Vec<CompiledFilter>, Vec<FilterTestCase>) {
    let mut files: Vec<_> = std::fs::read_dir(dir)
        .unwrap_or_else(|e| panic!("read {dir}: {e}"))
        .filter_map(|e| e.ok())
        .filter(|e| e.path().extension().and_then(|x| x.to_str()) == Some("toml"))
        .collect();
    files.sort_by_key(|e| e.file_name());
    let mut combined = String::from("schema_version = 1\n\n");
    for entry in &files {
        let content = std::fs::read_to_string(entry.path()).unwrap();
        combined.push_str(&format!(
            "# --- {} ---\n",
            entry.file_name().to_string_lossy()
        ));
        combined.push_str(&content);
        combined.push_str("\n\n");
    }
    parse_and_compile(&combined, Path::new(dir).file_name().unwrap().to_str().unwrap())
}

fn parse_and_compile(content: &str, source: &str) -> (Vec<CompiledFilter>, Vec<FilterTestCase>) {
    let file: TomlFilterFile =
        toml::from_str(content).unwrap_or_else(|e| panic!("TOML parse error in {source}: {e}"));
    if file.schema_version != 1 {
        panic!(
            "unsupported schema_version {} in {} (expected 1)",
            file.schema_version, source
        );
    }
    let mut compiled = Vec::new();
    for (name, def) in file.filters {
        if !is_fully_anchored(&def.match_command) {
            eprintln!(
                "[rtk-parity] warning: filter '{name}' in {source}: match_command not fully anchored; ignored"
            );
            continue;
        }
        match compile_filter(name.clone(), def) {
            Ok(f) => compiled.push(f),
            Err(e) => eprintln!("[rtk-parity] warning: filter '{name}' in {source}: {e}"),
        }
    }
    let mut tests = Vec::new();
    for (filter_name, cases) in file.tests {
        for t in cases {
            tests.push(FilterTestCase {
                filter_name: filter_name.clone(),
                name: t.name,
                input: t.input,
                expected: t.expected,
            });
        }
    }
    (compiled, tests)
}

fn compile_filter(name: String, def: TomlFilterDef) -> Result<CompiledFilter, String> {
    if !def.strip_lines_matching.is_empty() && !def.keep_lines_matching.is_empty() {
        return Err(
            "strip_lines_matching and keep_lines_matching are mutually exclusive".into(),
        );
    }
    let _match_regex = Regex::new(&def.match_command)
        .map_err(|e| format!("invalid match_command regex: {e}"))?;

    let replace = def
        .replace
        .into_iter()
        .map(|r| {
            let pat = r.pattern.clone();
            Regex::new(&r.pattern)
                .map(|pattern| CompiledReplaceRule {
                    pattern,
                    replacement: r.replacement,
                })
                .map_err(|e| format!("invalid replace pattern '{pat}': {e}"))
        })
        .collect::<Result<Vec<_>, _>>()?;

    let match_output = def
        .match_output
        .into_iter()
        .map(|r| -> Result<CompiledMatchOutputRule, String> {
            let pat = r.pattern.clone();
            let pattern = Regex::new(&r.pattern)
                .map_err(|e| format!("invalid match_output pattern '{pat}': {e}"))?;
            let unless = match r.unless.as_deref() {
                Some(u) => Some(
                    Regex::new(u)
                        .map_err(|e| format!("invalid match_output unless pattern '{u}': {e}"))?,
                ),
                None => None,
            };
            Ok(CompiledMatchOutputRule {
                pattern,
                message: r.message,
                unless,
            })
        })
        .collect::<Result<Vec<_>, _>>()?;

    let line_filter = if !def.strip_lines_matching.is_empty() {
        let set = RegexSet::new(&def.strip_lines_matching)
            .map_err(|e| format!("invalid strip_lines_matching regex: {e}"))?;
        LineFilter::Strip(set)
    } else if !def.keep_lines_matching.is_empty() {
        let set = RegexSet::new(&def.keep_lines_matching)
            .map_err(|e| format!("invalid keep_lines_matching regex: {e}"))?;
        LineFilter::Keep(set)
    } else {
        LineFilter::None
    };

    Ok(CompiledFilter {
        name,
        description: def.description,
        strip_ansi: def.strip_ansi,
        replace,
        match_output,
        line_filter,
        truncate_lines_at: def.truncate_lines_at,
        head_lines: def.head_lines,
        tail_lines: def.tail_lines,
        max_lines: def.max_lines,
        on_empty: def.on_empty,
        filter_stderr: def.filter_stderr,
    })
}

fn strip_inline_flags(branch: &str) -> &str {
    let rest = match branch.strip_prefix("(?") {
        Some(r) => r,
        None => return branch,
    };
    let end = match rest.find(')') {
        Some(e) => e,
        None => return branch,
    };
    if rest[..end].chars().all(|c| "imsuxU-".contains(c)) {
        &rest[end + 1..]
    } else {
        branch
    }
}

fn top_level_branches(pattern: &str) -> Vec<&str> {
    let mut branches = Vec::new();
    let mut depth = 0usize;
    let mut escaped = false;
    let mut in_class = false;
    let mut start = 0usize;
    for (idx, character) in pattern.char_indices() {
        if escaped {
            escaped = false;
            continue;
        }
        match character {
            '\\' => escaped = true,
            '[' if !in_class => in_class = true,
            ']' if in_class => in_class = false,
            '(' if !in_class => depth += 1,
            ')' if !in_class => depth = depth.saturating_sub(1),
            '|' if !in_class && depth == 0 => {
                branches.push(&pattern[start..idx]);
                start = idx + 1;
            }
            _ => {}
        }
    }
    branches.push(&pattern[start..]);
    branches
}

fn is_fully_anchored(pattern: &str) -> bool {
    top_level_branches(pattern)
        .iter()
        .all(|branch| strip_inline_flags(branch).starts_with('^'))
}

fn apply_filter(filter: &CompiledFilter, stdout: &str) -> String {
    let mut lines: Vec<String> = stdout.lines().map(String::from).collect();

    if filter.strip_ansi {
        lines = lines.into_iter().map(|l| strip_ansi(&l)).collect();
    }

    if !filter.replace.is_empty() {
        lines = lines
            .into_iter()
            .map(|mut line| {
                for rule in &filter.replace {
                    line = rule
                        .pattern
                        .replace_all(&line, rule.replacement.as_str())
                        .into_owned();
                }
                line
            })
            .collect();
    }

    if !filter.match_output.is_empty() {
        let blob = lines.join("\n");
        for rule in &filter.match_output {
            if rule.pattern.is_match(&blob) {
                if let Some(ref unless_re) = rule.unless {
                    if unless_re.is_match(&blob) {
                        continue;
                    }
                }
                return rule.message.clone();
            }
        }
    }

    match &filter.line_filter {
        LineFilter::Strip(set) => lines.retain(|l| !set.is_match(l)),
        LineFilter::Keep(set) => lines.retain(|l| set.is_match(l)),
        LineFilter::None => {}
    }

    if let Some(max_chars) = filter.truncate_lines_at {
        lines = lines
            .into_iter()
            .map(|line| truncate(&line, max_chars))
            .collect();
    }

    let total = lines.len();
    if let (Some(head), Some(tail)) = (filter.head_lines, filter.tail_lines) {
        if total > head + tail {
            let mut result = lines[..head].to_vec();
            result.push(format!("... ({} lines omitted)", total - head - tail));
            result.extend_from_slice(&lines[total - tail..]);
            lines = result;
        }
    } else if let Some(head) = filter.head_lines {
        if total > head {
            lines.truncate(head);
            lines.push(format!("... ({} lines omitted)", total - head));
        }
    } else if let Some(tail) = filter.tail_lines {
        if total > tail {
            let omitted = total - tail;
            lines = lines[omitted..].to_vec();
            lines.insert(0, format!("... ({} lines omitted)", omitted));
        }
    }

    if let Some(max) = filter.max_lines {
        if lines.len() > max {
            let dropped = lines.len() - max;
            lines.truncate(max);
            lines.push(format!("... ({} lines truncated)", dropped));
        }
    }

    let result = lines.join("\n");
    if result.trim().is_empty() {
        if let Some(ref msg) = filter.on_empty {
            return msg.clone();
        }
    }
    result
}
