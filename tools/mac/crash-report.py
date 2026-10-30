"""Prints the crashed thread of a crash of a process (``python3 crash-report.py Mira [crashreport.json]``).

macOS's crash report (``~/Library/Logs/DiagnosticReports/<name>-<date>.ips``: a JSON header line, then the report in
JSON) gives the native frames; .NET's (``DOTNET_EnableCrashReport=1``: ``<dump>.crashreport.json``) the managed ones too.
"""
import glob
import json
import os
import sys

name = sys.argv[1]
reports = sorted(glob.glob(os.path.expanduser(f"~/Library/Logs/DiagnosticReports/{name}*.ips")), key=os.path.getmtime)
if reports:
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
else:
    print(f"no macOS crash report for {name}")

if len(sys.argv) > 2:
    if not os.path.exists(sys.argv[2]):
        print(f"no .NET crash report at {sys.argv[2]}")
        sys.exit(0)
    with open(sys.argv[2], encoding="utf-8") as file:
        payload = json.load(file).get("payload", {})
    print(".NET:", payload.get("ExceptionType"), payload.get("signal") or "")
    for thread in payload.get("threads", []):
        if str(thread.get("crashed")).lower() != "true":
            continue
        print("thread", thread.get("native_thread_id"), thread.get("managed_exception_type") or "")
        for frame in thread.get("stack_frames", [])[:60]:
            method = frame.get("method_name") or frame.get("unmanaged_name") or frame.get("native_address")
            print(f"  {frame.get('module_name', '?'):<40} {method}")
