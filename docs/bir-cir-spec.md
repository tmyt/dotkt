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
| `tv` | `scope:"type"\|"method"`, `i:int` | a type variable — `scope` is the CLR generic-param SPACE, `i` the owner-local positional index | `gp:X` (name-keyed, space-blind) |
| `fn` | `suspend:bool`, `ret:T`, `params:[T…]`, `recv?:T` | a function type; `suspend` is a flag, `recv` = extension receiver | `func:ret:args`, `sfunc:ret:args` |
| `nullable` | `of:T` | `T?` (NRT-annotated nullable, `NullableAttribute`=2) | `nullable:X` |
| `oblivious` | `of:T` | `T!` — an NRT-*oblivious* flexible type `(T..T?)` (`NullableAttribute`=0); the CLR term, not the Kotlin-consumer "platform" name | the META `!` platform suffix |
| `array` | `elem:T` | `Array<T>` (this-assembly array) | `array:X` |
| `byRef` | `of:T` | a CLR by-ref `ref T` | `byRef:X` |

Notes:
- **No CLR-resolution marker in kotc output.** kotc emits `{t:"fqn",name:"kotlin.Int"}` — the *identity*
  only. bir2cir DERIVES the CLR form (primitive opcode, generic construction, referenced-type resolution).
  `clr:`/`clrg:`/`@`/shorthand are DELETED; the resolution decision lives below the kotc boundary.
- **`tv` is scope-tagged + positional**, killing the `gp:`-name remap (`CanonSig`/`FindReflectedMethodBySigLoose`
  deleted). `scope:"method"` → the method's own generic params (CLR `!!i`, `GenericMethodParameter`);
  `scope:"type"` → the enclosing TYPE's generic params (CLR `!i`, `GenericTypeParameter`), where `i` is
  FLATTENED over the nesting chain (a nested generic type repeats its enclosing types' params — kotc computes
  the flattened type-index, as it already does). The two spaces are DISTINCT on the CLR; a single flat index
  conflating type+method is a Reflection.Emit bug (Codex-confirmed), so `scope` is MANDATORY. bir2cir/ilemit
  map `scope`+`i` straight to `!i` / `!!i`.
- `fn` subsumes both plain and suspend function types; the H2 position metadata is just an `fn` with
  `suspend:true` in a param/return/field slot — no separate `sfunc:` token, no `BirTokenToMeta`.
- **Nullability is TRI-STATE, named with the CLR/Roslyn vocabulary** (`NullableAttribute` 1/2/0 =
  not-annotated / annotated / **oblivious**). A reference type is one of three states, each a COHERENT node
  naming its own CLR state (the representation must NOT collapse oblivious to nullable — that breaks overload
  resolution + null-safety on every un-annotated BCL member):
  - **not-null `T`** — the BARE type node (no wrapper). Default.
  - **nullable `T?`** — `{t:"nullable","of":T}` (NRT-annotated nullable).
  - **oblivious `T!`** — `{t:"oblivious","of":T}` — a flexible type `(T..T?)`; the frontend/`ConeFlexibleType`
    decides null-safety per use, exactly as Kotlin treats un-annotated Java. `oblivious` is the CLR/Roslyn term
    for `NullableAttribute`=0, NOT the Kotlin-consumer "platform" name — the node states the .NET metadata's
    actual annotation, not how a consumer treats it.
  This is ONE tri-state model shared by BIR and META: **kotc BIR emits only not-null + nullable** (`oblivious`
  is frontend-only — resolved to a concrete nullability BEFORE the backend, so `{t:"oblivious"}` never appears
  in a BIR/CIR the backend produces; bir2cir/ilemit's `TypeNode.Read` accepts it for round-trip but never
  encounters it); **facadegen META emits all three** (a `.NET` member with NO `NullableAttribute` → `oblivious`).
  `oblivious` is a coherent sibling node (each state names itself), NOT a `nullable`-node refinement flag —
  additive per principle 3. The old **duplicate** nullability encodings — the type wrapper AND the separate
  decl-level `"nullable":true` flag — collapse onto the Type node (the decl flag is retired in m5; a type's
  nullability lives on the Type node).
