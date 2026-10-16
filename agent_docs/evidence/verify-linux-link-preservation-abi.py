#!/usr/bin/env python3
"""Validate native-header evidence; this does not enable product admission."""
import argparse
import hashlib
import json
import platform
from pathlib import Path


def verify(report, architecture):
    if report.get("schema") != 1 or report.get("ok") is not True:
        raise ValueError("Native ABI probe did not succeed")
    expected = {"architecture": architecture, "endianness": "little", "pointerBytes": 8,
                "longBytes": 8, "devTBytes": 8, "devTAlignment": 8}
    if any(report.get(key) != value for key, value in expected.items()):
        raise ValueError("Unverified process architecture or native scalar layout")
    if report.get("statfs") != {"size": 120, "alignment": 8, "typeOffset": 0, "typeBytes": 8,
                                "fsidOffset": 56, "fsidBytes": 8, "flagsOffset": 80, "flagsBytes": 8}:
        raise ValueError("Unverified statfs layout")
    if report.get("canaries") != {"beforeBytes": 64, "afterBytes": 64, "valueOffset": 64,
                                  "afterOffset": 184, "beforeIntact": True, "afterIntact": True}:
        raise ValueError("Native statfs write exceeded the verified bounds")
    held = report.get("heldDirectory", {})
    if held.get("fstatfsResult") != 0 or held.get("directory") is not True:
        raise ValueError("Held-directory native observation failed")
    cases = report.get("deviceCases", [])
    expected_pairs = [(0, 0), (1, 1), (8, 48), (0xfff, 0xff), (0x1000, 0x100),
                      (0xfffff, 0xfffff), (0x12345678, 0x9abcdef0),
                      (0xffffffff, 0), (0, 0xffffffff), (0xffffffff, 0xffffffff)]
    if [(case.get("inputMajor"), case.get("inputMinor")) for case in cases] != expected_pairs:
        raise ValueError("Native device-number cases are incomplete")
    for case in cases + [{"inputMajor": held.get("major"), "inputMinor": held.get("minor"),
                          "decodedMajor": held.get("major"), "decodedMinor": held.get("minor"),
                          "encodedUnsigned": held.get("deviceUnsigned"), "encodedHex": held.get("deviceHex")}]:
        major, minor = case["inputMajor"], case["inputMinor"]
        encoded = int(case["encodedUnsigned"])
        wanted = ((major & 0xfff) << 8) | (minor & 0xff) | ((major & 0xfffff000) << 32) | ((minor & 0xffffff00) << 12)
        decoded_major = ((encoded >> 8) & 0xfff) | ((encoded >> 32) & 0xfffff000)
        decoded_minor = (encoded & 0xff) | ((encoded >> 12) & 0xffffff00)
        if not (0 <= major <= 0xffffffff and 0 <= minor <= 0xffffffff and 0 <= encoded <= 0xffffffffffffffff):
            raise ValueError("Native device number exceeds its ABI")
        if encoded != wanted or int(case["encodedHex"], 16) != encoded or (decoded_major, decoded_minor) != (major, minor) or (case["decodedMajor"], case["decodedMinor"]) != (major, minor):
            raise ValueError("Managed device-number decoding differs from native macros")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--input", type=Path, required=True)
    parser.add_argument("--source", type=Path, required=True)
    parser.add_argument("--architecture", choices=("x64", "arm64"), required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    host = {"x86_64": "x64", "aarch64": "arm64"}.get(platform.machine().lower())
    if host != args.architecture:
        raise ValueError("Probe must execute on the expected native host")
    input_bytes = args.input.read_bytes()
    report = json.loads(input_bytes)
    verify(report, args.architecture)
    args.output.write_text(json.dumps({"schema": 1, "verified": True, "architecture": host,
                                      "inputSha256": hashlib.sha256(input_bytes).hexdigest(),
                                      "sourceSha256": hashlib.sha256(args.source.read_bytes()).hexdigest(),
                                      "scope": "Native C ABI evidence only; no product or filesystem admission enabled",
                                      "report": report}, indent=2) + "\n")


if __name__ == "__main__":
    main()
