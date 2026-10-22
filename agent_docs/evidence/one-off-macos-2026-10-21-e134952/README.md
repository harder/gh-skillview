# One-off macOS ARM64 acceptance — 2026-10-21

Tested commit: `e13495244c73fc4fd6c14d834de03692c5fd29dc`.

Overall **FAIL**: startup query focus, one unit assertion and the original AOT runner restore path failed. All 16 supplemental native proofs and the verified install/update/Undo/cleanup journeys passed. These results do not test the later fixes on this branch.

- [Agent report](AGENT-REPORT.md)
- [Original runner report](run/REPORT.md) and [structured results](run/report.json)
- [Supplemental commands](supplemental/commands.json) and [proof outcomes](supplemental/proof-commands.json)
- [Startup-focus reproduction](startup-repro.startup-focus-probe.txt)
- [Terminal mutation evidence](terminal.evidence.json)

The directory contains the curated reports, logs and text/raw PTY captures. It excludes private fixture homes, state archives, SDKs, binaries and package caches. Absolute paths identify the originating Mac and retained local fixtures; they are not portable fixture inputs. No credentials are included. Captured screen text uses the repository's small ANSI helper and can retain stale cells; use raw transcripts, app logs and filesystem assertions to assess the evidence.

`MANIFEST.sha256` covers every other file in this directory. Raw evidence bytes are preserved; only report placement/relative references and the terminal evidence's reduced source projection were prepared for sharing.
