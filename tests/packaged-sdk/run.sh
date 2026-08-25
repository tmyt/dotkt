#!/usr/bin/env bash
# The PACKAGED-SDK gate: the ONLY gate that exercises the shipping nupkg-resolution path — a project that
# resolves `DotKt.Sdk` / `DotKt.Sdk.Mpp` + the implicit `DotKt.Toolchain` / `DotKt.Stdlib` PackageReferences
# from real .nupkgs in a NuGet feed. `tests/msbuild/run.sh` uses the IN-REPO dev entry (eng/KotlinClr.targets,
# hard-coded tool paths) and never restores a nupkg, so packaging-only bugs slip past it — 0.9.5 shipped
# broken twice for exactly this reason (#131 stale SDK version, #132 a Library's non-copy-local reference
# never reaching bir2cir/ilemit). This suite packs the 5 nupkgs to a local feed and drives EIGHT isolated
# scenarios through `dotnet build`/`dotnet run` from that feed only:
#   exe      — a plain `Sdk="DotKt.Sdk"` Exe under a punctuation/whitespace-containing path: build + RUN, assert
#              stdout, and prove each >8191-byte compiler argument set travels through a packaged response file.
#   multi-target-klib-references — direct outer-build invocation of the public KLIB-reference target: dispatch
#              across both TFMs and preserve each generated KLIB's source/TFM ownership metadata.
#   library  — a `Library` that PackageReferences a SECOND DotKt library (packed as its own nupkg) and calls
#              into it. A package runtime dll is NOT copy-local for OutputType=Library, so under the old
#              copy-local-glob targets that reference never reached ilemit -> the emit FAILED (#132-general).
#              This is the case the gate exists to hold: build succeeds + the emitted dll carries the call.
#   csharp-consumer — a real C# Exe ProjectReferences a packaged-SDK Kotlin library and binds its emitted CLR
#              signatures LITERALLY (no Kotlin re-import), so it is the only lane that measures what the ABI is
#              rather than what the compiler can restore. Reports two verdicts: `nullable-generic-shape` (the
#              erased slot's physical type + [KotlinNullableGeneric] carrier, by reflection) and `csharp-consumer`
#              (the C# program compiles against those slots and runs). Both #86, both XFAIL_PKG-listed today.
#   coroutine-cross-module — a packaged Kotlin library exports a suspend function that genuinely suspends on
#              Task.Delay; a separately compiled packaged-SDK Kotlin Exe restores and drives that function across
#              the assembly boundary, observes return-before-resume, and both emitted assemblies pass ILVerify (#137).
#   mpp      — a `Sdk="DotKt.Sdk.Mpp"` multiplatform Exe (common `expect` + clr `actual`): the MPP SDK path
#              end-to-end through nupkg resolution. build + RUN, assert stdout.
#   template — install DotKt.Templates, scaffold the CLI template, then build + RUN it.
#   mpp-template — scaffold the MPP template and verify both SDK pins before build + RUN.
#
# Isolation (defeats the cache-masking landmine): every project the cases drive restores through a per-run
# nuget.config with <clear/> + the local feed ONLY and an isolated <globalPackagesFolder> under the scratch
# dir, so a stale published 0.9.5 in the user's ~/.nuget cache can never mask the freshly-packed one and the
# packages the cases resolve never land in that cache; `dotnet new` installs into a scratch template hive
# instead of the machine-global store. Getting the toolchain BUILT is deliberately outside that boundary:
# the refcheck tool below restores from the user's configured NuGet sources into ~/.nuget, and pack-nuget.sh
# drives Gradle (~/.gradle, Maven Central) plus the toolchain's own NuGet restore. Green = every fail name is
# XFAIL_PKG-listed AND every listed name still fails; NEW-FAIL or stale FIXED names exit 1.
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
SCRIPT_NAME=packaged-sdk-tests
source "$ROOT/scripts/lib.sh"

usage() { cat <<EOF
usage: $SCRIPT_NAME
Packs the 5 nupkgs to build/nuget-feed and drives 8 packaged SDK/template scenarios from that feed only.
Green (exit 0) = no fail name outside XFAIL_PKG and no stale entry inside it.
EOF
}
while (( $# )); do
	case "$1" in
		-h|--help) usage; exit 0 ;;
		*) usage_error "unknown argument '$1'" ;;
	esac
done

# The authoritative XFAIL baseline (fail name -> reason). A listed name that starts passing prints
# "FIXED — remove it" and reddens as a stale baseline; any name NOT listed that fails prints NEW-FAIL and reddens.
# Computed by lib.sh xfail_diff at the bottom.
declare -A XFAIL_PKG=(
	# EMPTY. Both halves of case_csharp_consumer were listed here for #86: their assertions are written against the
	# POST-erasure ABI — the ABI break is the sanctioned decision in #86 — so they stayed red until it landed, and
	# they are the reason the case exists (no other gate sees the physical signature; every other one re-imports the
	# library as Kotlin). The uniform-erasure core made the top-level `T?` param, ctor param and return
	# `System.Object` with a carrier; `Array<X?>`-is-`object[]` (D2) closed the last two slots, so a C# caller now
	# binds `object[]` in both directions and every probe matches.
)

# The two XFAIL-listed verdicts above are per-case booleans, so on their own they only say "it did not work" —
# and a missing tool, a restore failure, a changed diagnostic or a NEW wrong slot would satisfy them exactly as
# well as the documented #86 break. These are the EXACT failures they are allowed to be. A mismatch reddens
# under csharp-consumer-diagnostics / nullable-generic-shape-drift, neither of which is baseline-listed, so a
# drift cannot hide inside an expected red. Both are checked only while the case is failing; once the erasure
# lands the sets are empty, the verdicts pass, and there is nothing left to compare.
#
# Sorted, one per line, with paths and the trailing [project] suffix stripped — a diagnostic's identity here is
# its line, code and message, not where the scratch workspace happened to be.
# EMPTY, like its NG_SHAPE_EXPECTED sibling: the three `int?[]` / `object[]` conversion errors it used to pin were
# the C# consumer's view of `Array<Int?>` before #86 D2 made it `object[]`, and the case now builds and runs. The
# drift check below is reached only while the case is FAILING, so an empty set means it has nothing left to guard.
CS_EXPECTED_DIAGNOSTICS=""

# The refcheck --shape mismatches the erasure has not yet fixed — EMPTY now that every probed slot is its
# declaration's erasure. The drift check below only runs while mismatches exist, so an empty set means the verdict
# passes and there is nothing left for it to guard. `System.Int32`'s assembly qualification inside Nullable`1[[…]]
# carries a runtime version, so it is collapsed before comparison; nothing else is normalized.
NG_SHAPE_EXPECTED=""

# The package version the pack stamps (single-sourced in DotKt.Versions.props). The Sdk="DotKt.Sdk/$VER"
# reference, the second library's PackageReference, and the MPP global.json all pin THIS version, so a
# version skew between the props and the SDK's embedded DotKtVersion (the #131 class of bug) surfaces here as
# a restore failure rather than a silent stale-toolchain pull.
VER_PREFIX="$(grep -oP '<DotKtVersionPrefix>\K[^<]+' "$ROOT/packaging/DotKt.Versions.props")"
VER_SUFFIX="$(grep -oP '<DotKtVersionSuffix>\K[^<]*' "$ROOT/packaging/DotKt.Versions.props")"
VER="$VER_PREFIX${VER_SUFFIX:+-$VER_SUFFIX}"
[[ -n "$VER" ]] || die "could not read DotKtVersionPrefix from packaging/DotKt.Versions.props"
KOTLIN_VER="$(grep -oP '<DotKtKotlinVersion>\K[^<]+' "$ROOT/packaging/DotKt.Versions.props")"
[[ -n "$KOTLIN_VER" ]] || die "could not read DotKtKotlinVersion from packaging/DotKt.Versions.props"
FEED="$ROOT/build/nuget-feed"

