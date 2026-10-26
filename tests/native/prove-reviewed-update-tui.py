"""POSIX PTY proof of reviewed updates with disposable actual-gh installations.

The tiny ANSI screen reader only locates visible controls and records evidence;
payload, native continuity and recovery assertions come from the filesystem/CLI.
"""

import argparse
import codecs
import contextlib
import fcntl
import hashlib
import json
import os
import pathlib
import pty
import re
import select
import signal
import stat
import struct
import subprocess
import tempfile
import termios
import time
import unicodedata

p = argparse.ArgumentParser()
p.add_argument("--app", required=True)
p.add_argument("--gh", required=True)
p.add_argument("--shortcut", choices=["u", "U", "button"], default="u")
p.add_argument("--disposition", choices=["replace"], default="replace")
p.add_argument("--preserve-first", action="store_true")
p.add_argument("--local-edits", action="store_true", help="Review replacement of locally edited instructions, retaining the original for undo.")
p.add_argument("--output", required=True)
p.add_argument("--resource-replacement", action="store_true", help="Actual conditional Package B action, same-tag review, postcheck and TUI Undo.")
p.add_argument("--columns", type=int, choices=[80, 120], default=120)
p.add_argument("--rows", type=int, choices=[24, 36, 40], default=40)
a = p.parse_args()
os.umask(0o022)
width, height = a.columns, a.rows
app = str(pathlib.Path(a.app).resolve())
gh = str(pathlib.Path(a.gh).resolve())
auth = (
    os.environ.get("GH_TOKEN")
    or os.environ.get("GITHUB_TOKEN")
    or subprocess.run(
        [gh, "auth", "token"], text=True, capture_output=True, check=True
    ).stdout.strip()
)


def manifest(root):
    return [
        (
            str(q.relative_to(root)),
            stat.S_IMODE(q.lstat().st_mode),
            q.lstat().st_dev,
            q.lstat().st_ino,
            os.readlink(q)
            if q.is_symlink()
            else hashlib.sha256(q.read_bytes()).hexdigest()
            if q.is_file()
            else "",
        )
        for q in [root, *sorted(root.rglob("*"))]
    ]


