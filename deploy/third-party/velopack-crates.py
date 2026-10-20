#!/usr/bin/env python3
"""Writes deploy/third-party/velopack-crates.json: the Rust crates statically linked into Velopack's native binaries
(Update.exe/Squirrel.exe, Setup.exe and the stub on Windows, UpdateMac, UpdateNix), which vpk adds to the installers and
update packages. Velopack publishes no list of them, so this derives one from its sources at the release tag; rerun it
when the Velopack package version changes (deploy/third-party-notices.cs refuses a list for another version):

    python deploy/third-party/velopack-crates.py 1.2.161

It needs Python 3.11 or later and a logged-in `gh`, and no Rust toolchain: everything comes from GitHub, read-only:

- the crate graph from Velopack's Cargo.lock at the tag, starting at the velopack_bins package;
- which edges are normal dependencies, and for which targets, from the workspace members' Cargo.toml files and, for
  crates.io crates, the crates.io index (github.com/rust-lang/crates.io-index). Dev- and build-dependencies are left out:
  they are not linked into the binaries. Proc macros are kept (the index does not mark them), which only adds entries;
- the targets from Velopack's build-rust.yml at 1.2.161: Windows i686/x86_64-win7 and aarch64 MSVC, x86_64/aarch64
  macOS and x86_64/aarch64 Linux musl. A crate is listed for each OS any of its targets links it on;
- each crate's licence and copyright from GitHub's dependency graph SBOM of velopack/velopack (ClearlyDefined's data
  about the crate's published sources). That SBOM describes Velopack's default branch, not the tag: a crate version it
  does not have takes the entry of the nearest version of the same crate it has, and says so in "metadataFrom". A
  crate ClearlyDefined has no licence for takes it from the LICENCES table below, read by hand ("licenceFrom").

Cargo.lock lists every optional dependency some workspace member enables, so the graph is a superset of what
`cargo build -p velopack_bins` links (the Windows build is `--workspace`, which unifies features the same way).
"""

import json
import re
import subprocess
import sys
import tomllib
from pathlib import Path

REPOSITORY = "velopack/velopack"
OUTPUT = Path(__file__).resolve().parent / "velopack-crates.json"

# The cfg values of each target Velopack builds the binaries for, by the .NET runtime identifier prefix they ship under.
TARGETS = {
    "win-": [
        {"triple": "i686-win7-windows-msvc", "target_arch": "x86", "target_vendor": "win7", "target_pointer_width": "32"},
        {"triple": "x86_64-win7-windows-msvc", "target_arch": "x86_64", "target_vendor": "win7", "target_pointer_width": "64"},
        {"triple": "aarch64-pc-windows-msvc", "target_arch": "aarch64", "target_vendor": "pc", "target_pointer_width": "64"},
    ],
    "osx-": [
        {"triple": "x86_64-apple-darwin", "target_arch": "x86_64", "target_vendor": "apple", "target_pointer_width": "64"},
        {"triple": "aarch64-apple-darwin", "target_arch": "aarch64", "target_vendor": "apple", "target_pointer_width": "64"},
    ],
    "linux-": [
        {"triple": "x86_64-unknown-linux-musl", "target_arch": "x86_64", "target_vendor": "unknown", "target_pointer_width": "64"},
        {"triple": "aarch64-unknown-linux-musl", "target_arch": "aarch64", "target_vendor": "unknown", "target_pointer_width": "64"},
    ],
}
OS = {
    "win-": {"target_os": "windows", "target_family": "windows", "target_env": "msvc", "flags": {"windows"}},
    "osx-": {"target_os": "macos", "target_family": "unix", "target_env": "", "flags": {"unix"}},
    "linux-": {"target_os": "linux", "target_family": "unix", "target_env": "musl", "flags": {"unix"}},
}
FEATURES = {"x86": {"fxsr", "sse", "sse2"}, "x86_64": {"fxsr", "sse", "sse2"}, "aarch64": {"neon"}}