# 1. Pack the 5 nupkgs FRESH from the current sources (rebuilds the tools + re-copies the shipped targets into
#    DotKt.Toolchain). Uses the cached stdlib dlls when present — correct for an MSBuild/targets-only change;
#    a stdlib/kotc/bir2cir source change is gated by verify-tests, not here.
info "packing 5 nupkgs (version $VER) -> $FEED"
bash "$ROOT/scripts/pack-nuget.sh" >/dev/null || die "pack-nuget.sh failed"
# #223: immediately repeat the standalone pack. Tool builds may refresh output mtimes, but a content-stable
# toolchain must reuse the just-baked frontend KLIB and stdlib pair. The helper also proves that a real same-size
# content change invalidates the new fingerprint, so idempotency does not weaken stale-artifact protection.
bash "$ROOT/tests/packaged-sdk/verify-pack-idempotency.sh"

# 2. Scratch workspace: an isolated globalPackagesFolder + a local-only feed, so restore can ONLY see the
#    freshly-packed nupkgs (no cache masking, no touching the user's ~/.nuget). Each template case gets its
#    own hive under here too, so `dotnet new` can ONLY see the freshly-packed templates.
WS="$ROOT/build/verify-packaged-sdk"
rm -rf "$WS"; mkdir -p "$WS/pkgs"
RESULTS="$WS/results"; mkdir -p "$RESULTS"
NUGET_CONFIG="$WS/nuget.config"
cat > "$NUGET_CONFIG" <<EOF
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <config>
    <add key="globalPackagesFolder" value="$WS/pkgs" />
  </config>
  <packageSources>
    <clear />
    <add key="local" value="$FEED" />
  </packageSources>
</configuration>
EOF

# A hive is the template engine's whole state: the installed packages and their expanded templates. Naming one
# inside the scratch workspace keeps the gate out of the machine-global store under $HOME, so two worktrees
# running this gate at once cannot collide over one shared hive — issue #250, which surfaced as
# ThrowMoreThanOneMatchException / "Could not find the template package containing template ...".
# The hive is per CASE rather than per run because installing DotKt.Templates into a hive that already carries
# it must not happen: without --force it is a hard error, and WITH --force the engine appends a SECOND
# registration for the same id so every later scaffold in that hive hits the same ambiguity error. A fresh hive
# per case needs neither. $WS is wiped at the top of every run, so the hives are disposable and nothing needs
# uninstalling afterwards. The switch goes AHEAD of the caller's arguments: on the scaffold form a trailing
# option would be offered to the template's own parameter parser, so an SDK that stopped recognizing it could
# absorb it instead of failing.
dotnet_new() { # <hive> <dotnet-new-args>...
	local hive="$1"; shift
	dotnet new --debug:custom-hive "$hive" "$@"
}
# An SDK that no longer knows the switch exits non-zero on the unrecognized option, so the case fails loudly.
# This tripwire covers the other direction — an SDK that ACCEPTS it and installs under $HOME instead would leave
# the package out of the scratch hive and otherwise pass, quietly restoring the cross-worktree race.
hive_isolated() { # <case-name> <hive> — true iff the install landed the package IN this case's hive
	local name="$1" hive="$2"
	if find "$hive" -name "DotKt.Templates.$VER.nupkg" -print -quit 2>/dev/null | grep -q .; then return 0; fi
	fail "$name" "dotnet new installed outside the scratch template hive" "no DotKt.Templates.$VER.nupkg under ${hive#"$ROOT/"}"
	return 1
}

# One atomic result record per case + a fail-name list for the verdict.
declare -a FAILS=()
pass() { # <name>
	echo "PASS  pkgsdk:$1"; echo "PASS  pkgsdk:$1" > "$RESULTS/$1.tmp"; mv -f "$RESULTS/$1.tmp" "$RESULTS/$1"
}
fail() { # <name> <reason> [detail]
	local detail="${3:-}"
	echo "FAIL  pkgsdk:$1 ($2)"
	[[ -n "$detail" ]] && printf '%s\n' "$detail"
	{ echo "FAIL  pkgsdk:$1 ($2)"; [[ -n "$detail" ]] && printf '%s\n' "$detail"; } > "$RESULTS/$1.tmp"; mv -f "$RESULTS/$1.tmp" "$RESULTS/$1"
	FAILS+=("$1")
}
# Strip the compiler's own chatter from a run's stdout so the assert compares only program output.
run_out() { grep -vE 'kotlin/clr:|duplicate source root' || true; }

# run_project <dir> <stderr-logfile> [timeout] — build+run a packaged project in <dir>; echo its noise-filtered program
# stdout; RETURN the run's exit status (0 iff build AND execution succeeded). Build/restore output is kept out of
# the captured program stdout: on a clean GitHub runner NuGet can print certificate and package-install messages
# to stdout even at quiet verbosity. Status and stdout are captured independently (issue #163), so a project
# which prints the expected text and THEN throws / returns non-zero remains rejected.
run_project() { # <dir> <stderr-logfile> [timeout]
	local dir="$1" log="$2" run_timeout="${3:-}" build_log="${2%.err}.build.log"
	if ! (cd "$dir" && dotnet build -v q --nologo >"$build_log" 2>&1); then
		cp "$build_log" "$log"
		return 1
	fi
	local rc=0 raw
	if [[ -n "$run_timeout" ]]; then
		raw="$(cd "$dir" && timeout "$run_timeout" dotnet run --no-build --no-restore -v q 2>"$log")" || rc=$?
	else
		raw="$(cd "$dir" && dotnet run --no-build --no-restore -v q 2>"$log")" || rc=$?
	fi
	printf '%s' "$raw" | run_out
	return $rc
}

