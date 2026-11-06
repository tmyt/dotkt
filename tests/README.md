# Categorized NUnit compiler tests

The compiler behavior tests are grouped by the contract they exercise, rather than by the old shell-case or
backend batch that happened to create them:

- `basic/` — Kotlin language and standard-library behavior with no CLR-specific dependency.
- `interop/` — BCL, C# producer, PackageReference, delegate, event, by-ref, and other CLR interop behavior.
- `coroutines/` — suspend lowering, continuations, coroutine context, sequences, and Task/ValueTask bridges.
- `roundtrip/` — emitted Kotlin metadata consumed by another Kotlin or C# project through a real project reference.
- `support/` — shared test-only projects, currently the coroutine driver used by coroutine and interop fixtures.
- `special/` — valid compiler tests whose required build shape does not fit an NUnit method (for example a
  greater-than-16-parameter delegate assembly-shape check).

Fixture files and classes use feature names. Historical migration batches such as `MigratedM2`, `CorA`, and the
old undifferentiated `il` suite are not categories.

External-process fixtures follow the same subject grouping. Multiple verdicts must share the smallest valid
compilation graph; use separate runtime processes when crashes need independent attribution rather than compiling
one program per assertion.

`run-nunit-tests.sh` builds all categorized projects against the locally packed SDK, runs `dotnet test`, and then
runs `run-ilverify.sh` once for every DotKt-emitted assembly. A project build failure, test failure, discovery
failure (including a zero-test TRX), or ILVerify finding outside the narrow baseline fails the gate. Before any
build, every suite graph is restored and transitively cleaned against one fresh SDK extraction. Independent
consumer builds then reuse unchanged shared producers within that cohort; a same-version SDK repack starts a new
clean cohort so previous compiler outputs cannot survive.

```bash
make pack
bash tests/run-nunit-tests.sh
```

`make verify-nunit` packs the current SDK and invokes the NUnit runner. `make verify-tests` adds
the isolated negative compilation lane, and `make verify-test-corpus` runs schema and sanity over
the freshly emitted NUnit corpus. `nuget.config` uses an isolated cache and the local
`build/nuget-feed`, so a same-version repack cannot be masked by a previously extracted package.

`run-ilverify.sh` keeps real runtime-safe compiler defects in the machine-readable `ILVERIFY_XFAIL` baseline and
intentional ECMA-335 unverifiability in `ILVERIFY_UNVERIFIABLE`. The latter accepts only ILVerify's
`[Unverifiable]` finding kind, so its method keys cannot mask another verification error. Both baselines are keyed to
the narrowest emitted type or fixture method possible, require a reason, and turn a newly clean result into a red
dead-key verdict until it is pruned.
