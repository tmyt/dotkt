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
#   <name>.kt         the source, compiled standalone by scripts/dotkt.sh (the same pipeline the .ktproj lane drives)
#   <name>.expected   substrings the combined stdout+stderr must ALL contain; blank lines and `#` lines are comments
#   <name>.cs         OPTIONAL — a .NET surface the case is refused against, compiled to a plain C# library and
#                     passed as `--ref`. A refusal ABOUT a foreign declaration has no Kotlin-only witness.
#
# Green (exit 0) iff every case that fails is listed in CF_XFAIL below, with the same machine-readable
# "one reason per known failure" discipline as the other gates — the baseline is EMPTY, and a new failure or a
# newly-fixed XFAIL is reported as NEW-FAIL / FIXED rather than folded into a count.
source "$(cd -- "$(dirname -- "$0")/../.." && pwd -P)/scripts/lib.sh"

# Known-broken cases (substring key = the case name). EMPTY baseline: every case below must currently refuse
# with its documented message.
declare -A CF_XFAIL=()

HERE="$ROOT/tests/compile-fail"
work="$(mktemp -d)"; trap 'rm -rf "$work"' EXIT

declare -a NEW_FAILS=() FIXED=()
total=0

for kt in "$HERE"/*.kt; do
	[[ -e "$kt" ]] || continue
	name="$(basename "$kt" .kt)"
	exp="$HERE/$name.expected"
	(( ++total ))
	if [[ ! -f "$exp" ]]; then
		echo "FAIL  $name — no $name.expected beside the case"
		NEW_FAILS+=("$name"); continue
	fi

	# A case may name a .NET SURFACE it is refused against: `<name>.cs` beside it is compiled to a plain C#
	# library and passed as a reference. Some refusals are ABOUT a foreign declaration — a .NET member whose
	# signature no Kotlin expression inhabits — so there is no Kotlin-only source that can witness them, and a
	# refusal with no witness is a claim rather than a behaviour. The project is generated here rather than checked
	# in: the case owns one .cs file and nothing else.
	declare -a refargs=()
	cs="$HERE/$name.cs"
	if [[ -f "$cs" ]]; then
		csdir="$work/$name.ref"; mkdir -p "$csdir"
		cp "$cs" "$csdir/"
		cat > "$csdir/ref.csproj" <<'CSPROJ'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net10.0</TargetFramework><Nullable>disable</Nullable><ImplicitUsings>disable</ImplicitUsings><AssemblyName>CompileFailRef</AssemblyName></PropertyGroup>
</Project>
CSPROJ
		if ! (cd "$csdir" && dotnet build -c Release -o bin -v q --nologo >"$csdir/build.log" 2>&1); then
			echo "FAIL  $name — the case's .NET surface ($name.cs) did not build"
			sed 's/^/        /' "$csdir/build.log" | tail -20
			NEW_FAILS+=("$name"); continue
		fi
		refargs=(--ref "$csdir/bin/CompileFailRef.dll")
	fi

	out="$(bash "$ROOT/scripts/dotkt.sh" ${refargs[@]+"${refargs[@]}"} "$kt" -d "$work/$name" 2>&1)" && rc=0 || rc=$?

	detail=""
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

	if [[ -z "$detail" ]]; then
		if [[ -v CF_XFAIL[$name] ]]; then
			echo "FIXED $name — now refuses as documented; remove it from the CF_XFAIL baseline"
			FIXED+=("$name")
		else
			echo "PASS  $name (exit $rc, diagnostic as documented)"
		fi
	elif [[ -v CF_XFAIL[$name] ]]; then
		echo "XFAIL $name (${CF_XFAIL[$name]})"
	else
		echo "FAIL  $name — $detail"
		echo "----- compiler output -----"
		echo "$out" | tail -20
		echo "---------------------------"
		NEW_FAILS+=("$name")
	fi
done

echo "=========================================================="
if (( total == 0 )); then
	echo "compile-fail: no cases found in tests/compile-fail — the lane would pass vacuously"
	exit 1
fi
if (( ${#NEW_FAILS[@]} )); then
	echo "compile-fail: RED — ${#NEW_FAILS[@]} case(s) outside the CF_XFAIL baseline: ${NEW_FAILS[*]}"
	exit 1
fi
echo "compile-fail: GREEN ($total case(s)${FIXED[0]:+, ${#FIXED[@]} newly FIXED})"