# A tiny metadata-only reflection checker (does the emitted dll declare owner.member?) — built ONCE with the
# DEFAULT NuGet config (it needs System.Reflection.MetadataLoadContext from nuget.org; it is a build-time
# tool, NOT part of the isolated SDK-resolution test). Lives OUTSIDE $WS so the isolated local-only
# nuget.config there does not govern its restore, and is cached across runs.
REFCHECK="$ROOT/build/verify-packaged-sdk-tool"
# gen_refcheck_src <dir> — write the tool's sources. Kept separate from the build so the cache key can be a
# hash of the ACTUAL generated files rather than of a text range of this script: a range is not the source
# (its end delimiter drifts the moment another `}` appears inside), and hashing the files themselves cannot
# drift by construction.
gen_refcheck_src() {
	mkdir -p "$1"
	cat > "$1/refcheck.csproj" <<'EOF'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><Nullable>disable</Nullable><ImplicitUsings>disable</ImplicitUsings><AssemblyName>refcheck</AssemblyName></PropertyGroup>
  <ItemGroup><PackageReference Include="System.Reflection.MetadataLoadContext" Version="9.0.0" /></ItemGroup>
</Project>
EOF
	cat > "$1/Program.cs" <<'EOF'
using System; using System.Linq; using System.Reflection;
// refcheck <dll> <ownerFqn> <memberName> [exactRefs]
//     -> exit 0 iff the dll declares the requested member (the DECLARATION mode).
// refcheck --shape <dll> <ownerFqn> <methodName> <slot> <clrTypeFullName> <carrier:0|1|any> [exactRefs]
//     -> exit 0 iff that method's SLOT has EXACTLY <clrTypeFullName> as its CLR type and does (1) / does not (0)
//        carry [DotKt.Runtime.CompilerServices.KotlinNullableGeneric]; `any` asserts the physical type only.
//        <slot> is `ret` or `pN` (0-based param).
// The shape mode pins the ERASURE INVARIANT at the metadata level: a nullable-generic slot's physical type is
// `System.Object` and its pre-erasure Kotlin shape travels in the carrier attribute. That is one assertion a
// behavioral case cannot make — a slot can be physically wrong and still run when nothing crosses it.
class P {
    // The slot type as a STABLE display name: a constructed generic's arguments are rendered recursively rather
    // than through `FullName`, whose per-argument assembly qualification carries a runtime version and would make
    // every expectation drift with the SDK. An open type parameter has no FullName at all; its Name is `T`.
    static string Render(Type t) =>
        t.IsArray ? Render(t.GetElementType()) + "[]"
        : t.IsGenericType ? t.GetGenericTypeDefinition().FullName + "[" + string.Join(",", t.GetGenericArguments().Select(Render)) + "]"
        : (t.FullName ?? t.Name);
    static int Main(string[] a) {
        var shape = a.Length > 0 && a[0] == "--shape";
        if (shape) a = a.Skip(1).ToArray();
        var dll = System.IO.Path.GetFullPath(a[0]);
        // The TPA list is the runtime host's resolved platform set. Non-platform dependencies are explicit; never
        // turn the input assembly's parent directory into an implicit reference universe.
        var paths = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))
            .Split(System.IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).ToList();
        var extraRefs = shape ? (a.Length > 6 ? a[6] : null) : (a.Length > 3 ? a[3] : null);
        if (extraRefs != null) paths.AddRange(extraRefs.Split(';', StringSplitOptions.RemoveEmptyEntries));
        paths.Add(dll);
        using var mlc = new MetadataLoadContext(new PathAssemblyResolver(paths.Distinct()));
        var asm = mlc.LoadFromAssemblyPath(dll);
        Type[] ts; try { ts = asm.GetTypes(); } catch (ReflectionTypeLoadException e) { ts = e.Types.Where(t => t != null).ToArray(); }
        var owner = ts.FirstOrDefault(t => t.FullName == a[1]);
        if (owner == null) { Console.Error.WriteLine($"refcheck: type {a[1]} not found"); return 1; }
        const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;
        if (!shape) {
            var m = owner.GetMembers(All).FirstOrDefault(x => x.Name == a[2]);
            if (m == null) { Console.Error.WriteLine($"refcheck: {a[1]}.{a[2]} not found"); return 1; }
            return 0;
        }
        // A ctor is named `.ctor`; anything else is a method. Refuse an ambiguous name rather than guessing which
        // overload was meant — the same refusal discipline the reference readers use.
        MethodBase[] cands = a[2] == ".ctor"
            ? owner.GetConstructors(All).Cast<MethodBase>().ToArray()
            : owner.GetMethods(All).Where(x => x.Name == a[2]).Cast<MethodBase>().ToArray();
        if (cands.Length == 0) { Console.Error.WriteLine($"refcheck: {a[1]}.{a[2]} not found"); return 1; }
        if (cands.Length > 1) { Console.Error.WriteLine($"refcheck: {a[1]}.{a[2]} is an overload set ({cands.Length}); shape mode needs one declaration"); return 1; }
        var target = cands[0];
        Type slotType; System.Collections.Generic.IList<CustomAttributeData> attrs;
        if (a[3] == "ret") {
            if (target is not MethodInfo mi) { Console.Error.WriteLine("refcheck: `ret` is not a slot of a constructor"); return 1; }
            slotType = mi.ReturnType; attrs = mi.ReturnParameter.GetCustomAttributesData();
        } else if (a[3].Length > 1 && a[3][0] == 'p' && int.TryParse(a[3].Substring(1), out var pi)) {
            var ps = target.GetParameters();
            if (pi >= ps.Length) { Console.Error.WriteLine($"refcheck: {a[1]}.{a[2]} has {ps.Length} parameter(s), no {a[3]}"); return 1; }
            slotType = ps[pi].ParameterType; attrs = ps[pi].GetCustomAttributesData();
        } else { Console.Error.WriteLine($"refcheck: unknown slot '{a[3]}' (expected `ret` or `pN`)"); return 1; }
        var actual = Render(slotType);
        var carrier = attrs.Any(x => x.AttributeType.FullName == "DotKt.Runtime.CompilerServices.KotlinNullableGenericAttribute");
        var ok = actual == a[4] && (a[5] == "any" || carrier == (a[5] == "1"));
        if (!ok) Console.Error.WriteLine($"refcheck: {a[1]}.{a[2]} slot {a[3]} is [{actual}] carrier={(carrier ? 1 : 0)}; expected [{a[4]}] carrier={a[5]}");
        return ok ? 0 : 1;
    }
}
EOF
}

# The tool is cached across runs, so an existence-only check would keep serving a stale binary after the
# heredocs above change — the same lazy-need_tool staleness that makes a toolchain result meaningless. The
# cache key is a hash of the generated sources, and the whole build happens in a STAGING directory:
#   * a rebuild never writes into the live cache, so a run that fails halfway cannot leave a half-built tool;
#   * the directory and its hash marker are swapped in together, only on success;
#   * a FAILED rebuild removes the marker, so have_refcheck rejects the previous binary instead of accepting
#     a tool that does not match this script.
# have_refcheck re-checks the marker rather than the file's existence, which is what makes that last point hold.
REFCHECK_STAGE="$REFCHECK.stage"
rm -rf "$REFCHECK_STAGE"; mkdir -p "$REFCHECK_STAGE/src"
gen_refcheck_src "$REFCHECK_STAGE/src"
REFCHECK_HASH="$(cat "$REFCHECK_STAGE/src/refcheck.csproj" "$REFCHECK_STAGE/src/Program.cs" | sha256sum | cut -d' ' -f1)"
have_refcheck() { [[ -f "$REFCHECK/bin/refcheck.dll" && "$(cat "$REFCHECK/.srchash" 2>/dev/null)" == "$REFCHECK_HASH" ]]; }

if have_refcheck; then
	info "reflection checker cached"
	rm -rf "$REFCHECK_STAGE"
else
	info "building reflection checker"
	if dotnet build "$REFCHECK_STAGE/src" -c Release -o "$REFCHECK_STAGE/bin" -v q --nologo >"$REFCHECK_STAGE/build.log" 2>&1; then
		rm -rf "$REFCHECK"; mkdir -p "$REFCHECK"
		mv "$REFCHECK_STAGE/src" "$REFCHECK/src"; mv "$REFCHECK_STAGE/bin" "$REFCHECK/bin"
		printf '%s' "$REFCHECK_HASH" > "$REFCHECK/.srchash"
		rm -rf "$REFCHECK_STAGE"
	else
		# Leave nothing usable: a stale binary that does not match these sources must not silently answer.
		# The staging dir stays for its build log — nothing reads the tool from there.
		rm -f "$REFCHECK/.srchash"
		warn "refcheck build failed — the metadata verdict cannot be taken (see ${REFCHECK_STAGE#"$ROOT/"}/build.log)"
	fi
fi

