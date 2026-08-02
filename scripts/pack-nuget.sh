#!/usr/bin/env bash
# Build the FIVE DotKt NuGet packages into the local feed (build/nuget-feed):
#   DotKt.Sdk       — the MSBuild SDK (Sdk.props/targets; implicit refs to Toolchain + Stdlib)
#   DotKt.Sdk.Mpp   — thin composition SDK: base DotKt.Sdk + DotKtMultiplatform=true (expect/actual)
#   DotKt.Toolchain — the compiler: kotc + bir2cir + ilemit + dll2klib + retarget + the CLR frontend
#                     stdlib KLIB + the COMPILE-TIME stdlib reference assembly (tools/stdlib/DotKt.Private.Stdlib.dll)
#   DotKt.Stdlib    — the RUNTIME stdlib assembly (lib/net10.0/DotKt.Stdlib.dll, copy-local)
#   DotKt.Templates — `dotnet new` templates
# (There is no separate runtime package.) Version is single-sourced in
# packaging/DotKt.Versions.props (imported by every pack .csproj). Orchestrated by `make pack` (which
# builds the prerequisites first); standalone it builds what is missing OR stale (fingerprint-aware need_*,
# so a klib/stdlib baked by an older toolchain is never shipped, #106). Output: build/nuget-feed (wiped).
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
# $version$ does NOT reach it) that pins the implicit Toolchain/Stdlib PackageReferences. The repository-root
# global.json also pins the custom SDK versions for every static test project. A stale value silently pulls an OLD
# package set (0.9.5 shipped pulling 0.9.3). Refuse to pack a mismatch — bump both surfaces with the release.
VERPREFIX="$(grep -oE '<DotKtVersionPrefix>[^<]+' "$ROOT/packaging/DotKt.Versions.props" | sed 's/.*>//')"
VERSUFFIX="$(grep -oE '<DotKtVersionSuffix>[^<]*' "$ROOT/packaging/DotKt.Versions.props" | sed 's/.*>//')"
VERCORE="$VERPREFIX"; [[ -n "$VERSUFFIX" ]] && VERCORE="$VERPREFIX-$VERSUFFIX"
KOTLINVER="$(grep -oE '<DotKtKotlinVersion>[^<]+' "$ROOT/packaging/DotKt.Versions.props" | sed 's/.*>//')"
[[ -n "$KOTLINVER" ]] || die "could not read DotKtKotlinVersion from packaging/DotKt.Versions.props"
# Provenance (#166): stamp the source commit into every package's <repository>/RepositoryCommit. MSBuild reads
# RepoCommit off the `-p:` below; the nuspec $repoCommit$ token and the Templates csproj RepositoryCommit both
# resolve from it. `unknown` when not in a git checkout (a shallow/exported tree) — never blocks the pack.
COMMIT="$(git -C "$ROOT" rev-parse HEAD 2>/dev/null || echo unknown)"
for sp in packaging/DotKt.Sdk/Sdk/Sdk.props packaging/DotKt.Sdk.Mpp/Sdk/Sdk.props; do
	sv="$(grep -oE "<DotKtVersion Condition[^>]*>[^<]+" "$ROOT/$sp" | sed 's/.*>//')"
	[[ "$sv" == "$VERCORE" ]] || die "$sp DotKtVersion default ($sv) != release version core ($VERCORE) — bump it (else the SDK ships pulling a stale toolchain, GitHub #131)"
done
for sdk in DotKt.Sdk DotKt.Sdk.Mpp; do
	gv="$(sed -n "s/^[[:space:]]*\"$sdk\"[[:space:]]*:[[:space:]]*\"\([^\"]*\)\".*/\1/p" "$ROOT/global.json")"
	[[ "$gv" == "$VERCORE" ]] || die "global.json $sdk version ($gv) != release version core ($VERCORE) — bump it (else tests resolve a stale SDK)"
done

# GUARD (#53): the version/Kotlin-version strings scattered across templates, docs, and nuspec tags drift after a
# release/Kotlin bump because they were hardcoded and ungated. Everything single-sources off DotKt.Versions.props;
# refuse to pack a mismatch. (Template Sdk pin + nuspec kotlin tag are SUBSTITUTED below/at pack; here we assert
# the substitution tokens survive and the un-substitutable DOC fragments are current.)
# (a) The `dotnet new` template project files must keep the DOTKT_SDK_VERSION placeholder (substituted at pack time).
TPL_CSPROJ="packaging/DotKt.Templates/content/dotkt-cli/DotKtApp.csproj"
grep -q 'DotKt\.Sdk/DOTKT_SDK_VERSION' "$ROOT/$TPL_CSPROJ" || die "$TPL_CSPROJ lost its 'DotKt.Sdk/DOTKT_SDK_VERSION' placeholder — restore it (it is substituted to the release version at pack time, GitHub #53)"
# The MPP template (#133) ships a global.json pinning BOTH DotKt.Sdk.Mpp and its nested DotKt.Sdk — the only place the
# NuGet nested-SDK resolver reads the base version from, so the scaffolded project builds without hand-writing one.
TPL_MPP_GJ="packaging/DotKt.Templates/content/dotkt-mpp/global.json"
grep -q 'DOTKT_SDK_VERSION' "$ROOT/$TPL_MPP_GJ" || die "$TPL_MPP_GJ lost its 'DOTKT_SDK_VERSION' placeholder — restore it (both SDK pins are substituted to the release version at pack time, GitHub #133)"
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
dotnet build "$ROOT/toolchain/dll2klib"   -c Release -o "$ROOT/build/dll2klib-bin"   -v q --nologo
dotnet build "$ROOT/toolchain/retarget"  -c Release -o "$ROOT/build/retarget-bin"  -v q --nologo

