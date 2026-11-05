# Emitted IR corpus tests

The runners select freshly emitted BIR/CIR documents from the standard library and NUnit projects. The
generic validators remain in `scripts/verify-schema.py` and `scripts/verify-sanity.py`; only corpus selection
and test verdicts live here.

Run `make verify-test-corpus` to emit a fresh NUnit corpus, then check schema and sanity. The normal
compiler output for these checks is BIR/CIR, not a full Kotlin IR text dump.

The runners also use explicit witnesses for shapes a current producer will not emit:

- `selftest/` supplies BIR shape checks to `run-schema.sh` and CIR semantic checks to `run-sanity.sh`.
- `selftest-schema/` supplies CIR shape checks to `run-schema.sh`. Accept and reject controls keep
  a broken validator from passing on a clean generated corpus.
- `lowering/` (`make verify-lowering`) supplies BIR directly to bir2cir and asserts the lowering
  result or rejection, independently of the current frontend.
