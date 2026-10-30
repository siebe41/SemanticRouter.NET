"""Whether a signed binary's entitlements (codesign -d --entitlements - --xml) are exactly an entitlements file's.

    python3 deploy/macos/entitlements-match.py <entitlements file> <signed entitlements plist>

Exits 0 when they are the same set with the same values; otherwise prints what differs and exits 1. Compared as plists,
so key order and formatting do not matter. Used by deploy/installer-signature-check.sh.
"""
import plistlib
import sys

with open(sys.argv[1], 'rb') as f:
    expected = plistlib.load(f)
try:
    with open(sys.argv[2], 'rb') as f:
        signed = plistlib.load(f)
except Exception as error:
    sys.exit(f'it is signed with no entitlements that can be read ({error})')

if signed != expected:
    extra = sorted(set(signed) - set(expected))
    missing = sorted(set(expected) - set(signed))
    changed = sorted(k for k in set(signed) & set(expected) if signed[k] != expected[k])
    sys.exit(f'its entitlements differ from the file: extra {extra}, missing {missing}, changed {changed}')
