# Changelog

All notable changes to DotKt (Kotlin → .NET/CLR). Package versions carry the embedded
Kotlin compiler version as SemVer build metadata (e.g. `0.9.1+kotlin-2.2.0`).

## Unreleased

0.9.4 carries the 4-layer compiler migration to completion, lands a full coroutine engine,
and turns hundreds of mid-migration fixes into a coherent release. Headlines: `suspend` /
`sequence{}` / `Task.await()` run end-to-end; the compiler's hand-written stdlib lowerings are
retired into a real pure-Kotlin standard library; and every verify gate is XFAIL-zero.

### Coroutines

- **Full `suspend` support end-to-end via a cold-core state machine + `Task<T>` bridge.** A
  `suspend fun` lowers (in bir2cir) to a plain-CIR `ContinuationImpl` state machine plus a public
  `Task<R>` bridge that C#/F# callers consume as a normal hot `Task` — the bidirectional CLR async
  model in `docs/design-coroutine-cold-core-task-bridge.md`. Covers straight-line bodies, all control
  flow (`if`/`when`/`while`/`for`), `try`/`catch`/`finally` across a suspension, generic suspend funs,
  extension + instance + interface + abstract/override members, and cross-module suspend calls
  (consuming a `suspend` fun from another DotKt assembly). A `suspend fun main` is drained correctly
  whether it completes synchronously or genuinely suspends.
- **`sequence{}` / `iterator{}` / `yield` / `yieldAll` are now ordinary library code** over the shared
  cold core (`SequenceBuilderIterator`), for reference and value element types alike. The compiler
  holds ZERO knowledge of these symbols — no CPS engine, no `sequence`/`yield` special-case.
