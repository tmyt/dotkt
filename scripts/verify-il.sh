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
# of ~9s for `gradlew --no-daemon :compiler:run` — a ~4x speedup on the dominant compile step.
"$ROOT/gradlew" -q :compiler:installDist >/dev/null 2>&1
LAUNCHER="$ROOT/compiler/build/install/compiler/bin/compiler"

# Run samples concurrently (each compile is an independent ~2s JVM startup). A job pool caps parallelism; results
# (FAIL markers, runtime-dll paths for the ilverify phase) cross back from the subshells via files.
JOBS="$(nproc 2>/dev/null || echo 4)"; (( JOBS > 6 )) && JOBS=6
gate() { while (( $(jobs -rp | wc -l) >= JOBS )); do wait -n 2>/dev/null || true; done; }
rm -f "$ROOT"/build/fail-* "$ROOT"/build/refdll-* 2>/dev/null || true

dotnet build "$ROOT/tools/ilemit" -c Release -o "$ROOT/build/ilemit-bin" -v q --nologo >/dev/null

# DotKt.Runtime: the runtime assembly for promoted lowerings (printf->composite format, …). Every compiled
# assembly references it (the SDK auto-references it), so pass it globally to ilemit + ilverify.
dotnet build "$ROOT/runtime/DotKt.Runtime" -c Release -o "$ROOT/build/dotkt-runtime" -v q --nologo >/dev/null 2>&1
DOTKT_RT="$ROOT/build/dotkt-runtime/DotKt.Runtime.dll"

# DotKt.Coroutines: the shared Continuation core (Path B) for @KCont/`suspend fun` lowered to a class. Passed
# globally to ilemit + ilverify (harmless when unreferenced); copied next to each emitted dll for the run phase.
dotnet build "$ROOT/runtime/DotKt.Coroutines" -c Release -o "$ROOT/build/dotkt-coroutines" -v q --nologo >/dev/null 2>&1
DOTKT_CO="$ROOT/build/dotkt-coroutines/DotKt.Coroutines.dll"

