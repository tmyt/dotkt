#!/usr/bin/env bash
# Build the tracked first-party DotKt.Stdlib (runtime/DotKt.Stdlib/src) into build/dotkt-stdlib/DotKt.Stdlib.dll with
# DotKt's own toolchain. This holds the real-Kotlin stdlib ops that have been migrated off the hand-written
# COLLECTION_OPS lowering. Auto-referenced by .ktproj builds + the verify harnesses (see KotlinClr.targets / verify-*.sh).
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
L="$ROOT/toolchain/kotc/build/install/kotc/bin/kotc"
JAR="$ROOT/toolchain/kotc/vendor/kotlin-stdlib.jar"
RT="$ROOT/build/dotkt-runtime/DotKt.Runtime.dll"
REFPACK="$(dirname "$(find /usr/share/dotnet/packs/Microsoft.NETCore.App.Ref -name 'System.Runtime.dll' -path '*net10.0*' | head -1)")"
REFS="$(ls "$REFPACK"/*.dll | tr '\n' ';')"
SRC="$ROOT/runtime/DotKt.Stdlib/src"
OUT="$ROOT/build/dotkt-stdlib"; BIR="$ROOT/build/dotkt-stdlib-bir"; CIR="$ROOT/build/dotkt-stdlib-cir"
rm -rf "$OUT" "$BIR" "$CIR"; mkdir -p "$OUT" "$BIR" "$CIR"

[ -f "$RT" ] || dotnet build "$ROOT/runtime/DotKt.Runtime" -c Release -o "$ROOT/build/dotkt-runtime" -v q --nologo >/dev/null 2>&1
echo "== kotc: DotKt.Stdlib -> BIR =="
CLR_TYPES_METADATA="" "$L" "$SRC" -no-stdlib -classpath "$JAR" -Xallow-kotlin-package -d "$BIR"
echo "== bir2cir: BIR -> CIR =="
[ -f "$ROOT/build/bir2cir-bin/bir2cir.dll" ] || dotnet build "$ROOT/toolchain/bir2cir" -c Release -o "$ROOT/build/bir2cir-bin" -v q --nologo >/dev/null
dotnet "$ROOT/build/bir2cir-bin/bir2cir.dll" "$CIR" --ref "$RT" "$BIR"/*.bir.json
echo "== ilemit: CIR -> DotKt.Stdlib.dll =="
dotnet "$ROOT/build/ilemit-bin/ilemit.dll" "$OUT" DotKt.Stdlib --ref "$RT" "$CIR"/*.cir.json
echo "== retarget: repoint CoreLib refs (so facadegen can read it back) =="
dotnet "$ROOT/build/retarget-bin/retarget.dll" "$OUT/DotKt.Stdlib.dll" --refs "$REFS$RT"
echo "== built: $OUT/DotKt.Stdlib.dll =="
ls -la "$OUT/DotKt.Stdlib.dll"
