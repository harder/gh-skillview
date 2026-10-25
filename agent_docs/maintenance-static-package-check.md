# Bounded static package checks

`check --path PATH` and `check --install-id ID` select exactly one local inventory
installation. Paths may identify the physical payload or a captured agent exposure.
The CLI requests an offline inventory with no gh host. The Installed-tab `K`
action uses the workflow coordinator to recapture the selected installation with
the same workspace/profile roots, then opens an owned asynchronous read-only
dialog. Filter typing retains printable `K`.

JSON schema 2 adds independent `assessments.format`, `agentCompatibility`,
`payloadIntact` and `sourceReviewed`, plus bounded literal `observations` and
`declarationCoverage`. Legacy schema-1 fields keep their scoped meaning, including
the two old `not-assessed` strings; no old saved-plan or payload fingerprint domain
changes. `rootMetadataSha256` is a separate raw-file hash, used only as root-rule
evidence. Source integrity stays unknown without a trusted or explicitly declared
expected baseline; complete capture and matching references do not supply one.
`payloadIntact.observedCondition` separately reports local defects, partial coverage,
or no supported reference defects observed. Known missing/mismatched required
files/references retain exact failing findings and `local-defects-observed`, while
source integrity remains unknown. Working dependencies and agent behavior remain
unobserved. Source remains
`not-reviewed`; merely displaying a report never acknowledges source changes.
Typed findings retain source paths, available line numbers, targets and repair
guidance. `referenceScope` and `declarationScope` name the limited grammars. Complete
coverage with no format/reference errors or known selected-agent incompatibility
exits 0; findings or partial coverage exit 1. Unknown compatibility/availability
and unreviewed source are independent assessments, not inferred passes.
Invalid options exit 2, unavailable environment
errors exit 10, and a selector that does not match exactly one installation exits
20. Human output is terminal-sanitized; JSON escapes the original evidence.

The limited parser excludes multiline inline-code/link-label paragraphs until
a blank line, and excludes list-contained fenced code from both reference and
definition collection. These unsupported contexts report partial coverage.
Raw `script`, `style`, `pre` and `textarea` blocks remain excluded through a
CommonMark type-1 closing tag, including intervening blank lines. Unescaped
ampersands in Markdown destinations (including HTML character references) are
unsupported and report partial coverage; a backslash-escaped ampersand stays
literal. The checker does not attempt full Markdown/HTML entity decoding.
Reference traversal through a captured link remains unknown even when later
lexical `..` segments would remove that link's name. No link is followed.
The code-span, HTML block, list indentation and escape boundaries are based on
[CommonMark 0.31.2](https://spec.commonmark.org/0.31.2/); this limited checker does
not claim full CommonMark conformance.

Fragments use plain ASCII ATX headings with GitHub-style lowercase space-to-hyphen
IDs and numeric duplicate suffixes. Rich/non-ASCII/setext headings, HTML IDs and
unsupported fragment encodings cannot prove a missing anchor. Heading parsing uses
the same conservative Markdown context exclusions as reference parsing. Existing
captured IDs can prove a supported match; missing IDs require complete supported
target context. Case findings use exact captured spelling.

Declaration text shares the native held capture and raw-hash recheck. The existing
128-file/8-MiB aggregate text budget and per-document byte bounds cover Markdown,
selected scripts and manifests together, reserving root metadata. Each document
has at most 4,096 URL/dependency observations, each retained value at most 4,096
characters; the report keeps at most 256 observations with exact omission counts.
Only regular files are read. Selected declaration links, invalid encoding,
unsupported input and omitted observations make declaration coverage partial.
Markdown is shared input: skipped oversized/bounded files, invalid UTF-8 and
nonregular Markdown entries (including root `SKILL.md`) also make declaration
coverage partial independently of reference coverage.
`package.json` supports dependency mapping/string fields, with duplicate root/dependency key
rejection before local-path interpretation. `requirements.txt` supports plain
names/extras/version constraints and literal `-r` target existence checks; nested
include contents are not expanded and retain partial coverage. Other syntax remains
unknown. External declarations get concrete provision-in-selected-environment
guidance, never an installed/missing guess. Missing package-local declarations get
exact path repair guidance; no package manager or interpreter runs.
Home-prefixed `file:`/`link:` dependency addresses remain environment-specific and
unknown, with partial declaration coverage. Never expand HOME or classify an
external home-relative address against a literal `~` directory in the payload.

`--agent` / `--agent-version` select static rules in `PackageCompatibilityChecker`
on complete hash-verified root bytes. Its citations cover package metadata only;
agent settings, permissions and trust are not read or modeled. Only the cited
Gemini 0.46.0 framing rejection (BOM or missing closing `---`) is currently a
definite package incompatibility. A later `---` after YAML `...` remains unknown. Passing
this subset is still unknown for whole-agent compatibility; other agents/versions
retain explicit unknown reasons. No ambient policy/default allow is inferred.
Portability and literal URL/tool observations remain review evidence, distinct
from installed dependencies, actual connections and effective permission grants.

No skill scripts, agent clients, remote fetches, or automated fixes are part of
this workflow. It does not establish authenticity, whole-agent compatibility,
behavioral safety or source review; execution is `none`. Static checking is one
part of the combined [historical verification](https://github.com/harder/gh-skillview/blob/0f7264f7eca48f6329f9a82d60fbf803e43dd5bd/agent_docs/evidence/maintenance-pr71-m19-m20-final.json),
verified and merged in PR71. Other original maintenance findings remain open.

The [saved-update source-review phase](maintenance-update-source-review.md) adds
frozen content diffs, bounded declaration deltas and explicit acknowledgement to
new product plans. Source authors can use `gh skill publish --dry-run` directly;
its diagnostics are separate from installed-package static evidence.
PR71's four native hosts verified static checks.
Those results do not establish skill safety. Never use
`publish --fix` to repair installed updates or silently edit authored instructions.

The dialog uses `ModalOperationTracker` ownership through queued completion.
Closing cancels and drains its worker before disposing controls; parent lifetime
cancellation suppresses a queued successful result. All UI mutations are dispatched
through the application while the dialog is active.

Installed's conditional resource-replacement action is separate from checking.
Its narrow fact/intent/full-comparison/postcheck contract is in
[package-check contracts](contracts-package-checks.md). It uses ordinary
schema-2 saved whole-package replacement and both existing approvals; it is not
an automated fix, resource editor or parser extension. The static checker still
does no remote work or mutation.
