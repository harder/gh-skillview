"""Download one official gh release binary after verifying its release checksum."""
import argparse
import hashlib
import io
import json
from pathlib import Path
import re
import tarfile
import urllib.request
import zipfile

parser = argparse.ArgumentParser()
parser.add_argument('--version', required=True)
parser.add_argument('--rid', choices=('linux-x64', 'linux-arm64', 'osx-arm64', 'win-x64', 'win-arm64'), required=True)
parser.add_argument('--output', required=True, type=Path)
args = parser.parse_args()
if not re.fullmatch(r'[0-9]+\.[0-9]+\.[0-9]+', args.version):
    parser.error('An exact stable release version is required.')
operating_system, architecture = args.rid.split('-')
operating_system = {'osx': 'macOS', 'win': 'windows'}.get(operating_system, operating_system)
architecture = {'x64': 'amd64'}.get(architecture, architecture)
zip_archive = operating_system in ('macOS', 'windows')
archive_name = 'gh_' + args.version + '_' + operating_system + '_' + architecture + ('.zip' if zip_archive else '.tar.gz')
base = 'https://github.com/cli/cli/releases/download/v' + args.version + '/'


def download(name, limit):
    with urllib.request.urlopen(base + name, timeout=90) as response:
        data = response.read(limit + 1)
    assert len(data) <= limit, 'Official release download exceeded its budget'
    return data


checksums = download('gh_' + args.version + '_checksums.txt', 1024 * 1024)
rows = [row.split() for row in checksums.decode('ascii').splitlines()]
matching = [row[0] for row in rows if len(row) == 2 and row[1].lstrip('*') == archive_name]
assert len(matching) == 1 and re.fullmatch('[a-f0-9]{64}', matching[0]), 'Missing or ambiguous official checksum'
archive = download(archive_name, 128 * 1024 * 1024)
assert hashlib.sha256(archive).hexdigest() == matching[0], 'Official archive checksum mismatch'
leaf = 'bin/gh.exe' if operating_system == 'windows' else 'bin/gh'
if zip_archive:
    with zipfile.ZipFile(io.BytesIO(archive)) as opened:
        members = [item for item in opened.infolist() if item.filename == leaf or item.filename.endswith('/' + leaf)]
        assert len(members) == 1 and 0 < members[0].file_size <= 128 * 1024 * 1024
        binary = opened.read(members[0])
else:
    with tarfile.open(fileobj=io.BytesIO(archive), mode='r:gz') as opened:
        members = [item for item in opened.getmembers() if item.name == leaf or item.name.endswith('/' + leaf)]
        assert len(members) == 1 and members[0].isfile() and 0 < members[0].size <= 128 * 1024 * 1024
        with opened.extractfile(members[0]) as stream:
            binary = stream.read(128 * 1024 * 1024 + 1)
        assert len(binary) == members[0].size
# Only the verified binary is written. Archive paths never become filesystem paths.
requested = args.output.absolute()
assert all(not path.is_symlink() for path in (requested, *requested.parents)), 'Tool output must not follow symlinks'
requested.mkdir(mode=0o700, parents=True, exist_ok=False)
target = requested / Path(leaf).name
with target.open('xb') as stream:
    stream.write(binary)
target.chmod(0o755)
(requested / 'source.json').write_text(json.dumps(dict(version=args.version, rid=args.rid,
    archiveUrl=base + archive_name, checksumsUrl=base + 'gh_' + args.version + '_checksums.txt',
    archiveSha256=matching[0], binarySha256=hashlib.sha256(binary).hexdigest()), indent=2) + '\n')
print(str(target))