# ---------------------------------------------------------------------------------------------------------
# Case: exe — a plain packaged Exe, build + run.
# ---------------------------------------------------------------------------------------------------------
case_exe() {
	# The punctuation and whitespace are deliberate: response-file parsing must preserve the absolute source path on
	# every host. The net10.0 reference sets are also long enough to trip cmd.exe when passed inline.
	local d="$WS/exe O'Brien response-file"; mkdir -p "$d"; cp "$NUGET_CONFIG" "$d/nuget.config"
	cat > "$d/App.ktproj" <<EOF
<Project Sdk="DotKt.Sdk/$VER">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>disable</Nullable>
  </PropertyGroup>
  <!-- Public toolchain-extension contract: the target returns only KLIBs projected from this TFM's resolved
       references. The embedded frontend stdlib remains available through its dedicated property. -->
  <Target Name="AssertDotKtKlibReferences"
          BeforeTargets="KotlinCompile"
          DependsOnTargets="DotKtResolveKlibReferences">
    <ItemGroup>
      <_MissingReferenceKlib Include="@(DotKtResolvedKlibReference)" Condition="!Exists('%(FullPath)')" />
      <_ReferenceWithoutSource Include="@(DotKtResolvedKlibReference)"
                               Condition="'%(SourceAssembly)' == '' or !Exists('%(SourceAssembly)')" />
      <_WrongReferenceTfm Include="@(DotKtResolvedKlibReference)"
                          Condition="'%(TargetFramework)' != '\$(TargetFramework)'" />
      <_RelativeReferenceKlib Include="@(DotKtResolvedKlibReference)"
                              Condition="'%(Identity)' != '%(FullPath)'" />
    </ItemGroup>
    <Error Condition="'@(DotKtResolvedKlibReference)' == ''" Text="DotKtResolvedKlibReference was not published." />
    <Error Condition="'@(_MissingReferenceKlib)' != ''" Text="DotKtResolvedKlibReference contains missing files: @(_MissingReferenceKlib)" />
    <Error Condition="'@(_ReferenceWithoutSource)' != ''" Text="DotKtResolvedKlibReference lost SourceAssembly: @(_ReferenceWithoutSource)" />
    <Error Condition="'@(_WrongReferenceTfm)' != ''" Text="DotKtResolvedKlibReference has the wrong TargetFramework: @(_WrongReferenceTfm)" />
    <Error Condition="'@(_RelativeReferenceKlib)' != ''" Text="DotKtResolvedKlibReference identity was not absolute: @(_RelativeReferenceKlib)" />
    <Error Condition="'\$(DotKtStdlib)' == '' or !Exists('\$(DotKtStdlib)')" Text="DotKtStdlib was not published as a dedicated property." />
    <Error Condition="'@(DotKtReferenceKlib)' != '' or '@(DotKtFrontendKlib)' != ''"
           Text="The removed synthetic frontend-input items were still published." />
    <Error Condition="'\$(DotKtKotlinVersion)' != '$KOTLIN_VER'" Text="DotKtKotlinVersion was '\$(DotKtKotlinVersion)', expected '$KOTLIN_VER'." />
  </Target>
</Project>
EOF
	cat > "$d/App;Response.kt" <<'EOF'
fun nullableCharSequence(): CharSequence? = null
fun nullableBuilder(value: StringBuilder?): CharSequence? = value
var producerCalls = 0
fun nextBuilder(): StringBuilder? {
    producerCalls += 1
    return StringBuilder().append("once")
}
fun nullableFromCall(): CharSequence? = nextBuilder()
fun main() {
    val value: Any? = nullableCharSequence()
    check(value == null)
    check(nullableBuilder(null) == null)
    val builder = StringBuilder()
    builder.append("snapshot")
    check(nullableBuilder(builder) == "snapshot")
    check(nullableFromCall() == "once")
    check(producerCalls == 1)
    println("packaged exe ok: " + (2 + 3))
}
EOF
	local expected="packaged exe ok: 5" actual rc=0
	actual="$(run_project "$d" "$d/run.err")" || rc=$?
	if (( rc != 0 )); then fail exe "run exit $rc" "$(printf -- '--- expected ---\n%s\n--- stdout ---\n%s\n--- stderr ---\n%s' "$expected" "$actual" "$(tail -30 "$d/run.err" 2>/dev/null)")"
	elif [[ ! -s "$d/obj/Debug/net10.0/dotkt-kotc.rsp" \
		|| ! -s "$d/obj/Debug/net10.0/dotkt-bir2cir.rsp" \
		|| ! -s "$d/obj/Debug/net10.0/dotkt-ilemit.rsp" ]]; then
		fail exe "packaged compiler pipeline did not materialize all three response files"
	elif (( $(wc -c < "$d/obj/Debug/net10.0/dotkt-kotc.rsp") <= 8191 \
		|| $(wc -c < "$d/obj/Debug/net10.0/dotkt-bir2cir.rsp") <= 8191 \
		|| $(wc -c < "$d/obj/Debug/net10.0/dotkt-ilemit.rsp") <= 8191 )); then
		fail exe "a packaged response-file fixture no longer exceeds cmd.exe's 8191-character limit"
	elif ! grep -qx -- '-classpath' "$d/obj/Debug/net10.0/dotkt-kotc.rsp"; then
		fail exe "kotc response file did not carry the frontend classpath option"
	elif ! grep -qx -- '--compile-refs' "$d/obj/Debug/net10.0/dotkt-bir2cir.rsp" \
		|| ! grep -qx -- '--runtime-refs' "$d/obj/Debug/net10.0/dotkt-ilemit.rsp"; then
		fail exe "back-end response files did not carry the resolved reference sets"
	elif ! grep -q 'App;Response.kt' "$d/obj/Debug/net10.0/dotkt-kotc.rsp"; then
		fail exe "packaged response file lost the punctuation-containing source path"
	elif grep -Eq 'kotc(\.bat)?"? .* -classpath |bir2cir\.dll" .*--compile-refs|ilemit\.dll" .*--(compile|runtime)-refs' "$d/run.build.log"; then
		fail exe "a packaged compiler tool still received its generated argument set inline"
	elif [[ "$actual" == "$expected" ]]; then pass exe
	else fail exe "output mismatch" "$(printf -- '--- expected ---\n%s\n--- actual ---\n%s' "$expected" "$actual")"; fi
}

# ---------------------------------------------------------------------------------------------------------
# Case: multi-target-klib-references — invoke the public target on the CROSS-TARGETING outer build. The
# buildMultiTargeting package asset must dispatch to every inner TFM and aggregate only the generated KLIBs,
# preserving the TFM and source-assembly metadata that lets an LSP keep the reference universes separate.
# ---------------------------------------------------------------------------------------------------------
case_multitarget_klib_references() {
	local d="$WS/multitarget-klib-references"; mkdir -p "$d"; cp "$NUGET_CONFIG" "$d/nuget.config"
	mkdir -p "$d/ref-src" "$d/refs"
	cat > "$d/ref-src/OuterOnlyReference.csproj" <<'EOF'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <AssemblyName>OuterOnlyReference</AssemblyName>
  </PropertyGroup>
</Project>
EOF
	cat > "$d/ref-src/OuterOnlyReference.cs" <<'EOF'
public sealed class OuterOnlyReferenceType { }
EOF
	if ! dotnet build "$d/ref-src/OuterOnlyReference.csproj" -c Release -o "$d/refs" -v q --nologo >"$d/ref-build.log" 2>&1; then
		fail multi-target-klib-references "conditional reference build failed" "$(tail -30 "$d/ref-build.log")"; return
	fi
	cat > "$d/App.ktproj" <<EOF
<Project Sdk="DotKt.Sdk/$VER">
  <PropertyGroup>
    <TargetFrameworks>net10.0;net10.0-windows</TargetFrameworks>
    <Nullable>disable</Nullable>
  </PropertyGroup>
  <!-- Deliberately give only one inner build a custom reference. This proves the outer target aggregates each
       TFM's actual resolved universe instead of duplicating one synthetic set with different TFM metadata. -->
  <ItemGroup Condition="'\$(TargetFramework)' == 'net10.0'">
    <Reference Include="OuterOnlyReference">
      <HintPath>$d/refs/OuterOnlyReference.dll</HintPath>
      <Private>false</Private>
    </Reference>
  </ItemGroup>
  <Target Name="AssertDotKtMultiTargetKlibReferences">
    <!-- Consume TargetOutputs directly so this probe pins the public outer target's Returns contract. -->
    <CallTarget Targets="DotKtResolveKlibReferences">
      <Output TaskParameter="TargetOutputs" ItemName="_ReturnedKlibReference" />
    </CallTarget>
    <ItemGroup>
      <_Net10Klib Include="@(_ReturnedKlibReference)" Condition="'%(TargetFramework)' == 'net10.0'" />
      <_Net10WindowsKlib Include="@(_ReturnedKlibReference)" Condition="'%(TargetFramework)' == 'net10.0-windows'" />
      <_Net10ConditionalReference Include="@(_ReturnedKlibReference)"
                                  Condition="'%(TargetFramework)' == 'net10.0' and '%(Filename)' == 'OuterOnlyReference'" />
      <_UnexpectedWindowsConditionalReference Include="@(_ReturnedKlibReference)"
                                              Condition="'%(TargetFramework)' == 'net10.0-windows' and '%(Filename)' == 'OuterOnlyReference'" />
      <_MissingKlib Include="@(_ReturnedKlibReference)" Condition="!Exists('%(FullPath)')" />
      <_RelativeKlib Include="@(_ReturnedKlibReference)" Condition="'%(Identity)' != '%(FullPath)'" />
      <_MissingSourceAssembly Include="@(_ReturnedKlibReference)"
                              Condition="'%(SourceAssembly)' == '' or !Exists('%(SourceAssembly)')" />
    </ItemGroup>
    <Error Condition="'@(_Net10Klib)' == ''" Text="No net10.0 KLIB references were returned." />
    <Error Condition="'@(_Net10WindowsKlib)' == ''" Text="No net10.0-windows KLIB references were returned." />
    <Error Condition="'@(_Net10ConditionalReference)' == ''" Text="The net10.0-only reference was not returned for net10.0." />
    <Error Condition="'@(_UnexpectedWindowsConditionalReference)' != ''" Text="The net10.0-only reference leaked into net10.0-windows." />
    <Error Condition="'@(_MissingKlib)' != ''" Text="Returned KLIB does not exist: @(_MissingKlib)" />
    <Error Condition="'@(_RelativeKlib)' != ''" Text="Returned KLIB identity was not absolute: @(_RelativeKlib)" />
    <Error Condition="'@(_MissingSourceAssembly)' != ''" Text="Returned item lost SourceAssembly: @(_MissingSourceAssembly)" />
    <Error Condition="'\$(DotKtStdlib)' == '' or !Exists('\$(DotKtStdlib)')"
           Text="DotKtStdlib was not published to the outer build as a dedicated property." />
    <Error Condition="'@(DotKtReferenceKlib)' != '' or '@(DotKtFrontendKlib)' != ''"
           Text="The removed synthetic frontend-input items were still published." />
    <Error Condition="'\$(DotKtStdlibRefAsm)' != ''"
           Text="TFM-specific toolchain properties leaked into the outer build: DotKtStdlibRefAsm='\$(DotKtStdlibRefAsm)'." />
    <WriteLinesToFile File="$d/resolved.txt"
                      Lines="@(_ReturnedKlibReference->'%(TargetFramework)|%(RuntimeIdentifier)|%(FullPath)|%(SourceAssembly)')"
                      Overwrite="true" />
  </Target>
</Project>
EOF
	if ! dotnet restore "$d/App.ktproj" --configfile "$d/nuget.config" -v q --nologo >"$d/restore.log" 2>&1; then
		fail multi-target-klib-references "restore failed" "$(tail -30 "$d/restore.log")"; return
	fi
	if ! dotnet msbuild "$d/App.ktproj" -t:AssertDotKtMultiTargetKlibReferences -v q --nologo >"$d/resolve.log" 2>&1; then
		fail multi-target-klib-references "outer target failed" "$(tail -40 "$d/resolve.log")"; return
	fi
	if [[ ! -s "$d/resolved.txt" ]] \
		|| ! grep -q "^net10\\.0||$d/obj/Debug/net10\\.0/klib/" "$d/resolved.txt" \
		|| ! grep -q "^net10\\.0-windows||$d/obj/Debug/net10\\.0-windows/klib/" "$d/resolved.txt"; then
		fail multi-target-klib-references "outer target did not preserve TFM-specific KLIB paths" "$(cat "$d/resolved.txt" 2>/dev/null)"; return
	fi
	pass multi-target-klib-references
}

