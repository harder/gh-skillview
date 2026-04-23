#!/usr/bin/env bash
# cli/gh-extension-precompile@v2 calls this script expecting it to build
# gh-skillview-<GOOS>-<GOARCH>[.exe] binaries. SkillView builds those binaries
# in the matrix jobs that ran before this step; the release job has already
# collected them into dist/. We exit 0 to tell the action "binaries are ready".
set -euo pipefail
echo "build-noop.sh: extension binaries pre-built by matrix, nothing to do"
exit 0
