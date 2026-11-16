#!/usr/bin/env bash
# Polls the reader every 4 s while Hearthstone runs and appends to the log only when what it
# reads changes. Usage: watch.sh <logfile>
EXE="$(dirname "$0")/bin/Release/MemSpike.exe"
LOG="$1"; last=""
echo "=== watch started $(date '+%F %T')" >> "$LOG"
while true; do
  if tasklist //FI "IMAGENAME eq Hearthstone.exe" 2>/dev/null | grep -qi hearthstone.exe; then
    now="$("$EXE" me 2>&1 | grep -v '^attach'; "$EXE" lobby 2>&1 | grep -v '^attach'; "$EXE" board 2>&1 | grep -v '^attach'; "$EXE" recent 2>&1 | grep -v '^attach')"
  else
    now="Hearthstone is not running."
  fi
  if [ "$now" != "$last" ]; then printf '\n--- %s\n%s\n' "$(date '+%T')" "$now" >> "$LOG"; last="$now"; fi
  sleep 4
done
