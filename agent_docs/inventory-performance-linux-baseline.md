# Linux inventory baseline: corrected CLI and historical PTY cohorts

This is an offline implementation measurement on the reviewed UX1 product tree, before the auth/startup changes. It is not human UX acceptance, release certification, or an optimization mandate. The CLI cohort below was remeasured on 2026-10-27 UTC after review exposed a quantized parent-process endpoint. The independently bound 2026-10-26 PTY cohort is retained unchanged; no new terminal measurements are implied.

- Product HEAD: `3ae4c5fe1a29185b913797716bd8d294422f2b19`; tooling/docs were uncommitted and unstaged.
- Corrected CLI UTC start: `2026-10-27T00:34:48.086836+00:00`. Historical PTY UTC start: `2026-10-26T21:38:08.902840+00:00`.
- Host: `Linux-7.0.0-38-generic-x86_64-with-glibc2.43`; `x86_64`; 12 logical CPUs; Python `3.14.4 (main, Aug 20 2026, 10:41:58) [GCC 15.2.0]`.
- Pinned build: SDK 10.0.113, self-contained runtime 10.0.13, Release managed apphost. Native AOT and other OS/architectures were not measured.
- gh: `gh version 2.103.2 (2026-10-23)`; real local list/help/version, explicit unauthenticated auth-status stub. No credentials, network commands, installs, updates, removals, AI clients or skill execution.
- Terminal: 160 columns × 48 rows. Private HOME/config/state/CWD/plugin homes and TMPDIR under a freshly created `/var/tmp` fixture. No builds/tests were run by this task during this cohort; shared-host load, OS caches and storage temperature were not controlled.

## Corrected CLI binary and tool identity

| File | SHA-256 |
| --- | --- |
| `skillview` | `aad9cffe5319927d6ab569b88d3c08ca88e56a8d46a927724527af4a9368bcf0` |
| `skillview.dll` | `054507837033935df9b5c4993767e0269c3afe0d70710828e7242ba930ae8298` |
| `SkillView.Core.dll` | `c514a9fad406a9f818e88138ef280dc7253da1d2728fd5d45805ca41e31beb70` |
| gh | `cbfdc94f3a5c0713c0d6df3e0a1d88a59a76b0c52332aad7d21d112f9da2cc0e` |
| `tools/inventory_fixture.py` | `bc444bc9bb261e0dcc0e6a563f521e86640d423cda5967577b9794f57a58a1c2` |
| `tools/measure_inventory.py` | `ad0ed018ca63a5f84610f89c84fd3d656a7bcc1ed1f49c37d8fe0930480daf44` |
| `tools/test_once.py` | `2257a1403d6bc4cf42dc43536b4d7039efd35c5746b2c2cf6cb05a46641cd005` |

All 221 adjacent runtime/assembly files are hashed in the full receipt. The SHA-256 of its `binaryFiles` map, encoded as UTF-8 JSON with sorted keys and separators `,`/`:` and no trailing newline, is
`b4e4c54fbdfcbb80b2099788768c7b44453ff1bf6ed15890768396d795cb0203`. All binary hashes stayed unchanged during measurement.

## Timing correction and superseded observations

The reviewed schema-1 **Wall ms** values (1366.270, 1366.223, 1366.253, 1366.410, 1366.432) measured a Python wrapper through parent `Popen.wait(timeout)` polling. They are superseded as process wall measurements and must not be used in before/after performance claims. They remain unchanged in the original receipt and the `superseded-review-1` handoff archive. The product phase logs were not the cause.

The local Python 3.14.4 POSIX implementation sleeps 1, 2, 4, 8, 16 and 32 ms, then at most 50 ms between nonblocking `waitpid` checks. Nominal checks therefore include 1363 ms and 1413 ms after launch, plus actual polling overhead. A fresh diagnostic traces those real sleeps and independently timestamps the wrapper's blocking wait on the real host:

| Traced observation | Wrapper endpoint ms | Direct host blocking-wait ms | Parent observation lag ms | Nominal requested-sleep sum ms |
| --- | ---: | ---: | ---: | ---: |
| 1 | 1416.540127 | 1349.348596 | 48.389136 | 1413.000 |
| 2 | 1366.727004 | 1309.764508 | 35.224039 | 1363.000 |
| 3 | 1366.864223 | 1325.214653 | 20.283029 | 1363.000 |

Observations 2 and 3 have different host durations but land in the same parent polling interval. This demonstrates quantization, not exceptional measurement precision. The trace records actual sleep start/end counters, direct host PID/exit counters, argv, environment and product phase logs. Instrumentation adds some overhead; it is diagnostic evidence of the mechanism, not a replacement baseline.

