# CLR stdlib `@ClrIntrinsic` binding audit

Status: **verification (2026-06-30, 6 parallel sub-agent audits)**. Scope: all **490** `TODO("clr binding")` / `TODO("@Clr…")` stubs across **47** files under `runtime/stdlib/clr/`. This document is the verification result before the implementation pass; it records, for every stub, how it must be bound.

## Binding model (the three rules)

Source of truth: `docs/design-clr-collection-binding.md` and `BirEmitter.kt` "Rule 3" (`:1232`, `:3564`).

1. **`@kotlin.clr.ClrIntrinsic("Namespace.Type")` on a class** → the Kotlin type *is* that BCL type; construction is substituted; the class body is not emitted.
2. **`@kotlin.clr.ClrIntrinsic("Member")` on a member** → the call is substituted to that BCL member (overload resolved by receiver BCL type + arg types). The body stays `TODO()` — pure metadata, never emitted or run.
3. **A member of a `@ClrIntrinsic` class with NO own `@ClrIntrinsic` but WITH a body** → hoisted to a static helper `<>dotkt_ClrH_<Class>`; its body **must be real Kotlin** (using its `@ClrIntrinsic`'d siblings). A `TODO()` here **throws** — this is the core defect class the user flagged ("TODO は未実装マーカーとして使えません").

> **Layer note:** the `@ClrIntrinsic`→BCL substitution is **consumed at bir2cir** (sourced from the ref dll, `DotKt.Private.Stdlib.dll`); its present location in `BirEmitter.kt` (`:1232`, `:3564`) is the interim/defect tracked by ship-tasks.md §3.

## Categories

- **INTRINSIC `"X"`** — 1:1 BCL member; add/keep `@ClrIntrinsic("X")`, body stays `TODO` (metadata).
- **BODY** — no 1:1 BCL member; replace `TODO()` with real Kotlin (Rule-3 hoist, or a plain top-level/extension body).
- **DEAD** — never reached because the compiler intrinsically lowers it; the `TODO` is harmless. Reserved for **genuine primitive IL ops** (Int/Long/Double arithmetic/compare/convert; array `ldelem`/`stelem`/`ldlen`/`newarr`; Boolean `!`/`&`/`|`/`^`; Object virtuals).
- **BLOCKED** — needs a compiler-mechanism extension before it can bind (see next section). Per user direction these are *not* permanent — most are mechanism work.

### Policy (user direction, 2026-06-30)

- **Prefer INTRINSIC over compiler-lowering.** Major compiler lowerings are to be retired; a named function with a clean BCL equivalent (e.g. `String.format`→`System.String.Format`, `kotlin.math.sqrt`→`System.Math.Sqrt`) should be `@ClrIntrinsic`, not a compiler special-case. `MathClr.kt` is already `@ClrIntrinsic`-annotated under the live math-map, so retiring the map needs no stdlib change. See [[intrinsic-over-compiler-lowering]].
- **All CLR type arguments are reified.** `reified` is a JVM erasure workaround, moot on CLR; generic `newarr !T` is emittable, so generic `Array<T>` allocation is **not** a blocker. See [[clr-all-type-args-reified]].
- **byref is trackable through BIR/CIR/IL** (`byrefOf`/`byrefLoad`/`byrefStore` already exist); a `@ClrRefArguments(mask)` marker (bitmask, bit position = arg position) unblocks `Interlocked`. Not a blocker. (The normal interop byref is `kotlin.clr.byref`/`ClrRef<T>`; `@ClrRefArguments` is the escape hatch for `@ClrIntrinsic` stdlib fns that can't carry `ClrRef<T>` — see docs/design-compiler-modes.md §3.5.)

## Cross-cutting compiler-mechanism work (turns BLOCKED → bindable)

These are tracked as task #5. None is a true dead-end.

1. **`@ClrRefArguments(mask)` byref args** — a bitmask (bit position = arg position) on a `@ClrIntrinsic` member; emit the masked arguments as managed pointers (reuse `byrefOf`/`byrefLoad`/`byrefStore`). Escape hatch for `@ClrIntrinsic` stdlib fns that can't carry `ClrRef<T>` in their signature (the normal byref is `kotlin.clr.byref`). Unblocks `Atomics` → lock-free `System.Threading.Interlocked.CompareExchange/Add/Exchange` (currently uses a `Monitor` lock — works, but the Interlocked rebind is the correct CLR idiom).
2. **instance-member-through-static-property** — e.g. `System.Text.Encoding.UTF8.GetString(...)`. Unblocks `String.decodeToString`/`encodeToByteArray`.
3. **generic `newarr !T`** — drop the non-reified `Array<T>(n){init}` refusal (`BirEmitterExpressions.kt:171-174`) and emit `newarr !T`/`!!T`. Unblocks generic `Array<T>.copyOf/copyOfRange/plus`.
4. **top-level reified `enumValues`/`enumValueOf` interception** — wire call-site interception to `System.Enum.GetValues(typeof(T))` / `Enum.Parse` (the enum-class path exists at `BirEmitter.kt:3460`; top-level reified entries are not yet wired, so they throw).
5. **member `String.compareTo`/`subSequence` lowering** — `compareTo`→`System.String.CompareOrdinal` (Kotlin ordinal vs .NET culture), `subSequence`→`Substring(start, end-start)`; currently no call-site interception → correctness gap.
6. **`ulongToString` decimal/radix** — needs unsigned 64-bit formatting (`System.UInt64.ToString` reinterpret, or an intrinsic); the signed `Convert.ToString` path is wrong.

### Annotation bugs to fix (already-annotated stubs that are wrong)

- `MathClr.sign(Double)`/`sign(Float)` — annotated `@ClrIntrinsic("System.Math.Sign")` but `Math.Sign` returns **Int** and **throws on NaN**; must be **BODY**.
- `NumbersClr.Double.Companion.fromBits`/`Float.Companion.fromBits` — `clrName('.')` prepends the companion receiver → emits a 2-arg call; the companion receiver must be dropped for the static BCL call.
- `RegexClr.pattern` — annotated `"Pattern"`, but `System...Regex` has no `Pattern` property → use `"ToString"`.
- `RegexClr.matches` — annotated `"IsMatch"`, but Kotlin `matches` = full match while `IsMatch` = partial → **BODY** (anchored).
- `CharClr.uppercase()`/`lowercase()` — annotated to `ToUpper/LowerInvariant` (return `char`) but declared `String` → **BODY** (`this.toString().uppercase()`).
- `StringBuilderClr.length` — annotated bare `"Length"` vs the `get_*`/`set_*` accessor-name convention used elsewhere; confirm the resolver accepts bare property names or change to `get_Length`.

## Aggregate counts (approximate)

| Area | INTRINSIC | BODY | DEAD | BLOCKED |
|---|---|---|---|---|
| text-core (StringBuilder/Strings/StringNumberConversions/_Strings) | ~38 | ~30 | 0 | 4 (UTF-8 enc) |
| char/regex (Char/CharClr/CharCategory/CharCode/Regex/RegexExt) | ~13 | ~22 | 18 (builtins/Char) | 0 |
| math/numbers (Math/Numbers/Unsigned/Number) | ~24 | ~22 | ~70 (math-map + uint div/rem) | 2 (ulongToString) |
| collections (TypeAliases/Maps/Collections/Sets/MutableColl/Arrays/Grouping) | ~22 | ~45 | 0 | 4 (keys×2, set-iter×2, arrayOfNulls(ref)) |
| builtins/arrays (Arrays/Array/_Arrays/_UArrays/Library/Boolean/String/Enum/Any/Atomics/ArrayIntrinsics) | ~18 (already ok) | ~10 | ~55 | 5 (generic Array<T> alloc) |
| misc/runtime (coroutine/Console/time/Exceptions/Uuid/Random/seq/…) | 5 | 21 | 1 | 6 (coroutine CPS) |

### Block investigation (2026-06-30) — verified against `bir2cir`/`ilemit`/`BirEmitter`, not assumed

Every audit "BLOCKED" was re-checked against the real compiler code (3 sub-agents). **Result: zero permanent blocks.**

| Audit "BLOCKED" | Verdict | Fix location |
|---|---|---|
| UTF-8 `decodeToString`/`encodeToByteArray` | BINDABLE | **stdlib-only** — a `@ClrIntrinsic("System.Text.UTF8Encoding")` class with ctor + `GetString`/`GetBytes` instance methods (the proven Regex pattern). The `Encoding.UTF8.GetString` *single-intrinsic* form is the only truly-blocked path (`ResolveType` can't resolve `…UTF8`), and it's unnecessary. |
| `arrayOfNulls(reference, size)` | BINDABLE | **stdlib-only** — `reference` is a JVM erasure artifact; `arrayOfNulls<T>(size)` already lowers to generic `newarr` (proven by reified `toTypedArray`). Body: `arrayOfNulls<T>(size) as Array<T>`. |
| `ulongToString`/`uintToString` | BINDABLE | **stdlib-only** — `value.toULong().toString()` (the audit's `@ClrIntrinsic("System.UInt64.ToString")` direct-bind does NOT work: a top-level normal-param annotation becomes a *static* call → no `UInt64.ToString(Int64)`). Uses the existing `objMethod` box→`UInt64.ToString` path. |
| `uint`/`ulong` `/` `%` `<` `>` | FIXABLE (**+ real bug**) | **ilemit** — `EmitBin` (Program.cs:1667-1680) emits unconditional *signed* `Div`/`Rem`/`Clt`/`Cgt`; the operand CLR type already carries unsignedness, so add `Div_Un`/`Rem_Un`/`Clt_Un`/`Cgt_Un` when unsigned (~6 lines; layering-correct — reads CIR type, no Kotlin knowledge). `a/b` on `UInt`≥2³¹ is currently wrong. |
| collections `keys`/`entries`/mutable-`iterator()` | not a block | **stdlib BODY** views (a `MutableSet` over the dictionary, a snapshot iterator). |
| `Continuation.intercepted()` | BINDABLE now | **stdlib-only** — `context[ContinuationInterceptor]?.interceptContinuation(this) ?: this` (mislabeled; `return this` when no interceptor is correct spec). |
| `startCoroutineUninterceptedOrReturn`×3, `createCoroutineUnintercepted`×2 | approx now / faithful = 1 compiler add | **runtime helper / deferred-kickoff** now (sequential, context-independent uses); full dispatcher/`coroutineContext` fidelity needs a suspend-lambda `create(completion): Continuation<Unit>` factory — **not impossible by design**. The audit's "suspend CPS ABI absent" was inaccurate; the CPS/state-machine infra exists. |

Net new *compiler* work strictly required: the unsigned `Div_Un` ilemit fix (also a correctness bug). Optional: the coroutine `create(completion)` factory for full async fidelity. Everything else is stdlib-side. Details: scratchpad `investigate-*.md`.

---

## Per-area binding decisions

> Detailed per-stub decisions live in the six sub-agent reports (scratchpad `audit-*.md`); the actionable summary per file follows.

### text-core

**StringBuilderClr.kt** (class `@ClrIntrinsic("System.Text.StringBuilder")`): append/insert(Char/Any/Boolean/Int/Long/Float/Double/CharArray/String/Byte/Short), get→`get_Chars`, set→`set_Chars`, length→`Length`, capacity→`get_Capacity`, ensureCapacity→`EnsureCapacity`, setLength→`set_Length`, clear→`Clear`, nativeRemove→`Remove`, nativeSubstring→`ToString(int,int)` are **INTRINSIC**. BODY: `append(CharSequence?)`(→`append((v?:"null").toString())`), `append(CharSequence?,s,e)`, `reverse()`(swap-loop via get/set), `indexOf`/`lastIndexOf`(→`toString().indexOf(...)`), `trimToSize`, `setRange`, `toCharArray`, `appendRange`×2, `insertRange`×2 (via small `private @ClrIntrinsic` count-adapter wrappers), `appendLine(Int/Short/Byte/Long/Float/Double)`(→`append(v).appendLine()`).

**StringsClr.kt**: INTRINSIC: `nativeIndexOf(char,int)`, `nativeLastIndexOf(char,int)`, `toUpperCase`→`ToUpper`, `uppercase`→`ToUpperInvariant`, `toLowerCase`→`ToLower`, `lowercase`→`ToLowerInvariant`, `toCharArray()`/`nativeToCharArray`→`ToCharArray`, `substring`/`nativeSubstring`→`Substring`. BODY (several need a `private @ClrIntrinsic` ordinal wrapper — .NET string `IndexOf`/`Replace` are culture-sensitive, Kotlin wants ordinal): `nativeIndexOf(str,from)`, `nativeLastIndexOf(str,from)`, `replace`×2, `replaceFirst`×2, `String(CharArray[,off,len])`(→`concatToString`), `compareTo(other,ignoreCase)`, `contentEquals`×2, `regionMatches`×2, `CASE_INSENSITIVE_ORDER`. BLOCKED (mechanism #2): `decodeToString`×2, `encodeToByteArray`×2.

**StringNumberConversionsClr.kt**: `toByte/toShort/toInt/toLong/toFloat/toDouble`→`System.{SByte,Int16,Int32,Int64,Single,Double}.Parse` **INTRINSIC**; `toBoolean()`→**BODY**. **_StringsClr.kt**: `CharSequence.elementAt`→**BODY** (`get(index)`).

### char/regex

**builtins/Char.kt** — all 18 **DEAD** (primitive `System.Char`). **CharClr.kt**: INTRINSIC `isLetter/isLetterOrDigit/isDigit/isISOControl(IsControl)/isWhitespace/isUpperCase(IsUpper)/isLowerCase(IsLower)/uppercaseChar/lowercaseChar/toUpperCase/toLowerCase(ToUpper/LowerInvariant)/titlecaseChar(approx)/isHighSurrogate/isLowSurrogate`; BODY `category/isDefined/uppercase()/lowercase()(annotation bug)/isTitleCase/digitOf/checkRadix`. **CharCategoryClr.kt** `contains`→BODY; **CharCodeClr.kt** `Char(UShort)`→BODY. **RegexClr.kt** (class `@ClrIntrinsic("System.Text.RegularExpressions.Regex")`): INTRINSIC `containsMatchIn`→`IsMatch`, `replace(String)`→`Replace`, `toString`→`ToString`, `escape`→`Escape`; BODY (need a **`MatchResult`/`MatchGroupCollection` adapter over `System...Match`** — a missing prerequisite to author): `pattern`(fix→`ToString`), `options`, `matches`(fix→anchored), `find/findAll/matchEntire/matchAt/matchesAt`, `replace(transform)/replaceFirst`, `split/splitToSequence`, `fromLiteral/escapeReplacement`. **RegexExtensionsClr.kt** `MatchGroupCollection.get(name)`→BODY (adapter).

### math/numbers

**MathClr.kt** — DEAD via math-map (already `@ClrIntrinsic`, become live on retirement): all transcendental/rounding/`abs/min/max/pow/withSign` (Double/Float/Int/Long). Reached: `sign`×2→**BODY** (fix mis-binding), `nextUp/nextDown`→INTRINSIC `BitIncrement/BitDecrement`, `ulp/nextTowards`→BODY. **NumbersClr.kt** — INTRINSIC `isNaN/isInfinite/isFinite`, `toRawBits`→`BitConverter.*ToInt*Bits`, `count{One,LeadingZero,TrailingZero}Bits`→`BitOperations.*`, `rotateLeft/rotateRight`→`BitOperations.*` (uint coercion caveats); BODY `toBits`×2 (NaN-canon), `take{Highest,Lowest}OneBit`×4; FIX `Companion.fromBits`×2 (receiver-prepend). **Number.kt** `toChar`→BODY. **UnsignedClr.kt** — DEAD `uintDivide/Remainder`,`ulongDivide/Remainder` (latent **signed-Div/Rem** bug, separate issue); BODY all compares/conversions/`uintToString`×2; BLOCKED `ulongToString`×2 (mechanism #6).

### collections

**TypeAliasesClr.kt** — `ArrayList`(→`List`): INTRINSIC `trimToSize(TrimExcess)/size(Count)/contains/get(get_Item)/indexOf/lastIndexOf/add(Add,void→synth true)/remove/clear/add(index)(Insert)` + add `ensureCapacity(EnsureCapacity)`; BODY `containsAll/addAll×2/removeAll/retainAll/iterator/listIterator×2/subList` and the **return-the-old-element** wrappers `set`/`removeAt` (private `native*` `@ClrIntrinsic` + bodied wrapper). `HashMap`/`LinkedHashMap`(→`Dictionary`): INTRINSIC `size(Count)/containsKey/containsValue/nativeGet(get_Item)/nativeSet(set_Item)/nativeRemove(Remove)/clear/values(Values)`; BODY `putAll/entries`; BLOCKED `keys` (KeyCollection≠ISet, mechanism/BODY view). `HashSet`/`LinkedHashSet`(→`HashSet`): INTRINSIC `size/contains/add(clean bool)/remove/clear`; BODY `containsAll/addAll/removeAll/retainAll`; BLOCKED mutable `iterator()`. (Semantic caveat: `LinkedHash*` on plain `Dictionary`/`HashSet` lose insertion order.) **MapsClr/CollectionsClr/SetsClr/MutableCollectionsClr/GroupingClr** — all **BODY** (pure factories/builders/helpers; templates in `AbstractMutable*Clr.kt`/`ClrCollectionDefaults.kt`). **ArraysClr.kt** `orEmpty`→BODY; `arrayOfNulls(reference,size)`→BLOCKED (reflective same-element-type alloc).

### builtins/arrays

**Arrays.kt/Array.kt** get/set/size→DEAD; `iterator()`→BODY (explicit-call only). **_ArraysClr.kt** generic `Array<T>.copyOf/copyOfRange/plus`×3→BLOCKED→BODY via mechanism #3; `plusElement`→BODY; `nativeClone/nativeFill/sort×7/nativeSort×8`→INTRINSIC (already correct). **_UArraysClr.kt** `U*Array.asList`×4→BODY. **Library.kt** `String?.plus`→BODY (null→"null"); array factories `arrayOf/arrayOfNulls/*ArrayOf`→DEAD; `enumValues/enumValueOf`→mechanism #4. **Boolean.kt/Enum.kt/Any.kt** all DEAD; **String.kt** members DEAD except `compareTo/subSequence` (mechanism #5). **Atomics.kt** `monitorEnter/Exit`→INTRINSIC (correct); Atomic* methods→Interlocked rebind via mechanism #1. **ArrayIntrinsicsClr.kt** `emptyArray`→BODY (`arrayOfNulls<T>(0) as Array<T>`).

### misc/runtime

**IntrinsicsClr.kt** — 6 coroutine CPS intrinsics → **BLOCKED** (permanent; Task ABI). **ConsoleClr.kt** print/println→INTRINSIC (done); `readlnOrNull`→INTRINSIC `Console.ReadLine` (add annotation); `readln`→BODY. **CancellationExceptionClr/MonoTimeSourceClr/InstantClr/DurationClr/ExceptionsClr/UuidClr/PlatformRandomClr/serializationUtilClr/AutoCloseableClr/UnitClr/SequencesClr/KClassesImplClr** — BODY (each needs only a tiny `@Clr` primitive: `Stopwatch.GetTimestamp`, `DateTimeOffset.UtcNow`, `RandomNumberGenerator`, `Console.Error`, `System.Exception.ToString`) wrapped by pure Kotlin copied from JVM/JS actuals. `stackTraceToString`→INTRINSIC `ToString`. **EnumEntriesClr.kt** `enumEntriesIntrinsic`→DEAD.

---

## Implementation plan

Implemented per area by sub-agents (task #3), each producing a file diff, then a single stdlib ref/rt build + behavioral verification (task #4). Order:

1. **Pure-BODY files first** (no compiler change, low risk): misc/runtime, collections factories, Maps/Sets/Collections/Grouping, Char/Strings bodies, Numbers `take*OneBit`, Math `sign/ulp/nextTowards`, `Number.toChar`, `emptyArray`, `U*Array.asList`, `String?.plus`.
2. **INTRINSIC annotation adds/fixes** (metadata only): StringBuilder/Char/Regex/Numbers/Console intrinsics; fix the 6 annotation bugs.
3. **Rule-3 collection bodies** (ArrayList/HashMap/HashSet members; the `set`/`removeAt` old-element wrappers).
4. **Mechanism work** (task #5) then its dependent bindings: byref→Atomics/Interlocked; generic `newarr`→`Array<T>` ops; static-property→UTF-8; enum reified interception; String member lowering; the `MatchResult` adapter for Regex.

## Implementation results (2026-06-30)

Implemented stdlib-side via 6 parallel agents: **490 → 363 TODO stubs** (127 BODY/INTRINSIC applied; the 363 remaining are correct-as-TODO `@ClrIntrinsic` metadata bodies + DEAD compiler-lowered primitives + ~40 mechanism-gated items). **ref + rt + frontend jar all build clean** (0 errors). The only binding-annotation bug across the 127 was StringBuilder `length` using `"get_Length"` — must be the property name `"Length"` (ilemit derives the accessor); see [[clrintrinsic-property-name-convention]].

Behavioral: **~12/15 pass** — StringBuilder (append/length/reverse-builds/chain), String (lowercase/toInt), Char (isLetter/uppercaseChar), math (sqrt), collections (sorted/list/setOf/intArrSum), unsigned (`ULong.toString` = `18000000000000000000`, the value.toULong().toString() fix), regex, `Number.toChar`.

Remaining 4 failures are **compiler-layer, not binding errors** (task #6, belong in kotc/ilemit per the layer split):
- `sign()` — fileClass divergence: app references `kotlin.math.MathClrKt` (K2 jar, un-stripped) but the rt emits `kotlin.math.MathKt` (kotc strips "Clr"). kotc must apply the strip to *references* of non-inline `*Clr` actuals, not only emissions.
- `Unit.toString()` — clrInstance `kotlin.Unit.ToString` not resolved.
- `reverse()` — rule-3 static-helper hoist NullRef (codegen).
- `char.uppercase()` — `this.toString()` returns null inside an extension body (primitive-`toString` codegen); `'a'.toString()` works standalone.

Note: `build-clr-stdlib.sh` AND `build-clr-stdlib-runtime.sh` BOTH need `--emit` (without it they stop after BIR and `rm -rf` their dll dir). Never run `build-dotkt-stdlib.sh`.
