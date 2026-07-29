# Emitted IR corpus tests

The runners select freshly emitted BIR/CIR documents from the standard library and NUnit projects. The
generic validators remain in `scripts/verify-schema.py` and `scripts/verify-sanity.py`; only corpus selection
and test verdicts live here.

Two lanes instead need documents the corpus does not contain, and carry their own:

- `selftest/` (`run-schema.sh`) — shapes the VALIDATOR must refuse, and the legitimate shape next door it must
  accept. Without them a validator that silently stopped checking would look exactly like a clean corpus.
- `lowering/` (`run-lowering.sh`) — synthetic BIR fed straight to bir2cir, asserted against the emitted CIR.
  For a lowering RULE with no natural instance left in the corpus, typically because the producer was fixed so
  the shape it guards no longer reaches it. A rule with no witness quietly stops being a rule.
