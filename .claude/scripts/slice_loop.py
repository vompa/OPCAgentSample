#!/usr/bin/env python3
"""Builds the open round slice by slice, each slice in a fresh headless Claude session, then closes the round.
A slice that ends with SLICE-READY is squashed by the merge queue: one slice-NN branch after another, in this
checkout, with the message from .slice-loop/slice-NN.msg, once .slice-loop/slice-NN.ready names the branch tip (the
session wrote it last, at SLICE-READY) and .slice-loop/slice-NN.checks proves green checks on
the branch tip. Only proof lines of the branch tip count (lines of other commits are stale runs and ignored); of each
command the last line on the tip counts. Conflicts are resolved by rule and checked: work/checklist.md by the union of
ticks, and in any text file a hunk where both sides only added lines (both kept) or changed rows of one table (merged
per row key, e.g. the status row of a round). A slice cut from an older integration branch is squashed as it is,
unless the integration branch changed since then a file the slice changes too (documents *.md and work/ aside). That
overlap, a real conflict, a missing proof, a duplicate finding id, a line glued to the next one in the round record, a
decision line in neither format ("Ruling: ...", "Build decision (slice NN): ...") or a malformed manual test block leaves
that slice behind with its branch; the queue goes on, lists every slice left behind and exits non-zero. A slice whose
session has not reported ready on its tip yet is listed as not ready yet and does not fail --merge. Removing the
worktree of a squashed slice is retried, and the entries of every folder a link in the worktree points to are counted
before and after: fewer afterwards is reported and makes the run exit non-zero (the squash stays done). A stale
.slice-loop/full-suite.lock (its slice branch is gone, or it is older than 60 minutes) is reported at the start, never
removed; so is a work/conventions.md without a "- Baseline check" line (its keys stay English in any language).
Stops for plan approval unless --yes. With --yes a NEEDS-DECISION parks its slice, and with --parallel so does a red
slice: .slice-loop/slice-NN.parked names the reason, the other slices go on, the run lists it and exits non-zero, and
the next run builds it once .slice-loop/slice-NN.answer holds the answer. Otherwise red tests or a blocked slice stop the
run. A turn without activity (log, transcript, the slice's files in .slice-loop) for SLICE_STALL_SECONDS is stopped with
its processes and resumed once; a second silence ends the slice. A slice that ends its turn with SLICE-WAIT is still
running: the loop checks back on it in the same session. Headless claude ends background commands with the turn, so
turns run with a Bash timeout of SLICE_STALL_SECONDS and checks belong in the foreground. Each turn gets a SessionStart
hook that re-reads the rules after a compaction. .slice-loop/events.jsonl records started, stalled and parked slices.

Usage: python .claude/scripts/slice_loop.py [--yes] [--parallel] [--dry-run] [--max-slices N] [--merge [--verify] [--watch]]
  (on macOS and Linux the interpreter may be called python3)
  --yes           approve plans without asking (a slice with a NEEDS-DECISION is parked)
  --parallel      build slices that are ready at the same time side by side (headless; for long autonomous runs):
                  at most SLICE_PARALLEL_MAX per batch, and a slice whose "files" in work/_slices.json overlap a
                  slice already in the batch waits for the next one
  --dry-run       only print the slices that are ready now, then exit
  --max-slices N  stop after N built slices if slices are left (exit 0; run again to continue)
  --merge         only run the merge queue: squash every slice-NN branch ticked through Checks, retry removing
                  worktrees an earlier squash left, then exit (starts no session, needs no open round)
  --verify        with --merge: after the queue, when it squashed a slice, run every command of the "- Full check
                  chain" line in work/conventions.md on the integration branch, under .slice-loop/full-suite.lock
                  (red: exit 1)
  --watch         with --merge: wait until a slice reports ready on a new tip, run the queue once, then exit (a
                  background command, so it wakes the chat that started it). A slice left behind brings it back only
                  with a new tip; a second watch next to a running one exits at once
  --notify        wait until a slice has a problem or a question for the user, print it and exit (a background command, so
                  it wakes the chat that started it); uses SLICE_WATCH_SECONDS and SLICE_WATCH_MAX
Optional field "files" of an entry in work/_slices.json: the repo-relative paths (or prefixes ending in "/") the slice
is expected to change.
Env: SLICE_PERMISSION_MODE (default: auto) - passed to claude --permission-mode.
     SLICE_PARALLEL_MAX (default: 2) - most slices in one --parallel batch (three sessions on one machine made unrelated tests red under load).
     SLICE_WAIT_SECONDS (default: 300), SLICE_WAIT_MAX (default: 30) - pause and number of check-backs on SLICE-WAIT.
     SLICE_STALL_SECONDS (default: 7200) - a turn without activity for this long is stopped; also its Bash timeout.
     SLICE_WATCH_SECONDS (default: 60), SLICE_WATCH_MAX (default: 480) - pause and number of checks of --watch.
     SLICE_WRITE_RETRIES (default: 5), SLICE_WRITE_BACKOFF_SECONDS (default: 2) - retries and pause when the queue
     cannot write a file or reset a merge because another process (scanner, indexer, editor) holds it, and when git
     cannot remove the worktree of a squashed slice (a shell of the finished session may still stand in it).
Exit codes: 0 success (also: no open round, nothing to do), 1 failure or a slice left behind, 130 interrupted.
"""
from __future__ import annotations

import datetime
import glob
import json
import os
import pathlib
import re
import shutil
import signal
import stat
import subprocess
import sys
import threading
import time
import uuid

SLICES_FILE = "work/_slices.json"
CHECKLIST_FILE = "work/checklist.md"
CONVENTIONS_FILE = "work/conventions.md"
ROUNDS_DIR = "work/rounds"
LOG_DIR = ".slice-loop"
WATCH_ALIVE = f"{LOG_DIR}/watch.alive"  # sign of life of a running --watch, written at every check
MERGE_LOCK = f"{LOG_DIR}/merge.lock"  # taken (mkdir) by a running --merge queue; older than 60 minutes: its owner died
LOCK_DIR = f"{LOG_DIR}/full-suite.lock"  # taken (mkdir) by whoever runs the full suite; holds a file "owner"
EVENTS_FILE = f"{LOG_DIR}/events.jsonl"  # one JSON line per started, stalled or parked slice; read when a run stopped
COMPACT_HOOK_FILE = f"{LOG_DIR}/compact-hook.json"  # settings of every turn: the rules are read again after a compaction
NOTIFY_SEEN = f"{LOG_DIR}/notify.seen"  # how many lines of the event history --notify has passed on
USAGE = "use --yes, --parallel, --dry-run, --max-slices N, --merge, --verify, --watch (with --merge) and --notify"
LOCK_MAX_AGE_SECONDS = 3600
CMD_UNSAFE = set('%^&|<>"\r\n')  # characters cmd.exe would interpret in an argument passed to a claude.cmd shim

PRINT_LOCK = threading.Lock()


class Die(Exception):
    """A fatal error: the message is printed as "slice-loop: <message>" and the run exits with 1."""


class TurnStalled(Die):
    """A claude turn showed no activity for SLICE_STALL_SECONDS and was stopped."""


class NeedsDecision(Die):
    """A plan under --yes names decisions only the user can make: its slice is parked."""

    def __init__(self, questions: list[str]):
        super().__init__("; ".join(questions))
        self.questions = questions


class WriteFailed(Exception):
    """A file stayed locked by another process through every retry."""

    def __init__(self, path: str):
        super().__init__(path)
        self.path = path


def say(message: str = "", *, out: bool = False) -> None:
    """One line to stderr (stdout with out=True), flushed at once because the tests read the output as it comes."""
    with PRINT_LOCK:
        print(message, file=sys.stdout if out else sys.stderr, flush=True)


def die(message: str) -> None:
    raise Die(message)


def env_number(name: str, default: float) -> float:
    raw = os.environ.get(name)
    if not raw:
        return default
    try:
        return float(raw)
    except ValueError:
        raise Die(f"{name} needs a number, not '{raw}'") from None


def number_text(value: float) -> str:
    return f"{value:g}"


# --- text helpers ---------------------------------------------------------------------------------------------------

def lines_of(text: str) -> list[str]:
    """Lines without line ends (CR and LF); no empty last element for a final newline."""
    lines = re.split(r"\r?\n", text)
    if lines and lines[-1] == "":
        lines.pop()
    return lines


def read_text(path: str) -> str:
    """File content as it is (line ends kept); undecodable bytes survive a write back."""
    with open(path, encoding="utf-8", errors="surrogateescape", newline="") as handle:
        return handle.read()


def read_lines(path: str) -> list[str]:
    return lines_of(read_text(path))


def text_of(lines: list[str]) -> str:
    return "".join(f"{line}\n" for line in lines)


def row_pattern(nr: str) -> re.Pattern[str]:
    return re.compile(rf"^\|\s*{re.escape(nr)}(?![0-9])")


def row_number(line: str) -> str | None:
    """Slice number of a checklist row; None for any other line."""
    match = re.match(r"^\|\s*([0-9]{2,})(?![0-9])", line)
    return match.group(1) if match else None


def union_row(ours: str, theirs: str) -> str:
    """Row ours with every cell ticked that row theirs - the same slice's row on the other side - has ticked."""
    cells = ours.rstrip("\r").split("|")
    other = theirs.rstrip("\r").split("|")
    for i, cell in enumerate(cells):
        if i < len(other) and "\u2611" in other[i]:
            cells[i] = cell.replace("\u2610", "\u2611")
    return "|".join(cells)


# --- table rows: a row starts with "|", its key is its first cell, a cell holds items separated by "; " ---------------

def is_row(line: str) -> bool:
    return line.startswith("|")


def row_key(line: str) -> str:
    parts = line.split("|")
    return parts[1].strip(" \t") if len(parts) > 1 else ""


def cells_of(line: str) -> list[str]:
    raw = line.split("|")
    last = len(raw) - 2 if raw[-1].strip(" \t") == "" else len(raw) - 1
    return [raw[i].strip(" \t") for i in range(1, last + 1)]


def has_item(text: str, item: str) -> bool:
    return any(part.strip(" \t") == item for part in text.split("; "))


def merge_cell(ours: str, base: str, theirs: str) -> str:
    """A cell both sides changed keeps the integration branch's text and adds every item only the branch's text has."""
    if ours == theirs or theirs == base:
        return ours
    if ours == base:
        return theirs
    out = ours
    for part in theirs.split("; "):
        item = part.strip(" \t")
        if item and not has_item(ours, item) and not has_item(base, item):
            out = f"{out}; {item}" if out else item
    return out


def base_cells(base: str, width: int) -> list[str] | None:
    """Cells of the base row, empty cells when both sides added the row; None when their number differs."""
    if not base:
        return [""] * width
    cells = cells_of(base)
    return cells if len(cells) == width else None


