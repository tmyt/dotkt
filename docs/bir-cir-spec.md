# BIR/CIR Specification (v1 — frozen contract)

> NORMATIVE. This is the single source of truth for the BIR/CIR serialization format. Every layer
> (kotc emit / bir2cir consume+produce / ilemit consume / facadegen meta / [KotlinInline] splice)
> implements to THIS. Design rationale: `docs/design-bir-cir-freeze.md`. Exhaustive prior-state audit:
> `docs/bir-audit/*.md`. Durable-ABI principles: uniformity, self-describing, additive-extensible,
> codec-agnostic, single-source. **BIR contains NO stringly-typed compound tokens — types are nodes.**

## 0. Envelope & versioning
- The carrier attributes stamp `(string version, byte[] content)`. `version` = `"bir-json/1"` today
  (future binary = `"bir-msgpack/1"`; schema bump = `"bir-json/2"`). `content` = the codec-encoded body.
- A single `DecodeBody(version, byte[])` / `EncodeBody(version, node)` dispatches on `version`.
- Carriers: `KotlinInlineAttribute(string version, byte[] content)` (inline-fn body),
  `KotlinSuspendFunctionTypeAttribute` folds into the structured `Type` (its `sfunc:` string is gone).

## 1. Type — the universal type representation (FULL structured, no exceptions)

A `Type` is ALWAYS a JSON object with a `t` discriminator. **There is no bare-string type.** Readers
`dispatch(t)`; they never split/scan a string. `T` below denotes a nested `Type`.

| `t` | fields | Kotlin meaning | replaces (old string token) |
|-----|--------|----------------|-----------------------------|
| `fqn` | `name:string`, `args?:[T…]` | a named type `kotlin.collections.List<…>` — a PURE Kotlin/CLR FQN identity, generic args optional | plain FQN, `clr:`, `clrg:Name[..]`, `@Name`/`@Name[..]`, primitive shorthand (`int`/`string`/`void`/`object`/…) |
| `tv` | `i:int` | a type variable, **positional** (declaration order on the owning generic decl) | `gp:X` (name-keyed) |
| `fn` | `suspend:bool`, `ret:T`, `params:[T…]`, `recv?:T` | a function type; `suspend` is a flag, `recv` = extension receiver | `func:ret:args`, `sfunc:ret:args` |
| `nullable` | `of:T` | `T?` | `nullable:X` |
| `array` | `elem:T` | `Array<T>` (this-assembly array) | `array:X` |
| `byref` | `of:T` | a CLR by-ref `ref T` | `byref:X` |

Notes:
- **No CLR-resolution marker in kotc output.** kotc emits `{t:"fqn",name:"kotlin.Int"}` — the *identity*
  only. bir2cir DERIVES the CLR form (primitive opcode, generic construction, referenced-type resolution).
  `clr:`/`clrg:`/`@`/shorthand are DELETED; the resolution decision lives below the kotc boundary.