# ---------------------------------------------------------------------------------------------------------
# Case: library — the #132-general reproducer. A `Library` that PackageReferences a SECOND DotKt library
# (packed as its own nupkg) and calls into it. The second library's runtime dll is NOT copy-local for a
# Library, so the OLD copy-local-glob targets starved ilemit of it and the emit FAILED. Under the general
# @(ReferencePath) rule it flows through. Assert: consumer builds AND the emitted dll declares the call.
# ---------------------------------------------------------------------------------------------------------
case_library() {
	# (a) build the second DotKt library.
	local lib="$WS/lib"; mkdir -p "$lib"; cp "$NUGET_CONFIG" "$lib/nuget.config"
	cat > "$lib/MyDotKtLib.ktproj" <<EOF
<Project Sdk="DotKt.Sdk/$VER">
  <PropertyGroup>
    <OutputType>Library</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>disable</Nullable>
  </PropertyGroup>
</Project>
EOF
	cat > "$lib/lib.kt" <<'EOF'
package mylib
fun libValue(): Int = 42
class Doubler { fun twice(n: Int): Int = n * 2 }
EOF
	if ! (cd "$lib" && dotnet build -v q --nologo >"$lib/build.log" 2>&1); then
		fail library "second-library build failed" "$(tail -20 "$lib/build.log")"; return
	fi
	local libdll; libdll="$(find "$lib/bin" -name 'MyDotKtLib.dll' | head -1)"
	[[ -f "$libdll" ]] || { fail library "second-library dll not emitted"; return; }

	# (b) pack the emitted dll as a NuGet package into the feed. Build-time only; it lives under $WS, so NuGet's
	#     upward config walk picks up the isolated nuget.config just like the scenarios do.
	local pw="$WS/packwrap"; mkdir -p "$pw"
	cat > "$pw/PackWrap.csproj" <<EOF
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <IncludeBuildOutput>false</IncludeBuildOutput>
    <PackageId>MyDotKtLib</PackageId>
    <Version>$VER</Version>
    <NoWarn>NU5128;NU5127</NoWarn>
    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
  </PropertyGroup>
  <ItemGroup>
    <None Include="$libdll" Pack="true" PackagePath="lib/net10.0/" />
  </ItemGroup>
</Project>
EOF
	if ! (cd "$pw" && dotnet pack -o "$FEED" -v q --nologo >"$pw/pack.log" 2>&1); then
		fail library "packing the second library failed" "$(tail -20 "$pw/pack.log")"; return
	fi
	# The consumer restores it fresh into the isolated packages folder (nothing cached there yet).
	rm -rf "$WS/pkgs/mydotktlib"

	# (c) the consuming Library — PackageReference into the packed second library.
	local con="$WS/consumer"; mkdir -p "$con"; cp "$NUGET_CONFIG" "$con/nuget.config"
	cat > "$con/Consumer.ktproj" <<EOF
<Project Sdk="DotKt.Sdk/$VER">
  <PropertyGroup>
    <OutputType>Library</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>disable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="MyDotKtLib" Version="$VER" />
  </ItemGroup>
</Project>
EOF
	cat > "$con/consumer.kt" <<'EOF'
package consumer
import mylib.libValue
import mylib.Doubler
fun compute(): Int = libValue() + Doubler().twice(10)
EOF
	if ! (cd "$con" && dotnet build -v q --nologo >"$con/build.log" 2>&1); then
		fail library "consumer Library build failed" "$(tail -25 "$con/build.log")"; return
	fi
	local condll; condll="$(find "$con/bin" -name 'Consumer.dll' | head -1)"
	[[ -f "$condll" ]] || { fail library "consumer dll not emitted"; return; }
	# The emitted dll must declare the call — proves the cross-package reference resolved through bir2cir+ilemit.
	if [[ -x "$REFCHECK/bin/refcheck" || -f "$REFCHECK/bin/refcheck.dll" ]]; then
		if ! dotnet "$REFCHECK/bin/refcheck.dll" "$condll" "consumer.ConsumerKt" "compute" "$libdll" >"$con/refcheck.log" 2>&1; then
			fail library "emitted Consumer.dll missing consumer.ConsumerKt.compute" "$(cat "$con/refcheck.log")"; return
		fi
	fi
	pass library
}

