# Headless workflow smoke instructions

This project supplements the repository root instructions. It is a separate
process so Avalonia, static database paths and transport substitutions cannot
affect the pure desktop unit tests.

- Seed a unique temporary data root and legacy-migration marker before creating
  the application or MainWindow. Never use a personal catalog or credentials.
- Never show MainWindow, invoke its startup handlers, start playback or
  enumerate output devices. An isolated headless control host may perform
  bounded layout.
- Use fresh cached metadata and synthetic HTTP. Block external profile/image
  transport before application creation and assert that no such requests occur.
- Use valid synthetic inputs. Exercise production adapters and their existing
  history/ownership; do not introduce a second navigation model for tests.
- Every asynchronous gate must be deterministic. Completion timeouts are failure
  diagnostics, not performance thresholds. Pump the dispatcher on its owning
  thread and release gates/hosts/transport during teardown.
- Keep diagnostics fixed and credential-free. Do not print headers, URLs, real
  paths, catalog metadata or environment values.
- The initial runtime contract is Windows-only. Add evidence and CI execution
  before claiming other-platform support. Pure unit tests remain cross-platform.
- Run the project in Debug and Release through `scripts/verify-all.ps1` after
  changes. Windows CI must continue invoking it in both configurations.
- Match the desktop Avalonia version for Headless dependencies. Document all
  fixture types/members in English and maintain root documentation/checklists.