- Examples:
  - `kotlin.Int` → `{"t":"fqn","name":"kotlin.Int"}`
  - `List<Int>` → `{"t":"fqn","name":"kotlin.collections.List","args":[{"t":"fqn","name":"kotlin.Int"}]}`
  - `(Int)->String` → `{"t":"fn","suspend":false,"ret":{"t":"fqn","name":"kotlin.String"},"params":[{"t":"fqn","name":"kotlin.Int"}]}`
  - `suspend Foo<T>.()->T?` → `{"t":"fn","suspend":true,"recv":{"t":"fqn","name":"Foo","args":[{"t":"tv","scope":"type","i":0}]},"ret":{"t":"nullable","of":{"t":"tv","scope":"type","i":0}},"params":[]}`

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

### 2.5 Node-kind FORMAT stabilization — canonical field names + per-kind schema + validator
Node kinds are `{k}`-tagged objects but their FIELD names drifted (each wave's agent named fields ad-hoc).
Audit-confirmed drift: the "a type" concept is spelled `type`/`retType`/`ret`/`elem`/`of`/`keyType`
(`retType`≡`ret` are the SAME return type; `type`≡`of` overlap); a value/sub-expr is `value`/`val`/`init`/
**`e`/`l`/`r`** (cryptic single letters = expression/left/right); a list is `args`/`params`/`elems`; a name is
`name`/`member`/`method`/`field`. (`recv` is the good case — one spelling.)

Unlike `Type` (§1), node kinds CANNOT be collapsed to one shared model — there are ~95 distinct shapes. So
node-format stability is achieved DECLARATIVELY, in three parts:
0. **Casing convention** — every `k` value AND every field name is **lowerCamelCase**, uniformly. Audit
   (89 kotc `k` values): no snake_case/UpperCamel/dotted (good), but flattened abbreviations HIDE case
   boundaries inconsistently — `isinst`/`isinstRef` spell "instance" as `inst` while `callInstance` uses
   `Instance`. Fix: `isinst`→`isInst`, `isinstRef`→`isInstRef`; de-abbreviate case-hiding shorthands
   (`bin`→`binOp`, `un`→`unaryOp`) OR pin a documented short-operator exception — one policy, in the spec.
   Single-word kinds (`for`/`if`/`block`) are already one-word lowerCamel (fine). The validator's canonical
   `k` set is the casing enforcer: any spelling not in the frozen set reddens the gate.
1. **Canonical field names** — one name per concept. Collapse pure synonyms (`retType`→`ret`, `val`→`value`);
   rename cryptic single letters (`e`→`e` is kept ONLY if documented; `l`/`r`→`lhs`/`rhs`). Keep genuinely
   role-distinct fields distinct (a call's `args` ≠ a decl's `params` ≠ an array's `elems`; `ret`≠`elem`≠`keyType`
   when the roles differ). Every type-valued field holds a `Type` node (§1).
2. **Per-kind schema** — the spec pins each `k`'s exact field set (name, required/optional, value shape),
   generated from the audit. This is the normative node shape.
3. **Schema validator (§4/§5)** — validates every node against its kind's schema: unknown `k`, unknown/missing
   field, or a wrong value shape reddens a gate. **This is the ENFORCER** — because there is no single shared
   node model, the validator is node-format's ONLY structural safety net (contrast `Type`, which is drift-proof
   by construction). Therefore the validator is NOT deferred to last; it lands early enough to guard the flip.

### 2.2 `sig` — call-site overload signature is a `Type[]` (retire the comma-joined string)
A call node carries `sig` so a consumer resolves the right OVERLOAD by name+signature. Today it is a
**comma-joined string of param type tokens** (`BirEmitter.kt:1705`, `(ext + regs).joinToString(",")`),
hand-parsed in ≥5 places (bir2cir `EnumMemberBinding`/`MapVarianceRealign`/`ValueTypeNullableCollectionArg`,
ilemit `Emitter.Expressions.cs:166/227`, `CanonSig`, `FindReflectedMethodBySig`). FREEZE: `sig` is a
**JSON array of `Type` nodes** (§1) — `"sig":[T, T, …]` (extension receiver first, then value params). No
comma-join, no `CanonSig`/`FindReflectedMethodBySig` string parse; overload match walks the `Type[]`.
Generic params in a `sig` use positional `tv` (§1), which kills the def-vs-call name-remap dance.

