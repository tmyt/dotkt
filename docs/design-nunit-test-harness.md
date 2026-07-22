# NUnit test architecture

The compiler regression suite is organized as subject-oriented NUnit projects. The former one-directory-per-case shell corpus has been retired.

## Test topology

| Path | Scope |
|---|---|
| `tests/basic/` | Kotlin language and stdlib behavior that needs no external CLR producer |
| `tests/interop/` | .NET producer/consumer graphs and CLR metadata behavior |
| `tests/coroutines/` | Suspend lowering, task/awaiter interop, continuations, and sequence builders |
| `tests/roundtrip/` | DotKt library production and consume-as-Kotlin metadata restoration |
| `tests/ir/` | BIR/CIR schema and sanity validation |
| `tests/msbuild/` | Stateful MSBuild behavior that cannot be expressed inside one test process |
| `tests/packaged-sdk/` | Local-feed restore and packaged SDK scenarios |
| `tests/roundtrip/scenarios/` | Irreducible external-process roundtrip cases |
| `tests/special/` | Specialized synthetic or platform-sensitive gates |
| `tests/support/` | Shared Kotlin test support |

`tests/run-nunit-tests.sh` drives the Basic, Interop, Coroutines, and Roundtrip projects. `make verify` is the aggregate repository gate.

## Design rules

1. Group related assertions into a typed fixture battery instead of creating a new project or process.
2. Assert values and exceptions directly with NUnit; do not encode behavior as stdout snapshots when a typed assertion can express it.
3. Keep tests near the subsystem they exercise. Cross-module and CLR-producer cases belong in Interop or Roundtrip, not Basic.
4. Use an external shell scenario only when the behavior inherently depends on a separate build, restore, process, filesystem state, or generated package.
5. Every regression keeps a permanent focused assertion after its issue is fixed.
6. Test projects consume the locally built DotKt packages through `tests/nuget.config`; they must not silently fall back to a published SDK.
7. Shared infrastructure belongs under `tests/support/`; fixture-specific helpers stay with their fixture.

## Adding a regression

1. Select the existing subject fixture that owns the behavior.
2. Add the smallest source shape that reaches the faulty compiler path.
3. Assert both the successful result and the important negative or boundary case.
4. For an IL-shape defect, ensure the relevant project participates in ILVerify.
5. Run the focused project or `tests/run-nunit-tests.sh`, then `make verify` for cross-layer changes.

Do not recreate the retired `cases/` hierarchy or a per-case compiler runner.
