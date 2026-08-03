#!/usr/bin/env bash
# NEGATIVE compile gate: Kotlin the compiler must REFUSE, and the diagnostic it must print.
#
# Every other lane asserts what compiles. Some behaviour is only expressible as a refusal — a value the CLR
# cannot store where the lowering would have to put it (a `ref struct` in a coroutine state machine or a closure
# class) has no valid CIL, so the compiler owes the author an actionable message instead of a TypeLoadException
# at run time. A silent miscompile and a wrong message are equally wrong here, so each case pins BOTH: a non-zero
# exit AND the substrings the diagnostic must contain.
#
# Layout — one case per `<name>.kt` with a companion `<name>.expected`:
#   <name>.kt         the source, compiled in isolation through the same kotc -> bir2cir -> ilemit pipeline
#   <name>.expected   substrings the combined stdout+stderr must ALL contain; blank lines and `#` lines are comments
#   <name>.cs         OPTIONAL — a .NET surface the case is refused against. All such surfaces are content-deduped,
#                     compiled into one plain C# library, and passed as `--ref`; a refusal ABOUT a foreign
#                     declaration has no Kotlin-only witness.
#   <name>.csref      OPTIONAL — the basename of another case's identical `.cs` fixture; use instead of copying it.
#
# Green (exit 0) iff every case is refused with its documented diagnostic. There is deliberately no XFAIL path:
# an accepted case or a changed/missing diagnostic is a regression and reddens the gate.
source "$(cd -- "$(dirname -- "$0")/../.." && pwd -P)/scripts/lib.sh"

HERE="$ROOT/tests/compile-fail"
work="$(mktemp -d)"; trap 'rm -rf "$work"' EXIT

failures=()
total=0

need_kotc
need_tool ilemit
need_tool bir2cir
need_tool dll2klib
need_fe_klib
need_dotnet_reference_sets
[[ -f "$STDLIB_REF_DLL" ]] || die "missing $STDLIB_REF_DLL — build it with: scripts/build-stdlib-ref.sh --emit"
[[ -f "$STDLIB_RT_DLL" ]]  || die "missing $STDLIB_RT_DLL — build it with: scripts/build-stdlib-rt.sh --emit"

