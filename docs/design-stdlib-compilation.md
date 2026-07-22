# Compiling the CLR standard library

Status: current maintainer guide. Historical implementation roadmaps for the stdlib are preserved in Git history.

## Artifacts

The same Kotlin sources under `libraries/stdlib/` produce three distinct artifacts:

| Artifact | Command | Purpose |
|---|---|---|
| Frontend KLIB | `./scripts/build-stdlib-klib.sh` | Kotlin declarations and metadata consumed by kotc |
| Reference assembly | `./scripts/build-stdlib-ref.sh --emit` | Pure Kotlin-facing metadata plus `@Clr*` bindings consumed by bir2cir |
| Runtime assembly | `./scripts/build-stdlib-rt.sh --emit` | Shipping CLR implementation |

The artifact split and layer ownership are defined in [architecture.md](architecture.md).

## Binding model

CLR platform actuals use one of three forms:

1. A direct CLR correspondence uses `@ClrTypeAlias` or `@ClrIntrinsic` metadata.
2. A type with no CLR correspondence has a real Kotlin implementation.
3. A member without a one-to-one CLR operation has a real Kotlin body built from bound primitive members.

bir2cir reads binding metadata from `DotKt.Private.Stdlib.dll` and substitutes the application call or type. kotc does not recognize stdlib symbols, and ilemit does not interpret Kotlin bindings.

## `TODO()` bodies

A `TODO()` body is not by itself evidence of missing work. An `actual` declaration carrying `@ClrIntrinsic`, or enclosed by a bound type, can retain a throwing Kotlin body as metadata filler because application call sites are substituted before runtime.

Classify a declaration by reading its annotations and enclosing type:

- bound annotation plus filler body: implemented binding;
- real Kotlin body: implemented library code;
- declaration-only interface or abstract member: contract;
- unbound `TODO()` with no covering binding: genuine missing implementation.

Do not use raw `TODO` counts as a progress metric. Track real missing behavior in GitHub Issues and add a focused regression test.

## Cardinal rule

If a stdlib function needs different CLR behavior, fix its stdlib declaration, binding metadata, or real Kotlin body. Do not add symbol-specific recognition to kotc or ilemit.

## Verification

Run the three artifact builds for stdlib changes, then the focused NUnit fixtures and `make verify`. Collection, coroutine, metadata, or generic representation changes should also run their corresponding roundtrip or interop suites.