# ---------------------------------------------------------------------------------------------------------
# Case: csharp-consumer + nullable-generic-shape (#86) — the C#-VISIBLE surface of the nullable-generic family.
# Every other gate consumes an emitted DotKt library AS KOTLIN, so it re-imports whatever the compiler emitted and
# is blind to the physical signature by construction. A real C# project cannot re-import anything: it binds the CLR
# signature literally, so it is the only lane that measures what the ABI actually IS. Two independent verdicts over
# one library, because they prune at different points:
#   nullable-generic-shape — METADATA: a nullable-generic slot's physical type is `System.Object` and its
#                            pre-erasure Kotlin shape rides the [KotlinNullableGeneric] carrier. A slot can be
#                            physically wrong and still run when nothing crosses it, so this is asserted directly.
#   csharp-consumer        — BEHAVIOR: a C# program COMPILES against those slots (passing `null` at T=int, which
#                            no bare-`T` slot admits) and RUNS to the expected output.
# Both are written against the POST-erasure ABI (the ABI break is sanctioned by the decision in #86), so they start
# XFAIL_PKG-listed and prune as the erasure lands — see the reasons in the baseline at the top of this file.
# ---------------------------------------------------------------------------------------------------------
case_csharp_consumer() {
	local d="$WS/csharp-consumer"; mkdir -p "$d"; cp "$NUGET_CONFIG" "$d/nuget.config"

	# (a) the Kotlin library: one declaration per nullable-generic POSITION the C# side then binds literally.
	local lib="$d/lib"; mkdir -p "$lib"
	cat > "$lib/NgLib.ktproj" <<EOF
<Project Sdk="DotKt.Sdk/$VER">
  <PropertyGroup>
    <OutputType>Library</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>disable</Nullable>
  </PropertyGroup>
</Project>
EOF
	cat > "$lib/Api.kt" <<'EOF'
package nglib

fun <T> firstOr(x: T?, d: T): T = x ?: d          // top-level T? PARAM
fun <T> pick(x: T, use: Boolean): T? = if (use) x else null   // top-level T? RETURN

class NBox<T>(val value: T?) {                    // T? CTOR PARAM + backing field
    fun orElse(d: T): T = value ?: d
}

class NgLists {
    fun boxedList(n: Int): List<Int?> = listOf(n, null, n * 2)   // List<Int?> RETURN
    fun sumPresent(xs: List<Int?>): Int {                        // List<Int?> PARAM
        var s = 0
        for (x in xs) if (x != null) s += x
        return s
    }
    fun joinPresent(xs: List<String?>): String {                 // the REFERENCE control: still IReadOnlyList<string>
        var s = ""
        for (x in xs) if (x != null) s = if (s == "") x else s + "," + x
        return s
    }
}

class NgArrays {
    fun boxedPair(n: Int): Array<Int?> {          // Array<Int?> RETURN
        val a = arrayOfNulls<Int>(3)
        a[0] = n
        a[2] = n * 2
        return a
    }
    fun sumPresent(xs: Array<Int?>): Int {        // Array<Int?> PARAM
        var s = 0
        for (x in xs) if (x != null) s += x
        return s
    }
}
EOF
	# The library building is a PRECONDITION of both verdicts, not one of the documented failures — so it gets
	# its own name. Absorbing it into the XFAIL-listed pair would let a broken library masquerade as the
	# expected #86 red and keep the gate green.
	if ! (cd "$lib" && dotnet build -v q --nologo >"$lib/build.log" 2>&1); then
		fail csharp-consumer-library "nullable-generic library build failed" "$(tail -25 "$lib/build.log")"; return
	fi
	local libdll; libdll="$(find "$lib/bin" -name 'NgLib.dll' | head -1)"
	if [[ ! -f "$libdll" ]]; then
		fail csharp-consumer-library "NgLib.dll not emitted"; return
	fi

	# (b) METADATA verdict. Each slot's physical type must be the erasure of its DECLARED Kotlin type, with the
	#     pre-erasure shape in the carrier. Single-quoted where a generic arity backtick appears — an unquoted one
	#     is command substitution and would silently assert against an empty type name.
	#
	#     The verdict is XFAIL-listed today, so it also carries a DRIFT check: the observed mismatch set must be
	#     exactly the documented one. Without it the XFAIL only says "some slot is wrong", and a new wrong slot,
	#     a differently-wrong slot, or a probe that stopped resolving would all be absorbed silently. The drift
	#     name is NOT baseline-listed, so any of those reddens. It is checked only while mismatches exist; once
	#     the erasure lands the set is empty, the verdict passes, and there is nothing left to drift.
	if ! have_refcheck; then
		fail refcheck-unavailable "the metadata verdict cannot be taken — refcheck did not build"
	else
		local shape_fail="" probe
		# owner | member | slot | expected CLR type | carrier
		for probe in \
			"nglib.ApiKt|firstOr|p0|System.Object|1" \
			"nglib.ApiKt|pick|ret|System.Object|1" \
			'nglib.NBox`1|.ctor|p0|System.Object|1' \
			"nglib.NgArrays|boxedPair|ret|System.Object[]|any" \
			"nglib.NgArrays|sumPresent|p0|System.Object[]|any" \
			'nglib.NgLists|boxedList|ret|System.Collections.Generic.IReadOnlyList`1[System.Object]|1' \
			'nglib.NgLists|sumPresent|p0|System.Collections.Generic.IReadOnlyList`1[System.Object]|1' \
			'nglib.NgLists|joinPresent|p0|System.Collections.Generic.IReadOnlyList`1[System.String]|0'
		do
			IFS='|' read -r pOwner pMember pSlot pType pCarrier <<<"$probe"
			if ! dotnet "$REFCHECK/bin/refcheck.dll" --shape "$libdll" "$pOwner" "$pMember" "$pSlot" "$pType" "$pCarrier" \
				>"$d/shape.log" 2>&1; then
				shape_fail+="$(cat "$d/shape.log")"$'\n'
			fi
		done
		if [[ -z "$shape_fail" ]]; then
			pass nullable-generic-shape
		else
			fail nullable-generic-shape "erased slot shape mismatch" "$shape_fail"
			# The System.Int32 assembly-qualification inside Nullable`1[[...]] carries a runtime version, so it
			# is collapsed before comparison; nothing else is normalized.
			local observed
			observed="$(printf '%s' "$shape_fail" | sed -E 's/System\.Nullable`1\[\[System\.Int32,[^]]*\]\]/System.Nullable`1[[System.Int32]]/g' | sed '/^$/d' | LC_ALL=C sort)"
			if [[ "$observed" != "$NG_SHAPE_EXPECTED" ]]; then
				fail nullable-generic-shape-drift "the observed slot-shape mismatches are not the documented set" \
					"$(printf -- '--- documented ---\n%s\n--- observed ---\n%s' "$NG_SHAPE_EXPECTED" "$observed")"
			fi
		fi
	fi

	# (c) BEHAVIOR verdict: a plain C# Exe ProjectReferences the .ktproj and binds the emitted signatures literally.
	local app="$d/app"; mkdir -p "$app"
	cat > "$app/CsConsumer.csproj" <<'EOF'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>disable</Nullable>
    <ImplicitUsings>disable</ImplicitUsings>
    <AssemblyName>CsConsumer</AssemblyName>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="../lib/NgLib.ktproj" />
  </ItemGroup>
</Project>
EOF
	cat > "$app/Program.cs" <<'EOF'
using System;
using nglib;

// Every line below needs the nullable-generic slot to be `object`: a bare `T` slot at T=int cannot take `null`,
// so a wrong ABI fails at COMPILE here rather than producing a wrong value. The array lines additionally need
// Array<Int?> to be `object[]` — `Nullable<int>[]` is not array-compatible with it.
class Program {
    static int Main() {
        int viaNull = ApiKt.firstOr<int>(null, 7);
        int viaValue = ApiKt.firstOr<int>(3, 7);
        object absent = ApiKt.pick<int>(5, false);
        object present = ApiKt.pick<int>(5, true);
        int boxNull = new NBox<int>(null).orElse(9);
        int boxValue = new NBox<int>(4).orElse(9);
        var lists = new NgLists();
        // A `List<Int?>` is an `IReadOnlyList<object>`: a C# caller can hand it a list holding a null at T=int,
        // which no `IReadOnlyList<int>` slot admits. The `List<String?>` control still binds as `string`.
        System.Collections.Generic.IReadOnlyList<object> boxedList = lists.boxedList(4);
        int listSum = lists.sumPresent(new object[] { 1, null, 5 });
        string joined = lists.joinPresent(new string[] { "a", null, "b" });
        var arrays = new NgArrays();
        object[] boxed = arrays.boxedPair(4);
        int sum = arrays.sumPresent(boxed);
        int noneSum = arrays.sumPresent(new object[] { null, null });
        Console.WriteLine("{0} {1} {2} {3} {4} {5} {6} {7} {8} {9} {10} {11} {12}",
            viaNull, viaValue, absent == null ? "null" : absent.ToString(), present,
            boxNull, boxValue, boxed.Length, boxed[1] == null ? "null" : "set", sum, noneSum,
            boxedList.Count, listSum, joined);
        return 0;
    }
}
EOF
	local expected="7 3 null 5 9 4 3 null 12 0 3 6 a,b" actual rc=0
	actual="$(run_project "$app" "$app/run.err")" || rc=$?
	if (( rc == 0 )) && [[ "$actual" == "$expected" ]]; then pass csharp-consumer; return; fi

	if (( rc != 0 )); then fail csharp-consumer "C# consumer build/run exit $rc" "$(printf -- '--- expected ---\n%s\n--- stdout ---\n%s\n--- stderr/build ---\n%s' "$expected" "$actual" "$(tail -30 "$app/run.err" 2>/dev/null)")"
	else fail csharp-consumer "output mismatch" "$(printf -- '--- expected ---\n%s\n--- actual ---\n%s' "$expected" "$actual")"; fi

	# The verdict above is XFAIL-listed, so on its own it only says "the C# consumer did not work" — which a
	# restore failure, a missing SDK, a changed diagnostic or a NEW diagnostic would each satisfy just as well
	# as the documented #86 ABI break. So the csc diagnostics are compared against the documented set, exactly:
	# an extra, a missing, or a differently-worded one reddens under a name that is NOT baseline-listed.
	# Normalized to `line N: error CSxxxx: message` — the leading path and the trailing [project] suffix are
	# location, not claim, and MSBuild prints each diagnostic twice (once inline, once in its error summary),
	# which is why the set is deduplicated. The message text itself is compared verbatim, brackets included.
	local observed
	observed="$(grep -E ': error CS[0-9]+: ' "$app/run.err" 2>/dev/null \
		| sed -E 's/^[^(]*\(([0-9]+),[0-9]+\): error/line \1: error/; s/ \[[^]]*\.csproj\]$//; s/[[:space:]]+$//' \
		| LC_ALL=C sort -u)"
	if [[ "$observed" != "$CS_EXPECTED_DIAGNOSTICS" ]]; then
		fail csharp-consumer-diagnostics "the C# consumer did not fail with the documented diagnostics" \
			"$(printf -- '--- documented ---\n%s\n--- observed ---\n%s\n--- raw build output ---\n%s' \
				"$CS_EXPECTED_DIAGNOSTICS" "$observed" "$(tail -40 "$app/run.err" 2>/dev/null)")"
	fi
}

