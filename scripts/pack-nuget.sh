#!/usr/bin/env bash
# Build the FIVE DotKt NuGet packages into the local feed (build/nuget-feed):
#   DotKt.Sdk       — the MSBuild SDK (Sdk.props/targets; implicit refs to Toolchain + Stdlib)
#   DotKt.Sdk.Mpp   — thin composition SDK: base DotKt.Sdk + DotKtMultiplatform=true (expect/actual)
#   DotKt.Toolchain — the compiler: kotc + bir2cir + ilemit + facadegen + retarget + the CLR frontend
#                     stdlib KLIB + the COMPILE-TIME stdlib reference assembly (tools/stdlib/DotKt.Private.Stdlib.dll)
#   DotKt.Stdlib    — the RUNTIME stdlib assembly (lib/net10.0/DotKt.Stdlib.dll, copy-local)
#   DotKt.Templates — `dotnet new` templates
# (There is no separate runtime package.) Version is single-sourced in
# packaging/DotKt.Versions.props (imported by every pack .csproj). Orchestrated by `make pack` (which
# builds the prerequisites first); standalone it builds what's missing. Output: build/nuget-feed (wiped).
source "$(dirname "$0")/lib.sh"

usage() {
	cat <<EOF
usage: $SCRIPT_NAME
Packs the 5 DotKt NuGet packages into build/nuget-feed (no flags). -h for this help.
EOF
}
while (( $# )); do
	case "$1" in
		-h|--help) usage; exit 0 ;;
		*) usage_error "unknown argument '$1'" ;;
	esac
done

FEED="$ROOT/build/nuget-feed"; rm -rf "$FEED"; mkdir -p "$FEED"

# GUARD (#131): the SDK Sdk.props hardcode a DotKtVersion default (copied verbatim into the package; nuspec
# $version$ does NOT reach it) that pins the implicit Toolchain/Stdlib PackageReferences. A stale value silently
# pulls an OLD toolchain (0.9.5 shipped pulling 0.9.3). Refuse to pack a mismatch — bump Sdk.props with the release.
VERPREFIX="$(grep -oE '<DotKtVersionPrefix>[^<]+' "$ROOT/packaging/DotKt.Versions.props" | sed 's/.*>//')"
VERSUFFIX="$(grep -oE '<DotKtVersionSuffix>[^<]*' "$ROOT/packaging/DotKt.Versions.props" | sed 's/.*>//')"
VERCORE="$VERPREFIX"; [[ -n "$VERSUFFIX" ]] && VERCORE="$VERPREFIX-$VERSUFFIX"
KOTLINVER="$(grep -oE '<DotKtKotlinVersion>[^<]+' "$ROOT/packaging/DotKt.Versions.props" | sed 's/.*>//')"
[[ -n "$KOTLINVER" ]] || die "could not read DotKtKotlinVersion from packaging/DotKt.Versions.props"
for sp in packaging/DotKt.Sdk/Sdk/Sdk.props packaging/DotKt.Sdk.Mpp/Sdk/Sdk.props; do
	sv="$(grep -oE "<DotKtVersion Condition[^>]*>[^<]+" "$ROOT/$sp" | sed 's/.*>//')"
	[[ "$sv" == "$VERCORE" ]] || die "$sp DotKtVersion default ($sv) != release version core ($VERCORE) — bump it (else the SDK ships pulling a stale toolchain, GitHub #131)"
done

# GUARD (#53): the version/Kotlin-version strings scattered across templates, docs, and nuspec tags drift after a
# release/Kotlin bump because they were hardcoded and ungated. Everything single-sources off DotKt.Versions.props;
# refuse to pack a mismatch. (Template Sdk pin + nuspec kotlin tag are SUBSTITUTED below/at pack; here we assert
# the substitution tokens survive and the un-substitutable DOC fragments are current.)
# (a) The `dotnet new` template project file must keep the DOTKT_SDK_VERSION placeholder (substituted at pack time).
TPL_CSPROJ="packaging/DotKt.Templates/content/dotkt-cli/DotKtApp.csproj"
grep -q 'DotKt\.Sdk/DOTKT_SDK_VERSION' "$ROOT/$TPL_CSPROJ" || die "$TPL_CSPROJ lost its 'DotKt.Sdk/DOTKT_SDK_VERSION' placeholder — restore it (it is substituted to the release version at pack time, GitHub #53)"
# (b) The nuspec kotlin tag must be the substitution token, never a hardcoded kotlin-<ver>.
for ns in packaging/DotKt.Toolchain/DotKt.Toolchain.nuspec packaging/DotKt.Stdlib/DotKt.Stdlib.nuspec packaging/DotKt.Sdk/DotKt.Sdk.nuspec packaging/DotKt.Sdk.Mpp/DotKt.Sdk.Mpp.nuspec; do
	grep -q 'kotlin-\$kotlinVersion\$' "$ROOT/$ns" || die "$ns: kotlin tag must be 'kotlin-\$kotlinVersion\$' (nuspec-substituted from DotKtKotlinVersion), not a hardcoded version (GitHub #53)"
	if grep -qE 'kotlin-[0-9]' "$ROOT/$ns"; then die "$ns: hardcoded 'kotlin-<ver>' tag — use 'kotlin-\$kotlinVersion\$' (GitHub #53)"; fi
done
# (c) The doc `DotKt.Sdk/<ver>` examples cannot be substituted (docs are not packed) — they must match VERCORE.
for doc in README.md docs/user/getting-started.md; do
	grep -q "DotKt\.Sdk/$VERCORE" "$ROOT/$doc" || die "$doc: no 'DotKt.Sdk/$VERCORE' example — its SDK-version fragment drifted from the release core ($VERCORE); bump it (GitHub #53)"
	stale="$(grep -oE "DotKt\.Sdk/[0-9][^\"< )]*" "$ROOT/$doc" | grep -vFx "DotKt.Sdk/$VERCORE" || true)"
	[[ -z "$stale" ]] || die "$doc: stale SDK-version fragment(s) [$stale] — expected DotKt.Sdk/$VERCORE (GitHub #53)"
