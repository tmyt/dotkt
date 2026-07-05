#!/usr/bin/env bash
# Build the FOUR DotKt NuGet packages into the local feed (build/nuget-feed):
#   DotKt.Sdk       — the MSBuild SDK (Sdk.props/targets; implicit refs to Toolchain + Stdlib)
#   DotKt.Toolchain — the compiler: kotc + bir2cir + ilemit + facadegen + retarget + the CLR frontend
#                     stdlib jar + the COMPILE-TIME stdlib reference assembly (tools/stdlib/DotKt.Private.Stdlib.dll)
#   DotKt.Stdlib    — the RUNTIME stdlib assembly (lib/net10.0/DotKt.Stdlib.dll, copy-local)
#   DotKt.Templates — `dotnet new` templates
# (There is no separate runtime package.) Version is single-sourced in
# packaging/DotKt.Versions.props (imported by every pack .csproj). Orchestrated by `make pack` (which
# builds the prerequisites first); standalone it builds what's missing. Output: build/nuget-feed (wiped).
source "$(dirname "$0")/lib.sh"

usage() {
	cat <<EOF
usage: $SCRIPT_NAME
Packs the 4 DotKt NuGet packages into build/nuget-feed (no flags). -h for this help.
EOF
}
while (( $# )); do
	case "$1" in
		-h|--help) usage; exit 0 ;;
		*) usage_error "unknown argument '$1'" ;;
	esac
done

FEED="$ROOT/build/nuget-feed"; rm -rf "$FEED"; mkdir -p "$FEED"

info "build compiler (installDist) + tools"
( cd "$ROOT" && ./gradlew -q :kotc:installDist )
dotnet build "$ROOT/toolchain/ilemit"    -c Release -o "$ROOT/build/ilemit-bin"    -v q --nologo
dotnet build "$ROOT/toolchain/bir2cir"   -c Release -o "$ROOT/build/bir2cir-bin"   -v q --nologo
dotnet build "$ROOT/toolchain/facadegen" -c Release -o "$ROOT/build/facadegen-bin" -v q --nologo
dotnet build "$ROOT/toolchain/retarget"  -c Release -o "$ROOT/build/retarget-bin"  -v q --nologo

# The CLR FRONTEND stdlib jar (kotc -classpath): built FROM our CLR stdlib sources, REPLACING the JVM kotlin-stdlib.jar
# whose java.util.* typealiases leaked into the frontend. Consumes the kotc install lib/*.jar produced by installDist.
info "ensure CLR frontend stdlib jar"
[[ -f "$FE_JAR" ]] || bash "$ROOT/scripts/build-stdlib-jar.sh"

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
cp "$FE_JAR" "$TC/kotlin-stdlib-clr-frontend.jar"
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

info "pack DotKt.Toolchain + DotKt.Sdk + DotKt.Stdlib + DotKt.Templates"
dotnet pack "$ROOT/packaging/DotKt.Toolchain/DotKt.Toolchain.pack.csproj" -o "$FEED" -v q --nologo
dotnet pack "$ROOT/packaging/DotKt.Sdk/DotKt.Sdk.pack.csproj" -o "$FEED" -v q --nologo
dotnet pack "$ROOT/packaging/DotKt.Stdlib/DotKt.Stdlib.pack.csproj" -o "$FEED" -v q --nologo
dotnet pack "$ROOT/packaging/DotKt.Templates/DotKt.Templates.csproj" -o "$FEED" -v q --nologo

info "feed:"; ls -1 "$FEED"
