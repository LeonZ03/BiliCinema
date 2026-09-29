#!/bin/bash
set -euo pipefail

APP_PATH="${1:?App bundle path is required.}"
PLIST_PATH="$APP_PATH/Contents/Info.plist"
LOG_FILE="$(mktemp "${TMPDIR:-/tmp}/downkyi-app-loader.XXXXXX")"
DATA_DIRECTORY="$(mktemp -d "${TMPDIR:-/tmp}/downkyi-app-loader-data.XXXXXX")"
PID=""

force_stop_process() {
  if [ -n "$PID" ] && kill -0 "$PID" 2>/dev/null; then
    kill -KILL "$PID" 2>/dev/null || true
  fi
  if [ -n "$PID" ]; then
    wait "$PID" 2>/dev/null || true
    PID=""
  fi
}

cleanup() {
  force_stop_process
  rm -f -- "$LOG_FILE"
  rm -rf -- "$DATA_DIRECTORY"
}
trap cleanup EXIT

resolve_file_path() {
  local path="$1"
  local directory
  local target

  while [ -L "$path" ]; do
    directory="$(cd "$(dirname "$path")" && pwd -P)"
    target="$(readlink "$path")"
    case "$target" in
      /*) path="$target" ;;
      *) path="$directory/$target" ;;
    esac
  done

  directory="$(cd "$(dirname "$path")" && pwd -P)"
  printf '%s/%s\n' "$directory" "$(basename "$path")"
}

if [ ! -f "$PLIST_PATH" ]; then
  echo "::error::App bundle Info.plist is missing: $PLIST_PATH" >&2
  exit 1
fi

EXECUTABLE_NAME="$(/usr/libexec/PlistBuddy -c 'Print :CFBundleExecutable' "$PLIST_PATH")"
EXECUTABLE="$APP_PATH/Contents/MacOS/$EXECUTABLE_NAME"
CORECLR="$APP_PATH/Contents/MacOS/libcoreclr.dylib"

if [ ! -x "$EXECUTABLE" ]; then
  echo "::error::Packaged app executable is missing or not executable: $EXECUTABLE" >&2
  exit 1
fi
if [ ! -e "$CORECLR" ]; then
  echo "::error::Packaged .NET runtime is missing libcoreclr.dylib: $CORECLR" >&2
  exit 1
fi
if [ ! -x /usr/sbin/lsof ]; then
  echo "::error::macOS lsof is required to observe the loaded runtime image." >&2
  exit 1
fi

RESOLVED_CORECLR="$(resolve_file_path "$CORECLR")"
DOWNKYI_DATA_DIR="$DATA_DIRECTORY" "$EXECUTABLE" >"$LOG_FILE" 2>&1 &
PID=$!

while true; do
  if ! kill -0 "$PID" 2>/dev/null; then
    if wait "$PID"; then
      status=0
    else
      status=$?
    fi
    PID=""
    echo "::error::Packaged app loader exited before mapping bundled libcoreclr.dylib (status $status)." >&2
    cat "$LOG_FILE" >&2
    exit 1
  fi

  if /usr/sbin/lsof -a -p "$PID" -Fn 2>/dev/null | grep -Fxq "n$RESOLVED_CORECLR"; then
    break
  fi

  sleep 0.05
done

force_stop_process
echo "[INFO] Packaged app loader mapped bundled libcoreclr.dylib from $RESOLVED_CORECLR."
