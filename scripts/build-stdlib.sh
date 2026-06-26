#!/usr/bin/env bash
# Build a REAL slice of the Kotlin standard library (the vendored source under runtime/stdlib/src) into
# DotKt.Stdlib.dll with DotKt's own toolchain. This is the first step of Path B (docs/design-stdlib-compilation.md):
# compile real Kotlin stdlib source instead of hand-lowering it, growing the slice as compiler support lands.
#
# Mode: the builtins (Int/String/Enum/collections) come from the kotlin-stdlib.jar for FRONTEND resolution (no
# -Xbuiltins-from-sources, which hits the multi-layer builtins bootstrap); we compile the stdlib files whose dependency
# closure is entirely builtins + .NET-mappable (no uncompiled Kotlin stdlib classes), so ilemit can emit them.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
L="$ROOT/toolchain/kotc/build/install/kotc/bin/kotc"
JAR="$ROOT/toolchain/kotc/vendor/kotlin-stdlib.jar"
RT="$ROOT/build/dotkt-runtime/DotKt.Runtime.dll"
SRC="$ROOT/runtime/stdlib/src/kotlin"
FLAGS="-no-stdlib -classpath $JAR -Xallow-kotlin-package -opt-in=kotlin.contracts.ExperimentalContracts -opt-in=kotlin.ExperimentalMultiplatform"

# Self-contained files (compile clean + closure is builtins/.NET only). Grows as more of the platform layer is bound.
FILES="$SRC/collections/IndexedValue.kt $SRC/util/KotlinVersion.kt $SRC/text/Typography.kt $SRC/internal/Annotations.kt"
FILES="$FILES $(find $SRC/annotations -name '*.kt' ! -name 'Native*.kt')"
FILES="$FILES $(find $SRC/experimental -name '*.kt' ! -name '*ObjC*' ! -name '*Native*')"
FILES="$FILES $SRC/uuid/ExperimentalUuidApi.kt $SRC/time/ExperimentalTime.kt $SRC/io/encoding/ExperimentalEncodingApi.kt $SRC/contextParameters/ExperimentalContextParameters.kt"

BIR="$ROOT/build/stdlib-bir"; CIR="$ROOT/build/stdlib-cir"; DLL="$ROOT/build/stdlib-dll"; rm -rf "$BIR" "$CIR" "$DLL"; mkdir -p "$BIR" "$CIR" "$DLL"
echo "== kotc: $(echo $FILES | wc -w) stdlib files -> BIR =="
"$L" $FILES $FLAGS -d "$BIR"
echo "== bir2cir: BIR -> CIR =="
[ -f "$ROOT/build/bir2cir-bin/bir2cir.dll" ] || dotnet build "$ROOT/toolchain/bir2cir" -c Release -o "$ROOT/build/bir2cir-bin" -v q --nologo >/dev/null
dotnet "$ROOT/build/bir2cir-bin/bir2cir.dll" "$CIR" --ref "$RT" "$BIR"/*.bir.json
echo "== ilemit: CIR -> DotKt.Stdlib.dll =="
dotnet "$ROOT/build/ilemit-bin/ilemit.dll" "$DLL" DotKt.Stdlib --ref "$RT" "$CIR"/*.cir.json
echo "== built: $DLL/DotKt.Stdlib.dll =="
ls -la "$DLL/DotKt.Stdlib.dll"
