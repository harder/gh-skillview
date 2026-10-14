#!/usr/bin/env python3
"""Render a Homebrew formula from verified published SkillView assets."""

import argparse
import hashlib
from pathlib import Path
import re


ASSETS = {
    "darwin-arm64": "skillview-darwin-arm64",
    "linux-amd64": "skillview-linux-amd64",
}
SHA_LINE = re.compile(r"^([0-9a-fA-F]{64})\s+\*?([^\s]+)$")
VERSION = re.compile(r"^v([0-9]+\.[0-9]+\.[0-9]+)$")


def asset_sha(assets_dir: Path, platform: str, asset: str) -> str:
    checksums = assets_dir / f"SHA256SUMS-{platform}.txt"
    entries = {}
    for line in checksums.read_text(encoding="ascii").splitlines():
        match = SHA_LINE.fullmatch(line)
        if match is None or match.group(2) in entries:
            raise ValueError(f"Invalid or duplicate checksum in {checksums}: {line!r}")
        entries[match.group(2)] = match.group(1).lower()
    if asset not in entries:
        raise ValueError(f"{asset} is missing from {checksums}")
    actual = hashlib.sha256((assets_dir / asset).read_bytes()).hexdigest()
    if actual != entries[asset]:
        raise ValueError(f"Published checksum does not match {asset}")
    return actual


def render(tag: str, assets_dir: Path, template: Path) -> str:
    match = VERSION.fullmatch(tag)
    if match is None:
        raise ValueError(f"Expected stable vX.Y.Z release tag, got {tag!r}")
    version = match.group(1)
    values = {"{{VERSION}}": version}
    for platform, asset in ASSETS.items():
        suffix = "DARWIN_ARM64" if platform == "darwin-arm64" else "LINUX_X64"
        values[f"{{{{URL_{suffix}}}}}"] = (
            f"https://github.com/harder/gh-skillview/releases/download/{tag}/{asset}"
        )
        values[f"{{{{SHA_{suffix}}}}}"] = asset_sha(assets_dir, platform, asset)
    formula = template.read_text(encoding="utf-8")
    for placeholder, value in values.items():
        if placeholder not in formula:
            raise ValueError(f"Template is missing {placeholder}")
        formula = formula.replace(placeholder, value)
    if re.search(r"{{[A-Z_]+}}", formula):
        raise ValueError("Template has an unresolved placeholder")
    return formula


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--tag", required=True)
    parser.add_argument("--assets-dir", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    formula = render(
        args.tag,
        args.assets_dir,
        Path(__file__).with_name("skillview.rb.tmpl"),
    )
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(formula, encoding="utf-8", newline="\n")


if __name__ == "__main__":
    main()