- **`suspendCoroutine{}` / `suspendCoroutineUninterceptedOrReturn` / `@RestrictsSuspension`** lower to
  real suspension points, including cross-module `suspendCoroutine{}` (the wrapper is reconstructed in
  the caller's state machine through a `SafeContinuation`).
- **`Task.await()` — the `.NET Task ⇒ Kotlin suspend` reverse bridge.** `import kotlin.clr.await;
  task.await()` suspends on a `TaskAwaiter`, resuming on completion (sync fast path + genuine async).
  `Task.WhenAll(vararg Task<T>)` / `WhenAny` and generic static factories (`Task.FromResult<T>`)
  resolve and run, so Kotlin can both consume and build a `Task<T>`.
- **`kotlin.clr` coroutine surface = `await` only** (the genuine CLR async boundary, facadegen-injected).
  `blockOn`/`delay` are NOT stdlib — they are re-implemented in a pure-Kotlin test harness over the
  public primitives (`startCoroutine`/`Continuation`/`Monitor`), a living proof that `runBlocking` is
  ordinary library code over the shared core.
- **kotlinx.coroutines purged (BREAKING).** The pre-stdlib `kotlinx.coroutines` stopgap is removed; use
  `kotlin.clr.await` (and a harness `blockOn`) in place of `kotlinx.coroutines.runBlocking`/`delay`.
- **The compiler back half is coroutine-free.** All coroutine lowering lives in bir2cir; kotc's CPS
  engine and ilemit's state-machine codegen are DELETED. The coroutine ABI is monomorphic
  (`Continuation<object>` / `Result<object>`, matching JVM erasure).
- **Coroutine correctness:** suspension-crossing evaluation honors Kotlin's strict left-to-right order —
  impure operands (property / field / array-element reads) left of a suspend call are spilled into a
  state-machine field before the suspension; a `try`/`finally` across a suspension runs its `finally`
  exactly once (not early + twice); shadowed same-name locals of different types get distinct SM fields;
  exceptions propagate across a suspended `Task` boundary.
- **A suspend call inside an INLINE scope function used as a sub-expression lowers.** An expression body
  `suspend fun doFetch(lib, b) = with(lib){ b.fetch() }` (or `c.apply{ s() }.x`) no longer refuses at
  compile time: kotc inlines the scope function to a `valueBlock` verbatim (holding NO coroutine
  knowledge), and bir2cir's `SuspendColdLowering` flattens the value-block — emitting its stmts as
  ordinary statements and segmenting the suspend call in its result as a normal suspension point.
- **Interface `suspend fun` bridge is verifiable IL.** An interface member `suspend fun` (kotc emits it
  `virtual` but without an `abstract` flag, unlike an abstract-class member) is now recognized by bir2cir
  as abstract — its cold entry AND `Task<R>` bridge are emitted abstract (no body), mirroring the
  abstract-class shape — so the synthesized bridge no longer does an unverifiable non-virtual `call` on
  the abstract cold entry (`ilverify CallAbstract`). `cases/il-ifacesuspend` is now ilverify-gated.

### Language & correctness

- **`CharSequence.windowed(size){ value-type R }` no longer garbles its elements (#25 / W4-B).**
  `"abcd".windowed(2){ it.length }` returned pointer garbage instead of `[2, 2, 2]` (a reference-type `R`
  like `{ it.toString() }` was fine). Root: the pure-app `CharSequence`→`System.String` lowering
  (`CharSeqStringLowering`, bir2cir) collapsed the transform LAMBDA's `it: CharSequence` param to `string`
  and its member reads to `System.String.get_Length`/`get_Chars` — but that lambda is a `delegateNew` target
  whose `funcType` KEEPS the synthetic `<>dotkt_CharSequence` (it must match the stdlib's `Func<CharSequence,R>`
  generic sig), and the stdlib `windowed` passes a genuine `<>dotkt_CharSequence` (its `subSequence` result)
  into the delegate. Reading `String.Length` off a non-String object then reinterpreted pointer bits as an
  `Int`; a reference `R` masked it because `toString()` is a virtual `objMethod`. Fix: exempt any lambda used
  as a `delegateNew`/`delegateInvoke` target with a `<>dotkt_CharSequence` param from the lowering, so its
  param stays synthetic and its member reads stay virtual interface calls. Regression case `il-cwindowedv`
  (JVM-oracle PURE).
- **`Double`/`Float` boxed structural equality and `compareTo` now follow Kotlin's total order (C14).**
  Kotlin gives floating types a total order in the boxed/`compareTo`/structural-`equals` path (distinct from the
  primitive IEEE operators): `-0.0 != 0.0`, `NaN` is the largest value, `NaN == NaN` and `NaN.compareTo(NaN) == 0`.
  On the CLR `kotlin.Double` IS `System.Double`, whose `Object.Equals`/`CompareTo` do not match that order. kotc now
  routes a BOXED `==` on a floating value to the stdlib total-order helper `clrDoubleEquals`/`clrFloatEquals`
  (`toBits()` bit-compare) and a direct `Double`/`Float.compareTo` to `clrDoubleCompare`/`clrFloatCompare` (JDK
  total-order algorithm). Primitive `==`/`<`/`>` stay IEEE (`-0.0 == 0.0` true, `NaN == NaN` false; `il-nancmp`-green).
  `(-0.0 as Any) == (0.0 as Any)` → `false`, `(-0.0).compareTo(0.0)` → `-1`. Gate: `cases/il-negzero` (JVM-oracle PURE),
  `docs/dotkt-semantics.md §5a` (was a documented deviation, now removed).
- **Collection `==` is now STRUCTURAL, not reference identity.** Kotlin `==` on a `List`/`Set`/`Map` compares elements
  (`AbstractList/Set/Map.equals`), but the CLR-lowered BCL collections use reference `Object.Equals`, so
  `listOf(7,8) == listOf(7,8)` returned `false`. kotc now routes a collection `==`/`!=` (static-type-driven off both
  operands, mirroring `collToStringRoute`) to the stdlib structural helpers `clrCollStructEquals` (List/ordered),
  `clrSetStructEquals` (unordered), `clrMapStructEquals` (entrywise). `listOf(1)==setOf(1)` stays `false` (kind
  mismatch → reference), and non-collection reference `==` is unchanged. Gate: `cases/il-listeq` (JVM-oracle PURE).
- **`for (i in coll.indices)` / `"s".indices` now iterates in APP builds.** A for-loop over a non-literal `IntRange`
  obtained from `.indices` fell to the iterator protocol and hit an unresolved `IntIterator.hasNext` (emit-time
  crash). kotc now counter-lowers a `for` over an IntRange VALUE in app builds too: it spills the range once and reads
  `first`/`last` off the referenced type (an IntRange is always step-1 ascending). Gate: `cases/il-indices` (JVM-oracle
  PURE). (A value-type-element list still crashes in the `.indices` getter itself — the pre-existing
  `generic-ext-property-getter-typeargs` bug, separate from the loop.)
- **Same-module default argument referencing another value parameter (C3 residual).** A default like
  `fun f(a: Int, b: Int = a * 10)` called `f(5)` was rejected (`omitting a non-constant default argument`). kotc's
  positional-fill now inlines such a default with each referenced value parameter rewritten to THIS call's filled arg
  (via captureSubst, the twin of the `= this` receiver case). The cross-module `@KotlinDefault` BIR now encodes a
  value-param read as a `{param N}` token and bir2cir's `DefaultArgSplice` substitutes it (peer of its `{this}`
  substitution) — latent until `@KotlinDefault` param attributes are encoded into the ref.dll (see Known issues).
  Gate: `cases/il-defargs2` (JVM-oracle PURE).
- **`generateSequence(seed){ next }` now drives correctly for value AND reference elements (C13a).**
  Two ilemit codegen bugs in the cold-sequence path are fixed: (1) a generic capturing closure passed as a
  DELEGATE argument (the `{ seed }` closure into `GeneratorSequence`'s `Function0` ctor param) had its
  `newobj` emitted with an OPEN generic operand (`Closure`1::.ctor(!0)`) — a `TypeLoadException` at run;
  the delegate-arg binding path now instantiates the closure generic (shared with the main `closureNew`
  emit via `ResolveClosure`). (2) The `GeneratorSequence` iterator's `delegateInvoke` passed a boxed `T?`
  to a `Func<T,object>::Invoke(!0)` slot with no unbox — tolerated for a reference element (the object IS a
  valid reference) but an `InvalidProgramException` for a value element; delegateInvoke now coerces each arg
  to the delegate's declared param type (`unbox.any` — unbox a value param, castclass a reference one).
  `generateSequence(1){ it*2 }.take(3).toList()` == `[1, 2, 4]`. (`cases/il-genseq2`.)
- **`break`/`continue` in expression position now lowers (C13b).** A `break`/`continue` used as an
  `if`/`when` branch VALUE (`val end = if (…) x else break`) — Kotlin-typed `Nothing` — previously hit
  `the .NET backend does not support this expression yet: IrBreakImpl`. kotc now emits the same control
  transfer inside a `valueBlock` with an unreachable `throw` result, so it never falls through to the
  surrounding merge (mirrors the existing `throwExpr`/`returnExpr`-in-expression handling). Unblocks
  `CharSequence.windowed(size)` (`"abcd".windowed(2)` → `[ab, bc, cd]`), whose stdlib body uses the
  construct. New PURE case `il-cwindowed`.
- **`Grouping.eachCount()` (regression guard, C13c).** `listOf("a","ab","b").groupingBy { it.first() }
  .eachCount()` → `{a=2, b=1}`. Its body reads a value-type-nullable smart-cast (`Int?`) in arithmetic
  (`count + 1`) — already correct via the C1 value-slot-unwrap; locked with new PURE case `il-eachcount`.
- **Default arguments now fill positionally — an omitted middle default no longer shifts a later
  argument's slot (C3).** The kcc-review C3 family is fixed in kotc + bir2cir:
  - `list.joinToString("-") { "x$it" }` prints `x1-x2-x3` (was `System.Func…1-2-3`: the transform lambda
    had leaked into the `prefix` slot because the four omitted middle defaults were dropped, sliding the
    lambda up the argument list).
  - `str.substringAfter("=")` / `substringBefore` (default `missingDelimiterValue = this`) return the
    right value (was `InvalidProgramException`).
  - `dataInstance.copy(field = x)` compiles and runs, same-module and cross-module (the generated
    `copy`'s self-referential `y = this.y` default was previously refused with "omitting a non-constant
    default argument").
  - kotc `filledArgs` emits a positional `{"k":"defaultArg"}` placeholder for each omitted arg of a
    `@KotlinDefault`-carrying cross-module callee, and inlines a same-module receiver-referencing default
    with `this` rewritten to the call's receiver; bir2cir's `DefaultArgSplice` replaces each placeholder
    in place (by array index, matching the `@KotlinDefault` stamp) and rewrites a `{"k":"this"}` default
    to the call's receiver. See `docs/dotkt-semantics.md §7`/§10 (default omission now works everywhere —
    trailing, named-middle, reordered, and mixed with a trailing lambda). A same-module default that
    reads another VALUE parameter (`b = a * 10`) still needs a `$default` synthetic (documented follow-up).

- **Boxed-primitive dual-representation through generics no longer crashes or loses data (C2).** A family
  of value-type-via-generic-`T`/`V` miscompiles is fixed in bir2cir + ilemit:
  - `MutableMap<K, primitive>.getOrPut(k){…}` no longer silently returns `0` and skips the insert. The
    inlined `get()`'s erased-nullable (`object`) result was stored raw into the `gp:V` local, so
    `value == null` never saw the `null`; the local is now object-typed and the `else` branch unbox.any's
    back to `V`.
  - `Map<K, primitive>.getOrElse(presentKey){…}` returns the real value instead of garbage (the `object`
    `else`-branch of the result `cond` is now unbox.any'd to `V`).
  - `compareBy`/`compareValuesBy`/`sortedBy` with a primitive selector no longer NREs: a `Comparable<*>`
    selector return lowers to the NON-generic `System.IComparable` (a boxed `Int` is `IComparable`, never
    the contravariant `IComparable<object>`), and a value returned where a reference is declared now boxes.
  - `Array<Int?>` (= `Nullable<int>[]`) element access no longer SIGSEGVs: `arrayOf(1, null, 3)` /
    `arrayOfNulls<Int>(3).also{ it[0]=5 }` wrap each element into `Nullable<int>` (or `default`) at
    `stelem`, and the array creation allocates the correct `Nullable<int>[]`.
  - `fun <T : Enum<T>> …(e.name)` no longer throws a VerificationException: the self-referential
    `Enum<T>` bound lowers to the CLR `System.Enum` constraint, and `e.name` on a generic enum receiver
    binds to `System.Enum.ToString()`.
  - Covered by the JVM-oracle differential case `cases/il-boxgen`.
- **`Int`/`Long`.`toString(radix)` renders sign + arbitrary base, not two's-complement (C4).** kotc's
  legacy `System.Convert.ToString(value, base)` special-case (a BCL name in the frontend — a layer
  violation) was both wrong and crash-prone: `(-255).toString(16)` gave `ffffff01` instead of `-ff`,
  `Int.MIN_VALUE.toString(16)` dropped its sign, and any base outside `{2,8,10,16}` (`35.toString(36)`)
  threw `ArgumentException: Invalid Base`. The special-case is deleted; kotc now emits the plain
  `kotlin.text` extension call and bir2cir attributes it to the stdlib `StringNumberConversionsKt` body,
  which produces `-ff` / `-80000000` / `z`. Covered by `cases/il-radix` (JVM-oracle differential).
- **Deterministic `String`/`Double`/`Float` `hashCode()` (C5).** kotc's universal-method intercept
  unconditionally rewrote every `.hashCode()`/`.toString()`/`.equals()` on a `kotlin.*` receiver to the
  `System.Object` slot (`GetHashCode`/`ToString`/`Equals`), which shadowed the stdlib's declared
  overrides — so `"Aa".hashCode()` returned .NET's per-process-randomized hash instead of Kotlin's
  deterministic polynomial `2112`, `""`.hashCode() was non-zero, and `(-0.0).hashCode()` was not
  `Int.MIN_VALUE`. The intercept is now GATED: it falls through to the real declared member when the
  receiver TYPE declares its own override (String's polynomial hash, Double/Float's deterministic
  bit-hash — routed to the stdlib body; String's `@ClrIntrinsic` toString/equals — to their BCL slot),
  and keeps the `System.Object` slot only for a genuine universal call on a type with NO override (an
  inherited `kotlin.Any` member) and for primitive value types' bodyless `toString`/`equals`
  (`Int`/`Long`/`Char`/`Boolean` — the BCL slot is correct there). This also resolves the layer-review
  M2-vs-C5 tension (the routing is kept exactly where it is still correct). Covered by `cases/il-strhash`
  and `cases/il-pairtostr`.
- **Cross-module top-level extension-property getters no longer crash (C7).** A `val List<T>.lastIndex`,
  `val Int.absoluteValue`, `val CharSequence.lastIndex` (a top-level extension property with no
  declaring class) fell to a current-file-class static-field read that dropped the receiver entirely —
  `NotSupportedException: field <AppKt>.lastIndex not found` at emit. kotc now routes an extension
  property to `callStatic owner=null get_<name>(receiver)` (mirroring the top-level extension-FUNCTION
  path, so bir2cir attributes it to the ref.dll file class), carrying the resolved type args for a
  GENERIC getter (`get_lastIndex[T]`) so ilemit instantiates it. Covered by `cases/il-extprop`.
- **Value-type nullable smart-cast reads the value, not `HasValue` (C1).** An `Int?`/`Long?`/`Double?`
  (a CLR `Nullable<T>`) narrowed by `if (n != null)` and then read as its non-null `T` — an assignment
  (`val z: Int = n`), an arithmetic/comparison operand (`n + 1`, `n > 5`), a function argument, or a
  `return` — now UNWRAPS `Nullable<T>.Value` instead of loading the raw struct. Previously the raw
  `Nullable<T>` slot flowed into an `int`/`long`/`double` context, giving garbage (`1` for `7`), an
  `InvalidProgramException`, a SIGSEGV in arithmetic, or a wrong branch (`n > 5` taking the else). kotc
  now emits the unwrap at each JVM-style coercion slot (the smart-cast carries no IR cast node, mirroring
  the JVM's implicit `Integer.intValue()` coercion). Covered by `cases/il-nullableprim` in the JVM-oracle
  differential.
- **Value-type nullable generics (`T?`) round-trip correctly.** A generic `T?` erases to `System.Object`
  (the only CLR rep that carries a real null for a value `T`), so `listOf(10,20).firstOrNull()` returns
  `10`/`null` (not `0`), and value-type `sequence{}` / `asSequence().filter{}` / `List<Int?>.filterNotNull()`
  run to completion instead of NRE/InvalidProgram.
- **Generic collection dispatch on BCL-aliased types.** Kotlin's use-site `in`/`out` variance (a JVM
  erasure-ism) is realigned to the CLR's invariant generics, so `val x by map` delegation,
  `groupBy`/`associate*`, `.map`/`.filter`/`.add`/`.size`, and a mutable-map `for ((k,v) in m)` dispatch
  the right slot instead of `EntryPointNotFound`.
- **Null renders as `"null"` consistently.** `println(null)`/`print(null)`, a null operand in `"$x"` /
  `"" + x`, and `x.toString()` all render the literal `"null"` (Kotlin semantics) rather than an empty
  string; nested collections/maps stringify Kotlin-style (`{a=[1, 2]}`) instead of raw .NET type names,
  recursively.
- **Evaluation-order fixes:** a value-producing `try` in an operand slot (`1 + try{…}`) is hoisted to a
  preceding temp; a `when`-subject / safe-call receiver / `x in a..b` operand evaluates exactly once;
  strict left-to-right operand order is preserved.
- **~55 further correctness fixes**, including: `Char - Char → Int` and `Char + Int → Char`;
  `Char.digitToIntOrNull()` value+null join; ordinal `String.compareTo`; `MutableList.set`/`removeAt`
  return the old element; `catch (IndexOutOfBoundsException)` catches both .NET out-of-range types;
  `x is Collection<*>` / `is Map<*,*>` holds for value-type collections; `HashSet`/`HashMap(capacity,
  loadFactor)` construct; float `NaN <=`/`>=`; `return` inside nested `try`/`finally`; store/return
  coercion into reference/nullable slots; `printStackTrace()` on any `Throwable`-typed receiver.
- **Number parsing matches JVM** (deviation, recorded in `docs/dotkt-semantics.md`): `String.toInt()`/
  `toLong()`/… are strict base-10 and throw a real catchable `NumberFormatException`; `toDouble()`/
  `toFloat()` parse invariant-culture and reject the group separator (`"3,14".toDouble()` throws).
- **`kotlin.time`:** `2.seconds + 3.seconds` and the `Duration` value-class arithmetic/formatting run
  end-to-end.
- **Unsigned `UInt`/`ULong` division, remainder, and `toString(radix)`** now have real pure-Kotlin bodies
  (previously threw); **enum reflection** `enumValues<T>()` / `enumValueOf<T>()` / `enumEntries<T>()` work
  (documented gaps for non-inlined generic contexts).
- **Generic `Array<T>` ops** (`copyOf`, `copyOfRange`, `plus`, `plusElement`, `orEmpty`, `arrayOfNulls`)
  run pure-Kotlin (generic `newarr !T`, reified on the CLR).
- **`kotlin.Result` / `runCatching`, user `Comparable<T>` sorting, `Map` property delegation, and
  cross-module default arguments** (a 2-tier `[DefaultParameterValue]` / embedded-BIR-splice rule) all run.
- **`@kotlin.concurrent.Volatile` is now a real CLR volatile field** (`modreq(IsVolatile)` + the
  `volatile.` prefix on backing-field access) — it was previously a silent no-op.

### .NET interop

- **Idiomatic .NET events: `w.Changed += handler` / `-= handler`.** A .NET event surfaces as a
  `ClrEvent<T>` member with `+=`/`-=` operators (replacing the `add_`/`remove_` accessor stopgap), for
  instance, static, and interface events. The event Kotlin↔CLR relation now lives entirely in bir2cir.
- **Interop without static registries (internal, A2).** All four process-global name-keyed side-tables in
  kotc were replaced by pure projections of facadegen metadata keyed on the resolved IR `ClassId`/
  `CallableId`; the emitted BIR is byte-identical. User-visible consequence: same-name top-level overloads
  across different DotKt file facades now route 1:1 (previously they collided last-wins).
- **facadegen symbol-surface completions:** constructed-generic member types (`IList<Widget>`,
  `Dictionary<String,Widget>`), transitive (reachable-closure) injection, aliased imports (`import … as
  SB`), operators on generic .NET types, C#-origin `[Extension]` methods, generic constraints +
  declaration-site variance round-trip, and same-name arity families (`Task` vs `Task<T>`).
- **Round-trip carriers:** re-consuming a DotKt `.dll` as Kotlin now restores `sealed` (modality +
  cross-module inheritance enforcement + exhaustive `when` with no `else`) and `fun interface` nature.
  (Deviations, `docs/dotkt-semantics.md` §10: a `fun interface` restores the nature but a bare lambda
  still won't SAM-convert; an `enum class` re-consumes as an `object` of `val`s — both pinned-compiler
  limits.)
- **`CharSequence` is `System.String`** and **`Appendable` is `System.Text.StringBuilder`** on the CLR
  (each a JVM abstraction with a single faithful CLR representation), so `joinToString`/`joinTo` and
  CharSequence polymorphism run. `CharSequence` is an immutable snapshot, not a live view (deviation,
  §5b); a user `class S : CharSequence` keeps a synthetic polymorphic interface.
- **Suspend function-type POSITIONS now carry round-trip metadata (H2).** A `suspend (…) -> T` in a
  parameter / return / property / field position has its type slot erased to `object` (a suspend-lambda
  value is a `Continuation`-based state-machine object, not a `Func` delegate), which previously destroyed
  the suspend origin AND its arg/return shape — `fun run(block: suspend () -> T)` was indistinguishable
  from a plain function-typed one in the emitted metadata. bir2cir now records the pre-erasure
  `sfunc:<ret>:<args>` shape as a positional fact (`suspendFnType`/`retSuspendFnType`) and ilemit stamps
  it as an embedded `[KotlinSuspendFunctionType(shape)]` at every such position (mirroring the
  `[Nullable]`/`[KotlinInline]` metadata-carrier model — a SHAPE string, not a bare flag, since the CLR
  type is `object`). Verified applied+reflectable on the stdlib coroutine intrinsics at all four position
  kinds (`createCoroutine`/`startCoroutine` receivers, `suspend()`'s return, `DeepRecursiveFunction.block`
  property). NOTE: the metadata now SURVIVES emission, but facadegen does not yet reconstruct the
  `suspend (…) -> T` type on re-consumption — that final restore hop requires a kotc `ClrTypeInjection`
  change (an `sfunc:` case in `coneOf` building `kotlin.coroutines.SuspendFunctionN`), tracked separately.

### Standard library

- **The compiler's hand-written stdlib lowerings are retired into a real pure-Kotlin CLR stdlib.**
  `kotlin.math`, `String`/`Char` ops, `trim`/`pad*`/`replace` (STRING_OPS), `coerceIn`/`coerceAtMost`/
  `coerceAtLeast`, `isBlank`, `println`/`print`, `Regex`, `AutoCloseable`/`use{}`, `Lazy`/`by lazy`,
  `Throwable.message`/`cause`/`printStackTrace`, the collection and `StringBuilder` member slots, and
  `Int/Long.toString(radix)` now run their real Kotlin bodies (bound via `@ClrTypeAlias`/`@ClrIntrinsic`
  on the reference stdlib and substituted by bir2cir). This is the cardinal-rule payoff: correctness
  fixes land stdlib-side, never as compiler special-cases.
- **`Regex`** runs on real bodies: `matches`/`find`/`replace`/`replaceFirst`/`split`/`.value`/`.pattern`/
  `groupValues`, plus named + indexed groups (`replaceFirst` no longer corrupts memory).
- **`lazy {}`** is pure-Kotlin and thread-safe by default (`SynchronizedLazyImpl`) with a lock-free
  double-checked-locking fast read (one volatile load on the hot path), backed by the now-real `@Volatile`.
- **`Map`/`MutableMap` → `IDictionary<K,V>`** (both — deliberately NOT a read-only/mutable split, §5c) with
  Kotlin-semantic members via `ClrMapDefaults`; core collection ops (`map`/`filter`/`fold`/`toList`/…) run
  on real Kotlin bodies over BCL collections.
- **`MutableMap.merge(key, value) { old, new -> … }`** now works (C2). On Kotlin/JVM `merge` is the
  `java.util.Map.merge` member (a `java.util.function.BiFunction` overload); on the CLR that erased SAM
  materialized the Kotlin lambda as `Func<V,V,object>` and then `castclass`-ed it to the `? super V`-erased
  `Func<object,object,object>` → `InvalidCastException`. `merge` is now declared on the `MutableMap` builtin
  with a Kotlin function-type parameter (the frontend binds to THIS overload, so no cast), routed to
  `ClrMapDefaults.clrMapMerge` for BCL-aliased receivers. Semantics mirror `java.util.Map.merge`
  (absent → insert; present → remap; null result → remove).
- **`groupBy {}` read surface is covariance-safe (C2).** `listOf(1,2,3,4).groupBy { it % 2 }` returns a
  `Map<K, List<V>>` (`IDictionary<K, IReadOnlyList<V>>`) but the runtime object is the `Dictionary<K, MutableList<V>>`
  (`IDictionary<K, IList<V>>`) that `groupByTo` built and mutated — and CLR `IDictionary<,>` is INVARIANT in the value,
  so the runtime map is not assignable to the read interface: reading it (`toString`/`m[k]`/`for ((k,v) in m)`/`.entries`/
  `.keys`/`.values`) threw `EntryPointNotFound`/`InvalidCastException` through the mismatched generic slot. The
  `ClrMapDefaults` READ helpers now route through the NON-GENERIC `System.Collections.IDictionary` (implemented by every
  `Dictionary<K,V>` regardless of V) via `IDictionaryEnumerator` + `get_Item(object)` — the read-side mirror of bir2cir's
  write-side `MapVarianceRealign`. Regular `mapOf`/`mutableMapOf` read/iterate/`toString` are unaffected. Verified against
  the JVM oracle (`cases/il-groupby2`, added to `verify-differential`).
- **`groupBy {}.mapValues {}` and a direct `m.size`/`m.containsKey` on a groupBy result are covariance-safe (#29).**
  `size` and `containsKey` are now UNBOUND on the `Map`/`MutableMap` interface (their `@ClrIntrinsic("Count")`/
  `("ContainsKey")` bindings, which read through the INVARIANT generic `IDictionary<K,V>`, are removed); bir2cir Rule 5m
  routes `get_size`/`containsKey` on a `Map`/`MutableMap` owner to the covariance-safe `ClrMapDefaults.clrMapSize`/
  `clrMapContainsKey` (non-generic `ICollection.Count` / `IDictionary.Contains`), exactly as `get`/`get_keys`/`get_values`
  already route. This also makes `mapValues`' transitive `mapCapacity(this.size)` pre-size covariance-safe, so
  `listOf(1,2,3,4).groupBy { it % 2 }.mapValues { it.value.size }` no longer throws `EntryPointNotFound`. Normal
  `mapOf`/`mutableMapOf` `size`/`containsKey` stay correct. Verified against the JVM oracle (`cases/il-mapvalues`).
- **Nested collections/maps inside `Pair`/`Triple.toString()`** render Kotlin-style (C11):
  `(listOf(1, 2) to listOf(3, 4)).toString()` is `([1, 2], [3, 4])`, not the raw
  `(System.Collections.Generic.List\`1[System.Int32], …)`. A tuple component's erased generic static type
  used to reach .NET's `Object.ToString()`; components now route through the runtime collection-aware
  stringifier (`clrRenderTupleElement` → `clrElemToString`), matching `println(list)`.
- **`@ClrProperty`** explicit accessor binding (READ/WRITE) replaces the fragile `get_`/`set_`
  intrinsic-string prefix sniff.
- **`String.format`** binds to .NET `String.Format` — use .NET composite format (`"{0:F2}"`), NOT Java
  printf (`"%.2f"`) (BREAKING deviation, §5).
- **`abs(Int)`/`abs(Long)` now WRAP at `MIN_VALUE`** (matching Kotlin's unchecked negation:
  `abs(Int.MIN_VALUE) == Int.MIN_VALUE`) instead of throwing `OverflowException`. The `@ClrIntrinsic("System.Math.Abs")`
  binding — whose checked overload throws at `MIN` — is dropped for the integer overloads in favor of the
  pure-Kotlin body `if (n < 0) -n else n`; the `Float`/`Double` overloads keep their `System.Math.Abs`
  binding. Verified against the JVM oracle (`cases/il-mathabs`, added to `verify-differential`).
- **Deterministic `String`/`Double`/`Float` `hashCode()` bodies added** (polynomial hash for `String`,
  bit-based for `Double`/`Float`) replacing reliance on .NET's randomized/native `GetHashCode`. The
  correct stdlib bodies now ship, but they are still SHADOWED at the call site by kotc's universal-method
  intercept (`BirEmitter.kt` `isBuiltin && name=="hashCode"` → `objMethod GetHashCode`), so `"Aa".hashCode()`
  remains non-deterministic until that intercept is gated to fall through when the receiver type declares its
  own `hashCode` — a compiler-layer follow-up, not a stdlib change.

### Compiler architecture (4-layer / layer purity)

- **ilemit: `@kotlin.clr.KotlinDefault` custom attributes now encode (#23b).** The ref-stdlib emit was
  skipping ~172 `@KotlinDefault(index, bir)` applications with `ArgumentException: Parameter count does not
  match`. Root: `BuildCab` stamps a param/method attribute during pass-3 member declaration, but a
  `@KotlinDefault` on an EARLIER type's parameter reached `BuildCab` before `kotlin.clr.KotlinDefault`'s own
  `(int, string)` ctor was defined (pass 3 declares types one at a time) — the old
  `ti.Ctors[0] ?? DefineDefaultConstructor()` then minted a bogus parameterless ctor per application and every
  stamp failed the arity check. Fix: `EnsureCtorsDefined(ti)` defines a type's ctors from its CIR on demand
  (idempotent, guarded), pulled early by `BuildCab`, which now also picks the ctor whose parameter count
  matches the applied argument count. bir2cir reads these attributes from the reference assembly to splice a
  callee's omitted non-constant (`CharSequence`/object) default at a cross-module call — so a Tier-2
  default-omitted call (`listOf(1,2,3).joinToString()`, `separator`/`prefix`/`postfix` `CharSequence`
  defaults) now fills correctly instead of crashing. (Pre-existing `@Deprecated`/`@OptIn`/`@WasExperimental`
  skips — Kotlin optional-param / `KClass`-arg annotations `CustomAttributeBuilder` can't encode — are
  unchanged and out of scope.)

- **ilemit dead-code sweep (M1).** Removed producer-zero legacy CIR handling now that bir2cir emits
  the plain BCL-call / collection-factory vocabulary: the 21 unreachable retire-list `EmitExpr` cases
  (`nullableOf`/`strRepeat`/`split`/`associateWith`/`associateBy`/`groupBy`/`linq*`/`listGet`/`listSet`/
  `mapGet`/`mapSet`/`mapSize`/`tupleNew`/`tupleItem`), the standalone native-CIR `clr.*` handlers
  (`clr.newobj`/`clr.call`/`clr.ldfld`/`clr.ldsfld`/`clr.stfld`/`clr.stsfld`/`clr.isinst`/`clr.isinst.ref`/
  `clr.castclass`) and their 6 dead-only helpers (`EmitNativeClrNewObj`/`Call`/`FieldGet`/`FieldSet`/
  `IsInst`/`CastClass`) plus 2 exclusive sub-helpers. The live computed-kind factories
  (`listNew`/`setNew`/`mapNew`/`strReversed`) and the 11 shared `EmitNativeClr*` helpers stay.
- **Make-it-loud: an unresolved CLR member no longer silently degrades to a runtime NRE.** bir2cir Rule-4
  used to emit a `clrInstance` for ANY member it could not resolve; ilemit's `clrInstance` fallback then
  reflected (`recv.GetType().GetMethod(name)`, no signature match) → `null` → an opaque `NullReferenceException`.
  Now: (1) bir2cir refuses, at compile time, a lowercase-camelCase member on a CLR-bound NON-interface owner
  (naming `owner.member`) — a BCL member is PascalCase, so such a member is an unbound routing MISS; (2)
  ilemit's `clrInstance`→dynamic-dispatch fallback is gated to INTERFACE owners (the clrInstance analog of the
  `callInstance` path's `OwnerHasClrInterface` gate), so a miss on a concrete BCL owner throws at EMIT. The
  intended dynamic dispatch (`MutableCollection.addAll/removeAll/retainAll` via `ICollection<T>`) is preserved.
- **bir2cir emits a suspend-lowering diagnostic when it drops a fun from the cold-transform set** — a
  shape-eligible suspend fun with an unresolvable suspend call (no same-assembly cold entry, no ref.dll
  Suspend-flagged member) now names the fun and the offending call on stderr, instead of silently surviving
  to trip the distant "suspend method reached codegen un-lowered" error at the ilemit boundary.
- **kotc reads NEITHER `@ClrIntrinsic` NOR `@ClrTypeAlias` and emits pure Kotlin.** All Kotlin↔CLR
  substitution is bir2cir's, sourced from the reference stdlib dll: kotc emits `kotlin.Unit` (bir2cir
  derives `void`), the Kotlin exception FQN (bir2cir substitutes the `System.*` type), a plain
  `annotation` flag (bir2cir derives the `: System.Attribute` base), the `kotlin.reflect.KClass` member
  (bir2cir derives `System.Type.Name`/`.FullName`), and plain member calls (bir2cir renames the BCL slot).
  The `clrName`/`annClr` side-tables and the `System.Math`/`System.Console`/exception/collection/
  StringBuilder/Regex/Closeable hardcodes are gone.
- **Deleted the `kotlin.String.length` → `System.String.Length` hardcode in kotc (M2).** It was redundant
  CLR knowledge: the stdlib's `@ClrIntrinsic("Length")` binding + bir2cir's `MemberCallSubstitution` already
  rewrite the plain `kotlin.String.length` member read (the sibling `String.get` → `get_Chars` was cleaned the
  same way). `"abc".length` stays `3`.
- **kotc stamps a stable `suspendIntrinsic:true` marker on the lowered `suspendCoroutineUninterceptedOrReturn`
  block (L1).** bir2cir's cold-suspension recognizer already prefers this flag over sniffing the intrinsic's
  fake `throw` message string, so the fragile string-match path becomes dead weight (its removal is a bir2cir
  follow-up). suspend samples unchanged.
- **The primitive/`Comparable` `compareTo` lowering moved to bir2cir** — the last kotc CLR-knowledge leak
  of its class. kotc emits a plain `callInstance` (`kotlin.Int.compareTo` / `kotlin.Comparable.compareTo`);
  bir2cir derives a primitive `System.<Prim>.CompareTo` and a `constrained. System.IComparable<T>::CompareTo`
  (its `Constrainify` pass now recovers the receiver static type from a `callInstance` return / `arrayGet`
  element and builds `IComparable<recvType>` directly, so a `Comparator.compare` override — whose `T` lives on
  an outer scope — still constrains). The runtime stdlib emits byte-behavior-identical constrained IL.
- **Removed the dead `Assembly.LoadFrom` ref-scan in bir2cir** — it always threw `TypeLoadException` on the
  metadata-only reference stdlib (surfacing a spurious `metadata scan failed: … 'kotlin.String'` warning once
  ref-scan diagnostics started reaching stderr) and its `Members`/`Types`/`Functions` output fed only
  callerless resolution helpers. The live `@ClrTypeAlias`/`@ClrIntrinsic`/rule-3 substitution reads solely from
  the `MetadataLoadContext` scan (loads per-type cleanly); genuine ref-scan failures still surface loud.
- **Single type-lowering path.** The `CompatBir` verbatim-copy mode and the `--compat-bir`/`--native-cir`
  flags are removed — one env-gated bir2cir pass rewrites the Kotlin type vocabulary into the CLR-codegen
  vocabulary ilemit consumes.
- **Namespace projection removed** (`[DotKtNamespaceProjection]` and the associated flag/meta/MSBuild item)
  — a DotKt assembly's types are seen 1:1 at their .NET namespace as the Kotlin package.
- **Pruned stale tombstone comments** across kotc / bir2cir / ilemit / facadegen / stdlib / scripts —
  dead-symbol references (`ClrTypeRegistry`/`ClrTopLevelRegistry`/`ClrEventRegistry`, `netType`,
  `NET_EXCEPTIONS`, `--compat-bir`/`--native-cir`, the retired `add_`/`remove_` event model) and
  `(RETIRED)`/`is GONE` archaeology left by the migration deletions are trimmed to present-tense layer
  guards or removed; genuine "why" rationale is preserved. Comment-only, no behavior change.
- **facadegen enforces the `kotlin.*` BINDING invariant in-layer (M3, defense-in-depth).** The rule
  "`kotlin.*` comes from the frontend JAR, never from facadegen" is now guaranteed by the owning layer:
  a `kotlin.*` symbol is short-circuited in BOTH the seed resolution AND `ShouldInject` (new
  `IsKotlinStdlibSymbol` predicate), so facadegen can never inject a stdlib symbol (which would be
  semantically degraded and would collide with the JAR's). The deliberate `kotlin.clr.await` CLR-async
  bridge is whitelisted — it is surfaced textually by `EmitTaskAwait`, never through the injection
  closure. Output-neutral (the closure never reached a `kotlin.*` type under the existing "don't
  `--scan-asm` the stdlib" discipline); the guarantee previously lived only downstream
  (`ClrTypeInjection.kt`, injected classes/interfaces — not top-level functions) plus that discipline.
  Same sweep: `System.Nullable\`1` added to `NO_INJECT` (a value-type `X?` is projected to Kotlin `X?`
  by `Map`, never the literal `Nullable<X>` — its open-definition injection was a stray dead type,
  mirroring `Span\`1`); a member signature type that degrades to `Any?` now emits a deduped `note:` to
  stderr (a silent `Any?` weakens the injected overload); the retarget `System.Runtime` fallback ref
  now carries the well-known ECMA PublicKeyToken `b03f5f7f11d50a3a` (a PKT-less ref failed a C#
  `<Reference>` bind); and two stale facadegen comments (`clrgen` package, `func:<ret>:<arg>` grammar)
  are corrected. No metadata-output change beyond dropping the dead `Nullable\`1` injection.

### Tooling, build & gates

- **`Makefile` orchestrator** over the canonical scripts (incremental targets `all` / `toolchain` /
  `stdlib{,-jar,-ref,-rt}` / `pack` / `verify*` / `dev`), and a **4-package NuGet structure** (Sdk /
  Toolchain / Stdlib / Templates) that fixes the packaging gap where the shipped SDK carried no stdlib
  DLLs and could not actually compile a consumer.
- **`scripts/` overhaul:** one `<verb>-<noun>` naming scheme aligned with the make targets, a shared
  `scripts/lib.sh` (strict mode, common tool/artifact paths, `need_*`/`build_tool`), and two harness bug
  fixes (the rt grep-exit-1 footgun; the verify-il dropped-FAIL-line race — now one atomic result record
  per sample).
- **Every gate is XFAIL-zero** (verify-il, verify-differential, verify-roundtrip, verify-ktproj). The
  known-fail baselines are machine-readable `XFAIL_*` maps diffed on each run (printing `NEW-FAIL`/`FIXED`),
  replacing prose fail-counts.
- **Failure posture is loud, not silent:** ambiguous `@Clr*` overloads, an un-lowered `suspend` fn reaching
  ilemit in an app build, ref.dll-scan diagnostics, and per-file stdlib-emit crashes now fail or warn
  explicitly instead of silently dropping work.
- **frontend stdlib jar** (`kotlin-stdlib-clr-frontend.jar`) replaces the JVM `kotlin-stdlib.jar` as kotc's
  `-classpath`, killing the `java.util.*` typealias leak; its `.kotlin_builtins` are generated from our own
  sources.
- **Gate-hygiene fixes (final-review 2026-07-05):** closed the `verify-differential` `empty==empty`
  false-MATCH hole (a MATCH now requires BOTH the jvm oracle and the clr side to have produced real,
  non-empty output — two compile/run failures no longer silently pass as a MATCH); removed a stale
  `verify-il` comment referencing the retired `XFAIL_RUN[cobuild]` and a duplicate `comaindrain`
  invocation; and **wired 44 run-only cases into the `verify-il` ilverify pass** (they were run-checked
  but had no formal-verification coverage). Two cases are documented-excluded: `stackalloc`
  (`localloc` is unverifiable by ECMA-335), plus `ifacesuspend` which runs correctly but emits a
  genuinely-unverifiable `CallAbstract` in the interface-suspend bridge — surfaced as a real latent
  finding, not XFAIL-hidden. (`strops` was the third; its primitive-array `StackUnexpected` is now
  fixed in ilemit and it is wired into the ilverify pass — see below.)
- **`verify-differential` coverage expanded to the JVM oracle (COV1, kcc review §2B) — the structural
  fix.** The differential gate (the ONLY gate that checks against real Kotlin/JVM semantics) validated
  only ~43 samples; the other ~120 pure-Kotlin `il-*` samples self-scored against DotKt-captured fixed
  strings in `verify-il`, so a Kotlin-INCORRECT mapping could pass green forever. The JVM-runnable
  pure-Kotlin `il-*` subset (string / collection / math / regex / unsigned / enum / data-class /
  generics / delegates / lazy / …) is now promoted into the `PURE` list, so each runs on BOTH the
  kotlin/jvm oracle and the shipping CLR backend and must match — **163 samples, ALL MATCH**.
  CLR-specific-by-design samples are excluded with a per-sample reason: `il-bmore`/`il-fmt` (`.format`
  uses .NET composite format strings, literal text on the JVM), `il-reified` (`Int::class.simpleName`
  is the CLR name `Int32` vs the JVM's `Int`); the coroutine cold-core family and all interop
  (`il_check_imports`/`il_check_inject`) samples stay out (not JVM-runnable). Two harness bugs found and
  fixed along the way: a `package`-declared sample ran `java <Class>` without the FQN (empty JVM output →
  false DIFF — now prefixes the package), and the parallel result echoes shared one redirected stdout
  offset and clobbered each other under a warm cache (the same race `verify-il` already retired — now one
  atomic result record per sample). This makes the C1–C11-class regressions the review found redden the
  gate instead of passing green.
- **`il-strops` ilverify finding FIXED (2026-07-05) — the last ilverify-dirty finding.** The 3×
  `[StackUnexpected][found Char]` in `main` was the `String.trim(vararg chars: Char)` call site building
  a `char[]`, where `ilemit` emitted the generic token opcode `stelem <System.Char>` instead of the
  specialized `stelem.i2`. ECMA-335 requires the specialized `stelem`/`ldelem` opcode for a PRIMITIVE
  element type; the token form is unverifiable for primitives (`stelem <char>` → `[found Char]`,
  `ldelem <char>` → `[found Short]`; `stelem.i2`/`ldelem.u2` verify clean). Fixed with a shared
  `EmitStelem`/`EmitLdelem` helper (`Program.cs`) that selects the specialized opcode for a BCL
  primitive element (char→`stelem.i2`/`ldelem.u2`, int→`stelem.i4`, …), `stelem.ref`/`ldelem.ref` for a
  reference element, and keeps the TOKEN form ONLY for a generic-parameter (`!T`/`!!T`) or non-primitive
  struct element (specializing a generic-param element would be wrong for a value-type instantiation).
  Wired into all five array store/load sites (`EmitNewArray`, `newArrayInit`, `arrayGet`/`arraySet`,
  for-in-over-array). `il-strops` now RUNS correct and verifies clean, and is wired into the `verify-il`
  ilverify pass — leaving `verify-il`/`differential`/`ktproj`/`roundtrip` + ilverify all XFAIL-zero.
- **Repo hygiene (kcc review §X1/§L2, 2026-07-06):** untracked the 90 compiled DLLs (+87
  `runtimeconfig.json`, ~3.1M) under `dotkt-out/` — the `dotkt.sh`/`dotkt-keep.sh` default output dir,
  pure build artifacts, never fixtures — and added `dotkt-out/` to `.gitignore` (it no longer dirties
  every build, pollutes diffs, or masks stdlib regressions). Pruned a cluster of stale
  comments/dead references that were pure archaeology: the dead `steps`/`coClass` node-kind entries in
  bir2cir `SuspendColdLowering` `LambdaKinds` (the `sequenceNew` producer is gone; the surviving
  `steps`/`coClass` method-property guards are a separate mechanism, kept), the retired `delay`/`blockOn`
  reference in the `InteropBridgeFileClass` comment, ilemit's `cps-field` store-target comment (CPS is
  gone), and kotc's `native-cir`/`compat-passthrough` comment (the dual-track was removed 2026-06-30).
  Behavior-neutral: every gate stays XFAIL-zero.

## 0.9.3 — 2026-06-24

Round-trip interop: a DotKt-compiled assembly can now be consumed **as Kotlin** by another
`.ktproj` (the basis for shipping compiled kotlinx-* libraries for the CLR), plus bidirectional
compile-time `ProjectReference` between C# and Kotlin projects.

### Added
- **Reference-type nullability via .NET NRT + platform types.** A reference-type `String?` now rides .NET's own
  nullable-reference metadata (`[Nullable]`/`[NullableContext]`) instead of a bespoke attribute: ilemit stamps
  `[NullableContext(1)]` per type and `[Nullable(2)]` on each nullable reference return/parameter, so a **C# consumer
  also sees** DotKt's `String?` as nullable. facadegen reads NRT uniformly for every assembly, which closes a soundness
  hole — a reference type from any non-DotKt assembly was previously injected as strictly non-null. A reference type from
  an assembly built without `<Nullable>enable</Nullable>` (oblivious) now injects as a Kotlin **platform type** `T!`
  (`ConeFlexibleType(T, T?)`, à la Kotlin/JVM's treatment of un-annotated Java), instead of lying "non-null". The old
  `[KotlinNullable]` attribute is retired. See `docs/dotkt-semantics.md` §9.
- **Round-trip metadata attributes are compiler-embedded per assembly.** The `[Kotlin*]` attributes moved to namespace
  `DotKt.Runtime.CompilerServices` and are now defined as internal types inside each emitted assembly (the csc model for
  its own `NullableAttribute`/`IsReadOnlyAttribute`) rather than referenced from `DotKt.Runtime`. They are metadata-only,
  so this makes each assembly self-contained and removes the "ilemit needs `--ref DotKt.Runtime` to stamp" coupling.
  (`[DotKtNamespaceProjection]` stays a referenced type — it is assembly-level, which PersistedAssemblyBuilder can't
  embed.) `DotKt.Runtime` now carries only executed code plus that one attribute.
- **Consume a DotKt assembly AS KOTLIN — Kotlin-modifier round-trip.** Kotlin-language facts with no native .NET
  representation now survive compilation and are restored on a consuming module's FIR, so a `.ktproj` can use
  another DotKt-compiled assembly with idiomatic Kotlin syntax (the basis for shipping compiled kotlinx-* libraries
  for the CLR). Embedded `DotKt.Runtime.CompilerServices` attributes (`[KotlinFunction(Infix|Operator|Suspend)]`, `[KotlinFileClass]`) are
  stamped onto the IL by ilemit, read back by `facadegen --meta`, and restored by the FIR injector:
  - `infix fun` / `operator fun` — restored as `status { isInfix/isOperator }` (call notation + operator resolution).
  - `suspend fun` — emitted as `Task<T>`; restored as `suspend fun(): T` (the Task is unwrapped and re-awaited by the
    coroutine machinery), for both members and top-level functions.
  - top-level functions — a `<File>Kt` facade carries `[KotlinFileClass]`; its statics restore as top-level package
    functions, called via a new `ClrTopLevelRegistry` as a static call on the file class. **Generic** top-level
    functions are restored with their type parameters and called via `clrGenericStatic`, so a cross-module
    `inline fun <reified T>` is consumed as a generic method (`f<Int>()`) — CLR generics are reified, so no inlining
    or carried body is needed. (The only cross-module inline case that can't degrade — a lambda with a non-local
    `return` — fails with a clean compile error; see docs/design-kotlin-metadata-attributes.md.)
  - `final`/`open`/`abstract`, visibility, and **`reified`** need no attribute — they ride plain .NET metadata (CLR
    generics are reified, so `inline fun <reified T>` is just a generic method).
  - **`inline` (with a lambda) — cross-module non-local `return`.** DotKt inlines at EMIT time (BirEmitter, no JVM
    `FunctionInlining` lowering), so a cross-module inline call to a body-less injected stub can't be inlined — which
    means a non-local `return` through the lambda (the one inline case that can't degrade to a regular call) was a
    compile error. Now: `ilemit` stamps `[KotlinInline(birJson)]` with the function's own BIR body; the injector
    marks it `inline`; and the consumer's `ilemit` reads that body from the referenced assembly and splices it at the
    call site (param + lambda-body substitution), so the lambda's `return` becomes the caller's `return`. Lighter than
    JVM's `@Metadata` (BIR, emit-time, no IR deserializer). Verified by `scripts/verify-roundtrip.sh`.

- **Bidirectional `ProjectReference` (R-1, reverse interop)** — a C# project can now
  `<ProjectReference>`/`<Reference>` a Kotlin `.ktproj` at **compile time** (not just
  reflection-load), so a Visual Studio solution can split code across C# and Kotlin
  projects that reference each other. New build-time tool **`tools/retarget`**
  (Mono.Cecil) repoints the emitted assembly's BCL `TypeRef`s off the single
  `System.Private.CoreLib` onto the real contract assemblies (`Object`/`Task` →
  `System.Runtime`, `List`/`Dictionary` → `System.Collections`, …) — the type→contract
  map is the forward path's machinery in reverse (the ref pack via `MetadataLoadContext`).
  This is pure post-emit metadata surgery, so it sidesteps the Reflection.Emit/MLC
  generic-instantiation limits that sank the two earlier attempts; `List`/`Dictionary`
  and `suspend fun` → `Task<T>` all consume cleanly from C#. New sample
  **`samples/ktproj-bidir`** (cslib.csproj ← klib.ktproj ← app.csproj: forward + reverse
  in one graph) is green in `verify-ktproj.sh`. Default ON; opt out with
  `<KotlinClrRetarget>false</KotlinClrRetarget>` / `<DotKtRetarget>false</DotKtRetarget>`.

### Fixed
- **A closure/local function capturing an enclosing generic type parameter crashed ilemit.** A lambda or local
  function inside a generic function that captured a value whose type involves the enclosing `T` (a `T` value, a
  `(T)->Unit`, a `List<T>`) threw `NotSupportedException: unresolved generic type parameter T` — the synthesized closure
  class / lifted method wasn't generic over `T` (reified CLR generics need it). The closure class is now generic over the
  captured type parameters and instantiated with the enclosing ones at the capture site; a captured local function is
  lifted to a generic static method. (An object expression or local *class* that captures an enclosing type parameter is
  not yet supported and now fails with a clear compile error instead of crashing.)
- **Cross-file / namespaced interface polymorphism crashed ilemit.** A class in a Kotlin `package` implementing an
  interface from another file threw `KeyNotFoundException` during the interface-link pass — `FindMethod` was keyed by the
  TypeBuilder's simple name while `_types` is keyed by the BIR full name. Now keyed consistently.
- **A generic function applying `(T) -> Unit` to a `List<T>` crashed ilemit.** `for (x in xs) f(x)` inside
  `fun <T> each(xs: List<T>, f: (T) -> Unit)` threw `NotSupportedException` (TypeBuilder generic instantiation doesn't
  resolve members) — the `forEach` lowering called `.GetMethod` on `IEnumerable<T>` directly instead of via
  `TypeBuilder.GetMethod`.
- **Assigning a Boolean to a .NET `bool?` property failed the frontend.** facadegen mapped a nullable value type
  `Nullable<X>` to the literal generic `Nullable<X>` (a distinct type) instead of Kotlin's `X?`, so e.g.
  `checkBox.IsChecked = true` reported an assignment type mismatch. `System.Nullable<X>` now maps to `X?`.
- **Kotlin → Kotlin `ProjectReference` round-trip — a library's top-level functions vanished.** A `.ktproj` consuming
  another `.ktproj` as Kotlin got `unresolved reference` on the library's top-level functions (`import mylib.boxed`),
  while classes resolved fine. The MSBuild `ilemit` step built its `--ref` list from `@(ReferenceCopyLocalPaths)`, which
  doesn't contain `DotKt.Runtime` (a compile reference, not copy-local) — so ilemit couldn't resolve the metadata
  attribute types and **silently skipped stamping** `[KotlinFileClass]`/`[KotlinFunction]`. The file facade then looked
  like a plain class to the consumer, which finds top-level functions only on `[KotlinFileClass]`-marked classes. ilemit
  is now passed `DotKt.Runtime` from `@(ReferencePath)` (SDK + in-repo targets). New regression test
  `samples/ktproj-roundtrip` (this Kotlin→Kotlin `ProjectReference` path had no coverage before).
- Renamed the metadata attribute `[KotlinFile]` → **`[KotlinFileClass]`** (clearer: it marks the `<File>Kt` *class* that
  holds a file's top-level declarations). Pre-1.0, no compat shim.
- **Omitting a non-constant default argument is a clean compile error instead of a backend crash.** A default that reads
  the callee's own parameters/receiver (`b: Int = a * 10`, or a data class `copy`'s `x = this.x`) can't be filled by
  inlining it at the call site (`a`/`this` aren't in scope there) — it needs callee-side evaluation (Kotlin/JVM's
  `$default`), not yet implemented on the .NET backend. Such an omission previously crashed ilemit with
  `InvalidProgram`/`NotSupported`; it now reports a source-located error at the omitting call. Detected at the call site,
  not the declaration, so a data class whose `copy` is never arg-omitted still compiles.
- **Kotlin packages are now projected to .NET namespaces** (`package geom; class Vec` → `.NET geom.Vec`, file facade
  `geom.LibKt`). Previously every type was flattened to the **root** namespace — a correctness bug: two classes with
  the same simple name in different packages (e.g. `alpha.Box` + `beta.Box`) both emitted as `.NET Box` and **collided**
  (ilemit crash), and a packaged library couldn't be consumed across an assembly boundary (`import geom.Vec` resolved
  nothing). `BirEmitter` now qualifies top-level classes/interfaces/enums and the file facade with `packageFqName`
  (nested types stay simple-named — their outer carries the namespace; root-package code is unchanged by construction).
  This unblocks consuming a packaged DotKt library via MSBuild, including its top-level functions (`import geom.greet`).
- **Member `suspend fun` returning a user type** crashed ilemit (`AsyncTaskMethodBuilder<T>`/`Task<T>`/`TaskAwaiter<T>`
  are TypeBuilder instantiations whose `GetMethod` throws). A `GenM` helper re-anchors those members via
  `TypeBuilder.GetMethod`, and `EmitClrCall` now substitutes the open return type (`TaskAwaiter`1<!0>`) from the BIR
  `ret` hint so the await temp is typed correctly. Works through both a `suspend fun` and a `runBlocking { … }` lambda.
- **Parameter names** weren't emitted into the IL (ilemit defined methods by type only), so cross-assembly callers
  couldn't use named arguments. ilemit now writes them via `DefineParameter` (the names were always in the BIR).
- **Forward `ProjectReference`/`PackageReference` under the IL backend** — the dev-path
  `msbuild/KotlinClr.targets` never passed copy-local references to `ilemit`, so a
  `.ktproj` consuming a referenced non-BCL .NET type (e.g. a C# project's `Theme.Palette`,
  `Ext.Widget`) crashed at emit on the default IL backend (`ktproj-extlib` was broken).
  ilemit now receives `@(ReferenceCopyLocalPaths)` as `--ref`, matching the packaged SDK.
- **`ProduceReferenceAssembly` for `.ktproj`** — the SDK built its `obj/ref` reference
  assembly from our placeholder `.cs` (which holds no Kotlin types), so a downstream C#
  `<ProjectReference>` bound the empty ref assembly (CS0246). Disabled for `.ktproj` so
  consumers reference the real, retargeted output.

### Added (round-trip interop — consume a DotKt assembly AS KOTLIN)
All identified round-trip gaps resolved; guarded by `scripts/verify-roundtrip.sh` (roundtrip-pkg), each kept verify-il green.
- **Properties** (`val`/`var`/custom getters) — facadegen surfaces public instance fields and non-special `get_`/`set_`
  methods as Kotlin `prop`s; ilemit's `clrPropGet/Set` falls back to a field then a `get_`/`set_` method. This also makes
  **data classes** consumable (property access + already-round-tripping `componentN` operators + `equals`/`toString`).
- **Asymmetric visibility** (`val`, `var ... private set`) — a not-publicly-settable property's backing field is stamped
  `[KotlinReadOnly]`; the consumer restores it read-only (rejecting external writes). Fixes `val x` being exposed writable.
- **Extension functions, extension properties & top-level extension operators** — an extension's `__self` receiver is
  marked and restored as an extension receiver; `operator fun Vec.plus` is usable as `a + b`; `val T.p` round-trips as an
  extension property (BirEmitter emits its `get_/set_(__self)` statics; the backend routes `x.p` to them). Also fixed
  `isBuiltin` defaulting top-level functions to "builtin", which had lowered a restored `Vec + Vec` to a primitive `bin`.
- **vararg** — ilemit stamps `[ParamArray]`, facadegen encodes `vararg:<elem>`, the injector restores `isVararg`; `f(1,2,3)`
  and empty `f()` both work.
- **Default arguments** (constant, trailing) — restored @JvmOverloads-style (one overload per trailing default omitted);
  ilemit stamps `[DefaultParameterValue]` so the omitted args are filled at the call site.
- **Nullable types** — a `[KotlinNullable]` bitmask carries the signature's nullability; the consumer restores `T?`
  (type-level: passing null to a non-null parameter is rejected).
- Named-argument calls also work (ilemit emits parameter names). New metadata attributes: `[KotlinNullable]`, `[KotlinReadOnly]`.
  Remaining known limits (not round-trip blockers): object singletons — see docs/future-work-interop.md §5.
- **Default arguments — omit ANYWHERE (named-middle, reordered), on functions AND constructors.** Previously a restored
  default arg was @JvmOverloads-style (one positional overload per *trailing* default omitted), so a **named middle
  omission** — skip a middle default but provide a later one (`box(1, c = 9)`, `greet("C", punct = "?")`, `Pt(y = 4)`) —
  matched no overload and failed. The restored param now carries a **real constant default**: facadegen encodes the
  value in the metadata token (`opt:Int=2`, spaces escaped), and the injector builds a `FirLiteralExpression` and
  `replaceDefaultValue`s it (fir2ir then inlines the constant for any omitted arg, which `filledArgExprs` fills at the
  call site). Constructor parameter **names** are now emitted too (`DefineParamNames` for ctors), so named-arg ctor calls
  work. A .NET BCL method with a non-constant default (an enum/struct, e.g. `NumberStyles = 7`) keeps the @JvmOverloads
  trailing-overload fallback — the two strategies can't mix on one function (a bare `hasDefaultValue` flag with no literal
  crashes fir2ir). Guarded by `scripts/verify-roundtrip.sh` (roundtrip-defargs).
- **Generic round-trip** — user generics now consume from another `.ktproj` as Kotlin in **every position** and
  **combined with every other restored feature**: a generic user **class** (`class Box<T>`, with `operator`/`infix`
  members and a generic method `fun <R> mapTo(f)`), **two type parameters** (`Holder<A, B>`), generic user types in
  **return** and **parameter** position (`fun <T> wrap(x: T): Box<T>`, `fun <T> unwrap(b: Box<T>): T`), generic
  **extension** functions and **extension operators** on a generic type (`fun <T> Box<T>.twice()`), generic **top-level
  `suspend`** (`echoAsync`), and generics combined with **nullable** / **default-arg** / **vararg**. (Reified generics
  already worked — a generic method with no carried type.) The coordinated fixes:
  - **facadegen** — a root-namespace generic type's open .NET name was `.Box` (a leading dot: `Type.Namespace` is null at
    the root); now `OpenName` omits it. `Supported`/`CrossType` dropped a generic user type appearing in a signature
    (`Box<T>` → `Any?`), so the whole function silently vanished from the metadata; both now keep it (`generic:Box:T`).
  - **ilemit** — a generic type was emitted as `Box` without the CLR ``Box`1`` arity suffix, so a cross-assembly
    `GetType("Box`1")` missed it (same-assembly use resolves through the `_types` registry by BIR name, so it never
    surfaced); the metadata name now carries the arity, the registry key stays bare. A generic **extension** call omitted
    the `__self` receiver's shape (so overload resolution saw 0 params); it's now included. A generic fn with a
    **default arg** supplies fewer shapes than the single .NET method's params — `ResolveGenericMethod` now tolerates the
    trailing optional params and the emit path default-fills them.
  - **injector** — `coneOf` lost the method type variable nested inside a `generic:Box:T` argument (resolved `T` → `Any?`
    with a null owner, so a returned `Box<T>` became `Box<object>` and corrupted the call site); a type-variable resolver
    is now threaded through every recursion. The generic top-level path also ignored the extension receiver / `inline` /
    `infix` / `operator` / `vararg` / default-arg overloads — unified into the one path the ordinary case already used.
  - Guarded by `scripts/verify-roundtrip.sh` (roundtrip-generic). Known limitation (NOT a round-trip regression — it
    fails the same way in a single module): a `suspend` member of a generic class (`class Box<T> { suspend fun f(): T }`)
    is a separate pre-existing coroutine×generics gap, tracked in docs/future-work-interop.md.
- **Higher-order generics — a generic user type nested in a lambda parameter.** A function-type parameter whose argument
  or return is a generic user type (`fun <U,V> apply2(f: (Box<U>) -> Box<V>, …)`) now round-trips, in every position
  (top-level / member / extension / `infix` / `operator` / `inline`). Root cause: the internal metadata **type grammar
  was flat** (`func:<ret>:<args>` / `generic:<Open>:<args>`, colon/comma-delimited), so a `generic:` couldn't nest
  inside a `func:` — facadegen deliberately dropped such a lambda to `Any?`, which erased the type variable and made it
  uninferable at the call site. The grammar is now **recursive (bracketed)**: `generic:Box[V]`, `func:[ret,a,b]` — a
  compound child keeps its own commas, the injector splits at bracket depth 0, and `(Box<U>)->Box<V>` survives as
  `func:[generic:Box[V],generic:Box[U]]`. Guarded by `scripts/verify-roundtrip.sh` (roundtrip-generic-hof).
- **Member-declared extension functions** (`class C { fun T.f() }`) now round-trip — plain, `infix`, `operator`,
  `inline`+generic-method, and `protected` — consumed as Kotlin via `with(c) { x.f() }`. This also fixes a **pre-existing
  single-module bug**: a member extension's two implicit receivers (the dispatch `this` and the extension `__self`, both
  named `<this>` in IR) were name-keyed and got swapped, producing wrong results; they're now substituted by symbol
  identity, and a member-extension call dispatches on the enclosing instance with the extension receiver prepended.
  facadegen stamps `,ext`/`,inline` on the member `fun` line; the injector restores the extension receiver on the member
  path (the `fun`-line parser had also been dropping `,ext`/`,inline`). Guarded by `scripts/verify-roundtrip.sh`
  (roundtrip-memext).
- **Member-declared extension properties** (`class C { val T.p }`, `var` too) now round-trip — public + protected. A new
  `memextprop` metadata line carries the `get_p(__self)`/`set_p(__self, v)` member accessors; the injector restores a
  member property with an extension receiver, and a `x.p` read/write inside `with(c)` routes to C's `get_`/`set_` method
  with the extension receiver prepended.
- **Suspend member extensions** (`class C { suspend fun T.f() }`) — public + protected, consumed via the natural
  `with(c) { x.f() }`. Two general coroutine fixes enable it: (1) a `suspend fun`'s state machine was a top-level type
  and so threw `MethodAccessException` when its body touched a `protected`/`private` member of the owner — the SM is now
  **nested in its owner** (non-generic owners), which can reach those members; (2) a **suspending call inside an inline
  scope function** (`with(x){ f() }`, `run`/`let`/`apply`/`also`) is now **CPS-linearized through the state machine**
  instead of emitting an un-awaited `Task` (was a silent `InvalidProgram`). The scope function's receiver is bound to a
  state-machine field, `this`/`it` is substituted, and the lambda body's suspensions become real await points (handles
  nested scope functions, suspending args, and multi-statement bodies). Guarded by `scripts/verify-roundtrip.sh`
  (roundtrip-memext2). Remaining edge: a scope function used as a **sub-expression** (`c.apply{ f() }.x`) is a clean
  compile error — bind it to a `val` first.
- **Namespace projection** (`[assembly: DotKtNamespaceProjection(kotlinPrefix, dotNetPrefix)]`) — a DotKt library whose
  types live in one .NET namespace (e.g. `DotKt.Coroutines`) can be consumed under a different Kotlin package (e.g.
  `import kotlinx.coroutines.*`). The producer stamps it via `ilemit --ns-projection k=d` (SDK: a `<DotKtNamespaceProjection>`
  item); the consumer's facadegen reverse-projects each import to the real .NET type and the FIR injector forward-projects
  the .NET namespace to the Kotlin package, so types resolve under the imported package while the backend calls the real
  type. Prefix-based (sub-packages follow). The import scanner no longer drops `kotlinx.*` (external libs, not stdlib);
  only `kotlin.*` is filtered. Verified by `scripts/verify-roundtrip.sh` (roundtrip-nsproj).

### Removed
- **C# backend regression suite (`scripts/verify-all.sh`)** — the C# backend was retired
  in 0.x (2026-06-18); regression-testing a backend we no longer ship has no value, and the
  harness had rotted (the generated C#/façade path no longer compiles). The valuable
  MSBuild/.ktproj end-to-end coverage it carried moved to the new **`scripts/verify-ktproj.sh`**,
  which runs those samples on the shipping **IL backend** (and adds `ktproj-bidir`). CI runs
  `verify-il` + `verify-differential` + `verify-ktproj`.

## 0.9.2 — 2026-06-23

Interop/primitive bug fixes, most surfaced building a real WinUI app from Kotlin.

### Fixed
- **Signed `Byte` / `Short`** as parameters, locals, fields, and constant args threw
  `InvalidProgramException` (or crashed ilemit). They were omitted from the primitive
  paths (Int/Long/unsigned were present): `birType` fell to the user-type fallback
  `@Byte`/`@Short`, and ilemit `EmitConst` had no `byte`/`short` case so a `const byte`
  pushed `null`. Kotlin `Byte` = signed `sbyte`, `Short` = `Int16` (UByte stays
  unsigned). Fixes `MemoryStream().WriteByte(65)` too. (`il-bytearg`)
- **Lambda passed to a .NET constructor's delegate parameter** (`new Thread({ … })`)
  crashed ilemit with a `NullReferenceException` (`EmitClrNew`): the façade erases the
  delegate param, so the exact-type ctor lookup found nothing. `EmitClrNew` now selects
  the ctor by arity (preferring delegate-param/lambda-arity matches) and builds the
  specific delegate. (`il-delegatearg`)
- **`for (x in <.NET IEnumerable<T>>)`** over a raw .NET enumerable (not a Kotlin
  collection) failed to compile: `iterator()` was ambiguous (only the clashing stdlib
  extension `iterator()`s applied). facadegen now injects a frontend-only
  `operator fun iterator(): Iterator<T>` for any type implementing `IEnumerable<T>`;
  the backend bypasses it and enumerates via GetEnumerator/MoveNext/Current
  (forEachInline). (`il-netenum`)
- **User class implementing Kotlin `Iterable<T>`** (`class R : Iterable<T>`) crashed
  ilemit (`KeyNotFoundException 'Iterable'`): `Iterator<T>` had a monomorphized
  synthetic interface but `Iterable<T>` did not. Added `KIterable_<elem>`
  (`operator fun iterator(): KIterator_<elem>`), parallel to the existing
  `KIterator_<elem>`; both the `for` loop and explicit `.iterator()` now work. (`il-iterable`)
- **User class implementing/extending a .NET-mapped Kotlin stdlib supertype** crashed
  ilemit (`KeyNotFound`) — the supertype emission didn't route these through their
  .NET mapping. A whole cluster:
  - **Custom exceptions** `class E(msg) : Exception(msg)` / `RuntimeException` -> a CLR
    class `: System.Exception` (ctor chains to `System.Exception(string)`, `.message`/
    `.cause` -> `.Message`/`.InnerException`, catchable by base type). (`il-customexc`)
  - **`Comparator<T>`** -> `IComparer<T>` (`compare` -> `Compare`). (`il-comparator`)
  - **`AutoCloseable`/`Closeable`** -> `IDisposable` (`close` -> `Dispose`).
  Mechanism: supertype base/interface emission now routes through `birType` when it
  maps to a `clr:`/`clrg:` spec; `clrIfaceMemberName` renames the overridden members;
  the `catch` clause types via `birType` (a user exception catches as its own type, not
  `object`); `MapType` resolves bare .NET FQNs. (Comparable<T> as a self-referential
  generic supertype is now handled too — see below.)
- **`use {}`** (Closeable/AutoCloseable) now lowers to `try { block(it) } finally { close()/Dispose() }`
  returning the block value — the CLR analogue of C# `using`. (`il-use`)
- **`Comparable<T>`** (`class V : Comparable<V>`) — the self-referential generic interface
  `IComparable<V>` (V the emitted type) made ilemit call `.GetMethods()` on a
  TypeBuilderInstantiation (throws). Interface-impl linking now enumerates the OPEN
  generic definition and re-anchors each method via `TypeBuilder.GetMethod` (same
  pattern as the self-ref base ctor). `<`/`>`/`<=`/`compareTo`/`sorted()` all work. (`il-comparable`)
- **`class S : CharSequence`** -> a synthetic `<>dotkt_CharSequence` interface (length
  getter + get(i) operator + subSequence); no faithful BCL equivalent exists. (`il-charseq`)
- **`String.substring(start, end)`** used .NET `Substring(start, LENGTH)` directly, but
  Kotlin's `end` is an EXCLUSIVE INDEX -> the 2-arg form now converts `end -> end - start`
  (`"hello".substring(1,4)` = "ell", was "ello"). (`il-substr`)
- **Type-injector metadata** (façade generation), found building a WinUI-on-Kotlin library:
  - Assignability edge no longer dropped for a non-constructible base (WinRT `UIElement`,
    `SafeHandle`): the supertype edge is emitted for is-a regardless of a base no-arg ctor;
    a `basector none` marker suppresses the synthesized `: super()` only. (`il-injbase`)
  - Member signature types now use the FULLY-QUALIFIED name, so a same-simple-name type from
    another namespace (`Microsoft.UI.Xaml.LaunchActivatedEventArgs` vs the UWP one) no longer
    shadows the right one — fixes overrides that "override nothing". (`il-injfqn`)
  - Public **static members of a normal class** (one with instance members too) are now
    injected — they were dropped, so `Application.Start(cb)` / `Application.Current` were
    unresolved. Surfaced on a synthesized companion: facadegen emits `sfun`/`sprop`, the
    injector generates the companion, the backend emits .NET static calls (lambda args bind
    to the .NET delegate). Accessed via `App.Companion.Start(cb)` / `App.Companion.Current`
    (`il-injstatic`). NOTE: the bare `App.Start` form is NOT supported — the current
    compiler doesn't resolve the implicit companion of a plugin-generated class, so the
    `.Companion` qualifier is required (accepted rule).
  - A .NET **FIELD surfaced as a Kotlin property** (facadegen records static/const fields
    and public instance fields as `sprop`) crashed ilemit with a `NullReferenceException`
    (later a 0xC0000005 access-violation via MSBuild) — `clrPropGet`/`clrPropSet` only looked
    up a property accessor. They now fall back to `ldfld`/`ldsfld` / `stfld`/`stsfld` — and a `const`/literal field is
    INLINED (its value pushed, as C# does, since a literal has no storage and can't be
    `ldsfld`'d) — otherwise an actionable "no property OR field" error. Verified via
    `il-injstatic` (`App.Companion.Answer`=99 static readonly; `App.Companion.Magic`=123 const).
  - `ilemit` gained an `ILEMIT_TRACE` env switch that prints each emission step (ref load,
    parents, signatures, bodies, createType, save) flushed to stderr — so a Reflection.Emit
    hard-crash (uncatchable AV, exit 0xC0000005) can be localized to the culprit type/method.
- **Per-file lifted state leaked across files (multi-file)** — one `BirEmitter` instance
  processes every file, but its per-file lifted collections (`liftedMethods`/`liftedTypes`/
  synthesized delegate classes/ref cells/iterator+property+CharSequence+KProperty synthetics)
  were never reset, so each file's BIR ACCUMULATED the prior files' lifted lambdas/types —
  duplicating e.g. `App.kt`'s `__lambda*` into ControlsKt/DslKt/LayoutKt/ReactiveKt. The
  `<>dotkt_*` types are de-duplicated by ilemit, but lifted `__lambdaN` are file-class methods
  that are not, so this was real metadata bloat (and a corruption hazard surfaced building a
  multi-file WinUI app). `emitFile` now resets all per-file lifted state up front. (`il-mflambda`)
- **Overloaded user functions resolved to the wrong method** — ilemit keyed methods by NAME
  only, so `f(String)` and `f(() -> String)` collided in one dictionary: the last-declared
  overwrote, a body was emitted into the wrong overload's `MethodBuilder`, and calls picked
  the wrong target. Manifested as a WinUI crash — the DSL's `text(String)` / `text(() -> String)`
  caused `text(() -> String)` to run `tb.Text = <the Func itself>` (the String overload's body),
  so CsWinRT marshaled a `Func` object as a string (`WindowsCreateStringReference` AV / OOM).
  ilemit now keys methods by name + parameter-type signature (`MethodsBySig`); BirEmitter emits
  that signature on each call (callStatic/callInstance, incl. extension and companion calls) so
  body emission AND call resolution pick the right overload. Covers top-level and member
  overloads, by arity and by parameter type. (`il-overload`)
- **Expression-body function with a Unit-typed body dropped the call** — `IrReturn(<expr>)`
  emitted a bare `{"k":"return"}` when the value's type was `Unit`, discarding the
  expression. So `fun main() = winUiApp { … }` (and `fun f() = sideEffect()`, or an explicit
  `return doCleanup()`) launched/ran NOTHING. A Unit-typed return value is now EVALUATED
  (`exprStmt`) before the bare return; only a plain Unit reference (`return`/`return Unit`)
  stays a bare return. (`il-exprbody`)
- **Unsigned .NET parameter types weren't mapped to Kotlin unsigned types** — facadegen's
  primitive map had `System.Int32→Int` etc. but no `System.UInt32`/`UInt64`/`UInt16`, so a
  `uint` parameter surfaced as the bare name `UInt32`, which doesn't unify with `kotlin.UInt`
  ("argument type mismatch: actual 'UInt', expected 'UInt32'") — hit calling WinUI's
  `Bootstrap.Initialize(uint majorMinorVersion)`. Added `UInt32→UInt`, `UInt64→ULong`,
  `UInt16→UShort`, `SByte→Byte`. (`il-injuint`)
- **Synthetic type names collided across files in a multi-file assembly** — every file's
  `BirEmitter` used a fresh counter, so `<>dotkt_Closure0…`, `<>dotkt_Ref_<elem>`, and
  `<>dotkt_Seq…` repeated across files. Linking all BIR into one assembly overwrote them in
  ilemit's `_types`, orphaning a `TypeBuilder` that was never `CreateType()`'d →
  `NotSupportedException` ("not supported before the type is created") at `Save`, or a
  `0xC0000005` via MSBuild. (Single-file samples never hit it.) BirEmitter now prefixes these
  per-file-DISTINCT synthetics with the file class (`<>dotkt_<FileKt>_Closure0`); ilemit
  de-dups per-file-IDENTICAL shared synthetics (`<>dotkt_Result`/`KProperty`/`KIterator_*`/…)
  by name; and `Ordered()`/a pre-Save sweep make every defined TypeBuilder get created.
  (`il-mfclosure` — two files, capturing closures + ref cells.) Found building a WinUI app
  whose `.ktproj` source-includes the whole library.

## 0.9.1 — 2026-06-23

Language/stdlib long-tail completion + a type-emission correctness refactor. The
direct-IL backend, coroutine surface, generics, and forward interop were already
complete in 0.9.0; this release closes the remaining A (language) / B (stdlib) gaps
so the A/B checklists in `docs/remaining-tasks.md` have **zero** open items.

### Added
- **Regex `matches` / `find`** — full-input match + `MatchResult?` (via `DotKt.Text.Regexes`
  shims), `MatchResult.value` → `Match.Value`. (`il-regex`)
- **`return` as an expression** — `val x = if (c) a else return b` (new `returnExpr`
  lowering, `tryStack`-aware). (`il-langtail`)
- **enum per-entry bodies** — `enum class Op { PLUS { override fun apply(…)=… }; abstract
  fun apply(…) }`: the base enum becomes abstract and each body entry is emitted as a
  subclass `<>Enum_NAME : Enum`. (`il-enumbody`)
- **Field-level visibility** — a property's visibility is honored on its backing field:
  `private` → true `FieldAttributes.Private`, `internal` → `Assembly`, `protected` →
  `FamORAssem`. (`il-fieldvis`)

### Changed
- **Inner / nested classes are now emitted as true CLR nested types** (`Outer+Inner`)
  instead of being flattened to separate top-level types. Nested types retain Kotlin's
  legal access to the enclosing type's `private` members, which is what makes true
  `private` field visibility correct. `inner` classes still capture `__outer`.

### Fixed
- **Compound-condition smart-cast** — `if (x is Int && x > 10)` no longer mis-takes the
  then-branch (the `>` operand stayed boxed as `Any`); `bin` now coerces a boxed operand
  to the other operand's primitive type, and `IrGetValue` honors a narrowed smart-cast.

### Notes
- Verified working & locked by samples this release: `lateinit` (uninitialized read
  throws), `field` in custom accessors, `when`+type smart-cast.
- Full IL suite green + JVM differential ALL MATCH + ilverify-clean.
- Known residue (unchanged, tracked in `docs/remaining-tasks.md` §F / §R): packaged-SDK
  end-to-end consumption still has MSBuild SDK-resolution plumbing to finish (F-308);
  reverse-interop cosmetic naming/`[Nullable]` is gated behind R-1.

## 0.9.0

Initial pre-1.0 line: direct-IL backend (C# codegen retired), CLR-native coroutines
(`suspend` ⇔ `Task<T>` / `IAsyncEnumerable`), user generics, forward .NET interop
(import-driven, façade-free), and the 3-package distribution (Sdk / Toolchain / Runtime
+ Templates).
