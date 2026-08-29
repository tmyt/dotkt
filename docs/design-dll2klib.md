# DLL-to-KLIB dll2klib

## Status

This document defines the production design of the DotKt CLR reference-assembly
projection pipeline.

`dll2klib` converts each MSBuild-resolved CLR reference assembly into one
metadata-only Kotlin library. The generated KLIBs are ordinary Kotlin 2.4.10
KLIBs and are supplied to the standard `kotc` KLIB loader.

The design has four primary goals:

1. make every public CLR declaration available to Kotlin name and overload
   resolution, regardless of which declarations appear in source imports;
2. preserve Kotlin vocabulary already recorded in DotKt-produced CLR
   assemblies;
3. keep CLR physical representation decisions in `bir2cir`; and
4. make dll2klib parallel and incrementally cacheable per project.

## Pipeline

```text
                    MSBuild-resolved compile references
                                  |
                                  v
                    dll2klib batch coordinator
                    bounded in-process conversion
                       |       |       |
                       v       v       v
                    one metadata-only KLIB per reference assembly
                                  |
frontend stdlib KLIB ------------+----------------------+
                                                         |
                                                         v
                                                   kotc frontend
                                                         |
                                                        BIR
                                                         |
                          resolved compile references --> bir2cir
                                                         |
                                                        CIR
                                                         |
                          resolved runtime references --> ilemit
                                                         |
                                                   CLR assembly
```

MSBuild is the authority for reference selection, conflict resolution, and the
compile/runtime reference split. No DotKt tool reconstructs the dependency
graph by probing adjacent files.

### Ownership of meaning

| Component | Owns | Must not own |
| --- | --- | --- |
| `dll2klib` | ECMA-335 metadata reading and projection of declarations into Kotlin vocabulary | call binding, CLR call shape, code generation |
| `kotc` | Kotlin parsing, type checking, overload resolution, and Kotlin IR-to-BIR projection | CLR member resolution or physical ABI decisions |
| `bir2cir` | binding BIR declarations to CLR metadata and choosing their concrete CLR representation | frontend declaration discovery |
| `ilemit` | one-to-one CIR-to-CIL emission | overload resolution, Kotlin semantic reconstruction, or standard-library ABI inference |

The reference KLIB is a frontend declaration surface. It is not a second CLR
binding database. CLR-specific facts are carried only when Kotlin metadata has
no equivalent and a later layer cannot recover the fact unambiguously.

## Inputs and outputs

The production input set is `@(ReferencePathWithRefAssemblies)`, resolved by
MSBuild. `@(ReferencePath)` is used only as a compatibility fallback when the
former item set is unavailable.

For every input:

```text
<reference-directory>/<AssemblyName>.dll
    ->
    $(IntermediateOutputPath)klib/<AssemblyName>.klib
```

The output is project-local. A shared cache is deliberately not used because
the projection depends on the complete resolved reference universe as well as
the individual DLL.

An input is expected to be a managed PE containing valid ECMA-335 metadata.
Method bodies are neither read nor required. Reference assemblies must retain
declaration signatures, generic constraints, custom attributes used by DotKt,
and nullable metadata.

## KLIB representation

Each output is a packed ZIP-format KLIB with the following minimum layout:

```text
default/manifest
default/linkdata/module
default/linkdata/package_<fq-name>/0_<short-name>.knm
```

- `default/manifest` is a properties file containing the Kotlin ABI, compiler,
  metadata, IR signature, and unique-name fields.
- `default/linkdata/module` is a serialized
  `KlibMetadataProtoBuf.Header`.
- Each `.knm` file is a serialized `ProtoBuf.PackageFragment`.
- KLIB extension fields 171 through 174 provide package and class discovery
  metadata.
- No IR implementation payload is emitted.
- No JSON resource is embedded or consumed by `kotc`.

The C# protobuf definitions are wire-compatible subsets of the Kotlin 2.4.0
schemas in:

- `upstream/core/metadata/src/metadata.proto`
- `upstream/compiler/util-klib/src/KlibMetadataProtoBuf.proto`

KLIB extension fields are represented as ordinary C# protobuf fields with the
same wire numbers. Kotlin's registered extension parser therefore reads the
result as standard KLIB metadata.

The module identity is derived from the CLR assembly identity and MVID. The manifest records that identity as the
ordinary `unique_name`; the protobuf header records the corresponding Kotlin special name (`module_name=<...>`), as
required by the standard Kotlin metadata loader.
Projection of the same input and projection catalog is deterministic; the E2E
test verifies byte-identical output.

