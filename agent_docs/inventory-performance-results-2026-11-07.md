# Integrated Linux performance results — 2026-11-07

These are source-bound implementation observations, not release or human UX
acceptance. The call-local filtering change was retained after the predeclared
comparison, but **the 50 ms whole-filter target remains unmet**. One candidate
navigation p95 also exceeded 50 ms. No current CI-green verdict follows from
these receipts.

The [performance guide](inventory-performance.md) describes the current method
and capture/lifetime boundaries. Its ARM64 Mac measurements were recorded in
October 2026 with SDK 10.0.113/runtime 10.0.13; the
[October Linux baseline](inventory-performance-linux-baseline.md) also precedes
this integrated product/toolchain. Keep those histories separate.

## Product and method identity

- Baseline A: product tree `81a3dcb913cec39fe25ea17e41e12f6f5488ca9a`,
  NativeAOT App SHA-256
  `63379219565bf2dc4f92652bd7493730a5333b01fd98d6c7649643ba1f2113f7`.
- Candidate B: tree `e9be24b511f4ef0058f0d0dcc40012dcc2b5941f`,
  App SHA-256
  `521dc32fa9cb2f8517893f5b407a018ba9f357dd71dc9621b0d8285123905941`.
  This exact tree was published in `c55fd4bd254cee546fa72e61241a82bede3cbb2e`
  and integrated by merge `38ae9622766ee4479288bc3bba7af8e3e5986236`.
- Both builds: SDK 10.0.403, self-contained runtime/AOT/linker 10.0.14,
  Terminal.Gui 2.5.1-develop.183 and Editor 2.5.8-develop.6; actual build-role
  hashes and locked source manifests are retained. gh 2.103.3 SHA-256 is
  `b768184570b12536be4ac1528893f6340d1c6a7b40b56ad45bca9cd0e87ad34d`.
- Every ABBA cohort used the same reviewed `tools/measure_inventory.py` bytes,
  SHA-256 `9f0364df31cc4a20928f110b2ced9252f9c6e0ef9f36a1fb26e12cde38e49d8d`
  (method tree `5f32bead1a037c594d3b895adee7902bb963e9fd`). The product change
  reuses unchanged row/detail work only within one synchronous `RefreshAll`;
  it does not change input pacing, endpoints, dimensions or targets.

The measured host was Linux x86-64, kernel 7.0.0-43, glibc 2.43, 12 logical
CPUs and Python 3.14.4. No CPU-heavy builds/tests were launched by the measurement
task alongside timing. Shared-host activity, cache temperature and storage
conditions were not controlled.

## Successful CLI observations before the filter change

These seven direct-host samples are from baseline A, **not new measurements of
candidate B**. They remain valid separately even though the original combined
cohort's typical PTY quit was unqualified. Each CLI host had natural exit 0 and
unchanged serialized native inputs. Time spans direct launch through blocking
exit observation, including host serialization and observer scheduling.

| Workload | Process wall ms | Observed inventory |
| --- | --- | --- |
| Typical first process | 1038.663 | 200 copies, 50 explicit roots, nonpartial |
| Four subsequent typical processes | 1033.080, 1042.547, 1059.123, 1039.703 | Same complete 200-copy corpus |
| Large, one process | 1362.826 | 1,000 copies, 50 explicit roots, nonpartial |
| Messy, one process | 202.019 | 12 physical rows, deliberately partial; all twelve story checks observed |

The complete runs had 53 successful provider reports; absent default locations
remain configured observations in addition to the 50 explicit roots. A first
process is not OS-cache cold: generation and hashing already touched the files.
Subsequent samples are fresh processes, not TUI cache hits. Large/messy each have
one sample, not a distribution. No CLI latency comparison is inferred from the
UI-only change.

## Qualified PTY method and bounds

Each PTY-only cohort uses fresh private corpora: typical 200 copies across 50
explicit roots and large 1,000/50, at 160 columns by 48 rows. The large view can
project 2,000 source/copy table entries. There are 30 alternating cached
Installed/Issues transitions, 30 Down selections with exact group/copy detail,
and 30 **whole** Ctrl+A plus nine-ASCII-character filter replacements through
the required query cells and `1 of 1000` footer. No per-key latency is measured.

The original mixed cohort exited 0 but had `typical.pty.cleanExit=false`; it is
not an overall qualified PTY run. Its exact failure cause was unrecorded. The
reviewed correction drains raw PTY output concurrently with an exact-child
blocking exit waiter inside the original shared five-second quit deadline.
Success requires natural direct exit 0, EOF, joined waiter, no error/fallback,
unchanged inputs and the existing metric/cache predicates. A parent exit 0 or
`completed` report alone cannot qualify it. The earlier separately qualified
pre-optimization PTY cohort had cached/navigation/filter p95s
50.552/46.971/145.376 ms; those samples are not pooled with the comparison below.

