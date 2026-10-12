# Synthetic catalog measurements

## Method

Run this command from the repository root in Release, twice:

```powershell
dotnet run --project scripts/CatalogBench/CatalogBench.csproj -c Release -- artifacts/catalog-benchmark.json
```

The tool creates 1,000 and 100,000 synthetic tracks, ten tracks per album and
ten albums per artist. Metadata includes title, artist, album, genre, format,
duration, bitrate and a ten-percent favorite population with profile state. The
production schema is initialized normally; deterministic bulk SQL seeding is
excluded from the samples. This measures reading metadata, not scanner speed.
Every reader warms once, then runs seven times. All row/summary counts are
validated. JSON keeps every sample plus min/median/max and median current-thread
managed allocations. Reports omit paths, machine names and catalog metadata.
Owned temporary databases/pools are cleaned after the run.

The data and OS cache are already warm after generation. These are neither
cold-start nor UI/HTTP/audio measurements. Managed allocations are cumulative
per-call allocation traffic, not live heap, RSS, native SQLite buffers or a peak
memory figure. GC and scheduling can affect elapsed time; seven samples are not
a percentile or service-level guarantee.

## 2026-10-12 observation

Baseline Core revision: `ac20048`, with the new harness. Two independent Release
processes on .NET 10.0.12, Windows build 10.0.26300, x64, 32 logical processors.
Numbers below show each run's median milliseconds and the second run's median
managed allocation in MiB (1,048,576 bytes). Raw local JSON remains uncommitted.

| Reader               | 1,000: run 1 / run 2 ms | 100,000: run 1 / run 2 ms | 100,000 allocated MiB |
| -------------------- | ----------------------: | ------------------------: | --------------------: |
| First 500-track page |             3.04 / 3.01 |             19.94 / 19.62 |                  0.39 |
| Last 500-track page  |             3.57 / 3.44 |           140.04 / 137.18 |                  0.39 |
| Resolve 500 IDs      |             3.84 / 3.77 |               3.26 / 3.11 |                  0.64 |
| All compact tracks   |             6.02 / 5.77 |           552.54 / 543.60 |                 77.54 |
| All track facets     |             2.64 / 2.44 |           239.02 / 236.17 |                 81.35 |
| All albums           |             0.17 / 0.16 |             13.18 / 13.27 |                  2.08 |
| All artists          |             0.03 / 0.03 |               1.66 / 1.58 |                  0.15 |
| Dashboard summary    |             0.08 / 0.08 |             22.27 / 22.26 |            below 0.01 |

The largest observed 100,000-track samples across both runs were 20.50 ms for
the first page, 141.08 ms for the last page, 591.94 ms for all compact tracks,
and 254.93 ms for all facets. These are observations on one machine only.

## Decisions and remaining evidence

- Retain bounded page/ID reads for bounded screens and requests. At 500 rows,
  page allocation stayed about 0.39 MiB at both catalog sizes. Fetching the
  complete catalog for the same screen would allocate about 77.54 MiB here.
- Keep whole-catalog track/facet work off the UI thread and avoid unnecessary
  repetition. Both allocate proportionally to catalog size in this fixture.
- Investigate deep-page ordering/offset cost before changing the paging API. The
  last page was about seven times slower than the first at 100,000 tracks. An
  execution-plan/index or cursor comparison needs its own measured evidence.
- Retain aggregate dashboard counting instead of materializing track DTOs. It
  allocated below 0.01 MiB while returning validated counts.
- Use row bounds and allocation scaling as the current structural budgets. Do
  not add absolute CI latency caps from this single workstation. Cold-cache,
  lower-capacity machine, other-platform and end-to-end UI/server measurements
  remain required before proposing portable latency budgets.

This completes a repeatable database baseline. It does not close the broader
workflow/performance roadmap or establish physical playback behavior.
