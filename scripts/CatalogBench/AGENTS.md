# Synthetic catalog measurement instructions

Follow the repository root checklist. This opt-in executable supplements tests;
it must never turn machine-specific timings into automatic CI failures.

- Use only explicit, uniquely owned temporary SQLite paths. Never use
  OpenDefault, a personal catalog, environment data-directory mutation or real
  media files.
- Seed the production schema deterministically in a transaction. Validate every
  measured result count and keep generation outside measurement samples.
- Record every elapsed/allocation sample, one warmup per reader, runtime/OS and
  logical processor count. Current-thread managed allocations are not RSS or
  native SQLite memory. Warm after-seeding runs are not cold-cache measurements.
- Reports contain only fixed phases, counts and timings, never paths, machine
  names, catalog metadata, URLs, credentials or environment values.
- Clear only the owned database pools. Check the resolved cleanup directory
  against the temporary parent and fixed fixture prefix before recursive
  removal.
- Keep this outside normal CI timing gates. Run Release twice for an
  observation; document variation and limits before proposing budgets or
  optimizations.
- Benchmark changes must build in Debug/Release and maintain the reference and
  measurement document. Preserve English XML documentation and root Markdown
  formatting/lint requirements.
