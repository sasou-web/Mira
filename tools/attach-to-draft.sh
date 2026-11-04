#!/usr/bin/env bash
# Attaches a release workflow's files to the draft tools/release.ps1 made: attach-to-draft.sh <vX.Y.Z> <commit> <file>...
# Only the draft made for this very commit, never a published release nor a draft made since for another commit.
# Safe to run again (a re-run of the job after a failure, a dropped connection): a file already there with the same
# SHA-256 is kept, any other copy of it (an earlier build of a re-run, an upload cut halfway) is replaced. Every call to
# GitHub is tried four times. tools/release.ps1 compares the draft's files, digest by digest, with the run it watched
# before publishing anything. Needs GH_TOKEN and GITHUB_REPOSITORY.
set -euo pipefail
release=$1 commit=$2
shift 2
[[ "$release" =~ ^v[0-9]+\.[0-9]+\.[0-9]+$ && "$commit" =~ ^[0-9a-f]{40}$ ]] || { echo "release or commit invalid" >&2; exit 1; }
[ $# -gt 0 ] || { echo "no file to attach" >&2; exit 1; }
repository=${GITHUB_REPOSITORY:?}

# Runs a command up to four times, 10, 20 then 30 s apart: GitHub's API and uploads sometimes drop a connection.
retry() {
  local attempt
  for attempt in 1 2 3 4; do
    "$@" && return 0
    [ "$attempt" = 4 ] && return 1
    echo "::warning::$1 $2 failed (attempt $attempt); trying again in $((attempt * 10)) s." >&2
    sleep $((attempt * 10))
  done
}

state=$(retry gh release view "$release" --repo "$repository" --json isDraft,targetCommitish --jq '[(.isDraft | tostring), .targetCommitish] | join(" ")')
[ "$state" = "true $commit" ] || { echo "$release is not a draft for $commit (found: $state)" >&2; exit 1; }
# A draft has no tag: its assets are reached through its API address.
api=$(retry gh release view "$release" --repo "$repository" --json apiUrl --jq .apiUrl)
api=${api#https://api.github.com/}

for file in "$@"; do
  name=$(basename "$file")
  digest="sha256:$(sha256sum "$file" | cut -d ' ' -f 1)"
  for attempt in 1 2 3 4; do
    # The copies of this name already on the draft: id, state and digest.
    existing=$(retry gh api "$api/assets?per_page=100" --jq ".[] | select(.name == \"$name\") | [.id, .state, .digest] | @tsv")
    kept=no
    while IFS=$'\t' read -r id assetState assetDigest; do
      [ -n "$id" ] || continue
      if [ "$assetState" = uploaded ] && [ "$assetDigest" = "$digest" ]; then kept=yes; continue; fi
      echo "Replacing $name already on the draft ($assetState, $assetDigest)."
      retry gh api -X DELETE "repos/$repository/releases/assets/$id"
    done <<< "$existing"
    if [ "$kept" = yes ]; then echo "$name: already on the draft, same SHA-256."; break; fi
    if gh release upload "$release" "$file" --repo "$repository"; then echo "$name: attached ($digest)."; break; fi
    [ "$attempt" = 4 ] && { echo "$name could not be attached to $release." >&2; exit 1; }
    echo "::warning::Uploading $name failed (attempt $attempt); trying again in $((attempt * 15)) s." >&2
    sleep $((attempt * 15))
  done
done