def merge_row(ours: str, base: str, theirs: str) -> str | None:
    """The row both sides changed, merged cell by cell; None when the cells differ in number."""
    if ours == theirs or theirs == base:
        return ours
    if ours == base:
        return theirs
    c_ours, c_theirs = cells_of(ours), cells_of(theirs)
    c_base = base_cells(base, len(c_ours))
    if len(c_theirs) != len(c_ours) or c_base is None:
        return None
    return "|" + "".join(f" {merge_cell(o, b, t)} |" for o, b, t in zip(c_ours, c_base, c_theirs))


def row_covered(line: str, base: str, result: str) -> bool:
    """Every item row line added to a cell (against base row base) is in the result row."""
    c_line, c_result = cells_of(line), cells_of(result)
    c_base = base_cells(base, len(c_line))
    if len(c_result) != len(c_line) or c_base is None:
        return False
    for mine, before, now in zip(c_line, c_base, c_result):
        for part in mine.split("; "):
            item = part.strip(" \t")
            if item and not has_item(before, item) and not has_item(now, item):
                return False
    return True


def key_index(lines: list[str]) -> dict[str, int] | None:
    """Key -> index of table rows with unique keys; None when a line is no row, has no key or repeats one."""
    keys: dict[str, int] = {}
    for i, line in enumerate(lines):
        key = row_key(line) if is_row(line) else ""
        if not key or re.fullmatch(r"[-: ]+", key) or key in keys:
            return None
        keys[key] = i
    return keys


def merge_table_hunk(ours: list[str], base: list[str], theirs: list[str]) -> list[str] | None:
    """A hunk of table rows where no side removed a key: one row per key; None when the hunk is not such a table."""
    k_ours, k_base, k_theirs = key_index(ours), key_index(base), key_index(theirs)
    if k_ours is None or k_base is None or k_theirs is None:
        return None
    if any(key not in k_ours or key not in k_theirs for key in k_base):
        return None
    merged: list[str] = []
    for line in ours:
        key = row_key(line)
        if key not in k_theirs:
            merged.append(line)
            continue
        row = merge_row(line, base[k_base[key]] if key in k_base else "", theirs[k_theirs[key]])
        if row is None:
            return None
        merged.append(row)
    merged += [line for line in theirs if row_key(line) not in k_ours]
    return merged


def resolve_hunk(ours: list[str], base: list[str], theirs: list[str], mode: str) -> list[str] | None:
    """One conflict hunk resolved by rule; None for a real conflict. Both sides only added lines (empty base): the
    integration branch's lines, then the branch's. Table rows: merge_table_hunk. Mode "record" (the open round record)
    keeps both sides of any other hunk, too."""
    if not base:
        return ours + theirs
    table = merge_table_hunk(ours, base, theirs)
    if table is not None:
        return table
    return ours + theirs if mode == "record" else None


def resolve_hunk_lines(lines: list[str], mode: str) -> list[str] | None:
    """Every conflict hunk (diff3 style) of the lines resolved by rule; None on a real conflict."""
    state = ""
    out: list[str] = []
    ours: list[str] = []
    base: list[str] = []
    theirs: list[str] = []
    for line in lines:
        if state == "" and line.startswith("<<<<<<<"):
            state, ours, base, theirs = "o", [], [], []
        elif state == "o" and line.startswith("|||||||"):
            state = "b"
        elif state in ("o", "b") and line == "=======":
            state = "t"
        elif state == "t" and line.startswith(">>>>>>>"):
            state = ""
            resolved = resolve_hunk(ours, base, theirs, mode)
            if resolved is None:
                return None
            out += resolved
        elif state == "o":
            ours.append(line)
        elif state == "b":
            base.append(line)
        elif state == "t":
            theirs.append(line)
        else:
            out.append(line)
    return None if state else out


def count_results(lines: list[str]) -> int:
    return sum(line.startswith("Result:") for line in lines)


def lost_line(result: list[str], ours: list[str], theirs: list[str], before: list[str]) -> str:
    """Nothing was lost in a resolved text file: every line both sides kept or either side added is in the result, a
    table row as it is or covered by the result row of its key; no key has more rows than either side had; the Result:
    lines add up (ours + theirs - merge base). The first problem; empty when the file is fine."""
    have, theirs_set, base_set = set(result), set(theirs), set(before)
    base_row: dict[str, str] = {}
    for line in before:
        if is_row(line):
            base_row.setdefault(row_key(line), line)
    needed = [line for line in ours if line in theirs_set or line not in base_set]
    needed += [line for line in theirs if line not in base_set]
    needed_keys: set[str] = set()
    for line in needed:
        if line in have:
            continue
        if not is_row(line):
            return f"lost: {line}"
        key = row_key(line)
        needed_keys.add(key)
        if not any(is_row(r) and row_key(r) == key and row_covered(line, base_row.get(key, ""), r) for r in result):
            return f"lost: {line}"

    def rows_of(lines: list[str], key: str) -> int:
        return sum(1 for line in lines if is_row(line) and row_key(line) == key)

    for key in sorted(needed_keys):
        in_result = rows_of(result, key)
        if in_result > rows_of(ours, key) and in_result > rows_of(theirs, key):
            return f"duplicate rows for {key}"
    if count_results(result) != count_results(ours) + count_results(theirs) - count_results(before):
        return "Result: lines do not add up"
    return ""


def order_manual_tests(lines: list[str]) -> list[str]:
    """The blocks under "## Manual test list" in slice-number order (stable); nothing else moves."""
    start = next((i for i, line in enumerate(lines) if line.startswith("## Manual test list")), None)
    if start is None:
        return lines
    stop = start + 1
    while stop < len(lines) and not lines[stop].startswith("## "):
        stop += 1
    firsts = [i for i in range(start + 1, stop) if re.match(r"### Slice [0-9]", lines[i])]
    if not firsts:
        return lines
    numbers = [int(re.search(r"[0-9]+", lines[i]).group()) for i in firsts]  # type: ignore[union-attr]
    lasts = []
    for b, first in enumerate(firsts):
        last = (firsts[b + 1] if b + 1 < len(firsts) else stop) - 1
        while last > first and lines[last] == "":
            last -= 1
        lasts.append(last)
    blank = 0
    i = stop - 1
    while i > lasts[-1] and lines[i] == "":
        blank += 1
        i -= 1
    out = lines[:firsts[0]]
    for position, b in enumerate(sorted(range(len(firsts)), key=lambda index: numbers[index])):
        if position:
            out.append("")
        out += lines[firsts[b]:lasts[b] + 1]
    return out + [""] * blank + lines[stop:]


def manual_test_problem(lines: list[str], nr: str) -> str:
    """Problems of the manual test block of slice nr in the record: steps numbered 1..n, exactly one Expected: per
    step, exactly one Closes: and one Result: per block, one block per slice. Empty when the slice has no block or it
    is fine."""
    bad: list[str] = []
    steps = checked = expected = closes = results = blocks = 0
    section = in_block = False

    def end_step() -> None:
        nonlocal checked, expected
        if steps > checked:
            if expected != 1:
                bad.append(f"step {steps} has {expected} Expected: lines")
            checked, expected = steps, 0

    for line in lines:
        if line.startswith("## "):
            end_step()
            section, in_block = line.startswith("## Manual test list"), False
        elif section and line.startswith("### "):
            end_step()
            in_block = re.match(rf"### Slice {re.escape(nr)}([^0-9]|$)", line) is not None
            blocks += in_block
        elif not in_block:
            continue
        elif re.match(r"[0-9]+\. ", line):
            end_step()
            steps += 1
            if int(re.match(r"[0-9]+", line).group()) != steps:  # type: ignore[union-attr]
                bad.append(f"step {steps} is numbered {int(re.match(r'[0-9]+', line).group())}")  # type: ignore[union-attr]
        elif line.startswith("Expected:"):
            if steps == 0 or closes or results:
                bad.append("Expected: outside a step")
            else:
                expected += 1
        elif line.startswith("Closes:"):
            end_step()
            closes += 1
        elif line.startswith("Result:"):
            end_step()
            results += 1
    end_step()
    if not blocks:
        return ""
    if blocks > 1:
        bad.append(f"{blocks} blocks")
    if not steps:
        bad.append("no numbered step")
    if closes != 1:
        bad.append(f"{closes} Closes: lines")
    if results != 1:
        bad.append(f"{results} Result: lines")
    return "; ".join(bad)


def notice_of(line: str) -> str:
    """The line --notify prints for one line of the event history; empty when the event needs nobody."""
    try:
        event = json.loads(line)
    except ValueError:
        return ""
    nr, kind, reason = event.get("slice", ""), event.get("type"), event.get("reason", "").replace("NEEDS-DECISION: ", "")
    if kind == "loop_stopped":
        return f"SLICE-PROBLEM: the loop stopped: {reason} → start the loop again once that is fixed"
    if kind == "slice_parked" and event.get("question"):
        return f"SLICE-QUESTION: {nr} {reason} → answer in {answer_path(nr)}, then start the loop again"
    if kind == "slice_parked":
        return f"SLICE-PROBLEM: {nr} parked: {reason} → answer in {answer_path(nr)}, then start the loop again"
    if kind == "slice_left":
        return f"SLICE-PROBLEM: {nr} left behind: {reason} → fix it on slice-{nr}, then run --merge"
    if kind == "slice_stalled":
        return f"SLICE-PROBLEM: {nr} stalled: no activity, its turn was stopped and is resumed once"
    return ""


def manual_test_steps(lines: list[str], nr: str) -> list[str]:
    """The manual test of slice nr as written: start, numbered steps, expected result. A step alone can read "Run the
    command above"."""
    section = in_block = False
    steps = []
    for line in lines:
        if line.startswith("## "):
            section, in_block = line.startswith("## Manual test list"), False
        elif section and line.startswith("### "):
            in_block = re.match(rf"### Slice {re.escape(nr)}([^0-9]|$)", line) is not None
        elif in_block and re.match(r"[0-9]+\. |Start: |Expected: ", line):
            steps.append(line)
    return steps


def proof_on_tip(lines: list[str], tip: str) -> tuple[dict[str, str], int, str]:
    """Reads a checks proof: command -> exit code of its last line on the tip (in order of the first line), the number
    of stale lines, and the first line in no format (empty when there is none; reading stops there)."""
    last: dict[str, str] = {}
    stale = 0
    for line in lines:
        if not line or line.startswith("#"):
            continue
        match = re.fullmatch(r"(-?[0-9]+)\s+([0-9a-f]{7,40})(\S*)\s+(.+)", line)
        if not match:
            return last, stale, line
        if not match.group(3) and tip.startswith(match.group(2)):
            last[match.group(4)] = match.group(1)
        else:
            stale += 1
    return last, stale, ""