Bounds remained: 120 seconds per active PTY session, 30 seconds per condition,
32 MiB raw output per session, five seconds for normal quit, ten seconds for
owned fallback wait and ten seconds for waiter join; each comparison controller
had 600 seconds. No next workload starts after an unqualified owner. These are
logical observer bounds, not a hard OS scheduling/disk-I/O guarantee.

## Predeclared A1–B1–B2–A2 comparison

Exactly four sequential cohorts ran, with eight fully qualified direct hosts.
All 360 series samples remain ordered in the retained CSV; no warmup/outlier
removal, pooling or rerun. p95 is nearest rank, the 29th of 30 sorted samples;
median is the mean of the middle two retained observations. Each cell below is
**median / p95 milliseconds**.

| Cohort | Product | Cached tabs | Navigation | Whole filter burst |
| --- | --- | ---: | ---: | ---: |
| A1 | baseline | 44.8560 / 71.097 | 39.5950 / 46.582 | 142.1365 / 146.012 |
| B1 | candidate | 45.8930 / 70.973 | 40.1945 / 70.048 | 100.1065 / 106.513 |
| B2 | candidate | 46.3845 / 70.206 | 39.5310 / 43.693 | 100.7080 / 106.317 |
| A2 | baseline | 46.9190 / 71.413 | 39.3330 / 45.213 | 141.9835 / 144.922 |

The predeclared numerical keep criteria were met: all cohorts qualified with
consistent semantic/cache/input/owner evidence; B1/B2 filter p95s were at least
10% below their paired A1/A2 baseline and both candidate medians were lower;
every cached-tab p95 stayed below 150 ms. Observed paired filter p95 reductions
were **27.051886% and 26.638468%**. This is a repeatable paired observation here,
not proof of a unique cause, a general speedup guarantee or target completion.

**Warnings remain:** B1 navigation p95 rose from 46.582 to 70.048 ms and missed
50 ms; B2 was 43.693 versus A2 45.213 ms. Navigation medians increased about
1.514% and 0.503%, respectively. The B1 tail is retained, not erased by B2;
its cause is unknown. Both candidate whole-filter p95s (106.513 and 106.317 ms)
still miss the unchanged **50 ms whole-burst** target. Navigation was a
preregistered warning, not a post-hoc hidden acceptance rule. The owner retained
the candidate with these limits explicit.

### Shell observations from the same four cohorts

First shell paint is not readiness. Rendered F1-help response establishes causal
input handling and gives a launch-to-response **upper bound** on readiness;
exact independent readiness is unmeasured. Each row is one observation, not a
shell distribution or physical display latency.

| Cohort | Corpus | First paint ms | F1-response upper bound ms |
| --- | --- | ---: | ---: |
| A1 | typical | 59.815 | 118.922 |
| A1 | large | 59.589 | 116.557 |
| B1 | typical | 60.883 | 121.553 |
| B1 | large | 60.457 | 121.193 |
| B2 | typical | 59.927 | 119.204 |
| B2 | large | 60.993 | 120.937 |
| A2 | typical | 59.621 | 120.411 |
| A2 | large | 59.830 | 118.237 |

## What is and is not qualified

All eight comparison owners had natural exit 0, EOF, joined waiters and no
fallback. Before/after input snapshots matched byte-for-byte, including native
addresses within each fresh fixture. Relative namespace/kind/mode/content
hashes matched across cohorts, while their native addresses intentionally differ.
Captures remained one before/after; the large local sequence's retained prestart
exec-record counts were unchanged. These are log observations/lower bounds,
not complete child/HTTP counts, principal attribution or mutation authority.

Child HOME, state, CWD, Git and client configuration were private, with a local
gh adapter and closed proxies. Only local version/help/list commands were
requested; auth status is an explicit unauthenticated fixture stub. This is not
an OS network sandbox, HTTP-zero proof or trace of escaped descendants. Timings
end at decoded required cells and include scheduler/observer overhead; they do
not prove physical display presentation or complete styled-frame emission.