### Dependencies

Generated manifests and headers do not contain assembly dependencies.
MSBuild already supplies the complete resolved KLIB set on the frontend
classpath, so duplicating that graph would create a second and potentially
inconsistent resolution authority.

A declaration may refer to a classifier from another generated KLIB. The
standard KLIB loader resolves that classifier from the complete classpath.

## Projection rules

### Declaration surface

`dll2klib` projects the following public or protected CLR surface:

- top-level and nested classes, interfaces, structs, enums, and delegates;
- constructors, instance methods, and static methods;
- properties, indexers, events, and fields;
- generic type and method parameters and their constraints;
- base classes and implemented interfaces;
- explicit interface implementations represented by `MethodImpl`;
- CLR operators and C# extension methods; and
- enum entries and literal constants.

Projection failure for a public top-level or nested type is fatal. A conversion
must not publish a partially projected KLIB.

### Names and packages

CLR namespaces become Kotlin packages. Nested CLR types become nested Kotlin
class declarations.

CLR permits classifiers such as `Task` and `Task<T>` to share a source name;
Kotlin metadata does not. The batch coordinator computes generic-arity name
collisions from the complete reference set and gives every conversion the same
stable naming catalog. A classifier is renamed only where the collision
requires it.

Different input assemblies that map to the same output KLIB filename are a
hard error.

### Types and nullability

Primitive, class, array, generic, by-reference, function, extension-function,
context-function, and suspend-function types are projected into Kotlin
metadata types.

C# nullable metadata is applied over the complete type tree:

- `NullableAttribute` and `NullableContextAttribute` determine declared
  reference-type nullability;
- `MaybeNull` and `NotNull` contribute return flow information; and
- an exact DotKt Kotlin type carrier takes precedence when present.

An oblivious CLR reference type is encoded as a standard flexible Kotlin type.
For example, `System.String` becomes `String..String?`:

- the lower bound is the non-null classifier;
- `flexible_upper_bound` contains the nullable form of the same type; and
- `flexible_type_capabilities_id` is `dotkt.clr.PlatformType`.

The Kotlin 2.4.10 FIR KLIB loader reconstructs this as a flexible type, giving
the frontend normal platform-type behavior and presenting it as `String!` to
frontend clients such as completion.

### Kotlin vocabulary from DotKt assemblies

DotKt-produced assemblies already contain Kotlin facts as ECMA-335 custom
attributes. `dll2klib` reads those attributes with
`System.Reflection.Metadata` and maps them to standard KLIB declarations,
types, and flags.

The projection includes:

- file facades and top-level declarations;
- member and top-level extension declarations;
- operator, infix, suspend, inline, and default-parameter flags;
- object, sealed, fun-interface, and value-class flags;
- value-class underlying-property name and type;
- exact Kotlin type and nullable-generic carriers;
- Kotlin collection identity;
- context parameters; and
- extension, context, and suspend function types.

Arbitrary applications of arbitrary CLR `CustomAttribute`s are not reproduced
as KLIB annotations. Annotation classes may themselves be projected, but
general attribute-application round-tripping is outside this interop contract.

### Static members and companions

CLR static members are emitted directly in the containing class with the
standard Kotlin 2.4.0 `IS_STATIC_FUNCTION` or `IS_STATIC_PROPERTY` flag. They
have no dispatch receiver.

A conventional C# `[Extension]` MethodDef has two source meanings, but they are
not projected as two Kotlin extensions. Its declaring-class view remains an
ordinary static function whose leading receiver is a value parameter. A single
top-level extension view is emitted in the CLR namespace package, with that
leading parameter promoted to `receiver_type` and `ClrExternal` naming the
physical static owner. `dll2klib` does not create a second package named after
the static container: that would give the same CLR method both a class callable
and a package callable under one qualified name, duplicating completion and
overload surfaces. Thus Kotlin can use either `Extensions.method(value, ...)`
or the namespace-imported `value.method(...)`, while each form has one metadata
declaration.

A real Kotlin companion is a different shape: it is a nested
`Class.Kind.COMPANION_OBJECT`, is named by the containing class's
`companion_object_name`, and its members have a companion-instance dispatch
receiver. `dll2klib` does not synthesize a companion to represent CLR statics.

The embedded CLR compiler enables `CompanionBlocksAndExtensions` as a platform
capability so the standard KLIB loader accepts static class members. Every CLR
analysis host, including an IDE/LSP integration, must install the same target
language-version settings; enabling it only in a command-line wrapper would make
editor analysis disagree with compilation. When the
frontend produces a fake-override accessor for a static property, the BIR
emitter resolves it to the underlying declaration and uses the IR declaration
shape—not the number of call arguments—to preserve static ownership.

