# C# 14 static extension-member ABI oracle

This fixture pins the released .NET 10 / C# 14 metadata representation that #389 consumes and emits. The producer
contains methods, properties, overloads, separated receiver containers, and a constrained generic receiver. The
inspector verifies the exact type and method flags, the nested grouping/marker graph, `ExtensionMarkerAttribute`
links, generic constraints, signature-only `ldnull; throw` bodies, and consumer call targets.

The implementation methods are ordinary source-named statics on the top-level container. In particular, property
implementation accessors are **not** `specialname`; only their declarations in the grouping type are accessors of
actual Property rows. Executable calls must target the top-level implementations and never the grouping stubs.

The negative fixture pins Roslyn's `CS0111` rule for identical receiverless implementation signatures in one
container. This is why DotKt must partition companion extensions by associated receiver rather than placing every
implementation on one file facade.

The Increment 1 interop lane consumes this producer through `dll2klib`, calls every method/property shape from
Kotlin, and inspects the emitted Kotlin consumer IL to prove that calls target the top-level implementation methods,
never the nested signature stubs. `run-malformed.sh` emits raw ECMA-335 graphs and requires both `dll2klib` and
`bir2cir` to reject missing marker types, zero or duplicate implementation matches, callable declaration stubs, and
callable marker stubs. Discovery starts from the standard attributes and their string edges; the `<G>$` and `<M>$`
spellings are validated only after that graph exists and are never inference keys.
