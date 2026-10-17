"""Owned ext4 native-interop proof, with an independent privileged xattr witness.

This exercises the managed proof host's live primitive. It is not an App/extension
AOT, saved restart, exhaustive metadata, or consumer-detachment proof.
"""
import argparse
import base64
import hashlib
import json
import os
from pathlib import Path
import stat
import subprocess
import sys
import tempfile


MARKER = 'SkillView disposable exposure link proof\n'
ATTRIBUTE = 'trusted.skillview-exposure-proof'
VALUE = b'SkillView owned hidden attribute witness\x00v1'


def identity(info):
    return [info.st_dev, info.st_ino, stat.S_IMODE(info.st_mode), info.st_uid,
            info.st_gid, info.st_nlink]


def privileged_witness(action, root, owner):
    if sys.platform != 'linux' or os.geteuid() != 0:
        raise RuntimeError('The independent attribute witness requires Linux root authority')
    root_fd = os.open(root, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW)
    try:
        info = os.fstat(root_fd)
        if info.st_uid != owner or stat.S_IMODE(info.st_mode) != 0o700:
            raise RuntimeError('Witness root must be the caller-owned private fixture')
        marker_fd = os.open('disposable-proof-marker', os.O_RDONLY | os.O_NOFOLLOW, dir_fd=root_fd)
        with os.fdopen(marker_fd, 'rb') as marker:
            if marker.read(1024) != MARKER.encode():
                raise RuntimeError('Missing exact disposable exposure proof marker')
        parent_fd = os.open('exposures', os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW, dir_fd=root_fd)
        try:
            parent = os.fstat(parent_fd)
            if parent.st_uid != owner or stat.S_IMODE(parent.st_mode) != 0o700:
                raise RuntimeError('Witness exposure parent must be caller-owned and private')
            before = os.stat('demo', dir_fd=parent_fd, follow_symlinks=False)
            if not stat.S_ISLNK(before.st_mode) or before.st_uid != owner or before.st_nlink != 1:
                raise RuntimeError('Witness only admits one caller-owned ordinary symlink')
            if os.readlink('demo', dir_fd=parent_fd) != '../targets/payload':
                raise RuntimeError('Witness only admits the exact relative fixture target')
            address = '/proc/thread-self/fd/' + str(parent_fd) + '/demo'
            if action == 'set':
                os.setxattr(address, ATTRIBUTE, VALUE, flags=os.XATTR_CREATE, follow_symlinks=False)
            metadata_before = os.stat('demo', dir_fd=parent_fd, follow_symlinks=False)
            value = os.getxattr(address, ATTRIBUTE, follow_symlinks=False)
            if value != VALUE:
                raise RuntimeError('Hidden witness attribute value changed')
            visible = sorted(os.listxattr(address, follow_symlinks=False))
            if len(visible) > 64 or sum(len(name.encode()) for name in visible) > 65536:
                raise RuntimeError('Privileged attribute enumeration exceeded its fixture bound')
            if ATTRIBUTE not in visible:
                raise RuntimeError('Privileged witness cannot enumerate its real attribute')
            attributes = {}
            total_bytes = 0
            for name in visible:
                captured = os.getxattr(address, name, follow_symlinks=False)
                total_bytes += len(captured)
                if total_bytes > 2 * 1024 * 1024:
                    raise RuntimeError('Privileged attribute values exceeded their fixture bound')
                attributes[name] = base64.b64encode(captured).decode('ascii')
            after = os.stat('demo', dir_fd=parent_fd, follow_symlinks=False)
            if identity(before) != identity(after) or metadata_before.st_ctime_ns != after.st_ctime_ns:
                raise RuntimeError('Witness link identity changed during its observation')
            print(json.dumps(dict(identity=identity(after), attributes=attributes,
                                  knownAttributeSha256=hashlib.sha256(value).hexdigest())))
        finally:
            os.close(parent_fd)
    finally:
        os.close(root_fd)