A Kotlin 2.4 source `companion { }` member is the same shape from the other
direction: it is emitted as a static member of its own CLR type, so it needs no
carrier and is read back by the ordinary static-member path above.

A Kotlin 2.4 companion extension — `companion fun C.foo()` or `companion val C.foo` — has no receiver
parameter at all: the frontend drops it. `bir2cir` emits the released C# 14 static extension-member graph: source-named
executable methods on a receiver-partitioned top-level container plus nested
signature, property, and receiver-marker declarations. Calls target the executable methods.
Field-backed `val`/`var` declarations become ordinary `get_`/`set_` implementation methods over private
storage on the source file facade; keeping all storage there preserves its single type-initializer order across
receiver-partitioned C# extension containers.
same-module Kotlin code is rewritten through those methods too, so the Property row has one executable meaning.
`dll2klib` restores the source name and standard Kotlin shape from that attributed
graph — the static-declaration flag plus a receiver type, which is exactly what
`isStatic && receiverParameter != null` means. No generated-name inference is
involved. For a generic associated classifier, the standard source-named executable method carries the receiver
block's CLR method parameters for C# inference and forwards to an unspeakable receiverless Kotlin core. A trusted
`[KotlinExtensionCore]` edge records only that wrapper-to-core relationship; the standard graph remains the sole
source of the receiver, source name, and declaration kind. `dll2klib` projects the core and erases the wrapper-only
receiver block so the Kotlin classifier remains bare. Suspend functions and context-parameter properties remain on
the trusted `[KotlinCompanionExtension]` carrier during the staged migration.

An ordinary C# 14 static extension member is imported into that same standard
Kotlin shape from its released metadata graph. `dll2klib` starts from
`ExtensionAttribute` plus the `ExtensionMarkerAttribute` string edges, validates
the nested grouping/receiver-marker declarations, and pairs every signature-only
declaration with exactly one source-named executable method on the top-level
container. The executable method owns the projected signature and CLR binding;
the nested declaration only supplies the associated receiver and source member
shape. Group/marker spellings and throwing bodies are validation details after
the attributed graph is established, never discovery heuristics. Malformed or
ambiguous graphs fail rather than falling back to ordinary members.

### Properties and fields

CLR properties use normal Kotlin property/accessor metadata. DotKt custom
accessors are represented by the standard accessor `IS_NOT_DEFAULT` flag.

A plain CLR `FieldDef` must remain distinguishable from a property because the
eventual CIL operation is `ldfld`/`stfld`, not an accessor call. Standard KLIB
has no declaration kind for a raw CLR field, so its projected Kotlin property
carries the binary-retained `kotlin.clr.ClrField` annotation.

`kotc` translates that marker into the existing BIR field-access form.
`bir2cir` resolves the actual `FieldDef`, and `ilemit` emits the CIR operation
without reinterpreting the marker.

Literal fields use the standard Kotlin `IS_CONST`, `HAS_CONSTANT`, and
`compile_time_value` metadata where representable.

### Physical CLR owners

Kotlin metadata cannot express the physical CLR owner of every declaration,
most notably a top-level declaration stored in a DotKt file-facade class.
Declarations that require this identity carry:

```kotlin
@kotlin.clr.ClrExternal(
    owner = "<projected Kotlin owner>",
    physicalOwner = "<exact CLR TypeDef name>",
)
```