The corrected schema-2 harness launches the host directly and timestamps blocking `waitpid` return on an observer thread. An event supplies the deadline without successful timed polling. Output-file creation, parent notification and cleanup fall outside the endpoint. Launch, host work/serialization and observer-thread scheduling remain included. Integer `perf_counter_ns` endpoints and the clock implementation/resolution are retained, so elapsed time can be independently recomputed; a 1 ns clock resolution is not a claim of 1 ns accuracy. Forced timeout exits receive no process-wall measurement.

## Corrected typical CLI inventory

Workload: 200 uniquely named physical copies across 50 explicit scanned roots, round-robin, without locks or managed regions. The normal resolver also retained absent default locations (the TUI reported 50 scanned / 129 configured roots). All CLI provider observations succeeded, `partial` was false, and every sample returned exactly 200 rows.

| Process | Direct process wall ms | Inventory pipeline ms | Logged children | gh list children |
| --- | ---: | ---: | ---: | ---: |
| first-process 1 | 1341.251 | 1161 | 54 | 51 |
| warm-process 2 | 1328.938 | 1148 | 54 | 51 |
| warm-process 3 | 1314.730 | 1137 | 54 | 51 |
| warm-process 4 | 1326.208 | 1148 | 54 | 51 |
| warm-process 5 | 1325.774 | 1144 | 54 | 51 |

First-process means a new process on newly generated files. Warm-process means subsequent fresh processes on those same files. Neither is OS-cache cold or an in-process cache lookup. The corrected endpoint spans immediately before direct host launch to exit observed by blocking wait; pipeline time is the product's own milestone. The difference from the old wrapper column is a measurement correction, not a product performance improvement.

Raw monotonic endpoints for the five typical observations (nanoseconds):

```csv
sample,startedNs,popenReturnedNs,exitObservedNs,parentResumedNs
1,423129661898353,423129662250130,423131003149452,423131003206760
2,423131008632477,423131009006546,423132337570002,423132337642459
3,423132344230738,423132344590510,423133658960511,423133659029822
4,423133664293382,423133664653845,423134990501647,423134990559516
5,423134995672381,423134996068101,423136321446784,423136321500125
```

Compute each `processWallMs` as `(exitObservedNs - startedNs) / 1e6`, displayed to three decimal places. Each raw `.timing.json` carries these endpoints plus PID, exact argv, UTC start, exit status and clock metadata. Parent-notification latency is exposed separately rather than folded into the endpoint.

| Other corrected CLI cohort | Observations | Direct process wall ms | Interpretation |
| --- | ---: | ---: | --- |
| messy | 1 | 378.294 | Single observation; no statistical distribution estimated |
| large | 1 | 1843.861 | Single observation; no statistical distribution estimated |

The messy capture remained deliberately partial and passed all twelve scenario assertions. The large capture returned exactly 1,000 rows with complete successful providers. These single samples do not establish latency distributions or repeatability.

First typical-process phase observations (milliseconds):

```text
managed discovery: 43ms
payload metadata scanned: 95ms cumulative
local discovery and default catalog: 541ms (total 541ms)
root catalogs: 549ms (total 1090ms)
physical reconciliation: 30ms (total 1120ms)
package matching: 2ms (total 1122ms)
managed ownership: 5ms (total 1127ms)
native observations: 31ms (total 1158ms)
cleanup preferences: 3ms (total 1161ms)
```

## Historical terminal observations, 2026-10-26

These values and ordered samples are unchanged from the original PTY receipt (schema 1, SHA-256 `53efb028fbdbadb8745231c6b5598b13d083c23ccdf7ff047ae58e629fb308c2`). Its original observer was `tools/measure_inventory.py` SHA-256 `e8ed79bd45c49f06526d88171c637b725b2333869aa5c25cab36dc019e8698bf`; its complete tool/environment/workload/binary identities remain in that receipt. The CLI timing fix did not rerun or rebind this terminal cohort.

The reported p95 includes the first returns after initial loading. Ordered samples show warm-up decay, so early samples dominate the inclusive p95; this is not a warmed steady-state distribution. Preserve that endpoint in comparisons rather than silently excluding slow samples.

| Observation | Samples | Result ms |
| --- | ---: | ---: |
| First shell paint, 200 copies | 1 | 619.357 |
| Launch through rendered F1 response, 200 copies | 1 | 747.109 |
| First shell paint, 1,000 copies | 1 | 625.439 |
| Launch through rendered F1 response, 1,000 copies | 1 | 759.030 |
| Cached Installed ↔ Issues transitions, p95 | 30 | 195.146 |
| Down navigation at 1,000 copies, p95 | 30 | 211.606 |
| Replace filter at 1,000 copies, p95 | 30 | 208.708 |

Tab capture count was 1 before and 1 after all 30 transitions. The 1,000-copy local sequence also kept one capture and 55 logged children before/after. Both PTYs exited with code 0. Required redraw cells were observed for every sample. These endpoints include Python cell decoding and event overhead; physical display completion and all draw-handler filesystem I/O are unmeasured. The 1,000 physical copies project up to 2,000 source/copy table entries.

