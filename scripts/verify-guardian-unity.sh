#!/usr/bin/env bash
set -euo pipefail

project_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
unity_path="${GUARDIAN_UNITY_PATH:-}"

if [[ -z "$unity_path" && "$(uname -s)" == "Darwin" ]]; then
  unity_path="/Applications/Unity/Hub/Editor/6000.3.12f1/Unity.app/Contents/MacOS/Unity"
fi

if [[ -z "$unity_path" || ! -x "$unity_path" ]]; then
  printf '%s\n' 'Set GUARDIAN_UNITY_PATH to the Unity 6000.3.12f1 executable.' >&2
  exit 2
fi

mkdir -p "$project_root/Logs"
log_path="$project_root/Logs/guardian-integration.log"
rm -f -- "$log_path"
set +e
"$unity_path" -batchmode -nographics -quit \
  -projectPath "$project_root" \
  -executeMethod Guardian.UnitySim.Editor.GuardianIntegrationBatch.Verify \
  -logFile "$log_path"
unity_status=$?
set -e

if [[ -f "$log_path" ]]; then
  cat "$log_path"
fi
if [[ $unity_status -ne 0 ]]; then
  exit "$unity_status"
fi
if [[ ! -f "$log_path" ]]; then
  printf 'Unity did not create its integration log: %s\n' "$log_path" >&2
  exit 1
fi
grep -Fq '[Guardian] GUARDIAN_BATCH_OK: integration' "$log_path"
printf '%s\n' '[Guardian] Licensed Unity integration verification passed.'