def payload_manifest(root):
    rows = []
    for path in [root, *sorted(root.rglob('*'))]:
        info = path.lstat()
        if stat.S_ISLNK(info.st_mode):
            raise RuntimeError('Target fixture unexpectedly contains a link')
        rows.append([str(path.relative_to(root)), identity(info),
                     hashlib.sha256(path.read_bytes()).hexdigest() if path.is_file() else None])
    return rows


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dotnet', type=Path)
    parser.add_argument('--proof', type=Path)
    parser.add_argument('--output', type=Path)
    parser.add_argument('--witness', choices=('set', 'read'))
    parser.add_argument('--root', type=Path)
    parser.add_argument('--owner', type=int)
    args = parser.parse_args()
    if args.witness:
        if args.root is None or args.owner is None or args.owner <= 0:
            parser.error('Witness requires its caller-owned root and non-root owner')
        privileged_witness(args.witness, args.root, args.owner)
        return
    if args.dotnet is None or args.proof is None or args.output is None:
        parser.error('--dotnet, --proof and --output are required')
    output = args.output.resolve()
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(dict(schemaVersion=1, succeeded=False, positiveHiddenAttributeProof=False,
                                      stage='starting', scope='No successful native or hidden-attribute proof yet'), indent=2) + '\n')
    if sys.platform != 'linux' or os.geteuid() == 0:
        raise RuntimeError('The product primitive must run as an unprivileged Linux user')
    dotnet, proof = args.dotnet.resolve(strict=True), args.proof.resolve(strict=True)
    script = Path(__file__).resolve(strict=True)
    with tempfile.TemporaryDirectory(prefix='skillview-link-hidden-') as temporary:
        root = Path(temporary).resolve()
        root.chmod(0o700)
        home, state, exposures, targets = [root / name for name in ('home', 'state', 'exposures', 'targets')]
        for path in (home, state, exposures, targets):
            path.mkdir(mode=0o700)
        payload = targets / 'payload'
        payload.mkdir(mode=0o700)
        (payload / 'SKILL.md').write_text('---\nname: demo\ndescription: hidden metadata native proof\n---\nunchanged\n')
        (payload / 'empty').mkdir()
        executed = root / 'script-executed'
        (payload / 'script.py').write_text('from pathlib import Path\nPath(' + repr(str(executed)) + ').write_text("executed")\n')
        (payload / 'script.py').chmod(0o751)
        link = exposures / 'demo'
        link.symlink_to('../targets/payload', target_is_directory=True)
        (root / 'disposable-proof-marker').write_text(MARKER)
        witness_commands = []

        def witness(action):
            command = ['sudo', '-n', str(Path(sys.executable).resolve()), str(script), '--witness', action,
                       '--root', str(root), '--owner', str(os.geteuid())]
            result = subprocess.run(command, capture_output=True, text=True, timeout=30)
            witness_commands.append(dict(action=action, exitCode=result.returncode, stderr=result.stderr))
            if result.returncode != 0:
                output.parent.mkdir(parents=True, exist_ok=True)
                output.write_text(json.dumps(dict(schemaVersion=1, succeeded=False, stage='privileged-witness-' + action,
                                                  positiveHiddenAttributeProof=False, exitCode=result.returncode,
                                                  stderr=result.stderr[-8192:],
                                                  scope='Missing/failed fixture setup or witness; never a positive preservation result'), indent=2) + '\n')
                raise RuntimeError('Privileged fixture/witness failed: ' + result.stderr[-8192:])
            return json.loads(result.stdout)

        privileged_before = witness('set')
        if ATTRIBUTE in os.listxattr(link, follow_symlinks=False):
            raise RuntimeError('The product caller can see the trusted attribute; this is not a hidden-visibility fixture')
        before_link = identity(link.lstat())
        before_payload = payload_manifest(payload)
        raw_before = os.readlink(link).encode()
        env = {key: value for key, value in os.environ.items()
               if not key.startswith(('GIT_', 'GH_', 'GITHUB_', 'SKILLVIEW_'))}
        env.update(HOME=str(home), USERPROFILE=str(home), SKILLVIEW_STATE_HOME=str(state),
                   XDG_CONFIG_HOME=str(home / 'config'), XDG_STATE_HOME=str(home / 'state'),
                   XDG_DATA_HOME=str(home / 'data'), XDG_CACHE_HOME=str(home / 'cache'),
                   DOTNET_CLI_HOME=str(home), DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1',
                   GIT_CONFIG_COUNT='0')
        command = [str(dotnet), str(proof), '--exposure-link-roundtrip', str(root)]
        result = subprocess.run(command, cwd=root, env=env, capture_output=True, text=True, timeout=180)
        report = json.loads(result.stdout) if result.returncode == 0 else None
        if result.returncode != 0:
            raise RuntimeError('Native proof failed: ' + result.stderr)
        assert report['SchemaVersion'] == 1 and report['HostKind'] == 'managed-maintenance-proof', report
        assert report['Mode'] == 'LinuxSameOriginalObjectPreservation', report
        assert report['FullMetadataSupported'] is False, report
        assert report['Continuity'] == 'live-original-link-pin', report
        assert report['Coverage'] == 'caller-visible-all-attributes; hidden-namespaces-and-inode-flags-unknown', report
        assert report['RetainedSameNativeObject'] and report['RestoredSameNativeObject'] and report['TargetUnchanged'], report
        assert report['VolumeId'] == before_link[0] and report['FileIdLow'] == before_link[1], report
        assert report['RawTargetSha256'] == hashlib.sha256(raw_before).hexdigest(), report
        assert report['TargetBeforeFingerprint'] == report['TargetAfterFingerprint'], report
        assert report['VisibleAttributesFingerprint'], report
        assert identity(link.lstat()) == before_link and os.readlink(link).encode() == raw_before
        assert payload_manifest(payload) == before_payload
        assert not executed.exists(), 'Native retention executed a payload script'
        assert ATTRIBUTE not in os.listxattr(link, follow_symlinks=False)
        privileged_after = witness('read')
        assert privileged_before == privileged_after, (privileged_before, privileged_after)
        output.parent.mkdir(parents=True, exist_ok=True)
        output.write_text(json.dumps(dict(schemaVersion=1, succeeded=True, positiveHiddenAttributeProof=True,
                                          proofHostSha256=hashlib.sha256(proof.read_bytes()).hexdigest(),
                                          coreAssemblySha256=hashlib.sha256((proof.parent / 'SkillView.Core.dll').read_bytes()).hexdigest(),
                                          dotnetSha256=hashlib.sha256(dotnet.read_bytes()).hexdigest(),
                                          scriptSha256=hashlib.sha256(script.read_bytes()).hexdigest(),
                                          productUnprivileged=True, knownTrustedAttributeActuallyHidden=True,
                                          privilegedBefore=privileged_before, privilegedAfter=privileged_after,
                                          nativeReport=report, witnessCommands=witness_commands,
                                          sourceAndTargetUnchanged=True,
                                          payloadScriptNotExecuted=True,
                                          scope='Live managed native-interop proof only; no saved restart or product AOT claim'), indent=2) + '\n')
        print(output)


if __name__ == '__main__':
    main()
