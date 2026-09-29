#!/bin/bash
set -euo pipefail

APP_PATH="${1:?App bundle path is required.}"
ARIA_EXECUTABLE="$APP_PATH/Contents/MacOS/aria2/aria2c"
PROBE_ROOT="$(mktemp -d "${TMPDIR:-/tmp}/downkyi-aria2-probe.XXXXXX")"
ARIA_PID=""

cleanup() {
  if [ -n "$ARIA_PID" ] && kill -0 "$ARIA_PID" 2>/dev/null; then
    kill -KILL "$ARIA_PID" 2>/dev/null || true
  fi
  if [ -n "$ARIA_PID" ]; then
    wait "$ARIA_PID" 2>/dev/null || true
  fi
  rm -rf -- "$PROBE_ROOT"
}
trap cleanup EXIT

fail() {
  echo "::error::aria2 RPC readiness invariant failed: $*" >&2
  if [ -f "$PROBE_ROOT/aria2.stderr" ]; then
    sed -E 's/token:[[:xdigit:]]+/token:[redacted]/g' "$PROBE_ROOT/aria2.stderr" >&2
  fi
  exit 1
}

if [ ! -x "$ARIA_EXECUTABLE" ]; then
  fail "bundled aria2 executable is missing or not executable: $ARIA_EXECUTABLE"
fi
if ! command -v python3 >/dev/null 2>&1; then
  fail "python3 is required for the deterministic RPC readiness probe."
fi

RPC_PORT="$(python3 - <<'PY'
import socket

with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as listener:
    listener.bind(("127.0.0.1", 0))
    print(listener.getsockname()[1])
PY
)"
RPC_SECRET="$(openssl rand -hex 32)"
RPC_CONFIG="$PROBE_ROOT/aria2.conf"
SESSION_FILE="$PROBE_ROOT/aria2.session"
VERSION_REQUEST="$PROBE_ROOT/version-request.json"
VERSION_RESPONSE="$PROBE_ROOT/version-response.json"
SHUTDOWN_REQUEST="$PROBE_ROOT/shutdown-request.json"
SHUTDOWN_RESPONSE="$PROBE_ROOT/shutdown-response.json"

umask 077
printf 'rpc-secret=%s\n' "$RPC_SECRET" >"$RPC_CONFIG"

# DHT is not compiled into every trusted aria2 build. When those options are
# available, keep their persistent state inside the disposable probe boundary.
ARIA_OPTION_HELP="$("$ARIA_EXECUTABLE" --help=#all 2>/dev/null || true)"
if grep -Fq -- '--dht-file-path=' <<<"$ARIA_OPTION_HELP"; then
  printf 'dht-file-path=%s\n' "$PROBE_ROOT/dht.dat" >>"$RPC_CONFIG"
fi
if grep -Fq -- '--dht-file-path6=' <<<"$ARIA_OPTION_HELP"; then
  printf 'dht-file-path6=%s\n' "$PROBE_ROOT/dht6.dat" >>"$RPC_CONFIG"
fi

: >"$SESSION_FILE"
mkdir "$PROBE_ROOT/downloads"
printf '{"jsonrpc":"2.0","id":"runtime-readiness","method":"aria2.getVersion","params":["token:%s"]}' \
  "$RPC_SECRET" >"$VERSION_REQUEST"
printf '{"jsonrpc":"2.0","id":"runtime-shutdown","method":"aria2.shutdown","params":["token:%s"]}' \
  "$RPC_SECRET" >"$SHUTDOWN_REQUEST"

"$ARIA_EXECUTABLE" \
  "--conf-path=$RPC_CONFIG" \
  --enable-rpc=true \
  --rpc-listen-all=false \
  --rpc-allow-origin-all=false \
  "--rpc-listen-port=$RPC_PORT" \
  "--stop-with-process=$$" \
  "--input-file=$SESSION_FILE" \
  "--save-session=$SESSION_FILE" \
  "--dir=$PROBE_ROOT/downloads" \
  --log-level=notice \
  >"$PROBE_ROOT/aria2.stdout" 2>"$PROBE_ROOT/aria2.stderr" &
ARIA_PID=$!

while true; do
  if ! kill -0 "$ARIA_PID" 2>/dev/null; then
    if wait "$ARIA_PID"; then
      status=0
    else
      status=$?
    fi
    ARIA_PID=""
    fail "aria2 exited before RPC became ready (status $status)."
  fi

  if curl --fail --silent --show-error \
      --header 'Content-Type: application/json' \
      --data-binary "@$VERSION_REQUEST" \
      "http://127.0.0.1:$RPC_PORT/jsonrpc" \
      >"$VERSION_RESPONSE" 2>/dev/null \
    && python3 - "$VERSION_RESPONSE" <<'PY'
import json
import sys

with open(sys.argv[1], "r", encoding="utf-8") as response_file:
    response = json.load(response_file)

result = response.get("result")
if not isinstance(result, dict) or not isinstance(result.get("version"), str):
    raise SystemExit(1)
features = result.get("enabledFeatures")
if not isinstance(features, list) or not all(isinstance(feature, str) for feature in features):
    raise SystemExit(1)
if "downkyi-secure-redirect-v2" not in features:
    raise SystemExit(1)
PY
  then
    break
  fi

  sleep 0.1
done

if ! curl --fail --silent --show-error \
    --header 'Content-Type: application/json' \
    --data-binary "@$SHUTDOWN_REQUEST" \
    "http://127.0.0.1:$RPC_PORT/jsonrpc" \
    >"$SHUTDOWN_RESPONSE"; then
  fail "aria2 shutdown RPC request failed."
fi

if ! python3 - "$SHUTDOWN_RESPONSE" <<'PY'
import json
import sys

with open(sys.argv[1], "r", encoding="utf-8") as response_file:
    response = json.load(response_file)

if response.get("result") != "OK":
    raise SystemExit(1)
PY
then
  fail "aria2 shutdown RPC did not return OK."
fi

if wait "$ARIA_PID"; then
  status=0
else
  status=$?
fi
ARIA_PID=""
if [ "$status" -ne 0 ]; then
  fail "aria2 exited abnormally after shutdown RPC (status $status)."
fi

echo "[INFO] Packaged aria2 passed RPC readiness, secure-redirect capability, and graceful shutdown checks."