def duplicate_ids(lines: list[str]) -> list[str]:
    """Finding ids defined more than once: as the first cell of two table rows, or as the first word of two headings.
    An id is uppercase letters (A-Z or a Latin-1 capital such as Ae, Oe, Ue), an optional "-" and digits (B71, F-01)."""
    seen: dict[tuple[str, str], int] = {}
    found: set[str] = set()
    for line in lines:
        if line.startswith("|"):
            kind, ident = "row", row_key(line)
        elif re.match(r"#+ ", line):
            words = line.split()
            kind, ident = "head", re.sub(r"[:.,]$", "", words[1]) if len(words) > 1 else ""
        else:
            continue
        if not re.fullmatch("[A-Z\u00c0-\u00de]+-?[0-9]+", ident):
            continue
        seen[(kind, ident)] = seen.get((kind, ident), 0) + 1
        if seen[(kind, ident)] == 2:
            found.add(ident)
    return sorted(found)


def source_paths(record_lines: list[str]) -> list[str]:
    """The backtick spans under "## Source" of the record (the findings file)."""
    paths: list[str] = []
    inside = False
    for line in record_lines:
        if line.startswith("## "):
            inside = line.startswith("## Source")
        elif inside:
            paths += re.findall(r"`([^`]*)`", line)
    return paths


def glued_line(head_lines: list[str], result: list[str]) -> str:
    """A line of the record on the integration branch that the squash lost and that is the start of a longer line of
    the result: two lines glued into one (a hand-made conflict resolution without the line break). The first one;
    empty when there is none. Glued only when the rest starts like a line of its own (a bullet, a step, a heading,
    "Word:"); text a slice adds to the end of a line is no glue."""
    have = set(result)
    own_line = re.compile(r"[ \t]*([-*+] |[0-9]+\. |#+ |[^\W\d_][\w-]*: )")
    for line in head_lines:
        if not line.strip(" \t") or line.startswith("|") or line in have:
            continue
        for candidate in result:
            if len(candidate) > len(line) and candidate.startswith(line) and own_line.match(candidate[len(line):]):
                return line
    return ""


def decision_lines(lines: list[str]) -> set[str]:
    """The non-empty lines of the section "## Decisions"."""
    found: set[str] = set()
    inside = False
    for line in lines:
        if line.startswith("## "):
            inside = re.match(r"## Decisions([^A-Za-z0-9]|$)", line) is not None
        elif inside and line.strip():
            found.add(line)
    return found


DECISION_FORMAT = re.compile(r"(- )?(Ruling: |Build decision \(slice [0-9]{2,}\): ).* \u2013 .+, [0-9][^ ]*")


def check_commands(prefix: str, conventions: list[str]) -> list[str]:
    """The commands of the line of work/conventions.md that starts with prefix ("- Baseline check", "- Full check
    chain"): its backtick spans before the source arrow."""
    line = next((line for line in conventions if line.startswith(prefix)), "")
    return [command for command in re.findall(r"`([^`]*)`", line.split("\u2190")[0]) if command]


def shared_path(a: str, b: str) -> str | None:
    """Two paths of the "files" of slices overlap when they are equal or one is a prefix ending in "/" of the other.
    The more specific of the two; None when they do not overlap."""
    if a == b:
        return a
    if a.endswith("/") and b.startswith(a):
        return b
    if b.endswith("/") and a.startswith(b):
        return a
    return None


# --- files and links --------------------------------------------------------------------------------------------------

def is_link(path: str) -> bool:
    """A symlink or a Windows junction (Python 3.10 knows no os.path.isjunction); never follows it."""
    if os.path.islink(path):
        return True
    if os.name != "nt":
        return False
    try:
        info = os.lstat(path)
    except OSError:
        return False
    return bool(info.st_file_attributes & stat.FILE_ATTRIBUTE_REPARSE_POINT) \
        and getattr(info, "st_reparse_tag", 0) == stat.IO_REPARSE_TAG_MOUNT_POINT


def find_links(root: str) -> list[str]:
    """Every link below root, without following any and without descending into one."""
    found: list[str] = []
    pending = [root]
    while pending:
        folder = pending.pop()
        try:
            entries = list(os.scandir(folder))
        except OSError:
            continue
        for entry in entries:
            if is_link(entry.path):
                found.append(entry.path)
            elif entry.is_dir(follow_symlinks=False):
                pending.append(entry.path)
    return sorted(found)


def detach_link(path: str) -> None:
    """Removes the link itself, never what it points to."""
    try:
        os.rmdir(path)  # a junction or a directory symlink
    except OSError:
        os.unlink(path)  # a file symlink (or a directory symlink that rmdir refused on this platform)


def entry_count(folder: str) -> int:
    """Entries directly in a folder; 0 when it is gone."""
    try:
        return len(os.listdir(folder))
    except OSError:
        return 0


def terminal_answer(prompt: str) -> str | None:
    """A line typed at the terminal; None when there is none to ask."""
    if not sys.stdin.isatty():
        return None
    try:
        return input(prompt)
    except EOFError:
        return None


# --- the loop -------------------------------------------------------------------------------------------------------

class Slice:
    def __init__(self, entry: dict):
        self.nr = str(entry["nr"])
        deps = entry["deps"]
        self.deps = re.findall(r"[0-9]{2,}", " ".join(map(str, deps)) if isinstance(deps, list) else str(deps))
        self.model = str(entry.get("model") or "")
        self.round = str(entry.get("round") or "")
        files = entry.get("files") or []
        self.files = [str(path) for path in files if path] if isinstance(files, list) else []


