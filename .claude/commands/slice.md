---
description: Work the current round slice by slice – next slice, a given slice, status, or close the round
argument-hint: "[status | NN | close | quick <finding>] [plan] [auto | parallel] [direct]"
---
You work the open round of this repository slice by slice. Arguments: "$ARGUMENTS".
Answer me in the language of CLAUDE.md or README.md.
"auto" means a script drives this session headless: nobody can answer questions mid-run. Run long checks in the
foreground: a headless run ends background commands with its turn.
Never stop to ask in auto mode – decide on the documented default, or end with the marker
lines of section 5 so the script can ask me.
Everything specific to this repository comes from work/ and CLAUDE.md; this command never needs editing.

## 1. Decide what to do
The open round is the record in work/rounds/ whose first line is exactly "Status: open"; its id
Rnn is the start of the file name. A slice belongs to a round by the field "round" in
work/_slices.json. A slice is done when its row in work/checklist.md has a ☑ and no ☐.
- no open round → say "No open round – start one with the vibe-coding skill" and stop; change nothing.
  More than one → name them and stop (auto: "SLICE-OPEN: NN more than one round is open – close one
  first"; with "close": "ROUND-OPEN: Rnn more than one round is open").
- "status" → read work/checklist.md, work/_slices.json and the git branches named slice-*.
  Show one table for the slices of the open round: slice, name, model (field "model" in
  work/_slices.json), state (done / parked (.slice-loop/slice-NN.parked names why) / in progress / ready to merge / ready / blocked by …; "in progress" = not done,
  but a branch slice-NN exists or the row has a ☑; "ready to merge" = a branch slice-NN whose row is ticked
  through Checks), then the round's check level and its next milestone (section 4, step 1), the number of open
  decisions in the round record, one line for a held .slice-loop/full-suite.lock (owner, since when) and one line
  "Ready now: …" – the ready slices, each with the slices it shares a path of its field "files" with ("70 shares
  src/EntryPreview.cs with 69"). Two or more ready: offer parallel sessions (.claude/slice/parallel.md). Stop there.
- "close" → read and follow .claude/slice/close.md.
- "quick" → read and follow .claude/slice/quick.md.
- a number NN (two or more digits) → work on slice NN. Already done: say so and stop. A slice it
  depends on (field "deps") is not done: say which one and ask before going on
  (auto: print "SLICE-OPEN: NN blocked by <deps>" and stop).
- nothing → pick the lowest-numbered slice of the open round that is not done and whose
  dependencies are all done. Say which one and why; if others are ready too, offer them parallel
  sessions first (.claude/slice/parallel.md), then go on with yours. Slices are left but none is ready:
  show the status table and stop. Every slice of the round is done: say "All slices of Rnn done",
  propose "/slice close" (auto: print "ALL-DONE") and stop.

## 2. Read first, then check the baseline
- work/conventions.md – integration branch, merge style, the commands (baseline check, checks for a
  slice, full check chain), check level, protocol file, documents that must change with a change, language,
  privacy rule
- the open round record work/rounds/Rnn-*.md – source, brief, decisions so far (a check level and milestones for
  the round), open decisions
- work/slices/NN-*.md – goal, source, model, check level, scope, patterns to mirror, NOT building, prompt,
  Definition of Done, non-goals
- CLAUDE.md (and AGENTS.md when present) – the repository's hard rules
- work/session-hints.md – the repository's hints for slice sessions, when it exists
- what the slice's Source names: the findings file or concept document (the round record only
  references it), work/research.md when the slice cites it
Before you change anything the working tree must be clean and the integration branch checked out;
otherwise say what to do (auto: "SLICE-OPEN: NN <reason>"). Then create the slice's worktree (recipe in section 3)
and run the baseline check from work/conventions.md there, before your first change (a full suite takes the lock of
section 4): a build in the main checkout also picks up files of other slices' worktrees. Red → change nothing and ask
whether to fix that first (auto: "SLICE-OPEN: NN baseline red: <check>" and stop). No baseline check listed: only a
slice that creates the checks itself may go on; otherwise stop ("SLICE-OPEN: NN no baseline check").

## 3. Plan and build the slice
A branch slice-NN or a worktree for it may exist already (normal after a red slice and a re-run):
continue on it, never create a second one and never delete or reset it silently. Re-read its plan in
work/plans/ (and the superpowers ledger, if there is one) and the round record, and resume where they say.
If the superpowers skills are available and "direct" is not in the arguments:
1. worktree recipe: `git worktree add .claude/worktrees/slice-NN -b slice-NN <integration branch>`, then enter
   that folder; `.claude/worktrees/` goes into `.git/info/exclude` once. Use superpowers:using-git-worktrees
   only where it follows this recipe. The squash always runs in the main checkout.
2. superpowers:writing-plans – save the plan as work/plans/<YYYY-MM-DD>-slice-NN.md with a section
   "Assumptions (confirm on approval)" and a "Review focus"; mirror the slice's patterns, leave out
   its NOT-building list, and name the failing test for every acceptance case (the test name
   carries the Given/When/Then). Review the plan and show it to me before writing any code.
3. after my approval: superpowers:subagent-driven-development (superpowers:executing-plans without
   subagents) – tests first (red), then the minimum to get green, every rule in CLAUDE.md and
   work/conventions.md; task commits on slice-NN are fine, each passed with `git commit -F <file>` (section 4)

Otherwise (no superpowers, or "direct"):
1. the worktree recipe above (new branch "slice-NN" in its own worktree)
2. follow the prompt in the slice file: present the plan (files, classes, tests, assumptions, review
   focus), save it as work/plans/<YYYY-MM-DD>-slice-NN.md and wait for my approval
3. failing tests first, then the minimum to get green, then refactor

Rules for plan and build (both paths):
- Assumptions: one numbered round, a recommendation per point, worded so that "yes" takes them all. Facts come
  from code and docs; only decisions are asked. An unambiguous task skips the round.
- Seams: the plan names the seams the tests hit; the review focus says per seam what the test catches and what it misses.
- Tests: expected values come from an independent source, not computed like the code (that is tautological).
  Red counts when the test checks exactly the fault or requirement. Red means the new test fails against the code
  before the change (stash the change, or run it in a second worktree on the integration branch); a test made to fail
  by a wrong expectation is not red – say this in every brief to a subagent. Write that red run to the checks proof
  (section 4) with its exit code and the SHA it ran on; tick Tests red only with such a line, so test and code are not
  written in one go. Run the type check and the single test file often, the checks of your check level (section 4)
  once at the end – a full suite once at the end only where the level asks for one; refactor after green.
- Before writing a test, read .claude/slice/tests.md (which level, how many, how to write it) and say so in every brief.
- A fault: start with a command that shows it red, before any hypothesis.
- A screen's behaviour is tested through the real input (click, key, focus) where the repository's test harness can,
  not only by setting view-model state: a click path that breaks stays green otherwise.
- Generated outputs (screenshots, snapshots, generated clients): commit those the slice's change explains, reset the rest.
- Check scope beyond the slice's own screen: a rule added to an endpoint, a service or a base class → search every
  caller and run their tests, or (from check level 2) the full suite before "green"; a change to a shared control or base → run its own tests
  first, before changing it; a change to key bindings, focus or tab order → add the repository's focus-order tests.
- New finding ids: take them from the slice's reserved range (field "ids" in work/_slices.json, column "New ids" of
  "## Slices"). Without one, read the ids used on the integration branch (round record and findings file) and take
  the next free ones; after the merge of step 6a, renumber your own new ids that collide.