with (contextlib.nullcontext(tempfile.mkdtemp(prefix="skillview-reviewed-tui-")) if a.resource_replacement
      else tempfile.TemporaryDirectory(prefix="skillview-reviewed-tui-")) as tmp:
    root = pathlib.Path(tmp).resolve()
    home = root / "home"
    home.mkdir()
    state = root / "state"
    env = {k: v for k, v in os.environ.items() if k not in ('CODEX_HOME', 'CLAUDE_CONFIG_DIR', 'CLAUDE_CODE_PLUGIN_CACHE_DIR', 'CLAUDE_CODE_PLUGIN_DIRS', 'CLAUDE_CODE_PLUGIN_SEED_DIR') if not k.startswith(("GIT_", "SKILLVIEW_"))}
    env.update(
        HOME=str(home),
        USERPROFILE=str(home),
        GH_TOKEN=auth,
        GH_EXTENSION="1",
        GH_PATH=gh,
        GH_CONFIG_DIR=str(root / "gh-config"),
        SKILLVIEW_STATE_HOME=str(state),
        XDG_STATE_HOME=str(root / "xdg-state"),
        XDG_CONFIG_HOME=str(root / "xdg-config"),
        XDG_CACHE_HOME=str(root / "xdg-cache"),
        XDG_DATA_HOME=str(root / "xdg-data"),
        APPDATA=str(root / "appdata"),
        LOCALAPPDATA=str(root / "localappdata"),
        CLAUDE_CONFIG_DIR=str(home / ".claude"),
        PI_CODING_AGENT_DIR=str(home / ".pi/agent"),
        TERM="xterm-256color",
        COLORTERM="truecolor",
        COLUMNS=str(width),
        LINES=str(height),
        TMPDIR=str(root / "tmp"),
        CODEX_HOME=str(home / ".codex"),
        GIT_CONFIG_COUNT="0",
        GH_NO_EXTENSION_UPDATE_NOTIFIER="1",
        GH_NO_UPDATE_NOTIFIER="1",
        GH_TELEMETRY="false",
        SKILLVIEW_LOG="debug",
    )
    (root / "tmp").mkdir()
    installed = subprocess.run(
        [
            gh,
            "skill",
            "install",
            "--agent",
            "universal",
            "--scope",
            "user",
            *(["--allow-hidden-dirs"] if a.resource_replacement else ["--pin=33789149b9def6b66ddf247438a79fac94e22074"]),
            "--",
            "cli/cli",
            ".github/skills/cli-exercise" if a.resource_replacement else "skills/gh-skill",
        ],
        env=env,
        cwd=root,
        capture_output=True,
        text=True,
        timeout=120,
    )
    assert installed.returncode == 0, installed.stderr
    payload = home / ".agents/skills" / ("cli-exercise" if a.resource_replacement else "gh-skill")
    registry = home / ".agents/.skill-lock.json"
    source_receipts = {}
    def source_resolution(phase):
        markdown = (payload / "SKILL.md").read_text()
        recorded = {key: re.search(r"^    " + key + r": (.+)$", markdown, re.M)[1]
                    for key in ("github-path", "github-ref", "github-tree-sha")}
        reference = recorded["github-ref"]
        assert reference.startswith("refs/tags/"), "Recorded tag kind required; never pass a full ref as Options.Tag"
        tag = reference[len("refs/tags/"):]
        # Product's existing Choose version -> ValidateTag -> PlanTagAsync also
        # validates this bare value. No branch/SHA/bare-kind fallback is used.
        def api(endpoint):
            response = subprocess.run([gh, "api", "--hostname", "github.com", "--", endpoint], env=env, cwd=root,
                                      capture_output=True, text=True, timeout=90)
            assert response.returncode == 0, response.stderr
            return json.loads(response.stdout)
        obj = api("repos/cli/cli/git/ref/tags/" + tag)
        assert obj["ref"] == reference
        obj = obj["object"]
        for _ in range(8):
            if obj["type"] == "commit": break
            assert obj["type"] == "tag"
            obj = api("repos/cli/cli/git/tags/" + obj["sha"])["object"]
        assert obj["type"] == "commit"
        commit = obj["sha"]
        tree = api("repos/cli/cli/git/commits/" + commit)["tree"]["sha"]
        for part in recorded["github-path"].split("/"):
            listing = api("repos/cli/cli/git/trees/" + tree)
            assert not listing["truncated"] and listing["sha"] == tree
            entry = [e for e in listing["tree"] if e["path"] == part]
            assert len(entry) == 1 and entry[0]["type"] == "tree"
            tree = entry[0]["sha"]
        assert tree == recorded["github-tree-sha"] == "96a779efe46b37049866f9ad5101c3374d711956", "Frozen fixture changed; stop"
        assert "pinned" not in markdown.split("---", 2)[1]
        source_receipts[phase] = dict(repository="cli/cli", subpath=recorded["github-path"], ref=reference, tag=tag, commit=commit, tree=tree)
        return tag

    def check_package():
        checked = subprocess.run([app, "check", "--path", str(payload), "--allow-hidden-dirs", "--json"],
                                 env=env, cwd=root, capture_output=True, text=True, timeout=90)
        assert checked.returncode == 1, (checked.stdout, checked.stderr)
        return json.loads(checked.stdout)

    def counts(report):
        problems = [f for f in report["findings"] if f["category"] == "reference" and f["code"] == "reference-missing" and f["path"] == "SKILL.md"]
        reviews = len(report["findings"]) - len(problems) + sum(o["state"] == "review" for o in report.get("observations", []))
        assert report["coverage"]["payload"] == "complete" and report["coverage"]["references"] == "partial"
        return len(problems), reviews

    if a.resource_replacement:
        tag = source_resolution("before")
        healthy_report = check_package()
        assert counts(healthy_report) == (0, 3)
        # Give the fixture a real unrelated gh owner before capturing authority.
        other = subprocess.run([gh, "skill", "install", "--agent", "universal", "--scope", "user", "--", "cli/cli", "skills/gh-skill"],
                               env=env, cwd=root, capture_output=True, text=True, timeout=180)
        assert other.returncode == 0, other.stderr
        exposure = home / ".claude/skills/cli-exercise"
        exposure.parent.mkdir(parents=True)
        exposure.symlink_to(payload, target_is_directory=True)
        exposure_before = (exposure.lstat().st_dev, exposure.lstat().st_ino, os.readlink(exposure))
        (payload / "references/maintenance.md").unlink()
    original_registry = registry.read_bytes()
    unrelated = home / ".agents/skills" / ("gh-skill" if a.resource_replacement else "unrelated-skill")
    if not a.resource_replacement:
        unrelated.mkdir()
    if not a.resource_replacement: (unrelated / "SKILL.md").write_bytes(
        b"---\nname: unrelated-skill\ndescription: Unselected local fixture\n---\nKeep these instructions\n"
    )
    if not a.resource_replacement: (unrelated / "resource").write_bytes(b"Unselected resource\x00\xff")
    unrelated_before = manifest(unrelated)
    (payload / "user-extra").write_bytes(b"Keep local extra\x00\xff")
    if a.local_edits or a.resource_replacement:
        (payload / "SKILL.md").write_bytes((payload / "SKILL.md").read_bytes() + b"\nLocally authored instructions\n")
    original = manifest(payload)
    if a.resource_replacement:
        missing_report = check_package()
        assert counts(missing_report) == (1, 4)
    master, slave = pty.openpty()
    fcntl.ioctl(slave, termios.TIOCSWINSZ, struct.pack("HHHH", height, width, 0, 0))

    def child():
        os.setsid()
        fcntl.ioctl(0, termios.TIOCSCTTY, 0)

    proc = subprocess.Popen(
        [app],
        stdin=slave,
        stdout=slave,
        stderr=slave,
        env=env,
        cwd=root,
        preexec_fn=child,
    )
    os.close(slave)
    screen = [[" "] * width for _ in range(height)]
    x = y = 0
    pending = ""
    transcript = bytearray()
    decoder = codecs.getincrementaldecoder("utf-8")("replace")

    def send(value):
        os.write(master, value if isinstance(value, bytes) else value.encode())

    def render(text):
        global x, y, pending
        pending += text
        while pending:
            if pending[0] == "\x1b":
                if len(pending) < 2:
                    return
                if pending[1] == "[":
                    m = re.match(r"\x1b\[([0-?]*)([ -/]*)([@-~])", pending)
                    if not m:
                        return
                    data, inter, cmd = m.groups()
                    pending = pending[m.end() :]
                    nums = [
                        int(v) if v.isdigit() else 0
                        for v in data.lstrip("?=>").split(";")
                    ]
                    n = nums[0] or 1
                    if cmd in "Hf":
                        y = min(height - 1, max(0, n - 1))
                        x = min(width - 1, max(0, (nums[1] if len(nums) > 1 else 1) - 1))
                    elif cmd == "A":
                        y = max(0, y - n)
                    elif cmd == "B":
                        y = min(height - 1, y + n)
                    elif cmd == "C":
                        x = min(width - 1, x + n)
                    elif cmd == "D":
                        x = max(0, x - n)
                    elif cmd == "G":
                        x = min(width - 1, n - 1)
                    elif cmd == "J" and nums[0] in [2, 3]:
                        for row in screen:
                            row[:] = [" "] * width
                    elif cmd == "K":
                        if nums[0] == 2:
                            screen[y][:] = [" "] * width
                        else:
                            screen[y][x:] = [" "] * (width - x)
                    elif cmd == "n" and nums[0] == 6:
                        send("\x1b[1;1R")
                    elif cmd == "t" and nums[0] == 18:
                        send(f"\x1b[8;{height};{width}t")
                    elif cmd == "c":
                        send("\x1b[?1;2c")
                    elif cmd == "u" and data == "?":
                        send("\x1b[?0u")
                    continue
                if pending[1] == "]":
                    end = pending.find("\x07")
                    end2 = pending.find("\x1b\\")
                    if end < 0 and end2 < 0:
                        return
                    if end >= 0 and (end2 < 0 or end < end2):
                        pending = pending[end + 1 :]
                    else:
                        pending = pending[end2 + 2 :]
                    continue
                pending = pending[2:]
                continue
            ch, pending = pending[0], pending[1:]
            if ch == "\r":
                x = 0
            elif ch == "\n":
                y = min(height - 1, y + 1)
            elif ch == "\b":
                x = max(0, x - 1)
            elif ord(ch) >= 32:
                if unicodedata.combining(ch):
                    continue
                screen[y][min(x, 119)] = ch
                x += 2 if unicodedata.east_asian_width(ch) in "WF" else 1
                if x >= width:
                    x = 0
                    y = min(height - 1, y + 1)

    def read(seconds=0.1):
        if select.select([master], [], [], seconds)[0]:
            try:
                data = os.read(master, 65536)
            except OSError:
                return
            transcript.extend(data)
            render(decoder.decode(data))

    def text():
        return "\n".join("".join(row) for row in screen)

    def wait(predicate, timeout=60):
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            read()
            if predicate():
                return
            if proc.poll() is not None:
                # Exit can race the first predicate check above. poll caches
                # the return code, so recheck before treating it as unexpected.
                assert predicate(), "TUI exited: " + str(proc.returncode) + "\n" + text()
                return
        raise AssertionError("TUI condition timed out\n" + text())

    def click(label, offset=1, first=False):
        rows = ["".join(row) for row in screen]
        found = [(row.index(label), i) for i, row in enumerate(rows) if label in row]
        assert found, "Missing control " + label + "\n" + text()
        # Status/instruction text can repeat an action label. Prefer the actual
        # Terminal.Gui button rather than the last textual occurrence.
        buttons = [(xx, yy) for xx, yy in found
                   if rows[yy].rfind("⟦", 0, xx) > rows[yy].rfind("⟧", 0, xx)]
        xx, yy = (buttons or found)[0 if first else -1]
        xx += offset
        send(f"\x1b[<0;{xx + 1};{yy + 1}M\x1b[<0;{xx + 1};{yy + 1}m")
        for _ in range(4):
            read(0.05)

    try:
        print("PTY started", width, height, flush=True)
        wait(lambda: "Installed" in text() and ("observed copies" in text() if a.resource_replacement else "gh 2." in text()), 90)
        click("Installed")
        print("phase: startup-list", flush=True)
        wait(lambda: ("2 observed copies" in text() if a.resource_replacement else "2 skill(s)" in text()), 60)
        if a.resource_replacement:
            # Select the first observed copy row, below its source heading.
            click("@refs/tags/", first=True)
            send("\r")
            wait(lambda: "More actions" in text())
            send("K")
            wait(lambda: "Static package check" in text() and "Format:" in text() and "Check again" in text())
            check_screen = text()
            click("Close")
            wait(lambda: "Static package check" not in text() and "More actions" in text())
            click("More actions")
            wait(lambda: "Review replacement for references/maintenance.md" in text())
            action_screen = text()
            print("phase: conditional-action", flush=True)
            click("Review replacement for references/maintenance.md")
            wait(lambda: "Choose version" in text() and "exact installation selected" in text())
            assert not list(state.rglob("plan.json")), "Entry must not prepare automatically"
            click("Include hidden directories")
            click("Choose version")
            wait(lambda: "Git tag:" in text() and "Prepare tag" in text())
            send(tag)
            click("Prepare tag")
        else:
            send("u")
        # Main entry immediately prepares. This fixture is pinned, so wait for its
        # read-only pin refusal before changing the explicit unpin option.
        if not a.resource_replacement:
            wait(lambda: "Include hidden directories" in text() and "pin retained" in text().lower(), 60)
            click("Unpin with update")
            click("Include hidden directories")
            if a.shortcut == "button":
                click("Review update")
            else:
                send(a.shortcut)  # u and U both review this exact seeded copy.
        wait(lambda: len(list(state.rglob("plan.json"))) == 1, 180)
        wait(lambda: "review required" in text().lower(), 30)
        planfile = next(state.rglob("plan.json"))
        print("phase: saved-plan", flush=True)
        saved = json.loads(planfile.read_bytes())
        if a.preserve_first:
            click("Preserve installed")
            wait(lambda: "installed payload preserved" in text(), 60)
            assert manifest(payload) == original and registry.read_bytes() == original_registry
            assert not (planfile.parent / "original").exists()
            send("u")
            wait(lambda: len(list(state.rglob("plan.json"))) == 2, 180)
            wait(lambda: "review required" in text().lower(), 30)
            planfile = next(path for path in state.rglob("plan.json") if path != planfile)
            saved = json.loads(planfile.read_bytes())
        if a.resource_replacement:
            source_resolution("after")
            assert source_receipts["before"] == source_receipts["after"]
            assert saved["revision"]["source"]["ref"] == source_receipts["before"]["ref"]
            assert saved["revision"]["source"]["tree"] == source_receipts["before"]["tree"]
            assert saved["original"]["payload"] == missing_report["payloadFingerprint"]
            paths = {entry["path"]: entry for entry in saved["candidate"]["entries"]}
            assert paths["references/maintenance.md"]["kind"] == "file"
            assert all(entry["path"] != "references/maintenance.md" for entry in saved["original"]["entries"])
        candidate = planfile.parent / "candidate"
        approved = manifest(candidate)
        assert manifest(payload) == original, "Preparation changed installed payload"
        assert registry.read_bytes() == original_registry
        assert manifest(unrelated) == unrelated_before
        assert not (planfile.parent / "original").exists()
        # Enter without retention approval cannot apply the review.
        send("\r")
        for _ in range(6):
            read(0.1)
        assert (
            manifest(payload) == original
            and not (planfile.parent / "original").exists()
        )
        assert registry.read_bytes() == original_registry
        screenshot = text()
        assert saved['schemaVersion'] == 2 and 'localDisposition' not in saved
        assert saved['sourceReview']['version'] == 1
        assert not any(label in screenshot for label in ('Review local', 'Build merge', 'Capture file for path'))
        click("Details (F2)")
        wait(lambda: "Summary (F2)" in text(), 30)
        if a.resource_replacement:
            click("Retain original")
            click("Replace reviewed")
            for _ in range(6): read(0.1)
            assert manifest(payload) == original and not (planfile.parent / "original").exists()
            click("Retain original")
        click("I reviewed F2 source changes/omissions")
        if a.resource_replacement:
            click("Replace reviewed")
            for _ in range(6): read(0.1)
            assert manifest(payload) == original and not (planfile.parent / "original").exists()
        click("Retain original")
        click("Replace reviewed")
        wait(
            lambda: (
                (planfile.parent / "original").exists()
                and payload.exists()
                and not candidate.exists()
            ),
            60,
        )
        wait(lambda: ("replacement installed" in text() if a.resource_replacement else "changed · Refresh inventory" in text()), 60)
        assert manifest(payload) == approved
        assert manifest(planfile.parent / "original") == original
        assert manifest(unrelated) == unrelated_before
        result_screen = text()
        print("phase: native-apply", flush=True)
        if a.resource_replacement:
            repaired_report = check_package()
            assert counts(repaired_report) == (0, 3)
            assert repaired_report["payloadFingerprint"] == saved["candidate"]["payload"]
            assert [f for f in healthy_report["findings"]] == repaired_report["findings"]
            assert (exposure.lstat().st_dev, exposure.lstat().st_ino, os.readlink(exposure)) == exposure_before
            # Find the bounded resource postcheck note in the actual Markdown pane.
            for _ in range(30):
                if "This resource was restored; other checks remain incomplete." in " ".join(text().split()): break
                send("\x1b[6~")
                for _ in range(3): read(0.05)
            assert "This resource was restored; other checks remain incomplete." in " ".join(text().split()), text()
            result_screen = text()
            print("phase: restored-note", flush=True)
            click("Undo this update")
            wait(lambda: "Restore the retained original" in text())
            undo_screen = text()
            print("phase: undo-review", flush=True)
            assert "This resource was restored" not in text()
            click("Restore the retained original")
            click("Undo update")
            def original_restored():
                # Native Undo intentionally has a vacant-original interval.
                # Poll the completed native state, never fail on that interval.
                try: return manifest(payload) == original
                except FileNotFoundError: return False
            wait(original_restored, 60)
            print("phase: native-undo", flush=True)
            wait(lambda: "Original restored" in text(), 60)
            assert "This resource was restored" not in text()
            assert "Replacement installed; package verification incomplete" not in text()
            assert registry.read_bytes() == original_registry and manifest(unrelated) == unrelated_before
            assert (exposure.lstat().st_dev, exposure.lstat().st_ino, os.readlink(exposure)) == exposure_before
            send("\x1b")
            wait(lambda: "Installed" in text() and "2 observed copies" in text() and "refreshing" not in text().lower() and "loading" not in text().lower(), 60)
            if "@refs/tags/" not in text():
                click("Back (Esc)")
                wait(lambda: "@refs/tags/" in text(), 60)
            click("@refs/tags/", first=True)
            send("\r")
            wait(lambda: "More actions" in text() and "Selected physical copy" not in text(), 60)
            send("K")
            wait(lambda: "Check again" in text() and "Format:" in text(), 60)
            next_check_screen = text()
            print("phase: next-k", flush=True)
            again_report = check_package()
            assert counts(again_report) == (1, 4)
            assert again_report["payloadFingerprint"] == missing_report["payloadFingerprint"]
            click("Close")
        send(b"\x11")
        wait(lambda: proc.poll() is not None, 20)
        assert proc.returncode == 0, f"TUI teardown failed: {proc.returncode}"

        def cli(command, *arguments):
            process = subprocess.run(
                [app, command, *arguments, "--allow-hidden-dirs", "--json"],
                env=env,
                cwd=root,
                text=True,
                capture_output=True,
                timeout=120,
            )
            assert process.returncode == 0, (
                process.returncode,
                process.stdout,
                process.stderr,
            )
            return json.loads(process.stdout)["operation"]

        if not a.resource_replacement:
            inspected = cli("update-inspect", "--id", saved["id"])
            assert (
                inspected["status"] == "installed"
                and inspected["owner"]["state"] == "desired"
                and inspected["sourceReviewAcknowledged"]
            )
            undone = cli(
                "update-undo",
                "--id",
                saved["id"],
                "--yes",
                "--expect",
                inspected["reviewFingerprint"],
            )
            assert undone["succeeded"] and manifest(payload) == original
            assert registry.read_bytes() == original_registry
            assert manifest(unrelated) == unrelated_before
        result = {
            "shortcut": "Installed More actions" if a.resource_replacement else a.shortcut,
            "disposition": a.disposition,
            "preserveVerified": a.preserve_first,
            "locallyEditedInstructionsReviewed": a.local_edits or a.resource_replacement,
            "sourceReviewAcknowledged": True,
            "actualPty": True,
            "actualGh": subprocess.run(
                [gh, "--version"], capture_output=True, text=True, check=True
            ).stdout.splitlines()[0],
            "prepareDidNotMutate": True,
            "unapprovedEnterDidNotApply": True,
            "approvedCandidateMatches": True,
            "originalNativePayloadRetained": True,
            "cliInspectionAndNativeUndoVerified": not a.resource_replacement,
            "originalRegistryBytesRestored": True,
            "unrelatedInstallationUnchanged": True,
            "exactSeedDidNotWidenToUnrelatedCopy": True,
            "planId": saved["id"],
            "reviewScreen": screenshot,
            "resultScreen": result_screen,
        }
        if a.resource_replacement:
            result.update(resourceReplacement=True, fixtureRoot=str(root), terminal=dict(columns=width, rows=height), sourceReceipts=source_receipts,
                          binarySha256=hashlib.sha256(pathlib.Path(gh).read_bytes()).hexdigest(),
                          appSha256=hashlib.sha256(pathlib.Path(app).read_bytes()).hexdigest(),
                          coreAssemblySha256=hashlib.sha256(pathlib.Path(app).with_name("SkillView.Core.dll").read_bytes()).hexdigest() if pathlib.Path(app).with_name("SkillView.Core.dll").exists() else None,
                          counts=[counts(healthy_report), counts(missing_report), counts(repaired_report), counts(again_report)],
                          fullComparisonRawFingerprintBound=True, sourceAndRetentionIndependentlyRequired=True,
                          planFingerprint=saved["reviewFingerprint"], savedPlanPath=str(planfile),
                          savedPlanSha256=hashlib.sha256(planfile.read_bytes()).hexdigest(),
                          installedRawFingerprint=saved["original"]["payload"], candidateRawFingerprint=saved["candidate"]["payload"],
                          restoredNoteThenTuiUndoThenNoteGone=True, nextExplicitKProblemReturns=True,
                          sharedExposureNativeObjectsUnchanged=True, exactUndoOriginalNativeObjects=True,
                          unrelatedOwnerBytesUnchanged=True, checkScreen=check_screen, actionScreen=action_screen,
                          undoScreen=undo_screen, nextCheckScreen=next_check_screen,
                          packageReports=dict(healthy=healthy_report, missing=missing_report, repaired=repaired_report, afterUndo=again_report),
                          originalNativeObjects=original, sharedExposureNativeObject=exposure_before,
                          registrySha256=hashlib.sha256(original_registry).hexdigest())
        pathlib.Path(a.output).write_text(json.dumps(result, indent=2) + "\n")
        print(
            json.dumps({k: v for k, v in result.items() if "Screen" not in k and k not in ("packageReports", "originalNativeObjects")}, indent=2)
        )
    finally:
        if proc.poll() is None:
            os.killpg(proc.pid, signal.SIGKILL)
        os.close(master)
        pathlib.Path(a.output + ".transcript").write_bytes(transcript)
        proc.wait(timeout=15)
