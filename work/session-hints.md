# Hints for slice sessions
Repository-specific experience for the sessions that work a slice. They add to `.claude/commands/slice.md` and `work/conventions.md`; every round appends what it learned.

- GateGuard blockt den ersten Edit oder Write je Datei und den ersten Shell-Befehl einmal: Fakten nennen und genau einmal wiederholen. Edits und Commit nie im selben Aufruf-Batch.
- `.editorconfig` verlangt LF, das Write-Tool schreibt unter Windows CRLF: neue Dateien vor `dotnet format` auf LF umstellen.
- Private statische Felder heißen PascalCase, private Instanzfelder `_camelCase`; `IDE1006` und `IDE0005` brechen den Build oder `dotnet format`.
- `WebApplicationFactory`-Tests laufen in der Umgebung `Testing`, damit kein echter Claude-Key aus den User-Secrets greift. Nie echte API-Aufrufe in Tests.
- Der Testfilter ist VSTest: `dotnet test … --filter "FullyQualifiedName~Name"`. `-- --filter-class` wird ignoriert und startet die ganze Suite.
- Vor einer vollen Suite eigene Simulator-, Server- und Testprozesse beenden. Andere Sitzungen auf demselben Rechner erzeugen Redis-Timeouts in Ingestion- und Snapshot-Tests; das zuerst prüfen, bevor es als Regression gilt.
- Check commands never end in `| tail` or another pipe inside an `&&` chain: the pipe hides the exit code; write the output to a file and read its exit code.
- Stopping a background check leaves its child processes (test runners, builds) running: find them by the worktree path in their command line and end them, or the next build finds its files locked.
- Generated files you did not touch that show as changed or missing usually come from another session's build in this checkout: wait until no such build runs (at most 10 minutes), look again, leave them alone.
- Every timeout or wait raised in a test goes into the protocol entry with its reason and the measured time.
- Der Quelltext des Adapters zur eingebundenen Version: Tag `Anthropic-v12.57.0` im Repo `anthropics/anthropic-sdk-csharp`, Datei `src/Anthropic/AnthropicClientExtensions.cs`, per `gh api` lesen.
