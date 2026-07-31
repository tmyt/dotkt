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

`run-nunit-tests.sh` builds all categorized projects against the locally packed SDK, runs `dotnet test`, and then
runs `run-ilverify.sh` once for every DotKt-emitted assembly. A project build failure, test failure, discovery
failure (including a zero-test TRX), or ILVerify finding outside the narrow baseline fails the gate. Builds are
non-incremental because the gate deliberately repacks and reuses the same local SDK version.

```bash
make pack
bash tests/run-nunit-tests.sh
```

The canonical repository entry point is `make verify-tests`, which packs the current SDK before invoking the
runner. `nuget.config` uses an isolated cache and the local `build/nuget-feed`, so a same-version repack cannot be
masked by a previously extracted SDK package.

`run-ilverify.sh` contains the machine-readable `ILVERIFY_XFAIL` baseline. Baselines are keyed to the narrowest
emitted type or fixture method possible and require a tracking reason; a newly clean result must be pruned.
