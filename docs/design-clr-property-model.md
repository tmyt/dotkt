# CLR property model — emit every Kotlin property as a CLR property

Status: **design locked** (design owner, 2026-06-28). Implementation pending (broad-impact; phase it).

## Decision

1. **Every Kotlin property is emitted as a REAL CLR property** — `PropertyBuilder` (`DefineProperty` +
   `SetGetMethod`/`SetSetMethod`) over `T get_p()` / `void set_p(T)` accessors, with a **private backing field**. So a
   Kotlin `val`/`var` is seen as a *property* by C#/F#/reflection — the interop-correct shape. (Today it's a plain public
   field — a deviation; see [dotkt-semantics.md](dotkt-semantics.md).)
2. **Emitting a property as a plain FIELD is OPT-IN via `@ClrField`.** `@ClrField val p` -> a public CLR field `p`, no
   accessors/property (the current default behavior, now opt-in). For perf-critical / layout-sensitive interop.
3. **A `byref` (ref/out interop — `__clrref` / `__clrout`, [[interop-surface-complete]]) to a property WITHIN the current
   source set lowers to IL-level BACKING-FIELD access** (`ldflda <backing>`), since the backing field is reachable
   in-module. A byref to a *cross-assembly* property is NOT supported (the backing field is private; an accessor returns
   a value you cannot take the address of) — diagnose it.

## Current state (what must change)

- **ilemit emits NO CLR properties** — there is no `PropertyBuilder`/`DefineProperty` anywhere.
- A simple `val`/`var` (no custom accessor) -> a plain **public FIELD**; access sites read it directly (`IrGetField` ->
  `{"k":"field"}`). `get_`/`set_` **methods** are emitted only for custom-accessor / CLR-interface-member / computed
  (no-backing-field) properties — and even those are bare methods, not a `PropertyBuilder` property.
- Consequence: a property implementing a Kotlin **interface property** (e.g. `ComparableRange.start` over
  `ClosedRange<T>.start`) emits only a field, so the abstract `get_start` slot is unfilled -> load failure. And no
  property is idiomatic to a C# consumer.

## Target shape

For `class C { val p: T ; var q: U }` (no `@ClrField`):
- private backing fields `<p>__bf : T`, `<q>__bf : U` (renamed so they don't collide with the property name).
- `public T get_p()` { ldarg.0; ldfld <p>__bf; ret } ; `public U get_q()` ; `public void set_q(U)`.
- CLR properties `p` (get only) and `q` (get+set) via `PropertyBuilder`.
- accessors are `virtual final` when the property implements an interface/override (binds the slot — see the
  `overridesIface` method/accessor fix already landed for the method side), else non-virtual.

`@ClrField val p: T` -> `public T p` field, no accessor/property (today's path).

## Implementation phases (each ends with the full verify suite green)

1. **ilemit `PropertyBuilder`.** Add a `properties` list to the type metadata (name, type, getMethod, setMethod?). In
   the method-baking pass, after methods exist, `tb.DefineProperty(name, PropertyAttributes.None, type, null)` +
   `SetGetMethod`/`SetSetMethod`. Make the backing field private + renamed. (Additive — no access-site change yet.)
2. **kotc emit accessors + property metadata for ALL properties** (not a public field), backing field private/renamed,
   UNLESS `@ClrField`. Reuses `accessorMethod` (already emits `return field` / virtual-on-override). Emit the
   `properties` list.
3. **Access-site routing (the broad, risky step).** A property read/write -> accessor call (`clrPropGet`/`clrPropSet`)
   instead of `{"k":"field"}`. Touches every property access in every program — run m0 / verify-native-cir-ilemit /
   verify-roundtrip / verify-il. (Within the owner, a self read MAY stay backing-field for perf — optional.)
4. **`@ClrField` annotation.** New `kotlin`-package annotation (or `@Clr`-family). When present, property emits as the
   plain field (phase-0 behavior). Round-trips via metadata so a consumer also sees a field.
5. **byref lowering.** `__clrref`/`__clrout` of an own-source-set property -> `ldflda <backing>` (the BIR already has a
   byref family; point it at the backing field for in-module properties). Diagnose cross-assembly byref-of-property.

## Risks

- **Phase 3 is the regression surface** — every property access changes. Do it under the full verify suite; expect to
  fix differential/round-trip fallout (e.g. a consumer reading `obj.p` must now call `get_p`).
- **Backing-field naming**: a CLR field and property *can* share a name across metadata tables, but rename the backing
  field (`<p>__bf` / the C# `<p>k__BackingField` convention) to avoid consumer confusion and any emit-time clash.
- **Perf**: trivial accessors are JIT-inlined, so the field->accessor change is effectively free at runtime.
- **`data class` / `equals`/`hashCode`/`copy`**: these read properties; once routed through accessors they still work,
  but verify the generated members.
- Interaction with the existing field-based paths (lateinit, ref-cells, delegated properties, companion/top-level
  props): each currently emits fields/accessors specially — audit they still hold (or also become properties).

## Why now

The immediate trigger is the load-error grind (`get_start`/`get_endInclusive` no-impl = interface-property binding).
Phase 2 alone fixes that whole class uniformly (no per-case special-casing), and the full model is the interop-correct
end state. Relationship to the metadata attrs: `@ClrField` joins the `[Kotlin*]` round-trip family
([design-kotlin-metadata-attributes.md](design-kotlin-metadata-attributes.md)).
