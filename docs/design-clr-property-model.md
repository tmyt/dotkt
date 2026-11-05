# CLR representation of Kotlin properties

An accessor-routed Kotlin property is a CLR Property row with typed getter and/or setter methods.
C#, F#, and reflection consumers bind through that row. A separate backing field, where needed,
is implementation storage rather than the public property. For a normal backed property, bir2cir
chooses the compiler-generated `<Name>k__BackingField` name; a distinct name avoids collisions
in reflection-driven libraries. The accessor names need not follow the C# `get_Name` and
`set_Name` spelling. See [the property contract](dotkt-semantics.md#5h-an-auto-propertys-backing-field-is-a-compiler-generated-namek__backingfield-228).

## Fields and initialization

- `kotlin.clr.ClrField` opts a supported declaration into a CLR field instead of a property.
  The annotation is identified by its fully qualified name. Kotlin constants also have a
  field representation; delegate storage is not a public property surface.
- `lateinit var` is a typed property, not a CLR field. Its private backing slot stores
  `null` until initialized and boxes CLR value types when assigned. The getter checks
  initialization; Kotlin and C# property writes go through the typed setter.
  `@ClrField lateinit` is a compile error.
- A normal property is not a C# `ref`/`out` storage location. For supported same-compilation
  Kotlin backing-field arguments, the compiler addresses the actual field, using a generated
  access bridge if required by CLR visibility. Ordinary cross-assembly property access and
  `lateinit` do not expose a native backing-field address. Use an actual field or a documented
  [live ref-return delegate](user/using-dotnet-from-kotlin.md#4-out--ref-parameters--byref)
  where the API needs live native storage.

## Ownership and checks

`kotc` carries the selected Kotlin property and backing-field facts in BIR.
`bir2cir` resolves storage, accessor and owner identities and chooses the physical CLR shape.
`ilemit` encodes the resolved fields, methods and property associations from CIR.

The interop fixtures cover [backing-field shape](../tests/interop/consumer/fixtures/AutoPropertyBackingFieldTests.kt)
and [by-reference arguments](../tests/interop/consumer/fixtures/ByRefParameterTests.kt).
Round-trip fixtures cover [C# lateinit access](../tests/roundtrip/bidirectional/consumer/LateinitPropertyTests.cs)
and [Kotlin lateinit access](../tests/roundtrip/consumer/LateinitStorageTests.kt).