# ---------------------------------------------------------------------------------------------------------
# Case: coroutine-cross-module — #137's end-user path. The producer and consumer are separate Kotlin
# compilations, both resolved through the freshly packed SDK. The producer's Task.Delay is incomplete when
# startCoroutine returns to the consumer, so this covers a real cross-assembly suspend/resume rather than the
# synchronous fast path. The producer is packed as an ordinary NuGet dependency to exercise the same dll2klib
# re-import and reference-asset path an end user hits. Both DotKt-emitted assemblies must also be ILVerify-clean.
# ---------------------------------------------------------------------------------------------------------
case_coroutine_cross_module() {
	# (a) Build the packaged-SDK Kotlin producer.
	local lib="$WS/async-lib"; mkdir -p "$lib"; cp "$NUGET_CONFIG" "$lib/nuget.config"
	cat > "$lib/DotKt.AsyncGate.ktproj" <<EOF
<Project Sdk="DotKt.Sdk/$VER">
  <PropertyGroup>
    <OutputType>Library</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>disable</Nullable>
  </PropertyGroup>
</Project>
EOF
	cat > "$lib/AsyncGate.kt" <<'EOF'
package asyncgate

import System.Threading.Tasks.Task

suspend fun delayedValue(): Int {
    Task.Delay(500).await()
    return 42
}
EOF
	if ! (cd "$lib" && dotnet build -v q --nologo >"$lib/build.log" 2>&1); then
		fail coroutine-cross-module "producer build failed" "$(tail -30 "$lib/build.log")"; return
	fi
	local libdll; libdll="$(find "$lib/bin" -name 'DotKt.AsyncGate.dll' | head -1)"
	[[ -f "$libdll" ]] || { fail coroutine-cross-module "producer dll not emitted"; return; }

	# (b) Put the emitted producer in the same isolated feed as a normal runtime package.
	local pw="$WS/async-packwrap"; mkdir -p "$pw"
	cat > "$pw/PackWrap.csproj" <<EOF
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <IncludeBuildOutput>false</IncludeBuildOutput>
    <PackageId>DotKt.AsyncGate</PackageId>
    <Version>$VER</Version>
    <NoWarn>NU5128;NU5127</NoWarn>
    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
  </PropertyGroup>
  <ItemGroup>
    <None Include="$libdll" Pack="true" PackagePath="lib/net10.0/" />
  </ItemGroup>
</Project>
EOF
	if ! (cd "$pw" && dotnet pack -o "$FEED" -v q --nologo >"$pw/pack.log" 2>&1); then
		fail coroutine-cross-module "packing producer failed" "$(tail -30 "$pw/pack.log")"; return
	fi
	rm -rf "$WS/pkgs/dotkt.asyncgate"

	# (c) Compile a separate Kotlin consumer from the packaged SDK and packaged producer.
	local con="$WS/async-consumer"; mkdir -p "$con"; cp "$NUGET_CONFIG" "$con/nuget.config"
	cat > "$con/AsyncConsumer.ktproj" <<EOF
<Project Sdk="DotKt.Sdk/$VER">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>disable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="DotKt.AsyncGate" Version="$VER" />
  </ItemGroup>
</Project>
EOF
	cat > "$con/Main.kt" <<'EOF'
@file:Suppress("UNCHECKED_CAST")

package asyncconsumer

import System.Threading.Monitor
import asyncgate.delayedValue
import kotlin.coroutines.Continuation
import kotlin.coroutines.CoroutineContext
import kotlin.coroutines.EmptyCoroutineContext
import kotlin.coroutines.startCoroutine

private var observedSuspension = false

private class GateSink : Continuation<Any?> {
    var done = false
    var value: Any? = null
    var exception: Throwable? = null

    override val context: CoroutineContext
        get() = EmptyCoroutineContext

    override fun resumeWith(result: Result<Any?>) {
        Monitor.Enter(this)
        try {
            value = result.getOrNull()
            exception = result.exceptionOrNull()
            done = true
            Monitor.Pulse(this)
        } finally {
            Monitor.Exit(this)
        }
    }
}

private fun <T> blockOnObserved(block: suspend () -> T): T {
    val sink = GateSink()
    block.startCoroutine(sink)
    // Task.Delay(500) is still incomplete here. sink.done must be false (and observedSuspension true) before
    // the producer resumes from another assembly; a synchronous-only cross-module bridge cannot satisfy the gate.
    observedSuspension = !sink.done
    Monitor.Enter(sink)
    try {
        while (!sink.done) Monitor.Wait(sink)
    } finally {
        Monitor.Exit(sink)
    }
    sink.exception?.let { throw it }
    return sink.value as T
}

fun main() {
    val value = blockOnObserved { delayedValue() }
    println("packaged coroutine ok: $value suspended=$observedSuspension")
}
EOF
	local expected="packaged coroutine ok: 42 suspended=True" actual rc=0
	actual="$(run_project "$con" "$con/run.err" 20s)" || rc=$?
	if (( rc != 0 )); then
		fail coroutine-cross-module "consumer run exit $rc" \
			"$(printf -- '--- expected ---\n%s\n--- stdout ---\n%s\n--- stderr ---\n%s' "$expected" "$actual" "$(tail -30 "$con/run.err" 2>/dev/null)")"
		return
	fi
	if [[ "$actual" != "$expected" ]]; then
		fail coroutine-cross-module "output mismatch" \
			"$(printf -- '--- expected ---\n%s\n--- actual ---\n%s' "$expected" "$actual")"
		return
	fi

	local condll; condll="$(find "$con/bin" -name 'AsyncConsumer.dll' | head -1)"
	[[ -f "$condll" ]] || { fail coroutine-cross-module "consumer dll not emitted"; return; }
	if ! bash "$ROOT/tests/run-ilverify.sh" "$libdll" "$condll" >"$con/ilverify.log" 2>&1; then
		fail coroutine-cross-module "ILVerify failed" "$(tail -40 "$con/ilverify.log")"; return
	fi
	pass coroutine-cross-module
}

# ---------------------------------------------------------------------------------------------------------
# Case: mpp — a packaged MULTIPLATFORM Exe via Sdk="DotKt.Sdk.Mpp" (common `expect` + clr `actual`). The Mpp
# SDK nests a version-LESS import of the base DotKt.Sdk, whose version the NuGet SDK resolver reads ONLY from
# global.json's msbuild-sdks — so a global.json pinning both is REQUIRED (and part of what this covers).
# ---------------------------------------------------------------------------------------------------------
case_mpp() {
	local d="$WS/mpp"; mkdir -p "$d/common" "$d/clr"; cp "$NUGET_CONFIG" "$d/nuget.config"
	cat > "$d/global.json" <<EOF
{ "msbuild-sdks": { "DotKt.Sdk.Mpp": "$VER", "DotKt.Sdk": "$VER" } }
EOF
	cat > "$d/App.ktproj" <<'EOF'
<Project Sdk="DotKt.Sdk.Mpp">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>disable</Nullable>
  </PropertyGroup>
</Project>
EOF
	cat > "$d/common/Greeter.kt" <<'EOF'
package mpp.greeter
expect class Greeter { fun say(): String }
EOF
	cat > "$d/clr/Greeter.kt" <<'EOF'
package mpp.greeter
actual class Greeter { actual fun say(): String = "Hello from the CLR actual (packaged MPP SDK)" }
EOF
	cat > "$d/clr/Main.kt" <<'EOF'
package mpp.greeter
fun main() { println(Greeter().say()) }
EOF
	local expected="Hello from the CLR actual (packaged MPP SDK)" actual rc=0
	actual="$(run_project "$d" "$d/run.err")" || rc=$?
	if (( rc != 0 )); then fail mpp "run exit $rc" "$(printf -- '--- expected ---\n%s\n--- stdout ---\n%s\n--- stderr ---\n%s' "$expected" "$actual" "$(tail -30 "$d/run.err" 2>/dev/null)")"
	elif [[ "$actual" == "$expected" ]]; then pass mpp
	else fail mpp "output mismatch" "$(printf -- '--- expected ---\n%s\n--- actual ---\n%s' "$expected" "$actual")"; fi
}