- Stop only for a real blocker: a missing dependency, a contradiction to the plan, or an ambiguity that would
  produce wrong code under a wrong assumption. Trivia the code answers, style questions (follow existing patterns)
  and anything a test can settle go on.

"plan" in the arguments → stop right after the plan is saved and shown. End with the line
"PLAN: <path of the plan file>", plus one line "NEEDS-DECISION: <question>" for each point that
changes architecture, scope, cost or licences, or adds a package the repository does not use yet,
and that the plan cannot settle by an assumption. The next message in this same session either approves ("build
and close it"), carries answers to the questions (record each as a Ruling, section 4), or is a change
request (revise the plan, save it, end again with the PLAN line).
Without "plan" and with "auto": go on with the plan's assumptions; a point that needs a decision ends
the run with its NEEDS-DECISION line and nothing built.

The slice's model is the field "model" of its entry in work/_slices.json: the cheapest model that does
this slice reliably, and the model this session was started on. Subagents of this slice (explorers,
implementers) run on that model or a cheaper one; only a reviewing pass may use a stronger one, and only
when work/conventions.md asks for it.

As each step finishes, tick its column in the slice's row of work/checklist.md on slice-NN: Plan when
the plan is approved, Tests red when the new tests fail for the right reason, Green when they pass.
A column that does not apply (for example Tests red in a harness or docs-only slice) is ticked too,
and the reason goes into the round record under Decisions as a Build decision (section 4, step 2), so the row has no ☐ left.

Keep me posted: one short status line at least every few minutes (what finished, what runs next); with `auto` or
`parallel`, only the marker lines of section 5.
Long reports of subagents go to work/_raw/ (git-ignored), not into the chat.

## 4. Close the slice
Do 1–4 on slice-NN, so they land in the same commit as the code.
1. Run the checks that work/conventions.md names for a slice (always including every command of the baseline
   check); check the Definition of Done in the slice file, including "no test weakened or skipped" and "no new
   package without approval".
   Check level (how often and how thoroughly a slice is checked): 1 to 5, default 3. It comes from the line
   "- Check level" of work/conventions.md; the round record may set another level and name its milestones under
   Decisions, and the slice file's "Check level" sets one for this slice only (the most specific one wins). "Own
   checks" are the smallest fitting tier of work/conventions.md's checks for a slice; "shared code" is a shared UI
   control, style or the shell, middleware, a database trigger, or a new rule on an endpoint, service or base class.
   The baseline check runs on every level.
   1. Own checks without the scan tests; full suites stay out of the slice; the full check chain only at round close.
   2. Own checks including the scan tests; the full suite of an area only when the slice touches shared code; the full
      check chain only at round close.
   3. (default) Like 2, plus `slice_loop.py --merge --verify` at the round's milestones and at round close, not after
      every group.
   4. Like 3, plus `--merge --verify` after every group, and after merging the integration branch (step 6a) the full
      suites of the areas the integration branch changed since the old base.
   5. The full check chain in every slice on the tip and after every merge of the integration branch; `--merge --verify`
      after every group.
   A red test is first re-run alone; the whole suite runs again only when it stays red. A red test that is green when run
   alone or in a second unchanged run is load, not a regression: note it under Surprises as "load, not a fault" (test, both
   runs) and go on.
   The run that proves the slice is the last one: on the final tip, after steps 2–5 and the merge of step 6a when it runs. A run
   before a later commit must be repeated, so earlier runs cover what you changed and the full set runs once, at the end.
   Checks proof: write each check you run to .slice-loop/slice-NN.checks in the main checkout (the parent of
   `git rev-parse --git-common-dir`), one line per check: `<exit code> <commit SHA> <command>` – the command exactly
   as work/conventions.md writes it, the SHA of `git rev-parse HEAD` on slice-NN when it ran. Always append; the merge
   queue reads only the lines with the branch tip, the last one per command, so a stale run cannot wipe the proof. A run on uncommitted changes (such as the red step of a test) gets no proof line. A
   filtered test run that selected no tests proves nothing: correct the filter and write no proof line for that run.
   Review the whole branch diff with a fresh, read-only reviewer (a subagent that did not write the code), on two axes, kept apart: specification (every finding cites the plan line; ask about unrequested
   scope) and standards (the repository rule beats the general rule; what tools enforce stays out).
   Full-suite lock (also for the baseline check of section 2): before a command that work/conventions.md names a full
   suite, take .slice-loop/full-suite.lock in the main checkout with `mkdir` (PowerShell: `New-Item -ItemType Directory`
   without `-Force`), which fails when it exists, and write `owner` inside: `slice NN` and an ISO-8601 time. Remove it
   right after the suite, red or green. A lock held by a slice whose branch slice-MM exists and whose time is under 60
   minutes old: retry every minute for at most 30 minutes. Any other lock (branch gone, older, or the wait ran out) is
   reported with owner and time (auto: `SLICE-OPEN: NN lock held by slice MM since <time>`); leave it untouched.
2. Append to the round record: under Decisions one line per decision in one of two formats – "Ruling: <decision> –
   User, <date>" for my answers and changes, "Build decision (slice NN): <decision> – Claude, <date>" for what you
   decided while building (a column that does not apply, a point the code settled); the merge queue refuses a slice
   that adds a line in another format there. Under Surprises what the plan did not foresee, under Deferred minors what
   you left out on purpose. Every question for me is a "NEEDS-DECISION: <question>" line under Open decisions,
   not only a note elsewhere. In auto mode the approval of a plan comes from the script, not from me: it is no Ruling.
   Under Manual test list insert the slice's block in slice-number order, unindented and in exactly this format, because
   the merge queue refuses any other (.claude/slice/close.md has the rules): a line "### Slice NN – <name>", a line
   "Start: <command>", the numbered steps each followed by one line "Expected: <observable result>", then one line
   "Closes: <ids>" and one line "Result: ☐". A step of an earlier slice's block that your change makes
   wrong gets "(superseded by slice NN: <what holds now>)" at the end of its Expected: line, so I do not test what no
   longer applies.
   When "## Source" names a tracked findings file, mark the slice's ids there as "done in slice NN" in the
   marking format the record names, before the squash, and add new findings there in the file's own format.
   Decisions are recorded as "User, <date>" unless work/conventions.md allows names. No absolute
   paths, user or machine names in any file; personal names only where work/conventions.md allows.
3. Write the entry that work/conventions.md names for the decision protocol, if it names one, in
   that file's own format and with its fields; leave out fields that exist only for pull requests
   (number, URL): the slice lands as a local squash commit. Update the documents that work/conventions.md lists as
   "must change with a change" (status page, help). Write an ADR only when the decision is hard to reverse,
   would surprise without context or settled a real conflict of goals; everything else is a Ruling in the round record.
4. Tick Checks in the slice's row of work/checklist.md (replace ☐ by ☑), and Commit last, right
   before the squash commit, so the finished row lands in it. No ☐ may be left in the row: a column
   that does not apply is ticked too, with the reason under Decisions (see section 3).
5. Commit these edits on slice-NN (task commit) before merging; uncommitted edits are lost and the
   loop stops with "reported done but not ticked". Pass the message with `git commit -F <file>` (a file or
   a heredoc), never inline: Git Bash rewrites a leading `/` into a path.
   With `auto` or `parallel`, do 6a, then stop here: write the squash message (commit language and style of
   work/conventions.md, naming the slice and its ids) to .slice-loop/slice-NN.msg in the main checkout, leave Commit at
   ☐, do not merge. Before SLICE-READY, all of these hold: the row is ticked through Checks, the worktree is clean, and
   the checks proof has a green line on the tip for every check and every baseline command. As the very last write, put
   the tip's SHA (`git rev-parse HEAD`) into .slice-loop/slice-NN.ready in the main checkout – again after any later
   commit or merge – and end with the last line SLICE-READY: NN. The merge queue squashes only a slice whose ready file
   names its tip, so it leaves a session alone while its last checks run.
   The orchestrating session reacts only to SLICE-READY, NEEDS-DECISION and SLICE-OPEN, so report nothing else: a
   fault found or a decision needed is said at once with its marker; "suite still running" is no report – background
   checks wake you anyway. Only when the harness ends your turn while checks or a subagent still run, end with the one line
   "SLICE-WAIT: NN <what runs>" and no status text – not with SLICE-READY, and not with "I wait for …" without the marker.
   Without both arguments go on with step 6.
6. Integrate: superpowers:finishing-a-development-branch (or by hand) – ONE squash commit on the
   integration branch named in work/conventions.md. Before the squash, in this order:
   a. merge the integration branch into slice-NN only when needed: `git merge-tree --write-tree <integration branch>
      slice-NN` reports a conflict, or `git diff --name-only <old base> <integration branch>` (old base: `git merge-base
      slice-NN <integration branch>`) lists a file the slice also changes, documents (*.md) and work/ aside. Otherwise
      keep your tip and its proof: the queue squashes a slice cut from an older integration branch as it is, and leaves
      it behind ("integration branch moved and touches <files>") when such a file turns up later – then merge here.
      After a merge, repeat the baseline check and your own checks on the merged tip before the squash or SLICE-READY;
      full suites only when your check level asks for them (step 1) or the overlap lists a file; the interplay of
      slices is covered by `--merge --verify` as the level asks; append these runs to the checks proof (the merged tip);
   b. in the main checkout, `git merge --squash slice-NN`;
   c. check the resolved files before committing: `grep '^<<<<<<<'` finds nothing and every line of both sides
      (`git show HEAD:<file>` and `git show slice-NN:<file>`) is still present, every `Result:` line too;
   d. commit in a separate command with `git commit -F <file>`, the message in the commit language and style
      of work/conventions.md, naming the slice and its finding or AC ids; resolving and committing never run
      in one command. Never push. If the merge conflicts in work/checklist.md or the round record, keep every tick and
      entry from both sides; lines both sides only added keep both, a table row both changed becomes one row with both
      changes. In auto mode take this option without asking, then remove the merged slice-NN branch and its own
      worktree, nothing else.
7. Two or more slices are ready now: offer parallel sessions for them (.claude/slice/parallel.md) before the lines below.
End with exactly three lines:
- "Slice NN done" (or what is still open and why)
- "Ready next: …" (e.g. "07, 08, 09 – can run in parallel in separate sessions"; all done: "none –
  type /slice close")
- "Next step: start a fresh session and type /slice"
  (auto: instead "SLICE-DONE: NN" when everything is green, ticked and on the integration branch,
  else "SLICE-OPEN: NN <reason>")

## 5. Marker lines
The loop script (.claude/scripts/slice_loop.py) reads these lines. Print each on a line of its own,
from the first column, exactly as written – no bullet, quote or code format, nothing before it:
- "PLAN: <path of the plan file>" – last line of a "plan" run
- "NEEDS-DECISION: <question>" – one per open point, before the PLAN line
- "SLICE-DONE: NN" – the slice is green, ticked and on the integration branch
- "SLICE-READY: NN" – branch committed, checklist ticked through Checks, squash message and ready file written; waits for the merge queue
- "SLICE-WAIT: NN <what runs>" – the harness ends the turn while checks still run in the background: the slice is still
  running, not done; one line, no report; the loop checks back in this session, an orchestrating chat does not answer it
- "SLICE-OPEN: NN <reason>" – anything else that stops the slice
- "ALL-DONE" – every slice of the round is done, the round is still open
- "ROUND-DONE: Rnn" – the round is closed
- "ROUND-OPEN: Rnn <reason>" – the round could not be closed
- "QUICK-DONE: <commit>" – a quick fix (.claude/slice/quick.md) is committed on the integration branch
