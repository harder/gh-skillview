# PTY session UX issues

This file is the session-specific landing spot for PTY/TUI findings that should
not be mixed into the broader `PHASE10_UX_ISSUES.md` backlog.

## Issues identified in this PTY session

1. **Install agent IDs were stale.**
   The install UI used older agent identifiers like `claude` instead of the
   current `gh skill install --agent` values such as `claude-code` and
   `github-copilot`.

2. **Successful install could exit the whole TUI.**
   The install flow stopped the app-wide run loop instead of stopping only the
   nested install dialog.

3. **Installed opened with filter focus and swallowed shortcuts.**
   The Installed screen initially focused the filter text field, so printable
   shortcuts like `x` were typed into the filter instead of being handled as
   commands.

These session findings have since been fixed in code and covered by tests; keep
using this file for any new PTY-session-specific issues found later.