class Loop:
    def __init__(self) -> None:
        self.permission_mode = os.environ.get("SLICE_PERMISSION_MODE") or "auto"
        self.wait_seconds = env_number("SLICE_WAIT_SECONDS", 300)
        self.wait_max = int(env_number("SLICE_WAIT_MAX", 30))
        self.stall_seconds = env_number("SLICE_STALL_SECONDS", 7200)
        if self.stall_seconds <= 0:
            die("SLICE_STALL_SECONDS needs a number above zero")
        self.write_retries = int(env_number("SLICE_WRITE_RETRIES", 5))
        self.write_backoff = env_number("SLICE_WRITE_BACKOFF_SECONDS", 2)
        self.parallel_max = int(env_number("SLICE_PARALLEL_MAX", 2))
        self.watch_seconds = env_number("SLICE_WATCH_SECONDS", 60)
        self.watch_max = int(env_number("SLICE_WATCH_MAX", 480))
        self.auto_approve = self.parallel = self.dry_run = self.merge_only = self.verify = self.watch = self.notify = False
        self.max_slices = 0  # 0 = no cap
        self.built = 0
        self.round = ""
        self.claude = ""
        self.merge_locked = False  # this run holds the queue lock; the clean-up removes it
        self.watching = False  # this run wrote the sign of life; the clean-up removes it
        self.merging = False  # a squash merge is in progress in this checkout; the clean-up resets it
        self.squashed_count = 0  # slices this run squashed
        self.link_lost = False  # a folder a link of a removed worktree pointed to lost entries
        self.left_nrs: set[str] = set()  # slices the merge queue left behind in this run
        self.left_lines: list[str] = []  # "slice NN: <reason>", in the order they were left
        self.not_ready_lines: list[str] = []  # slices --merge found not ready yet (their session still checks)
        self.clean_up_lines: list[str] = []  # squashed slices whose worktree or folder could not be removed
        self.children: list[subprocess.Popen] = []

    # --- arguments and plan files ---

    def parse_args(self, args: list[str]) -> None:
        i = 0
        while i < len(args):
            arg = args[i]
            if arg == "--yes":
                self.auto_approve = True
            elif arg == "--parallel":
                self.parallel = True
            elif arg == "--dry-run":
                self.dry_run = True
            elif arg == "--merge":
                self.merge_only = True
            elif arg == "--verify":
                self.verify = True
            elif arg == "--watch":
                self.watch = True
            elif arg == "--notify":
                self.notify = True
            elif arg == "--max-slices":
                if i + 1 >= len(args) or not re.fullmatch(r"[1-9][0-9]*", args[i + 1]):
                    die(f"--max-slices needs a positive integer ({USAGE})")
                self.max_slices = int(args[i + 1])
                i += 1
            else:
                die(f"unknown option '{arg}' ({USAGE})")
            i += 1
        if self.verify and not self.merge_only:
            die(f"--verify needs --merge ({USAGE})")
        if self.watch and not self.merge_only:
            die(f"--watch needs --merge ({USAGE})")

    def slices(self) -> list[Slice]:
        try:
            with open(SLICES_FILE, encoding="utf-8-sig") as handle:
                entries = json.load(handle)
            if not all(isinstance(e, dict) and "nr" in e and "deps" in e for e in entries):
                raise KeyError(SLICES_FILE)
            return [Slice(entry) for entry in entries]
        except (OSError, ValueError, KeyError, TypeError):
            die(f'every entry in {SLICES_FILE} needs "nr" and "deps"')
            return []

    def slice_model(self, nr: str) -> str:
        """Model of slice nr - its "model" field. Every turn of the slice runs on it, never on the caller's default."""
        return next((s.model for s in self.slices() if s.nr == nr), "")

    # --- git ---

    def git(self, *args: str) -> subprocess.CompletedProcess:
        return subprocess.run(["git", *args], stdin=subprocess.DEVNULL, capture_output=True)

    def git_ok(self, *args: str) -> bool:
        return self.git(*args).returncode == 0

    def git_lines(self, *args: str) -> list[str]:
        """Output lines of a git command, decoded as UTF-8; none when it fails."""
        result = self.git(*args)
        return lines_of(result.stdout.decode("utf-8", "surrogateescape")) if result.returncode == 0 else []

    def git_first(self, *args: str) -> str:
        lines = self.git_lines(*args)
        return lines[0] if lines else ""

    def tip(self, nr: str) -> str:
        return self.git_first("rev-parse", f"slice-{nr}")

    def branch_name(self) -> str:
        return self.git_first("branch", "--show-current")

    def names(self, *args: str) -> list[str]:
        return self.git_lines("-c", "core.quotepath=false", *args)

    # --- writing with retries ---

    def pause(self, seconds: float) -> None:
        time.sleep(seconds)

    def replace_file(self, target: str, text: str) -> None:
        """Writes text (as it is, UTF-8) to target through a temp file next to it, retrying while another process
        holds target (a scanner or indexer with it mapped); after the last retry it raises WriteFailed."""
        for attempt in range(self.write_retries + 1):
            temp = f"{target}.{os.getpid()}.tmp"
            try:
                with open(temp, "w", encoding="utf-8", errors="surrogateescape", newline="") as handle:
                    handle.write(text)
                os.replace(temp, target)
                return
            except OSError as error:
                try:
                    os.unlink(temp)
                except OSError:
                    pass
                if attempt >= self.write_retries:
                    raise WriteFailed(target) from error
                say(f"slice-loop: could not write {target} (locked by another process?) \u2013 retrying in "
                    f"{number_text(self.write_backoff)} s (attempt {attempt + 1} of {self.write_retries})")
                self.pause(self.write_backoff)

    def reset_merge(self) -> None:
        """Resets the merge in progress, retrying while another process holds a file it has to restore; stops the loop
        when it cannot, so the checkout never stays mid-squash unnoticed. Hard: merge_slice starts only on a clean
        checkout, and a reset --merge that failed half-way leaves files every later reset --merge refuses."""
        self.merging = False
        for attempt in range(self.write_retries + 1):
            if self.git_ok("reset", "-q", "--hard"):
                return
            if attempt >= self.write_retries:
                die("could not reset the merge in progress (a file is still locked by another process?) \u2013 "
                    "run git reset --merge once it is free, then --merge again")
            say("slice-loop: could not reset the merge (a file is locked by another process?) \u2013 retrying in "
                f"{number_text(self.write_backoff)} s (attempt {attempt + 1} of {self.write_retries})")
            self.pause(self.write_backoff)

    # --- checklist ---

    def checklist_row(self, nr: str) -> str:
        pattern = row_pattern(nr)
        return next((line for line in read_lines(CHECKLIST_FILE) if pattern.match(line)), "")

    def is_done(self, nr: str) -> bool:
        """A slice is done when its checklist row has at least one tick and no open box."""
        row = self.checklist_row(nr)
        return "\u2611" in row and "\u2610" not in row

    def branch_ready(self, nr: str) -> bool:
        """Ready for the merge queue: the slice's row on branch slice-NN has a tick and no open box outside its last
        cell (Commit)."""
        pattern = row_pattern(nr)
        row = next((line for line in self.git_lines("show", f"slice-{nr}:{CHECKLIST_FILE}") if pattern.match(line)), "")
        if not row:
            return False
        body = row.rpartition("|")[0].rpartition("|")[0]  # without the closing pipe and the last cell
        return "\u2611" in body and "\u2610" not in body

    def tick_commit(self, nr: str) -> None:
        """Ticks the last open box of slice nr's row - its Commit column - keeping the file's line endings."""
        text = read_text(CHECKLIST_FILE)
        pattern = re.compile(rf"(?m)^(\|[ \t]*{re.escape(nr)}(?![0-9])[^\r\n]*)\u2610")
        self.replace_file(CHECKLIST_FILE, pattern.sub("\\1\u2611", text))

    def resolve_checklist(self, branch: str) -> None:
        """The checklist after a squash conflict, built from both committed versions: the integration branch's lines in
        their order, a row the branch also has with the union of both rows' ticks, rows only the branch has appended in
        its order."""
        theirs: dict[str, str] = {}
        for line in self.git_lines("show", f"{branch}:{CHECKLIST_FILE}"):
            nr = row_number(line)
            if nr:
                theirs[nr] = line
        out: list[str] = []
        ours_has: set[str] = set()
        for line in self.git_lines("show", f"HEAD:{CHECKLIST_FILE}"):
            nr = row_number(line)
            if nr:
                ours_has.add(nr)
                if nr in theirs:
                    line = union_row(line, theirs[nr])
            out.append(line)
        out += [line for nr, line in theirs.items() if nr not in ours_has]
        self.replace_file(CHECKLIST_FILE, text_of(out))

    def checklist_problem(self, branch: str) -> bool:
        """Every row of both sides is in the resolved checklist, and none of their ticks is missing there."""
        rows: dict[str, str] = {}
        for line in read_lines(CHECKLIST_FILE):
            nr = row_number(line)
            if nr:
                rows[nr] = line
        for line in self.git_lines("show", f"HEAD:{CHECKLIST_FILE}") + self.git_lines("show", f"{branch}:{CHECKLIST_FILE}"):
            nr = row_number(line)
            if nr and (nr not in rows or union_row(rows[nr], line) != rows[nr]):
                return True
        return False

    # --- reasons a slice is not squashed ---

    def conventions(self) -> list[str]:
        return read_lines(CONVENTIONS_FILE) if os.path.isfile(CONVENTIONS_FILE) else []

    def ready_problem(self, nr: str) -> str:
        """Why slice nr has not reported ready on its branch tip; empty when it has. As its last step before
        SLICE-READY, /slice writes the tip's SHA to .slice-loop/slice-NN.ready: a session still running its last
        checks has no such file, or one of an older tip, and its worktree is not taken away under it."""
        path = f"{LOG_DIR}/slice-{nr}.ready"
        if not os.path.isfile(path):
            return f"not reported ready: no {path} (the session has not ended with SLICE-READY)"
        tip = self.tip(nr)
        sha = "".join(read_text(path).split())
        if re.fullmatch(r"[0-9a-f]{7,40}", sha) and tip.startswith(sha):
            return ""
        return f"slice-{nr} moved after SLICE-READY: {path} names '{sha}', the tip is {tip[:12]}"

    def checks_problem(self, nr: str) -> str:
        """Why the checks proof .slice-loop/slice-NN.checks does not allow the squash; empty when it does. One line
        per check: "<exit code> <commit SHA> <command>". Only lines of the branch tip count (the SHA is a prefix of it,
        7 to 40 hex digits): lines of other commits are stale runs and ignored, and so is a SHA with a suffix
        ("<sha>+uncommitted"), a run on changes no commit holds. Of each command the last line on the tip counts - red
        then green is fine, green then red is red. Every such line is green and every command of the baseline check in
        work/conventions.md has one."""
        path = f"{LOG_DIR}/slice-{nr}.checks"
        if not os.path.isfile(path):
            return f"no checks proof {path}"
        tip = self.tip(nr)
        last, stale, unreadable = proof_on_tip(read_lines(path), tip)
        if unreadable:
            return f"unreadable line in {path}: {unreadable}"
        if not last:
            if stale == 0:
                return f"no check listed in {path}"
            return f"no check ran on the tip {tip[:12]} of slice-{nr} ({stale} stale line(s) ignored)"
        for command, code in last.items():
            if code != "0":
                return f"check red (exit {code}): {command}"
        for command in check_commands("- Baseline check", self.conventions()):
            if command not in last:
                return f"baseline check missing from {path}: {command}"
        return ""

    def overlap_files(self, nr: str) -> list[str]:
        """The files the integration branch (checked out here) changed since slice-NN last took it in that the slice
        changes too: the slice's checks never ran on them together. Documents (*.md) and work/ are left out - the queue
        merges those by its rules. None when the slice's tip already holds the integration branch."""
        base = self.git_first("merge-base", "HEAD", f"slice-{nr}")
        integration = set(self.names("diff", "--name-only", base, "HEAD"))
        sliced = set(self.names("diff", "--name-only", base, f"slice-{nr}"))
        return sorted(f for f in integration & sliced if not f.startswith("work/") and not f.endswith(".md"))

    def decision_problem(self, record: str, branch: str) -> str:
        """The first line the slice added under "## Decisions" of round record that is neither a ruling ("Ruling: ...")
        nor a build decision ("Build decision (slice NN): ..."), each ending in "- <who>, <date>"; a leading "- " is
        allowed. Empty when every added line fits."""
        base = self.git_first("merge-base", "HEAD", branch)
        before = decision_lines(self.git_lines("show", f"{base}:{record}"))
        added = decision_lines(self.git_lines("show", f"{branch}:{record}")) - before
        return next((line for line in sorted(added) if not DECISION_FORMAT.fullmatch(line)), "")

    def new_duplicate_ids(self, record: str) -> list[str]:
        """Finding ids the squash would define twice in the record or its findings file, as "<id> in <file>"; ids
        already duplicated on the integration branch do not count."""
        files = [record] + [path for path in source_paths(read_lines(record)) if os.path.isfile(path) and path != record]
        found = []
        for file in files:
            before = set(duplicate_ids(self.git_lines("show", f"HEAD:{file}")))
            found += [f"{ident} in {file}" for ident in duplicate_ids(read_lines(file)) if ident not in before]
        return found

    def resolution_problem(self, file: str, branch: str) -> str:
        """Why resolved file lost something; empty when it is fine. No conflict marker is left; the checklist keeps
        every row and tick of both sides; any other file passes lost_line."""
        result = read_lines(file)
        if any(re.match(r"(<<<<<<<|=======|>>>>>>>|\|{7})", line) for line in result):
            return "a conflict marker is left"
        if file == CHECKLIST_FILE:
            return "a row or tick is missing" if self.checklist_problem(branch) else ""
        base = self.git_first("merge-base", "HEAD", branch)
        return lost_line(result, self.git_lines("show", f"HEAD:{file}"), self.git_lines("show", f"{branch}:{file}"),
                         self.git_lines("show", f"{base}:{file}"))

    # --- leaving slices behind and the report ---

    def leave_slice(self, nr: str, reason: str) -> None:
        """Leaves slice nr behind: its branch stays, the run goes on without it."""
        say(f"[slice {nr}] left behind: {reason} \u2013 slice-{nr} kept")
        if nr not in self.left_nrs:
            self.event("slice_left", nr, reason=reason)
        self.left_nrs.add(nr)
        self.left_lines.append(f"slice {nr}: {reason}")
        if self.watch:  # --watch comes back to this slice only with a new tip
            with open(f"{LOG_DIR}/slice-{nr}.left", "w", encoding="utf-8", newline="\n") as handle:
                handle.write(self.tip(nr) + "\n")

    def not_ready(self, nr: str, reason: str) -> None:
        """Slice nr is left for a later --merge: its session has not reported ready on its tip yet."""
        say(f"[slice {nr}] not ready yet: {reason} \u2013 left for a later --merge")
        self.not_ready_lines.append(f"slice {nr}: {reason}")

    def report_left(self) -> bool:
        """Lists every slice not ready yet and every slice left behind; False when one was left behind, or when a
        removed worktree's link target lost entries (that was reported where it happened)."""
        if self.not_ready_lines:
            say(f"slice-loop: {len(self.not_ready_lines)} slice(s) not ready yet \u2013 their sessions still check; "
                "run --merge again after SLICE-READY:")
            for line in self.not_ready_lines:
                say(f"  {line}")
        if self.left_lines:
            say(f"slice-loop: {len(self.left_lines)} slice(s) left behind \u2013 fix them on their branch, then run --merge again:")
            for line in self.left_lines:
                say(f"  {line}")
        if self.clean_up_lines:
            say(f"slice-loop: {len(self.clean_up_lines)} squashed slice(s) left something to clean up:")
            for line in self.clean_up_lines:
                say(f"  {line}")
        return not self.left_lines and not self.link_lost

    def reset_and_leave(self, nr: str, reason: str) -> None:
        self.reset_merge()
        self.leave_slice(nr, f"{reason} \u2013 merge reset")

    # --- worktrees ---

    def worktree_of(self, branch: str) -> str:
        """Path of the worktree that has the branch checked out; empty when none has."""
        path = ""
        for line in self.git_lines("worktree", "list", "--porcelain"):
            if line.startswith("worktree "):
                path = line[len("worktree "):]
            elif line == f"branch refs/heads/{branch}":
                return path
        return ""

    def remove_worktree(self, nr: str, path: str) -> bool:
        """Removes the worktree path of slice nr and the links in it - never what they point to: git follows a
        junction (for example node_modules linked onto the main checkout) when it removes the worktree and empties its
        target. The entries of every folder a link points to are counted before and after, so that a loss is reported
        (and makes the run fail). False when git will not remove the worktree, even after retries."""
        held: dict[str, int] = {}
        links = find_links(path)
        for link in links:
            target = os.path.realpath(link)
            if os.path.isdir(target) and target not in held:
                held[target] = entry_count(target)
        for link in links:
            detach_link(link)
        for attempt in range(self.write_retries + 1):  # a shell of the finished session may still stand in the worktree
            if self.git_ok("worktree", "remove", path):
                break
            if not self.worktree_of(f"slice-{nr}"):  # git drops its entry even when files inside are held
                break
            if attempt >= self.write_retries:
                return False
            say(f"slice-loop: could not remove the worktree of slice-{nr} (a shell or process still runs inside it?) \u2013 "
                f"retrying in {number_text(self.write_backoff)} s (attempt {attempt + 1} of {self.write_retries})")
            self.pause(self.write_backoff)
        for target, before in held.items():
            now = entry_count(target)
            if now < before:
                say(f"slice-loop: link target {target} lost entries during the removal of the worktree of slice-{nr} "
                    f"(before {before}, now {now}) \u2013 restore it (for example npm ci)")
                self.link_lost = True
        return True

    def remove_slice_branch(self, nr: str) -> None:
        """Removes the squashed branch slice-NN and its worktree; a worktree git will not remove is reported and kept
        with its branch."""
        branch = f"slice-{nr}"
        path = self.worktree_of(branch)
        if path and not self.remove_worktree(nr, path):
            say(f"[slice {nr}] worktree {path} could not be removed \u2013 most likely a shell, editor or process still runs "
                f"inside it (or it is locked); close it and run --merge again to retry. {path} and {branch} are kept")
            self.clean_up_lines.append(f"slice {nr}: {path} and {branch} kept \u2013 close what runs inside it, then run --merge again")
            return
        if path and os.path.exists(path):
            self.clean_up_lines.append(f"slice {nr}: folder {path} remains \u2013 close what holds it and delete it")
        try:
            os.unlink(f"{LOG_DIR}/slice-{nr}.squashed")
        except OSError:
            pass
        # -D: a squash merge leaves the branch unmerged in git's eyes; the squash commit made just now is the proof.
        if not self.git_ok("branch", "-D", branch):
            say(f"[slice {nr}] branch {branch} could not be removed")

    def retry_removal(self, nr: str) -> None:
        """A slice squashed in an earlier run whose worktree could not be removed: its branch still points where it
        did at the squash (.slice-loop/slice-NN.squashed), so the removal is tried again; a branch that moved since is
        left as it is."""
        marker = f"{LOG_DIR}/slice-{nr}.squashed"
        if not os.path.isfile(marker):
            return
        if self.tip(nr) == read_text(marker).strip("\r\n"):
            say(f"[slice {nr}] already squashed \u2013 removing its leftover worktree and branch again")
            self.remove_slice_branch(nr)
        else:
            say(f"[slice {nr}] already squashed; slice-{nr} has commits after the squash \u2013 nothing to do for the queue "
                "(squash them by hand or delete the branch)")

    # --- the squash ---

    def open_round_file(self) -> str:
        """Path of the first round record in work/rounds whose first line is "Status: open"; empty if none is open."""
        try:
            names = sorted(name for name in os.listdir(ROUNDS_DIR) if name.endswith(".md"))
        except OSError:
            return ""
        for name in names:
            path = f"{ROUNDS_DIR}/{name}"
            if os.path.isfile(path):
                with open(path, encoding="utf-8-sig", errors="surrogateescape") as handle:
                    if handle.readline().rstrip("\r\n") == "Status: open":
                        return path
        return ""

    def resolve_conflicts(self, nr: str, branch: str, record: str) -> bool:
        """Resolves every conflict of the squash by rule and checks the result; False when the slice was left behind."""
        conflicts = self.names("diff", "--name-only", "--diff-filter=U")
        if not conflicts:
            self.reset_and_leave(nr, "git merge --squash failed")
            return False
        real: list[str] = []
        for file in conflicts:
            if file == CHECKLIST_FILE:
                self.resolve_checklist(branch)
            else:
                resolved = resolve_hunk_lines(read_lines(file), "record" if file == record else "strict")
                if resolved is None:
                    real.append(file)
                    continue
                self.replace_file(file, text_of(resolved))
            problem = self.resolution_problem(file, branch)
            if problem:
                self.reset_and_leave(nr, f"the rule-based resolution of {file} failed its check ({problem})")
                return False
            self.git("add", file)
        if real:
            self.reset_and_leave(nr, f"merge conflict in {' '.join(real)} \u2013 merge the integration branch on the slice branch")
            return False
        return True

    def record_problem(self, nr: str, branch: str, record: str) -> str:
        """The record after the squash: its manual tests in slice order, then a reason to leave the slice, if any."""
        lines = read_lines(record)
        ordered = order_manual_tests(lines)
        if text_of(ordered) != read_text(record).replace("\r", ""):  # rewritten only when a block moved
            self.replace_file(record, text_of(ordered))
        self.git("add", record)
        result = read_lines(record)
        glued = glued_line(self.git_lines("show", f"HEAD:{record}"), result)
        if glued:
            return f"line glued to the next one in {record}: {glued}"
        problem = manual_test_problem(result, nr)
        if problem:
            return f"manual test block of slice {nr} in {record}: {problem}"
        duplicates = self.new_duplicate_ids(record)
        if duplicates:
            return f"finding id defined twice: {', '.join(duplicates)}"
        decision = self.decision_problem(record, branch)
        if decision:
            return (f"decision line in neither format ('Ruling: \u2026' or 'Build decision (slice {nr}): \u2026') "
                    f"in {record}: {decision}")
        return ""

    def squash(self, nr: str) -> bool:
        branch = f"slice-{nr}"
        record = self.open_round_file()  # read before the merge: a conflict at its first line would hide "Status: open"
        self.merging = True
        # diff3 keeps every hunk whole and shows its base: the default style moves lines both sides end with (such as
        # "Result: [ ]") out of the hunk, and keeping both sides would then keep that line once.
        if not self.git_ok("-c", "merge.conflictStyle=diff3", "merge", "--squash", branch) \
                and not self.resolve_conflicts(nr, branch, record):
            return False
        if record:
            problem = self.record_problem(nr, branch, record)
            if problem:
                self.reset_and_leave(nr, problem)
                return False
        self.tick_commit(nr)
        self.git("add", CHECKLIST_FILE)
        self.replace_file(f"{LOG_DIR}/slice-{nr}.squashed", self.tip(nr) + "\n")
        if not self.git_ok("commit", "-q", "-F", f"{LOG_DIR}/slice-{nr}.msg"):
            die(f"slice {nr}: the squash commit failed \u2013 merge reset")
        self.merging = False
        self.squashed_count += 1
        say(f"[slice {nr}] squashed onto {self.branch_name()} ({self.git_first('rev-parse', '--short', 'HEAD')})")
        if not self.is_done(nr):
            die(f"slice {nr} was squashed but is not ticked in {CHECKLIST_FILE} \u2013 check it before running again")
        self.write_report(nr, self.tip(nr))
        self.remove_slice_branch(nr)
        return True

    def started_at(self, nr: str) -> str:
        """When the first planning of slice nr began, else its first build, so a slice parked for an answer keeps its
        start; empty for a slice no run started."""
        times: dict[str, str] = {}
        for line in read_lines(EVENTS_FILE) if os.path.isfile(EVENTS_FILE) else []:
            event = json.loads(line) if line else {}
            if event.get("slice") == nr:
                times.setdefault(event.get("type", ""), event.get("ts", ""))
        return times.get("slice_planning") or times.get("slice_started") or ""

    def write_report(self, nr: str, tip: str) -> None:
        """What a finished slice leaves for people: .slice-loop/slice-NN.report.json and a slice_merged event, read by
        the Slice Board. tip is the squashed branch tip, empty when the session squashed the slice itself. A failure
        here never stops the round: the squash is done."""
        try:
            message = read_lines(f"{LOG_DIR}/slice-{nr}.msg") if os.path.isfile(f"{LOG_DIR}/slice-{nr}.msg") else []
            record = self.open_round_file()
            proof = f"{LOG_DIR}/slice-{nr}.checks"
            members = self.round_slices()
            position = sum(self.is_done(entry.nr) for entry in members)
            report = {
                "nr": nr,
                "title": message[0] if message else "",
                "summary": " ".join(line for line in message[1:] if line),
                "started": self.started_at(nr),
                "ended": datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
                "position": position,
                "total": len(members),
                "commit": self.git_first("rev-parse", "--short=8", "HEAD") if tip else "",
                "files": len(self.names("diff", "--name-only", "HEAD~1", "HEAD")) if tip else 0,
                "checks": len(proof_on_tip(read_lines(proof), tip)[0]) if tip and os.path.isfile(proof) else 0,
                "manual": manual_test_steps(read_lines(record), nr) if record else [],
            }
            self.replace_file(f"{LOG_DIR}/slice-{nr}.report.json", json.dumps(report, ensure_ascii=False, indent=1) + "\n")
            self.event("slice_merged", nr, position=str(position), total=str(len(members)))
        except Exception as error:  # pylint: disable=broad-except
            say(f"[slice {nr}] no report written: {error}")

    def merge_slice(self, nr: str) -> bool:
        """Squashes branch slice-NN onto the branch checked out here; False when it leaves the slice behind (branch
        kept). First the checks proof; then every conflict is resolved by rule and checked (a real conflict leaves the
        slice); then the record's manual test list is put in slice order and the slice's block checked, and no finding
        id may be defined twice. A file it cannot write leaves the slice behind, too; any other way out of the merge (a
        stop, a signal) resets it."""
        # A crash of Git Bash leaves bash.exe.stackdump here now and then: it is no change of anybody's, and no reason to stop.
        if [line for line in self.git_lines("status", "--porcelain") if not re.fullmatch(r"\?\? .*\.stackdump", line)]:
            die(f"slice {nr}: this checkout has uncommitted changes \u2013 commit or remove them, then run --merge")
        problem = self.ready_problem(nr)
        if problem and self.merge_only:
            self.not_ready(nr, problem)
            return False
        if not problem and not os.path.isfile(f"{LOG_DIR}/slice-{nr}.msg"):
            problem = f"no squash message: {LOG_DIR}/slice-{nr}.msg is missing"
        problem = problem or self.checks_problem(nr)
        if not problem:
            overlap = self.overlap_files(nr)
            if overlap:
                problem = f"integration branch moved and touches {' '.join(overlap)} \u2013 merge it on the slice branch"
        if problem:
            self.leave_slice(nr, problem)
            return False
        try:
            return self.squash(nr)
        except WriteFailed as failure:
            self.reset_and_leave(nr, f"could not write {failure.path} (locked by another process)")
            return False
        finally:
            if self.merging:
                self.reset_merge()

    def finish_slice(self, nr: str) -> None:
        """After a build turn: a slice the session squashed itself is done; one that waits on its branch goes through
        the queue."""
        if not self.is_done(nr):
            if not self.branch_ready(nr):
                die(f"slice {nr} reported done or ready but is not ticked in {CHECKLIST_FILE} or on slice-{nr} \u2013 "
                    "check it before running again")
            if not self.merge_slice(nr):
                return
        else:
            self.write_report(nr, "")
        say(f"[slice {nr}] done")

    # --- merge queue, watch and locks ---

    def branch_numbers(self) -> list[str]:
        """The number of every slice-NN branch, lowest first."""
        names = self.git_lines("for-each-ref", "--format=%(refname:short)", "refs/heads/slice-*")
        numbers = [m.group(1) for m in (re.fullmatch(r"slice-([0-9]+)", name) for name in names) if m]
        return sorted(numbers, key=int)

    def merge_queue(self) -> None:
        """The merge queue alone (--merge): every slice-NN branch, lowest number first; one not ticked through Checks
        is left, one that conflicts or lacks its proof is left behind and the queue goes on."""
        for nr in self.branch_numbers():
            if self.is_done(nr):
                self.retry_removal(nr)
            elif self.branch_ready(nr):
                self.merge_slice(nr)
            else:
                say(f"[slice {nr}] not ready to merge: its row on slice-{nr} is not ticked through Checks \u2013 left as it is")

    def squash_reported(self) -> None:
        """Before a run plans anything: a slice that reported ready on its tip (an earlier run, or a repair on its branch)
        goes through the queue; planning it again would find nothing to do."""
        for nr in self.branch_numbers():
            if not self.is_done(nr) and self.branch_ready(nr) and not self.ready_problem(nr):
                self.merge_slice(nr)

    def slice_reported(self) -> bool:
        """Whether a slice has reported ready on a tip the queue has not left behind yet."""
        for nr in self.branch_numbers():
            if self.is_done(nr) or self.ready_problem(nr):
                continue
            left = f"{LOG_DIR}/slice-{nr}.left"
            if os.path.isfile(left) and "".join(read_text(left).split()) == self.tip(nr):
                continue
            return True
        return False

    def take_merge_lock(self) -> bool:
        """Takes the queue lock; False while another queue holds it."""
        try:
            os.mkdir(MERGE_LOCK)
        except OSError:
            if not os.path.isdir(MERGE_LOCK):
                say(f"slice-loop: could not create {MERGE_LOCK} \u2013 going on without it")
                return True
            if age_seconds(MERGE_LOCK) < LOCK_MAX_AGE_SECONDS:
                say(f"slice-loop: another merge queue runs ({MERGE_LOCK}; delete it if none runs) \u2013 this one does nothing")
                return False
            say(f"slice-loop: taking over {MERGE_LOCK}, older than 60 minutes")
        self.merge_locked = True
        stamp = datetime.datetime.now(datetime.timezone.utc).strftime("merge queue %Y-%m-%dT%H:%M:%SZ")
        try:
            with open(f"{MERGE_LOCK}/owner", "w", encoding="utf-8", newline="\n") as handle:
                handle.write(stamp + "\n")
            os.utime(MERGE_LOCK)
        except OSError:
            pass  # the owner file is only a hint for people; the lock itself is the folder
        return True

    def release_merge_lock(self) -> None:
        if self.merge_locked:
            shutil.rmtree(MERGE_LOCK, ignore_errors=True)
        self.merge_locked = False

    def watch_running(self) -> bool:
        """Whether another --watch shows a sign of life. An older one has died: its file stays, and this run goes on."""
        return os.path.isfile(WATCH_ALIVE) and age_seconds(WATCH_ALIVE) < LOCK_MAX_AGE_SECONDS

    def wait_for_ready_slice(self) -> bool:
        """--watch: waits until a slice has reported ready; False after the last check. The queue then runs once and
        the script ends, so the chat that started it in the background is woken even when a session's own report never
        reached it."""
        say(f"slice-loop: watching for SLICE-READY every {number_text(self.watch_seconds)} s, at most {self.watch_max} check(s)")
        for check in range(1, self.watch_max + 1):
            with open(WATCH_ALIVE, "w", encoding="utf-8"):
                pass
            if self.slice_reported():
                return True
            if check < self.watch_max:
                self.pause(self.watch_seconds)
        say(f"slice-loop: no slice reported ready after {self.watch_max} check(s) \u2013 start the watch again while sessions run")
        return False

    def lock_owner(self) -> str:
        """Who holds the full-suite lock, from its owner file, e.g. "slice 100 2026-10-27T09:00:00Z"."""
        path = f"{LOCK_DIR}/owner"
        lines = read_lines(path) if os.path.isfile(path) else []
        return lines[0] if lines else "unknown"

    def stale_lock_notice(self) -> None:
        """Reports a full-suite lock nobody can hold any more: its owner is a slice whose branch is gone, or it is
        older than an hour. Only reports - removing it is the caller's call; the run goes on."""
        if not os.path.isdir(LOCK_DIR):
            return
        owner = self.lock_owner()
        touched = f"{LOCK_DIR}/owner" if os.path.isfile(f"{LOCK_DIR}/owner") else LOCK_DIR
        match = re.match(r"slice ([0-9]{2,})(\s|$)", owner)
        why = ""
        if match and not self.git_ok("rev-parse", "--verify", "--quiet", f"refs/heads/slice-{match.group(1)}"):
            why = "its slice branch is gone"
        elif age_seconds(touched) > LOCK_MAX_AGE_SECONDS:
            why = "it is older than 60 minutes"
        if why:
            say(f"slice-loop: stale lock {LOCK_DIR} (owner: {owner}) \u2013 {why}; delete the folder if no run holds it")

    def baseline_notice(self) -> None:
        """Reports a work/conventions.md without a "- Baseline check" line: the queue then cannot require the baseline
        commands in the checks proof. A key translated into the repository's language is the usual cause. Only
        reports; the run goes on."""
        if os.path.isfile(CONVENTIONS_FILE) and not any(line.startswith("- Baseline check") for line in self.conventions()):
            say(f"slice-loop: no '- Baseline check' line in {CONVENTIONS_FILE} \u2013 checks proofs are not checked for the "
                "baseline commands; the key stays English in any language, e.g. '- Baseline check (<own words>): `<command>`'")

    def verify_chain(self) -> bool:
        """After the merge queue (--verify): when the queue squashed a slice, every command of the "- Full check chain"
        line in work/conventions.md runs on the integration branch, under the full-suite lock. False when one is red
        or the lock is held. The commands are shell command lines, run by the platform's shell."""
        if self.squashed_count == 0:
            return True
        commands = check_commands("- Full check chain", self.conventions())
        if not commands:
            say(f"slice-loop: no '- Full check chain' line in {CONVENTIONS_FILE} \u2013 nothing verified "
                "(the key stays English in any language)")
            return True
        try:
            os.mkdir(LOCK_DIR)
        except OSError:
            say(f"slice-loop: {LOCK_DIR} is held (owner: {self.lock_owner()}) \u2013 the full check chain was not run")
            return False
        red = False
        try:
            stamp = datetime.datetime.now(datetime.timezone.utc).strftime("merge queue %Y-%m-%dT%H:%M:%SZ")
            with open(f"{LOCK_DIR}/owner", "w", encoding="utf-8", newline="\n") as handle:
                handle.write(stamp + "\n")
            for command in commands:
                code = subprocess.run(command, shell=True, stdin=subprocess.DEVNULL).returncode  # noqa: S602 - a line of the repository's own conventions
                if code == 0:
                    say(f"[verify] {command} \u2013 green")
                else:
                    say(f"[verify] {command} \u2013 red (exit {code})")
                    red = True
        finally:
            shutil.rmtree(LOCK_DIR, ignore_errors=True)
        if red:
            say(f"slice-loop: full check chain red on {self.branch_name()} after the merge queue")
        return not red

    # --- planning and building ---

    def round_slices(self) -> list[Slice]:
        """The slices of the open round; a round planned ahead waits. An entry without "round" counts as part of it."""
        current = self.open_round()
        return [entry for entry in self.slices() if entry.round in ("", current)]

    def ready_slices(self) -> list[str]:
        ready = []
        for entry in self.round_slices():
            if self.is_done(entry.nr) or entry.nr in self.left_nrs:
                continue
            if os.path.isfile(parked_path(entry.nr)) and not os.path.isfile(answer_path(entry.nr)):
                continue
            if all(self.is_done(dep) for dep in entry.deps):
                ready.append(entry.nr)
        return ready

    def all_done(self) -> bool:
        return all(self.is_done(entry.nr) for entry in self.round_slices())

    def open_count(self) -> int:
        return sum(not self.is_done(entry.nr) for entry in self.round_slices())

    def parallel_batch(self, ready: list[str]) -> list[str]:
        """The ready slices (in order) that go into the next --parallel batch: at most SLICE_PARALLEL_MAX, and none
        whose "files" overlap a slice already picked; that one waits for a later batch."""
        files = {entry.nr: entry.files for entry in self.slices()}
        batch: list[str] = []
        for nr in ready:
            if len(batch) >= self.parallel_max:
                break
            clash = ""
            for other in batch:
                for mine in files.get(nr, []):
                    for theirs in files.get(other, []):
                        shared = shared_path(mine, theirs)
                        if shared and not clash:
                            clash = f"shares {shared} with slice {other}"
            if clash:
                say(f"[slice {nr}] waits: {clash}")
            else:
                batch.append(nr)
        return batch

    def open_round(self) -> str:
        """Id (R01, R02 ...) of the open round record; empty if none is open."""
        match = re.match(r"R[0-9]+", os.path.basename(self.open_round_file()))
        return match.group() if match else ""

    def event(self, kind: str, nr: str = "", **fields: str) -> None:
        """One line in the round's history, so a stop can be read afterwards. A failed write never stops the round."""
        now = datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
        line = json.dumps({"ts": now, "type": kind, **({"slice": nr} if nr else {}), **fields}, ensure_ascii=False)
        try:
            with PRINT_LOCK, open(EVENTS_FILE, "a", encoding="utf-8", newline="\n") as handle:
                handle.write(line + "\n")
        except OSError:
            pass

    def write_compact_hook(self) -> None:
        """The settings file every turn gets: after a compaction the session is told again what it is doing. Written once,
        before the first turn: a claude starting in a parallel batch must not read it half written."""
        script = pathlib.Path(__file__).resolve().as_posix()
        python = pathlib.Path(sys.executable).as_posix()
        hook = {"type": "command", "command": f'"{python}" "{script}" --after-compact'}
        settings = {"hooks": {"SessionStart": [{"matcher": "compact", "hooks": [hook]}]}}
        with open(COMPACT_HOOK_FILE, "w", encoding="utf-8", newline="\n") as handle:
            json.dump(settings, handle)

    def turn_environment(self, nr: str) -> dict[str, str]:
        """Headless claude ends a background command with its turn, so checks run in the foreground: Bash may take as long
        as the stall limit allows and is not moved to the background on the way."""
        limit = str(int(self.stall_seconds * 1000))
        return dict(os.environ, SLICE_NR=nr, BASH_DEFAULT_TIMEOUT_MS=limit, BASH_MAX_TIMEOUT_MS=limit)

    def last_activity(self, session: str, log: str, nr: str) -> float:
        """When the turn last did anything: its log, its transcript and those of its subagents, its files in .slice-loop."""
        config = os.environ.get("CLAUDE_CONFIG_DIR") or os.path.expanduser("~/.claude")
        pattern = os.path.join(glob.escape(config), "projects", "*", glob.escape(session))
        paths = [log, *glob.glob(f"{pattern}.jsonl"), *glob.glob(f"{pattern}/**/*.jsonl", recursive=True),
                 *glob.glob(f"{glob.escape(LOG_DIR)}/slice-{nr}.*")]
        times = []
        for path in paths:
            try:
                times.append(os.stat(path).st_mtime)
            except OSError:
                pass
        return max(times, default=0.0)

    def watch_turn(self, process: subprocess.Popen, session: str, log: str, nr: str, stalled: threading.Event) -> None:
        """Stops the turn's whole process tree when it shows no activity for SLICE_STALL_SECONDS."""
        started = time.time()
        while process.poll() is None:
            if time.time() - max(started, self.last_activity(session, log, nr)) > self.stall_seconds:
                stalled.set()
                stop_process_tree(process)
                return
            time.sleep(min(30.0, self.stall_seconds / 4))

    def release_lock_of(self, nr: str) -> None:
        """A stopped slice cannot release its full-suite lock any more."""
        if os.path.isdir(LOCK_DIR) and re.match(rf"slice {re.escape(nr)}(\s|$)", self.lock_owner()):
            shutil.rmtree(LOCK_DIR, ignore_errors=True)
            say(f"[slice {nr}] released the full-suite lock it held")

    def claude_turn(self, log: str, model: str, *arguments: str) -> str:
        """Runs one claude turn on model (empty: the CLI default); the output goes to the log as it comes and is
        returned for marker parsing. arguments starts with --session-id or --resume and the session."""
        command = [self.claude, "-p", "--permission-mode", self.permission_mode, *(["--model", model] if model else []),
                   "--settings", COMPACT_HOOK_FILE, *arguments]
        match = re.search(r"slice-([0-9]+)-", log)
        nr = match.group(1) if match else ""
        if os.name == "nt" and self.claude.lower().endswith((".cmd", ".bat")):
            unsafe = [a for a in command[1:] if CMD_UNSAFE & set(a)]
            if unsafe:
                die("an argument holds a character cmd.exe would interpret (one of % ^ & | < > \") and claude is a "
                    f".cmd shim ({self.claude}) \u2013 use the native claude.exe or leave the character out")
        process = subprocess.Popen(command, stdin=subprocess.DEVNULL, stdout=subprocess.PIPE, encoding="utf-8", errors="replace",
                                   env=self.turn_environment(nr), start_new_session=os.name != "nt")
        self.children.append(process)
        stalled = threading.Event()
        threading.Thread(target=self.watch_turn, args=(process, arguments[1], log, nr, stalled), daemon=True).start()
        chunks: list[str] = []
        try:
            with open(log, "a", encoding="utf-8", newline="\n") as handle:
                for line in process.stdout:  # type: ignore[union-attr]
                    handle.write(line)
                    handle.flush()
                    chunks.append(line)
            code = process.wait()
        finally:
            if process.poll() is None:  # interrupted: the turn runs in its own session, so its test runs would live on
                stop_process_tree(process)
            self.children.remove(process)
        if stalled.is_set():
            say(f"[slice {nr}] no activity for {number_text(self.stall_seconds)} s – stopped the turn (log {log})")
            self.event("slice_stalled", nr)
            self.release_lock_of(nr)
            raise TurnStalled(f"slice {nr} stalled")
        if code != 0:
            die(f"claude exited with an error (exit {code}) \u2013 see {log}")
        return "".join(chunks)

    def plan_slice(self, nr: str, session: str, log: str, model: str) -> str:
        """Plans slice nr on its model and waits for approval. Returns the message for the build turn."""
        say(f"[slice {nr}] planning (session {session}, model {model or 'default'}, log {log})")
        output = self.claude_turn(log, model, "--session-id", session, f"/slice {nr} plan auto")
        approval = f"Approved. Build slice {nr} and close it (auto)."
        while True:
            lines = lines_of(output)
            plans = [line for line in lines if line.startswith("PLAN:")]
            if not plans:
                die(f"slice {nr}: no PLAN line \u2013 see {log}")
            decisions = [line for line in lines if line.startswith("NEEDS-DECISION:")]
            say(f"[slice {nr}] {plans[-1]}")
            if self.auto_approve and decisions:
                raise NeedsDecision(decisions)
            if self.auto_approve:
                return approval
            for line in decisions:
                say(line)
            answer = terminal_answer(f"[slice {nr}] Enter = approve, text = change request / answers, q = stop: ")
            if answer is None:
                die(f"slice {nr}: approval needs a terminal \u2013 see {log}")
            if answer == "q":
                die(f"stopped by user before building slice {nr}")
            if not answer:
                return approval
            if decisions:
                return f"Approved with these answers: {answer}. Build slice {nr} and close it (auto)."
            output = self.claude_turn(log, model, "--resume", session,
                                      f"Change request: {answer}. Revise the plan, save it and end again with the PLAN line (auto).")

    @staticmethod
    def slice_finished(nr: str, output: str) -> bool:
        return re.search(rf"(?m)^(SLICE-DONE:|SLICE-READY:) {re.escape(nr)}([^0-9]|$)", output) is not None

    @staticmethod
    def wait_line(nr: str, output: str) -> str:
        """The last SLICE-WAIT line of slice nr in output; empty when the turn did not end waiting."""
        found = re.findall(rf"(?m)^SLICE-WAIT: {re.escape(nr)}(?:[^0-9].*)?$", output)
        return found[-1].rstrip("\r") if found else ""

    def turn_with_one_restart(self, nr: str, session: str, log: str, model: str, message: str) -> str:
        """One turn of a slice; a turn stopped for silence is resumed once, a second silence ends the slice."""
        try:
            return self.claude_turn(log, model, "--resume", session, message)
        except TurnStalled:
            try:
                return self.claude_turn(log, model, "--resume", session,
                                        f"Your last turn showed no activity and was stopped. Read the state from git, "
                                        f"{CHECKLIST_FILE} and {LOG_DIR}/slice-{nr}.checks, run what is missing in the "
                                        f"foreground, then finish slice {nr} and close it (auto).")
            except TurnStalled:
                die(f"slice {nr} stalled twice – see {log}")
                return ""

    def park(self, nr: str, run: dict, reason: str, is_question: bool = False) -> None:
        """Takes a slice out of the round until the user answers; the other slices go on. An answer to an earlier reason
        is gone: left in place, it would send the slice straight back into the same failure."""
        self.replace_file(parked_path(nr), json.dumps({**run, "reason": reason}, ensure_ascii=False, indent=1) + "\n")
        remove_if_there(answer_path(nr))
        self.event("slice_parked", nr, reason=reason, **({"question": "yes"} if is_question else {}))
        say(f"[slice {nr}] parked: {reason}")

    @staticmethod
    def read_parked(nr: str) -> dict:
        """The run a parked slice left; a hand-edited or damaged file stops the run with its name."""
        try:
            with open(parked_path(nr), encoding="utf-8") as handle:
                run = json.load(handle)
            if not isinstance(run, dict) or not {"nr", "session", "log", "model", "reason"} <= run.keys():
                raise ValueError("fields are missing")
        except (OSError, ValueError) as error:
            raise Die(f"{parked_path(nr)} is unreadable ({error}) – repair or delete it") from error
        return run

    def report_parked(self) -> bool:
        """Lists every parked slice and where its answer goes; False when there is none."""
        parked = [entry.nr for entry in self.slices() if os.path.isfile(parked_path(entry.nr)) and not self.is_done(entry.nr)]
        if parked:
            say(f"slice-loop: {len(parked)} slice(s) parked – write the answer to the named file, then start the loop again:")
        for nr in parked:
            say(f"  slice {nr}: {self.read_parked(nr)['reason']} → {answer_path(nr)}")
        return bool(parked)

    def plan_or_park(self, nr: str) -> dict | None:
        """The run of slice nr: a parked slice with an answer goes straight to its build turn, and its files stay until that
        build has worked. A plan that never finished is planned again. None when it was parked."""
        if os.path.isfile(parked_path(nr)):
            run = self.read_parked(nr)
            if not run.get("replan"):
                try:
                    with open(answer_path(nr), encoding="utf-8") as handle:
                        answer = handle.read().strip()
                except (OSError, ValueError) as error:
                    raise Die(f"{answer_path(nr)} is unreadable ({error})") from error
                run["approval"] = f"Approved with these answers: {answer}. Build slice {nr} and close it (auto)."
                return run
            remove_if_there(answer_path(nr))
            remove_if_there(parked_path(nr))
        session = str(uuid.uuid4())
        log = f"{LOG_DIR}/slice-{nr}-{datetime.datetime.now().strftime('%Y%m%d-%H%M%S')}.log"
        run = {"nr": nr, "session": session, "log": log, "model": self.slice_model(nr)}
        self.event("slice_planning", nr)
        try:
            run["approval"] = self.plan_slice(nr, session, log, run["model"])
            return run
        except NeedsDecision as decision:
            self.park(nr, run, " ".join(decision.questions), is_question=True)
        except Die as failed:
            if not self.parallel:
                raise
            self.park(nr, {**run, "replan": True}, str(failed))
        return None

    def build_slice(self, nr: str, session: str, log: str, approval: str, model: str) -> None:
        note = ""
        if self.parallel:
            note = (" Other slices are built in parallel: if git is locked or the integration branch moved, wait and retry; "
                    "in conflicts keep every tick in work/checklist.md.")
        say(f"[slice {nr}] building \u2013 this can take a while (log {log})")
        self.event("slice_started", nr)
        output = self.turn_with_one_restart(nr, session, log, model, approval + note)
        waits = 0
        # SLICE-WAIT: the session ended its turn while its checks still run - it is not done, so check back in it.
        while not self.slice_finished(nr, output):
            waiting = self.wait_line(nr, output)
            if not waiting:
                break
            if waits >= self.wait_max:
                die(f"slice {nr} still waiting after {waits} check-backs ({waiting}) \u2013 see {log}")
            waits += 1
            what = waiting[len(f"SLICE-WAIT: {nr}"):].strip()
            say(f"[slice {nr}] still running: {what} \u2013 checking back in {number_text(self.wait_seconds)}s")
            self.pause(self.wait_seconds)
            output = self.turn_with_one_restart(
                nr, session, log, model,
                f"Still waiting for: {what}. A background command ends with a headless turn: if it left no result, run it "
                f"again in the foreground. When it is done, finish slice {nr} and close it (auto).")
        if not self.slice_finished(nr, output):
            opened = re.findall(r"(?m)^SLICE-OPEN:.*$", output)
            die(f"slice {nr} not finished: {opened[-1].rstrip(chr(13)) if opened else 'no SLICE-DONE or SLICE-READY line'} \u2013 see {log}")
        remove_if_there(answer_path(nr))
        remove_if_there(parked_path(nr))

    def build_in_thread(self, run: dict, failures: dict[str, str]) -> None:
        try:
            self.build_slice(run["nr"], run["session"], run["log"], run["approval"], run["model"])
        except Exception as error:  # whatever went wrong: unrecorded, the slice would count as built and the others be lost
            say(f"slice-loop: {error}")
            failures[run["nr"]] = str(error) if isinstance(error, Die) else repr(error)

    def run_round(self) -> None:
        ready = self.ready_slices()
        if not ready:
            clean = self.report_left()
            if self.report_parked() or not clean:
                raise SystemExit(1)
            die(f"no slice is ready \u2013 check {CHECKLIST_FILE} (/slice status)")
        ready = self.parallel_batch(ready) if self.parallel else ready[:1]
        if self.max_slices > 0:
            ready = ready[:self.max_slices - self.built]
        runs = [run for run in map(self.plan_or_park, ready) if run]
        failures: dict[str, str] = {}
        threads = []
        for run in runs:
            if self.parallel:
                thread = threading.Thread(target=self.build_in_thread, args=(run, failures), daemon=True)
                thread.start()
                threads.append(thread)
            else:
                self.build_slice(run["nr"], run["session"], run["log"], run["approval"], run["model"])
                self.finish_slice(run["nr"])
        # Parallel builds are squashed one after another in list order, each as soon as it and the ones before it are built.
        for run, thread in zip(runs, threads):
            thread.join()
            if run["nr"] in failures:
                self.park(run["nr"], run, failures[run["nr"]])
                continue
            self.finish_slice(run["nr"])
        self.built += len(runs)

    def close_round(self) -> None:
        """Closes the round in one more fresh session and prints its ROUND-DONE line."""
        log = f"{LOG_DIR}/close-{datetime.datetime.now().strftime('%Y%m%d-%H%M%S')}.log"
        say(f"[round {self.round}] all slices done \u2013 closing the round (log {log})")
        output = self.claude_turn(log, "", "--session-id", str(uuid.uuid4()), "/slice close auto")
        done = re.findall(r"(?m)^ROUND-DONE:.*$", output)
        if not done:
            opened = re.findall(r"(?m)^ROUND-OPEN:.*$", output)
            die(f"round {self.round} not closed: {opened[-1].rstrip(chr(13)) if opened else 'no ROUND-DONE line'} \u2013 see {log}")
        say(done[-1].rstrip("\r"), out=True)
        say(f"Round closed. Next: test with the manual test list in {ROUNDS_DIR}/, then start the next round with the vibe-coding skill.", out=True)

    # --- entry points ---

    def run_merge(self) -> int:
        if self.watch:
            if self.watch_running():
                say(f"slice-loop: already watching ({WATCH_ALIVE} is under 60 minutes old; delete it if no watch runs)")
                return 0
            self.watching = True
            if not self.wait_for_ready_slice():
                return 0
        if not self.take_merge_lock():
            return 0
        self.merge_queue()
        self.release_merge_lock()
        clean = self.report_left()
        if self.verify and not self.verify_chain():
            clean = False
        return 0 if clean else 1

    def run_notify(self) -> int:
        """--notify: waits for a slice with a problem or a question for the user, prints one line per such event and
        exits, so a chat that started it as a background command wakes up. .slice-loop/notify.seen keeps the place, so a
        restart misses nothing; the first start begins at the end of the history. Also exits when no round is open."""
        lines = read_lines(EVENTS_FILE) if os.path.isfile(EVENTS_FILE) else []
        seen = int(read_text(NOTIFY_SEEN).strip() or 0) if os.path.isfile(NOTIFY_SEEN) else len(lines)
        for check in range(self.watch_max):
            if check:
                self.pause(self.watch_seconds)
            lines = [line for line in read_lines(EVENTS_FILE) if line] if os.path.isfile(EVENTS_FILE) else []
            notices = [text for text in map(notice_of, lines[seen:]) if text]
            seen = len(lines)
            self.replace_file(NOTIFY_SEEN, f"{seen}\n")
            if notices:
                for text in notices:
                    say(text, out=True)
                return 0
            if not self.open_round():
                say("SLICE-NOTIFY: no open round – nothing to watch", out=True)
                return 0
        say(f"SLICE-NOTIFY: no problem after {self.watch_max} check(s) – start --notify again", out=True)
        return 0

    def run_rounds(self) -> int:
        self.round = self.open_round()
        if not self.round:
            say(f"slice-loop: no open round in {ROUNDS_DIR} \u2013 start a round with the vibe-coding skill first")
            return 0
        if not self.round_slices():
            say(f"slice-loop: round {self.round} has no slices in {SLICES_FILE} – nothing to build, the round stays open")
            return 0
        self.claude = shutil.which("claude") or ""
        if not self.claude:
            die("claude CLI not found")
        self.squash_reported()
        while not self.all_done():
            self.run_round()
            if self.max_slices > 0 and self.built >= self.max_slices and not self.all_done():
                say(f"slice-loop: stopped after {self.built} slice(s) \u2013 {self.open_count()} still open")
                clean = self.report_left()
                self.report_parked()
                return 0 if clean else 1
        self.close_round()
        return 1 if self.link_lost else 0

    def add_log_dir_exclude(self) -> None:
        """Keeps .slice-loop/ out of git status: one line in .git/info/exclude (a repository without that file is left)."""
        exclude = os.path.join(".git", "info", "exclude")
        if not os.path.isdir(os.path.dirname(exclude)):
            return
        existing = read_lines(exclude) if os.path.isfile(exclude) else []
        if f"{LOG_DIR}/" not in existing:
            with open(exclude, "a", encoding="utf-8", newline="\n") as handle:
                handle.write(f"{LOG_DIR}/\n")

    def run(self, args: list[str]) -> int:
        self.parse_args(args)
        if not (os.path.isfile(SLICES_FILE) and os.path.isfile(CHECKLIST_FILE)):
            die(f"run from the repo root (needs {SLICES_FILE} and {CHECKLIST_FILE})")
        if self.dry_run:
            for nr in self.ready_slices():
                say(nr, out=True)
            return 0
        os.makedirs(LOG_DIR, exist_ok=True)
        self.add_log_dir_exclude()
        if self.notify:
            return self.run_notify()
        self.stale_lock_notice()
        self.baseline_notice()
        if self.merge_only:
            return self.run_merge()
        self.write_compact_hook()
        return self.run_rounds()

    def clean_up(self) -> None:
        """What the shell version did in its exit trap: the sign of life, the queue lock, a merge in progress and any
        claude still running."""
        for child in list(self.children):
            if child.poll() is None:
                stop_process_tree(child)
        if self.watching:
            try:
                os.unlink(WATCH_ALIVE)
            except OSError:
                pass
        self.release_merge_lock()
        if self.merging:
            self.reset_merge()


