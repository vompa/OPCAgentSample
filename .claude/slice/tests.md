# Rules for new tests

Read before writing a test. A repository's own rule on tests (CLAUDE.md, work/conventions.md) wins.

## Which test, and how many
- Test on the cheapest level that proves the behaviour.
  - Logic you own gets a fast test without database, network, files or real time.
  - A test through the database or HTTP only when that path is the behaviour: a permission, a trigger, a query, a contract.
  - A test through the screen only for what the user sees or does and no faster test covers.
- Do not repeat a behaviour on a slower level. Do not test the framework or a third-party library.
- Do not write a test for coverage. Every new test names the fault it would catch; a test that cannot is not written.

## Writing a test
- One behaviour per test: several asserts are fine, a second act is not.
- The name is a short sentence, situation then result, in the language the repository uses for test names, at most about 80 characters. A name that needs "and then" is two tests.
- No `if` or loops in a test; parameterise it instead. A scan over all screens or files may loop.
- Time comes from an injected clock, never from the system clock or a real wait.
- Check what can be observed, not call counts or private state, unless the call is the behaviour.
- Give a test only the data its behaviour needs, built by a helper instead of shared setup.
