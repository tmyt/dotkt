# TASK #55 — Primitive `@ClrTypeAlias` unification: feasibility + staged plan

> **Status: Stages A–D DONE (2026-07-08).** The redundant `KotlinToClr` map is deleted; primitives
> now lower to their ref.dll `@ClrTypeAlias` BCL form via `AliasBcl`, and the three ilemit opcode
> switches normalize it back through `PrimShorthandName`. **Stage D landed too:** kotc's
> `clrMethodShape` .NET-name shape-matcher is DELETED — kotc emits pure-Kotlin `shapeTypes`, and
> bir2cir's new `ShapeSynthesis` pass derives the frozen `shapes` tokens off the `@ClrTypeAlias`
> index. Behavior-preserving (values + overload resolution byte-identical); full gate green. Only
> Stage E (facadegen reverse map) remains as a decoupled follow-up. Implementation notes at the end.
>
> Original READ-ONLY investigation follows: verdict, then evidence per §1–5, then the crux (is the
> shorthand token vocabulary deletable?), then the staged plan + honest cost.

## Executive verdict

**Rerouting bir2cir's primitive lowering through the already-read `@ClrTypeAlias` index is a
CONTAINED reroute, NOT a Phase-5-scale core rewrite — with ONE caveat.** The metadata the user
believes makes it "just read it" **is genuinely already read** (proven below): every rt/app build's
bir2cir alias index already contains `kotlin.Int → System.Int32`, `kotlin.Byte → System.SByte`, etc.,
scanned live from the ref.dll. `KotlinToClr` is pure redundancy with data already in `_aliases`.

The caveat is the crux question: the CLR **shorthand token vocabulary** (`"int"`/`"sbyte"`/…) is
**partly load-bearing** — but only in **exactly three name-keyed opcode-selection switches** in
ilemit (`EmitConst`, `EmitConv`, `ConstArgValue`), each a small contained switch with an existing
dual-key precedent. It is **NOT** load-bearing for type resolution, value-type detection, boxing,
arithmetic, arrays, or generic construction — those already resolve `System.Int32` identically to
`"int"`.