def age_seconds(path: str) -> float:
    try:
        return time.time() - os.stat(path).st_mtime
    except OSError:
        return 0.0


def parked_path(nr: str) -> str:
    return f"{LOG_DIR}/slice-{nr}.parked"


def answer_path(nr: str) -> str:
    return f"{LOG_DIR}/slice-{nr}.answer"


def remove_if_there(path: str) -> None:
    try:
        os.unlink(path)
    except OSError:  # missing, or held by a scanner: a built slice never reads it again
        pass


def stop_process_tree(process: subprocess.Popen) -> None:
    """Stops a turn with everything it started; a surviving test run would keep the pipe open and the files locked."""
    try:
        if os.name == "nt":
            subprocess.run(["taskkill", "/T", "/F", "/PID", str(process.pid)], stdin=subprocess.DEVNULL, capture_output=True)
        else:
            os.killpg(process.pid, signal.SIGKILL)
    except OSError:
        pass
    if process.poll() is None:
        process.kill()


def after_compact_text() -> str:
    """What a slice session reads after a compaction (SessionStart hook): the rules may be gone from its summary."""
    nr = os.environ.get("SLICE_NR", "NN")
    return (f"Your context was just compacted. You work on slice {nr} in auto mode, driven by a script: nobody answers questions. "
            f"Before going on, read .claude/commands/slice.md (sections 4 and 5) and the plan of slice {nr} in work/plans/ again, and "
            f"take the state from git on slice-{nr}, {CHECKLIST_FILE} and {LOG_DIR}/slice-{nr}.checks in the main checkout, not "
            "from your summary. Run long checks in the foreground. End with exactly one marker line of section 5.")