# S5 FIR-injection metadata for samples that inherit a real .NET base type (façade-free).
dotnet build "$ROOT/tools/facadegen" -c Release -o "$ROOT/build/facadegen-bin" -v q --nologo >/dev/null 2>&1
EXCMETA="$ROOT/build/exc.meta"
dotnet "$ROOT/build/facadegen-bin/facadegen.dll" --meta "$EXCMETA" System.Exception System.Console >/dev/null 2>&1
COLLMETA="$ROOT/build/coll.meta"
dotnet "$ROOT/build/facadegen-bin/facadegen.dll" --meta "$COLLMETA" System.Collections.ObjectModel.Collection >/dev/null 2>&1
OBSCOLLMETA="$ROOT/build/obscoll.meta"
dotnet "$ROOT/build/facadegen-bin/facadegen.dll" --meta "$OBSCOLLMETA" System.Collections.ObjectModel.ObservableCollection >/dev/null 2>&1
GMMETA="$ROOT/build/gm.meta"
dotnet "$ROOT/build/facadegen-bin/facadegen.dll" --meta "$GMMETA" System.Runtime.CompilerServices.Unsafe System.Runtime.CompilerServices.RuntimeHelpers System.Collections.ObjectModel.Collection >/dev/null 2>&1

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
		dotnet "$ROOT/build/facadegen-bin/facadegen.dll" --meta "$meta" --refs "$(ls ${RD}*.dll | tr '\n' ';');$refdll" --scan "$src"/*.kt >/dev/null 2>&1
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
		if ! CLR_TYPES_METADATA="$meta" "$LAUNCHER" $src -no-stdlib -classpath "$CP" -d $birdir >/dev/null 2>&1; then
			echo "FAIL  il:$name (compile error)"; touch "$ROOT/build/fail-$name"; exit 0; fi
		if ! dotnet "$ROOT/build/ilemit-bin/ilemit.dll" "$ildir" "$asm" --ref "$DOTKT_RT" "$birdir"/*.bir.json >/dev/null 2>&1; then
			echo "FAIL  il:$name (ilemit error)"; touch "$ROOT/build/fail-$name"; exit 0; fi
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
		if ! dotnet "$ROOT/build/ilemit-bin/ilemit.dll" "$ildir" "$asm" --ref "$DOTKT_RT" --ref "$DOTKT_CO" "$birdir"/*.bir.json >/dev/null 2>&1; then
			echo "FAIL  il:$name (ilemit error)"; touch "$ROOT/build/fail-$name"; exit 0; fi
		cp "$DOTKT_CO" "$ildir/"
		actual="$(dotnet "$ildir/$asm.dll" 2>/dev/null)"
		if [[ "$actual" == "$expected" ]]; then echo "PASS  il:$name"; else
			echo "FAIL  il:$name"; printf -- '--- expected ---\n%s\n--- actual ---\n%s\n' "$expected" "$actual"; touch "$ROOT/build/fail-$name"; fi
	} &
}
il_check_mpp expect Expect "$ROOT/samples/il-expect" common.kt "$(printf 'CLR\n42\nhello from CLR\n2\nTrue\n10\n15\nTrue\nhi')"

il_check m0    M0Kt  "$ROOT/samples/m0/M0.kt"  "$(printf 'sum = 5\nzero\nn=1\nn=2')"
il_check mc1   MC1   "$ROOT/samples/m-c1"      "$(printf 'c = (4, 6)\na.d2 = 25\nrect area=30')"
il_check iface Iface "$ROOT/samples/il-iface"  "$(printf 'Hello\nKonnichiwa')"
il_check enum  Enum  "$ROOT/samples/il-enum"   "$(printf 'red\ngreen\nblue')"
il_check m2    M2    "$ROOT/samples/m2"         "$(printf 'max(3, 7) = 7\nmin(3, 7) = 3\nabs(-9) = 9')"
il_check mi1   MI1   "$ROOT/samples/m-i1"       "$(printf 'Hello, CLR 42\nlength = 13')"
il_check for   ForT  "$ROOT/samples/il-for"     "$(printf 'sum 1..5 = 15\ncountdown 5 = 54321')"
il_check exc   Exc   "$ROOT/samples/il-exc"     "$(printf 'safeDiv(10,2) = 5\nsafeDiv(1,0) = -1')"
il_check ops   Ops   "$ROOT/samples/il-ops"     "$(printf '3\n2\n7\n3\n16\n15\n-1\n3\n5')"
il_check math  MathT "$ROOT/samples/il-math"    "$(printf '9\n7\n3\n4')"
il_check str   Str   "$ROOT/samples/il-str"     "$(printf 'HELLO\nhello\nhi\nello\nTrue\nTrue')"
il_check cp    Cp    "$ROOT/samples/il-cp"      "$(printf '50\n3.5\nTrue\nTrue\nX')"
il_check ext   Ext   "$ROOT/samples/il-ext"     "$(printf '21\nHI')"
il_check arr   Arr   "$ROOT/samples/il-arr"     "$(printf '10\n30\n99\n3\n139\n139')"
il_check lam   Lam   "$ROOT/samples/il-lambda"  "$(printf '42\n12')"
il_check clo   Clo   "$ROOT/samples/il-closure" "$(printf '15\n105\n17')"
il_check scope Sc    "$ROOT/samples/il-scope"   "$(printf '10\n6\n9\n10\n10\n7')"
il_check coll  Coll  "$ROOT/samples/il-coll"    "$(printf '5\n5\n3\n2\n3\nTrue\nTrue\n3\n1\n4\nTrue\n5')"
il_check coll2 Coll2 "$ROOT/samples/il-coll2"   "$(printf '10\n1-2-3-4\n1, 2, 3, 4\n100')"
il_check coll3 Coll3 "$ROOT/samples/il-coll3"   "$(printf '60\n6')"
il_check seq   Seq   "$ROOT/samples/il-seq"     "$(printf '6,12\n16\n3\n27\n10-20-30\n1,2,3\n4,5,6\n3')"
il_check char  Char  "$ROOT/samples/il-char"    "$(printf 'True\nTrue\nTrue\nTrue\nA\nz\nTrue\nTrue\n97\nb')"
il_check sort  Sort  "$ROOT/samples/il-sort"    "$(printf '9,6,5,4,3,2,1,1\na,dd,bbb,cccc\ncccc,bbb,dd,a')"
il_check funref Funref "$ROOT/samples/il-funref" "$(printf '2,4,6\n1,4,9,16,25,36\nHi, Kotlin\n105\n107\ncalc100\n203\n42')"
il_check mapdes MapDes "$ROOT/samples/il-mapdes" "$(printf '10\n60\n13\nx=1\ny=2\nz=3\ntotal=6')"
il_check unsgn Unsigned "$ROOT/samples/il-unsigned" "$(printf '4000000100\n4000000000\n18000000000000000000\n60000\n250')"
il_check regex Regex "$ROOT/samples/il-regex" "$(printf 'True\nFalse\na#b#c#\na_b_c')"
il_check result Result "$ROOT/samples/il-result" "$(printf 'True\n10\n10\nTrue\n\n-99\nneg -1\n\nfb')"
il_check bmore BMore "$ROOT/samples/il-bmore" "$(printf '5 items\nx = 42\n3.14\n00007\nff\n100%% ok: yes\n0:a,1:b,2:c\n0,20,60')"
il_check chunk Chunk "$ROOT/samples/il-chunk" "$(printf '3,7,5\n3\n1-2-3 4-5\na,b,c\n3\n1,3,5\n9')"
il_check collmore CollMore "$ROOT/samples/il-collmore" "$(printf '20,40\n1,10,2,20,3,30,4,40,5,50\n1,2,3,4,5\n15\n14\n-1\n3\n3')"
il_check tryexpr TryExpr "$ROOT/samples/il-tryexpr" "$(printf '42\n-1\n5\n-7\n4')"
il_check localclass LocalClass "$ROOT/samples/il-localclass" "$(printf '10\n42\n101\n3,4\nTrue\n60')"
il_check collops2 CollOps2 "$ROOT/samples/il-collops2" "$(printf '2,4,6 | 1,3,5\n0:a 1:b 2:c \n1,2,3\n0,1,3,6,10\n100,101,103,106,110\n6,9,12\n3\n-99')"
il_check refcell RefCell "$ROOT/samples/il-refcell" "$(printf '3\n30\nab\n10')"
il_check annot Annot "$ROOT/samples/il-annot" "$(printf 'widget#7\n42')"
il_check props Props "$ROOT/samples/il-props" "$(printf '20\n8\n16\nnot initialized\nready')"
il_check valcls ValCls "$ROOT/samples/il-valclass" "$(printf '1250\n12\n1250\nff\n1010\nff')"
il_check ctorref CtorRef "$ROOT/samples/il-ctorref" "$(printf '(1,2)\n(3,4)\n(9,9)')"
il_check getcls GetClass "$ROOT/samples/il-getclass" "$(printf 'String\nWidget\nWidget\nString')"
il_check forin Forin "$ROOT/samples/il-forin" "$(printf '60\n10,20,30,\n3')"
il_check ldeleg LocalDeleg "$ROOT/samples/il-localdeleg" "$(printf '42\n42\nHI\nWORLD')"
il_check langf LangFeat "$ROOT/samples/il-langfeat" "$(printf '7\n1024\n120\ntf\ncircle=12\nsq=25\n1a\n2b')"
il_check pair  Pair  "$ROOT/samples/il-pair"    "$(printf '3\n4\nx\n10\n11')"
il_check null  Null  "$ROOT/samples/il-null"    "$(printf 'none\nHI\nfallback\nABC\n5')"
il_check nullv MS1   "$ROOT/samples/m-s1/app.kt" "$(printf 'fallback\npresent\nforced\nlen null = -1\nlen hello = 5')"
il_check op    OpT   "$ROOT/samples/il-op/app.kt" "$(printf '(4, 6)\n(2, 2)\n(6, 8)\n(-3, -4)\n3\n4\nTrue\nTrue\nFalse\nTrue\n7\n15')"
il_check dataq Dq    "$ROOT/samples/m-s2/app.kt" "$(printf 'Point(x=3, y=4)\nPoint(x=7, y=9)\nx=3 y=4\na==b: True\na==c: False\nhash eq: True')"
il_check inline InlF "$ROOT/samples/il-inline/app.kt" "$(printf '5\n40\n3\n0')"
il_check inline2 Inl2 "$ROOT/samples/il-inline2" "$(printf '4\n42\n3')"
il_check ctor  CtorT "$ROOT/samples/il-ctor/app.kt" "$(printf '12\n25\n5x5\nhi=7\nsolo=0')"
il_check objex Oe    "$ROOT/samples/il-objexpr/app.kt" "$(printf 'hello from anon\n105')"
il_check nest  Nst   "$ROOT/samples/il-nested/app.kt" "$(printf 'outer:root\nnode(7)\n14\nleaf 3')"
il_check scast Sc2   "$ROOT/samples/il-smartcast/app.kt" "$(printf 'int:42\nother\nyo\nnone')"
il_check vis   VisT  "$ROOT/samples/il-vis/app.kt" "$(printf '98\nacct\n99')"
il_check throwx Tx   "$ROOT/samples/il-throwexpr/app.kt" "$(printf 'pos\n42\n3')"
il_check enumr Er    "$ROOT/samples/il-enumrich/app.kt" "$(printf '5\nTrue\nFalse\nJUPITER\n1\n9\nEARTH\nMARS\nJUPITER\nTrue\nFalse')"
il_check reqnn Rn    "$ROOT/samples/il-reqnn/app.kt" "$(printf 'h\n7')"
il_check reif  Rf    "$ROOT/samples/il-reified/app.kt" "$(printf 'String\nInt32\nTrue\nFalse\nTrue\nyo\nno')"
il_check iter  Iter  "$ROOT/samples/il-iter"    "$(printf 'x=10\nx=20\nx=30\nsum = 60\nn=3\nn=2\nn=1\nacc = 6')"
il_check inner Inner "$ROOT/samples/il-inner"   "$(printf '110\n120\nT2\n5')"
il_check lazy  Lazy  "$ROOT/samples/il-lazy"    "$(printf 'before\ncomputing...\nVALUE\nVALUE\n42\n42')"
il_check deleg Deleg "$ROOT/samples/il-deleg"   "$(printf 'set count = 7\nget count\n7')"
il_check rwp   Rwp   "$ROOT/samples/il-rwp"     "$(printf 'set n = 5\nget n\n5')"
il_check bymap Bm    "$ROOT/samples/il-bymap"   "$(printf 'Alice\n30')"
il_check del2  D2    "$ROOT/samples/il-deleg2"  "$(printf '0 -> 1\n1 -> 2\n5\nhi')"
il_check gen   Gen   "$ROOT/samples/il-generic" "$(printf '42\n42\nhello\n7\nworld\n3\nthree')"
il_check gen2  Gen2  "$ROOT/samples/il-generic2" "$(printf '99\nIntBox holding an Int\ntag\nNamed holding a String')"
il_check gen3  Gen3  "$ROOT/samples/il-generic3" "$(printf '7\nbanana\n10')"
il_check gen4  Gen4  "$ROOT/samples/il-generic4" "$(printf '42\n42 & hi\n42 & 99\nx')"
il_check gen5  Gen5  "$ROOT/samples/il-generic5" "$(printf '10\n20\n99\nz')"
il_check gen6  Gen6  "$ROOT/samples/il-generic6" "$(printf 'hello\nconsumed: world')"
il_check netbase  Nb  "$ROOT/samples/il-netbase"  "$(printf 'app error\n7')" "$EXCMETA"
il_check netbase2 Nb2 "$ROOT/samples/il-netbase2" "$(printf 'AppError #7\nAppError #21')" "$EXCMETA"
il_check netgen  Ng  "$ROOT/samples/il-netgen"  "$(printf '3\nTrue\n2')" "$COLLMETA"
il_check netgen2 Ng2 "$ROOT/samples/il-netgen2" "$(printf '3\nTrue\n2')" "$COLLMETA"
il_check event   Ev  "$ROOT/samples/il-event"   "$(printf 'changed\nchanged\n2\nchanged\nh fired\nchanged\n4')" "$OBSCOLLMETA"
il_check loopjump LjT "$ROOT/samples/il-loopjump" "$(printf 'break at 3\nsumOdd=9\nouter break at 1,2')"
il_check netgen3 Ng3 "$ROOT/samples/il-netgen3" "$(printf '4\n8\n8\nFalse\nTrue\n20\n99\n3')" "$GMMETA"

# Coroutines: a suspend fun lowered to a CLR-native IAsyncStateMachine, driven by an external runtime (--ref).
il_check_ref() { # <name> <asm> <srcDir> <expected> <runtimeAsm>
	gate
	{ name="$1"; asm="$2"; src="$3"; expected="$4"; rasm="$5"
		birdir="$ROOT/build/bir-$name"; ildir="$ROOT/build/il-$name"
		refdll="$(build_runtime "$src" "$rasm")"; echo "$refdll" > "$ROOT/build/refdll-$name"
		rm -rf "$birdir" "$ildir"; mkdir -p "$birdir" "$ildir"
		if ! "$LAUNCHER" $src -no-stdlib -classpath "$CP" -d $birdir >/dev/null 2>&1; then
			echo "FAIL  il:$name (compile error)"; touch "$ROOT/build/fail-$name"; exit 0; fi
		if ! dotnet "$ROOT/build/ilemit-bin/ilemit.dll" "$ildir" "$asm" --ref "$DOTKT_RT" --ref "$DOTKT_CO" --ref "$refdll" "$birdir"/*.bir.json >/dev/null 2>&1; then
			echo "FAIL  il:$name (ilemit error)"; touch "$ROOT/build/fail-$name"; exit 0; fi
		cp "$refdll" "$ildir/"; cp "$DOTKT_CO" "$ildir/"
		actual="$(dotnet "$ildir/$asm.dll" 2>/dev/null)"
		if [[ "$actual" == "$expected" ]]; then echo "PASS  il:$name"; else
			echo "FAIL  il:$name"; printf -- '--- expected ---\n%s\n--- actual ---\n%s\n' "$expected" "$actual"; touch "$ROOT/build/fail-$name"; fi
	} &
}
il_check_ref kcont KCont "$ROOT/samples/il-kcont" "$(printf '30\n14\n6\n15\n10\n-99')" KfcK
il_check_ref kintrin KIntrin "$ROOT/samples/il-kintrin" "$(printf '7\n42\n72')" KfcI
il_check_ref kgen KGen "$ROOT/samples/il-kgen" "$(printf '7\nhi\n2\nb')" KfcG
il_check_ref kunit KUnit "$ROOT/samples/il-kunit" "42" KfcU
il_check_ref coro Coro "$ROOT/samples/il-coro" "$(printf 'tryOk = 11\ntryCatch = -99\ntryFallthrough = 8\nloopCond = 3\ncondBranch = 6\nspillSum = 30\nspillNested = 17\nspillArg = 16\nchain = 30\nfetchDouble(7) = 14\nuseChain = 35\nsumLoop(4) = 6\nbranch(true) = 15\nbranch(false) = 10')" Kfc
il_check_ref colam Colam "$ROOT/samples/il-colam" "$(printf '30\n6\n105\n18')" KfcLam
il_check_ref c1net C1Net "$ROOT/samples/il-c1net" "$(printf '42\nhi\n10\n15\n105\n52\n21')" Probe
il_check_inject firgap FirGap "$ROOT/samples/il-firgap" "$(printf '42\n60\n3\n20')" P
il_check_inject outref Outref "$ROOT/samples/il-outref" "$(printf 'ok=5\nfail\n2 1\n20\n20\n109')" OutR
il_check_inject netattr NetAttr "$ROOT/samples/il-netattr" "$(printf 'widget#7\n42')" Lbl
il_check_inject stackalloc Sa "$ROOT/samples/il-stackalloc" "$(printf '16\n30\n-1\n10\n21')" SpanRt
il_check fmt Fmt "$ROOT/samples/il-fmt" "$(printf '42 items, 87.5%% (ok)\n00007-ff\n[a   ]\n[bb  ]')"
il_check_inject mref Mr "$ROOT/samples/il-mref" "$(printf 'hello world\n0')" MrRt
il_check cobuild Cob "$ROOT/samples/il-cobuild" "25"
il_check arrops Arro "$ROOT/samples/il-arrops" "$(printf '3\n6,8,10\n14\n2\n-1\n10\n30')"

# Reverse interop: a .NET (C#) host loads the IL-emitted Kotlin assembly and calls a Kotlin class + top-level
# fun. Proves the IL output is a consumable .NET assembly. (Compile-time <Reference> needs per-type contract-
# assembly retargeting — blocked by a Reflection.Emit limitation; see design 5.2. Reflection load works today.)
il_revinterop() {
	local asm=KotlinLib src="$ROOT/samples/il-revinterop"
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
	declare -A ASMS=( [m0]=M0Kt [mc1]=MC1 [iface]=Iface [enum]=Enum [m2]=M2 [mi1]=MI1 [for]=ForT [exc]=Exc [ops]=Ops [math]=MathT [str]=Str [cp]=Cp [ext]=Ext [arr]=Arr [lam]=Lam [clo]=Clo [scope]=Sc [coll]=Coll [coll2]=Coll2 [coll3]=Coll3 [seq]=Seq [char]=Char [sort]=Sort [funref]=Funref [getcls]=GetClass [forin]=Forin [ldeleg]=LocalDeleg [langf]=LangFeat [mapdes]=MapDes [valcls]=ValCls [ctorref]=CtorRef [unsgn]=Unsigned [regex]=Regex [result]=Result [bmore]=BMore [chunk]=Chunk  [collmore]=CollMore  [tryexpr]=TryExpr  [localclass]=LocalClass [collops2]=CollOps2 [refcell]=RefCell [annot]=Annot [props]=Props [pair]=Pair [null]=Null [nullv]=MS1 [op]=OpT [dataq]=Dq [inline]=InlF [ctor]=CtorT [objex]=Oe [nest]=Nst [scast]=Sc2 [vis]=VisT [throwx]=Tx [enumr]=Er [reqnn]=Rn [reif]=Rf [iter]=Iter [inner]=Inner [lazy]=Lazy [deleg]=Deleg [rwp]=Rwp [bymap]=Bm [del2]=D2 [gen]=Gen [gen2]=Gen2 [gen3]=Gen3 [gen4]=Gen4 [gen5]=Gen5 [gen6]=Gen6 [netbase]=Nb [netbase2]=Nb2 [netgen]=Ng [netgen2]=Ng2 [event]=Ev [netgen3]=Ng3 [coro]=Coro [loopjump]=LjT [inline2]=Inl2  [c1net]=C1Net [firgap]=FirGap [fmt]=Fmt [cobuild]=Cob [arrops]=Arro [colam]=Colam [kcont]=KCont [kintrin]=KIntrin [expect]=Expect [kgen]=KGen [kunit]=KUnit )
	for n in "${!ASMS[@]}"; do
		dll="$ROOT/build/il-$n/${ASMS[$n]}.dll"
		[[ -f "$dll" ]] || continue
		# A sample that references an external runtime dll needs it on ilverify's resolve path too.
		refarg=(); [[ -n "${REFDLL[$n]:-}" ]] && refarg=(-r "${REFDLL[$n]}")
		if dotnet "$ILV" "$dll" -r "$REFDIR/*.dll" -r "$DOTKT_RT" -r "$DOTKT_CO" "${refarg[@]}" 2>&1 | grep -qi 'Verified\.'; then echo "VERIFY  $n"; else echo "VERIFY FAIL  $n"; fail=1; fi
	done
else
	echo "(ilverify not installed; skipping formal verification — 'dotnet tool install -g dotnet-ilverify')"
fi

echo "------------------------------------"
[[ $fail -eq 0 ]] && echo "IL ALL PASS" || { echo "IL SOME FAILED"; exit 1; }
