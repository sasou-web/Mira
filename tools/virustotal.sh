#!/usr/bin/env bash
# Sends a release's files to VirusTotal, so that anyone can look them up by their SHA-256 (wiki: Verify your download):
# virustotal.sh <file>... Needs VIRUSTOTAL_API_KEY (a free account's key); without it, says so and does nothing.
# Each file is public already (it sits in a GitHub release): nothing private leaves.
# Nothing here ever fails the job: whatever VirusTotal answers, or doesn't, ends in a warning and exit 0, and the
# workflows run it after « Attach to the draft » with continue-on-error anyway (it once came first and could stop the
# release before its files were attached: audit of 2026-11-09, point 5).
# The free API takes 4 requests a minute: one request every VIRUSTOTAL_PACE seconds (16 by default; 0 in the tests),
# a « too many requests » (429) waits a whole minute. Every request has its time limit (--max-time).
# Files over 32 MB go through an upload address VirusTotal gives first. A file already known there is just linked.
# Writes one line per file, and the links in the job's summary when GITHUB_STEP_SUMMARY is set.
# Tests: node --test tools/virustotal.test.mjs (a fake curl).
set -uo pipefail
[ $# -gt 0 ] || { echo "::warning::virustotal.sh: no file to send." >&2; exit 0; }
if [ -z "${VIRUSTOTAL_API_KEY:-}" ]; then echo "::notice::VIRUSTOTAL_API_KEY is not set: files not sent to VirusTotal."; exit 0; fi
api=https://www.virustotal.com/api/v3
pace=${VIRUSTOTAL_PACE:-16}
summary=${GITHUB_STEP_SUMMARY:-/dev/null}
body=$(mktemp)
trap 'rm -f "$body"' EXIT
last=0
code=000

# request <max seconds> <curl arguments>...: the HTTP code in $code (000 when nothing came back), the body in $body.
# Never called in $(…): the pace needs $last kept between calls.
request() {
  local limit=$1; shift
  local now wait
  now=$(date +%s)
  wait=$((last + pace - now))
  [ "$wait" -gt 0 ] && sleep "$wait"
  last=$(date +%s)
  : > "$body"
  code=$(curl -sS -o "$body" -w '%{http_code}' --connect-timeout 20 --max-time "$limit" -H "x-apikey: $VIRUSTOTAL_API_KEY" "$@" 2>/dev/null)
  [[ "$code" =~ ^[0-9]{3}$ ]] || code=000
}

# with_retries <max seconds> <curl arguments>...: up to three tries, the last HTTP code in $code; « too many requests » waits a minute first.
with_retries() {
  local attempt
  for attempt in 1 2 3; do
    request "$@"
    case "$code" in
      2??|404) return ;;
      429) [ "$attempt" -lt 3 ] && sleep $((pace * 4)) ;;
      *) [ "$attempt" -lt 3 ] && sleep $((pace * attempt)) ;;
    esac
  done
}

echo "### VirusTotal" >> "$summary"
for file in "$@"; do
  name=$(basename "$file")
  if [ ! -f "$file" ]; then echo "::warning::$file: no such file, not sent to VirusTotal." >&2; echo "- $name: missing" >> "$summary"; continue; fi
  sha=$(sha256sum "$file" | cut -d ' ' -f 1)
  link="https://www.virustotal.com/gui/file/$sha"
  # Known already (same bytes sent for an earlier attempt, or by someone else): no upload.
  with_retries 60 "$api/files/$sha"
  if [ "$code" = 200 ]; then echo "$name: already on VirusTotal, $link"; echo "- $name: already known, $link" >> "$summary"; continue; fi
  target="$api/files"
  if [ "$(stat -c %s "$file")" -gt $((32 * 1024 * 1024)) ]; then
    target=""
    with_retries 60 "$api/files/upload_url"
    if [ "$code" = 200 ]; then
      target=$(python3 -c 'import json, sys; print(json.load(open(sys.argv[1]))["data"])' "$body" 2>/dev/null || true)
    fi
    case "$target" in
      https://*) ;;
      *) echo "::warning::VirusTotal gave no upload address for $name (HTTP $code): not sent; the release goes on." >&2
         echo "- $name: not sent (no upload address)" >> "$summary"; continue ;;
    esac
  fi
  with_retries 300 -F "file=@$file" "$target"
  case "$code" in
    2??) echo "$name: sent, $link (the analysis takes a few minutes)"; echo "- $name: sent, $link" >> "$summary" ;;
    *) echo "::warning::$name could not be sent to VirusTotal (HTTP $code); the release goes on." >&2
       echo "- $name: not sent (HTTP $code)" >> "$summary" ;;
  esac
done
exit 0
