#!/usr/bin/env python3
"""Checks that the egress list `doctor` prints names every host the router's downloads reach.

Follows the redirect chains of the pinned ONNX model files (OnnxEmbeddingModels.cs) and of the latest release's assets,
the way the router and the desktop updater do, and fails when a hop lands on a host that neither
OnnxEmbeddingModels.DownloadHosts nor EgressList.UpdateHosts names. Run by .github/workflows/egress-hosts.yml.

Usage: tools/check-egress-hosts.py [owner/repo]
"""

import fnmatch
import json
import os
import re
import sys
import urllib.error
import urllib.parse
import urllib.request

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
MODELS = os.path.join(ROOT, "src", "SemanticRouter.Embeddings.Onnx", "OnnxEmbeddingModels.cs")
EGRESS = os.path.join(ROOT, "src", "SemanticRouter.Hosting", "EgressList.cs")
HOST = re.compile(r"(?:\*\.)?[a-z0-9-]+(?:\.[a-z0-9-]+)+", re.IGNORECASE)


def constant(path, name):
    match = re.search(rf'const string {name} = "([^"]*)"', open(path, encoding="utf-8").read())
    if not match:
        sys.exit(f"{name} not found in {path}")
    return match.group(1)


def hosts(text):
    return [h.lower() for h in HOST.findall(text)]


def model_urls():
    source = open(MODELS, encoding="utf-8").read()
    urls = []
    # Matches the object initializers (Repository = "...", Revision = "...", ModelFile = "...") in that order.
    pinned = r'Repository = "([\w.-]+/[\w.-]+)",\s*Revision = "([0-9a-f]{40})",\s*ModelFile = "([\w./-]+\.onnx)"'
    for repository, revision, model_file in re.findall(pinned, source):
        base = f"https://huggingface.co/{repository}/resolve/{revision}/"
        urls += [base + model_file, base + "vocab.txt"]
    if not urls:
        sys.exit(f"No pinned models found in {MODELS}")
    return urls


def release_urls(repository):
    api = f"https://api.github.com/repos/{repository}/releases/latest"
    request = urllib.request.Request(api, headers={"Accept": "application/vnd.github+json", "User-Agent": "egress-check"})
    token = os.environ.get("GITHUB_TOKEN")
    if token:
        request.add_header("Authorization", f"Bearer {token}")
    try:
        with urllib.request.urlopen(request, timeout=30) as response:
            assets = json.load(response).get("assets", [])
    except urllib.error.HTTPError as error:
        if error.code == 404:
            print(f"{repository} has no release yet; skipping the update hosts.")
            return [api]
        raise
    # One asset of each kind is enough: they all come from the same download hosts.
    return [api] + [a["browser_download_url"] for a in assets[:2]]


class Recorder(urllib.request.HTTPRedirectHandler):
    def __init__(self):
        self.hops = []

    def redirect_request(self, req, fp, code, msg, headers, newurl):
        self.hops.append(newurl)
        return super().redirect_request(req, fp, code, msg, headers, newurl)


def chain(url):
    recorder = Recorder()
    opener = urllib.request.build_opener(recorder)
    # One byte is enough to see where the file is served from.
    request = urllib.request.Request(url, headers={"Range": "bytes=0-0", "User-Agent": "egress-check"})
    with opener.open(request, timeout=30):
        pass
    return [url] + recorder.hops


def main():
    repository = sys.argv[1] if len(sys.argv) > 1 else os.environ.get("GITHUB_REPOSITORY", "siebe41/SemanticRouter.NET")
    allowed = hosts(constant(MODELS, "DownloadHosts")) + hosts(constant(EGRESS, "UpdateHosts"))
    print("Documented hosts: " + ", ".join(allowed))

    missing = {}
    for url in model_urls() + release_urls(repository):
        for hop in chain(url):
            host = (urllib.parse.urlsplit(hop).hostname or "").lower()
            print(f"  {host:45} {hop.split('?')[0][:100]}")
            if not any(fnmatch.fnmatch(host, pattern) for pattern in allowed):
                missing.setdefault(host, url)

    if missing:
        for host, url in missing.items():
            print(f"::error::{url} reaches {host}, which the egress list does not name. Add it to "
                  "OnnxEmbeddingModels.DownloadHosts or EgressList.UpdateHosts, and to docs/network.md.")
        return 1
    print("Every host reached is in the egress list.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
