#!/usr/bin/env bash
# dotkt — compile Kotlin (.kt) to a .NET assembly with the DotKt toolchain (kotc -> BIR -> CIR -> ilemit -> CIL), from the
# command line. A thin dev wrapper over the same pipeline the MSBuild targets / verify scripts drive, for quick
# one-shot builds (handy while iterating on DotKt.Stdlib or trying a snippet).
#
#   scripts/dotkt.sh [options] <file.kt | dir>...
#
# Options:
#   -o <name>       output assembly name           (default: derived from the first source, else 'app')
#   -d <dir>        output directory               (default: ./dotkt-out)
#   --exe           produce a runnable assembly     (writes <name>.runtimeconfig.json; implied by --run)
#   --run           build, then run it              (implies --exe)
#   --ref <dll>     add a compile/emit reference    (repeatable; e.g. a NuGet/BCL dll or another DotKt assembly)
#   --no-stdlib     do NOT reference DotKt.Stdlib   (the migrated real-Kotlin collection ops)
#   --retarget      repoint BCL refs off System.Private.CoreLib (so a C# project can <Reference> the output)
#   -h | --help     this help
#
# .NET interop: `import System.X` in the sources is resolved automatically (the kotc PSI import scan + facadegen, the
# same C-2 path the .ktproj uses), so no facade boilerplate is needed.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
KOTC="$ROOT/toolchain/kotc/build/install/kotc/bin/kotc"
ILEMIT="$ROOT/build/ilemit-bin/ilemit.dll"
BIR2CIR="$ROOT/build/bir2cir-bin/bir2cir.dll"
FACADEGEN="$ROOT/build/facadegen-bin/facadegen.dll"
RETARGET="$ROOT/build/retarget-bin/retarget.dll"
DOTKT_RT="$ROOT/build/dotkt-runtime/DotKt.Runtime.dll"
DOTKT_STDLIB="$ROOT/build/dotkt-stdlib/DotKt.Stdlib.dll"
JAR="$ROOT/toolchain/kotc/vendor/kotlin-stdlib.jar"
CORO="$(find "$HOME/.gradle/caches" -name 'kotlinx-coroutines-core-jvm-*.jar' 2>/dev/null | head -1)"

# --- args ---------------------------------------------------------------------------------------------------------
out_name=""; out_dir="$PWD/dotkt-out"; make_exe=0; do_run=0; use_stdlib=1; do_retarget=0
declare -a srcs=() extra_refs=()
while (( $# )); do
	case "$1" in
		-o) out_name="$2"; shift 2 ;;
		-d) out_dir="$2"; shift 2 ;;
		--exe) make_exe=1; shift ;;
		--run) do_run=1; make_exe=1; shift ;;
		--ref) extra_refs+=("$2"); shift 2 ;;
		--no-stdlib) use_stdlib=0; shift ;;
		--retarget) do_retarget=1; shift ;;
		-h|--help) sed -n '2,30p' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
		-*) echo "dotkt: unknown option '$1'" >&2; exit 2 ;;
		*) srcs+=("$1"); shift ;;
	esac
