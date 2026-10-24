#!/usr/bin/env bash
# One command from WSL: update GitHub -> local BOOTH assets -> Unity headless scene save.
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd -P)"
archive=""
open_after=0
overwrite=0
for arg in "$@"; do
  case "$arg" in
    --open) open_after=1 ;;
    --overwrite) overwrite=1 ;;
    -h|--help)
      echo 'Usage: bash scripts/apply-nekobot.sh [path/to/Nekobot.zip] [--open] [--overwrite]'
      echo 'Close Unity Editor for this project before running this command.'
      echo 'You can omit the ZIP path if it is in Windows Downloads or repo root.'
      exit 0 ;;
    --*) echo "Unknown option: $arg" >&2; exit 2 ;;
    *) if [[ -n "$archive" ]]; then echo 'Expected one archive path' >&2; exit 2; fi; archive="$arg" ;;
  esac
done
if [[ -z "$archive" ]]; then
  for file in "$root/Nekobot.zip" "$HOME/Downloads/Nekobot.zip" /mnt/c/Users/*/Downloads/Nekobot.zip; do
    if [[ -f "$file" ]]; then archive="$file"; break; fi
  done
fi
if [[ -z "$archive" || ! -f "$archive" ]]; then
  echo '[Guardian] Nekobot.zip missing. Download it from the creator and place it in Windows Downloads.' >&2
  echo 'Example: bash scripts/apply-nekobot.sh --open' >&2
  exit 2
fi
for program in git python3 powershell.exe wslpath; do
  command -v "$program" >/dev/null 2>&1 || { echo "[Guardian] Missing: $program" >&2; exit 2; }
done
[[ "$(git -C "$root" rev-parse --show-toplevel 2>/dev/null)" == "$root" ]] || {
  echo '[Guardian] Please run from the cloned GitHub project' >&2; exit 2;
}
[[ "$(git -C "$root" branch --show-current)" == main ]] || {
  echo '[Guardian] Switch to main before applying to protect your work' >&2; exit 2;
}
echo '[Guardian] Pulling GitHub main (fast-forward only)'
git -C "$root" pull --ff-only origin main
echo '[Guardian] Importing BOOTH model locally; no asset uploaded to GitHub'
if [[ "$overwrite" == 1 ]]; then
  python3 "$root/scripts/import_nekobot.py" "$archive" --project "$root" --overwrite
else
  python3 "$root/scripts/import_nekobot.py" "$archive" --project "$root"
fi
project_win="$(wslpath -w "$root")"
script_win="$(wslpath -w "$root/scripts/Invoke-UnityNekobot.ps1")"
echo '[Guardian] Starting Unity 6000.3.12f1 batch mode'
if [[ "$open_after" == 1 ]]; then
  powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$script_win" -ProjectPath "$project_win" -OpenAfter
else
  powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$script_win" -ProjectPath "$project_win"
fi
echo '[Guardian] Finished: Assets/Scenes/GuardianRestaurant.unity'
