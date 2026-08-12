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
- `reject-array-rank-one` — `rank` names the multi-dimensional ECMA array; a 1 would be a second spelling of
  the SZ vector, and two spellings of one shape is exactly the ambiguity the key was added to remove.

`accept-memberref` is the other half: one well-formed reference per `kind`, plus the three CIR-only ECMA
signature carriers (`ptr`, `array.rank`, in-position `mod`). Without it, a validator that refused *every*
`memberRef` would pass every fixture above.