# The CLR FRONTEND stdlib KLIB (kotc -classpath) + the stdlib dll pair are REQUIRED package contents (the shipped
# DotKt.Toolchain.targets needs both dlls: the ref feeds bir2cir's @ClrTypeAlias/@ClrIntrinsic substitution, the rt is
# the app's copy-local runtime). Use the FINGERPRINT-AWARE need_* builders (scripts/lib.sh), NOT a build-if-missing
# guard: the tools were just rebuilt above, so a klib/stdlib baked by an OLDER toolchain must be REBUILT before it
# ships — else pack would package a STALE stdlib against fresh tools (silently-broken user apps, or a false-green
# packaged-SDK gate). need_* hash the tool+source inputs into a sidecar .toolstamp and rebuild on mismatch OR absence,
# preserving the build-only-when-needed fast path (#106/#13).
info "ensure CLR frontend stdlib klib (fingerprint-aware)"
need_fe_klib
info "ensure CLR stdlib (ref + rt) (fingerprint-aware)"
need_stdlib_ref
need_stdlib_rt
[[ -f "$STDLIB_REF_DLL" ]] || die "missing $STDLIB_REF_DLL (scripts/build-stdlib-ref.sh --emit failed?)"
[[ -f "$STDLIB_RT_DLL"  ]] || die "missing $STDLIB_RT_DLL (scripts/build-stdlib-rt.sh --emit failed?)"

info "assemble DotKt.Toolchain/tools"
TC="$ROOT/packaging/DotKt.Toolchain/tools"; rm -rf "$TC"; mkdir -p "$TC"
cp -r "$ROOT/toolchain/kotc/build/install/kotc" "$TC/kotc"
cp "$FE_KLIB" "$TC/kotlin-stdlib-clr-frontend.klib"
cp -r "$ROOT/build/ilemit-bin"    "$TC/ilemit"
cp -r "$ROOT/build/bir2cir-bin"   "$TC/bir2cir"
cp -r "$ROOT/build/dll2klib-bin"  "$TC/dll2klib"
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
# The packaged NuGet readme is imported by the Templates csproj as `../DotKt.README.md` (a sibling of the csproj's
# parent) — stage it next to DotKt.Versions.props so that relative reference resolves in the staged layout too.
cp "$ROOT/packaging/DotKt.README.md" "$TPLSTAGE/DotKt.README.md"
cp -r "$ROOT/packaging/DotKt.Templates/." "$TPLSTAGE/DotKt.Templates/"
sed -i "s|DotKt\.Sdk/DOTKT_SDK_VERSION|DotKt.Sdk/$VERCORE|g" "$TPLSTAGE/DotKt.Templates/content/dotkt-cli/DotKtApp.csproj"
# The MPP template's global.json pins both SDKs to the release version (#133).
sed -i "s|DOTKT_SDK_VERSION|$VERCORE|g" "$TPLSTAGE/DotKt.Templates/content/dotkt-mpp/global.json"

info "pack DotKt.Toolchain + DotKt.Sdk + DotKt.Sdk.Mpp + DotKt.Stdlib + DotKt.Templates"
dotnet pack "$ROOT/packaging/DotKt.Toolchain/DotKt.Toolchain.pack.csproj" -o "$FEED" -v q --nologo -p:RepoCommit="$COMMIT"
dotnet pack "$ROOT/packaging/DotKt.Sdk/DotKt.Sdk.pack.csproj" -o "$FEED" -v q --nologo -p:RepoCommit="$COMMIT"
dotnet pack "$ROOT/packaging/DotKt.Sdk.Mpp/DotKt.Sdk.Mpp.pack.csproj" -o "$FEED" -v q --nologo -p:RepoCommit="$COMMIT"
dotnet pack "$ROOT/packaging/DotKt.Stdlib/DotKt.Stdlib.pack.csproj" -o "$FEED" -v q --nologo -p:RepoCommit="$COMMIT"
dotnet pack "$TPLSTAGE/DotKt.Templates/DotKt.Templates.csproj" -o "$FEED" -v q --nologo -p:RepoCommit="$COMMIT"

info "feed:"; ls -1 "$FEED"
