# Round close

Read by `/slice close` (the loop runs `/slice close auto` once after the last slice) and by slices for the block format. The skill copies this file to `.claude/slice/close.md` in the target repository. Only the open round; never ask in auto mode.

## Steps
1. **Precondition.** Exactly one round record has the first line `Status: open`. Every slice whose `round` field in `work/_slices.json` names that round is done (a `☑` and no `☐` in its checklist row). The working tree is clean and the integration branch is checked out. Otherwise list what is missing and end with `ROUND-OPEN: Rnn <reason>`.
2. **Full check chain** from `work/conventions.md`, run on the integration branch (not in a slice worktree). Red → change nothing, end with `ROUND-OPEN: Rnn <failing check>`; fixing it is a slice of its own, decided by the user.
3. **Manual test list** into the round record (format below). First validate the drafted blocks against the format; a block that breaks it is not repaired: end with `ROUND-OPEN: Rnn manual test list: <slice and problem>`. Then add the slices that have no block yet, taking the items from the `manual` fields of this round's slices.
4. **Close the record.** Set the first line to `Status: closed`; commit the record in the commit language and style of `work/conventions.md` (`Round Rnn closed: <slug>`); never push.
5. **Report.** List as bullets in the closing message the open decisions – the entries under `## Open decisions`, plus every place outside that section that leaves a decision to the user ("user decides", "<name> decides", "<Name> entscheidet", "decision pending", "to be decided") –, never as lines starting with `NEEDS-DECISION:` (that marker belongs to plan runs); the next round inherits them. Then give the handover message (below).
6. **Leftovers.** List the worktrees (`git worktree list`) and the branches `slice-*`, `agent-*` and `worktree-*` whose work is on the integration branch, with their tip SHA. Outside auto mode offer to remove the safe ones – only without changes (clean worktree, no commit missing from the integration branch); tip SHAs go into the closing message first, links (junctions, symlinks such as `node_modules`) are removed first because git follows them and empties their target, then `git worktree remove`, then the branch. In auto mode only list them.

End with `ROUND-DONE: Rnn`.

## Manual test list
Under `## Manual test list` in the round record: one block per slice that has a manual check, in slice-number order, without indentation. Each slice inserts its own block before the squash; the merge queue keeps the blocks in slice order and refuses to squash a slice whose block breaks the format.
```
### Slice NN – <name>
Start: <command from work/conventions.md>
1. <what to click or type>
Expected: <what must be visible or happen>
2. <next step>
Expected: <what must be visible or happen>
Closes: <finding or AC ids>
Result: ☐
```
- The format, enforced: steps numbered `1.` to `n.` without gaps, exactly one `Expected:` line after each step, exactly one `Closes:` and one `Result:` line per block, one block per slice.
- One action per numbered step. `Expected:` names something observable (text, colour, count, state), never just "works".
- `Closes:` uses the ids of the round record, so every answer can be given per id.
- `Result:` is ticked by the human, or replaced by what they saw. The checklist stays agent-only; the loop never reads this list.

## Handover message
Written in the user's language, after the commit of step 4:

> Round Rnn is closed: N slices are on `<integration branch>`, the full check chain is green, nothing is pushed.
> Next: try the build with the manual test list in `work/rounds/Rnn-<slug>.md` (section "Manual test list") – each block has the start command, the steps and what you should see.
> Report what you find: answer in chat or add it to the round record; the next round starts from your notes.
> Open decisions: `<list, or none>`.

## The next round inherits
- A new record `R(nn+1)-<slug>.md` with `Status: open`. The closed record is never edited again. Only one round is open at a time; a new intake first closes the open round or stops.
- Every `NEEDS-DECISION:` line still open in the closed record is copied into the new record's `## Open decisions`, marked `(from Rnn)`. The user answers them at intake; each answer becomes a `Ruling:` line in the new record, and the items that waited for it can be sliced.
- The user's notes after the manual test are the findings list of the next round (`intake.md`, A). A finding that shows an earlier fix did not hold is marked `reopens <id>`; its intake row names the slice that closed it.
- Slice numbers and checklist rows continue; the new slices carry the new round id in `round`.
