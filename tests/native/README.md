# Native verification tools

This directory owns active Python/C/shell verification tools and their Python
unit tests. Developer guides and historical result summaries live in
`agent_docs/`; executable test code belongs here. Changes anywhere under
`tests/` retain full CI verification, including Markdown fixtures.

## Automated coverage

- `.github/workflows/ci.yml` invokes current-host inventory, removal, update,
  metadata, authoring, evaluation, quarantine and saved-operation proofs on
  supported native targets. App and extension publishes remain sequential.
- `prove-old-native-*.py` creates and consumes actual prior-version saved plans.
  These are active compatibility tests for supported persisted schemas. Their
  frozen source commits are intentional, not stale dependencies to update.
- `.github/scripts/native_proof_inputs.py` verifies producer source/recipe/SDK
  receipts and transfers exact tool inputs into the native proof shards.
- Python tests are discovered by filename, rather than explicit imports:

  ```sh
  python3 -m unittest discover -s tests/native -p 'test_*.py'
  python3 -m unittest discover -s .github/scripts -p 'test_*.py'
  python3 -m unittest discover -s tools/tests -p 'test_*.py'
  ```

Manual tools such as `probe-linux-abi.c`, `prove-legacy-owner-compatibility.py`
and `prove-reviewed-update-tui.py` remain documented in the focused guides.
`tools/test_once.py` orchestrates opt-in acceptance checks. Keep real native,
Docker, network, authentication and terminal prerequisites explicit; a parser
or fixture test does not establish runtime publication or isolation.

## Retired tools

The October 23 audit removed three unreferenced batch scripts. Their original
bytes remain in Git history at `9825cede66f34348b2325cfe0179e17e6c85b99c`.

| Retired script | Why obsolete | Current coverage |
| --- | --- | --- |
| `prove-maintenance-batch-2.py` | Invokes old mutable `update --unpin/--force` behavior. | Revision-update, standalone-unpin and saved-plan proofs; exact-target CLI tests. |
| `prove-maintenance-batch-3.py` | Uses the replaced update/unpin flow and an old inventory projection. | Current inventory/ownership, tracking-roundtrip and saved-update proofs. |
| `prove-maintenance-batch-3b.py` | Historical discovery snapshot with no callers. | Installation-identity/inventory-filter proofs and scan-root/path/physical-merge unit tests. |

The audit also checked script callers, dynamic runner invocations, Python test
discovery, frozen baseline producers, Docker wrappers and C# skip conditions.
Platform-gated native regressions, schema-compatibility tests and current
integration/unit tests remain relevant and were retained.