# The foreign-surface cases used to build one tiny C# project per Kotlin verdict, including six byte-identical
# copies of just two fixtures. They are independent Kotlin verdicts, but they share one CLR reference graph. Build
# that graph once, content-deduping repeated sources so identical namespace/type declarations do not collide.
shared_ref=""
declare -a unique_cs=()
for cs in "$HERE"/*.cs; do
	[[ -e "$cs" ]] || continue
	duplicate=0
	for existing in "${unique_cs[@]}"; do
		if cmp -s "$cs" "$existing"; then duplicate=1; break; fi
	done
	(( duplicate == 1 )) || unique_cs+=("$cs")
done
if (( ${#unique_cs[@]} )); then
	csdir="$work/shared.ref"; mkdir -p "$csdir"
	for cs in "${unique_cs[@]}"; do cp "$cs" "$csdir/"; done
	cat > "$csdir/ref.csproj" <<'CSPROJ'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net10.0</TargetFramework><Nullable>disable</Nullable><ImplicitUsings>disable</ImplicitUsings><AssemblyName>CompileFailRef</AssemblyName></PropertyGroup>
</Project>
CSPROJ
	if ! (cd "$csdir" && dotnet build -c Release -o bin -v q --nologo >"$csdir/build.log" 2>&1); then
		echo "compile-fail: shared .NET fixture assembly did not build"
		sed 's/^/        /' "$csdir/build.log" | tail -20
		exit 1
	fi
	shared_ref="$csdir/bin/CompileFailRef.dll"
fi

# Project the immutable reference universe ONCE.  The old loop called scripts/dotkt.sh for every verdict, which
# converted the same full targeting pack to KLIBs for every case.  dll2klib's second invocation below is a warm
# extension of the same output directory: it keeps the framework projections and adds the one shared fixture DLL.
# Capture the base classpath before that extension so Kotlin-only cases do not see the foreign test declarations.
reference_klibs="$work/reference-klibs"; mkdir -p "$reference_klibs"
reference_rsp="$work/references.rsp"
printf '%s\n' "${FRAMEWORK_COMPILE_REF_PATHS[@]}" > "$reference_rsp"
reference_jobs="${DOTKT_DLL2KLIB_JOBS:-$(getconf _NPROCESSORS_ONLN 2>/dev/null || printf '1')}"
dotnet "$DLL2KLIB_DLL" --out "$reference_klibs" --jobs "$reference_jobs" @"$reference_rsp" >/dev/null

case "${OS:-}" in
	Windows_NT) klib_cp_sep=';' ;;
	*) klib_cp_sep=':' ;;
esac
reference_classpath() {
	local result="$FE_KLIB" klib
	while IFS= read -r klib; do result+="${result:+$klib_cp_sep}$klib"; done \
		< <(find "$reference_klibs" -maxdepth 1 -type f -name '*.klib' | LC_ALL=C sort)
	printf '%s' "$result"
}
base_classpath="$(reference_classpath)"
foreign_classpath="$base_classpath"
if [[ -n "$shared_ref" ]]; then
	printf '%s\n' "${FRAMEWORK_COMPILE_REF_PATHS[@]}" "$shared_ref" > "$reference_rsp"
	dotnet "$DLL2KLIB_DLL" --out "$reference_klibs" --jobs "$reference_jobs" @"$reference_rsp" >/dev/null
	foreign_classpath="$(reference_classpath)"
fi

base_bir_refs="$(refset_join "$FRAMEWORK_COMPILE_REFS" "$STDLIB_REF_DLL")"
foreign_bir_refs="$(refset_join "$FRAMEWORK_COMPILE_REFS" "$shared_ref" "$STDLIB_REF_DLL")"
base_emit_refs="$(refset_join "$FRAMEWORK_COMPILE_REFS" "$STDLIB_RT_DLL")"
foreign_emit_refs="$(refset_join "$FRAMEWORK_COMPILE_REFS" "$shared_ref" "$STDLIB_RT_DLL")"
base_runtime_refs="$STDLIB_RT_DLL"
foreign_runtime_refs="$(refset_join "$shared_ref" "$STDLIB_RT_DLL")"

# Validate the case manifests serially, then compile the valid cases in isolated work directories.  Four workers
# keep JVM/dotnet startup latency off the critical path without allowing the compiler processes to overwrite one
# another.  CI or a constrained machine can override the cap with DOTKT_COMPILE_FAIL_JOBS.
detected_jobs="$(getconf _NPROCESSORS_ONLN 2>/dev/null || printf '1')"
(( detected_jobs > 4 )) && detected_jobs=4
compile_jobs="${DOTKT_COMPILE_FAIL_JOBS:-$detected_jobs}"
[[ "$compile_jobs" =~ ^[1-9][0-9]*$ ]] || die "DOTKT_COMPILE_FAIL_JOBS must be a positive integer (got '$compile_jobs')"

declare -a cases=()
declare -A CASE_SETUP_ERROR=() CASE_USES_FOREIGN_REF=()
for kt in "$HERE"/*.kt; do
	[[ -e "$kt" ]] || continue
	name="$(basename "$kt" .kt)"
	exp="$HERE/$name.expected"
	(( ++total ))
	cases+=("$kt")
	if [[ ! -f "$exp" ]]; then
		CASE_SETUP_ERROR["$name"]="no $name.expected beside the case"
		continue
	fi

	# A case may name a .NET SURFACE it is refused against. Some refusals are ABOUT a foreign declaration — a .NET
	# member whose signature no Kotlin expression inhabits — so there is no Kotlin-only source that can witness it.
	# All companion sources were compiled into the shared reference above; the case still owns its exact witness.
	cs="$HERE/$name.cs"
	csref="$HERE/$name.csref"
	if [[ -f "$csref" ]]; then
		if [[ -f "$cs" ]]; then
			CASE_SETUP_ERROR["$name"]="has both $name.cs and $name.csref"
			continue
		fi
		IFS= read -r shared_name < "$csref" || shared_name=""
		if [[ -z "$shared_name" || "$shared_name" == */* || "$shared_name" != *.cs || ! -f "$HERE/$shared_name" ]]; then
			CASE_SETUP_ERROR["$name"]="invalid shared fixture in $name.csref: $shared_name"
			continue
		fi
		cs="$HERE/$shared_name"
	fi
	if [[ -f "$cs" ]]; then
		CASE_USES_FOREIGN_REF["$name"]=1
	fi