### 2.3 `@ClrProperty(access:Int)` bitmask → structured flags (no encoded int)
The stdlib `@ClrProperty` accessor binding encodes read/write as an **int bitmask** (`READ=1`/`WRITE=2`,
`bir2cir Program.cs:580` `out int access`). A "mysterious int" — replace with explicit booleans
`{"read":true,"write":true}` (or two attr fields). Same principle for any binding-annotation argument that
packs structure into an int/string. (`@ClrIntrinsic("System.String.Format")` stays a string — it names one
BCL method, not an encoded structure — but its target should resolve through the shared naming, not ad-hoc.)

### 2.4 Synthetic type/name generation — collision-free + documented (no lossy regex-mangle)
Synthetic CLR type names are built by **lossy regex-mangle** of a type FQN
(`"<>dotkt_ClrH_" + Regex.Replace(fqn, "[^A-Za-z0-9]", "_")` and peers). The abbreviation zoo — `ClrH`
(CLR helper), `K*` (`KIterator`/`KIterable`/`KProperty` Kotlin synthetics), `RW`/`RO` (read-write/read-only
property), `Ref` (ref cell), `tryval` (try-expr temp), `obj` (object-expr) — encodes a TYPE into a NAME
lossily: `kotlin.Char` and a hypothetical `kotlin$Char` collide (both → `kotlin_Char`), and it is not
reversible. FREEZE: derive synthetic names through a **single registry** that assigns a stable, collision-free
unique name per DISTINCT structured `Type` (dedup by the `Type` node, not by the mangled string), with the
prefix set (`ClrH`/`KIterator`/…) DOCUMENTED here as an enum. Name-mangling is not a serialization DSL, but a
lossy type→string encoding is the same durable-ABI smell (structure hidden in a string) and a real collision bug.

### 2.6 Naming convention — ONE rule for the whole format vocabulary
Every identifier in the serialization vocabulary is **lowerCamelCase**, uniformly: node `k` values, type
`t` values (§1), ALL field names (§2.5.1), `mods` keys (§2.1), `vis` enum values, injection-decl kinds (§5b),
carrier field names. Rules:
- **Multi-word → camelCase boundaries; never case-hiding-flat.** `byref`→`byRef`, `isinst`→`isInst`,
  `staticfieldset`→`staticFieldSet`. A boundary between words is always a case change.
- **No cryptic single letters / silent truncations** where they hide meaning: `e`/`l`/`r`→documented or
  `expr`/`lhs`/`rhs`; `bin`/`un`→`binOp`/`unaryOp` (or a pinned short-operator exception).
- **Accepted acronym tags** (documented, treated as a single lowercase unit — like the top-level `k`/`t`
  keys themselves): `fqn`, `tv`, `fn`. These are universal 2-3 letter type-tag units, NOT case-hiding
  multiword flattenings; they stay lowercase. (If one ever appears mid-identifier it becomes `Fqn`/`Tv`/`Fn`.)
- **Documented EXCEPTIONS (different domain, intentionally not lowerCamel):**
  - **.NET attribute TYPE names** are UpperCamel — `KotlinInlineAttribute`, `KotlinSuspendFunctionTypeAttribute`,
    `KotlinDefaultAttribute` — because they are CLR types and MUST follow the CLR/BCL convention. Their
    *constructor-arg / field* names still follow the lowerCamel rule.
  - **carrier version tags** are kebab-with-slash — `"bir-json/1"`, `"bir-msgpack/1"` — a codec+schema version
    identifier, not a vocabulary identifier; the `/` separates codec from schema-major.
