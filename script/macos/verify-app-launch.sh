#!/bin/bash
set -euo pipefail

APP_NAME="${1:-哔哩下载姬.app}"
EXECUTABLE_NAME="${MACOS_EXECUTABLE_NAME:-DownKyi}"
EXECUTABLE="$APP_NAME/Contents/MacOS/$EXECUTABLE_NAME"
LOG_FILE="$(mktemp "${TMPDIR:-/tmp}/downkyi-app-launch.XXXXXX")"
DATA_DIRECTORY="$(mktemp -d "${TMPDIR:-/tmp}/downkyi-app-launch-data.XXXXXX")"
PID=""

terminate_process() {
  kill -TERM "$PID" 2>/dev/null || true
  if kill -0 "$PID" 2>/dev/null; then
    kill -KILL "$PID" 2>/dev/null || true
  fi

  wait "$PID" 2>/dev/null || true
}

cleanup() {
  if [ -n "$PID" ] && kill -0 "$PID" 2>/dev/null; then
    terminate_process
  fi
  rm -f "$LOG_FILE"
  rm -rf "$DATA_DIRECTORY"
}
trap cleanup EXIT

if [ ! -x "$EXECUTABLE" ]; then
  echo "::error::Packaged app executable is missing or not executable: $EXECUTABLE" >&2
  exit 1
fi

DOWNKYI_DATA_DIR="$DATA_DIRECTORY" "$EXECUTABLE" >"$LOG_FILE" 2>&1 &
PID=$!

while true; do
  marker="$(find "$DATA_DIRECTORY/Logs" -type f -name events.jsonl \
    -exec grep -Fl 'Application initialized.' {} + 2>/dev/null | head -n 1 || true)"
  if [ -n "$marker" ]; then
    if ! kill -0 "$PID" 2>/dev/null; then
      wait "$PID" || status=$?
      echo "::error::Packaged app exited after writing the application initialization marker (status ${status:-0})." >&2
      cat "$LOG_FILE" >&2
      exit 1
    fi

    break
  fi

  if ! kill -0 "$PID" 2>/dev/null; then
    wait "$PID" || status=$?
    echo "::error::Packaged app exited before the application initialization marker (status ${status:-0})." >&2
    cat "$LOG_FILE" >&2
    exit 1
  fi

  sleep 0.05
done

terminate_process
PID=""
echo "[INFO] Packaged app emitted the application initialization marker."
