# verify-schema self-tests

The schema gate validates whatever BIR/CIR is on disk, so a check that silently stopped checking would look
exactly like a clean corpus. These synthetic documents pin the checks that have no natural negative in the
emitted corpus — today the §2.7 **nesting rule** (`plan_scope`), whose whole job is to catch a shape the
compiler is not supposed to produce.

- `reject-*.bir.json` — the validator MUST reject each, and its message must contain the file's expected
  substring (`reject-<name>.expected`).
- `accept-*.bir.json` — the validator MUST accept each. Without these a validator that rejected everything
  would pass the negative half.

`tests/ir/run-schema.sh` runs them before the real corpus.