def on_terminate(_signal_number: int, _frame: object) -> None:
    raise KeyboardInterrupt


def use_utf8_streams() -> None:
    """Whatever the console or PYTHONIOENCODING say, the loop reads and writes UTF-8 (the dashes in its messages, the
    markers of Claude, the answer typed at the approval prompt); line buffering keeps the output as it comes."""
    for stream in (sys.stdin, sys.stdout, sys.stderr):
        stream.reconfigure(encoding="utf-8", errors="replace")  # type: ignore[union-attr]
    sys.stdout.reconfigure(line_buffering=True)  # type: ignore[union-attr]
    sys.stderr.reconfigure(line_buffering=True)  # type: ignore[union-attr]


def main(args: list[str]) -> int:
    use_utf8_streams()
    if args == ["--after-compact"]:  # a hook of a running session: needs no settings, so a bad SLICE_* value cannot break it
        say(after_compact_text(), out=True)
        return 0
    signal.signal(signal.SIGTERM, on_terminate)
    loop: Loop | None = None
    try:
        loop = Loop()
        return loop.run(args)
    except Die as error:
        say(f"slice-loop: {error}")
        if loop is not None and not loop.notify:  # the whole run stopped: nobody learns that from a slice's event
            loop.event("loop_stopped", reason=str(error))
        return 1
    except KeyboardInterrupt:
        return 130
    finally:
        if loop is not None:
            try:
                loop.clean_up()
            except Die as error:
                say(f"slice-loop: {error}")


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