`owner` is the Kotlin-facing classifier identity, including any deterministic
arity-clash rename used by the projected KLIB. `physicalOwner`
retains the metadata nesting separator and every segment's own generic arity
(for example, ``Namespace.Outer`1+Leaf`1``). The latter is required because a
flattened Kotlin argument vector cannot distinguish that legal CLR declaration
from ``Namespace.Outer+Leaf`2``. Both arguments are required current-format
facts; older compiler artifacts are not inferred or accepted through a fallback.

The standard KLIB loader restores this as an ordinary IR annotation. `kotc`
consumes it while emitting BIR:

- class and instance-member references receive `ownerType`; and
- top-level, static, and inline references receive `owner`.

The annotation is not propagated beyond BIR. `kotc` requires both facts to
recognize the current-format external declaration and transports
`physicalOwner` in the existing owner/type fields; `bir2cir` uses that exact
identity with the MSBuild-resolved reference assemblies while its semantic
indexes remain keyed in Kotlin vocabulary.

### Delegates

CLR delegates are exposed as Kotlin function types when their `Invoke`
signature is known. This covers built-in `Func`/`Action`, delegates declared
in the current assembly, and delegates declared in another resolved assembly.

A recursive CLR delegate graph cannot be represented as a finite Kotlin
function type. `dll2klib` therefore rejects self-recursive and mutually
recursive `Invoke` signatures, including generic and cross-assembly cycles,
instead of truncating the graph to an order-dependent Kotlin type. The active
expansion path is keyed by resolved definition path and TypeDef row, so
reopening a defining assembly through the resolved catalog cannot evade the
cycle check and producer-chosen MVID values cannot alias distinct inputs.

The batch coordinator builds a compact delegate catalog from the complete
reference set. A conversion consults the defining assembly metadata only when a
referenced delegate requires it. Cross-assembly delegate definitions therefore
influence incremental staleness without becoming KLIB manifest dependencies.

High-arity delegate ABI beyond the upstream Kotlin function-arity limit is not
defined by this design. It is tracked separately in issue #220.

### Default arguments

The reference KLIB marks a parameter with standard
`DECLARES_DEFAULT_VALUE` metadata so Kotlin overload resolution permits its
omission. It does not copy the default expression into BIR.

After the frontend has selected a declaration, `bir2cir` reads the
authoritative default from the referenced DLL:

1. a DotKt `KotlinDefault` carrier, if present; otherwise
2. an ECMA-335 constant default.

This preserves Kotlin's call semantics while keeping declaration selection in
the frontend and CLR metadata binding in `bir2cir`. `ilemit` never invents or
trails omitted arguments.

### Synthesized Kotlin surface

Some CLR protocols have direct Kotlin language equivalents and are projected
as additional metadata-only declarations:

- a conforming `GetEnumerator` pattern produces `operator fun iterator()`;
- a conforming `GetAwaiter` pattern produces a suspend `await()` declaration;
  the declaration carries `kotlin.clr.ClrAwaitBridge` so `bir2cir` can lower
  the await protocol; and
- CLR exception classes rooted at `System.Exception` receive the logical
  Kotlin supertype `kotlin.Throwable`, allowing them in Kotlin `catch` clauses.

These are general signature-based rules. They do not depend on a particular
library or function implementation.

## CLI and process model

`dll2klib` accepts the complete resolved reference universe in batch mode:

```text
dll2klib --out <directory> [--jobs <N>] @<references.rsp>
```

A two-path standalone invocation is rejected because one DLL cannot resolve either the `Invoke` shape of delegates
or the trusted semantic identity of companion carriers defined in referenced assemblies.

Batch mode:

1. reads and normalizes the response-file inputs;
2. computes the naming, delegate, inner, companion, and public-type catalogs from the complete set;
3. rejects output-name collisions;
4. compares the current and last successful per-input MVID/direct-TypeRef graphs, adding definitions and users of any
   changed whole-universe arity key as invalidation roots;
5. selects only changed roots and their current or former reverse dependents;
6. converts those assemblies concurrently in process, bounded by `--jobs`; and
7. stages every KLIB and publishes the batch state only after every conversion succeeds.

`--jobs 0` means one concurrent conversion per stale input. The normal MSBuild
default is the processor count.

Each KLIB is first written beneath a unique batch staging directory. No final KLIB is replaced until every conversion
succeeds. Publication writes an incomplete marker before moving staged files; if publication is interrupted, the next
run treats the cache as cold and repairs every output before trusting the new graph state.

## Standard-library handling

The CLR reference and runtime forms of the DotKt standard library are stamped:

```text
AssemblyMetadata("DotKt.LibraryKind", "stdlib")
```

The authoritative frontend surface is the standard-library KLIB, so these CLR
assemblies must not also be projected as reference KLIBs.

- Direct CLI mode reports a warning and ignores a marked stdlib assembly.
- Batch/response-file mode silently removes marked stdlib assemblies because
  they are expected members of an MSBuild-resolved reference set.
- The shipping MSBuild target also filters the known
  `DotKt.Private.Stdlib` and `DotKt.Stdlib` filenames before invoking the tool.

## MSBuild integration

The shipping target sequence is:

```text
ResolveReferences
  -> DotKtResolveReferenceSets
  -> DotKtWriteReferenceKlibRsp
  -> DotKtGenerateReferenceKlibs
  -> KotlinCompile
