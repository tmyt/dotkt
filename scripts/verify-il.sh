#!/usr/bin/env bash
# Direct-IL backend differential: Kotlin -> BIR -> ilemit -> CIL -> dotnet, asserted vs the C# oracle.
set -euo pipefail
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
STDLIB="$(find "$HOME/.gradle/caches" -name 'kotlin-stdlib-2.2.0.jar' | head -1)"
CORO="$(find "$HOME/.gradle/caches" -name 'kotlinx-coroutines-core-jvm-1.8.0.jar' | head -1)"
CP="$STDLIB:$CORO"
fail=0

# Build the compiler launcher ONCE (a plain Java app). Per-sample invokes then cost ~2s of JVM startup instead
# of ~9s for `gradlew --no-daemon :kotc:run` — a ~4x speedup on the dominant compile step.
"$ROOT/gradlew" -q :kotc:installDist >/dev/null 2>&1
LAUNCHER="$ROOT/toolchain/kotc/build/install/kotc/bin/kotc"

# Run samples concurrently (each compile is an independent ~2s JVM startup). A job pool caps parallelism; results
# (FAIL markers, runtime-dll paths for the ilverify phase) cross back from the subshells via files.
JOBS="$(nproc 2>/dev/null || echo 4)"; (( JOBS > 6 )) && JOBS=6
gate() { while (( $(jobs -rp | wc -l) >= JOBS )); do wait -n 2>/dev/null || true; done; }
rm -f "$ROOT"/build/fail-* "$ROOT"/build/refdll-* 2>/dev/null || true

dotnet build "$ROOT/toolchain/ilemit" -c Release -o "$ROOT/build/ilemit-bin" -v q --nologo >/dev/null

# DotKt.Runtime: the runtime assembly for promoted Kotlin lowerings (printf->composite format, AND the
# kotlin.coroutines core — Continuation/CoroutineContext/Result/Unit/intercepted + sequence/Flow/Channel/select
# helpers — in the DotKt.Coroutines namespace). Every compiled assembly references it (the SDK auto-references it),
# so pass it globally to ilemit + ilverify, and copy it next to each emitted dll for the run phase.
dotnet build "$ROOT/runtime/DotKt.Runtime" -c Release -o "$ROOT/build/dotkt-runtime" -v q --nologo >/dev/null 2>&1
DOTKT_RT="$ROOT/build/dotkt-runtime/DotKt.Runtime.dll"

# S5 FIR-injection metadata for samples that inherit a real .NET base type (façade-free).
dotnet build "$ROOT/toolchain/facadegen" -c Release -o "$ROOT/build/facadegen-bin" -v q --nologo >/dev/null 2>&1
EXCMETA="$ROOT/build/exc.meta"
dotnet "$ROOT/build/facadegen-bin/facadegen.dll" --meta "$EXCMETA" System.Exception System.Console >/dev/null 2>&1
COLLMETA="$ROOT/build/coll.meta"
dotnet "$ROOT/build/facadegen-bin/facadegen.dll" --meta "$COLLMETA" System.Collections.ObjectModel.Collection >/dev/null 2>&1
OBSCOLLMETA="$ROOT/build/obscoll.meta"
dotnet "$ROOT/build/facadegen-bin/facadegen.dll" --meta "$OBSCOLLMETA" System.Collections.ObjectModel.ObservableCollection >/dev/null 2>&1
GMMETA="$ROOT/build/gm.meta"
dotnet "$ROOT/build/facadegen-bin/facadegen.dll" --meta "$GMMETA" System.Runtime.CompilerServices.Unsafe System.Runtime.CompilerServices.RuntimeHelpers System.Collections.ObjectModel.Collection >/dev/null 2>&1

# DotKt.Stdlib: the real-Kotlin stdlib ops migrated off the COLLECTION_OPS lowering (getOrElse, ...). Auto-referenced by
# every case (its [KotlinFileClass] facades injected via `facadegen --scan-asm`, and ilemit `--ref`), mirroring how a
# .ktproj gets DotKt.Stdlib. A call to a migrated op routes to the real Kotlin body instead of the retired LINQ lowering.
bash "$ROOT/scripts/build-dotkt-stdlib.sh" >/dev/null 2>&1
STDLIB_DLL="$ROOT/build/dotkt-stdlib/DotKt.Stdlib.dll"
STDLIB_META="$ROOT/build/stdlib.meta"
_REFPACK="$(dirname "$(find /usr/share/dotnet/packs/Microsoft.NETCore.App.Ref -name 'System.Runtime.dll' -path '*net10.0*' | head -1)")"
dotnet "$ROOT/build/facadegen-bin/facadegen.dll" --meta "$STDLIB_META" --refs "$(ls "$_REFPACK"/*.dll | tr '\n' ';')$STDLIB_DLL;$DOTKT_RT" --scan-asm "$STDLIB_DLL" >/dev/null 2>&1

declare -A REFDLL=()   # sample name -> external runtime dll it references (for ilverify -r)