The typical inventory observations are below the proposed 3-second warm target. Cached-tab p95 exceeds the proposed 150-ms target in this recorded cohort; local navigation/filter observations also exceed the proposed 50-ms target. These are observed endpoints including harness overhead, not isolated product CPU costs. The precise 500-ms input-ready-shell target is **unmeasured**: first paint is not readiness, and a later F1 response is only an upper bound. These facts authorize no product rewrite.

Raw terminal samples in input order (milliseconds; nearest-rank p95 is sorted sample 29 of 30):

cached tabs:

```json
[180.523, 195.146, 83.318, 212.555, 144.264, 91.337, 66.069, 57.622, 59.135, 57.997, 60.302, 96.13, 61.91, 49.275, 67.679, 50.803, 70.156, 48.091, 71.684, 48.809, 74.446, 44.88, 29.741, 50.343, 32.634, 45.336, 30.567, 51.556, 27.784, 48.917]
```

navigation:

```json
[145.06, 196.973, 211.606, 201.729, 220.672, 165.359, 71.999, 35.918, 48.699, 61.722, 49.274, 35.784, 45.14, 34.985, 44.902, 41.821, 38.508, 36.583, 42.67, 36.979, 45.35, 35.192, 42.992, 36.488, 44.283, 70.149, 47.077, 70.893, 48.697, 68.012]
```

filter replacement:

```json
[226.381, 208.708, 189.034, 184.729, 157.555, 183.36, 164.029, 163.553, 173.443, 161.782, 139.045, 132.345, 137.67, 131.526, 126.875, 122.433, 130.661, 126.249, 137.492, 126.758, 129.652, 128.02, 125.07, 128.31, 131.062, 122.684, 128.161, 122.181, 129.042, 127.171]
```

## Messy story and isolation

All twelve scenario checks passed against actual CLI JSON, including a real inaccessible-path observation under this non-root identity. Shared payload exposures retained User and Project scopes; duplicates stayed separate; project override copies retained their scopes; contradictory authored/owner refs and the lock-only pin were retained. The pin is owner evidence, not invented authored tracking. Broken/empty/invalid rows, managed read-only ownership and the absent-payload owner were observed. The Markdown resource remained absent; this is fixture evidence, not a claim that inventory validates resource links.

The intentionally partial messy capture is excluded from cached-tab measurements. Its 12 returned physical rows and all retained owner records are available in `messy-00.json`. Every measured workload retained unchanged fixture input native identities, bytes, topology and modes; the deliberately inaccessible subtree was not traversed by the manifest observer. This does not prove identities or contents inside that denied subtree.

## Receipt and reproduction

Corrected CLI receipt: `/var/tmp/skillview-perf-verification-3J16bgtL/cli-baseline-required-fix/report.json`, SHA-256 `8433c56b233f7f88ed4775538b0c6cc91df7f23d1c5b4e3f6cc161c156c845b0`; also copied as `linux-baseline.json` in the handoff bundle. It records all binary hashes, tool hashes, workload/environment manifests, native before/after input observations and per-sample stdout/stderr/timing paths plus SHA-256 hashes.

Historical PTY and superseded wrapper-wall receipt: `/var/tmp/skillview-perf-verification-3J16bgtL/linux-baseline-record/report.json`, SHA-256 `53efb028fbdbadb8745231c6b5598b13d083c23ccdf7ff047ae58e629fb308c2`. Its unchanged handoff copy is `superseded-review-1/linux-baseline.json`. Old wall numbers are superseded; the separately labelled historical PTY data remains valid at its stated endpoint/limits.

Quantization witness: `/var/tmp/skillview-perf-verification-3J16bgtL/wait-quantization-audit/report.json`, SHA-256 `ac7272e0eaa1ff56d27c31443a6e70f52dd6cc2483b843efc1333374eded3ac9`; handoff copy `wait-quantization-audit.json`. The sibling `python-popen-wait.txt` retains the inspected local implementation, and `diagnose_cli_timing.py` in the handoff bundle retains the exact experiment script. Its fixed output location was fresh for that run; choose a new private output path before repeating the trace.

Rebuild the pinned product tree and use [CONTRIBUTING](../CONTRIBUTING.md#offline-inventory-measurements) with `--expect-head 3ae4c5fe1a29185b913797716bd8d294422f2b19 --samples 5 --cli-only` to repeat this corrected CLI cohort. Omit `--cli-only` for a new, separately identified PTY cohort. Match the observer version, workload, adapter and endpoint before comparing later auth/startup candidates. Retain new HEAD, complete binary identities and ordered raw observations. No live authenticated cohort is interchangeable with these offline auth-stub measurements.
