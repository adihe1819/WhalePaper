#!/usr/bin/env bash
set -euo pipefail

case "$(uname -s)" in
  MINGW*|MSYS*|CYGWIN*) ;;
  *)
    printf '%s\n' 'WhalePaper requires Windows. Run setup.ps1 in PowerShell or this script in Windows Git Bash.' >&2
    exit 1
    ;;
esac

script_dir="$(cd -- "$(dirname -- "$0")" && pwd)"
cd -- "$script_dir"

# Use an existing Windows PowerShell runtime; never download or install tools.
if command -v pwsh.exe >/dev/null 2>&1; then
  exec pwsh.exe -NoProfile -File ./setup.ps1 "$@"
elif command -v powershell.exe >/dev/null 2>&1; then
  exec powershell.exe -NoProfile -File ./setup.ps1 "$@"
else
  printf '%s\n' 'PowerShell was not found. Open Windows PowerShell and run .\setup.ps1.' >&2
  exit 1
fi

