# First-playback acceptance

Updated: 2026-10-12.

Implementation and automated checks are complete for the planned setup changes.
Phase 5 remains open until human local and server-only audible playback is
confirmed. A headless host cannot establish physical sound, native OS input,
monitor rendering or assistive-technology behavior.

| Check                           | Evidence                                                                                                                    | State                         |
| ------------------------------- | --------------------------------------------------------------------------------------------------------------------------- | ----------------------------- |
| Source/output setup             | Existing embedded Settings routes, first-local-folder reveal, server-only routing                                           | Checked                       |
| Empty/unavailable catalogs      | Complete source-backed empty views; separate failures; current request/version/generation guards; held and watcher recovery | Checked                       |
| Output creation                 | Empty profile list permits Create output and disables selection-only actions                                                | Checked                       |
| Transport availability          | Fresh/idle no-player Play disabled; remembered-source resume retained                                                       | Checked                       |
| Keyboard/names/focus            | Headless Tab, Shift+Tab, Enter; localized automation names; themed focus marker                                             | Checked                       |
| Languages/themes/layout         | Seven locales, both themes, narrow/wide bounded controls, simulated 100/200% rendering scale                                | Checked                       |
| Motion/contrast                 | No setup row/focus transitions; opaque theme text ratio at least 4.5 and focus border/fill ratio at least 3                 | Checked                       |
| Visual inspection               | Local synthetic PNGs; representative German, Hindi, Russian and Chinese narrow views                                        | Checked within headless scope |
| Real local first playback       | Human confirmation through normal UI                                                                                        | Pending                       |
| Real server-only first playback | Human confirmation through normal UI                                                                                        | Pending                       |

The tracked workflow scope is in
[WORKFLOW-TEST-COVERAGE.md](WORKFLOW-TEST-COVERAGE.md). Simulated scaling does
not claim a physical display/DPI run. Contrast assertions are not accessibility
certification. UI checks create no player and do not scan media. The production
default-output migration remains unchanged; the smoke process pre-seeds a
synthetic output so it cannot discover the real default device.

## Fresh-start migration regression

A reported startup failure exposed a race between checking a missing legacy
artwork destination and copying it. Migration now preserves files created in
that interval and propagates other I/O failures without a completion marker.
Explicit ORYNIVO_DATA_DIR profiles do not import legacy data, even without the
marker prepared by the helper. Renaming the default Orynivo folder alone can
still intentionally migrate the old Player configuration and caches again. Use
the isolated check below for a clean test without moving personal data. Local
and server-only audible acceptance remain pending after this fix.

## Isolated human check

PowerShell 7 on Windows can prepare a separate profile and a quiet three-second
stereo PCM WAV without launching the app:

```powershell
pwsh -NoProfile -File scripts/prepare-first-playback-check.ps1 -Scenario Local
```

The command returns ProfileRoot and MusicFolder. It creates a legacy-migration
marker, empty source configuration and disabled startup updates, backups, MCP
and mobile remote. It neither scans nor starts playback. No personal settings or
media are copied. Temporary profiles are retained for inspection.

To prepare another fresh profile and explicitly open the normal Release UI:

```powershell
pwsh -NoProfile -File scripts/prepare-first-playback-check.ps1 -Scenario Local -Start
pwsh -NoProfile -File scripts/prepare-first-playback-check.ps1 -Scenario Server -Start
```

Build the Windows Release app first; ApplicationPath can select another local
build. The child alone receives ORYNIVO_DATA_DIR. The launch path has not been
claimed as an automated native UI or sound test.

For Local, use Directories to add the returned MusicFolder, select its Scan
action, wait for completion, save Settings, then double-click the test track in
Tracks. Verify audible output and normal Play/Pause/Stop behavior. Output device
opens the normal output settings if the default endpoint is unavailable.

For Server, configure the server in Orynivo servers, save Settings and play a
known track through Tracks with no local folder configured. Enter credentials
only in the app. Check unavailable-source retry and output selection there.
Record pass/failure and the failing step, without credentials, private URLs or
personal media metadata.

## Optional local rendering

```powershell
$env:ORYNIVO_SMOKE_RENDER_DIR = Join-Path $PWD '.codex/setup-render'
dotnet run --project Orynivo.Ui.Smoke/Orynivo.Ui.Smoke.csproj
Remove-Item Env:ORYNIVO_SMOKE_RENDER_DIR
```

This exports only synthetic setup controls, named by locale/theme/width/scale.
Ordinary CI does not export frames. Keep the output directory excluded locally
and uncommitted. This session produced 112 frames and inspected representative
ones, fixing low hint contrast and the duplicate focus rectangle it revealed.
