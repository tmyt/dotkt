# Documentation

The documentation tree contains current user guidance, normative contracts, and active design records. Completed plans, point-in-time reviews, and superseded audits live in Git history instead of the working tree.

## Start here

| Document | Audience |
|---|---|
| [user/getting-started.md](user/getting-started.md) | Install the SDK, create a project, build, and run it |
| [user/using-dotnet-from-kotlin.md](user/using-dotnet-from-kotlin.md) | Call .NET APIs, events, delegates, and by-reference parameters |
| [user/kotlin-on-clr-differences.md](user/kotlin-on-clr-differences.md) | Understand the most visible differences from Kotlin/JVM |
| [user/supported-features.md](user/supported-features.md) | Scan supported and not-yet-supported features |

## Canonical references

| Document | Role |
|---|---|
| [architecture.md](architecture.md) | Compiler layers, artifact split, and binding invariants |
| [dotkt-semantics.md](dotkt-semantics.md) | Complete Kotlin-to-CLR behavior and deliberate deviations |
| [bir-cir-spec.md](bir-cir-spec.md) | Normative BIR/CIR serialization contract |
| [bir-cir.schema.json](bir-cir.schema.json) | Machine-readable BIR/CIR schema |
| [design-compiler-modes.md](design-compiler-modes.md) | Metadata, runtime, and application build modes |
| [coroutine-abi.md](coroutine-abi.md) | Public `suspend` to `Task<T>` ABI |

## Current design records

These documents describe current implementation decisions or approved work that has not yet been superseded:

- [design-charsequence-clr-string.md](design-charsequence-clr-string.md)
- [design-clr-collection-binding.md](design-clr-collection-binding.md)
- [design-clr-event-model.md](design-clr-event-model.md)
- [design-clr-property-model.md](design-clr-property-model.md)
- [design-coroutine-cold-core-task-bridge.md](design-coroutine-cold-core-task-bridge.md)
- [design-kotlin-metadata-attributes.md](design-kotlin-metadata-attributes.md)
- [design-primitive-dual-representation.md](design-primitive-dual-representation.md)
- [design-ktproj-mpp.md](design-ktproj-mpp.md)
- [design-nunit-test-harness.md](design-nunit-test-harness.md)
- [design-stdlib-compilation.md](design-stdlib-compilation.md)

## Maintainer guide

- [kotlin-frontend-bump-playbook.md](kotlin-frontend-bump-playbook.md)

## Maintenance policy

- GitHub Issues is the only source of truth for bugs and remaining tasks.
- Update canonical references when behavior or architecture changes.
- Delete completed work plans, reviews, generated snapshots, and superseded designs; Git preserves their history.
- Do not create a new archive directory.