- **`tv.i` is positional**, killing the `gp:`-name remap (`CanonSig`/`FindReflectedMethodBySigLoose` deleted).
  The index is into the owning declaration's type-parameter list; nested generics repeat the enclosing
  params by index (the CLR nested-generic encoding is bir2cir's job, derived from the indices).
- `fn` subsumes both plain and suspend function types; the H2 position metadata is just an `fn` with
  `suspend:true` in a param/return/field slot — no separate `sfunc:` token, no `BirTokenToMeta`.
- Examples:
  - `kotlin.Int` → `{"t":"fqn","name":"kotlin.Int"}`
  - `List<Int>` → `{"t":"fqn","name":"kotlin.collections.List","args":[{"t":"fqn","name":"kotlin.Int"}]}`
  - `(Int)->String` → `{"t":"fn","suspend":false,"ret":{"t":"fqn","name":"kotlin.String"},"params":[{"t":"fqn","name":"kotlin.Int"}]}`
  - `suspend Foo<T>.()->T?` → `{"t":"fn","suspend":true,"recv":{"t":"fqn","name":"Foo","args":[{"t":"tv","i":0}]},"ret":{"t":"nullable","of":{"t":"tv","i":0}},"params":[]}`

## 2. Node kinds — the `{"k":…}` expression/statement/decl vocabulary

Node kinds stay `{"k":…}`-tagged objects (already structured). The freeze CLEANS them (no representation
change). Canonical set = the live kinds from the audit, MINUS the dead/merged below. Every `type`/`ret`/
`elem`-valued field inside a node now holds a **`Type` node** (§1), never a string.

DELETED (dead / producer-zero — `docs/bir-audit/ilemit-consume.md`):
- The entire `clr.*` twin family: `clr.const`, `clr.bin`, `clr.un`, `clr.conv`, `clr.obj.eq`, `clr.newarr`,
  `clr.ldelem`, `clr.stelem`, `clr.ldlen`, `clr.str.concat`, `clr.obj.method`, `clr.default`,
  `clr.array.spread`, `clr.stackalloc`, `clr.stack.*`, `clr.constrained.compareTo`, `clr.nullable.*`,
  `clr.enum.*`, `clr.safeCast.value`, `clr.typeof`, `clr.getType`. (The live spelling is the non-`clr.` twin.)

MERGED (same-shape variants → one canonical kind):
- `setField` / `setFieldExpr` / `staticFieldSet` field-write family → decide one canonical write node
  (a `field`/`staticField` target + a `value`; stmt-vs-expr is a position, not a kind). [finalize in impl]
- `objMethod` (one `ToString` site) → `callInstance`.

KEPT distinct (NOT synonyms — different semantics): `staticField`≠`clrStaticField`,
`callInstance`≠`clrInstance`, `field`≠`clrPropGet`, `field`/`setField` (this-asm field) ≠ property accessors.

Control flow: the structured `for*` family and the CFG `label`/`brIf`/`goto` while-family coexist
(mid-migration, audit D8) — the freeze picks the CFG form as canonical for lowered output; the structured
`for*` may remain as a kotc-emit sugar that bir2cir lowers. [finalize in impl]

### 2.1 Declaration modifiers — structured `mods` (replaces string-concat fragments, audit D7)
Today a decl's modifiers are **order-dependent string-concatenated fragments** (`$inlineFlag$kmods`, the
meta `final,inline,ext,suspend,infix,operator` comma string) — a stringly-typed set that drifts on ordering
and forces substring/`Contains` checks. FREEZE: every declaration (method/property/class/param) carries a
single **`mods` object**, a set of `name:true` flags (absent = not set). Order-free, self-describing,
additive (a new modifier = a new key). No comma strings, no `Contains`/`StartsWith` on a modifier blob.
```jsonc
"mods": { "inline":true, "infix":true, "operator":true }        // a fun; omitted keys = false
```
- Method/fun flags: `inline`, `infix`, `operator`, `tailrec`, `external`, `ext` (extension), `override`,
  `abstract`, `open`, `suspend` (also drives the `fn`-type `suspend` flag §1 — keep consistent), `data`-generated.
- Class flags: `data`, `sealed`, `inner`, `abstract`, `open`, `enum`, `fun` (fun-interface), `annotation`, `value`.
- Property flags: `const`, `lateinit`, `override`, `open`, `ext`.
- Param flags: `noinline`, `crossinline`, `vararg`.
- **Visibility is NOT a mod** (it is an enum, not a boolean): a separate `"vis": "public"|"private"|"protected"|"internal"`.
- **Modifier semantics that drive lowering stay first-class** where a consumer keys on them (e.g. `suspend`
  already gates cold-lowering) — `mods.suspend` is the single source; a redundant top-level `suspend` field is removed.
The meta side (facadegen tlfun/tlextprop/tlprop) emits the SAME `mods` object, not the `final,inline,ext` comma string.

(The full per-kind field table is generated from `docs/bir-audit/kotc-emit.md` §1 during impl; this section
lists only the freeze DECISIONS. The validator (§4) enforces the canonical set.)

## 3. Labels & naming (conventions consumed as opaque strings)
- SM / coroutine method names: `<name>$dotkt_suspend` (cold entry), `<name>$sm` (state machine class) —
  chosen by bir2cir, opaque to ilemit. Resume labels: integer CFG `id`s (ilemit consumes only `label`/
  `goto`/`brIf` with int ids; no textual resume-label vocabulary).
- Synthetic types `<>dotkt_*`; capture fields `__outer`/`$this`/`__self`; temp vars via a fresh counter.

## 4. Shared helper API (single-source — the anti-drift linchpin)

ONE type read/write per language, used by EVERY site. No other code parses/builds a `Type`.

**Kotlin (kotc)** — `kotc.bir.TypeNode` (sealed) + `TypeNode.toJson(): JsonValue` / `TypeNode.parse(json)`.
`birType(IrType): TypeNode` produces the node; nothing emits a type string.

**C# (bir2cir / ilemit / facadegen)** — a shared `DotKt.Bir.TypeNode` record hierarchy (Fqn/Tv/Fn/Nullable/
Array/Byref) + `TypeNode Read(JsonElement)` / `JsonNode Write(TypeNode)`, in ONE shared file referenced by
all three C# tools. Every `MapType`/`SplitTopLevel`/`FuncRetEnd`/`SkipTypeToken`/`BirTokenToMeta`/`BareOwner`/
`CanonSig` is DELETED and replaced by walking `TypeNode`.

## 5. Validator (§7 of the plan)
Validate live BIR/CIR + every emitted `[KotlinInline]` body against this spec: unknown `k`, a type that is
not a valid `Type` node, or an unknown `version` reddens a gate. Round-trip: decode every stdlib ref.dll
inline body, assert it re-encodes identically.