# Licences ClearlyDefined has no data for, read by hand in the crate's Cargo.toml (`license`) where it is published.
LICENCES = {
    "derivative": ("MIT OR Apache-2.0", "https://github.com/mcarton/rust-derivative/blob/master/Cargo.toml"),
    "dyn-clone": ("MIT OR Apache-2.0", "https://github.com/dtolnay/dyn-clone/blob/master/Cargo.toml"),
    "enum-map": ("MIT OR Apache-2.0", "https://github.com/KamilaBorowska/enum-map/blob/master/enum-map/Cargo.toml"),
    "enum-map-derive": ("MIT OR Apache-2.0", "https://github.com/KamilaBorowska/enum-map/blob/master/enum-map-derive/Cargo.toml"),
    "lyon_geom": ("MIT OR Apache-2.0", "https://github.com/nical/lyon/blob/main/crates/geom/Cargo.toml"),
    "mina": ("MIT", "https://github.com/focustense/mina/blob/main/Cargo.toml"),
    "mina_core": ("MIT", "https://github.com/focustense/mina/blob/main/core/Cargo.toml"),
    "mina_macros": ("MIT", "https://github.com/focustense/mina/blob/main/macros/Cargo.toml"),
    "multiversion": ("MIT OR Apache-2.0", "https://github.com/calebzulawski/multiversion/blob/master/multiversion/Cargo.toml"),
    "multiversion-macros": ("MIT OR Apache-2.0", "https://github.com/calebzulawski/multiversion/blob/master/multiversion-macros/Cargo.toml"),
    "oneshot": ("MIT OR Apache-2.0", "https://github.com/faern/oneshot/blob/main/Cargo.toml"),
    "simple-stopwatch": ("MIT", "https://github.com/huwb/rust-simple-stopwatch/blob/master/Cargo.toml"),
    "target-features": ("MIT OR Apache-2.0", "https://github.com/calebzulawski/target-features/blob/master/target-features/Cargo.toml"),
    "widestring": ("MIT OR Apache-2.0", "https://github.com/VoidStarKat/widestring-rs/blob/main/Cargo.toml"),
}


def gh(path, raw=True):
    command = ["gh", "api", path] + (["-H", "Accept: application/vnd.github.raw"] if raw else [])
    # Several hundred calls: a network hiccup (a TLS handshake timeout) is retried, a 404 or a rate limit fails too.
    for _ in range(3):
        result = subprocess.run(command, capture_output=True, check=False)
        if result.returncode == 0:
            return result.stdout.decode()
    raise RuntimeError(f"gh api {path}: {result.stderr.decode(errors='replace').strip()}")


def target_values(runtime, target):
    values = {**OS[runtime], **target, "target_endian": "little", "panic": "unwind"}
    values["target_feature"] = FEATURES[target["target_arch"]] | {"crt-static"}
    values["target_has_atomic"] = {"8", "16", "32", "64", "ptr"}
    return values


def cfg_matches(expression, values):
    """Whether a Cargo target string (a triple or cfg(...)) holds for a target. Unknown cfgs are unset, as in a build."""
    expression = expression.strip()
    if not expression.startswith("cfg("):
        return expression == values["triple"]

    tokens = re.findall(r'"[^"]*"|[A-Za-z0-9_]+|[(),=]', expression[4:-1])
    position = 0

    def predicate():
        nonlocal position
        name = tokens[position]
        position += 1
        if name in ("all", "any", "not") and position < len(tokens) and tokens[position] == "(":
            position += 1
            results = []
            while tokens[position] != ")":
                results.append(predicate())
                if tokens[position] == ",":
                    position += 1
            position += 1
            return all(results) if name == "all" else any(results) if name == "any" else not results[0]
        if position < len(tokens) and tokens[position] == "=":
            value = tokens[position + 1].strip('"')
            position += 2
            actual = values.get(name)
            return value in actual if isinstance(actual, set) else actual == value
        return name in values["flags"]

    return predicate()


def index_path(name):
    name = name.lower()
    if len(name) <= 2:
        return f"{len(name)}/{name}"
    if len(name) == 3:
        return f"3/{name[0]}/{name}"
    return f"{name[:2]}/{name[2:4]}/{name}"


def nearest(versions, wanted):
    def key(version):
        return [int(part) if part.isdigit() else 0 for part in re.split(r"[.+-]", version)]

    return min(versions, key=lambda v: [abs(a - b) for a, b in zip(key(v), key(wanted))])


