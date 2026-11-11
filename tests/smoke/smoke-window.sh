#!/usr/bin/env bash
# Real-window smoke test (not headless): starts the app for real, waits, takes a screenshot of the screen,
# checks that the process is still alive and that Portly's own log has no ERROR line, then stops it.
#   smoke-window.sh <program> <screenshot.png> <linux|mac>
# Linux: run it inside xvfb-run (needs ImageMagick "import"). macOS: needs the GUI session of the runner.
set -u
PROGRAM="${1:?program}"; SHOT="${2:?screenshot}"; KIND="${3:?linux|mac}"
export PORTLY_HOME="${PORTLY_HOME:-$(mktemp -d)}"
mkdir -p "$(dirname "$SHOT")" "$PORTLY_HOME"
fail=0

"$PROGRAM" > "$PORTLY_HOME/stdout.txt" 2>&1 &
PID=$!
echo "started $PROGRAM as pid $PID, data in $PORTLY_HOME"

sleep 25
if kill -0 "$PID" 2>/dev/null; then echo "PASS process alive after 25 s"; else echo "FAIL process died early"; fail=1; fi

if [ "$KIND" = "linux" ]; then
  import -window root "$SHOT" 2>"$PORTLY_HOME/shot-error.txt" || xwd -root -silent 2>/dev/null | convert xwd:- "$SHOT" || true
else
  screencapture -x "$SHOT" 2>"$PORTLY_HOME/shot-error.txt" || true
fi
if [ -s "$SHOT" ]; then echo "PASS screenshot $(wc -c < "$SHOT") bytes"; else echo "FAIL no screenshot"; cat "$PORTLY_HOME/shot-error.txt" 2>/dev/null; fail=1; fi

sleep 5
if kill -0 "$PID" 2>/dev/null; then echo "PASS process alive after 30 s"; else echo "FAIL process died"; fail=1; fi

LOG="$PORTLY_HOME/logs/portly.log"
if [ -f "$LOG" ]; then
  echo "---- portly.log"; cat "$LOG"; echo "----"
  if grep -q "ERROR" "$LOG"; then echo "FAIL ERROR line in portly.log"; grep "ERROR" "$LOG"; fail=1; else echo "PASS no ERROR in portly.log"; fi
else
  echo "FAIL no portly.log written (the window code never ran its start-up)"; fail=1
fi
echo "---- stdout/stderr"; head -50 "$PORTLY_HOME/stdout.txt"; echo "----"

kill "$PID" 2>/dev/null; sleep 2; kill -9 "$PID" 2>/dev/null
exit $fail
