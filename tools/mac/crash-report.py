"""Prints the crashed thread of the newest macOS crash report of a process (``python3 crash-report.py Mira``).

A crash report (``~/Library/Logs/DiagnosticReports/<name>-<date>.ips``) is a JSON header line, then the report in JSON.
"""
import glob
import json
import os
import sys

name = sys.argv[1]
reports = sorted(glob.glob(os.path.expanduser(f"~/Library/Logs/DiagnosticReports/{name}*.ips")), key=os.path.getmtime)
if not reports:
    print(f"no crash report for {name}")
    sys.exit(0)
with open(reports[-1], encoding="utf-8") as file:
    file.readline()
    report = json.load(file)
print(reports[-1])
print(report.get("exception"), report.get("termination", {}).get("indicator"))
images = report.get("usedImages", [])
for thread in report.get("threads", []):
    if not thread.get("triggered"):
        continue
    print("thread", thread.get("name") or thread.get("queue") or thread.get("id"))
    for frame in thread.get("frames", [])[:40]:
        image = images[frame["imageIndex"]] if frame.get("imageIndex", -1) < len(images) else {}
        print(f"  {image.get('name', '?'):<40} {frame.get('symbol', hex(frame.get('imageOffset', 0)))}")
