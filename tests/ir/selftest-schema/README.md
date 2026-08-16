# verify-schema self-tests — the CIR half

`scripts/verify-schema.py` fixtures whose subject is **CIR-only**, driven by `tests/ir/run-schema.sh`
alongside `tests/ir/selftest/*.bir.json`. The reject/accept contract, and the reason these files exist at
all, are documented once in [`../selftest/README.md`](../selftest/README.md) — including why the schema
lane's CIR fixtures need a directory of their own instead of sitting beside the sanity lane's.

## What is pinned today

The **#370 scalar `memberRef`** — one complete, already-resolved reference to a member of another assembly.
Every reject fixture removes or corrupts exactly one fact that makes the reference complete, because each of
those facts exists to stop a consumer having to reconstruct it, and reconstruction is member *selection*:

- `reject-memberref-without-assembly` / `-without-return` / `-without-calling-convention` — the three facts
  whose absence would send a consumer searching (every loaded assembly / an inherited slot shadowed only by
  return type / a static and an instance member of the same shape).
- `reject-memberref-field-with-parameters`, `reject-memberref-arity-on-field` — a field has no signature and
  no generic parameters of its own; carrying either means the producer guessed the member's shape.
- `reject-memberref-ctor-misnamed` — a constructor is `.ctor`, instance, and void.
- `reject-memberref-declaring-type-variable` — a type variable declares nothing.
- `reject-memberref-stray-carrier-key` — a resolved identity under an unregistered key, i.e. a second
  member-identity vocabulary growing beside the scalar one. `declaringType` is the discriminator that
  catches it wherever it is smuggled in.
- `reject-bir2cir-internal-member-fact` — a pass-to-pass resolution fact leaked into serialized CIR. Internal
  matching inputs may exist while bir2cir is resolving a declaration, but neither BIR nor CIR may expose them.
- `reject-external-base-ctor-without-ref` / `reject-clr-override-instruction-without-ref` — declaration-side
  external operands remain mandatory after the transitional owner/signature descriptors are retired. The base
  type plus compilation-local type set identifies an external constructor delegation; an explicit MethodImpl has
  a separate durable instruction, and neither may reach ilemit without its exact scalar operand.
- `reject-array-rank-one` — `rank` names the multi-dimensional ECMA array; a 1 would be a second spelling of
  the SZ vector, and two spellings of one shape is exactly the ambiguity the key was added to remove.

`accept-memberref` is the other half: one well-formed reference per `kind`, plus the three CIR-only ECMA
signature carriers (`ptr`, `array.rank`, in-position `mod`). Without it, a validator that refused *every*
`memberRef` would pass every fixture above.
