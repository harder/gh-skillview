"""Focused archive-selection and checksum-boundary tests; no network or binaries run."""
import contextlib
import hashlib
import io
import json
from pathlib import Path
import runpy
import tarfile
import tempfile
import unittest
from unittest.mock import patch
import zipfile


class PrepareGhProofToolTests(unittest.TestCase):
    script = Path(__file__).with_name('prepare-gh-proof-tool.py')

    def run_fixture(self, rid, archive_name, checksum_mode='valid'):
        binary = b'Fixture executable bytes; never executed'
        leaf = 'bin/gh.exe' if rid.startswith('win-') else 'bin/gh'
        member = 'gh_2.97.0_fixture/' + leaf
        packed = io.BytesIO()
        if archive_name.endswith('.zip'):
            with zipfile.ZipFile(packed, 'w') as archive:
                archive.writestr(member, binary)
        else:
            with tarfile.open(fileobj=packed, mode='w:gz') as archive:
                entry = tarfile.TarInfo(member)
                entry.size = len(binary)
                archive.addfile(entry, io.BytesIO(binary))
        archive_bytes = packed.getvalue()
        archive_hash = hashlib.sha256(archive_bytes).hexdigest()
        checksum = archive_hash + '  ' + archive_name + '\n'
        if checksum_mode == 'duplicate':
            checksum += checksum
        elif checksum_mode == 'wrong-name':
            checksum = archive_hash + '  ' + archive_name + '.other\n'
        elif checksum_mode == 'wrong-hash':
            checksum = '0' * 64 + '  ' + archive_name + '\n'
        requested = []

        def download(url, timeout):
            requested.append(url)
            if url.endswith('/gh_2.97.0_checksums.txt'):
                return io.BytesIO(checksum.encode('ascii'))
            self.assertTrue(url.endswith('/' + archive_name), url)
            return io.BytesIO(archive_bytes)

        with tempfile.TemporaryDirectory(prefix='skillview-gh-download-test-') as directory:
            output = Path(directory) / 'tool'
            argv = [str(self.script), '--version', '2.97.0', '--rid', rid, '--output', str(output)]
            with patch('sys.argv', argv), patch('urllib.request.urlopen', side_effect=download), contextlib.redirect_stdout(io.StringIO()):
                if checksum_mode != 'valid':
                    with self.assertRaisesRegex(AssertionError, 'checksum'):
                        runpy.run_path(str(self.script), run_name='__main__')
                    self.assertFalse(output.exists())
                    self.assertEqual(2 if checksum_mode == 'wrong-hash' else 1, len(requested))
                    return
                runpy.run_path(str(self.script), run_name='__main__')
            self.assertEqual(binary, (output / Path(leaf).name).read_bytes())
            self.assertEqual({Path(leaf).name, 'source.json'}, {path.name for path in output.iterdir()})
            source = json.loads((output / 'source.json').read_text())
            self.assertTrue(source['archiveUrl'].endswith('/' + archive_name))
            self.assertEqual(archive_hash, source['archiveSha256'])
            self.assertEqual(hashlib.sha256(binary).hexdigest(), source['binarySha256'])

    def test_official_asset_formats_for_every_supported_rid(self):
        for rid, archive in [
                ('linux-x64', 'gh_2.97.0_linux_amd64.tar.gz'),
                ('linux-arm64', 'gh_2.97.0_linux_arm64.tar.gz'),
                ('osx-arm64', 'gh_2.97.0_macOS_arm64.zip'),
                ('win-x64', 'gh_2.97.0_windows_amd64.zip'),
                ('win-arm64', 'gh_2.97.0_windows_arm64.zip')]:
            with self.subTest(rid=rid):
                self.run_fixture(rid, archive)

    def test_missing_or_ambiguous_exact_checksum_refuses_before_archive_download(self):
        for mode in ('duplicate', 'wrong-name'):
            with self.subTest(mode=mode):
                self.run_fixture('osx-arm64', 'gh_2.97.0_macOS_arm64.zip', mode)

    def test_mismatched_archive_checksum_refuses_before_output_creation(self):
        self.run_fixture('osx-arm64', 'gh_2.97.0_macOS_arm64.zip', 'wrong-hash')


if __name__ == '__main__':
    unittest.main()
