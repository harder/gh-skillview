"""POSIX PTY proof of reviewed updates with disposable actual-gh installations.

The tiny ANSI screen reader only locates visible controls and records evidence;
payload, native continuity and recovery assertions come from the filesystem/CLI.
"""

import argparse
import codecs
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
a = p.parse_args()
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


with tempfile.TemporaryDirectory(prefix="skillview-reviewed-tui-") as tmp:
    root = pathlib.Path(tmp).resolve()
    home = root / "home"
    home.mkdir()
    state = root / "state"
    env = {k: v for k, v in os.environ.items() if k not in ('CODEX_HOME', 'CLAUDE_CONFIG_DIR', 'CLAUDE_CODE_PLUGIN_CACHE_DIR', 'CLAUDE_CODE_PLUGIN_DIRS', 'CLAUDE_CODE_PLUGIN_SEED_DIR') if not k.startswith("GIT_")}
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
        COLUMNS="120",
        LINES="40",
        GH_NO_UPDATE_NOTIFIER="1",
        GH_TELEMETRY="false",
        SKILLVIEW_LOG="debug",
    )
    installed = subprocess.run(
        [
            gh,
            "skill",
            "install",
            "--agent",
            "universal",
            "--scope",
            "user",
            "--pin=33789149b9def6b66ddf247438a79fac94e22074",
            "--",
            "cli/cli",
            "skills/gh-skill",
        ],
        env=env,
        cwd=root,
        capture_output=True,
        text=True,
        timeout=120,
    )
    assert installed.returncode == 0, installed.stderr
    payload = home / ".agents/skills/gh-skill"
    registry = home / ".agents/.skill-lock.json"
    original_registry = registry.read_bytes()
    unrelated = home / ".agents/skills/unrelated-skill"
    unrelated.mkdir()
    (unrelated / "SKILL.md").write_bytes(
        b"---\nname: unrelated-skill\ndescription: Unselected local fixture\n---\nKeep these instructions\n"
    )
    (unrelated / "resource").write_bytes(b"Unselected resource\x00\xff")
    unrelated_before = manifest(unrelated)
    (payload / "user-extra").write_bytes(b"Keep local extra\x00\xff")
    if a.local_edits:
        (payload / "SKILL.md").write_bytes((payload / "SKILL.md").read_bytes() + b"\nLocally authored instructions\n")
    original = manifest(payload)
    master, slave = pty.openpty()
    fcntl.ioctl(slave, termios.TIOCSWINSZ, struct.pack("HHHH", 40, 120, 0, 0))

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
    screen = [[" "] * 120 for _ in range(40)]
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
                        y = min(39, max(0, n - 1))
                        x = min(119, max(0, (nums[1] if len(nums) > 1 else 1) - 1))
                    elif cmd == "A":
                        y = max(0, y - n)
                    elif cmd == "B":
                        y = min(39, y + n)
                    elif cmd == "C":
                        x = min(119, x + n)
                    elif cmd == "D":
                        x = max(0, x - n)
                    elif cmd == "G":
                        x = min(119, n - 1)
                    elif cmd == "J" and nums[0] in [2, 3]:
                        for row in screen:
                            row[:] = [" "] * 120
                    elif cmd == "K":
                        if nums[0] == 2:
                            screen[y][:] = [" "] * 120
                        else:
                            screen[y][x:] = [" "] * (120 - x)
                    elif cmd == "n" and nums[0] == 6:
                        send("\x1b[1;1R")
                    elif cmd == "t" and nums[0] == 18:
                        send("\x1b[8;40;120t")
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
                y = min(39, y + 1)
            elif ch == "\b":
                x = max(0, x - 1)
            elif ord(ch) >= 32:
                if unicodedata.combining(ch):
                    continue
                screen[y][min(x, 119)] = ch
                x += 2 if unicodedata.east_asian_width(ch) in "WF" else 1
                if x >= 120:
                    x = 0
                    y = min(39, y + 1)

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

    def click(label, offset=1, table=False):
        rows = ["".join(row) for row in screen]
        found = [(row.index(label), i) for i, row in enumerate(rows) if label in row]
        if table:
            found = [(xx, yy) for xx, yy in found if xx < 54]
        assert found, "Missing control " + label + "\n" + text()
        # Status/instruction text can repeat an action label. Prefer the actual
        # Terminal.Gui button rather than the last textual occurrence.
        buttons = [(xx, yy) for xx, yy in found
                   if rows[yy].rfind("⟦", 0, xx) > rows[yy].rfind("⟧", 0, xx)]
        xx, yy = (buttons or found)[-1]
        xx += offset
        send(f"\x1b[<0;{xx + 1};{yy + 1}M\x1b[<0;{xx + 1};{yy + 1}m")
        for _ in range(4):
            read(0.05)

    try:
        wait(lambda: "gh 2." in text() and "Installed" in text(), 90)
        click("Installed")
        wait(lambda: "2 skill(s)" in text(), 60)
        send("u")
        wait(
            lambda: (
                "Include hidden directories" in text()
                and "installed skill(s)" in text()
            ),
            60,
        )
        click("Unpin with update")
        click("Include hidden directories")
        if a.shortcut in ("u", "U"):
            click("all")  # row/marked shortcuts must not widen to this selection
        click("gh-skill", table=True)  # marking must target the table, not the last checkbox
        if a.shortcut == "U":
            send(" ")
            read(0.1)
            send("U")
        elif a.shortcut == "button":
            send(" ")
            read(0.1)
            click("Review update")
        else:
            send("u")
        wait(lambda: len(list(state.rglob("plan.json"))) == 1, 180)
        wait(lambda: "review required" in text().lower(), 30)
        planfile = next(state.rglob("plan.json"))
        saved = json.loads(planfile.read_bytes())
        if a.preserve_first:
            click("Preserve installed")
            wait(lambda: "installed payload preserved" in text(), 60)
            assert manifest(payload) == original and registry.read_bytes() == original_registry
            assert not (planfile.parent / "original").exists()
            click("Next / skip")
            click("gh-skill", table=True)
            send("u")
            wait(lambda: len(list(state.rglob("plan.json"))) == 2, 180)
            wait(lambda: "review required" in text().lower(), 30)
            planfile = next(path for path in state.rglob("plan.json") if path != planfile)
            saved = json.loads(planfile.read_bytes())
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
        click("I reviewed F2 source changes/omissions")
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
        wait(lambda: "changed · no remaining items" in text(), 60)
        assert manifest(payload) == approved
        assert manifest(planfile.parent / "original") == original
        assert manifest(unrelated) == unrelated_before
        result_screen = text()
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
            "shortcut": a.shortcut,
            "disposition": a.disposition,
            "preserveVerified": a.preserve_first,
            "locallyEditedInstructionsReviewed": a.local_edits,
            "sourceReviewAcknowledged": True,
            "actualPty": True,
            "actualGh": subprocess.run(
                [gh, "--version"], capture_output=True, text=True, check=True
            ).stdout.splitlines()[0],
            "prepareDidNotMutate": True,
            "unapprovedEnterDidNotApply": True,
            "approvedCandidateMatches": True,
            "originalNativePayloadRetained": True,
            "cliInspectionAndNativeUndoVerified": True,
            "originalRegistryBytesRestored": True,
            "unrelatedInstallationUnchanged": True,
            "allCheckboxDidNotWidenShortcut": a.shortcut in ("u", "U"),
            "planId": saved["id"],
            "reviewScreen": screenshot,
            "resultScreen": result_screen,
        }
        pathlib.Path(a.output).write_text(json.dumps(result, indent=2) + "\n")
        print(
            json.dumps({k: v for k, v in result.items() if "Screen" not in k}, indent=2)
        )
    finally:
        if proc.poll() is None:
            os.killpg(proc.pid, signal.SIGKILL)
        os.close(master)
        pathlib.Path(a.output + ".transcript").write_bytes(transcript)
        proc.wait(timeout=15)
