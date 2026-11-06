# Conventions – OPCAgentSample
> Pointers, not copies. One owner per fact: the document named after the arrow.

- Integration branch: feature/meai-chatclient (nur für Runde R01; Standard sonst main) ← docs/arbeitsanweisung-meai.md, User 2026-11-02
- Slice branch: slice-NN, cut from the integration branch ← default
- Merge style: one squash commit per slice onto the integration branch; never push ← default
- Commit language and style: Deutsch, Präfix feat/fix/docs/chore/test, optional Bereich in Klammern ← git log
- Build: `dotnet build OPCAgentSample.slnx -c Release` ← .github/workflows/ci.yml
- Test: `dotnet test OPCAgentSample.slnx --no-build -c Release`, einzelne Klassen mit `--filter "FullyQualifiedName~Name"` (xUnit 2, VSTest) ← .github/workflows/ci.yml
- Lint / format check: `dotnet format OPCAgentSample.slnx --verify-no-changes --no-restore`; Web im Ordner src/OPCAgentSample.Web `npm run lint`, `npm run format:check` ← .github/workflows/ci.yml
- Baseline check (Basisprüfung, vor jeder Slice): `dotnet build OPCAgentSample.slnx -c Release`, `dotnet format OPCAgentSample.slnx --verify-no-changes --no-restore` ← .github/workflows/ci.yml
- Checks for a slice: nur Agent-Code: `dotnet test tests/OPCAgentSample.Tests -c Release --no-build --filter "FullyQualifiedName~OPCAgentSample.Tests.Agent"`; gemeinsamer Code oder Registrierung: `dotnet test OPCAgentSample.slnx --no-build -c Release`; never two full suites at once (the lock `.slice-loop/full-suite.lock/`, see /slice) ← default
- Test worker limits under load: die .NET-Suite läuft rund 10 Minuten; unter fremder Last laufen Ingestion- und Snapshot-Tests in Redis-Timeouts (1 s), dann zuerst prüfen, was nebenher läuft ← Erfahrung R01
- Check level: 3 – how often and how thoroughly a slice is checked, 1 to 5 (levels: /slice section 4, step 1) ← default
- Full check chain (Gesamtprüfung; round close, and slice_loop.py --merge --verify as the check level asks): `dotnet build OPCAgentSample.slnx -c Release`, `dotnet format OPCAgentSample.slnx --verify-no-changes --no-restore`, `dotnet test OPCAgentSample.slnx --no-build -c Release` ← .github/workflows/ci.yml
- Decisions are recorded in: Rundenakte work/rounds/, Abschnitt Decisions ← default
- Must change with a change: README.md, wenn sich Aufbau oder Konfiguration ändern ← README.md
- Pull-request rule: Am Rundenende ein Draft-PR gegen main; nur nach Freigabe gepusht ← docs/arbeitsanweisung-meai.md
- Language: Oberfläche, Doku und Kommentare Deutsch; Bezeichner Englisch; Testnamen Deutsch oder Englisch wie in der jeweiligen Datei ← git log
- Privacy rule for names in files: only "User, <date>"; öffentliches Repo ohne Pfade und Mailadressen ← default
- Start the app for manual testing: `docker compose up -d`, `dotnet run --project src/OPCAgentSample.Simulator`, `dotnet run --project src/OPCAgentSample.Server` ← README.md
- Work folder: work/
- Finding ids: F-NN ← default

## Conflicts between documents
docs/arbeitsanweisung-meai.md nennt „kleine Commits (Interface → Adapter → Tests → Aufräumen)“; vibe-coding nennt einen Squash-Commit je Slice. Gelöst durch je einen Squash-Commit für diese Schritte. Die übrigen Abweichungen der Anweisung stehen als Rulings in work/rounds/R01-meai-chatclient.md.
