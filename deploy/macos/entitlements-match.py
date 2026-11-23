"""Whether a signed binary's entitlements (codesign -d --entitlements - --xml) are exactly an entitlements file's.

    python3 deploy/macos/entitlements-match.py <entitlements file> <signed entitlements plist>

Exits 0 when they are the same set with the same values; otherwise prints what differs and exits 1. Compared as plists
(XML or binary), so key order, formatting and comments do not matter, as after plutil -convert xml1. codesign versions
that write the entitlements blob's 8-byte header (magic 0xfade7171 and length) before the plist, as older ones did
without --xml, are read too. No output at all means the binary is signed without entitlements, which fails. Used by
deploy/installer-signature-check.sh.
"""
import plistlib
import sys

BLOB_MAGIC = b'\xfa\xde\x71\x71'


def signed_plist(data):
    data = data.lstrip(b'\0')
    if data.startswith(BLOB_MAGIC):
        data = data[8:]
    # Anything codesign put before the plist itself ("Executable=..." when stderr was merged in).
    starts = [i for i in (data.find(b'<?xml'), data.find(b'<plist'), data.find(b'bplist00')) if i >= 0]
    return data[min(starts):] if starts else data


with open(sys.argv[1], 'rb') as f:
    expected = plistlib.load(f)
with open(sys.argv[2], 'rb') as f:
    data = signed_plist(f.read())
if not data.strip():
    sys.exit('it is signed with no entitlements at all')
try:
    signed = plistlib.loads(data)
except Exception as error:
    sys.exit(f'it is signed with entitlements that cannot be read ({error})')
if not isinstance(signed, dict):
    sys.exit(f'its entitlements are not a dictionary but {type(signed).__name__}')

if signed != expected:
    extra = sorted(set(signed) - set(expected))
    missing = sorted(set(expected) - set(signed))
    changed = sorted(k for k in set(signed) & set(expected) if signed[k] != expected[k])
    sys.exit(f'its entitlements differ from the file: extra {extra}, missing {missing}, changed {changed}')