```

`DotKtResolveReferenceSets` captures the complete compile, runtime, and
reference-KLIB input universes. `DotKtWriteReferenceKlibRsp` uses
`WriteOnlyWhenDifferent` so adding or removing a reference changes a stable
input file without causing timestamp churn on no-op builds.

`DotKtGenerateReferenceKlibs` is an MSBuild incremental target:

```xml
<Target Name="DotKtGenerateReferenceKlibs"
        DependsOnTargets="DotKtWriteReferenceKlibRsp"
        Inputs="@(_DotKtKlibReference);$(DotKtDll2Klib);$(DotKtReferenceKlibResponseFile)"
        Outputs="@(_DotKtKlibReference->'%(KlibPath)')">
  <Exec Command="dotnet &quot;$(DotKtDll2Klib)&quot;
                 --out &quot;$(DotKtReferenceKlibPath)&quot;
                 --jobs &quot;$(DotKtDll2KlibJobs)&quot;
                 @&quot;$(DotKtReferenceKlibResponseFile)&quot;" />
</Target>
```

The batch coordinator repeats the per-output staleness check, allowing a
partially incremental target invocation to regenerate only changed
assemblies.

`KotlinCompile` places the frontend stdlib KLIB and every projected reference
KLIB on the normal `kotc -classpath`. The classpath uses
`System.IO.Path.PathSeparator`, so it is `:` on Unix-like systems and `;` on
Windows.

## Incrementality

An output KLIB is stale when any of the following is true:

- it does not exist;
- its input DLL is newer;
- the `dll2klib` executable is newer;
- a defining assembly used for a cross-assembly delegate is newer; or
- the complete-set projection catalog changed.

The projection catalog records the generic-arity collision set, delegate, and companion
definitions. A catalog change invalidates all KLIBs because it can change the
meaning or name of a declaration even when a particular input DLL is
unchanged.

KLIB manifests intentionally contain no dependency fingerprint. Incremental
ownership remains with the project-local coordinator and MSBuild rather than
being duplicated into frontend metadata.

## Failure policy

Dll2klib fails the build when:

- an input is not a valid managed metadata image;
- a public top-level or nested declaration cannot be projected;
- two assemblies map to the same output filename;
- an assembly conversion fails; or
- the output KLIB cannot be written atomically.

The tool must not catch a declaration-level error, print a warning, and
continue with an incomplete frontend universe. Selective omission would make
compile success depend on imports or source traversal order, which this design
exists to eliminate.

## Compatibility and current limits

The KLIB metadata wire format is a Kotlin compiler-internal format.
`dll2klib` is therefore pinned to the Kotlin 2.4.0 ABI/metadata schema and the
Kotlin 2.4.10 compiler version. Kotlin patch releases need not advance all
three manifest versions together, so the generated manifest must match the
frontend-produced KLIB exactly.
A compiler upgrade must validate:

- protobuf field and extension numbers;
- the manifest version fields against a freshly generated frontend KLIB; and
- newly generated KLIBs with the newly pinned frontend.

KLIBs generated by an earlier DotKt compiler are unsupported internal artifacts;
an upgrade neither validates nor preserves their compatibility.

Current deliberate limits are:

- pointer and function-pointer types fall back to `Any?`;
- recursive CLR delegate graphs are refused because Kotlin metadata cannot
  represent them as finite function types;
- Kotlin function arities 17..22 use the stdlib's canonical
  `DotKt.Runtime.CompilerServices.KFunc`/`KAction`. The stdlib produces no projected KLIB, but remains in the
  resolved delegate catalog so references are restored from the actual TypeDef/`Invoke` shape and assembly identity,
  exactly like any other external delegate. Kotlin function arities of 23 and above have no CLR delegate and are
  refused before this stage (dotkt-semantics §8e-bis);
- arbitrary CLR custom-attribute applications are not round-tripped; and
- explicit Kotlin companion-object reconstruction is not part of CLR static
  projection.

These limits must be addressed by general representation rules, not
library-specific exceptions.

## Verification

The permanent end-to-end regression is:

```bash
make dll2klib-e2e
```

It verifies:

1. generation of CLR reference assemblies;
2. deterministic, byte-identical KLIB output;
3. the standard packed KLIB layout;
4. frontend resolution exclusively from reference KLIB metadata;
5. types, nested types, constructors, properties, fields, events, indexers,
   generics, nullability, delegates, extensions, operators, and by-reference
   calls;
6. binding through `bir2cir`;
7. CIL emission through `ilemit`; and
8. execution of the resulting CLR assembly; and
9. bounded diagnostics for local, generic, and cross-assembly recursive CLR
   delegate graphs.

The round-trip and packaged-SDK suites are also authoritative because they
exercise the same MSBuild reference-set path used by production projects.
