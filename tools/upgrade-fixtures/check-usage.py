#!/usr/bin/env python3
"""Checks that a running router still has a seeded fixture's usage (seed-home.py printed the count): its usage page
reports at least that many requests in the last 7 days. Waits up to 90 s for the router to answer.

Usage: check-usage.py <router url> <expected requests> <what, for the messages>
"""
import json
import sys
import time
import urllib.error
import urllib.request

url, expected, what = sys.argv[1].rstrip("/"), int(sys.argv[2]), sys.argv[3]
deadline = time.time() + 90
last = None
while time.time() < deadline:
    try:
        with urllib.request.urlopen(url + "/router/api/usage", timeout=10) as response:
            totals = json.load(response)["totals"]
        if totals["requests"] >= expected:
            print(f"{what}: {totals['requests']} usage records, as before ({expected})")
            sys.exit(0)
        last = f"{totals['requests']} usage records, expected at least {expected}"
    except (urllib.error.URLError, OSError, KeyError, ValueError) as error:
        last = str(error)
    time.sleep(2)
print(f"::error::{what}: {last}")
sys.exit(1)