# ---------------------------------------------------------------------------------------------------------
# Case: template — the #53 reproducer. Install the packed DotKt.Templates via `dotnet new install`, scaffold a
# project with `dotnet new dotkt-cli`, and build+run it from the isolated feed. A stale template Sdk pin (the
# 0.9.5-while-release-is-0.9.6 drift) makes restore pull a version the feed does not carry -> this fails. The
# generated project file must pin the RELEASE version (proves the pack-time Sdk-version substitution worked).
# Installed from the exact packed nupkg into this case's own template hive.
# ---------------------------------------------------------------------------------------------------------
case_template() {
	local d="$WS/template"; mkdir -p "$d"
	local hive="$d/hive"
	local nupkg; nupkg="$(find "$FEED" -maxdepth 1 -name "DotKt.Templates.$VER.nupkg" | head -1)"
	[[ -f "$nupkg" ]] || { fail template "DotKt.Templates.$VER.nupkg not packed"; return; }
	if ! dotnet_new "$hive" install "$nupkg" >"$d/install.log" 2>&1; then
		fail template "dotnet new install failed" "$(tail -20 "$d/install.log")"; return
	fi
	if ! hive_isolated template "$hive"; then return 0; fi
	local proj="$d/hello"; rm -rf "$proj"
	if ! dotnet_new "$hive" dotkt-cli -o "$proj" >"$d/new.log" 2>&1; then
		fail template "dotnet new dotkt-cli failed" "$(tail -20 "$d/new.log")"; return
	fi
	cp "$NUGET_CONFIG" "$proj/nuget.config"
	# The scaffolded project must pin the release SDK version (the #53 drift shipped it pinned to a stale one).
	if ! grep -q "Sdk=\"DotKt.Sdk/$VER\"" "$proj"/*.csproj 2>/dev/null; then
		fail template "generated project does not pin DotKt.Sdk/$VER" "$(cat "$proj"/*.csproj 2>/dev/null)"; return
	fi
	local expected="Hello, World, from DotKt — Kotlin on .NET!" actual rc=0
	actual="$(run_project "$proj" "$proj/run.err")" || rc=$?
	if (( rc != 0 )); then fail template "run exit $rc" "$(printf -- '--- expected ---\n%s\n--- stdout ---\n%s\n--- stderr ---\n%s' "$expected" "$actual" "$(tail -30 "$proj/run.err" 2>/dev/null)")"
	elif [[ "$actual" == "$expected" ]]; then pass template
	else fail template "output mismatch" "$(printf -- '--- expected ---\n%s\n--- actual ---\n%s' "$expected" "$actual")"; fi
}

# ---------------------------------------------------------------------------------------------------------
# Case: mpp-template — the #133 reproducer. Install DotKt.Templates, scaffold `dotnet new dotkt-mpp`, and
# build+run it WITHOUT hand-writing a global.json. The MPP SDK nests a version-LESS import of the base
# DotKt.Sdk whose version the NuGet resolver reads ONLY from global.json — so before #133 a scaffolded MPP
# project failed to resolve the nested SDK. The template now ships that global.json (pinning both SDKs to the
# release version, substituted at pack), so this must build + run out of the box. Distinct from case_mpp
# (which hand-writes the global.json): this proves the SHIPPED template carries it.
# ---------------------------------------------------------------------------------------------------------
case_mpp_template() {
	local d="$WS/mpp-template"; mkdir -p "$d"
	local hive="$d/hive"
	local nupkg; nupkg="$(find "$FEED" -maxdepth 1 -name "DotKt.Templates.$VER.nupkg" | head -1)"
	[[ -f "$nupkg" ]] || { fail mpp-template "DotKt.Templates.$VER.nupkg not packed"; return; }
	if ! dotnet_new "$hive" install "$nupkg" >"$d/install.log" 2>&1; then
		fail mpp-template "dotnet new install failed" "$(tail -20 "$d/install.log")"; return
	fi
	if ! hive_isolated mpp-template "$hive"; then return 0; fi
	local proj="$d/hello-mpp"; rm -rf "$proj"
	if ! dotnet_new "$hive" dotkt-mpp -o "$proj" >"$d/new.log" 2>&1; then
		fail mpp-template "dotnet new dotkt-mpp failed" "$(tail -20 "$d/new.log")"; return
	fi
	cp "$NUGET_CONFIG" "$proj/nuget.config"
	# The scaffolded MPP project must ship the global.json pinning both SDKs to the release version (the #133 fix).
	if ! grep -q "\"DotKt.Sdk.Mpp\": \"$VER\"" "$proj/global.json" 2>/dev/null || ! grep -q "\"DotKt.Sdk\": \"$VER\"" "$proj/global.json" 2>/dev/null; then
		fail mpp-template "scaffolded global.json does not pin both SDKs to $VER" "$(cat "$proj/global.json" 2>/dev/null)"; return
	fi
	local expected="Hello, World, from a DotKt multiplatform app on .NET!" actual rc=0
	actual="$(run_project "$proj" "$proj/run.err")" || rc=$?
	if (( rc != 0 )); then fail mpp-template "run exit $rc" "$(printf -- '--- expected ---\n%s\n--- stdout ---\n%s\n--- stderr ---\n%s' "$expected" "$actual" "$(tail -30 "$proj/run.err" 2>/dev/null)")"
	elif [[ "$actual" == "$expected" ]]; then pass mpp-template
	else fail mpp-template "output mismatch" "$(printf -- '--- expected ---\n%s\n--- actual ---\n%s' "$expected" "$actual")"; fi
}

# ---- issue #163 self-test: a packaged Exe whose main prints the EXPECTED text then throws MUST be REJECTED.
# Drives the real run_project capture path from the isolated feed and asserts a non-zero status is observed. ----
selftest() {
	local d="$WS/selftest"; mkdir -p "$d"; cp "$NUGET_CONFIG" "$d/nuget.config"
	cat > "$d/App.ktproj" <<EOF
<Project Sdk="DotKt.Sdk/$VER">
  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><Nullable>disable</Nullable></PropertyGroup>
</Project>
EOF
	printf 'fun main() { println("SELFTEST-EXPECTED"); throw RuntimeException("boom after print") }\n' > "$d/app.kt"
	local rc=0
	run_project "$d" "$d/run.err" >/dev/null || rc=$?
	if (( rc == 0 )); then
		echo "PACKAGED-SDK GATE RED — exit-status self-test FAILED: a print-then-crash packaged exe was accepted"; exit 1
	fi
	info "self-test OK: a print-then-crash packaged exe is REJECTED (run exit $rc)"
}
selftest

case_exe
case_multitarget_klib_references
case_library
case_csharp_consumer
case_coroutine_cross_module
case_mpp
case_template
case_mpp_template

echo "------------------------------------"
xfail_diff pkgsdk XFAIL_PKG "${FAILS[@]}"
if xfail_gate_is_clean; then
	echo "PACKAGED-SDK OK"
else
	(( ${#XFAIL_NEW[@]} == 0 )) || echo "PACKAGED-SDK NEW-FAIL: ${XFAIL_NEW[*]}"
	(( ${#XFAIL_FIXED[@]} == 0 )) || echo "PACKAGED-SDK STALE XFAIL: ${XFAIL_FIXED[*]}"
	echo "PACKAGED-SDK FAIL"
	exit 1
fi
