#!/bin/bash
set -euo pipefail
NUGET="$HOME/.nuget/packages/quicknv.hikvisionisupsdk.native/1.0.3/runtimes/linux-x64/native"
OUT="${1:-}"
if [ -z "$OUT" ]; then
  OUT="$(cd "$(dirname "$0")/.." && pwd)/bin/Release/net9.0/runtimes/linux-x64/native"
fi
mkdir -p "$OUT"
cp -a "$NUGET/libssl.so" "$OUT/libssl.so"
cp -a "$NUGET/libcrypto.so" "$OUT/libcrypto.so"
cp -a "$NUGET/libssl.so.1.0.0" "$OUT/libssl.so.1.0.0"
cp -a "$NUGET/libcrypto.so.1.0.0" "$OUT/libcrypto.so.1.0.0"
ln -sfn libssl.so.1.0.0 "$OUT/libssl.so.10"
ln -sfn libcrypto.so.1.0.0 "$OUT/libcrypto.so.10"
echo "OpenSSL shim copied to $OUT"