- **SCOPE — vocabulary, NOT payload data.** This policy governs the format's OWN identifiers (the
  meta-language: `k`/`t` tags, field names, `mods` keys, decl kinds). It does **NOT** govern the DATA those
  fields carry — the Kotlin/CLR **symbol names** in a `name` value (`{"t":"fqn","name":"…"}`), which follow
  their SOURCE language's conventions and are copied verbatim. Concretely: the BIR type TAG for a by-ref is
  `byRef` (our vocabulary → lowerCamel), but the user-facing Kotlin by-ref SYMBOL is `kotlin.clr.byref`
  (lowercase BY DESIGN — a Kotlin API name, carried as a `name` value, untouched by this policy). Same for
  `kotlin.Int`, `System.String`, a user's `myFun` — `name` values are object-language data, never re-cased.
- **The validator enforces the canonical sets** (§4/§5): any `k`/`t`/field/mod/decl-kind spelling not in the
  frozen spec — wrong casing, an undocumented abbreviation, a synonym — reddens the gate. There is no
  case-insensitive fallback and no alias table; the spelling in this spec is THE spelling. (The validator
  checks VOCABULARY spelling, never a `name`-value's payload.)

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

## 5b. Injection metadata (facadegen → kotc) — structured, SAME vocabulary as BIR (retire the line grammar)
Today facadegen emits injection metadata as **space-separated, positional TEXT LINES** — `file <pkg>
<fileClassFQN>`, `tlfun <name> <ret> <mod=final[,inline][,ext][,suspend]…> [<TP>…] [<p>:<t>]*`,
`tlextprop <name> <type> <ro|rw> <recvType>`, `tlprop <name> <type> <ro|rw>` — and kotc's `ClrTypeInjection`
(`coneOf`/`generateProperties`/…) parses them. This is the SAME ad-hoc string DSL problem as the type tokens,
one level up, AND it forced the dual BIR-colon vs META-bracket type vocabularies + the `BirTokenToMeta`
translation.

FREEZE: injection metadata is **structured JSON reusing the BIR decl / `Type` (§1) / `mods` (§2.1) vocabulary**.
The `tlfun`/`tlextprop`/`tlprop`/`file` line grammar and its kotc parser are RETIRED. A file's injected surface
is a list of structured declaration nodes:
```jsonc
{ "file": "mylib.LibKt", "pkg": "mylib",
  "decls": [
    { "k":"fun",  "name":"exposeFun", "ret":{"t":"fqn","name":"kotlin.String"}, "mods":{"inline":true},
      "typeParams":[…], "params":[ {"name":"x","type":{"t":"fqn","name":"kotlin.Int"}} ] },
    { "k":"prop", "name":"greeting",  "type":{"t":"fqn","name":"kotlin.String"}, "mods":{}, "vis":"public" },
    { "k":"prop", "name":"lastIndex", "type":{"t":"fqn","name":"kotlin.Int"}, "mods":{"ext":true},
      "recv":{"t":"fqn","name":"kotlin.collections.List","args":[{"t":"tv","scope":"type","i":0}]} }
  ] }
```
- `tlfun` → `{k:"fun", …, "top":true}` (a top-level fun). `tlextprop` → a `prop` with a `recv` Type.
  `tlprop` → a `prop` without `recv`. The `,inline`/`,ext`/`,suspend` modifier string → `mods` (§2.1).
- The type slots are `Type` nodes (§1) — so BIR and META share ONE type vocabulary; `BirTokenToMeta`/
  `BirSkipTypeToken`/`BirSplitTopLevel` and the meta bracket-grammar are DELETED.
- Consumers (kotc `ClrTypeInjection`) walk the structured decls; no line-splitting, no `coneOf` string parse.

## 5. Validator (§7 of the plan)
Validate live BIR/CIR + every emitted `[KotlinInline]` body against this spec: unknown `k`, a type that is
not a valid `Type` node, or an unknown `version` reddens a gate. Round-trip: decode every stdlib ref.dll
inline body, assert it re-encodes identically.
