#!/usr/bin/env python3
"""Fills a configuration directory with an upgrade fixture (tests/fixtures/upgrade/<version>.zip), for the installer
tests: a home a released version set up and used. Every timestamp in its state database is moved forward so the newest
record is a minute old, as UpgradeFromReleasedVersionsTests does, so the usage page's default window (the last 7 days)
and retention still see it. Prints the number of usage records, for check-usage.py.

Usage: seed-home.py <fixture.zip> <configuration directory>
"""
import os
import sqlite3
import sys
import time
import zipfile

fixture, home = sys.argv[1], sys.argv[2]
os.makedirs(home, exist_ok=True)
with zipfile.ZipFile(fixture) as archive:
    archive.extractall(home)

database = sqlite3.connect(os.path.join(home, "semantic-router.db"))
newest = database.execute(
    "SELECT MAX(t) FROM (SELECT timestamp_ticks AS t FROM activity UNION ALL SELECT timestamp_ticks FROM usage)").fetchone()[0]
now = (int(time.time()) - 60 + 62135596800) * 10_000_000  # .NET ticks: 100 ns since 0001-01-01
shift = now - newest
for (table,) in database.execute("SELECT name FROM sqlite_master WHERE type = 'table'").fetchall():
    for column in database.execute(f'SELECT name FROM pragma_table_info("{table}")').fetchall():
        if column[0].endswith("_ticks"):
            database.execute(f'UPDATE "{table}" SET "{column[0]}" = "{column[0]}" + ?', (shift,))
database.commit()
print(database.execute("SELECT COUNT(*) FROM usage").fetchone()[0])
database.close()