The source validation retained one **full-unit-suite failure**: 3,303 total,
3,269 pass, 33 skip, one unchanged native-placement churn test failure. Its
selected hook indices were `[0,2,3]` rather than `[0,1,2,3]`; an IOException
refusal occurred. Whole-root retry accounting can consume an attempt before
that selected hook, but the actual ancestor/writer/cause was not recorded.
Both original theory cases later passed in one separately authorized isolated
diagnostic on the same built bits; both also passed in the earlier unchanged
UI-integration run. None of those passes replaces the full-suite red or proves
the proposed cause. No native fix/assertion relaxation was made. All 1,147
current UI tests (including eleven new reuse cases), integration 14/14,
zero-warning build, style and four Linux built-host UI smokes passed separately.
These observations make no current CI-green, Windows/macOS native, human UX or
release-acceptance claim.

## Remaining cost diagnosis: sampling denied

After integration, the installed `perf` 7.0.14 advertised PID targeting and
DWARF unwinding. One bounded diagnostic attached only to the freshly owned
1,000-copy App PID, with no global process scan or changed product flags.
It exited **255** before enable acknowledgement: it reported restricted access
and `perf_event_paranoid=4`, then failure to open any recording events. It also
printed a kernel build-id mmap support warning; that warning was not modified
or established as the reason for sampling failure.

No usable CPU sample set was produced (the data file was empty), and none of
the 30 planned profiled filter replacements ran. This is **unavailable evidence,
not zero CPU cost or a hotspot result**. The original 30-Down prelude ran without
latency statistics. The App then exited naturally 0 with EOF and waiter joined;
inputs were unchanged. No permission change, alternative profiler, installation,
retry or extra latency cohort followed. Direct App/perf parents settled;
internal descendants were not independently traced.

Static source still shows synchronous refresh for text changes, plus remaining
layout/footer/facet work and guard-validation cost. Component costs, actual
paint/callback counts and a unique remaining bottleneck are unmeasured. There
is no evidence-backed next product change here. If the 50 ms target remains
required, a separately authorized diagnostic on a profiling-capable environment
would be the next evidence step; do not weaken native/draw/input/lifetime
contracts or introduce batching/debounce/Width changes to make the target pass.

## Retained provenance

These private implementation receipts are identified by name and SHA-256 for
custodian verification; the table does not promise public downloads. Raw
profiles/fixtures, client homes and payloads are not published with this guide.
The full source/binary/build roles, commands, owner receipts, before/after native
snapshots and ordered samples remain retained. Old cohorts and first failures
are never overwritten or relabelled.

| Evidence | Retained artifact | SHA-256 |
| --- | --- | --- |
| Pre-optimization CLI and original mixed cohort | `performance-baseline/cohort/report.json` | `1826ed3caa629ecca14fe93a5b9e5c27fc77b950b7da0647512f368e1f4eb7ba` |
| Separate qualified pre-optimization PTY cohort | `performance-pty-qualified-20261107/cohort/report.json` | `3d3987c2326e6e602fe3d1237066aa139b99096c0cd3ff038bcfd848c18bd85d` |
| Predeclared ABBA plan | `filter-comparison-20261107/PREDECLARED-PLAN.json` | `6f08722cdc41a824e6d9e43ebd6a1a1b4606271e62bf345079503c66e9a3e3d5` |
| ABBA comparison results | `filter-comparison-20261107/COMPARISON-RESULT.json` | `c31ee39d2b0c1bfc1c8d57d240244a7098d64db34ac9945497da584629f16f15` |
| ABBA ordered samples | `filter-comparison-20261107/ORDERED-SAMPLES.csv` | `cfd7bf8c6b55c18ce954f5bf24f06d83db9d2afe00095f442da57a6414ecd439` |
| ABBA evidence seal | `filter-comparison-20261107/SEAL.json` | `db7524e42a5f035708e279569a1531236fdbbee68a5d706d461334c3b10f0547` |
| Original full-unit XML (red) | `filter-reuse/xml/filter-unit-full.xml` | `94a8e5aa4a6cc682b5cd2f330fa3d33264faca96c43ba63dbc4d034793d7ee10` |
| Isolated native diagnostic XML | `filter-reuse/xml/native-churn-isolated-diagnostic.xml` | `01413d0d7357fef0b241d446e336905d41b9438a4e3781a6e6f9d19519634f21` |
| Postmerge diagnostic result | `filter-postmerge-assessment-20261107/profile-1/DIAGNOSTIC-RESULT.json` | `5630921daa28646655cd9d53af4df9571daba334d57b75d2d8cd515a94ae217a` |
| Profiler stderr | `filter-postmerge-assessment-20261107/profile-1/perf.stderr` | `8434dfd711e1506acce81f9061c4e6922bbaaa59f10c796502670bd6fce6e576` |
