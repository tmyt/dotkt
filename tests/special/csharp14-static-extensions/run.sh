#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../../.." && pwd)"
OUT="$ROOT/build/csharp14-static-extensions"
rm -rf "$OUT"
mkdir -p "$OUT"

dotnet build "$ROOT/tests/special/csharp14-static-extensions/consumer/StaticExtensionConsumer.csproj" \
    -c Release --no-incremental -v:q --nologo

PRODUCER="$ROOT/tests/special/csharp14-static-extensions/producer/bin/Release/net10.0/StaticExtensionProducer.dll"
CONSUMER="$ROOT/tests/special/csharp14-static-extensions/consumer/bin/Release/net10.0/StaticExtensionConsumer.dll"

actual="$(dotnet "$CONSUMER")"
[[ "$actual" == "csharp14-static-extensions" ]] \
    || { echo "C# 14 static extension consumer returned '$actual'"; exit 1; }

dotnet run --project "$ROOT/tests/special/csharp14-static-extensions/inspector/StaticExtensionInspector.csproj" \
    -c Release -- "$PRODUCER" "$CONSUMER"
bash "$ROOT/tests/run-ilverify.sh" "$PRODUCER" "$CONSUMER"

negative_log="$OUT/same-container-collision.log"
if dotnet build "$ROOT/tests/special/csharp14-static-extensions/negative/SameContainerCollision.csproj" \
    -c Release -v:minimal --nologo >"$negative_log" 2>&1; then
    echo "C# accepted receiverless same-name implementations in one extension container"
    exit 1
fi
grep -q 'error CS0111' "$negative_log" \
    || { echo "same-container collision failed without CS0111"; tail -20 "$negative_log"; exit 1; }

echo "PASS  released C# 14 static extension-member ABI golden fixture"