def main():
    if len(sys.argv) != 2:
        print("Usage: python deploy/third-party/velopack-crates.py <Velopack tag, e.g. 1.2.161>", file=sys.stderr)
        return 2

    tag = sys.argv[1]
    commit = json.loads(gh(f"repos/{REPOSITORY}/commits/{tag}", raw=False))["sha"]

    def contents(path):
        return gh(f"repos/{REPOSITORY}/contents/{path}?ref={commit}")

    lock = tomllib.loads(contents("Cargo.lock"))
    workspace = tomllib.loads(contents("Cargo.toml"))
    members = {}
    for member in workspace["workspace"]["members"]:
        manifest = tomllib.loads(contents(f"{member}/Cargo.toml"))
        members[manifest["package"]["name"]] = manifest

    packages = {}
    by_name = {}
    for package in lock["package"]:
        key = (package["name"], package["version"])
        packages[key] = package
        by_name.setdefault(package["name"], []).append(key)

    def resolve(dependency):
        parts = dependency.split(" ")
        candidates = by_name[parts[0]]
        return candidates[0] if len(parts) == 1 else next(k for k in candidates if k[1] == parts[1])

    def member_edges(manifest):
        """(dependency name, target or None) for each normal dependency of a workspace member."""
        edges = [(name, None) for name in manifest.get("dependencies", {})]
        for target, tables in manifest.get("target", {}).items():
            edges += [(name, target) for name in tables.get("dependencies", {})]
        return edges

    index_cache = {}

    def registry_edges(name, version):
        if name not in index_cache:
            lines = gh(f"repos/rust-lang/crates.io-index/contents/{index_path(name)}").splitlines()
            index_cache[name] = [json.loads(line) for line in lines if line]
        entry = next(e for e in index_cache[name] if e["vers"] == version)
        return [(d.get("package", d["name"]), d.get("target")) for d in entry["deps"] if d.get("kind", "normal") == "normal"]

    # Walk the graph once per target, from velopack_bins, along normal dependencies whose target matches.
    runtimes_of = {}
    for runtime, targets in TARGETS.items():
        for target in targets:
            values = target_values(runtime, target)
            seen = set()
            pending = [resolve("velopack_bins")]
            while pending:
                key = pending.pop()
                if key in seen:
                    continue
                seen.add(key)
                package = packages[key]
                edges = member_edges(members[key[0]]) if "source" not in package else registry_edges(*key)
                allowed = {name for name, condition in edges if condition is None or cfg_matches(condition, values)}
                for dependency in package.get("dependencies", []):
                    child = resolve(dependency)
                    if child[0] in allowed:
                        pending.append(child)
            for key in seen:
                if "source" in packages[key]:
                    runtimes_of.setdefault(key, set()).add(runtime)

    sbom = json.loads(gh(f"repos/{REPOSITORY}/dependency-graph/sbom", raw=False))["sbom"]
    metadata = {}
    for package in sbom["packages"]:
        for reference in package.get("externalRefs", []):
            match = re.fullmatch(r"pkg:cargo/([^@]+)@(.+)", reference.get("referenceLocator", ""))
            if match:
                metadata[(match[1], match[2])] = package

    crates = []
    for (name, version), runtimes in sorted(runtimes_of.items(), key=lambda item: (item[0][0].lower(), item[0][1])):
        source = (name, version)
        if source not in metadata:
            versions = [v for (n, v) in metadata if n == name]
            source = (name, nearest(versions, version)) if versions else None
        package = metadata.get(source, {}) if source else {}
        licence = package.get("licenseConcluded") or package.get("licenseDeclared")
        copyright = package.get("copyrightText")
        crate = {"name": name, "version": version}
        crate["licence"] = licence if licence and licence != "NOASSERTION" else None
        if copyright and copyright != "NOASSERTION":
            crate["copyright"] = copyright
        if source and source != (name, version):
            crate["metadataFrom"] = f"{source[0]} {source[1]}"
        if not crate["licence"] and name in LICENCES:
            crate["licence"], crate["licenceFrom"] = LICENCES[name]
        crate["runtimes"] = sorted(runtimes)
        crates.append(crate)

    data = {
        "$comment": [
            "Written by deploy/third-party/velopack-crates.py (see it for how): the crates.io crates statically linked",
            "into Velopack's native binaries at the tag below, with the runtime identifier prefixes they ship under.",
            "deploy/third-party-notices.cs lists them with the Velopack package of the same version. Licence and",
            "copyright are ClearlyDefined's, through GitHub's dependency graph ('metadataFrom' when that had another",
            "version of the crate), or the licence was read by hand ('licenceFrom', the LICENCES table in the script).",
            "A null licence is unknown: add the crate to that table, or the generator refuses the file.",
        ],
        "velopack": tag,
        "lock": f"https://github.com/{REPOSITORY}/blob/{commit}/Cargo.lock",
        "crates": crates,
    }
    OUTPUT.write_text(json.dumps(data, indent=2, ensure_ascii=False) + "\n", encoding="utf-8", newline="\n")
    unknown = [c["name"] for c in crates if not c["licence"]]
    print(f"{OUTPUT}: {len(crates)} crates" + (f"; no licence known for {', '.join(unknown)}" if unknown else ""))
    return 0


if __name__ == "__main__":
    sys.exit(main())
