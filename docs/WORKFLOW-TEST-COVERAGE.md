# Workflow verification scopes

Updated: 2026-10-12. Counts identify the added workflow cases, not the total
number of unit tests in the repository.

| Scope                        | Tracked evidence                                                                                                                                                   | Execution                                                   | Limits                                                                                                                |
| ---------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------ | ----------------------------------------------------------- | --------------------------------------------------------------------------------------------------------------------- |
| Playback lifecycle           | 14 PlaybackSessionLifecycleTests cases, including four output-switch cases and one composed gapless queue-edit case; separate ownership/drain/refresh tests remain | Desktop unit tests                                          | Fake factories, seek, positions, pause, completion and disposal; no hardware or physical audio                        |
| Artist/search/album recovery | 46 Orynivo.Ui.Smoke groups using production adapters, original history and source collisions                                                                       | Windows Debug/Release CI configuration and local verify-all | MainWindow unshown; synthetic SQLite/HTTP and bounded headless layout; no pointer-dispatch or other-platform UI claim |
| Track HTTP routes            | Eight paging/detail/ID/auth/profile cases                                                                                                                          | Server TestServer unit-test project                         | Real route/middleware subset with temporary SQLite; no startup services or TCP listener                               |
| Playlist HTTP routes         | Seven order/duplicate/mutation/profile/smart-resolution cases                                                                                                      | Server TestServer unit-test project                         | Shared definitions and per-request track state; synthetic catalog                                                     |
| Original-file streaming      | Thirteen full/range/error/access/file-write cancellation cases                                                                                                     | Server TestServer unit-test project                         | Synthetic opaque files; no audio decoding, FFmpeg codec or physical network disconnect                                |
| Transcode lifetime           | Five tests with actual managed child processes                                                                                                                     | Server unit-test project                                    | Production runner, parent exit/copy/cleanup; not FFmpeg codecs or descendant-tree validation                          |
| Compact track profiles       | Four profile cases across all five reader methods                                                                                                                  | Core unit-test project                                      | Explicit false/zero and compatibility fallback; not every entity/DTO query                                            |
| Catalog measurements         | Two Release observations of eight readers at 1,000/100,000 tracks                                                                                                  | Opt-in scripts/CatalogBench                                 | Warm synchronous SQLite and per-thread managed allocations; no cold-start/UI/HTTP latency or RSS budget               |

The ordinary three test projects run through local verification and the existing
CI configuration. Windows adds the isolated UI smoke process. CI configuration
is separate from a completed GitHub Actions run; local commits alone do not
prove that a remote run succeeded. The benchmark is outside CI time gates;
methodology and results are in
[CATALOG-MEASUREMENTS.md](CATALOG-MEASUREMENTS.md).

The composed playback cases use the same production lifecycle helpers as the
application. Fake queue inputs are test data, not a second application queue or
navigation model. Gate-driven assertions check readiness separately from output
completion, reject superseded work and drain uniquely owned disposal. Diagnostic
timeouts only prevent stuck tests; they are not audible latency budgets.

Physical devices/receivers, FFmpeg codecs, operating-system network disconnects,
other-platform UI runtime behavior and portable end-to-end latency need separate
opt-in evidence. Fresh-profile first-playback usability remains a separate
follow-up. Keep these boundaries when adding features or reporting verification.