# Build a sample's <srcDir>/runtime.cs into a referenced .NET assembly (name from <runtimeAsm>); echo its path.
build_runtime() { # <srcDir> <runtimeAsm>
	local srcdir="$1" rasm="$2" rt="$ROOT/build/rt-$rasm"
	rm -rf "$rt"; mkdir -p "$rt"
	cp "$srcdir/runtime.cs" "$rt/runtime.cs"
	printf '%s\n' "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework><AssemblyName>$rasm</AssemblyName><Nullable>disable</Nullable></PropertyGroup></Project>" > "$rt/rt.csproj"
	dotnet build "$rt" -c Release -o "$rt/out" -v q --nologo >/dev/null 2>&1
	echo "$rt/out/$rasm.dll"
}

# Inject (façade-free) a sample's own runtime types AND reference the runtime dll: build runtime.cs, scan the
# .kt imports into a metadata file (facadegen --meta --scan), compile with it, then ilemit with --ref.
il_check_inject() { # <name> <asm> <srcDir> <expected> <runtimeAsm>
	gate
	{ name="$1"; asm="$2"; src="$3"; expected="$4"; rasm="$5"
		birdir="$ROOT/build/bir-$name"; ildir="$ROOT/build/il-$name"; meta="$ROOT/build/$name.meta"
		refdll="$(build_runtime "$src" "$rasm")"; echo "$refdll" > "$ROOT/build/refdll-$name"
		RD="$(ls -d /usr/share/dotnet/shared/Microsoft.NETCore.App/*/ | tail -1)"
		implist="$ROOT/build/$name.imports"
		"$LAUNCHER" --scan-imports --output "$implist" "$src"/*.kt >/dev/null 2>&1
		dotnet "$ROOT/build/facadegen-bin/facadegen.dll" --meta "$meta" --refs "$(ls ${RD}*.dll | tr '\n' ';');$refdll" --import-list "$implist" >/dev/null 2>&1
		rm -rf "$birdir" "$ildir"; mkdir -p "$birdir" "$ildir"
		if ! CLR_TYPES_METADATA="$meta" "$LAUNCHER" $src -no-stdlib -classpath "$CP" -d $birdir >/dev/null 2>&1; then
			echo "FAIL  il:$name (compile error)"; touch "$ROOT/build/fail-$name"; exit 0; fi
		if ! dotnet "$ROOT/build/ilemit-bin/ilemit.dll" "$ildir" "$asm" --ref "$DOTKT_RT" --ref "$refdll" "$birdir"/*.bir.json >/dev/null 2>&1; then
			echo "FAIL  il:$name (ilemit error)"; touch "$ROOT/build/fail-$name"; exit 0; fi
		cp "$refdll" "$ildir/"
		actual="$(dotnet "$ildir/$asm.dll" 2>/dev/null)"
		if [[ "$actual" == "$expected" ]]; then echo "PASS  il:$name"; else
			echo "FAIL  il:$name"; printf -- '--- expected ---\n%s\n--- actual ---\n%s\n' "$expected" "$actual"; touch "$ROOT/build/fail-$name"; fi
	} &
}

il_check() { # <name> <asm> <srcArg> <expected> [metadataFile]
	gate
	{ name="$1"; asm="$2"; src="$3"; expected="$4"; meta="${5:-}"
		birdir="$ROOT/build/bir-$name"; ildir="$ROOT/build/il-$name"
		rm -rf "$birdir" "$ildir"; mkdir -p "$birdir" "$ildir"
		# Auto-reference DotKt.Stdlib: merge its injection meta with any case-specific meta, and --ref the dll so a
		# migrated stdlib op (getOrElse, ...) resolves to its real Kotlin body instead of the retired LINQ lowering.
		usemeta="$STDLIB_META"
		if [[ -n "$meta" ]]; then usemeta="$ROOT/build/meta-$name"; cat "$STDLIB_META" "$meta" > "$usemeta"; fi
		if ! CLR_TYPES_METADATA="$usemeta" "$LAUNCHER" $src -no-stdlib -classpath "$CP" -d $birdir >/dev/null 2>&1; then
			echo "FAIL  il:$name (compile error)"; touch "$ROOT/build/fail-$name"; exit 0; fi
		if ! dotnet "$ROOT/build/ilemit-bin/ilemit.dll" "$ildir" "$asm" --ref "$DOTKT_RT" --ref "$STDLIB_DLL" "$birdir"/*.bir.json >/dev/null 2>&1; then
			echo "FAIL  il:$name (ilemit error)"; touch "$ROOT/build/fail-$name"; exit 0; fi
		cp "$DOTKT_RT" "$STDLIB_DLL" "$ildir/"
		actual="$(dotnet "$ildir/$asm.dll" 2>/dev/null)"
		if [[ "$actual" == "$expected" ]]; then echo "PASS  il:$name"; else
			echo "FAIL  il:$name"; printf -- '--- expected ---\n%s\n--- actual ---\n%s\n' "$expected" "$actual"; touch "$ROOT/build/fail-$name"; fi
	} &
}

# Multiplatform check: expect/actual compiled as common + platform fragments in one invocation (-Xcommon-sources),
# plus kotlinx.atomicfu mapped to the DotKt.Coroutines wrappers. <commonGlob> = the common source file(s).
il_check_mpp() { # <name> <asm> <srcDir> <commonFile> <expected>
	gate
	{ name="$1"; asm="$2"; src="$3"; common="$4"; expected="$5"
		birdir="$ROOT/build/bir-$name"; ildir="$ROOT/build/il-$name"
		rm -rf "$birdir" "$ildir"; mkdir -p "$birdir" "$ildir"
		if ! "$LAUNCHER" "$src"/*.kt -Xcommon-sources="$src/$common" -no-stdlib -classpath "$CP" -d $birdir >/dev/null 2>&1; then
			echo "FAIL  il:$name (compile error)"; touch "$ROOT/build/fail-$name"; exit 0; fi
		if ! dotnet "$ROOT/build/ilemit-bin/ilemit.dll" "$ildir" "$asm" --ref "$DOTKT_RT" "$birdir"/*.bir.json >/dev/null 2>&1; then
			echo "FAIL  il:$name (ilemit error)"; touch "$ROOT/build/fail-$name"; exit 0; fi
		cp "$DOTKT_RT" "$ildir/"
		actual="$(dotnet "$ildir/$asm.dll" 2>/dev/null)"
		if [[ "$actual" == "$expected" ]]; then echo "PASS  il:$name"; else
			echo "FAIL  il:$name"; printf -- '--- expected ---\n%s\n--- actual ---\n%s\n' "$expected" "$actual"; touch "$ROOT/build/fail-$name"; fi
	} &
}
il_check_mpp expect Expect "$ROOT/cases/il-expect" common.kt "$(printf 'CLR\n42\nhello from CLR\n2\nTrue\n10\n15\nTrue\nhi')"

il_check kseq  KSeq  "$ROOT/cases/il-kseq"  "$(printf '1,2,3\n1,4,9,16\n0,1\n0,1,2,3,4,5,6')"
il_check kgenseq KGenSeq "$ROOT/cases/il-kgenseq" "$(printf '1,2,3,4,5\na,aa,aaa\n1,2,3')"
il_check kflow KFlow "$ROOT/cases/il-kflow"  "$(printf '1\n2\n3')"
il_check kgflow KGFlow "$ROOT/cases/il-kgflow" "$(printf '1\n2\n3')"
il_check m0    M0Kt  "$ROOT/cases/m0/M0.kt"  "$(printf 'sum = 5\nzero\nn=1\nn=2')"
il_check mc1   MC1   "$ROOT/cases/m-c1"      "$(printf 'c = (4, 6)\na.d2 = 25\nrect area=30')"
il_check iface Iface "$ROOT/cases/il-iface"  "$(printf 'Hello\nKonnichiwa')"
il_check xfaceimpl XFace "$ROOT/cases/il-xfaceimpl" "1"   # cross-file + namespaced interface impl/dispatch (FindMethod key regression)
il_check genhof XHof "$ROOT/cases/il-genhof/app.kt" "$(printf '1\n2\n3')"   # generic fn: (T)->Unit over List<T> (TypeBuilderInstantiation.GetMethod regression)
il_check genclosure GenClo "$ROOT/cases/il-genclosure/app.kt" "$(printf '1\nfn:2\n3\n4\nret:5\nlf:6')"   # closure in a generic fn capturing T-typed values (generic closure class regression)
il_check enum  Enum  "$ROOT/cases/il-enum"   "$(printf 'red\ngreen\nblue')"
il_check m2    M2    "$ROOT/cases/m2"         "$(printf 'max(3, 7) = 7\nmin(3, 7) = 3\nabs(-9) = 9')"
il_check mi1   MI1   "$ROOT/cases/m-i1"       "$(printf 'Hello, CLR 42\nlength = 13')"
il_check for   ForT  "$ROOT/cases/il-for"     "$(printf 'sum 1..5 = 15\ncountdown 5 = 54321')"
il_check exc   Exc   "$ROOT/cases/il-exc"     "$(printf 'safeDiv(10,2) = 5\nsafeDiv(1,0) = -1')"
il_check ops   Ops   "$ROOT/cases/il-ops"     "$(printf '3\n2\n7\n3\n16\n15\n-1\n3\n5')"
il_check math  MathT "$ROOT/cases/il-math"    "$(printf '9\n7\n3\n4')"
il_check str   Str   "$ROOT/cases/il-str"     "$(printf 'HELLO\nhello\nhi\nello\nTrue\nTrue')"
il_check cp    Cp    "$ROOT/cases/il-cp"      "$(printf '50\n3.5\nTrue\nTrue\nX')"
il_check ext   Ext   "$ROOT/cases/il-ext"     "$(printf '21\nHI')"
il_check arr   Arr   "$ROOT/cases/il-arr"     "$(printf '10\n30\n99\n3\n139\n139')"
il_check lam   Lam   "$ROOT/cases/il-lambda"  "$(printf '42\n12')"
il_check clo   Clo   "$ROOT/cases/il-closure" "$(printf '15\n105\n17')"
il_check scope Sc    "$ROOT/cases/il-scope"   "$(printf '10\n6\n9\n10\n10\n7')"
il_check coll  Coll  "$ROOT/cases/il-coll"    "$(printf '5\n5\n3\n2\n3\nTrue\nTrue\n3\n1\n4\nTrue\n5')"
il_check coll2 Coll2 "$ROOT/cases/il-coll2"   "$(printf '10\n1-2-3-4\n1, 2, 3, 4\n100')"
il_check coll3 Coll3 "$ROOT/cases/il-coll3"   "$(printf '60\n6')"
il_check seq   Seq   "$ROOT/cases/il-seq"     "$(printf '6,12\n16\n3\n27\n10-20-30\n1,2,3\n4,5,6\n3')"
il_check char  Char  "$ROOT/cases/il-char"    "$(printf 'True\nTrue\nTrue\nTrue\nA\nz\nTrue\nTrue\n97\nb')"
il_check sort  Sort  "$ROOT/cases/il-sort"    "$(printf '9,6,5,4,3,2,1,1\na,dd,bbb,cccc\ncccc,bbb,dd,a')"
il_check funref Funref "$ROOT/cases/il-funref" "$(printf '2,4,6\n1,4,9,16,25,36\nHi, Kotlin\n105\n107\ncalc100\n203\n42')"
il_check mapdes MapDes "$ROOT/cases/il-mapdes" "$(printf '10\n60\n13\nx=1\ny=2\nz=3\ntotal=6')"
il_check unsgn Unsigned "$ROOT/cases/il-unsigned" "$(printf '4000000100\n4000000000\n18000000000000000000\n60000\n250')"
il_check regex Regex "$ROOT/cases/il-regex" "$(printf 'True\nFalse\na#b#c#\na_b_c\nTrue\nFalse\n42\n')"
il_check langtail LangTail "$ROOT/cases/il-langtail" "$(printf '6\nhi\nint:42\nstr:3\nbig:5\nsmall\n700\n9')"
il_check enumbody EnumBody "$ROOT/cases/il-enumbody" "$(printf '+: 8\n-: 4\n*: 12\nPLUS\n9')"
il_check bytearg ByteArg "$ROOT/cases/il-bytearg" "$(printf '5\n3\n7\n9\n4\n100\n-2')"
il_check iterable Iterable "$ROOT/cases/il-iterable" "$(printf '321\n6\n6')"
il_check customexc CustomExc "$ROOT/cases/il-customexc" "$(printf 'error -5\ncode=-5\ncaught:boom\n42')"
il_check comparator Comparator "$ROOT/cases/il-comparator" "$(printf -- '-3\n5\n0')"
il_check use Use "$ROOT/cases/il-use" "$(printf 'close abcd\nn=4\nclose x\ncaught:boom')"
il_check comparable Comparable "$ROOT/cases/il-comparable" "$(printf 'a<b\nc>b\na<=a\n-3\n1.2,1.5,2.0')"
il_check charseq CS "$ROOT/cases/il-charseq" "$(printf '5\ne\n3\ne\n5')"
il_check substr Substr "$ROOT/cases/il-substr" "$(printf 'ell\nworld\nhello\nworld')"
il_check result Result "$ROOT/cases/il-result" "$(printf 'True\n10\n10\nTrue\n\n-99\nneg -1\n\nfb')"
il_check bmore BMore "$ROOT/cases/il-bmore" "$(printf '5 items\nx = 42\n3.14\n00007\nff\n100%% ok: yes\n0:a,1:b,2:c\n0,20,60')"
il_check chunk Chunk "$ROOT/cases/il-chunk" "$(printf '3,7,5\n3\n1-2-3 4-5\na,b,c\n3\n1,3,5\n9')"
il_check collmore CollMore "$ROOT/cases/il-collmore" "$(printf '20,40\n1,10,2,20,3,30,4,40,5,50\n1,2,3,4,5\n15\n14\n-1\n3\n3')"
il_check tryexpr TryExpr "$ROOT/cases/il-tryexpr" "$(printf '42\n-1\n5\n-7\n4')"
il_check localclass LocalClass "$ROOT/cases/il-localclass" "$(printf '10\n42\n101\n3,4\nTrue\n60')"
il_check collops2 CollOps2 "$ROOT/cases/il-collops2" "$(printf '2,4,6 | 1,3,5\n0:a 1:b 2:c \n1,2,3\n0,1,3,6,10\n100,101,103,106,110\n6,9,12\n3\n-99')"
il_check refcell RefCell "$ROOT/cases/il-refcell" "$(printf '3\n30\nab\n10')"
il_check annot Annot "$ROOT/cases/il-annot" "$(printf 'widget#7\n42')"
il_check props Props "$ROOT/cases/il-props" "$(printf '20\n8\n16\nnot initialized\nready')"
il_check valcls ValCls "$ROOT/cases/il-valclass" "$(printf '1250\n12\n1250\nff\n1010\nff')"
il_check ctorref CtorRef "$ROOT/cases/il-ctorref" "$(printf '(1,2)\n(3,4)\n(9,9)')"
il_check getcls GetClass "$ROOT/cases/il-getclass" "$(printf 'String\nWidget\nWidget\nString')"
il_check forin Forin "$ROOT/cases/il-forin" "$(printf '60\n10,20,30,\n3')"
il_check ldeleg LocalDeleg "$ROOT/cases/il-localdeleg" "$(printf '42\n42\nHI\nWORLD')"
il_check langf LangFeat "$ROOT/cases/il-langfeat" "$(printf '7\n1024\n120\ntf\ncircle=12\nsq=25\n1a\n2b')"
il_check pair  Pair  "$ROOT/cases/il-pair"    "$(printf '3\n4\nx\n10\n11')"
il_check null  Null  "$ROOT/cases/il-null"    "$(printf 'none\nHI\nfallback\nABC\n5')"
il_check nullv MS1   "$ROOT/cases/m-s1/app.kt" "$(printf 'fallback\npresent\nforced\nlen null = -1\nlen hello = 5')"
il_check op    OpT   "$ROOT/cases/il-op/app.kt" "$(printf '(4, 6)\n(2, 2)\n(6, 8)\n(-3, -4)\n3\n4\nTrue\nTrue\nFalse\nTrue\n7\n15')"
il_check dataq Dq    "$ROOT/cases/m-s2/app.kt" "$(printf 'Point(x=3, y=4)\nPoint(x=7, y=9)\nx=3 y=4\na==b: True\na==c: False\nhash eq: True')"
il_check inline InlF "$ROOT/cases/il-inline/app.kt" "$(printf '5\n40\n3\n0')"
il_check inline2 Inl2 "$ROOT/cases/il-inline2" "$(printf '4\n42\n3')"
il_check xinline XInl "$ROOT/cases/il-xinline" "$(printf '20\n42\n105')"
il_check ctor  CtorT "$ROOT/cases/il-ctor/app.kt" "$(printf '12\n25\n5x5\nhi=7\nsolo=0')"
il_check objex Oe    "$ROOT/cases/il-objexpr/app.kt" "$(printf 'hello from anon\n105')"
il_check nest  Nst   "$ROOT/cases/il-nested/app.kt" "$(printf 'outer:root\nnode(7)\n14\nleaf 3')"
il_check scast Sc2   "$ROOT/cases/il-smartcast/app.kt" "$(printf 'int:42\nother\nyo\nnone')"
il_check vis   VisT  "$ROOT/cases/il-vis/app.kt" "$(printf '98\nacct\n99')"
il_check throwx Tx   "$ROOT/cases/il-throwexpr/app.kt" "$(printf 'pos\n42\n3')"
il_check enumr Er    "$ROOT/cases/il-enumrich/app.kt" "$(printf '5\nTrue\nFalse\nJUPITER\n1\n9\nEARTH\nMARS\nJUPITER\nTrue\nFalse')"
il_check reqnn Rn    "$ROOT/cases/il-reqnn/app.kt" "$(printf 'h\n7')"
il_check reif  Rf    "$ROOT/cases/il-reified/app.kt" "$(printf 'String\nInt32\nTrue\nFalse\nTrue\nyo\nno')"
il_check iter  Iter  "$ROOT/cases/il-iter"    "$(printf 'x=10\nx=20\nx=30\nsum = 60\nn=3\nn=2\nn=1\nacc = 6')"
il_check inner Inner "$ROOT/cases/il-inner"   "$(printf '110\n120\nT2\n5')"
il_check lazy  Lazy  "$ROOT/cases/il-lazy"    "$(printf 'before\ncomputing...\nVALUE\nVALUE\n42\n42')"
il_check deleg Deleg "$ROOT/cases/il-deleg"   "$(printf 'set count = 7\nget count\n7')"
il_check rwp   Rwp   "$ROOT/cases/il-rwp"     "$(printf 'set n = 5\nget n\n5')"
il_check bymap Bm    "$ROOT/cases/il-bymap"   "$(printf 'Alice\n30')"
il_check del2  D2    "$ROOT/cases/il-deleg2"  "$(printf '0 -> 1\n1 -> 2\n5\nhi')"
il_check gen   Gen   "$ROOT/cases/il-generic" "$(printf '42\n42\nhello\n7\nworld\n3\nthree')"
il_check gen2  Gen2  "$ROOT/cases/il-generic2" "$(printf '99\nIntBox holding an Int\ntag\nNamed holding a String')"
il_check gen3  Gen3  "$ROOT/cases/il-generic3" "$(printf '7\nbanana\n10')"
il_check gen4  Gen4  "$ROOT/cases/il-generic4" "$(printf '42\n42 & hi\n42 & 99\nx')"
il_check gen5  Gen5  "$ROOT/cases/il-generic5" "$(printf '10\n20\n99\nz')"
il_check gen6  Gen6  "$ROOT/cases/il-generic6" "$(printf 'hello\nconsumed: world')"
il_check netbase  Nb  "$ROOT/cases/il-netbase"  "$(printf 'app error\n7')" "$EXCMETA"
il_check netbase2 Nb2 "$ROOT/cases/il-netbase2" "$(printf 'AppError #7\nAppError #21')" "$EXCMETA"
il_check netgen  Ng  "$ROOT/cases/il-netgen"  "$(printf '3\nTrue\n2')" "$COLLMETA"
il_check netgen2 Ng2 "$ROOT/cases/il-netgen2" "$(printf '3\nTrue\n2')" "$COLLMETA"
il_check event   Ev  "$ROOT/cases/il-event"   "$(printf 'changed\nchanged\n2\nchanged\nh fired\nchanged\n4')" "$OBSCOLLMETA"
il_check loopjump LjT "$ROOT/cases/il-loopjump" "$(printf 'break at 3\nsumOdd=9\nouter break at 1,2')"
il_check netgen3 Ng3 "$ROOT/cases/il-netgen3" "$(printf '4\n8\n8\nFalse\nTrue\n20\n99\n3')" "$GMMETA"

# Coroutines: a suspend fun lowered to a CLR-native IAsyncStateMachine, driven by an external runtime (--ref).
il_check_ref() { # <name> <asm> <srcDir> <expected> <runtimeAsm>
	gate
	{ name="$1"; asm="$2"; src="$3"; expected="$4"; rasm="$5"
		birdir="$ROOT/build/bir-$name"; ildir="$ROOT/build/il-$name"
		refdll="$(build_runtime "$src" "$rasm")"; echo "$refdll" > "$ROOT/build/refdll-$name"
		rm -rf "$birdir" "$ildir"; mkdir -p "$birdir" "$ildir"
		if ! "$LAUNCHER" $src -no-stdlib -classpath "$CP" -d $birdir >/dev/null 2>&1; then
			echo "FAIL  il:$name (compile error)"; touch "$ROOT/build/fail-$name"; exit 0; fi
		if ! dotnet "$ROOT/build/ilemit-bin/ilemit.dll" "$ildir" "$asm" --ref "$DOTKT_RT" --ref "$refdll" "$birdir"/*.bir.json >/dev/null 2>&1; then
			echo "FAIL  il:$name (ilemit error)"; touch "$ROOT/build/fail-$name"; exit 0; fi
		cp "$refdll" "$ildir/"; cp "$DOTKT_RT" "$ildir/"
		actual="$(dotnet "$ildir/$asm.dll" 2>/dev/null)"
		if [[ "$actual" == "$expected" ]]; then echo "PASS  il:$name"; else
			echo "FAIL  il:$name"; printf -- '--- expected ---\n%s\n--- actual ---\n%s\n' "$expected" "$actual"; touch "$ROOT/build/fail-$name"; fi
	} &
}
il_check kcont2 KCont2 "$ROOT/cases/il-kcont2" "$(printf '42\nboom')"
il_check kctx KCtx "$ROOT/cases/il-kctx" "53"
il_check kintercept KIntercept "$ROOT/cases/il-kintercept" "$(printf '1\n7')"
il_check kunit2 KUnit2 "$ROOT/cases/il-kunit2" "True"
il_check_ref kcont KCont "$ROOT/cases/il-kcont" "$(printf '30\n14\n6\n15\n10\n-99')" KfcK
il_check_ref kintrin KIntrin "$ROOT/cases/il-kintrin" "$(printf '7\n42\n72')" KfcI
il_check_ref kgen KGen "$ROOT/cases/il-kgen" "$(printf '7\nhi\n2\nb')" KfcG
il_check_ref kresume KResume "$ROOT/cases/il-kresume" "$(printf '5\n107')" KfcR
il_check kflow2 KFlow2 "$ROOT/cases/il-kflow2" "$(printf '1\n2\n3')"
il_check kchan KChan "$ROOT/cases/il-kchan" "6"
il_check_ref kstart KStart "$ROOT/cases/il-kstart" "42" KfcSt
il_check_ref kcancel KCancel "$ROOT/cases/il-kcancel" "30" KfcCa
il_check_ref fieldvis FieldVis "$ROOT/cases/il-fieldvis" "$(printf '150\nme\nPrivate\nPublic')" KfcFv
il_check_inject delegatearg Dlg "$ROOT/cases/il-delegatearg" "$(printf '42\n20\n81')" KfcDel
il_check_inject netenum NetEnum "$ROOT/cases/il-netenum" "$(printf '60\n6\nabbccc')" KfcNetEnum
il_check_inject injbase InjBase "$ROOT/cases/il-injbase" "placed:0" KfcInjB
il_check_inject injfqn InjFqn "$ROOT/cases/il-injfqn" "42" KfcInjF
il_check_inject injstatic InjStatic "$ROOT/cases/il-injstatic" "$(printf 'p=42\n7\n99\n123')" KfcStatic
il_check_inject injuint InjUint "$ROOT/cases/il-injuint" "$(printf '65542\n42')" Boot
il_check_ref kfinally KFinally "$ROOT/cases/il-kfinally" "$(printf 'cleanup\n15')" KfcFin
il_check_ref kselect KSelect "$ROOT/cases/il-kselect" "2000" KfcSel
il_check_ref kasflow KAsFlow "$ROOT/cases/il-kasflow" "$(printf '0\n10\n20\n30')" KfcAsf
il_check_ref kunit KUnit "$ROOT/cases/il-kunit" "42" KfcU
il_check_ref kstruct KStruct "$ROOT/cases/il-kstruct" "$(printf '30\n42')" KfcS
il_check_ref coro Coro "$ROOT/cases/il-coro" "$(printf 'tryOk = 11\ntryCatch = -99\ntryFallthrough = 8\nloopCond = 3\ncondBranch = 6\nspillSum = 30\nspillNested = 17\nspillArg = 16\nchain = 30\nfetchDouble(7) = 14\nuseChain = 35\nsumLoop(4) = 6\nbranch(true) = 15\nbranch(false) = 10')" Kfc
il_check_ref colam Colam "$ROOT/cases/il-colam" "$(printf '30\n6\n105\n18')" KfcLam
il_check_ref c1net C1Net "$ROOT/cases/il-c1net" "$(printf '42\nhi\n10\n15\n105\n52\n21')" Probe
il_check_inject firgap FirGap "$ROOT/cases/il-firgap" "$(printf '42\n60\n3\n20')" P
il_check_inject inherit Inherit "$ROOT/cases/il-inherit" "$(printf 'run:derived\nshow:button\nbutton')" PInh
il_check_inject geninj GenInj "$ROOT/cases/il-geninj" "$(printf '2\na')" PGI
il_check_inject cbk Cbk "$ROOT/cases/il-cbk" "$(printf '=v42\nran')" PCbk
il_check_inject clriface ClrIface "$ROOT/cases/il-clriface" "$(printf '2\na')" PIf
il_check_inject clrimpl ClrImpl "$ROOT/cases/il-clrimpl" "$(printf 'draw:circle\ndraw:square\ncircle')" PImpl
il_check_inject clrasm ClrAsm "$ROOT/cases/il-clrasm" "$(printf '2\n2\n2')" PAsm
il_check_inject selfref SelfRef "$ROOT/cases/il-selfref" "4" PSelf
il_check_inject genim GenIM "$ROOT/cases/il-genim" "$(printf 'hello\nworld')" PGenIM
il_check_inject outref Outref "$ROOT/cases/il-outref" "$(printf 'ok=5\nfail\n2 1\n20\n20\n109')" OutR
il_check_inject netattr NetAttr "$ROOT/cases/il-netattr" "$(printf 'widget#7\n42')" Lbl
il_check_inject stackalloc Sa "$ROOT/cases/il-stackalloc" "$(printf '16\n30\n-1\n10\n21')" SpanRt
il_check fmt Fmt "$ROOT/cases/il-fmt" "$(printf '42 items, 87.5%% (ok)\n00007-ff\n[a   ]\n[bb  ]')"
il_check_inject mref Mr "$ROOT/cases/il-mref" "$(printf 'hello world\n0')" MrRt
il_check cobuild Cob "$ROOT/cases/il-cobuild" "25"
il_check dsl Dsl "$ROOT/cases/il-dsl" "a[Pb]c"
il_check object TObj "$ROOT/cases/il-object" "3"
il_check gfac TGfac "$ROOT/cases/il-gfac" "$(printf '42\nhi')"
il_check xprop Xprop "$ROOT/cases/il-xprop" "7"
il_check exprbody EB "$ROOT/cases/il-exprbody" "$(printf 'greet\nviaLambda\ncleanup\npos')"
il_check overload OV "$ROOT/cases/il-overload" "$(printf 'S:x\nF:y\nI:7\nbs:p\nbf:q')"
il_check mfclosure MfClosure "$ROOT/cases/il-mfclosure" "$(printf '10\n20')"
il_check mflambda MFL "$ROOT/cases/il-mflambda" "$(printf 'A1\nA2\nB1')"
il_check arrops Arro "$ROOT/cases/il-arrops" "$(printf '3\n6,8,10\n14\n2\n-1\n10\n30')"
	il_check collrealkt CollRealKt "$ROOT/cases/il-collrealkt" "$(printf '10
30
500
b,a,c
two')"
il_check mutcoll MutColl "$ROOT/cases/il-mutcoll" "$(printf '2,3,4\n2,4\n2\n0\n11,22,33')"
il_check mapfilter MapF "$ROOT/cases/il-mapfilter" "$(printf '10,20,30,40,50\n2,4\n4,5,6\n100,200,300\n2,4,6')"
il_check nan Nan "$ROOT/cases/il-nan" "$(printf 'True\nTrue\nTrue\nFalse\nFalse')"

# Reverse interop: a .NET (C#) host loads the IL-emitted Kotlin assembly and calls a Kotlin class + top-level
# fun. Proves the IL output is a consumable .NET assembly. (Compile-time <Reference> needs per-type contract-
# assembly retargeting — blocked by a Reflection.Emit limitation; see design 5.2. Reflection load works today.)
il_revinterop() {
	local asm=KotlinLib src="$ROOT/cases/il-revinterop"
	local birdir="$ROOT/build/bir-revinterop" ildir="$ROOT/build/il-revinterop"
	rm -rf "$birdir" "$ildir"; mkdir -p "$birdir" "$ildir"
	"$LAUNCHER" $src/lib.kt -no-stdlib -classpath "$CP" -d $birdir >/dev/null 2>&1 \
		|| { echo "FAIL  il:$name (compile error)"; fail=1; return; }
	dotnet "$ROOT/build/ilemit-bin/ilemit.dll" "$ildir" "$asm" --ref "$DOTKT_RT" "$birdir"/*.bir.json >/dev/null
	cp "$src/Program.cs" "$ildir/Program.cs"
	cat > "$ildir/consumer.csproj" <<EOF
<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework>
<Nullable>disable</Nullable><ImplicitUsings>disable</ImplicitUsings><EnableDefaultCompileItems>false</EnableDefaultCompileItems></PropertyGroup>
<ItemGroup><Compile Include="Program.cs" /></ItemGroup></Project>
EOF
	local actual expected; expected="$(printf 'Hi, World\n5')"
	actual="$(dotnet run --project "$ildir/consumer.csproj" -v q -- "$ildir/$asm.dll" 2>/dev/null | grep -vE 'warning|error |\.cs\(')"
	if [[ "$actual" == "$expected" ]]; then echo "PASS  il:revinterop (.NET host consumes IL asm)"; else
		echo "FAIL  il:revinterop"; echo "--- expected ---"; echo "$expected"; echo "--- actual ---"; echo "$actual"; fail=1
	fi
}

wait   # let every backgrounded sample check finish before aggregating + the ilverify phase
# Aggregate the parallel results: a FAIL marker -> overall failure; runtime-dll paths -> the ilverify phase's -r.
for f in "$ROOT"/build/fail-*;   do [[ -e "$f" ]] && fail=1; done
for f in "$ROOT"/build/refdll-*; do [[ -e "$f" ]] || continue; REFDLL["$(basename "$f" | sed 's/^refdll-//')"]="$(cat "$f")"; done

il_revinterop

# Formal IL verification (ilverify), if the tool is available.
ILV="$(find "$HOME/.dotnet" -name 'ILVerify.dll' 2>/dev/null | head -1)"
REFDIR="$(dirname "$(find /usr/share/dotnet/shared/Microsoft.NETCore.App -name System.Private.CoreLib.dll 2>/dev/null | sort | tail -1)")"
if [[ -n "$ILV" && -d "$REFDIR" ]]; then
	echo "--- ilverify ---"
	declare -A ASMS=( [m0]=M0Kt [mc1]=MC1 [iface]=Iface [enum]=Enum [m2]=M2 [mi1]=MI1 [for]=ForT [exc]=Exc [ops]=Ops [math]=MathT [str]=Str [cp]=Cp [ext]=Ext [arr]=Arr [lam]=Lam [clo]=Clo [scope]=Sc [coll]=Coll [coll2]=Coll2 [coll3]=Coll3 [seq]=Seq [char]=Char [sort]=Sort [funref]=Funref [getcls]=GetClass [forin]=Forin [ldeleg]=LocalDeleg [langf]=LangFeat [mapdes]=MapDes [valcls]=ValCls [ctorref]=CtorRef [unsgn]=Unsigned [regex]=Regex [result]=Result [bmore]=BMore [chunk]=Chunk  [collmore]=CollMore  [tryexpr]=TryExpr  [localclass]=LocalClass [collops2]=CollOps2 [refcell]=RefCell [annot]=Annot [props]=Props [pair]=Pair [null]=Null [nullv]=MS1 [op]=OpT [dataq]=Dq [inline]=InlF [ctor]=CtorT [objex]=Oe [nest]=Nst [scast]=Sc2 [vis]=VisT [throwx]=Tx [enumr]=Er [reqnn]=Rn [reif]=Rf [iter]=Iter [inner]=Inner [lazy]=Lazy [deleg]=Deleg [rwp]=Rwp [bymap]=Bm [del2]=D2 [gen]=Gen [gen2]=Gen2 [gen3]=Gen3 [gen4]=Gen4 [gen5]=Gen5 [gen6]=Gen6 [netbase]=Nb [netbase2]=Nb2 [netgen]=Ng [netgen2]=Ng2 [event]=Ev [netgen3]=Ng3 [coro]=Coro [loopjump]=LjT [inline2]=Inl2  [c1net]=C1Net [firgap]=FirGap [fmt]=Fmt [cobuild]=Cob [dsl]=Dsl [object]=TObj [gfac]=TGfac [xprop]=Xprop [arrops]=Arro [colam]=Colam [kcont]=KCont [kintrin]=KIntrin [expect]=Expect [kgen]=KGen [kunit]=KUnit [kstruct]=KStruct [kseq]=KSeq [kflow]=KFlow [kresume]=KResume [kgflow]=KGFlow [kstart]=KStart [kcancel]=KCancel [kcont2]=KCont2 [kflow2]=KFlow2 [kunit2]=KUnit2 [kchan]=KChan [kasflow]=KAsFlow [kgenseq]=KGenSeq [kfinally]=KFinally [kselect]=KSelect [kctx]=KCtx [kintercept]=KIntercept [langtail]=LangTail [enumbody]=EnumBody [fieldvis]=FieldVis [bytearg]=ByteArg [iterable]=Iterable [customexc]=CustomExc [comparator]=Comparator [use]=Use [comparable]=Comparable [charseq]=CS [substr]=Substr [injbase]=InjBase [injfqn]=InjFqn [injstatic]=InjStatic [mfclosure]=MfClosure [mflambda]=MFL [injuint]=InjUint [exprbody]=EB [overload]=OV [collrealkt]=CollRealKt [mutcoll]=MutColl [mapfilter]=MapF [nan]=Nan )
	for n in "${!ASMS[@]}"; do
		dll="$ROOT/build/il-$n/${ASMS[$n]}.dll"
		[[ -f "$dll" ]] || continue
		# A sample that references an external runtime dll needs it on ilverify's resolve path too.
		refarg=(); [[ -n "${REFDLL[$n]:-}" ]] && refarg=(-r "${REFDLL[$n]}")
		if dotnet "$ILV" "$dll" -r "$REFDIR/*.dll" -r "$DOTKT_RT" -r "$STDLIB_DLL" "${refarg[@]}" 2>&1 | grep -qi 'Verified\.'; then echo "VERIFY  $n"; else echo "VERIFY FAIL  $n"; fail=1; fi
	done
else
	echo "(ilverify not installed; skipping formal verification — 'dotnet tool install -g dotnet-ilverify')"
fi

echo "------------------------------------"
[[ $fail -eq 0 ]] && echo "IL ALL PASS" || { echo "IL SOME FAILED"; exit 1; }
