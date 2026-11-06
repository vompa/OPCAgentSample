# Quick fix
Read by `/slice quick <finding>`. For a small change outside a slice, typically after a round is closed (a finding from the manual test, a review of a screen). Too big for one session or a decision needed → say so and propose it for the next round instead.

1. /slice section 2 first: read, clean tree, baseline check. Work on the integration branch or a branch quick-<slug>.
2. Record the finding with the next free id (/slice section 3) in work/quick-fixes.md (create it with the header `| Date | Id | Finding | Test | Commit |`), and in the findings file when the open or last round names one.
3. A failing test first (red as in /slice section 3), then the minimum to get green; the check scope of section 3 applies.
4. The full check chain from work/conventions.md, green; the protocol entry work/conventions.md names (/slice section 4, step 3) and the documents that must change with a change.
5. One commit on the integration branch with `git commit -F <file>`; fill in the row's Commit cell. Never push.

End with "QUICK-DONE: <short commit SHA>". The next round's intake reads work/quick-fixes.md.