done

run_case() {
	local kt="$1" name case_dir classpath bir_refs emit_refs runtime_refs rc
	name="$(basename "$kt" .kt)"
	case_dir="$work/cases/$name"
	mkdir -p "$case_dir/bir" "$case_dir/cir" "$case_dir/il"
	classpath="$base_classpath"
	bir_refs="$base_bir_refs"
	emit_refs="$base_emit_refs"
	runtime_refs="$base_runtime_refs"
	if [[ -v CASE_USES_FOREIGN_REF[$name] ]]; then
		classpath="$foreign_classpath"
		bir_refs="$foreign_bir_refs"
		emit_refs="$foreign_emit_refs"
		runtime_refs="$foreign_runtime_refs"
	fi

	if "$KOTC" "$kt" -no-stdlib -classpath "$classpath" -d "$case_dir/bir" >"$case_dir/compiler.out" 2>&1; then
		if dotnet "$BIR2CIR_DLL" "$case_dir/cir" --compile-refs "$bir_refs" "$case_dir/bir"/*.bir.json \
			>>"$case_dir/compiler.out" 2>&1; then
			# No current case reaches ilemit, but retaining the final stage preserves the gate's meaning for a future
			# diagnostic owned by emission and makes an accidental full acceptance an exit-zero failure as before.
			if dotnet "$ILEMIT_DLL" "$case_dir/il" "$name" --compile-refs "$emit_refs" \
				--runtime-refs "$runtime_refs" --target-framework-moniker "$DOTKT_TARGET_FRAMEWORK_MONIKER" \
				--target-rid "" "$case_dir/cir"/*.cir.json >>"$case_dir/compiler.out" 2>&1; then
				rc=0
			else
				rc=$?
			fi
		else
			rc=$?
		fi
	else
		rc=$?
	fi
	printf '%s\n' "$rc" > "$case_dir/rc"
}

echo "compile-fail: running $total isolated case(s), jobs=$compile_jobs (reference KLIBs projected once)"
declare -a pids=()
for kt in "${cases[@]}"; do
	name="$(basename "$kt" .kt)"
	[[ -v CASE_SETUP_ERROR[$name] ]] && continue
	run_case "$kt" &
	pids+=("$!")
	if (( ${#pids[@]} == compile_jobs )); then
		for pid in "${pids[@]}"; do wait "$pid"; done
		pids=()
	fi
done
for pid in "${pids[@]}"; do wait "$pid"; done

for kt in "${cases[@]}"; do
	name="$(basename "$kt" .kt)"
	exp="$HERE/$name.expected"
	detail="${CASE_SETUP_ERROR[$name]-}"
	case_dir="$work/cases/$name"
	if [[ -z "$detail" ]]; then
		if [[ ! -f "$case_dir/rc" || ! -f "$case_dir/compiler.out" ]]; then
			detail="the isolated compiler worker did not produce a verdict"
		else
			rc="$(<"$case_dir/rc")"
			out="$(<"$case_dir/compiler.out")"
			if (( rc == 0 )); then
				detail="the compiler ACCEPTED it (exit 0); a refusal was expected"
			else
				while IFS= read -r want; do
					[[ -z "$want" || "$want" == \#* ]] && continue
					if [[ "$out" != *"$want"* ]]; then
						detail="diagnostic does not contain: $want"
						break
					fi
				done <"$exp"
			fi
		fi
	fi

	if [[ -z "$detail" ]]; then
		echo "PASS  $name (exit $rc, diagnostic as documented)"
	else
		echo "FAIL  $name — $detail"
		if [[ -f "$case_dir/compiler.out" ]]; then
			echo "----- compiler output -----"
			tail -20 "$case_dir/compiler.out"
			echo "---------------------------"
		fi
		failures+=("$name")
	fi
done

echo "=========================================================="
if (( total == 0 )); then
	echo "compile-fail: no cases found in tests/compile-fail — the lane would pass vacuously"
	exit 1
fi
if (( ${#failures[@]} )); then
	echo "compile-fail: RED — ${#failures[@]} case(s) failed: ${failures[*]}"
	exit 1
fi
echo "compile-fail: GREEN ($total case(s))"