Bottom line: this is **not a minefield for type lowering**, but it is **also not a pure deletion** —
the shorthand vocabulary cannot be fully erased, because (a) three ilemit opcode switches key on it
and (b) bir2cir itself *synthesizes* shorthand tokens at several sites independent of `KotlinToClr`.
The realistic win is **deleting the redundant `kotlin.* → shorthand` MAP (copy #1) and the two
downstream hardcoded reverse copies (#3, #4)** while keeping the shorthand as ilemit's internal
opcode alphabet — or, for a fuller cleanup, converting the three opcode switches to resolve-then-
switch-on-`Type` so `System.Int32` flows end-to-end.

---

## The confirmed finding + the LINCHPIN (proven empirically)

The stdlib primitives carry `@kotlin.clr.ClrTypeAlias` in source
(`libraries/stdlib/clr/builtins/Primitives.kt:17,395,771,1193,1618,1967`):
`@ClrTypeAlias("System.SByte") class Byte`, `@ClrTypeAlias("System.Int32") class Int`, …

bir2cir's ref.dll scan already picks up **any** class alias
(`toolchain/bir2cir/Program.cs:1022-1023`: `var classAlias = ClrAliasOf(...); if (classAlias != null)
metadata.Aliases[ownerFqn] = classAlias;`) and folds it into the shared `_ownerAlias` /
`_aliases` index (`Program.cs:561`, exposed as `Aliases` at `Program.cs:631`, wired into type
lowering at `Program.cs:355` via `refs.Aliases`).

**LINCHPIN VERIFIED** — reflected the actual built ref.dll
(`build/clr-stdlib/dll/DotKt.Private.Stdlib.dll`) with a MetadataLoadContext probe:

```
kotlin.Int:     alias=System.Int32
kotlin.Byte:    alias=System.SByte
kotlin.Long:    alias=System.Int64
kotlin.Double:  alias=System.Double
kotlin.Char:    alias=System.Char
kotlin.Boolean: alias=System.Boolean
```

So the primitives ARE emitted as TypeDefs WITH the alias attribute, and `_aliases["kotlin.Int"]`
== `"System.Int32"` in **every non-ref build today**. The metadata is not hypothetical — it is
present and already loaded. `KotlinToClr` (`Program.cs:1760`) and `KotlinAllToClr`
(`Program.cs:1784`) are consulted *before* `AliasBcl` (`Program.cs:1905`, `AliasBcl` at
`Program.cs:1855`), so they **shadow** the alias with the shorthand. Delete the shadow and the alias
path is what remains.

---

## §1 — Can bir2cir route primitives through the `@ClrTypeAlias` index? **YES (feasible, cheap for the DATA).**

**Non-primitive alias path** (e.g. `StringBuilder`, `Regex`): in `LowerType`
(`Program.cs:1898-1913`) a leaf `Fqn` with no map hit falls to `if (AliasBcl(f.Name) is string
bclNonGen) return new TypeNode.Fqn(bclNonGen);` → produces `"System.Text.StringBuilder"`. A generic
alias owner (`Program.cs:1911`) → `new TypeNode.Fqn(bcl, loweredArgs)`.

**Primitive path**: the same `LowerType` leaf hits `KotlinToClr` FIRST
(`Program.cs:1905` `var map = force ? KotlinAllToClr : KotlinToClr; if (map.TryGetValue(f.Name, out
var clr)) return new TypeNode.Fqn(clr);`) → produces the shorthand `"int"`/`"sbyte"`. It never
reaches `AliasBcl`, even though `AliasBcl("kotlin.Int")` would return `"System.Int32"`.

**What each produces:** `KotlinToClr` → `"int"`, `"sbyte"`, … ; the alias path → `"System.Int32"`,
`"System.SByte"`, … .

**Does ilemit resolve `System.Int32` to the right type?** — **YES, identically.**
`MapType`'s final switch (`toolchain/ilemit/Program.cs:4100-4118`) resolves `"int"` → `typeof(int)`
via the literal case, and resolves a dotted `"System.Int32"` via the `_ =>` arm → `TryMapEmittedType`
(null) → `t.Contains('.')` → `ResolveType("System.Int32")`, which reflects to **the same
`typeof(int)`** (System.Int32 *is* Int32 in reflection). Therefore **value-type detection**
(`Type.IsValueType`), **boxing** (`NeedsBoxToRef` on the resolved Type, `Program.cs:2736`+),
**arithmetic** (keyed on returned `Type`, `Program.cs:2512-2582`), **array `stelem`/`ldelem`**
(keyed on `elem == typeof(int)`, `Program.cs:1387,1406`), **generic construction**, and **default/
newarr** are all **unaffected** — they consume the resolved `Type`, not the token string.

**Is the shorthand load-bearing for opcode selection?** — **YES, in exactly three name-keyed
switches** that read the token STRING (via `SlotName`) and never resolve to a `Type`:

- `EmitConst` (`toolchain/ilemit/Program.cs:2462-2489`): `case "int": Ldc_I4 …`. A `System.Int32`
  token falls to `default: Ldnull; return typeof(object)` → **wrong IL / bad stack type** for a
  primitive constant.
- `EmitConv` (`toolchain/ilemit/Program.cs:2640-2653`): `case "int" or "kotlin.Int": Conv_I4`. A
  `System.Int32` token hits `default: throw NotSupportedException`. **NB the existing `"int" or
  "kotlin.Int"` dual-key is the precedent** — the design already anticipated the pure-Kotlin spelling
  here, so adding `"System.Int32"` (or a Type-based switch) is idiomatic, not novel.
- `ConstArgValue` (`toolchain/ilemit/Emitter.Metadata.cs:141-167`): annotation/const-default
  materialization, `"long" => v.GetInt64()`, `"sbyte" => (sbyte)…`, `"char" => (char)…`. A
  `System.*` token would fall to `_ => v.GetInt32()` → **truncation/overflow** for long/float/char.

Those three are the ENTIRE opcode-selection surface that keys on the shorthand string. (Other
`SlotName`-of-`type` readers — `EmitClrPropGet/Set` `Program.cs:3540/3571`, `NativeType`
`Program.cs:3391`, `EmitInlineSplice` `Program.cs:3138` — route the name back through
`MapType`/`ClrRef`, so they already accept `System.X`.)

**Verdict §1: feasible.** Rerouting the DATA is a delete-the-shadow change; the only real work is the
three opcode switches.

---

## §2 — Mode-gating: **NO chicken-and-egg. Clean fit.**

`LowerType` early-returns the pure-Kotlin surface for the ref build BEFORE any map/alias lookup:
`Program.cs:1893` `if (!force && refBuild) return f;`. So in the reference build a primitive STAYS
`kotlin.Int` regardless of whether the lookup is `KotlinToClr` or `AliasBcl` — the alias path is
already gated by the same `refBuild` guard that gates `KotlinToClr` today. Rerouting changes nothing
about the gate.

The ref build does **not** consume its own aliases: it early-returns above the alias read, and in a
ref build there is no prior ref.dll loaded anyway (`_aliases` is empty/irrelevant). The alias index
is populated only for rt/app builds, from the *already-built* ref.dll — which is exactly the build
that is supposed to lower primitives. So "does the ref build read its own aliases?" → it reads none
and applies none. **No bootstrap loop.**

(One nuance: `KotlinAllToClr` on the `force` path — attribute blobs — is applied UNCONDITIONALLY,
including in the ref build, because a custom-attribute blob needs a concrete `System.*` even there.
The alias index is *not* available in the ref build, so the **force/attribute path must keep a
hardcoded map** — see the staged plan. This is the one primitive map that genuinely cannot be
retired.)

**Verdict §2: feasible; the non-force path reroutes cleanly, the force/attribute path stays
hardcoded (unavoidable — no ref.dll in the ref build).**

---

## §3 — facadegen reverse map: **PARTIAL / harder — needs a new input.**

facadegen's `System.X → kotlin.X` reverse map is a hardcoded `switch` on `t.FullName`
(`toolchain/facadegen/Program.cs:1483-1494`: `"System.Int32" => new TN.Fqn("Int")`, `"System.SByte"
=> "Byte"`, …). It runs when reflecting an **arbitrary target .NET assembly** (Avalonia/WPF/NuGet)
via `MetadataLoadContext` (`Program.cs:59-86`), mapping .NET parameter/return types back to Kotlin.

**facadegen does NOT load the DotKt ref.dll** in the production import path. Its own note is explicit
(`Program.cs:305-307`): *"in the PRODUCTION import-scan path (`--meta … --import-list`, no DotKt
ref.dll scanned) …"*. It reflects only the target `--refs`/`--scan-asm` assemblies. So to make this
map metadata-driven, facadegen would have to **additionally load `DotKt.Private.Stdlib.dll` and build
a reverse index** by inverting its `@ClrTypeAlias` attributes (`System.Int32 → kotlin.Int`).

That is doable (same MetadataLoadContext it already uses; the same `ClrAliasOf` read bir2cir does at
`Program.cs:1176`), but it is a **new dependency + new inversion step**, and it must resolve the
**signed/unsigned split deliberately** (`System.SByte→Byte` vs `System.Byte→UByte`,
`Program.cs:1489-1493`, tagged #53) — the inverse of the alias is `System.SByte→kotlin.Byte` (correct)
but `System.Byte` has **no** primitive alias (kotlin.UByte aliases *to* System.Byte via a different
attribute path), so the reverse index needs the UByte/UShort/UInt/ULong aliases too, which live on
the unsigned inline-class declarations, not the signed Primitives.kt. Confirm those carry
`@ClrTypeAlias` before relying on inversion.

**Verdict §3: partial.** Feasible but the only one of the four that needs a genuinely new facadegen
input (the ref.dll) + an inversion pass with the signed/unsigned subtlety. Lower priority; it does
not block the bir2cir/ilemit reroute.

---

## §4 — kotc `clrMethodShape` (BirEmitter.kt:3052): **kotc-purity item; deletable but needs a bir2cir shape-synth.**

`clrMethodShape` (`toolchain/kotc/.../BirEmitter.kt:3052`, the block at 3082 is its `when`) produces
**ilemit-`Shape` MATCHER tokens** — `"Int64"`/`"SByte"`/`"Single"`/… — NOT type tokens (its own
comment: *"an ilemit-Shape MATCHER … not a type EMISSION"*). It is a layer violation (kotc knows .NET
shape names), tagged #53 in-file.

**Consumers:** two call sites (`BirEmitter.kt:3766`, `4317`) emit a `"shapes":[…]` array on
`clrGenericStatic` nodes. Downstream, ilemit's `ResolveGenericMethod`
(`toolchain/ilemit/Program.cs:3966-3967`) matches those strings against `Shape(Type)`
(`Program.cs:3946+`, which computes the SAME `"Int64"`/`"SByte"`/… from a resolved `Type`) to pick a
generic-method overload by exact parameter shape. bir2cir already *appends* to `shapes` in two places
(`ValueTypeNullableCollectionArg.cs:81` adds `"IEnumerable"`; `SuspendColdLowering.cs:2158-2160` adds
`"generic"`), proving bir2cir can own shape synthesis.

**Deletability:** yes — once bir2cir derives the `shapes` array from the (already type-lowered)
parameter types + the `@ClrTypeAlias` index instead of kotc precomputing it. This is **independent of
`KotlinToClr`** (a different DRY duplication — the *shape-name* alphabet, `Int64`/`SByte`, distinct
from the *type-token* alphabet `int`/`sbyte`). It is a real kotc-purity cleanup but should be
sequenced AFTER the type-token reroute, since it touches BIR node emission (`clrGenericStatic`) and
needs its own overload-resolution regression pass.

**Verdict §4: feasible, deletable, but a separate work item (BIR-schema-touching, needs bir2cir shape
synthesis + a generic-overload regression sweep).**

> **✅ DONE (Stage D, 2026-07-08).** Implemented exactly as scoped, via the transient-`shapeTypes`
> design Codex recommended (option a): kotc emits the DECLARED parameter types as pure-Kotlin `birType`
> nodes in a BIR-only `shapeTypes` array (the two sites at `BirEmitter.kt` — generic .NET member +
> generic top-level fun); bir2cir's new `ShapeSynthesis.cs` pass converts each to the ilemit shape token
> and writes the frozen `shapes` string array (unchanged reflection island), then removes `shapeTypes`.
> The `.NET` simple names come from the ref.dll `@ClrTypeAlias` index (`refs.Aliases`, `kotlin.Long` →
> `System.Int64` → `"Int64"`); a hardcoded primitive fallback (`PrimShapeName`) covers the alias-less ref
> build, mirroring the `KotlinAllToClr` decision. The structural tokens (`gp`/`array`/`generic`/`ienum`/
> `func:N`/`string`/`char`/`int`) fall straight out of the `TypeNode` shape. `clrMethodShape` and the dead
> `clrGen` helper are deleted from kotc (`grep clrMethodShape toolchain/kotc/src` → 0). The pass runs in
> the Phase-1 per-file loop right after `MemberCallSubstitution` — before `SuspendColdLowering` (which
> reads `shapes`) and before type lowering (int/string/char depend on the pre-lowering kotlin.* spelling).
> **ilemit is untouched.** Behavior byte-identical: the full stdlib + app `(method, shapes)` set is
> unchanged vs baseline; verify-il 242/0, all gates green, schema 0 violations.

---

## §5 — Blast radius of `KotlinToClr` / the shorthand vocabulary

- `KotlinToClr` / `KotlinAllToClr`: **8 references in `toolchain/bir2cir/Program.cs`** (two dict
  definitions at 1760/1784 + uses at 1758, 1829, 1846, 1905, 2201, 2238). Contained.
- The shorthand token vocabulary (`"int"`/`"sbyte"`/…) is **wider and re-synthesized independently**
  of `KotlinToClr`, so deleting the map does NOT purge the vocabulary:
  - ilemit consumers: `MapType` switch (`Program.cs:4102`), `PrimShorthand` set (`Program.cs:3910`),
    `NativeType` switch (`Program.cs:3394`), `EmitConst` (2464), `EmitConv` (2645), `ConstArgValue`
    (Emitter.Metadata.cs:141), `Shape`/`ShapeName` (3946+), array elem compares.
  - bir2cir *producers* of shorthand OTHER than `KotlinToClr`: `ValueTypePrimitiveFqns` seed
    (`Program.cs:553`), the field-conv map (`Program.cs:1255-1257`, keyed on BOTH `kotlin.Int` AND
    `System.Int32` already), the numeric-kind map (`Program.cs:789-791`, also dual-keyed
    `kotlin.Int`/`System.Int32`/`int`), `TypeName(Type)` reverse (`Program.cs:1320-1328`, `typeof(int)
    → "int"`), and synthetic constants `TypeJson.Fqn("int")` (`Program.cs:3427`, `4970`).

**What breaks if `KotlinToClr` is deleted with NO other change:** primitives lower to `System.Int32`;
type resolution/arith/arrays/box stay green; **`EmitConst`, `EmitConv`, `ConstArgValue` break** (the
three §1 switches). Everything else is either Type-keyed (fine) or already dual-keyed on
`kotlin.*`/`System.*` (fine — note `Program.cs:789` and `1255` already accept `System.Int32`).

**Verdict §5: contained.** The map is 8 sites; the breakage is 3 opcode switches; the vocabulary
itself is not removable (independent producers/consumers remain).

---

## THE CRUX — Is the shorthand token vocabulary DELETABLE or load-bearing?

**Definitive answer: the shorthand is LOAD-BEARING in a narrow, well-bounded way, and therefore NOT
fully deletable — but the redundant kotlin↔CLR *mapping fact* it encodes IS retirable.**

Two separable things are conflated in the task framing:

1. **The mapping fact** `kotlin.Int ⇄ System.Int32` — duplicated across the four copies. This **is**
   redundant with the `@ClrTypeAlias` metadata (proven: `_aliases["kotlin.Int"]=="System.Int32"`
   already). Copies #1 (`KotlinToClr` non-force), #3 (facadegen reverse), #4 (kotc shapes) can be
   made metadata-driven / deleted.

2. **The shorthand alphabet** `"int"`/`"sbyte"` — this is **ilemit's opcode-selection vocabulary**,
   not a copy of the mapping fact. It is load-bearing in `EmitConst`/`EmitConv`/`ConstArgValue`, and
   it is **produced by bir2cir at sites unrelated to `KotlinToClr`** (synthetic const nodes, field
   conv). You cannot delete `"int"` from the vocabulary without either (a) converting those three
   opcode switches to resolve-`System.Int32`-then-switch-on-`Type`, AND (b) migrating every bir2cir
   synthetic producer to `System.Int32`. That is a larger, lower-value churn.

**So:** the user's intuition ("the metadata makes it just read it") is **correct for the DATA / copy
#1's map** and **incorrect for the shorthand alphabet as a whole**. The honest framing: *retire the
redundant map, keep the alphabet.* If a fully `System.*`-uniform pipeline is desired later, the extra
cost is the three opcode switches + the synthetic-producer migration — still contained (no core
lowering rewrite), just more surface than the minimum.

---

## Staged plan (cheapest → most surgical)

Each stage is independently shippable and gate-checkable (`./scripts/verify-il.sh`,
`verify-roundtrip.sh`, `verify-schema`). Rebuild the stdlib clean between stages
(`rm -rf build/clr-stdlib*`) to avoid the cached-dll masking landmine.

### Stage A — Prove-out (no behavior change). RISK: none.
Add a temporary assert/log in bir2cir confirming `AliasBcl(f.Name)` returns the expected `System.*`
for each primitive when `KotlinToClr` would have fired. Confirms the index is populated in the actual
rt + app builds (the reflection probe already confirms the ref.dll; this confirms the *loaded* index).

### Stage B — Make the three ilemit opcode switches `System.*`-tolerant. RISK: low, isolated.
Convert `EmitConst` (`Program.cs:2462`), `EmitConv` (`Program.cs:2640`) and `ConstArgValue`
(`Emitter.Metadata.cs:141`) to **resolve the slot via `MapType`/`NativeType` then switch on the
`Type`** (or, minimally, add `"System.Int32"`-family cases alongside the existing `"int"`/`"kotlin.Int"`).
Prefer the Type-based rewrite — it is the durable form and follows the existing `"int" or
"kotlin.Int"` precedent. **Do this BEFORE Stage C** so the pipeline accepts `System.*` primitive
tokens before bir2cir starts emitting them. Gate must stay green (this is a superset-accept change —
`"int"` still works).

### Stage C — Retire `KotlinToClr` (the non-force map, copy #1). RISK: medium, the real reroute.
Delete the `KotlinToClr` shadow so the leaf falls through to `AliasBcl` → `System.Int32`. Keep the
`refBuild` early-return (`Program.cs:1893`) untouched (ref stays `kotlin.*`). **Keep `KotlinAllToClr`
(the force/attribute path)** — the ref build has no ref.dll, so attribute blobs still need a hardcoded
`System.*` map (§2). Also decide the fate of the shorthand: EITHER (c1) let primitives now be
`System.Int32` end-to-end and migrate bir2cir's synthetic `Fqn("int")` producers to `System.Int32`,
OR (c2) re-normalize `System.Int32→"int"` at the point of lowering to keep the vocabulary — but (c2)
just *moves* the hardcoded table, so it defeats the DRY goal and should be rejected. Choose (c1).
Full gate + roundtrip + schema.

### Stage D — kotc `clrMethodShape` → bir2cir shape synthesis (copy #4). RISK: medium, BIR-touching.
Move shape derivation into bir2cir (compute the `shapes` array from lowered param types + the
`@ClrTypeAlias` index, mirroring `Shape(Type)` and the existing `ValueTypeNullableCollectionArg` /
`SuspendColdLowering` shape appends), then delete `clrMethodShape` and the two kotc emit sites'
precomputation. Needs a generic-`.NET`-member-overload regression sweep. Independent of A–C; sequence
after C so bir2cir already owns the type view.

### Stage E — facadegen reverse map → ref.dll-driven (copy #3). RISK: medium, new input.
Have facadegen load `DotKt.Private.Stdlib.dll`, build a `System.* → kotlin.*` reverse index from its
`@ClrTypeAlias` attributes (+ the unsigned inline-class aliases), and replace the hardcoded switch
(`Program.cs:1483`). Preserve the signed/unsigned #53 split explicitly. Lowest priority; fully
decoupled from A–D.

---

## Honest cost estimate

| Item | Redundant with metadata? | Nature | Cost |
|------|--------------------------|--------|------|
| #1 `KotlinToClr` (non-force map) | **Yes — data already in `_aliases`** | Delete shadow + 3 opcode switches (Stage B+C) | **Contained reroute** (~a day incl. gate) |
| #1 `KotlinAllToClr` (force/attr) | No (no ref.dll in ref build) | **Must stay** | none — keep |
| shorthand alphabet `"int"`/… | It's the opcode vocabulary, not the mapping fact | Load-bearing in 3 switches + bir2cir synthetic producers | **Not deletable**; only convertible with extra churn (Stage C c1) |
| #4 kotc `clrMethodShape` | Yes (derivable) | bir2cir shape synth + kotc delete | **✅ DONE (Stage D)** — `ShapeSynthesis.cs` derives `shapes` from `shapeTypes`; byte-identical |
| #3 facadegen reverse | Yes (invertible) | New ref.dll input + inversion | **Contained, separate** (new dependency, #53 subtlety) |

**Is #55 a contained reroute or a Phase-5-scale core rewrite?** — **Contained.** There is **no core
primitive-lowering rewrite**: type resolution, value-type detection, boxing, arithmetic, arrays and
generic construction already treat `System.Int32` and `"int"` identically (ilemit resolves both to
`typeof(int)`). The only genuine surgery is **three small, isolated opcode switches**, each with an
existing dual-key precedent. The user's "just read it" intuition is **validated for the mapping
data** (the metadata is provably already loaded) and **qualified for the shorthand alphabet** (that
stays as ilemit's opcode vocabulary; retiring the *map* does not retire the *tokens*).

**Recommended minimum ship:** Stages A→B→C (retire `KotlinToClr`, the headline redundancy), leaving
D (kotc shapes) and E (facadegen) as independent follow-ups. This is a clean reroute, not a minefield
— provided Stage B lands first so the three opcode switches accept `System.*` before bir2cir emits it.

---

## Implementation notes (Stages A–C landed, 2026-07-08)

Shipped exactly as staged (A folded into the empirical gate rather than a temporary log). The actual
diff, and three ripple sites the read-only survey under-counted:

**Stage B — ilemit opcode switches (`toolchain/ilemit/`).** Added one static normalizer
`PrimShorthandName(string)` (`Program.cs`, next to `SlotName`) mapping the alias spelling to the opcode
alphabet — `System.Int32`→`int`, `System.SByte`→`sbyte` (SIGNED = Kotlin Byte), `System.Byte`→`byte`
(UNSIGNED = Kotlin UByte), `System.Single`→`float`, `System.Object`→`object`, … . Applied at the head
of the three switches: `EmitConst` (`type` slot), `EmitConv` (`to` slot), `ConstArgValue`
(`Emitter.Metadata.cs`, `type` slot). Superset-accept: the shorthand and the existing `"int" or
"kotlin.Int"` dual-keys still match.

**Stage C — bir2cir (`toolchain/bir2cir/Program.cs`).** Deleted the `KotlinToClr` dictionary. Both leaf
lowerers — structured `LowerType` and string `LowerLeaf` — now try `KotlinAllToClr` ONLY on the
`force`/attribute-blob path, else fall to `AliasBcl` (the ref.dll `@ClrTypeAlias` index). The
dual-representation `@kotlin.Int` decorated-primitive stays verbatim (its guard moved ahead of
`AliasBcl` in `LowerLeaf`). `KotlinAllToClr` kept (attribute blobs, no ref.dll in the ref build).

**Three ripple sites the §-survey missed / under-specified:**
1. **`kotlin.Nothing` has no `@ClrTypeAlias`** (the survey assumed all of `KotlinToClr`'s data was in
   `_aliases`; Nothing was the exception). Fixed stdlib-side: added `@ClrTypeAlias("System.Object")` to
   `libraries/stdlib/clr/builtins/Nothing.kt` (the bottom type erases to `object`, like `kotlin.Any`).
   Metadata-driven, not a compiler hardcode — and it lets the `FoundationalRefAliases` fallback for
   Nothing eventually retire too.
2. **`IsObjectish` (bir2cir)** keyed only on `"object"`/`kotlin.Any`/`kotlin.Nothing`; a star-projected
   `Comparable<*>` arg (kotc emits `kotlin.Any`) now lowers to `System.Object`, so `"System.Object"`
   was added — else the `Comparable<*>`→non-generic `System.IComparable` contravariance case regresses.
3. **The string-path twin at `LowerTypeString`** (the `genericBcl == "System.IComparable" && args ==
   "object"` guard) now also accepts `"clr:System.Object"` (the bare `kotlin.Any` leaf lowers to that
   form in the string path).

**Confirmed:** `grep -n KotlinToClr toolchain/bir2cir/Program.cs` returns only comments (the dict and
both uses are gone); `KotlinAllToClr` stays. Emitted primitive values are byte-identical (the #53/#54
byte tests + all arithmetic/conv/const samples are the safety net); full `m1verify` green.
