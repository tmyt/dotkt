# Project Principles

The compiler pipeline must preserve strict ownership of meaning. Each layer must
operate only on the facts assigned to it and must not infer semantics owned by
another layer.

- `kotc` must project Kotlin IR into BIR using Kotlin vocabulary and semantics.
  It must not decide the CLR representation of those semantics.
- `bir2cir` must resolve Kotlin semantics into their concrete CLR physical
  representation.
- `ilemit` must emit CIR to CIL one-to-one. It must not re-resolve overloads,
  reconstruct Kotlin semantics, or infer the standard-library ABI.
- Stripping method bodies from reference assemblies must not strip declaration
  signatures, generic constraints, or Kotlin metadata.
- The common source layers of the standard library, coroutines, and atomicfu
  must remain aligned with their upstream projects.
- Internal compatibility may be broken deliberately when doing so enables a
  correct design without breaking Kotlin source compatibility.
- Fixes must express general rules that produce valid CLR binaries from
  arbitrary Kotlin source. Do not introduce local special cases tied to a
  particular library or function name.