done
(( ${#srcs[@]} )) || { echo "dotkt: no .kt sources given (try -h)" >&2; exit 2; }

# Expand directories to their .kt files; collect the flat source list.
declare -a kts=()
for s in "${srcs[@]}"; do
	if [[ -d "$s" ]]; then while IFS= read -r f; do kts+=("$f"); done < <(find "$s" -name '*.kt'); else kts+=("$s"); fi
done
(( ${#kts[@]} )) || { echo "dotkt: no .kt files found in: ${srcs[*]}" >&2; exit 2; }
[[ -n "$out_name" ]] || { base="$(basename "${kts[0]}" .kt)"; out_name="${base^}"; [[ "$out_name" =~ Kt$ ]] || out_name="${out_name}Kt"; }

# --- bootstrap the toolchain if missing --------------------------------------------------------------------------
[[ -x "$KOTC" ]] || { echo "dotkt: building kotc..." >&2; (cd "$ROOT" && ./gradlew -q :kotc:installDist); }
[[ -f "$ILEMIT" ]] || { echo "dotkt: building ilemit..." >&2; dotnet build "$ROOT/toolchain/ilemit" -c Release -o "$ROOT/build/ilemit-bin" -v q --nologo; }
[[ -f "$BIR2CIR" ]] || { echo "dotkt: building bir2cir..." >&2; dotnet build "$ROOT/toolchain/bir2cir" -c Release -o "$ROOT/build/bir2cir-bin" -v q --nologo; }
[[ -f "$FACADEGEN" ]] || { echo "dotkt: building facadegen..." >&2; dotnet build "$ROOT/toolchain/facadegen" -c Release -o "$ROOT/build/facadegen-bin" -v q --nologo; }
[[ -f "$DOTKT_RT" ]] || { echo "dotkt: building DotKt.Runtime..." >&2; dotnet build "$ROOT/runtime/DotKt.Runtime" -c Release -o "$ROOT/build/dotkt-runtime" -v q --nologo; }
if (( use_stdlib )) && [[ ! -f "$DOTKT_STDLIB" ]]; then echo "dotkt: building DotKt.Stdlib..." >&2; bash "$ROOT/scripts/build-dotkt-stdlib.sh" >/dev/null; fi
if (( do_retarget )) && [[ ! -f "$RETARGET" ]]; then dotnet build "$ROOT/toolchain/retarget" -c Release -o "$ROOT/build/retarget-bin" -v q --nologo; fi

work="$(mktemp -d)"; trap 'rm -rf "$work"' EXIT
bir="$work/bir"; cir="$work/cir"; mkdir -p "$bir" "$cir" "$out_dir"
cp="$JAR"; [[ -n "$CORO" ]] && cp="$cp:$CORO"

# Reference assemblies (compile-resolution refs for facadegen + ilemit). The runtime ref-pack lets facadegen reflect
# BCL types; DotKt.Runtime carries the [Kotlin*] attributes; DotKt.Stdlib carries the migrated real-Kotlin ops.
refpack="$(dirname "$(find /usr/share/dotnet/packs/Microsoft.NETCore.App.Ref -name 'System.Runtime.dll' -path '*net10.0*' 2>/dev/null | head -1)")"
declare -a ilref_args=(--ref "$DOTKT_RT")
refs_semi="$(ls "$refpack"/*.dll 2>/dev/null | tr '\n' ';')$DOTKT_RT"
(( use_stdlib )) && { ilref_args+=(--ref "$DOTKT_STDLIB"); refs_semi="$refs_semi;$DOTKT_STDLIB"; }
for r in "${extra_refs[@]}"; do ilref_args+=(--ref "$r"); refs_semi="$refs_semi;$r"; done

# 1. .NET type injection: scan the sources' imports (PSI) + pull in DotKt.Stdlib's facades wholesale.
meta="$work/clrtypes.meta"; implist="$work/imports.txt"
"$KOTC" --scan-imports --output "$implist" "${kts[@]}" >/dev/null 2>&1 || true
scan_asm=(); (( use_stdlib )) && scan_asm=(--scan-asm "$DOTKT_STDLIB")
dotnet "$FACADEGEN" --meta "$meta" --refs "$refs_semi" "${scan_asm[@]}" --import-list "$implist" >/dev/null 2>&1 || true

# 2. kotc: .kt -> BIR.
echo "dotkt: compiling ${#kts[@]} file(s) -> BIR" >&2
CLR_TYPES_METADATA="$meta" "$KOTC" "${kts[@]}" -no-stdlib -classpath "$cp" -d "$bir"

# 3. bir2cir: BIR -> CIR.
echo "dotkt: lowering BIR -> CIR" >&2
dotnet "$BIR2CIR" "$cir" "${ilref_args[@]}" "$bir"/*.bir.json >/dev/null

# 4. ilemit: CIR -> CIL.
echo "dotkt: emitting $out_name.dll" >&2
dotnet "$ILEMIT" "$out_dir" "$out_name" "${ilref_args[@]}" "$cir"/*.cir.json

# 5. optional retarget (for compile-time C# <Reference>).
(( do_retarget )) && dotnet "$RETARGET" "$out_dir/$out_name.dll" --refs "$refs_semi" >/dev/null

# 6. exe scaffolding: copy copy-local refs + write a runtimeconfig so `dotnet <name>.dll` runs.
if (( make_exe )); then
	cp "$DOTKT_RT" "$out_dir/" 2>/dev/null || true
	(( use_stdlib )) && cp "$DOTKT_STDLIB" "$out_dir/" 2>/dev/null || true
	for r in "${extra_refs[@]}"; do cp "$r" "$out_dir/" 2>/dev/null || true; done
	cat > "$out_dir/$out_name.runtimeconfig.json" <<JSON
{"runtimeOptions":{"tfm":"net10.0","framework":{"name":"Microsoft.NETCore.App","version":"10.0.0"}}}
JSON
fi

echo "dotkt: built $out_dir/$out_name.dll"
if (( do_run )); then echo "----"; ( cd "$out_dir" && dotnet "$out_name.dll" ); fi
