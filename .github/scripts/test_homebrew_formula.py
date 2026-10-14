import hashlib
from pathlib import Path
import sys
import tempfile
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "packaging" / "homebrew"))
from render_formula import render  # noqa: E402


class HomebrewFormulaTests(unittest.TestCase):
    def test_renders_verified_release_assets(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            template = root / "skillview.rb.tmpl"
            template.write_text(
                "{{VERSION}} {{URL_DARWIN_ARM64}} {{SHA_DARWIN_ARM64}} "
                "{{URL_LINUX_X64}} {{SHA_LINUX_X64}}",
                encoding="utf-8",
            )
            for platform, asset in (
                ("darwin-arm64", "skillview-darwin-arm64"),
                ("linux-amd64", "skillview-linux-amd64"),
            ):
                data = platform.encode()
                (root / asset).write_bytes(data)
                sha = hashlib.sha256(data).hexdigest()
                (root / f"SHA256SUMS-{platform}.txt").write_text(
                    f"{sha}  {asset}\n", encoding="ascii"
                )
            output = render("v1.0.1", root, template)
            self.assertIn("1.0.1", output)
            self.assertIn("/v1.0.1/skillview-darwin-arm64", output)
            self.assertIn("/v1.0.1/skillview-linux-amd64", output)
            (root / "skillview-linux-amd64").write_bytes(b"changed")
            with self.assertRaisesRegex(ValueError, "checksum does not match"):
                render("v1.0.1", root, template)

    def test_rejects_prerelease_tag(self):
        with self.assertRaisesRegex(ValueError, "stable"):
            render("v1.0.1-rc.1", Path("."), Path("unused"))


if __name__ == "__main__":
    unittest.main()