done

info "build compiler (installDist) + tools"
( cd "$ROOT" && ./gradlew -q :kotc:installDist )
dotnet build "$ROOT/toolchain/ilemit"    -c Release -o "$ROOT/build/ilemit-bin"    -v q --nologo
dotnet build "$ROOT/toolchain/bir2cir"   -c Release -o "$ROOT/build/bir2cir-bin"   -v q --nologo
dotnet build "$ROOT/toolchain/facadegen" -c Release -o "$ROOT/build/facadegen-bin" -v q --nologo
dotnet build "$ROOT/toolchain/retarget"  -c Release -o "$ROOT/build/retarget-bin"  -v q --nologo

# The CLR FRONTEND stdlib KLIB (kotc -classpath): built FROM our CLR stdlib sources for the common frontend.
info "ensure CLR frontend stdlib klib"
[[ -e "$FE_KLIB" ]] || bash "$ROOT/scripts/build-stdlib-klib.sh"

# The CLR stdlib dll pair — REQUIRED package contents (the shipped DotKt.Toolchain.targets needs both: the ref feeds
# bir2cir's @ClrTypeAlias/@ClrIntrinsic substitution, the rt is the app's copy-local runtime). Build if missing;
# the build scripts exit nonzero themselves when the dll is not produced (no compensating '|| true' any more).
info "ensure CLR stdlib (ref + rt)"
[[ -f "$STDLIB_REF_DLL" ]] || bash "$ROOT/scripts/build-stdlib-ref.sh" --emit
[[ -f "$STDLIB_RT_DLL"  ]] || bash "$ROOT/scripts/build-stdlib-rt.sh" --emit
[[ -f "$STDLIB_REF_DLL" ]] || die "missing $STDLIB_REF_DLL (scripts/build-stdlib-ref.sh --emit failed?)"
[[ -f "$STDLIB_RT_DLL"  ]] || die "missing $STDLIB_RT_DLL (scripts/build-stdlib-rt.sh --emit failed?)"

info "assemble DotKt.Toolchain/tools"
TC="$ROOT/packaging/DotKt.Toolchain/tools"; rm -rf "$TC"; mkdir -p "$TC"
cp -r "$ROOT/toolchain/kotc/build/install/kotc" "$TC/kotc"
cp -r "$FE_KLIB" "$TC/kotlin-stdlib-clr-frontend.klib"
cp -r "$ROOT/build/ilemit-bin"    "$TC/ilemit"
cp -r "$ROOT/build/bir2cir-bin"   "$TC/bir2cir"
cp -r "$ROOT/build/facadegen-bin" "$TC/facadegen"
cp -r "$ROOT/build/retarget-bin"  "$TC/retarget"
# The compile-time stdlib REFERENCE assembly rides with the compiler (DotKt.Toolchain.props points
# $(DotKtStdlibRefAsm) here; Sdk.props turns it into a non-copy <Reference>).
mkdir -p "$TC/stdlib"
cp "$STDLIB_REF_DLL" "$TC/stdlib/DotKt.Private.Stdlib.dll"

info "assemble DotKt.Stdlib/lib"
SL="$ROOT/packaging/DotKt.Stdlib/lib/net10.0"; rm -rf "$ROOT/packaging/DotKt.Stdlib/lib"; mkdir -p "$SL"
cp "$STDLIB_RT_DLL" "$SL/DotKt.Stdlib.dll"

# Stage the templates with the release SDK version substituted into the `dotnet new` project file (single-sourced
# from DotKt.Versions.props) so the generated project pins the shipping version — never a drifting hardcode (#53).
# Staged as a SIBLING of a copied DotKt.Versions.props so the csproj's `../DotKt.Versions.props` import still resolves.
info "stage DotKt.Templates (substitute Sdk version $VERCORE)"
TPLSTAGE="$ROOT/build/templates-staged"; rm -rf "$TPLSTAGE"; mkdir -p "$TPLSTAGE/DotKt.Templates"
cp "$ROOT/packaging/DotKt.Versions.props" "$TPLSTAGE/DotKt.Versions.props"
cp -r "$ROOT/packaging/DotKt.Templates/." "$TPLSTAGE/DotKt.Templates/"
sed -i "s|DotKt\.Sdk/DOTKT_SDK_VERSION|DotKt.Sdk/$VERCORE|g" "$TPLSTAGE/DotKt.Templates/content/dotkt-cli/DotKtApp.csproj"

info "pack DotKt.Toolchain + DotKt.Sdk + DotKt.Sdk.Mpp + DotKt.Stdlib + DotKt.Templates"
dotnet pack "$ROOT/packaging/DotKt.Toolchain/DotKt.Toolchain.pack.csproj" -o "$FEED" -v q --nologo
dotnet pack "$ROOT/packaging/DotKt.Sdk/DotKt.Sdk.pack.csproj" -o "$FEED" -v q --nologo
dotnet pack "$ROOT/packaging/DotKt.Sdk.Mpp/DotKt.Sdk.Mpp.pack.csproj" -o "$FEED" -v q --nologo
dotnet pack "$ROOT/packaging/DotKt.Stdlib/DotKt.Stdlib.pack.csproj" -o "$FEED" -v q --nologo
dotnet pack "$TPLSTAGE/DotKt.Templates/DotKt.Templates.csproj" -o "$FEED" -v q --nologo

info "feed:"; ls -1 "$FEED"
