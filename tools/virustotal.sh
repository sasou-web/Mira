#!/usr/bin/env bash
# Sends a release's files to VirusTotal, so that anyone can look them up by their SHA-256 (wiki: Verify your download):
# virustotal.sh <file>... Needs VIRUSTOTAL_API_KEY (a free account's key, 500 requests a day); without it, says so and does
# nothing, the release goes on. Each file is public already (it sits in a GitHub release): nothing private leaves.
# Files over 32 MB go through an upload address VirusTotal gives first. A file already known there is just linked.
# Writes one line per file, and the links in the job's summary when GITHUB_STEP_SUMMARY is set.
set -euo pipefail
[ $# -gt 0 ] || { echo "no file to send" >&2; exit 1; }
if [ -z "${VIRUSTOTAL_API_KEY:-}" ]; then echo "::notice::VIRUSTOTAL_API_KEY is not set: files not sent to VirusTotal."; exit 0; fi
api=https://www.virustotal.com/api/v3
summary=${GITHUB_STEP_SUMMARY:-/dev/null}
echo "### VirusTotal" >> "$summary"
for file in "$@"; do
  name=$(basename "$file")
  sha=$(sha256sum "$file" | cut -d ' ' -f 1)
  link="https://www.virustotal.com/gui/file/$sha"
  # Known already (same bytes sent for an earlier attempt, or by someone else): no upload.
  code=$(curl -sS -o /dev/null -w '%{http_code}' -H "x-apikey: $VIRUSTOTAL_API_KEY" "$api/files/$sha" || echo 000)
  if [ "$code" = 200 ]; then echo "$name: already on VirusTotal, $link"; echo "- $name: already known, $link" >> "$summary"; continue; fi
  target="$api/files"
  if [ "$(stat -c %s "$file")" -gt $((32 * 1024 * 1024)) ]; then
    target=$(curl -sS -f -H "x-apikey: $VIRUSTOTAL_API_KEY" "$api/files/upload_url" | python3 -c 'import json, sys; print(json.load(sys.stdin)["data"])')
  fi
  sent=no
  for attempt in 1 2 3; do
    if curl -sS -f -o /dev/null -H "x-apikey: $VIRUSTOTAL_API_KEY" -F "file=@$file" "$target"; then sent=yes; break; fi
    echo "::warning::Sending $name to VirusTotal failed (attempt $attempt)." >&2
    sleep $((attempt * 10))
  done
  if [ "$sent" = yes ]; then echo "$name: sent, $link (the analysis takes a few minutes)"; echo "- $name: sent, $link" >> "$summary"
  else echo "::warning::$name could not be sent to VirusTotal; the release goes on." >&2; echo "- $name: not sent" >> "$summary"; fi
done
