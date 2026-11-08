#!/usr/bin/env bash
# The reports of main's last successful run of this workflow, to compare a pull request's numbers with
# (tools/perf/budget.mjs --baseline): the artifact <name> of that run, unpacked into <folder>. Nothing found (an
# artifact expired, a new name) leaves the folder empty: budget.mjs then says there is no comparison.
#   tools/perf/main-reports.sh <artifact name> <folder>
# Environment: GH_TOKEN (the job's token, with actions: read), GITHUB_REPOSITORY, GITHUB_WORKFLOW_REF.
set -euo pipefail
name=$1 folder=$2
mkdir -p "$folder"
workflow=$(basename "${GITHUB_WORKFLOW_REF%%@*}")
run=$(gh run list --repo "$GITHUB_REPOSITORY" --workflow "$workflow" --branch main --event push --status success --limit 1 --json databaseId --jq '.[0].databaseId // empty')
if [ -z "$run" ]; then echo "No successful run of $workflow on main."; exit 0; fi
echo "main's run $run ($workflow): artifact $name"
gh run download "$run" --repo "$GITHUB_REPOSITORY" --name "$name" --dir "$folder" || echo "No artifact $name in run $run."
find "$folder" -name '*.json' | sort
