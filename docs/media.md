# Terminal screenshots and walkthroughs

The current PNGs, GIFs and MP4 walkthroughs show the simpler browse layout and
warm terminal palette from the source build at `3d59a98b9ac404dd0f6e79b86b53e15ba944977f`.
This is a source-build preview, not a recapture of the published 1.1.0 binaries.
The README and [website](https://skillview.dev) use the same media masters.

All screens are actual Terminal.Gui output from a 120-column × 36-row PTY,
rendered at 1320 × 864 pixels. The capture child advertises full RGB color and
does not inherit `NO_COLOR`. Headings keep bold gold text and separator rules;
the selected row uses an accent fill. Narrow terminals use full-width details,
and wider terminals put details beside the list.

## Walkthroughs

These silent walkthroughs use settled screens with deliberate reading pauses.
They are edited demonstrations of navigation, not continuous recordings or
measurements of loading time. MP4s have playback controls on the website;
GIF alternatives are linked here. Nothing is installed or deleted.

| Walkthrough | Steps shown | GIF | Video |
| --- | --- | --- | --- |
| Discover and preview | Enter a sample query, select a result, read its preview, inspect install placement, then cancel. | [Discover](../assets/animations/g1-discover.gif) | [MP4](../assets/animations/g1-discover.mp4) |
| Browse Installed | Browse copies, filter to a package, run its static check, then return to the list. | [Installed](../assets/animations/g2-installed.gif) | [MP4](../assets/animations/g2-installed.mp4) |
| Review findings | Inspect Issues, open cleanup review, and return without applying a change. | [Issues and cleanup](../assets/animations/g3-changes-cleanup.gif) | [MP4](../assets/animations/g3-changes-cleanup.mp4) |
| Doctor and Help | Inspect environment diagnostics, return to browsing, and open contextual Help. | [Doctor and Help](../assets/animations/g4-doctor-help.gif) | [MP4](../assets/animations/g4-doctor-help.mp4) |
| Review removal | Select a sample copy, inspect its removal review, and cancel. | [Removal review](../assets/animations/g6-remove.gif) | [MP4](../assets/animations/g6-remove.mp4) |

## Sample data and capture limits

The credential-free fixture contains independent same-name copies, an intentional
project variation, a shared payload, a broken link, an empty directory, malformed
metadata, conflicting authored/lock evidence, an unmatched registry record, a
managed read-only copy and a missing Markdown resource. Incomplete coverage stays
visible. These files are disposable examples, not the maintainer’s skills.
Discover uses declared sample responses for the `terraform` query and preview.
Doctor reports the fixture’s lack of authentication. No credentials are copied,
no AI client or skill code runs, and install/removal dialogs are cancelled.

The renderer reads captured VT cells and RGB attributes with DejaVu Sans Mono.
It does not rewrite terminal text or reconstruct the cursor and every terminal
text style. The [capture manifest](../assets/screenshots/capture-provenance.txt)
binds source and binaries, fixture preservation, submitted input, raw output,
settled frame offsets and published media hashes. Historical text receipts in
the screenshots directory remain historical evidence.

## Refreshing the media

Build the pinned checkout and follow [PTY testing](../agent_docs/tui-pty-testing.md)
for private child HOME/state/CWD, neutralized Git configuration and a refusing
offline adapter. Set the actual PTY dimensions, `TERM=xterm-256color` and
`COLORTERM=truecolor`; remove `NO_COLOR` only from that capture child’s environment.
Answer startup probes and wait for complete, settled controls. Use the focused
query field and the actual results-table coordinates; metadata can repeat the
same skill name elsewhere on the screen.

Navigate Discover, Installed, Issues, Locations, package checks, comparison,
History, Doctor and Help. Cancel review dialogs. Retain original failed navigation
attempts, raw ANSI, input traces, rendered frames and before/after fixture identities.
Do not reuse an older screenshot without identifying its source revision.

Keep masters under `assets/screenshots/` and `assets/animations/`.
`python3 site/build.py` copies them to the ignored `dist/skillview-site/media/`
folder. Keep image dimensions, alt text and captions accurate. Verify desktop and
phone layouts, local media references, video playback and reduced-motion behavior.
