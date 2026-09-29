#!/bin/bash
set -euo pipefail

APP_PATH="${1:-哔哩下载姬.app}"

codesign --verify --deep --strict --verbose=2 "$APP_PATH"
codesign -dv --verbose=4 "$APP_PATH"

if [ "${MACOS_VERIFY_GATEKEEPER:-false}" = "true" ]; then
  spctl --assess --type execute --verbose=4 "$APP_PATH"
fi
