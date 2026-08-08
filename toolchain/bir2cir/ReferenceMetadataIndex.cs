using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DotKt.Bir;
using DotKt.Toolchain;

sealed partial class ReferenceMetadataIndex
{
    sealed class MalformedTrustedCompanionException : Exception
    {
        public MalformedTrustedCompanionException(string message, Exception inner = null) : base(message, inner) { }
    }

    const string KotlinFileClassAttr = "DotKt.Runtime.CompilerServices.KotlinFileClassAttribute";
    const string KotlinFunctionAttr = "DotKt.Runtime.CompilerServices.KotlinFunctionAttribute";
    const string KotlinInlineAttr = "DotKt.Runtime.CompilerServices.KotlinInlineAttribute";
    const string KotlinTypeAttr = "DotKt.Runtime.CompilerServices.KotlinTypeAttribute";
    const string KotlinCompanionAttr = "DotKt.Runtime.CompilerServices.KotlinCompanionAttribute";
    const string KotlinInnerAttr = "DotKt.Runtime.CompilerServices.KotlinInnerAttribute";
    // The #86/#147 positional carrier: the PRE-erasure Kotlin TypeNode of a declaration slot whose `Nullable(Tv)`
    // NullableGenericErasure object-erased. Read per member slot (return parameter and each value parameter) so a
    // CONSUMING module can re-derive `Subst(Erase(decl))` at a use of that slot instead of guessing from the call.
    const string KotlinNullableGenericAttr = "DotKt.Runtime.CompilerServices.KotlinNullableGenericAttribute";
    const string DotKtAssemblyMarkerAttr = "System.Reflection.AssemblyMetadataAttribute";
    const string DotKtAssemblyMarkerKey = "DotKt.Compiler";
    const string DotKtAssemblyMarkerValue = "metadata-v1";
    const string CompilerGeneratedAttr = "System.Runtime.CompilerServices.CompilerGeneratedAttribute";
    // The round-trip marker RoundtripMetadata stamps on every `value`/inline class. It REPLACES the old
    // `kotlin.jvm.JvmInline` key: the 2.4.0 frontend no longer materializes @JvmInline into the IR (OptionalExpectation
    // `expect` with no non-JVM actual), so value-ness now rides `mods.value` -> this synthetic attribute on the ref/rt DLL.
    const string KotlinValueAttr = "DotKt.Runtime.CompilerServices.KotlinValueAttribute";
    const string RestrictsSuspensionAttr = "kotlin.coroutines.RestrictsSuspension";
    // [KotlinFunction(flags)] flag word (mirrors ilemit Program.cs pass 4 / dll2klib): Infix=1, Operator=2, Suspend=4.
    const int KotlinFunctionSuspendFlag = 4;

    readonly List<ReferenceAssembly> _assemblies;
    readonly ManagedReferenceCatalog _compileRefs;

    // Aggregate CALL-SUBSTITUTION index across all reference assemblies.
    readonly Dictionary<string, string> _ownerAlias = new(StringComparer.Ordinal);   // Kotlin FQN -> BCL alias
    readonly Dictionary<string, string> _ownerKind = new(StringComparer.Ordinal);    // Kotlin FQN -> class/struct/...
    readonly HashSet<string> _byRefLikeOwners = new(StringComparer.Ordinal);         // Kotlin FQN -> is a `ref struct`
    readonly HashSet<string> _dotKtOwners = new(StringComparer.Ordinal);              // types authored by a DotKt-emitted assembly
    // Trusted [KotlinType(G<*,...>)] on a compiler-generated non-generic interface is the explicit existential ABI
    // relation. No physical-name suffix participates in recognition.
    readonly Dictionary<string, string> _existentialPhysicalBySemanticOwner = new(StringComparer.Ordinal);
    readonly HashSet<string> _existentialPhysicalOwners = new(StringComparer.Ordinal);
    readonly Dictionary<string, string> _existentialSemanticByPhysicalOwner = new(StringComparer.Ordinal);
    readonly Dictionary<string, bool> _companionStaticByPhysicalOwner = new(StringComparer.Ordinal);
    readonly Dictionary<string, string> _singletonCompanionCarrierBySemanticOwner = new(StringComparer.Ordinal);
    readonly Dictionary<string, string> _semanticOwnerByCompanionCarrier = new(StringComparer.Ordinal);
    readonly Dictionary<string, string> _companionCarrierByPhysicalOwner = new(StringComparer.Ordinal);
    readonly Dictionary<string, string> _companionSourceNameByPhysicalOwner = new(StringComparer.Ordinal);
    readonly Dictionary<string, string> _companionPhysicalOwnerBySemanticType = new(StringComparer.Ordinal);
    readonly Dictionary<string, string> _companionSemanticTypeByPhysicalOwner = new(StringComparer.Ordinal);
    readonly Dictionary<string, int> _ownerArity = new(StringComparer.Ordinal);      // Kotlin FQN -> generic arity
    readonly Dictionary<string, string[]> _ownerTypeParams = new(StringComparer.Ordinal); // Kotlin FQN -> generic param names
    // Per owner-FQN, the declared param type names of its (first/sole) constructor — used to adapt a static-String arg
    // flowing into a CharSequence ctor param of a SPLICED anonymous stdlib object (`dotkt$obj*`, e.g. the anonymous
    // Grouping from `CharSequence.groupingBy` whose ctor captures the receiver as `kotlin.CharSequence`). The spliced
    // `new dotkt$obj*(...)` node carries no argTypes, so the CharSequence-slot knowledge comes only from here.
    readonly Dictionary<string, string[]> _ownerCtorParams = new(StringComparer.Ordinal);
    // Per owner-FQN, the CLR generic-parameter CONSTRAINT class of each flattened type-param position:
    // "struct" (NotNullableValueTypeConstraint), "class" (ReferenceTypeConstraint), or "unconstrained". Drives the
    // struct-ness ORACLE for a type variable (#37/#48 nullability fold): a struct-constrained `T?` is `Nullable<T>`,
    // a class/unconstrained `T?` is a bare reference (nullability rides an NRT byte).
    readonly Dictionary<string, string[]> _ownerTypeParamConstraints = new(StringComparer.Ordinal);
    // Per owner-FQN (DOTTED, nested-`+`-normalized to match kotc's dotted vocabulary), the declared type-BOUND of each
    // type-param position as a structured TypeNode (or null when unconstrained / the bound is objectish / a
    // self-referential F-bound). Drives StarProjectionBoundLowering: a `Key<*>` erased to `Key<object>` violates
    // `E : Element`, so the objectish arg is replaced with the concrete bound (`Key<Element>`). An F-bound
    // (`E : Enum<E>`) stores null — no closed generic to substitute to — so its `<*>` arg is left unchanged.
    readonly Dictionary<string, TypeNode[]> _ownerTypeParamBounds = new(StringComparer.Ordinal);
    readonly HashSet<string> _helperTypes = new(StringComparer.Ordinal);             // emitted "dotkt$ClrH_*"
    readonly HashSet<string> _restrictsSuspension = new(StringComparer.Ordinal);     // @RestrictsSuspension owners
    readonly Dictionary<string, List<MemberBinding>> _membersByOwner = new(StringComparer.Ordinal);
    // ownerFqn -> declared parameter count -> the ctor declarations of that arity (#86 D1). A list, because a
    // same-arity overload set must be REFUSED rather than resolved by arity alone.
    readonly Dictionary<string, Dictionary<int, List<CtorBinding>>> _ctorsByOwner = new(StringComparer.Ordinal);
    // Reference-owner hierarchy in BIR's dotted Kotlin vocabulary.  Calls retain their Kotlin
    // receiver owner in BIR; inherited CLR MemberRefs are selected later by bir2cir, so that pass
    // needs the same constructed base/interface graph for referenced types as it has for local CIR
    // declarations.  Keep the graph as structured TypeNodes -- never reconstruct generic owners in
    // ilemit from reflection strings.
    readonly Dictionary<string, ReferenceTypeShape> _referenceTypeShapes = new(StringComparer.Ordinal);
    readonly Dictionary<string, string> _physicalTypeBySemanticName = new(StringComparer.Ordinal);
    readonly Dictionary<string, int> _innerCapturedCount = new(StringComparer.Ordinal);
    readonly Dictionary<string, string> _innerSemanticOwner = new(StringComparer.Ordinal);
    readonly Dictionary<string, string> _topLevelIntrinsics = new(StringComparer.Ordinal); // top-level fun name -> FQ static
    readonly Dictionary<string, string> _topLevelIntrinsicsBySig = new(StringComparer.Ordinal); // "name|paramKeys" -> FQ static (overload-disambiguated)
    readonly HashSet<string> _ambiguousTopLevelIntrinsics = new(StringComparer.Ordinal); // names whose overloads bind to DIFFERENT statics (Math vs MathF)
    readonly Dictionary<string, int[]> _topLevelIntrinsicByref = new(StringComparer.Ordinal); // top-level fun name -> byref param positions
    readonly Dictionary<string, string> _extMemberIntrinsics = new(StringComparer.Ordinal); // "name|recvKey|paramCount" -> bare member
    readonly Dictionary<string, (string Getter, string Conv)> _inlineBacking = new(StringComparer.Ordinal);
    readonly Dictionary<string, List<(string Owner, string RecvKey, string ParamKey)>> _topLevelStatics = new(StringComparer.Ordinal); // non-intrinsic top-level fun name -> [(file-class, coarse recvKey, fine first-param ParamKey)]
    readonly Dictionary<string, string> _collectionFactories = new(StringComparer.Ordinal); // @ClrCollectionFactory fun name -> "list"/"set"/"map"
    readonly Dictionary<string, string> _arrayFactories = new(StringComparer.Ordinal);       // @ClrArrayFactory fun name -> "vararg"/"sized"
    readonly Dictionary<string, string> _arrayFactoryElemHints = new(StringComparer.Ordinal);// array factory name -> concrete elem FQN (empty-call fallback)
    readonly Dictionary<string, Dictionary<int, string>> _kotlinDefaults = new(StringComparer.Ordinal); // "owner|name|paramCount" -> (argPos -> default BIR)
    // #146: OWNERLESS default-arg index "name|paramCount" -> defaults. DefaultArgSplice now runs at PHASE 1 (before
    // MemberCallSubstitution attributes the owner), so the omitted call is still `owner:null method:col2 sig:[…]`.
    // Built from _kotlinDefaults: a key with a SINGLE owner, or several owners whose defaults AGREE, maps to those
    // defaults; owners that DISAGREE mark it AMBIGUOUS (the splice loud-refuses rather than guess).
    readonly Dictionary<string, Dictionary<int, string>> _kotlinDefaultsOwnerless = new(StringComparer.Ordinal);
    readonly HashSet<string> _kotlinDefaultsAmbiguous = new(StringComparer.Ordinal);
    // OWNERFUL keys two same-arity declarations carry with different defaults (see ReferenceAssemblyMetadata).
    readonly HashSet<string> _kotlinDefaultsConflicted = new(StringComparer.Ordinal);
    // [KotlinInline] raw-BIR payloads (#71/#75): "owner|name|pc|ga" -> the CANDIDATE decoded carrier JSONs (one per overload
    // sharing that key; the raw pre-lowering decl facts InlineBirStash stashed). Read cross-module by InlineSplice, which
    // picks the UNIQUE candidate matching the call's `paramSig` (§4.2), then splices its body at the call site (so it
    // re-lowers in THIS app's context). owner = the .NET type FullName (file-facade class); pc/ga = the reflected
    // GetParameters/GetGenericArguments counts (parity with InlineBirStash's params.Count / typeParams.Count).
    readonly Dictionary<string, List<string>> _inlinePayloads = new(StringComparer.Ordinal);
    // OWNER-LESS callInline index (S3, §4.2 #75 S4b): "name|pc|ga" -> the CANDIDATE payload JSONs across EVERY `kotlin.*`
    // file-class hosting that shape. kotc cannot name the stdlib file class (the stdlib KLIB carries no physical owner —
    // whole stdlib rides the klib), so a scope-fn/@InlineOnly callInline carries owner=null. Since the bare `name|pc|ga`
    // collides across owners (Iterable/Array/IntArray/CharSequence `filter`/`map`/`forEach` etc.), the owner canNOT be picked
    // by the key alone — InlineSplice gathers ALL candidates here and picks the UNIQUE one whose declared params match the
    // call's `paramSig`; the winning payload's own `owner` field names the host. Restricted to `kotlin.*` so a user-lib
    // inline fn sharing a name|pc|ga cannot leak in.
    readonly Dictionary<string, List<string>> _ownerlessInlineCandidates = new(StringComparer.Ordinal);

    // ---- .NET-interop resolution (A2 / #61): the LONG-LIVED metadata universe over the exact compile references.
    // NetInteropBinding resolves a dll2klib-projected owner FQN
    // ("System.Console", "Kfc.App") to a metadata-only System.Reflection.Type here and reflects its member SHAPE
    // (static/instance/property/field/indexer/generic) to bind the plain callStatic/callInstance kotc emitted by
    // identity into the CLR-codegen `clr*` vocabulary. kotc no longer decides the .NET call shape (layer purity —
    // this is the SAME "emit the identity, bind in bir2cir" pattern as the stdlib ref.dll, one axis over). The MLC is
    // kept ALIVE for the whole run (Type handles are per-MLC; disposed in Driver.Run) and populated lazily.
    MetadataLoadContext _netMlc;
    List<Assembly> _netRefAsms;       // the explicit --compile-refs assemblies (framework + user references)
    readonly Dictionary<string, Type> _netTypeCache = new(StringComparer.Ordinal);
    bool _netInit;

    // The bare FQNs of every type DECLARED in THIS compilation (this run's BIR `types`). A local declaration is the
    // AUTHORITY for its identity — it wins over a referenced .NET/Kotlin dll that exports the SAME FQN (the #15
    // pathological layout: an app whose `**/*.kt` glob pulls in a nested ProjectReference lib's SOURCE — so it compiles
    // `demo.Plain` locally — AND references that lib's dll, which also exports `demo.Plain`). This mirrors the frontend
    // "source wins" fix: ResolveNetType refuses to bind a locally-emitted FQN to the ref, so every sibling resolution
    // routes to the this-assembly-emitted type — `new` stays a local `new` (not `newClr`), and a
    // callInstance/callStatic/field/boundDelegate stays owner-local (NetInteropBinding leaves it for the emitted-type
    // path) instead of reshaping to a `clr*` node against the ref. Set once by the Driver before the transform loop.
    // SCOPE: this filters the ResolveNetType axis ONLY. The ref.dll's DotKt sidecar indexes (TypeKinds/IsValueTypeFqn,
    // owner arity, ctor param types) are NOT filtered by this set — in the #15 layout they are populated from the SAME
    // source that produced the local decl, so they agree; a genuinely divergent stale-dll is out of scope (source-wins
    // is still the right precedence there, matching Roslyn CS0436). `@ClrTypeAlias`/`@ClrIntrinsic` maps are empty for a
    // user lib, so TryResolveClrOwner never mis-binds a local user type.
    IReadOnlySet<string> _localEmittedTypes = new HashSet<string>(StringComparer.Ordinal);
    public void SetLocalEmittedTypes(IReadOnlySet<string> fqns) => _localEmittedTypes = fqns;

    // Foundational REFERENCE-type aliases known to bir2cir directly (the same principle as the foundational
    // kotlin.* -> CLR type map already hardcoded in this file). Listed here so member-call / construction
    // substitution works even before kotc preserves the class @ClrTypeAlias attribute on the ref.dll. Only the
    // reference primitives (Any/String) — value primitives keep their identity and are handled by type lowering.
    static readonly IReadOnlyDictionary<string, string> FoundationalRefAliases = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["kotlin.Any"] = "System.Object",
        ["kotlin.String"] = "System.String",
        ["kotlin.Nothing"] = "System.Object",
    };

    // The foundational VALUE-type identities (seed for the struct-ness ORACLE): the numeric/bool/char primitives and
    // the unsigned set, in BOTH their kotlin.* spelling and the CLR shorthand a lowered/synthesized node may carry.
    // A nullable value type is the structural `System.Nullable<T>` (a DISTINCT type), so it keeps its `{t:nullable}`
    // wrapper through lowering — unlike a reference type, whose `?` is stripped to a bare type + an NRT byte. The
    // authoritative source for a concrete NON-primitive is the ref.dll `_ownerKind` (struct/enum); this seed makes the
    // primitives resolve even with no ref.dll loaded and shadows any ref-scan miss.
    static readonly HashSet<string> ValueTypePrimitiveFqns = new(StringComparer.Ordinal)
    {
        "kotlin.Int", "kotlin.Long", "kotlin.Short", "kotlin.Byte", "kotlin.Double", "kotlin.Float",
        "kotlin.Boolean", "kotlin.Char", "kotlin.UInt", "kotlin.ULong", "kotlin.UShort", "kotlin.UByte",
        "int", "long", "short", "sbyte", "double", "float", "bool", "char", "uint", "ulong", "ushort", "byte",
    };

    ReferenceMetadataIndex(List<ReferenceAssembly> assemblies, ManagedReferenceCatalog compileRefs)
    {
        _assemblies = assemblies;
        _compileRefs = compileRefs;
        foreach (var asm in assemblies)
        {
            foreach (var kv in asm.DotKt.Aliases) _ownerAlias[kv.Key] = kv.Value;
            foreach (var kv in asm.DotKt.TypeKinds) _ownerKind[kv.Key] = kv.Value;
            foreach (var owner in asm.DotKt.ByRefLikeOwners) _byRefLikeOwners.Add(owner);
            foreach (var owner in asm.DotKt.DotKtOwners)
                _dotKtOwners.Add(StripGenericArity(DottedFqn(owner)));
            foreach (var kv in asm.DotKt.ExistentialPhysicalBySemanticOwner)
            {
                var semantic = StripGenericArity(DottedFqn(kv.Key));
                var physical = StripGenericArity(DottedFqn(kv.Value));
                if (!_existentialPhysicalBySemanticOwner.TryAdd(semantic, physical)
                    || _existentialPhysicalBySemanticOwner[semantic] != physical)
                    throw new InvalidOperationException($"conflicting Kotlin existential ABI for '{semantic}'");
                _existentialPhysicalOwners.Add(physical);
                if (!_existentialSemanticByPhysicalOwner.TryAdd(physical, semantic)
                    || _existentialSemanticByPhysicalOwner[physical] != semantic)
                    throw new InvalidOperationException($"conflicting Kotlin existential physical owner '{physical}'");
            }
            foreach (var kv in asm.DotKt.CompanionStaticByPhysicalOwner)
            {
                var physicalOwner = StripGenericArity(DottedFqn(kv.Key));
                if (!_companionStaticByPhysicalOwner.TryAdd(physicalOwner, kv.Value) ||
                    _companionStaticByPhysicalOwner[physicalOwner] != kv.Value)
                    throw new InvalidOperationException($"conflicting Kotlin companion representation for '{physicalOwner}'");
            }
            foreach (var kv in asm.DotKt.SingletonCompanionCarrierBySemanticOwner)
            {
                var semanticOwner = StripGenericArity(DottedFqn(kv.Key));
                // Keep the reflected metadata spelling (`Outer`1+$Companion`) as the value. Keys use dotted Kotlin
                // vocabulary, but consumers that resolve the physical TypeDef must not have to guess where a dotted
                // source separator becomes CLR's nested `+` separator.
                var carrier = kv.Value;
                if (!_singletonCompanionCarrierBySemanticOwner.TryAdd(semanticOwner, carrier) ||
                    _singletonCompanionCarrierBySemanticOwner[semanticOwner] != carrier)
                    throw new InvalidOperationException($"conflicting Kotlin singleton companion carrier for '{semanticOwner}'");
            }
            foreach (var kv in asm.DotKt.CompanionSemanticOwnerByCarrier)
            {
                var carrierKey = StripGenericArity(DottedFqn(kv.Key));
                var semanticOwner = StripGenericArity(DottedFqn(kv.Value));
                if (!_semanticOwnerByCompanionCarrier.TryAdd(carrierKey, semanticOwner) ||
                    _semanticOwnerByCompanionCarrier[carrierKey] != semanticOwner)
                    throw new InvalidOperationException($"conflicting Kotlin semantic owner for companion carrier '{kv.Key}'");
            }
            foreach (var kv in asm.DotKt.CompanionCarrierByPhysicalOwner)
            {
                var physicalOwner = StripGenericArity(DottedFqn(kv.Key));
                if (!_companionCarrierByPhysicalOwner.TryAdd(physicalOwner, kv.Value) ||
                    _companionCarrierByPhysicalOwner[physicalOwner] != kv.Value)
                    throw new InvalidOperationException($"conflicting Kotlin companion physical owner '{physicalOwner}'");
            }
            foreach (var kv in asm.DotKt.CompanionSourceNameByPhysicalOwner)
            {
                var physicalOwner = StripGenericArity(DottedFqn(kv.Key));
                if (!_companionSourceNameByPhysicalOwner.TryAdd(physicalOwner, kv.Value) ||
                    _companionSourceNameByPhysicalOwner[physicalOwner] != kv.Value)
                    throw new InvalidOperationException($"conflicting Kotlin companion source name for '{physicalOwner}'");
            }
            foreach (var kv in asm.DotKt.CompanionPhysicalOwnerBySemanticType)
            {
                var semanticType = StripGenericArity(DottedFqn(kv.Key));
                var physicalOwner = kv.Value;
                if (!_companionPhysicalOwnerBySemanticType.TryAdd(semanticType, physicalOwner) ||
                    _companionPhysicalOwnerBySemanticType[semanticType] != physicalOwner)
                    throw new InvalidOperationException($"conflicting Kotlin companion declaration identity '{semanticType}'");
                else if (!_companionSemanticTypeByPhysicalOwner.TryAdd(
                    StripGenericArity(DottedFqn(physicalOwner)), semanticType) ||
                    _companionSemanticTypeByPhysicalOwner[StripGenericArity(DottedFqn(physicalOwner))] != semanticType)
                    throw new InvalidOperationException($"conflicting Kotlin companion physical declaration owner '{physicalOwner}'");
            }
            foreach (var kv in asm.DotKt.TypeArity) _ownerArity[kv.Key] = kv.Value;
            foreach (var kv in asm.DotKt.TypeParamNames) _ownerTypeParams[kv.Key] = kv.Value;
            foreach (var kv in asm.DotKt.CtorParamTypes) _ownerCtorParams[kv.Key] = kv.Value;
            foreach (var kv in asm.DotKt.TypeParamConstraints) _ownerTypeParamConstraints[kv.Key] = kv.Value;
            foreach (var kv in asm.DotKt.TypeParamBounds) _ownerTypeParamBounds[kv.Key] = kv.Value;
            foreach (var h in asm.DotKt.HelperTypes) _helperTypes.Add(h);
            foreach (var s in asm.DotKt.RestrictsSuspensionTypes) _restrictsSuspension.Add(s);
            foreach (var m in asm.DotKt.MemberBindings)
            {
                if (!_membersByOwner.TryGetValue(m.Owner, out var list))
                    _membersByOwner[m.Owner] = list = new List<MemberBinding>();
                list.Add(m);
            }
            foreach (var c in asm.DotKt.CtorBindings)
            {
                if (!_ctorsByOwner.TryGetValue(c.Owner, out var byArity))
                    _ctorsByOwner[c.Owner] = byArity = new Dictionary<int, List<CtorBinding>>();
                if (!byArity.TryGetValue(c.ParamCount, out var ctors)) byArity[c.ParamCount] = ctors = new List<CtorBinding>();
                ctors.Add(c);
            }
            foreach (var kv in asm.DotKt.TypeShapes) _referenceTypeShapes.TryAdd(kv.Key, kv.Value);
            foreach (var kv in asm.DotKt.PhysicalTypeBySemanticName)
                _physicalTypeBySemanticName.TryAdd(kv.Key, kv.Value);
            foreach (var kv in asm.DotKt.InnerCapturedCount)
                _innerCapturedCount.TryAdd(kv.Key, kv.Value);
            foreach (var kv in asm.DotKt.InnerSemanticOwner)
                _innerSemanticOwner.TryAdd(kv.Key, kv.Value);
            foreach (var kv in asm.DotKt.TopLevelIntrinsics) _topLevelIntrinsics.TryAdd(kv.Key, kv.Value);
            foreach (var kv in asm.DotKt.TopLevelIntrinsicsBySig) _topLevelIntrinsicsBySig.TryAdd(kv.Key, kv.Value);
            foreach (var n in asm.DotKt.AmbiguousTopLevelIntrinsics) _ambiguousTopLevelIntrinsics.Add(n);
            foreach (var kv in asm.DotKt.TopLevelIntrinsicByref) _topLevelIntrinsicByref.TryAdd(kv.Key, kv.Value);
            foreach (var kv in asm.DotKt.ExtMemberIntrinsics) _extMemberIntrinsics.TryAdd(kv.Key, kv.Value);
            foreach (var kv in asm.DotKt.InlineBacking) _inlineBacking.TryAdd(kv.Key, kv.Value);
            foreach (var kv in asm.DotKt.TopLevelStatics)
            {
                if (!_topLevelStatics.TryGetValue(kv.Key, out var lst))
                    _topLevelStatics[kv.Key] = lst = new List<(string, string, string)>();
                lst.AddRange(kv.Value);
            }
            foreach (var key in asm.DotKt.KotlinDefaultsConflicted) _kotlinDefaultsConflicted.Add(key);
            foreach (var kv in asm.DotKt.KotlinDefaults)
            {
                _kotlinDefaults.TryAdd(kv.Key, kv.Value);
                // OWNERLESS fold "owner|name|pc" -> "name|pc" (#146). Method/owner names carry no '|', so the split is exact.
                var parts = kv.Key.Split('|');
                // Only the 3-part ARITY key folds: a signature-keyed entry is 4 parts, and a CONSTRUCTOR is never called
                // ownerlessly (a `new` always names its type), so folding `.ctor|pc` would only make every type of the
                // same ctor arity collide with every other.
                if (parts.Length != 3 || parts[1] == CtorKeyName) continue;
                var np = parts[1] + "|" + parts[2];
                if (_kotlinDefaultsAmbiguous.Contains(np)) continue;
                // The OWNERFUL key is already known to be carried by two declarations that disagree, so the ownerless
                // fold of it cannot identify one either — mark it rather than folding the first-seen declaration in.
                if (asm.DotKt.KotlinDefaultsConflicted.Contains(kv.Key))
                {
                    _kotlinDefaultsOwnerless.Remove(np);
                    _kotlinDefaultsAmbiguous.Add(np);
                    continue;
                }
                if (_kotlinDefaultsOwnerless.TryGetValue(np, out var have))
                {
                    if (!SameDefaults(have, kv.Value)) { _kotlinDefaultsOwnerless.Remove(np); _kotlinDefaultsAmbiguous.Add(np); }
                }
                else _kotlinDefaultsOwnerless[np] = kv.Value;
            }
            foreach (var kv in asm.DotKt.CollectionFactories) _collectionFactories.TryAdd(kv.Key, kv.Value);
            foreach (var kv in asm.DotKt.ArrayFactories) _arrayFactories.TryAdd(kv.Key, kv.Value);
            foreach (var kv in asm.DotKt.ArrayFactoryElemHints) _arrayFactoryElemHints.TryAdd(kv.Key, kv.Value);
            // §4.2 (#75 S4b): merge candidate lists across assemblies — every overload sharing owner|name|pc|ga is kept; the
            // call site disambiguates by paramSig. (No poisoning: structural selection replaces the recv0-key collision.)
            foreach (var kv in asm.DotKt.InlinePayloads)
            {
                if (!_inlinePayloads.TryGetValue(kv.Key, out var lst)) _inlinePayloads[kv.Key] = lst = new List<string>();
                lst.AddRange(kv.Value);
            }
        }
        // Build the owner-less candidate index (S3, §4.2): "name|pc|ga" -> the candidate payload JSONs across every `kotlin.*`
        // file-class hosting that shape. The owner is NOT resolvable from the bare key (it collides across owners) — the call
        // site selects by paramSig and reads the winner's own `owner` field. Restricted to `kotlin.*` (kotc emits owner-less
        // ONLY for the klib stdlib; a user-lib inline fn is always owner-ful) so a user `T.use(block)` cannot leak in.
        foreach (var (key, jsons) in _inlinePayloads)
        {
            var bar = key.IndexOf('|');
            if (bar < 0) continue;
            if (!key.AsSpan(0, bar).StartsWith("kotlin.")) continue;
            var npg = key[(bar + 1)..];   // name|pc|ga
            if (!_ownerlessInlineCandidates.TryGetValue(npg, out var lst)) _ownerlessInlineCandidates[npg] = lst = new List<string>();
            lst.AddRange(jsons);
        }
    }

    // The CANDIDATE raw-BIR [KotlinInline] payloads for an OWNER-FUL cross-module inline fn (owner|name|pc|ga), each decoded to
    // its JSON object — the overloads sharing that key. Empty/null only when the referenced assembly carries no
    // [KotlinInline] for that shape. Current-format DotKt carriers are internal ABI; malformed/older payloads are
    // unsupported and must not fall back to a non-inline call. InlineSplice picks the UNIQUE paramSig match.
    public List<JsonObject> InlineCandidates(string owner, string name, int pc, int ga)
    {
        if (owner == null || name == null) return null;
        return ParseCandidates(_inlinePayloads.GetValueOrDefault($"{owner}|{name}|{pc}|{ga}"));
    }

    // The CANDIDATE payloads for an OWNER-LESS callInline (S3): every `kotlin.*` overload hosting name|pc|ga, across owners.
    // InlineSplice selects the unique paramSig match; the winner's own `owner` field names the host file class.
    public List<JsonObject> OwnerlessInlineCandidates(string name, int pc, int ga) =>
        name == null ? null : ParseCandidates(_ownerlessInlineCandidates.GetValueOrDefault($"{name}|{pc}|{ga}"));

    static List<JsonObject> ParseCandidates(List<string> jsons)
    {
        if (jsons == null || jsons.Count == 0) return null;
        var list = new List<JsonObject>(jsons.Count);
        foreach (var j in jsons)
        {
            try
            {
                list.Add((JsonObject)JsonNode.Parse(j, documentOptions: BirJson.DocOptions));
            }
            catch (Exception ex)
            {
                // Compiler carriers are internal ABI. Preserve failure across the broad foreign-metadata scan guard;
                // no compatibility interpretation or dedicated legacy diagnostic is required.
                throw new InvalidDataException(null, ex);
            }
        }
        return list;
    }

    // The @ClrCollectionFactory kind ("list"/"set"/"map") for a top-level fun NAME, or null when the fun is not a
    // collection factory. MemberCallSubstitution consults this on a `callStatic owner=null` to re-emit newList/newSet/newMap.
    public string CollectionFactoryKind(string funName) => _collectionFactories.GetValueOrDefault(funName);
    // The @ClrArrayFactory kind ("vararg"/"sized") for a top-level fun NAME, or null when not an array factory.
    public string ArrayFactoryKind(string funName) => _arrayFactories.GetValueOrDefault(funName);
    // The concrete element FQN for an array factory (the fallback for a call whose vararg brought no `newArray`
    // wrapper — a spread), or null.
    public string ArrayFactoryElemHint(string funName) => _arrayFactoryElemHints.GetValueOrDefault(funName);

    /// The `method` component of a CONSTRUCTOR's @KotlinDefault key (#235). `.ctor` is the CLR's own constructor name and
    /// is unspeakable in Kotlin, so it can never collide with a real method a `new`'s owner declares.
    internal const string CtorKeyName = ".ctor";

    // The @KotlinDefault BIR splice map for a call's callee — (argPosition -> default-expression BIR-json). #146:
    // dll2klib-projected calls already carry their exact file-facade `ownerType` in BIR, so use that structural identity
    // first. Truly ownerless Kotlin calls retain the conservative name+arity index; conflicting owners remain ambiguous
    // and are refused. Running this at phase 1 does not imply throwing away an owner kotc has already projected.
    public Dictionary<int, string> KotlinDefaultsFor(string owner, string method, int paramCount, string sigKey = null)
    {
        if (method == null) return null;
        if (owner != null)
        {
            var key = owner + "|" + method + "|" + paramCount;
            // A call carries its callee's declared parameter vector (`sig`/`shapeTypes`, or a `new`'s `argTypes`), so try
            // the SIGNATURE key first — that is what tells same-arity overloads apart. Exact first, then with class
            // positions collapsed (the call's Kotlin spelling and the reference's CLR spelling only compare there), then
            // the arity key, which refuses when two declarations carry it with different defaults.
            if (sigKey != null)
            {
                if (_kotlinDefaults.TryGetValue(key + "|" + sigKey, out var bySig)
                    && !_kotlinDefaultsConflicted.Contains(key + "|" + sigKey)) return bySig;
                var relaxed = key + "|~" + RelaxedSigKey(sigKey);
                if (_kotlinDefaults.TryGetValue(relaxed, out var byRelaxed) && !_kotlinDefaultsConflicted.Contains(relaxed))
                    return byRelaxed;
            }
            if (_kotlinDefaultsConflicted.Contains(key)) return null;
            return _kotlinDefaults.TryGetValue(key, out var exact) ? exact : null;
        }
        return _kotlinDefaultsOwnerless.TryGetValue(method + "|" + paramCount, out var ownerless) ? ownerless : null;
    }
    // True when the name+arity cannot identify ONE set of defaults: a genuinely ownerless name carried by >1 owner that
    // disagree, or an OWNERFUL key two same-arity declarations (ctor overloads) carry with different defaults.
    public bool KotlinDefaultsAmbiguous(string owner, string method, int paramCount) =>
        method != null && (owner == null
            ? _kotlinDefaultsAmbiguous.Contains(method + "|" + paramCount)
            : _kotlinDefaultsConflicted.Contains(owner + "|" + method + "|" + paramCount));

    static bool SameDefaults(Dictionary<int, string> a, Dictionary<int, string> b)
    {
        if (a.Count != b.Count) return false;
        foreach (var kv in a) if (!b.TryGetValue(kv.Key, out var v) || v != kv.Value) return false;
        return true;
    }

    // Cross-assembly suspend-call resolution (bundle-6 P3 wave 2a): does the referenced owner declare a suspend
    // member of this name? The cold entry is the naming-convention linkage (`<name>$dotkt_suspend` on the same
    // owner type), keyed off the [KotlinFunction(Suspend)] flag scanned into MemberBinding.Suspend. Used by
    // SuspendColdLowering to rewrite a cross-assembly `x.g()` suspend call to `x.g$dotkt_suspend(…, completion)`.
    public bool HasSuspendMember(string owner, string name) =>
        owner != null && TryMembersByBirOwner(owner, out var list)
        && list.Any(m => m.Suspend && string.Equals(m.Name, name, StringComparison.Ordinal));

    // #78 Defect A (cross-assembly axis) — the exact-owner HasSuspendMember above misses a suspend member declared on a
    // SUPERTYPE of the call site's referenced static-receiver (e.g. a local subclass extending a referenced coroutine
    // base, or a referenced interface whose suspend member is declared on a super-interface). Walk the reflected owner's
    // BaseType + interface chain across the compile-reference set (metadata-only), checking the flat member index at each
    // super. Best-effort and non-throwing: an unresolvable owner (a purely local type, or a name absent from the refs)
    // falls back to the flat exact-owner result. Same-assembly members no longer need a hierarchy walk (R1 declares a
    // cold entry for every same-assembly suspend member unconditionally; virtual dispatch resolves inherited/overridden).
    public bool HasSuspendMemberInHierarchy(string owner, string name)
    {
        if (owner == null) return false;
        if (HasSuspendMember(owner, name)) return true;
        try
        {
            EnsureNetMlc();
            if (_netMlc == null || _netRefAsms == null) return false;
            // Probe the GENERIC-arity spellings too: a `clr*` owner token is the bare FQN (`lib.Sub`), but the
            // reflected CLR type of a generic subtype is `lib.Sub`1` — a plain GetType(asm, "lib.Sub") misses it,
            // so the hierarchy walk (and thus R1b's cold-ABI existence guard) would false-negative on a suspend
            // member inherited from a super through a GENERIC referenced subtype. Try the plain name then `n`1..8`.
            Type start = null;
            foreach (var cand in NetTypeCandidates(owner, 0))
            {
                foreach (var asm in _netRefAsms) { start = SafeGetType(asm, cand); if (start != null) break; }
                if (start != null) break;
            }
            if (start == null) return false;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var work = new Queue<Type>();
            work.Enqueue(start);
            while (work.Count > 0)
            {
                var cur = work.Dequeue();
                var def = cur.IsGenericType && !cur.IsGenericTypeDefinition ? cur.GetGenericTypeDefinition() : cur;
                var curFqn = StripGenericArity(((def.Namespace is string ns && ns.Length > 0 ? ns + "." : "") + def.Name).Replace('+', '.'));
                if (!seen.Add(curFqn)) continue;
                if (!ReferenceEquals(cur, start) && HasSuspendMember(curFqn, name)) return true;
                if (cur.BaseType != null) work.Enqueue(cur.BaseType);
                try { foreach (var i in cur.GetInterfaces()) work.Enqueue(i); } catch { }
            }
        }
        catch { }
        return false;
    }

    // Does this owner type carry @kotlin.coroutines.RestrictsSuspension (a restricted-suspension scope, e.g.
    // SequenceScope)? A suspend lambda with such a receiver gets the RestrictedSuspendLambda SM base (bundle-6 P5).
    public bool HasRestrictsSuspension(string ownerToken) =>
        ownerToken != null && _restrictsSuspension.Contains(BareOwnerFqn(ownerToken));

    public int Count => _assemblies.Count;
    public IReadOnlyList<ReferenceAssembly> Assemblies => _assemblies;

    // Every ref.dll scan diagnostic (a swallowed MetadataLoadContext load failure / partial-type-load / per-type skip).
    // Surfaced to stderr in the driver so a silent ref-scan miss (which becomes a DISTANT EntryPointNotFound/NRE at
    // ilemit or run time) is visible at the layer that produced it. See the driver's `Run` for the fail-loud print.
    public IEnumerable<string> Diagnostics => _assemblies.SelectMany(a => a.DotKt.Diagnostics);

    // The ref.dll @ClrTypeAlias index (Kotlin FQN -> BCL), the SINGLE source of truth shared by both the member-call
    // substitution (owner identity) and the TYPE-TOKEN lowering (supertypes/interfaces/type-args/fields). Keyed on the
    // stripped FQN (no generic-arity backtick), matching a BIR type token's bare owner.
    public IReadOnlyDictionary<string, string> Aliases => _ownerAlias;

    // ---- Call-substitution lookups (consumed by MemberCallSubstitution) ----

    // A BIR owner token ("@kotlin.text.StringBuilder", "kotlin.collections.ArrayList[gp:E]", "clr:System.X") ->
    // its bare Kotlin FQN ("kotlin.text.StringBuilder"). Strips decoration, the clr:/clrg: marker, and type args.
    public static string BareOwnerFqn(string token)
    {
        var t = token.Trim().TrimStart('@');
        foreach (var p in new[] { "clrg:", "clr:" })
            if (t.StartsWith(p, StringComparison.Ordinal)) t = t[p.Length..];
        var br = t.IndexOf('[');
        if (br >= 0) t = t[..br];
        return StripGenericArity(t);
    }

    // The top-level-extension receiver KEY of a call's first-sig-arg Fqn — the call-site mirror of the ref-side
    // RecvKey(Type) (used to index/disambiguate TopLevelStatics by receiver type). A specialized primitive-array Fqn
    // (`kotlin.IntArray`/`CharArray`/... + the unsigned specialized arrays) collapses to "[]" — the SAME canonicalization
    // RecvKey(Type) applies to a real `int[]` (IsArray). kotc spells such a receiver as a bare `kotlin.IntArray` Fqn
    // (BirTypeLowering decomposes it to a real array only later), so without this collapse a primitive-array receiver
    // would key as "kotlin.IntArray" and never match the ref.dll's "[]" candidate — leaving `intArrayOf(..).toList()`
    // owner-null AND its return type unresolved (#153). generic `Array<T>` already reaches "[]" (its sig is a TypeNode.Array).
    // (ParamKey's `array:i32` is a DIFFERENT canonicalization for @ClrIntrinsic sig matching — not conflated here.)
    public static string RecvKeyOfFqn(string fqnName) =>
        BirTypeLowering.PrimArrayElem.ContainsKey(fqnName) ? "[]" : BareOwnerFqn(fqnName);

    // Receiver-nullability normalization for a fine first-param key. A top-level extension's RECEIVER nullability is NOT
    // part of the CLR static's identity, and the two key derivations disagree on it: the call side spells a nullable
    // ARRAY/reference receiver as `nullable:array:byte` (from the birType `UByteArray?`), but the ref.dll reflection can
    // never emit `nullable:` for a nullable reference-typed param (only Nullable<value> structs) -> the stored key is the
    // bare `array:byte`. Strip a single leading `nullable:` on BOTH operands so a nullable-receiver `ubyteArrayOf(..)
    // .contentToString()` still pins UArraysKt instead of falling to the buggy coarse "[]" first-match (#153). A
    // value-type nullable receiver keys `nullable:i32` on BOTH sides, so stripping both stays a match.
    static string NoRecvNull(string key) =>
        key != null && key.StartsWith("nullable:", StringComparison.Ordinal) ? key["nullable:".Length..] : key;

    // Resolve a member-call/construction OWNER to its BCL type. True for a @ClrTypeAlias / class-@ClrIntrinsic owner
    // (or a foundational reference primitive). `kind` is the ref.dll type kind (class/struct/interface/enum).
    public bool TryResolveClrOwner(string ownerToken, out string bcl, out string kind)
    {
        var fqn = BareOwnerFqn(ownerToken);
        if (FoundationalRefAliases.TryGetValue(fqn, out bcl)) { kind = "class"; return true; }
        if (_ownerAlias.TryGetValue(fqn, out bcl)) { kind = _ownerKind.GetValueOrDefault(fqn, "class"); return true; }
        bcl = null; kind = null; return false;
    }

    // Resolve a dll2klib-projected .NET owner FQN to its metadata-only reflection Type (A2 / #61), or null when the
    // owner is `kotlin.*` stdlib vocabulary (bound by MemberCallSubstitution off the ref.dll, NOT here), compiler-owned
    // `dotkt$…` synthetic vocabulary, a local app-emitted type, or absent from the compile-reference set.
    // `genericArity` lets a constructed
    // generic owner ("System.Collections.Generic.List"
    // + args) resolve its open definition (`List`1`). Consumed by NetInteropBinding to shape the call. Cached.
    public Type ResolveNetType(string fqn, int genericArity = 0)
    {
        if (string.IsNullOrEmpty(fqn)) return null;
        // #26 follow-up: only `dotkt$…` is compiler-owned synthetic vocabulary
        // (dotkt$obj*/dotkt$ClrH_*/dotkt$CharSequence/…). `dotkt` and `dotkt.*` were used by the retired pre-stdlib
        // runtime, but are ordinary user FQNs now; skipping them breaks a referenced Kotlin library in that namespace
        // exactly like the former over-broad StartsWith("dotkt") broke `dotktx.*` packages.
        // `kotlinx.*` is intentionally NOT special: it is the ordinary namespace used by separately-built libraries
        // such as atomicfu and coroutines. Treating that prefix as stdlib vocabulary prevents their projected
        // members from reaching the normal reflection-backed external-assembly binding.
        if (fqn == "kotlin" || fqn.StartsWith("kotlin.", StringComparison.Ordinal)
            || fqn.StartsWith("dotkt$", StringComparison.Ordinal)) return null;
        // A type carrying DotKt declaration metadata is a Kotlin library surface even when its namespace is ordinary
        // (`kotlinx.*`, `roundtrip.*`, ...). Its calls must retain Kotlin ABI handling in MemberCallSubstitution rather
        // than being reclassified as raw C# members by NetInteropBinding. This replaces namespace-prefix ownership
        // guesses for external DotKt libraries while leaving genuine .NET types on the reflection path.
        if (_dotKtOwners.Contains(BareOwnerFqn(fqn))) return null;
        // LOCAL-OVER-REF (#15): a type DECLARED in this compilation is this-assembly-emitted and is the authority for
        // its identity — never resolve it as an EXTERNAL .NET type off the refs, even when a referenced dll exports the
        // same FQN (the ProjectReference-source-glob layout). Source wins: leave the node routing to the emitted type.
        if (_localEmittedTypes.Contains(BareOwnerFqn(fqn))) return null;
        return ProbeNetType(fqn, genericArity);
    }

    // Resolve one exact public CLR member used through a Kotlin star-projected FOREIGN generic. There is no CLR
    // nominal type for G<*>, so ForeignStarProjectionBinding dispatches through the stdlib reflection runtime. The
    // compiler still owns overload resolution: it supplies the declaring generic definition and exact declaration
    // identity (token plus a structural key for ref.dll/runtime twins), and the runtime only maps that definition
    // onto the receiver's constructed type. Runtime argument values never participate in overload selection.
    // DotKt-authored generics are excluded because their compiler-generated existential metadata is the
    // authoritative, reflection-free ABI.
    public bool TryForeignStarMethod(TypeNode.Fqn sourceOwner, string sourceName, string propertyAccess,
        int methodArity, IReadOnlyList<TypeNode> callSignature,
        out string openDeclaringType, out int metadataToken, out string runtimeName,
        out string[] runtimeParameterKeys, out TypeNode declarationReturn, out bool returnsVoid)
    {
        openDeclaringType = null;
        metadataToken = 0;
        runtimeName = null;
        runtimeParameterKeys = null;
        declarationReturn = null;
        returnsVoid = false;
        if (sourceOwner?.Args is not { Length: > 0 } ownerArgs || sourceName == null
            || callSignature == null || HasDotKtOwner(sourceOwner.Name)) return false;

        var sourceType = ResolveNetType(BareOwnerFqn(sourceOwner.Name), ownerArgs.Length);
        if (sourceType == null) return false;
        var methodName = propertyAccess switch
        {
            "get" => "get_" + sourceName,
            "set" => "set_" + sourceName,
            _ => sourceName,
        };
        const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance;
        var seenTypes = new HashSet<Type>();
        var frontier = new List<Type> { sourceType };
        MethodInfo selected = null;
        while (frontier.Count > 0)
        {
            var candidates = new List<MethodInfo>();
            var next = new List<Type>();
            foreach (var current in frontier)
            {
                if (current == null || !seenTypes.Add(current)) continue;
                try
                {
                    candidates.AddRange(current.GetMethods(flags | BindingFlags.DeclaredOnly)
                        .Where(m => m.Name == methodName
                            && m.GetGenericArguments().Length == methodArity
                            && m.GetParameters().Length == callSignature.Count));
                    if (current.BaseType != null) next.Add(current.BaseType);
                    next.AddRange(current.GetInterfaces());
                }
                catch
                {
                    // An incomplete reference graph cannot authorize a reflection ABI. The caller reports the exact
                    // star-member shape as unsupported instead of guessing a namesake.
                }
            }
            var exact = candidates.Where(m => m.GetParameters()
                .Select(p => DeclarationTypeNode(p.ParameterType))
                .SequenceEqual(callSignature)).ToList();
            var compatible = exact.Count > 0 ? exact : candidates.Where(m => m.GetParameters()
                .Select((p, i) => ForeignStarDeclarationDescribesCall(
                    DeclarationTypeNode(p.ParameterType), callSignature[i]))
                .All(x => x)).ToList();
            if (compatible.Count > 1) return false;
            if (compatible.Count == 1)
            {
                selected = compatible[0];
                break;
            }
            frontier = next;
        }
        if (selected == null) return false;

        var declaring = selected.DeclaringType;
        if (declaring == null) return false;
        if (declaring.IsConstructedGenericType) declaring = declaring.GetGenericTypeDefinition();
        if (!declaring.IsGenericTypeDefinition) return false;
        openDeclaringType = ExactPhysicalMetadataName(declaring);
        metadataToken = selected.MetadataToken;
        runtimeName = selected.Name;
        runtimeParameterKeys = selected.GetParameters()
            .Select(parameter => ForeignStarRuntimeTypeKey(parameter.ParameterType)).ToArray();
        declarationReturn = DeclarationTypeNode(selected.ReturnType);
        returnsVoid = selected.ReturnType == typeof(void);
        return true;
    }

    // Metadata tokens are the fastest exact identity when compile-time and runtime modules are the same physical
    // image. Reference assemblies are allowed to assign different tokens from their implementation twin, however.
    // Carry this structural declaration key as the exact fallback; the runtime compares the selected declaration's
    // name/arity/parameter TYPES and never chooses an overload from runtime argument values.
    static string ForeignStarRuntimeTypeKey(Type type)
    {
        if (type.IsGenericParameter)
            return (type.DeclaringMethod == null ? "t" : "m") + type.GenericParameterPosition;
        if (type.IsByRef) return "r[" + ForeignStarRuntimeTypeKey(type.GetElementType()) + "]";
        if (type.IsArray)
            return "a" + type.GetArrayRank() + "[" + ForeignStarRuntimeTypeKey(type.GetElementType()) + "]";
        if (type.IsGenericType)
        {
            var definition = type.IsGenericTypeDefinition ? type : type.GetGenericTypeDefinition();
            return "g{" + (definition.FullName ?? definition.Name) + "}<"
                + string.Join(",", type.GetGenericArguments().Select(ForeignStarRuntimeTypeKey)) + ">";
        }
        return "n{" + (type.FullName ?? type.Name) + "}";
    }

    // A member declared on open G<T...> necessarily carries owner/method TVs, while the frontend call descriptor is
    // already instantiated at the readable projection (`Pair<*, String>.Second = value` has a String argument).
    // Treat only declaration TVs as wildcards after exact candidates have been preferred; nominal structure remains
    // recursive and overload sets that still admit more than one candidate are rejected rather than guessed.
    static bool ForeignStarDeclarationDescribesCall(TypeNode declaration, TypeNode call)
    {
        if (declaration is TypeNode.Tv) return true;
        if (declaration is TypeNode.Oblivious dOb)
            return ForeignStarDeclarationDescribesCall(dOb.Of, call);
        if (call is TypeNode.Oblivious cOb)
            return ForeignStarDeclarationDescribesCall(declaration, cOb.Of);
        if (declaration is TypeNode.Nullable dn)
            return call is TypeNode.Nullable cn
                ? ForeignStarDeclarationDescribesCall(dn.Of, cn.Of)
                : ForeignStarDeclarationDescribesCall(dn.Of, call);
        if (call is TypeNode.Nullable callNullable)
            return ForeignStarDeclarationDescribesCall(declaration, callNullable.Of);
        if (declaration is TypeNode.Fqn df && call is TypeNode.Fqn cf)
        {
            if (ParamKey(df) != ParamKey(cf)) return false;
            if (df.Args == null || cf.Args == null) return df.Args == null && cf.Args == null;
            return df.Args.Length == cf.Args.Length
                && df.Args.Select((arg, i) => ForeignStarDeclarationDescribesCall(arg, cf.Args[i])).All(x => x);
        }
        if (declaration is TypeNode.Array da && call is TypeNode.Array ca)
            return ForeignStarDeclarationDescribesCall(da.Elem, ca.Elem);
        if (declaration is TypeNode.ByRef db && call is TypeNode.ByRef cb)
            return ForeignStarDeclarationDescribesCall(db.Of, cb.Of);
        if (declaration is TypeNode.Fn dfn && call is TypeNode.Fn cfn
            && dfn.Params.Length == cfn.Params.Length)
            return ForeignStarDeclarationDescribesCall(dfn.Ret, cfn.Ret)
                && dfn.Params.Select((arg, i) => ForeignStarDeclarationDescribesCall(arg, cfn.Params[i])).All(x => x);
        return DeclarationDescribesCall(declaration, call);
    }

    // Field-backed CLR properties projected by dll2klib use the same clrPropGet/clrPropSet node as real properties.
    // Keep field dispatch exact as well: the runtime receives a metadata token, never a source-name lookup.
    public bool TryForeignStarField(TypeNode.Fqn sourceOwner, string sourceName,
        out string openDeclaringType, out int metadataToken, out TypeNode declarationType)
    {
        openDeclaringType = null;
        metadataToken = 0;
        declarationType = null;
        if (sourceOwner?.Args is not { Length: > 0 } ownerArgs || sourceName == null
            || HasDotKtOwner(sourceOwner.Name)) return false;
        var sourceType = ResolveNetType(BareOwnerFqn(sourceOwner.Name), ownerArgs.Length);
        if (sourceType == null) return false;

        const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly;
        var seen = new HashSet<Type>();
        var frontier = new List<Type> { sourceType };
        FieldInfo selected = null;
        while (frontier.Count > 0)
        {
            var matches = new List<FieldInfo>();
            var next = new List<Type>();
            foreach (var current in frontier)
            {
                if (current == null || !seen.Add(current)) continue;
                try
                {
                    matches.AddRange(current.GetFields(flags).Where(f => f.Name == sourceName));
                    if (current.BaseType != null) next.Add(current.BaseType);
                    next.AddRange(current.GetInterfaces());
                }
                catch { }
            }
            if (matches.Count > 1) return false;
            if (matches.Count == 1) { selected = matches[0]; break; }
            frontier = next;
        }
        if (selected == null || selected.DeclaringType == null) return false;
        var declaring = selected.DeclaringType;
        if (declaring.IsConstructedGenericType) declaring = declaring.GetGenericTypeDefinition();
        if (!declaring.IsGenericTypeDefinition) return false;
        openDeclaringType = ExactPhysicalMetadataName(declaring);
        metadataToken = selected.MetadataToken;
        declarationType = DeclarationTypeNode(selected.FieldType);
        return declarationType != null;
    }

    // W1-S2 (#46): resolve a STDLIB-owner clr* member's declaring type off the ref.dll — WITHOUT the `kotlin.*`
    // exclusion that `ResolveNetType` applies (that exclusion keeps NetInteropBinding from reshaping a stdlib call; it
    // does NOT apply to ClrMemberResolution, which runs AFTER substitution and only reflects a member's DECLARED sig).
    // Used for a clr* node IteratorConsumerNormalization deliberately keeps on its `kotlin.collections.Iterator` owner for
    // the rt-stdlib link. Still honors the local-emitted skip (a self-build's own kotlin.* type is authored, not reflected)
    // + the dotkt-synthetic skip (dotkt$CharSequence has no ref.dll type).  One synthetic family is deliberately a
    // real referenced declaration: `dotkt$obj*` anonymous-object classes captured in inline bodies.  When such a body
    // is spliced into a consumer, its constructor still belongs to the referenced assembly and must be resolved like
    // every other external member; excluding it leaves ilemit to rediscover the constructor from the runtime DLL.
    // Null when the type is not in the ref universe.
    public Type ResolveRefType(string fqn, int genericArity = 0)
    {
        if (string.IsNullOrEmpty(fqn)) return null;
        if (fqn.StartsWith("dotkt$", StringComparison.Ordinal)
            && !fqn.StartsWith("dotkt$obj", StringComparison.Ordinal)) return null;
        if (_localEmittedTypes.Contains(BareOwnerFqn(fqn))) return null;
        return ProbeNetType(fqn, genericArity);
    }

    // Resolve the physical ECMA-335 constant of a named entry on an external CLR enum. The BIR entry name is Kotlin
    // declaration identity; the underlying value is a CLR representation fact and therefore enters CIR only here.
    // String form preserves the complete UInt64 domain without lossy JSON-number conversion.
    public EnumPhysicalConstant ResolveNetEnumConstant(string fqn, string entry)
    {
        var type = ResolveNetType(fqn);
        if (type == null || !type.IsEnum || string.IsNullOrEmpty(entry)) return null;
        var field = type.GetField(entry, BindingFlags.Public | BindingFlags.Static);
        if (field == null || !field.IsLiteral) return null;
        object raw;
        try { raw = field.GetRawConstantValue(); }
        catch { return null; }
        if (raw == null) return null;
        Type underlying;
        try { underlying = type.GetEnumUnderlyingType(); }
        catch { return null; }
        return new EnumPhysicalConstant(
            underlying.FullName ?? underlying.Name,
            Convert.ToString(raw, CultureInfo.InvariantCulture));
    }

    // The shared MLC probe (cache + candidate spellings + forwarder collapse) — the caller applies the owner-universe
    // policy (ResolveNetType excludes kotlin.*/dotkt$ synthetics/local; ResolveRefType excludes only the latter two).
    Type ProbeNetType(string fqn, int genericArity)
    {
        if (_netTypeCache.TryGetValue(fqn, out var cached)) return cached;
        EnsureNetMlc();
        Type found = null;
        if (_netMlc != null)
        {
            foreach (var candidate in NetTypeCandidates(fqn, genericArity))
            {
                var matches = new Dictionary<string, Type>(StringComparer.Ordinal);
                foreach (var asm in _netRefAsms)
                {
                    var match = SafeGetType(asm, candidate);
                    if (match != null)
                        // Collapse type forwarders that resolve to the same defining assembly, but never pick an
                        // arbitrary winner when two selected references really define the same FQN.
                        matches.TryAdd(match.Assembly.GetName().FullName ?? match.Assembly.GetName().Name!, match);
                }
                if (matches.Count > 1)
                    throw new InvalidOperationException(
                        $"bir2cir: type '{candidate}' is defined by multiple compile references: " +
                        string.Join(", ", matches.Keys.OrderBy(x => x, StringComparer.Ordinal)));
                found = matches.Values.SingleOrDefault();
                if (found != null) break;
            }
        }
        _netTypeCache[fqn] = found;
        return found;
    }

    // The FQN spellings to probe: the plain name, then the generic-arity backtick form (`List`1`). The exact arity
    // (from the owner token's type-arg count) is tried first; a small fallback range covers a token that dropped its args.
    static IEnumerable<string> NetTypeCandidates(string fqn, int genericArity)
    {
        yield return fqn;
        if (genericArity > 0) yield return fqn + "`" + genericArity;
        for (var k = 1; k <= 8; k++) if (k != genericArity) yield return fqn + "`" + k;
    }

    static Type SafeGetType(Assembly asm, string fqn) { try { return asm.GetType(fqn, throwOnError: false); } catch { return null; } }

    void EnsureNetMlc()
    {
        if (_netInit) return;
        _netInit = true;
        try
        {
            _netMlc = _compileRefs.CreateMetadataLoadContext();
            _netRefAsms = new List<Assembly>();
            // The catalog already classified each entry as a readable managed PE (#52); a load failure here means a
            // transitive dependency the MLC resolver could not satisfy — surface it naming the file instead of a
            // silent skip. Non-fatal: NetInteropBinding probes the assemblies that DID load.
            foreach (var a in _compileRefs.Entries)
            {
                try { _netRefAsms.Add(_netMlc.LoadFromAssemblyPath(a.Path)); }
                catch (Exception ex) { Console.Error.WriteLine($"bir2cir: warning: could not load reference into the metadata context: {a.Path} — {ex.GetType().Name}: {ex.Message}"); }
            }
        }
        catch { _netMlc = null; }
    }

    public void DisposeNet() { try { _netMlc?.Dispose(); } catch { } _netMlc = null; }

    public int OwnerArity(string ownerFqn)
    {
        if (ownerFqn == null) return 0;
        // Reflection metadata keeps nested TypeDef identity with '+'. Prefer that exact key; dotted normalization is
        // only the fallback for source-style CIR tokens. This is load-bearing for a generic owner's nested companion:
        // its synthetic capture closes `$INSTANCE` even though the companion has no Kotlin-declared type parameters.
        var exact = StripGenericArity(ownerFqn);
        return _ownerArity.TryGetValue(exact, out var arity)
            ? arity
            : _ownerArity.GetValueOrDefault(StripGenericArity(DottedFqn(ownerFqn)), 0);
    }

    public bool TryExistentialPhysicalOwner(string semanticOwner, out string physicalOwner) =>
        _existentialPhysicalBySemanticOwner.TryGetValue(
            StripGenericArity(DottedFqn(BareOwnerFqn(semanticOwner))), out physicalOwner);

    public bool IsExistentialPhysicalOwner(string physicalOwner) =>
        _existentialPhysicalOwners.Contains(StripGenericArity(DottedFqn(BareOwnerFqn(physicalOwner))));

    public bool TryExistentialSemanticOwner(string physicalOwner, out string semanticOwner) =>
        _existentialSemanticByPhysicalOwner.TryGetValue(
            StripGenericArity(DottedFqn(BareOwnerFqn(physicalOwner))), out semanticOwner);
    public bool IsKotlinRichEnumOwner(string ownerFqn)
    {
        var bare = BareOwnerFqn(ownerFqn);
        if (!_membersByOwner.TryGetValue(bare, out var members)) return false;
        var values = members.Count(m => m.IsStatic && m.Name == "values" && m.ParamCount == 0
            && m.ReturnTypeNode is TypeNode.Array { Elem: TypeNode.Fqn valueElem } && valueElem.Name == bare);
        var valueOf = members.Count(m => m.IsStatic && m.Name == "valueOf" && m.ParamCount == 1
            && m.ParamTypeNodes is { Length: 1 } && IsStringType(m.ParamTypeNodes[0])
            && m.ReturnTypeNode is TypeNode.Fqn valueType && valueType.Name == bare);
        return values == 1 && valueOf == 1;
    }
    public bool IsKotlinRichEnumStaticApi(string ownerFqn, string memberName, int paramCount)
    {
        var bare = BareOwnerFqn(ownerFqn);
        if (!IsKotlinRichEnumOwner(bare) || !_membersByOwner.TryGetValue(bare, out var members)) return false;
        return memberName switch
        {
            "values" when paramCount == 0 => members.Count(m => m.IsStatic && m.Name == memberName &&
                m.ParamCount == 0 && m.ReturnTypeNode is TypeNode.Array { Elem: TypeNode.Fqn elem } &&
                elem.Name == bare) == 1,
            "valueOf" when paramCount == 1 => members.Count(m => m.IsStatic && m.Name == memberName &&
                m.ParamCount == 1 && m.ParamTypeNodes is { Length: 1 } && IsStringType(m.ParamTypeNodes[0]) &&
                m.ReturnTypeNode is TypeNode.Fqn ret && ret.Name == bare) == 1,
            _ => false,
        };
    }
    static bool IsStringType(TypeNode type) => type is TypeNode.Fqn f &&
        f.Name is "kotlin.String" or "System.String" or "string";
    public string[] OwnerTypeParamNames(string ownerFqn) => _ownerTypeParams.GetValueOrDefault(ownerFqn);

    // Exact CLR metadata identity for a trusted external DotKt classifier. Local source declarations remain
    // authoritative and therefore never rewrite through a same-named reference.
    public IReadOnlyDictionary<string, string> PhysicalTypeNames =>
        _physicalTypeBySemanticName
            .Where(kv => !_localEmittedTypes.Contains(kv.Key))
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);

    // Kotlin inner applications arrive in BIR as [own..., outer...]. Trusted DotKt metadata supplies the number of
    // semantic outer slots for referenced declarations; TypeOwnershipLowering alone projects them to CLR's flattened
    // [outer..., own...] order. Accept both semantic dotted and exact physical nested spellings.
    public bool TryInnerCapturedCount(string ownerFqn, out int count) =>
        _innerCapturedCount.TryGetValue(StripGenericArity(DottedFqn(ownerFqn)), out count);
    public bool TryInnerSemanticOwner(string ownerFqn, out string semanticOwner) =>
        _innerSemanticOwner.TryGetValue(StripGenericArity(DottedFqn(ownerFqn)), out semanticOwner);
    // The declared param type names of the owner's (sole/first) constructor, or null. Keyed by the arity-stripped
    // Kotlin FQN (`dotkt$obj90`, not `dotkt$obj90``1`), matching the CIR `new` node's bare type token.
    public string[] OwnerCtorParamTypeNames(string ownerFqn)
    {
        if (string.IsNullOrEmpty(ownerFqn)) return null;
        return _ownerCtorParams.GetValueOrDefault(ownerFqn)
            ?? _ownerCtorParams.GetValueOrDefault(StripGenericArity(ownerFqn))
            ?? _ownerCtorParams.GetValueOrDefault(StripGenericArity(DottedFqn(ownerFqn)));
    }

    // The struct-ness ORACLE (#37/#48 nullability fold). True iff a CONCRETE Kotlin/CLR type FQN is a VALUE type
    // (a foundational primitive, or a ref.dll struct/enum). A value `T?` is `System.Nullable<T>` (keeps its wrapper);
    // a reference `T?` is a bare type + an NRT byte. Consulted by BirTypeLowering (the Nullable strip) and the decl
    // NRT-byte walk. Not for type VARIABLES — use TvConstraint for those. Foundational value primitives resolve from
    // the hardcoded seed even with no ref.dll; a ref.dll struct/enum resolves from the scanned `_ownerKind`.
    public bool IsValueTypeFqn(string fqn)
    {
        if (fqn == null) return false;
        if (ValueTypePrimitiveFqns.Contains(fqn)) return true;
        var bare = StripGenericArity(fqn);
        var kind = _ownerKind.GetValueOrDefault(bare);
        return kind == "struct" || kind == "enum";
    }

    // The BYREF-LIKE oracle. True iff a concrete type FQN is a `ref struct` — a value the CLR refuses as the type of an
    // INSTANCE FIELD of a non-byref-like type, hence one that cannot be spilled into a coroutine state machine or
    // captured by a closure class. Read from the referenced metadata's `IsByRefLike` (see [IsByRefLikeType]), so a
    // `ref struct` nobody has written yet answers the same way and no caller matches a type NAME.
    // `kotlin.clr.Span` is the one spelling that needs canonicalizing: it is kotc's intrinsic name for `System.Span<T>`
    // and BirTypeLowering rewrites it, but the storage decisions run BEFORE that pass and so see the intrinsic token.
    // Both spellings come from the ONE pair of constants there, so this is the same identity that lowering asserts,
    // not a second fact.
    public bool IsByRefLikeFqn(string fqn)
    {
        if (fqn == null) return false;
        var bare = StripGenericArity(fqn);
        if (bare == BirTypeLowering.SpanIntrinsicFqn) bare = BirTypeLowering.SpanClrFqn;
        return _byRefLikeOwners.Contains(bare);
    }

    // The CLR generic-parameter constraint class of a type variable declared on `ownerFqn` at flattened index `i`:
    // "struct" (a value-type constraint -> a `T?` is `Nullable<T>`), "class" (a reference constraint -> bare + NRT),
    // or "unconstrained"/null (unknown -> treated as reference by the caller's sound fallback). Recorded from the
    // ref.dll's GenericParameterAttributes during the scan; empty when the owner is a local type / not on the ref.dll.
    public string TvConstraint(string ownerFqn, int i)
    {
        if (ownerFqn == null) return null;
        var arr = _ownerTypeParamConstraints.GetValueOrDefault(StripGenericArity(ownerFqn));
        return arr != null && i >= 0 && i < arr.Length ? arr[i] : null;
    }

    // The declared type-BOUND of a REFERENCED generic's type param at flattened index `i` (`Key<E : Element>` -> Element
    // for i=0), keyed by the DOTTED FQN kotc emits. Null when the owner is a local type / unconstrained / the bound is
    // objectish. Drives StarProjectionBoundLowering's `Key<object>` -> `Key<Element>` repointing for a referenced owner.
    public TypeNode TvBound(string ownerFqn, int i)
    {
        if (ownerFqn == null) return null;
        var arr = _ownerTypeParamBounds.GetValueOrDefault(StripGenericArity(ownerFqn));
        return arr != null && i >= 0 && i < arr.Length ? arr[i] : null;
    }

    // Deterministic synthetic owners (for example an F-bound star-view interface) are part of a DotKt reference's
    // ordinary type surface.  bir2cir may target one only when it is actually present; this avoids assuming that an
    // arbitrary CLR library opted into a compiler-private ABI convention merely because its generic shape looks alike.
    public bool HasDotKtOwner(string ownerFqn) =>
        ownerFqn != null && _dotKtOwners.Contains(StripGenericArity(DottedFqn(ownerFqn)));

    public bool TryCompanionIsStatic(string physicalOwner, out bool isStatic) =>
        _companionStaticByPhysicalOwner.TryGetValue(
            StripGenericArity(DottedFqn(physicalOwner)), out isStatic);

    // A late compiler synthesis can name a Kotlin companion member by its SEMANTIC owner after NetInteropBinding has
    // already run (SuspendColdLowering's Result factories are the current producer). Recover the exact singleton
    // carrier only from the validated trusted association; no suffix/name reconstruction is permitted.
    public bool TrySingletonCompanionCarrier(string semanticOwner, out string carrier) =>
        _singletonCompanionCarrierBySemanticOwner.TryGetValue(StripGenericArity(semanticOwner), out carrier);

    public bool TryCompanionCarrierByPhysicalOwner(string physicalOwner, out string carrier) =>
        _companionCarrierByPhysicalOwner.TryGetValue(
            StripGenericArity(DottedFqn(physicalOwner)), out carrier);

    public bool TryCompanionAccessor(string physicalOwner, string memberName, out string carrier)
    {
        carrier = null;
        var key = StripGenericArity(DottedFqn(physicalOwner));
        return memberName != null &&
            _companionSourceNameByPhysicalOwner.TryGetValue(key, out var sourceName) &&
            memberName == sourceName &&
            _companionCarrierByPhysicalOwner.TryGetValue(key, out carrier);
    }

    public bool TryCompanionPhysicalOwner(string semanticType, out string physicalOwner) =>
        _companionPhysicalOwnerBySemanticType.TryGetValue(
            StripGenericArity(DottedFqn(semanticType)), out physicalOwner);

    public bool TryCompanionSemanticType(string physicalOwner, out string semanticType) =>
        _companionSemanticTypeByPhysicalOwner.TryGetValue(
            StripGenericArity(DottedFqn(physicalOwner)), out semanticType);

    public bool TryCompanionSemanticOwner(string physicalCarrier, out string semanticOwner) =>
        _semanticOwnerByCompanionCarrier.TryGetValue(
            StripGenericArity(DottedFqn(physicalCarrier)), out semanticOwner);

    // Recover the exact reflected carrier token from an already-physical dotted CIR/KLIB token. Both directions are
    // explicit trusted metadata associations; no `$` suffix or nested-boundary inference participates.
    public bool TryCompanionMetadataCarrier(string physicalType, out string metadataCarrier)
    {
        metadataCarrier = null;
        return TryCompanionSemanticType(physicalType, out var semanticType) &&
            _companionPhysicalOwnerBySemanticType.TryGetValue(semanticType, out metadataCarrier);
    }

    public Type ResolveCompanionMetadataCarrier(string physicalType, int genericArity = 0)
    {
        if (!TryCompanionMetadataCarrier(physicalType, out var metadataCarrier))
            throw new InvalidOperationException(
                $"trusted companion physical type '{physicalType}' has no exact metadata carrier mapping");
        return ProbeNetType(metadataCarrier, genericArity) ?? throw new InvalidOperationException(
            $"trusted companion metadata carrier '{metadataCarrier}' is absent from the exact compile references");
    }

    // The frontend deliberately preserves the Kotlin surface of a CLR static class as an `object`: an event read on
    // that object therefore reaches BIR with an INSTANCE-looking receiver. Static/instance is nevertheless a CLR ABI
    // fact. Resolve it from the referenced declaration here so ClrEventSubscriptionBinding can produce final CIR
    // without asking ilemit to reinterpret the member. Refuse hierarchy collisions instead of selecting the first
    // reflection result.
    public bool TryClrEventIsStatic(string ownerFqn, string eventName, out bool isStatic)
    {
        isStatic = false;
        if (ownerFqn == null || eventName == null) return false;
        var type = ResolveNetType(BareOwnerFqn(ownerFqn), 0);
        if (type == null) return false;

        const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
            | BindingFlags.Static | BindingFlags.DeclaredOnly;
        var matches = new List<bool>();
        var seen = new HashSet<Type>();
        var stack = new Stack<Type>();
        stack.Push(type);
        while (stack.Count > 0)
        {
            var cur = stack.Pop();
            if (cur == null || !seen.Add(cur)) continue;
            try
            {
                foreach (var ev in cur.GetEvents(Flags))
                {
                    if (ev.Name != eventName) continue;
                    var accessor = ev.GetAddMethod(nonPublic: true) ?? ev.GetRemoveMethod(nonPublic: true);
                    if (accessor != null) matches.Add(accessor.IsStatic);
                }
            }
            catch { /* an incomplete metadata dependency cannot establish a unique declaration */ }
            Type baseType = null;
            try { baseType = cur.BaseType; } catch { }
            if (baseType != null) stack.Push(baseType);
            try { foreach (var i in cur.GetInterfaces()) stack.Push(i); } catch { }
        }

        if (matches.Count != 1) return false;
        isStatic = matches[0];
        return true;
    }

    // Resolve the physical member exposed by a referenced generic's compiler-generated existential. A member whose
    // signature mentions an owner type parameter cannot reuse the
    // Kotlin name: on G<object> the erased bridge would collide with the real closed-generic slot, so
    // FBoundStarProjectionErasure gives it a deterministic `$dotkt_star$<name>$<ordinal>` name.
    //
    // This is reference metadata, not a spelling guess: the trusted type-level [KotlinType(G<*,...>)] relation names
    // the emitted existential owner, then select a unique name+arity slot from its actual
    // member table.  The caller retains the Kotlin vocabulary until bir2cir asks this index for the
    // concrete CIR owner/member pair.
    public bool TryStarProjectionMember(string ownerFqn, string memberName, int paramCount,
        out string erasedOwner, out string erasedMember)
    {
        erasedOwner = erasedMember = null;
        if (ownerFqn == null || memberName == null) return false;
        if (!TryExistentialPhysicalOwner(ownerFqn, out var candidateOwner)
            || !TryMembersByBirOwner(candidateOwner, out var members)) return false;

        var prefix = "$dotkt_star$" + memberName + "$";
        var candidates = members
            .Where(m => !m.IsStatic && m.ParamCount == paramCount
                && (m.Name == memberName || m.Name.StartsWith(prefix, StringComparison.Ordinal)))
            .Select(m => m.Name)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (candidates.Count != 1) return false;
        erasedOwner = candidateOwner;
        erasedMember = candidates[0];
        return true;
    }

    // Recover the physical CLR signature hidden behind a KotlinType-restored existential surface. dll2klib correctly
    // presents `G<*>` to the frontend, but calls in CIR must use the referenced DLL's actual existential slots.
    // Require a unique declaration by staticness/name/generic-arity/parameter-count and require that at least one
    // signature position names a provenance-verified existential owner.
    public bool TryExistentialAbiMember(string ownerToken, string memberName, bool isStatic, int methodArity,
        int paramCount, out TypeNode[] parameters, out TypeNode result)
    {
        parameters = null;
        result = null;
        if (ownerToken == null || memberName == null
            || !TryMembersByBirOwner(BareOwnerFqn(ownerToken), out var members)) return false;
        var candidates = members.Where(m => m.IsStatic == isStatic && m.Name == memberName
            && m.MethodArity == methodArity && m.ParamCount == paramCount
            && m.ParamTypeNodes != null && m.ReturnType != null).ToList();
        if (candidates.Count != 1) return false;
        var match = candidates[0];
        if (!ContainsExistential(match.ReturnType)
            && !match.ParamTypeNodes.Any(ContainsExistential)) return false;
        parameters = match.ParamTypeNodes;
        result = match.ReturnType;
        return true;
    }

    bool ContainsExistential(TypeNode type) => type switch
    {
        TypeNode.Fqn f => IsExistentialPhysicalOwner(f.Name)
            || (f.Args?.Any(ContainsExistential) ?? false),
        TypeNode.Nullable n => ContainsExistential(n.Of),
        TypeNode.Oblivious o => ContainsExistential(o.Of),
        TypeNode.Array a => ContainsExistential(a.Elem),
        TypeNode.ByRef b => ContainsExistential(b.Of),
        TypeNode.Fn fn => ContainsExistential(fn.Ret) || fn.Params.Any(ContainsExistential)
            || (fn.Recv != null && ContainsExistential(fn.Recv)),
        _ => false,
    };

    // The @ClrProperty accessor binding for owner.member: its READ/WRITE access flags + the .NET property name. Routes the
    // call EXPLICITLY to clrPropGet/clrPropSet (no get_/set_ string-prefix sniff). Overload-disambiguated by arg count.
    public bool TryMemberProperty(string ownerFqn, string memberName, int argCount, out int access, out string name)
    {
        access = 0; name = null;
        if (!TryMembersByBirOwner(ownerFqn, out var list)) return false;
        var cands = list.Where(m => m.Name == memberName && m.PropertyName != null).ToList();
        if (cands.Count == 0) return false;
        var pick = cands.FirstOrDefault(m => m.ParamCount == argCount);
        if (pick == null)
        {
            // Failure posture (LOUD): no exact-arity match. A single candidate is unambiguous (use it); MULTIPLE
            // candidates that DISAGREE on the bound property are a genuine routing ambiguity — refuse rather than
            // pick an arbitrary overload (which would bind the wrong .NET property).
            if (cands.Select(c => (c.PropertyAccess, c.PropertyName)).Distinct().Count() > 1)
                throw new InvalidOperationException(
                    $"ambiguous @ClrProperty overload for {ownerFqn}.{memberName} (argCount={argCount}): candidate arities " +
                    $"[{string.Join(",", cands.Select(c => c.ParamCount))}] bind different properties — no exact-arity match");
            pick = cands[0];
        }
        access = pick.PropertyAccess; name = pick.PropertyName;
        return true;
    }

    // The @ClrConv numeric-conversion binding for owner.member: its conv TARGET (the callee's own return-type token, a
    // pre-lowering Kotlin FQN like `kotlin.Long`). Returns true when owner.member (arg count matched when possible) is a
    // @ClrConv-marked conversion — MemberCallSubstitution then emits `{k:conv, to:<convTo>, e:<recv>}`. A conversion is
    // nullary, so arg count is always 0; the arity match is kept for symmetry with the other member lookups.
    public bool TryMemberConv(string ownerFqn, string memberName, int argCount, out string convTo)
    {
        convTo = null;
        if (!TryMembersByBirOwner(ownerFqn, out var list)) return false;
        var cands = list.Where(m => m.Name == memberName && m.Conv).ToList();
        if (cands.Count == 0) return false;
        var pick = cands.FirstOrDefault(m => m.ParamCount == argCount) ?? cands[0];
        convTo = pick.ConvTo;
        return convTo != null;
    }

    // Whether this exact declaration overload carries any authored member-level CLR binding. This is intentionally
    // stricter than the individual substitution lookups, whose single-candidate fallback supports legacy call sites:
    // deciding whether a nested companion call may cross onto its aliased semantic outer must never let a differently
    // shaped bound overload capture an intrinsic-less real carrier body of the same Kotlin name. Generic arity and the
    // complete declaration vector are both part of the identity; a same-name/same-arity sibling is not evidence.
    public bool HasExactMemberClrBinding(string ownerFqn, string memberName, int methodArity,
        IReadOnlyList<TypeNode> signature) =>
        TryExactMemberClrBinding(ownerFqn, memberName, methodArity, signature, out _);

    internal bool TryExactMemberClrBinding(string ownerFqn, string memberName, int methodArity,
        IReadOnlyList<TypeNode> signature, out ExactClrMemberBinding binding)
    {
        binding = null;
        var matches = ExactBoundMembers(ownerFqn, memberName, methodArity, signature);
        if (matches.Count != 1) return false;
        var match = matches[0];
        binding = new ExactClrMemberBinding(match.Intrinsic, match.PropertyAccess, match.PropertyName,
            match.Conv, match.ConvTo, match.ByrefPositions);
        return true;
    }

    // Callable references are reshaped before MemberCallSubstitution. Give that earlier pass the same exact-overload
    // authority, and only expose an intrinsic method name: properties/conversions have different node vocabularies and
    // cannot be represented by a method delegate without an explicit lowering of their own.
    public bool TryExactMemberIntrinsic(string ownerFqn, string memberName, int methodArity,
        IReadOnlyList<TypeNode> signature, out string intrinsic)
    {
        intrinsic = null;
        if (!TryExactMemberClrBinding(ownerFqn, memberName, methodArity, signature, out var binding)
            || binding.Intrinsic == null) return false;
        intrinsic = binding.Intrinsic;
        return true;
    }

    List<MemberBinding> ExactBoundMembers(string ownerFqn, string memberName, int methodArity,
        IReadOnlyList<TypeNode> signature)
    {
        if (memberName == null || signature == null || !TryMembersByBirOwner(ownerFqn, out var list))
            return new List<MemberBinding>();
        var candidates = list.Where(m => m.Name == memberName && m.MethodArity == methodArity
            && m.ParamTypeNodes is { } ps && ps.Length == signature.Count
            && (m.Intrinsic != null || m.PropertyName != null || m.Conv)).ToList();
        var exact = candidates.Where(m => m.ParamTypeNodes.SequenceEqual(signature)).ToList();
        if (exact.Count > 0) return exact;
        return candidates.Where(m => m.ParamTypeNodes
            .Select((p, i) => DeclarationDescribesCall(p, signature[i])).All(x => x)).ToList();
    }

    // The @ClrIntrinsic BCL member name for owner.member (overload-disambiguated by arg count when possible).
    public bool TryMemberIntrinsic(string ownerFqn, string memberName, int argCount, out string intrinsic)
    {
        intrinsic = null;
        if (!TryMembersByBirOwner(ownerFqn, out var list)) return false;
        var cands = list.Where(m => m.Name == memberName && m.Intrinsic != null).ToList();
        if (cands.Count == 0) return false;
        var pick = cands.FirstOrDefault(m => m.ParamCount == argCount);
        if (pick == null)
        {
            // Failure posture (LOUD): no exact-arity match. A single candidate is unambiguous; MULTIPLE candidates
            // binding DIFFERENT BCL members are a genuine ambiguity — refuse rather than pick an arbitrary overload.
            if (cands.Select(c => c.Intrinsic).Distinct(StringComparer.Ordinal).Count() > 1)
                throw new InvalidOperationException(
                    $"ambiguous @ClrIntrinsic overload for {ownerFqn}.{memberName} (argCount={argCount}): candidate arities " +
                    $"[{string.Join(",", cands.Select(c => c.ParamCount))}] bind different BCL members — no exact-arity match");
            pick = cands[0];
        }
        intrinsic = pick.Intrinsic;
        return true;
    }

    // STRICT overload-exact @ClrIntrinsic lookup for the DECLARATION rename: the marker's arity is precise (Kotlin
    // override resolution), so `add(element)` (arity 1, ->Add) must NOT fall through to `add(index,element)` (arity 2,
    // ->Insert). Unlike TryMemberIntrinsic there is no `?? cands[0]` arity fallback — no exact-arity match = no rename.
    public bool TryMemberIntrinsicExact(string ownerFqn, string memberName, int argCount, out string intrinsic)
    {
        intrinsic = TryMembersByBirOwner(ownerFqn, out var list)
            ? list.FirstOrDefault(m => m.Name == memberName && m.Intrinsic != null && m.ParamCount == argCount)?.Intrinsic
            : null;
        return intrinsic != null;
    }

    // FULL-SIGNATURE @ClrIntrinsic lookup for the member-STRIP: is owner.name(paramKeys) a bound stub? Matches the
    // @ClrIntrinsic member whose canonicalized param types equal the emitted method's — so `StringBuilder.append(Char)`
    // (@ClrIntrinsic, dropped) is distinguished from `append(CharSequence?)` (rule-3, kept), which share name+arity.
    public bool IsBoundStub(string ownerFqn, string memberName, IReadOnlyList<string> birParamKeys)
    {
        if (!TryMembersByBirOwner(ownerFqn, out var list)) return false;
        return list.Any(m => m.Name == memberName && m.Intrinsic != null && m.ParamTypes != null
            && m.ParamTypes.Length == birParamKeys.Count
            && m.ParamTypes.Select(ParamKey).SequenceEqual(birParamKeys));
    }

    // Canonicalize a type token (a kotc birType or a ref.dll reflected TypeName) to a comparable identity for signature
    // matching: unwrap byref/array/nullable, drop the clr/@ marker + generic args, collapse a type param, fold primitives.
    // Deliberately shallow (top-level identity) — enough to separate the real overloads without full structural matching.
    public static string ParamKey(string t)
    {
        t = t.Trim();
        if (t.EndsWith("?", StringComparison.Ordinal)) t = t[..^1];
        foreach (var w in new[] { "byref:", "array:", "nullable:" })
            if (t.StartsWith(w, StringComparison.Ordinal)) return w + ParamKey(t[w.Length..]);
        foreach (var p in new[] { "clrg:", "clr:", "@" })
            if (t.StartsWith(p, StringComparison.Ordinal)) { t = t[p.Length..]; break; }
        // `sfunc:` (suspend fn TYPE) erases to `object`: a suspend-lambda VALUE is a SuspendLambda state-machine
        // OBJECT (a Continuation-based object), NOT a Func delegate — so it keys as `obj`, matching an intrinsic's
        // object-erased suspend param/receiver. A plain `func:` still keys as the delegate bucket.
        if (t.StartsWith("sfunc:", StringComparison.Ordinal)) return "obj";
        if (t.StartsWith("func:", StringComparison.Ordinal)) return "func";
        var br = t.IndexOf('[');
        if (br >= 0) t = t[..br];
        if (t.StartsWith("gp:", StringComparison.Ordinal)) return "gp";
        return t switch
        {
            "kotlin.Byte" or "System.SByte" or "sbyte" => "i8",             // signed 8-bit; token "sbyte" IS kotlin.Byte (System.SByte)
            "kotlin.Short" or "System.Int16" or "short" => "i16",
            "kotlin.Int" or "System.Int32" or "int" => "i32",
            "kotlin.Long" or "System.Int64" or "long" => "i64",
            "kotlin.Float" or "System.Single" or "float" => "f32",
            "kotlin.Double" or "System.Double" or "double" => "f64",
            "kotlin.Boolean" or "System.Boolean" or "bool" => "bool",
            "kotlin.Char" or "System.Char" or "char" => "char",
            "kotlin.String" or "System.String" or "string" => "str",
            "kotlin.Unit" or "System.Void" or "void" => "void",
            "kotlin.Any" or "System.Object" or "object" => "obj",
            // Unsigned scalars, folded like every other primitive: the specialized ARRAYS were already folded below, but
            // the element types were not, so a `UInt` parameter keyed as `kotlin.UInt` from a pre-lowering call site and
            // as `uint` from a reference assembly — two spellings of one type that no signature compare could match.
            "kotlin.UByte" or "System.Byte" or "byte" => "byte",
            "kotlin.UShort" or "System.UInt16" or "ushort" => "ushort",
            "kotlin.UInt" or "System.UInt32" or "uint" => "uint",
            "kotlin.ULong" or "System.UInt64" or "ulong" => "ulong",
            // Primitive-array class spellings (kotc lowers to `array:int`, but the ref.dll may reflect the kotlin.IntArray
            // class) -> the same array key so a top-level `sort(IntArray)`@ClrIntrinsic matches by signature.
            "kotlin.IntArray" => "array:i32",
            "kotlin.LongArray" => "array:i64",
            "kotlin.ByteArray" => "array:i8",
            "kotlin.ShortArray" => "array:i16",
            "kotlin.FloatArray" => "array:f32",
            "kotlin.DoubleArray" => "array:f64",
            "kotlin.BooleanArray" => "array:bool",
            "kotlin.CharArray" => "array:char",
            // Unsigned specialized arrays (#53): native System.Byte[]/UInt16[]/UInt32[]/UInt64[]. Same array key as
            // their element token so an @ClrIntrinsic signature over the ref.dll spelling matches.
            "kotlin.UByteArray" => "array:byte",
            "kotlin.UShortArray" => "array:ushort",
            "kotlin.UIntArray" => "array:uint",
            "kotlin.ULongArray" => "array:ulong",
            _ => StripGenericArity(t),
        };
    }

    // ParamKey over a STRUCTURED Type node (a birType-emitted param slot) — walks the TypeNode natively (never
    // re-renders a legacy token), matching the string ParamKey's top-level-identity canonicalization exactly:
    // byref/array/nullable unwrap-with-marker, a fn -> obj (suspend) / func, a type-var -> gp, an Fqn leaf folded via
    // the shared primitive switch (delegating to ParamKey(f.Name) — a bare FQN the switch already handles).
    public static string ParamKey(TypeNode t) => t switch
    {
        TypeNode.ByRef b => "byref:" + ParamKey(b.Of),
        TypeNode.Array a => "array:" + ParamKey(a.Elem),
        TypeNode.Nullable n => "nullable:" + ParamKey(n.Of),
        TypeNode.Fn fn => fn.Suspend ? "obj" : "func",
        TypeNode.Tv => "gp",
        TypeNode.Fqn f => ParamKey(f.Name),
        _ => "obj",
    };

    // ParamKey off a JSON type slot: a structured `{t:…}` node walks natively; a legacy string slot (sig-side token)
    // keeps the string path.
    public static string ParamKey(JsonNode typeSlot)
    {
        if (TypeJson.Read(typeSlot) is TypeNode tn) return ParamKey(tn);
        if (typeSlot is JsonValue v && v.TryGetValue<string>(out var s)) return ParamKey(s);
        return ParamKey("");
    }

    // A top-level fun (file-class static, called as `callStatic owner=null`) bound by @ClrIntrinsic to a
    // fully-qualified BCL static (e.g. clrTimestamp -> "System.Diagnostics.Stopwatch.GetTimestamp").
    public bool TryTopLevelIntrinsic(string funName, out string fqStatic) =>
        _topLevelIntrinsics.TryGetValue(funName, out fqStatic);

    // Overload-disambiguated variant: a top-level @ClrIntrinsic name that binds to DIFFERENT BCL statics per overload
    // — kotlin.math `sqrt`/`abs`/`pow`/... -> System.Math.* for Double/Int/Long but System.MathF.* for Float. Keyed by
    // name|<ParamKey-joined signature> so a call resolves the EXACT intrinsic overload (and a non-intrinsic sibling
    // overload, e.g. `Double.pow(Int)`, correctly MISSES here and falls through to its real Kotlin body). `sigKey` is
    // the call's ParamKey-normalized signature. This is what lets the by-name-first-wins map stop shadowing MathF.
    public bool TryTopLevelIntrinsicBySig(string funName, string sigKey, out string fqStatic) =>
        _topLevelIntrinsicsBySig.TryGetValue(funName + "|" + sigKey, out fqStatic);

    // Whether a top-level intrinsic NAME binds to more than one distinct BCL static across its overloads (sqrt/abs/
    // pow -> Math vs MathF). For such names the name-only fallback is UNSAFE (it would pick an arbitrary overload), so
    // the caller must require an exact signature match; single-static names still fall back by name.
    public bool IsAmbiguousTopLevelIntrinsic(string funName) => _ambiguousTopLevelIntrinsics.Contains(funName);

    // Whether the ref.dll ALSO has a NON-intrinsic (real-Kotlin-body) top-level fun of this name. Such a name is
    // unsafe for the NAME-ONLY intrinsic fallback even when every intrinsic overload agrees on one BCL static:
    // `sort` binds all 8 primitive-array overloads to "System.Array.Sort" (so it is NOT "ambiguous"), but
    // `MutableList<T>.sort()` / `Array<out T>.sort()` are real bodies — the name fallback rewrote the real-bodied
    // call inside the compiled `sorted()` to an open-generic `Array.Sort` ("not fully instantiated" at runtime).
    // With a real-bodied sibling present, only the sig-EXACT intrinsic match may substitute.
    public bool HasNonIntrinsicTopLevel(string funName) => _topLevelStatics.ContainsKey(funName);

    // The 0-based parameter positions a top-level @ClrIntrinsic fun's bound BCL static takes BY REFERENCE
    // (@ClrRefArgument). Empty when none — the substituted call then wraps no argTypes.
    public int[] TopLevelByrefPositions(string funName) =>
        _topLevelIntrinsicByref.TryGetValue(funName, out var pos) ? pos : Array.Empty<int>();

    // The 0-based parameter positions a bound MEMBER (owner.member, overload-matched by arg count) takes BY REFERENCE
    // (@ClrRefArgument). Empty when none.
    public int[] MemberByrefPositions(string ownerFqn, string memberName, int argCount)
    {
        if (!TryMembersByBirOwner(ownerFqn, out var list)) return Array.Empty<int>();
        var cands = list.Where(m => m.Name == memberName && m.ByrefPositions != null && m.ByrefPositions.Length > 0).ToList();
        if (cands.Count == 0) return Array.Empty<int>();
        var pick = cands.FirstOrDefault(m => m.ParamCount == argCount);
        if (pick == null)
        {
            // Failure posture (LOUD): no exact-arity match. A single candidate is unambiguous; MULTIPLE candidates
            // with DIFFERENT byref positions are a genuine ambiguity — refuse rather than pick an arbitrary overload.
            if (cands.Select(c => string.Join(",", c.ByrefPositions)).Distinct(StringComparer.Ordinal).Count() > 1)
                throw new InvalidOperationException(
                    $"ambiguous @ClrRefArgument byref overload for {ownerFqn}.{memberName} (argCount={argCount}): candidate " +
                    $"arities [{string.Join(",", cands.Select(c => c.ParamCount))}] disagree on byref positions — no exact-arity match");
            pick = cands[0];
        }
        return pick.ByrefPositions;
    }

    // A NON-intrinsic top-level fun (real Kotlin body) resolved to the file-class it lives in, so an APP's
    // `callStatic owner=null` gets an explicit owner ilemit reflects against the referenced runtime stdlib. When the
    // name is defined in multiple file-classes (getOrElse in CollectionsKt/ArraysKt/MapsKt/...), the call's receiver
    // type (recvKey = its first sig param's bare owner) disambiguates. A single candidate needs no receiver match.
    public bool TryResolveTopLevelStatic(string funName, string recvKey, out string owner) =>
        TryResolveTopLevelStatic(funName, recvKey, null, out owner);

    // Resolve the declaration signature of an already-attributed referenced static call while its frontend Kotlin
    // descriptor is still available. That descriptor can differ from the metadata declaration at an intentional
    // erasure seam (`generateSequence(seed: T?, next: (T)->T?)` reflects as `T, Func<T,object>`). Method generic arity
    // + parameter count normally identify one overload; when several remain, accept an exact/ABI-equivalent semantic
    // shape only. Identical duplicate declarations collapse to one structural shape. No first-pick is performed.
    public bool TryResolveStaticMemberSignature(string ownerFqn, string name, int methodArity,
        IReadOnlyList<TypeNode> callSignature, out TypeNode[] declarationSignature)
    {
        declarationSignature = null;
        if (ownerFqn == null || name == null || callSignature == null)
            return false;
        var bareOwner = BareOwnerFqn(ownerFqn);
        var ownerArity = _ownerArity.TryGetValue(bareOwner, out var oa) ? oa : 0;
        var owner = ResolveRefType(bareOwner, ownerArity);
        if (owner == null)
            return false;
        var candidates = owner.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            .Where(m => m.Name == name && m.GetGenericArguments().Length == methodArity
                && m.GetParameters().Length == callSignature.Count)
            .Select(m => m.GetParameters().Select(p => DeclarationTypeNode(p.ParameterType)).ToArray())
            .Where(ps => ps.All(p => p != null))
            .ToList();
        if (candidates.Count == 0)
            return false;

        var exact = candidates.Where(ps => ps.SequenceEqual(callSignature)).ToList();
        var compatible = exact.Count > 0
            ? exact
            : candidates.Where(ps => ps.Select((p, i) => DeclarationDescribesCall(p, callSignature[i])).All(x => x))
                .ToList();
        var source = compatible.Count > 0 ? compatible : candidates;
        var shapes = source
            .GroupBy(ps => string.Join(",", ps.Select(TypeNode.ToJson)), StringComparer.Ordinal)
            .Select(g => g.First())
            .ToList();
        if (shapes.Count != 1)
            return false;
        declarationSignature = shapes[0];
        return true;
    }

    // BIR's resolved Kotlin descriptor can retain semantic nullability that the metadata-only ref declaration has
    // already erased (`T?` parameter -> !!T, function return T? -> object). Compare only those ABI-equivalent seams;
    // nominal/function shape and Tv scope/index remain exact so sibling overloads cannot collapse.
    static bool DeclarationDescribesCall(TypeNode declaration, TypeNode call)
    {
        if (declaration == call) return true;
        if (declaration is TypeNode.Oblivious dOb)
            return DeclarationDescribesCall(dOb.Of, call);
        if (call is TypeNode.Oblivious cOb)
            return DeclarationDescribesCall(declaration, cOb.Of);
        if (call is TypeNode.Nullable cNull)
        {
            if (declaration is TypeNode.Tv dt && cNull.Of == dt) return true;
            if (declaration is TypeNode.Fqn df && cNull.Of is TypeNode.Fqn cf)
                return DeclarationDescribesCall(df, cf);
        }
        if (declaration is TypeNode.Fqn { Args: null } erased
            && ParamKey(erased) == "obj"
            && call is TypeNode.Nullable { Of: TypeNode.Tv })
            return true;
        if (declaration is TypeNode.Fqn dfqn && call is TypeNode.Fqn cfqn)
        {
            if (ParamKey(dfqn) != ParamKey(cfqn)) return false;
            if (dfqn.Args == null || cfqn.Args == null) return dfqn.Args == null && cfqn.Args == null;
            return dfqn.Args.Length == cfqn.Args.Length
                && dfqn.Args.Select((p, i) => DeclarationDescribesCall(p, cfqn.Args[i])).All(x => x);
        }
        if (declaration is TypeNode.Nullable dn && call is TypeNode.Nullable cn)
            return DeclarationDescribesCall(dn.Of, cn.Of);
        if (declaration is TypeNode.Array da && call is TypeNode.Array ca)
            return DeclarationDescribesCall(da.Elem, ca.Elem);
        if (declaration is TypeNode.ByRef db && call is TypeNode.ByRef cb)
            return DeclarationDescribesCall(db.Of, cb.Of);
        if (declaration is TypeNode.Fn dfn && call is TypeNode.Fn cfn)
        {
            if (dfn.Suspend != cfn.Suspend || dfn.Params.Length != cfn.Params.Length) return false;
            if (dfn.Clr != null && cfn.Clr != null && dfn.Clr != cfn.Clr) return false;
            return DeclarationDescribesCall(dfn.Ret, cfn.Ret)
                && dfn.Params.Select((p, i) => DeclarationDescribesCall(p, cfn.Params[i])).All(x => x)
                && (dfn.Recv == null
                    ? cfn.Recv == null
                    : cfn.Recv != null && DeclarationDescribesCall(dfn.Recv, cfn.Recv));
        }
        return false;
    }

    public bool TryResolveTopLevelStatic(string funName, string recvKey, string firstParamKey, out string owner)
    {
        owner = null;
        if (!_topLevelStatics.TryGetValue(funName, out var cands) || cands.Count == 0) return false;
        if (cands.Count == 1) { owner = cands[0].Owner; return true; }
        // When the coarse recvKey collapsed an ARRAY receiver to "[]" it is lossy — IntArray/CharArray/... AND the
        // unsigned specialized arrays AND the generic Array<T> all share "[]", so the plain recvKey loop below would pin
        // the FIRST array overload (the signed generic `toList<T>(T[])`) for EVERY array call, miscompiling an unsigned
        // `ubyteArrayOf(..).toList()` onto _ArraysKt's uninstantiated generic. The fine first-param ParamKey pins the
        // exact file-class+overload (UByteArray -> "array:byte" -> UArraysKt). Only "[]" is lossy; a normal owner recvKey
        // is already exact, so gate on it to leave every non-array resolution byte-identical. (#153)
        if (recvKey == "[]" && firstParamKey != null)
            foreach (var c in cands)
                if (NoRecvNull(c.ParamKey) == NoRecvNull(firstParamKey)) { owner = c.Owner; return true; }
        // The candidate RecvKey is the ref.dll's Kotlin receiver type (`kotlin.collections.List`); the call site's
        // recvKey may already be that type's @ClrTypeAlias CLR form (`System.Collections.Generic.IReadOnlyList`), when
        // kotc rendered the receiver local as its CLR alias (e.g. `val xs = listOf(...)` used only via an extension).
        // Match through the alias so the overload disambiguates in either representation. (The forward alias map is
        // unambiguous; a bare-Kotlin recvKey still matches the plain `c.RecvKey == recvKey` arm.)
        foreach (var c in cands)
            if (c.RecvKey == recvKey || (_ownerAlias.TryGetValue(c.RecvKey, out var aliased) && aliased == recvKey))
            { owner = c.Owner; return true; }
        // The receiver key didn't disambiguate the OVERLOAD, but if every candidate lives in the SAME file-class the
        // OWNER is still unambiguous (e.g. both `runCatching(Func)` and `T.runCatching(Func)` are in kotlin.ResultKt).
        // Emit the shared owner; ilemit's FindMethod then selects the exact overload by signature.
        var owners = cands.Select(c => c.Owner).Distinct().ToList();
        if (owners.Count == 1) { owner = owners[0]; return true; }
        return false;
    }

    // The declared RETURN type of a bound member (owner.name, matched by arg count then by name), from the ref.dll —
    // used by StaticType (#59) to recover a call / field read whose BIR node carries NO `ret` (kotc emits `ret` only for
    // a GENERIC call). null when the owner/member is unknown or its return type was not structurable (a delegate/gp).
    // `firstParamKey` (the call's first-arg ParamKey) disambiguates a same-name/same-arity overload set that a coarse
    // name+count match would resolve to the WRONG sibling: the primitive-array `IntArray.toList` (first param `int[]` ->
    // "array:i32", returning `List<Int>`) vs the generic `Array<out T>.toList` (first param `Array<T>` -> "array:gp",
    // returning `List<Tv>`) — both in ArraysKt. Picking the generic sibling's `List<Tv>` leaves the element unbound and
    // erases it to `object`, so `println(intArrayOf(1,2).toList())` wrapped in clrCollToString<object> then rejects the
    // `IReadOnlyList<int32>` stack (#153). PREFER the first-param-key match; fall back to the coarse first-match when no
    // key is supplied or none matches (monotone — only previously-arbitrary picks change).
    public TypeNode TryMemberReturn(string ownerFqn, string name, int argCount, string firstParamKey = null)
    {
        if (ownerFqn == null || !TryMembersByBirOwner(ownerFqn, out var list)) return null;
        if (firstParamKey != null
            && list.FirstOrDefault(b => b.Name == name && b.ParamCount == argCount && b.ReturnType != null
                    && b.ParamTypes is { Length: > 0 } && NoRecvNull(ParamKey(b.ParamTypes[0])) == NoRecvNull(firstParamKey)) is { } keyed)
            return keyed.ReturnType;
        return (list.FirstOrDefault(b => b.Name == name && b.ParamCount == argCount && b.ReturnType != null)
                ?? list.FirstOrDefault(b => b.Name == name && b.ReturnType != null))?.ReturnType;
    }

    // Resolve the exact cross-module ABI boundary produced by
    // UncheckedGenericCastReturnErasure: the referenced CLR method physically returns Object while its trusted
    // [KotlinType] return carrier records the source-level generic type variable.  The caller supplies the already
    // attributed file-class/member owner plus method shape; an ambiguous overload is deliberately not guessed.
    // The returned node is the DECLARATION type (normally a method/type Tv), which the call-side pass substitutes with
    // the call's owner/type arguments before accepting the frontend's `sty` as the CIR result conversion.
    public bool TryUncheckedGenericCastReturn(string ownerFqn, string name, bool isStatic, int argCount,
        int methodArity, out TypeNode kotlinReturn)
    {
        kotlinReturn = null;
        if (ownerFqn == null || name == null
            || !TryMembersByBirOwner(BareOwnerFqn(ownerFqn), out var list))
            return false;
        var shapeMatches = list.Where(m =>
                m.Name == name
                && m.IsStatic == isStatic
                && m.ParamCount == argCount
                && m.MethodArity == methodArity)
            .ToArray();
        // The BIR signature is the Kotlin surface and can intentionally differ from the physical one (a suspend
        // function parameter is Object in CLR), so do not fake an equality comparison between vocabularies.  Refuse
        // every same-name/same-shape overload set instead; a unique declaration is the only authoritative binding.
        if (shapeMatches.Length != 1
            || !IsObjectType(shapeMatches[0].ReturnType)
            || shapeMatches[0].KotlinReturnType is not TypeNode.Tv)
            return false;
        kotlinReturn = shapeMatches[0].KotlinReturnType;
        return true;
    }

    static bool IsObjectType(TypeNode type) =>
        type is TypeNode.Fqn f && f.Name is "System.Object" or "object" or "kotlin.Any";

    // #86 D1 — the PRE-erasure declaration of a referenced member's slots: its return and its parameter vector, as the
    // consuming module needs them to type a USE as `Subst(Erase(declaredKotlinType(slot)), typeArgs)`.
    //
    // Two sources, in order, per slot:
    //   * the `[KotlinNullableGeneric]` carrier — the exact pre-erasure Kotlin TypeNode NullableGenericErasure recorded
    //     on that slot, present iff this slot is one the erasure rewrote;
    //   * otherwise the PHYSICAL declaration (`ParamTypeNodes`/`ReturnTypeNode`), which IS `Erase(declared)` by
    //     construction — the producer emitted it through the same erasure — with generic parameters retained as `Tv`,
    //     so substituting the call's owner/method arguments into it yields the same answer. This second source is
    //     admitted ONLY where the physical node still carries a `Tv`, i.e. where the call site's type arguments are
    //     what completes it (`Iterator<E>.next(): !0`, `List<E>.get(i): !0`, `Slot<T>.get_value(): !0`). A Tv-FREE
    //     physical slot is refused, because the one thing it could contribute is a bare `System.Object` — and an
    //     `object` with no carrier beside it is indistinguishable from a declared `Any`. Deriving a use as `object`
    //     from every `Any`-returning referenced member is not the erasure family, it is all of it.
    // Either way the caller applies `Erase` then `Subst`; `Erase` is the identity on an already-erased node.
    //
    // A call names the owner it is DISPATCHED on, which is not always the owner that DECLARES the member
    // (`List<E>.iterator()` is declared on `Iterable<E>`), so the search walks the reference type's base and
    // interfaces. Each hop rewrites the supertype's own type parameters into the derived type's space using the
    // declared supertype arguments, so the result stays a declaration the CALLER substitutes with the call's owner
    // arguments — the contract does not change with the distance travelled.
    //
    // REFUSAL DISCIPLINE, identical to TryUncheckedGenericCastReturn: name + static-ness + parameter count + method
    // generic arity is all a call site gives us, so a same-shape overload SET is refused outright rather than resolved
    // to whichever sibling was enumerated first — deriving the wrong member's slot types manufactures exactly the
    // mismatch the consuming pass exists to remove.
    // `paramsRefused[i]` marks a parameter whose carrier the reader DELIBERATELY would not state. It is not the same
    // fact as `declaredParams[i] == null`, which also covers "the producer stated nothing here", and the consumer
    // must treat them differently: an absent declaration falls back to the call's own descriptor, while a REFUSED one
    // must not — the descriptor is that same erasure written in the call's substituted vocabulary, so falling back to
    // it applies exactly the derivation the refusal exists to prevent.
    //
    // The RETURN needs no such flag: a return the reader will not state simply leaves the call site's stamped result
    // standing, which is the pre-reader behaviour and has no fallback to bypass.
    public bool TryNullableGenericSlot(string ownerFqn, string name, bool isStatic, int argCount, int methodArity,
        out TypeNode declaredRet, out TypeNode[] declaredParams, out bool[] paramsRefused)
    {
        declaredRet = null;
        declaredParams = null;
        paramsRefused = null;
        if (ownerFqn == null || name == null) return false;
        var path = new HashSet<string>(StringComparer.Ordinal) { BareOwnerFqn(ownerFqn) };
        if (FindDeclaredSlot(ownerFqn, name, isStatic, argCount, methodArity, path, out var ret, out var ps)
            != SlotLookup.Declared)
            return false;
        declaredRet = ret.Node;
        declaredParams = ps.Select(p => p.Node).ToArray();
        paramsRefused = ps.Select(p => p.Refused).ToArray();
        // Declared, but with nothing this reader may state about it — the caller has no use for that. A REFUSAL is
        // something to state, though: it is what stops the caller reaching for the descriptor instead.
        return declaredRet != null || ps.Any(p => p.Node != null || p.Refused);
    }

    // The DIRECT supertypes of a referenced type, as constructed specs in that type's OWN type-parameter frame, plus
    // whether each is an interface. The override-slot bridge walks these so a class implementing `Derived<Int>` — where
    // the slot is declared on `Derived`'s own base `Sink` — reaches `Sink<Int>` as a spec of its own: a MethodImpl must
    // name the interface that DECLARES the slot, and the emitter looks the directive up under exactly that spec.
    // Empty for a type this index does not know, which is a supertype no bridge decision may be made about.
    public IEnumerable<(TypeNode.Fqn spec, bool isInterface)> ReferencedSupertypes(string ownerFqn)
    {
        if (ownerFqn == null || !_referenceTypeShapes.TryGetValue(DottedFqn(BareOwnerFqn(ownerFqn)), out var shape))
            yield break;
        foreach (var i in shape.Interfaces ?? Array.Empty<TypeNode.Fqn>()) yield return (i, true);
        if (shape.Base != null) yield return (shape.Base, false);
    }

    SlotLookup FindDeclaredSlot(string ownerFqn, string name, bool isStatic, int argCount, int methodArity,
        HashSet<string> path, out SlotFact declaredRet, out SlotFact[] declaredParams)
    {
        declaredRet = default;
        declaredParams = null;
        var bare = BareOwnerFqn(ownerFqn);
        if (TryMembersByBirOwner(bare, out var list))
        {
            var shapeMatches = list.Where(m =>
                    m.Name == name
                    && m.IsStatic == isStatic
                    && m.ParamCount == argCount
                    && m.MethodArity == methodArity
                    && m.ParamTypeNodes != null
                    && m.ParamTypeNodes.Length == argCount)
                .ToArray();
            // Declared HERE, ambiguously: refuse outright rather than walking upward, where an unrelated base member
            // of the same shape would look like an answer to a call this type's own overload set already owns.
            if (shapeMatches.Length > 1) return SlotLookup.Refused;
            if (shapeMatches.Length == 1)
            {
                var member = shapeMatches[0];
                declaredRet = DeclaredSlot(member.NullableGenericRet, member.ReturnTypeNode);
                declaredParams = new SlotFact[argCount];
                for (var i = 0; i < argCount; i++)
                    declaredParams[i] = DeclaredSlot(member.NullableGenericParams?[i], member.ParamTypeNodes[i]);
                // DECLARED HERE TERMINATES THE SEARCH, facts or no facts. A concrete member that shadows or
                // implements an inherited namesake IS the declaration the call binds to; continuing upward because
                // this one happens to carry no erasure fact would hand the call the BASE's carrier and rewrite a
                // descriptor the derived member never had.
                return SlotLookup.Declared;
            }
        }
        if (!_referenceTypeShapes.TryGetValue(DottedFqn(bare), out var shape)) return SlotLookup.NotDeclared;
        // Reflection reports the interface set TRANSITIVELY, so one hop reaches every interface declaration; the base
        // chain is walked one link at a time. Every supertype that answers is collected and they must AGREE — an
        // inherited member the call cannot distinguish is not a declaration this pass may act on.
        SlotFact foundRet = default;
        SlotFact[] foundParams = null;
        var answers = 0;
        foreach (var super in Supertypes(shape))
        {
            // The guard is PATH-LOCAL and keyed on the TYPE DEFINITION. Repeating a definition on ONE path is cyclic
            // metadata and the only thing worth stopping; repeating it on a SIBLING path is not, so the key is dropped
            // on the way out and `I<int>` / `I<string>` are both visited and then compared rather than the answer
            // being whichever the reflection interface order reached first. Because a definition can appear at most
            // once per path, the walk terminates on its own — a base chain is as deep as the program declares it, and
            // an arbitrary hop limit would silently drop a carrier declared past the limit AND let a shallower
            // same-shape interface answer win instead of triggering the disagreement refusal.
            var key = BareOwnerFqn(super.Name);
            if (!path.Add(key)) continue;
            var found = FindDeclaredSlot(super.Name, name, isStatic, argCount, methodArity, path,
                out var sret, out var sps);
            path.Remove(key);
            if (found == SlotLookup.Refused) return SlotLookup.Refused;
            if (found != SlotLookup.Declared) continue;
            var mret = MapThroughSupertype(sret, super.Args);
            var mps = sps.Select(p => MapThroughSupertype(p, super.Args)).ToArray();
            if (answers++ == 0) { foundRet = mret; foundParams = mps; continue; }
            if (!SameSlots(foundRet, foundParams, mret, mps)) return SlotLookup.Refused;
        }
        if (answers == 0) return SlotLookup.NotDeclared;
        declaredRet = foundRet;
        declaredParams = foundParams;
        return SlotLookup.Declared;
    }

    static IEnumerable<TypeNode.Fqn> Supertypes(ReferenceTypeShape shape)
    {
        if (shape.Base != null) yield return shape.Base;
        foreach (var i in shape.Interfaces ?? Array.Empty<TypeNode.Fqn>()) yield return i;
    }

    // A refusal survives the hop unchanged: which supertype declared the slot has no bearing on whether the reader
    // may state it.
    static SlotFact MapThroughSupertype(SlotFact f, TypeNode[] superArgs)
        => new(MapThroughSupertype(f.Node, superArgs), f.Refused);

    // Rewrite a declaration expressed in a SUPERTYPE's type-parameter space into the derived type's, using the
    // supertype arguments the derived type declared (`List<E> : Iterable<E>` maps `Iterable`'s `!0` to `List`'s `!0`;
    // `IntSlots : Slots<Int>` maps it to `Int`). Method-scope parameters belong to the member and are left alone.
    static TypeNode MapThroughSupertype(TypeNode t, TypeNode[] superArgs) => t switch
    {
        null => null,
        TypeNode.Tv { Scope: "type" } tv => superArgs != null && tv.I >= 0 && tv.I < superArgs.Length ? superArgs[tv.I] : t,
        TypeNode.Fqn { Args: { } args } f => new TypeNode.Fqn(f.Name, args.Select(a => MapThroughSupertype(a, superArgs)).ToArray()),
        TypeNode.Array a => new TypeNode.Array(MapThroughSupertype(a.Elem, superArgs)),
        TypeNode.Nullable n => new TypeNode.Nullable(MapThroughSupertype(n.Of, superArgs)),
        TypeNode.Oblivious o => new TypeNode.Oblivious(MapThroughSupertype(o.Of, superArgs)),
        TypeNode.ByRef b => new TypeNode.ByRef(MapThroughSupertype(b.Of, superArgs)),
        TypeNode.Fn fn => new TypeNode.Fn(fn.Suspend, MapThroughSupertype(fn.Ret, superArgs),
            fn.Params.Select(p => MapThroughSupertype(p, superArgs)).ToArray(),
            MapThroughSupertype(fn.Recv, superArgs), fn.Clr,
            fn.Ctx?.Select(c => MapThroughSupertype(c, superArgs)).ToArray()),
        _ => t,
    };

    // Two supertypes agree only if they agree on the REFUSAL too: one that may be stated and one that may not are
    // different answers, and taking either would be a guess.
    static bool SameSlots(SlotFact aRet, SlotFact[] aps, SlotFact bRet, SlotFact[] bps)
    {
        if (!SameSlot(aRet, bRet)) return false;
        if (aps.Length != bps.Length) return false;
        for (var i = 0; i < aps.Length; i++)
            if (!SameSlot(aps[i], bps[i])) return false;
        return true;
    }

    static bool SameSlot(SlotFact a, SlotFact b)
    {
        if (a.Refused != b.Refused) return false;
        if ((a.Node == null) != (b.Node == null)) return false;
        return a.Node == null || a.Node.Equals(b.Node);
    }

    // The same for a CONSTRUCTOR, keyed by owner + declared parameter count (a ctor has no name). A same-arity overload
    // set is refused for the same reason a same-shape method set is.
    public bool TryNullableGenericCtorSlot(string ownerFqn, int argCount, out TypeNode[] declaredParams,
        out bool[] paramsRefused)
    {
        declaredParams = null;
        paramsRefused = null;
        if (ownerFqn == null) return false;
        var bare = BareOwnerFqn(ownerFqn);
        if (!_ctorsByOwner.TryGetValue(bare, out var byArity))
        {
            var matches = _ctorsByOwner.Where(kv => DottedFqn(kv.Key) == bare).Take(2).ToList();
            if (matches.Count != 1) return false;
            byArity = matches[0].Value;
        }
        if (!byArity.TryGetValue(argCount, out var ctors) || ctors.Count != 1) return false;
        var ctor = ctors[0];
        if (ctor.ParamTypeNodes == null || ctor.ParamTypeNodes.Length != argCount) return false;
        var facts = new SlotFact[argCount];
        for (var i = 0; i < argCount; i++)
            facts[i] = DeclaredSlot(ctor.NullableGenericParams?[i], ctor.ParamTypeNodes[i]);
        declaredParams = facts.Select(f => f.Node).ToArray();
        paramsRefused = facts.Select(f => f.Refused).ToArray();
        return facts.Any(f => f.Node != null || f.Refused);
    }

    // ONE slot's declaration. Still a SlotFact rather than a bare TypeNode, because a REFUSAL is a fact of its own and
    // has to travel as one: the physical declaration is not a substitute for a refused carrier — that is the same
    // erasure spelled WITHOUT the evidence that it was one — and neither is the call's own descriptor, which is that
    // erasure again in the call's substituted vocabulary. A refusal that degrades to "no information" is not a refusal.
    //   * a carrier — the exact pre-erasure Kotlin type, and the best answer there is;
    //   * no carrier at all — the physical declaration, while it is still open.
    //
    // The one carrier this reader used to refuse was an `Array<X?>`, because the erasure was not uniform at an array
    // element: the producing assembly's slot said `object[]` while the value it handed back was a `Nullable<V>[]`, and
    // those are unrelated CLR types (ECMA-335 I.8.7.1). #86 D2 canonicalizes `Array<X?>` to `object[]` at every
    // position, so the carrier's `Erase` and the producer's physical slot now AGREE and the slot is served like any
    // other. Nothing produces a per-parameter refusal today; the channel is what would carry the next one.
    static SlotFact DeclaredSlot(TypeNode carrier, TypeNode physical)
        => new(carrier ?? OpenPhysical(physical), false);

    // The physical declaration of a slot, admitted only while it still says something the call site's type arguments
    // complete — see the refusal reasoning on TryNullableGenericSlot.
    static TypeNode OpenPhysical(TypeNode t) => t != null && ContainsTv(t) ? Canonical(t) : null;

    static bool ContainsTv(TypeNode t) => t switch
    {
        TypeNode.Tv => true,
        TypeNode.Fqn { Args: { } args } => args.Any(ContainsTv),
        TypeNode.Array a => ContainsTv(a.Elem),
        TypeNode.Nullable n => ContainsTv(n.Of),
        TypeNode.Oblivious o => ContainsTv(o.Of),
        TypeNode.ByRef b => ContainsTv(b.Of),
        TypeNode.Fn fn => ContainsTv(fn.Ret) || fn.Params.Any(ContainsTv) || (fn.Recv != null && ContainsTv(fn.Recv)),
        _ => false,
    };

    // The physical declaration is spelled in whichever vocabulary its producer emitted — a DotKt LIBRARY dll names
    // `System.Object`, the reference stdlib names the Kotlin `object` the erasure writes. The consuming pass works in
    // BIR (Kotlin) vocabulary, where the erased slot is the bare `object`, so that one name is normalized here.
    // Nothing else is translated: a physical name that stays CLR-spelled simply fails the consumer's
    // object-erasure gate and produces no rewrite, which is the fail-closed outcome.
    static TypeNode Canonical(TypeNode t) => t switch
    {
        null => null,
        TypeNode.Fqn { Name: "System.Object", Args: null } => new TypeNode.Fqn("object"),
        TypeNode.Fqn { Args: { } args } f => new TypeNode.Fqn(f.Name, args.Select(Canonical).ToArray()),
        TypeNode.Array a => new TypeNode.Array(Canonical(a.Elem)),
        TypeNode.Nullable n => new TypeNode.Nullable(Canonical(n.Of)),
        TypeNode.Oblivious o => new TypeNode.Oblivious(Canonical(o.Of)),
        TypeNode.ByRef b => new TypeNode.ByRef(Canonical(b.Of)),
        _ => t,
    };

    // The declared RETURN type of a top-level fun (a `callStatic owner=null`), resolved via its file-class owner then the
    // member's return type. `recvKey` = the call's first sig-param bare owner (disambiguates overloads across file-classes);
    // `argCount` = the sig's total param count (receiver + args), matching the ref.dll static's ParamCount. null if unresolved.
    public TypeNode TryTopLevelReturn(string funName, string recvKey, int argCount, string firstParamKey = null) =>
        TryResolveTopLevelStatic(funName, recvKey, firstParamKey, out var owner) ? TryMemberReturn(owner, funName, argCount, firstParamKey) : null;

    // A bare-@ClrIntrinsic extension fun resolved by name + the receiver-type key (the call's first-arg type) + the
    // FULL parameter count (receiver + args), so `set` on a MutableMap receiver -> set_Item (not StringBuilder's
    // set_Chars) AND a same-name/same-receiver overload of a DIFFERENT arity does not collide: `substring(String,Int)`
    // @ClrIntrinsic("Substring") must NOT capture the 3-param `substring(String,Int,Int)` real-body call (which would
    // wrongly emit Substring(start,end) with end read as a LENGTH). The paramCount disambiguates them; the real-bodied
    // overload misses here and falls through to its stdlib file-class attribution.
    // EXACT-signature @ClrIntrinsic ext-member lookup: `sigKey` is the call's full ParamKey signature (receiver-first),
    // so a same-name/same-arity NON-intrinsic overload (`substring(IntRange)` vs the bound `substring(Int)`) misses here
    // and falls through to its real Kotlin body — never captured by a lossy name+count key (the #46 same-name collapse).
    public bool TryExtMemberIntrinsic(string funName, string sigKey, out string member) =>
        _extMemberIntrinsics.TryGetValue(funName + "|" + sigKey, out member);

    // An @JvmInline value class's backing-field getter call (`x.get_data()`): the inline UNBOX. Returns the CLR conv
    // token for the field's declared type so the call collapses to `conv(recv)` (the erased primitive IS the value).
    public bool TryInlineFieldGetter(string ownerFqn, string member, out string conv)
    {
        conv = null;
        return _inlineBacking.TryGetValue(ownerFqn, out var info) && member == info.Getter && (conv = info.Conv) != null;
    }

    // Whether the owner is an @JvmInline value class erased to a primitive CLR form (so `new T(arg)` is the inline BOX).
    public bool IsInlineValueClass(string ownerFqn) => _inlineBacking.ContainsKey(ownerFqn);

    // A rule-3 hoist candidate: owner.member exists, is concrete (non-abstract) and carries NEITHER @ClrIntrinsic NOR
    // @ClrProperty, so its real Kotlin body is hoisted by bir2cir's AliasHelperHoist to the static helper `dotkt$ClrH_<owner>`. A @ClrProperty
    // accessor (setLength/capacity/nativeSetCapacity/ticks) is a BOUND stub — its call substitutes to clrPropGet/clrPropSet
    // (Rule 2p) — so it must NOT hoist its throwing TODO body into the helper (the same exclusion @ClrIntrinsic gets).
    public bool IsRule3Member(string ownerFqn, string memberName) =>
        TryMembersByBirOwner(ownerFqn, out var list) &&
        list.Any(m => m.Name == memberName && m.Intrinsic == null && m.PropertyName == null && !m.Conv && !m.IsAbstract);

    // Whether the ref.dll owner DECLARES its own concrete (non-abstract, nullary, instance) `iterator()` — a real slot a
    // `this.iterator()`/`x.iterator()` binds to directly, so MemberCallSubstitution must NOT reroute it to the base-Iterator
    // ClrIteratorBridge (which would drop the `MutableIterator` remove()/set() members). The post-#169 concrete
    // LinkedHashSet is the case an APP sees non-locally; the AbstractMutable{Collection,Set} bases keep iterator() ABSTRACT
    // (IsAbstract) so they still reroute. Mirrors the local-decl scan MemberCallSubstitution does for same-file owners.
    public bool DeclaresConcreteIterator(string ownerToken) =>
        ownerToken != null && TryMembersByBirOwner(BareOwnerFqn(ownerToken), out var list)
        && list.Any(m => m.Name == "iterator" && m.ParamCount == 0 && !m.IsAbstract && !m.IsStatic);

    // Does this exact referenced owner declare a concrete instance member of the given Kotlin/CLR name and arity?
    // Used to consume an explicit BIR fakeOverride fact when its inherited declaration is a DIM.  Exact owner and
    // arity keep overloads separate; hierarchy traversal belongs to the override closure already carried by BIR.
    public bool DeclaresConcreteMember(string ownerToken, string memberName, int paramCount) =>
        ownerToken != null && memberName != null
        && TryMembersByBirOwner(BareOwnerFqn(ownerToken), out var list)
        && list.Any(m => m.Name == memberName && m.ParamCount == paramCount && !m.IsAbstract && !m.IsStatic);

    // Exact referenced declaration lookup for inherited-member owner binding.  The signature is
    // structural (including type-vs-method Tv scope/index), not a name/arity guess, so overloads
    // remain distinct.  Multiple identical candidates are treated as ambiguous and refused.
    public bool DeclaresExactInstanceMember(string ownerToken, string memberName, int methodArity,
        IReadOnlyList<TypeNode> signature)
    {
        if (ownerToken == null || memberName == null || IsAliasedOwner(ownerToken)
            || !TryMembersByBirOwner(BareOwnerFqn(ownerToken), out var list)) return false;
        return list.Count(m => !m.IsStatic && m.Name == memberName && m.MethodArity == methodArity
            && m.ParamTypeNodes is { } ps && ps.Length == signature.Count
            && ps.Select((p, i) => p == signature[i]).All(x => x)) == 1;
    }

    // Whether the exact referenced declaration is virtual. When BIR omitted a declaration signature (common for
    // nullary property accessors), accept only a unique name/method-arity/parameter-count match. This is a CLR
    // dispatch fact consumed by bir2cir; ilemit must not rediscover it from reflection while emitting.
    public bool DeclaresVirtualInstanceMember(string ownerToken, string memberName, int methodArity,
        IReadOnlyList<TypeNode> signature, int paramCount)
    {
        if (ownerToken == null || memberName == null || IsAliasedOwner(ownerToken)
            || !TryMembersByBirOwner(BareOwnerFqn(ownerToken), out var list)) return false;
        var candidates = list.Where(m => !m.IsStatic && m.Name == memberName
            && m.MethodArity == methodArity && m.ParamCount == paramCount);
        if (signature != null)
            candidates = candidates.Where(m => m.ParamTypeNodes is { } ps && ps.Length == signature.Count
                && ps.Select((p, i) => p == signature[i]).All(x => x));
        var matches = candidates.ToList();
        return matches.Count == 1 && matches[0].IsVirtual;
    }

    // Return the declaration-shape of a referenced owner, normalized to BIR's dotted nested-type
    // spelling.  The returned base/interfaces may contain type-scoped Tvs and are substituted by
    // InheritedMemberOwnerBinding exactly like locally declared supertypes.
    public bool TryReferenceTypeShape(string ownerToken, out int typeParamCount, out string kind,
        out TypeNode.Fqn baseType, out TypeNode.Fqn[] interfaces)
    {
        if (ownerToken != null && !IsAliasedOwner(ownerToken)
            && _referenceTypeShapes.TryGetValue(DottedFqn(BareOwnerFqn(ownerToken)), out var shape))
        {
            typeParamCount = shape.TypeParamCount;
            kind = shape.Kind;
            baseType = shape.Base;
            interfaces = shape.Interfaces;
            return true;
        }
        typeParamCount = 0;
        kind = null;
        baseType = null;
        interfaces = Array.Empty<TypeNode.Fqn>();
        return false;
    }

    // @ClrTypeAlias owners are not CLR declaration owners: their calls must go through
    // MemberCallSubstitution (which also selects/renames the target member).  Late inherited
    // binding across this boundary would leave a Kotlin name on a BCL owner.
    bool IsAliasedOwner(string ownerToken)
    {
        var bare = BareOwnerFqn(ownerToken);
        if (_ownerAlias.ContainsKey(bare) || FoundationalRefAliases.ContainsKey(bare)) return true;
        var matches = _ownerAlias.Keys.Where(k => DottedFqn(k) == DottedFqn(bare)).Take(2).ToList();
        return matches.Count == 1;
    }

    // Reflection spells a nested owner with '+' while BIR deliberately remains in Kotlin's dotted vocabulary.
    // Resolve that representation seam here, in the reference index.  Refuse a theoretically ambiguous collision
    // (`A.B+C` and `A.B.C` both exist) instead of guessing which CLR owner the Kotlin token meant.
    bool TryMembersByBirOwner(string ownerFqn, out List<MemberBinding> members)
    {
        if (_membersByOwner.TryGetValue(ownerFqn, out members)) return true;
        var matches = _membersByOwner.Where(kv => DottedFqn(kv.Key) == ownerFqn).Take(2).ToList();
        if (matches.Count == 1)
        {
            members = matches[0].Value;
            return true;
        }
        members = null;
        return false;
    }

    public static string HelperTypeName(string ownerFqn) =>
        "dotkt$ClrH_" + System.Text.RegularExpressions.Regex.Replace(ownerFqn, "[^A-Za-z0-9]", "_");


    public static ReferenceMetadataIndex Build(IReadOnlyList<string> refs)
    {
        // bir2cir is a ref-READER: a consumed cross-module DotKt library references the RUNTIME stdlib
        // (DotKt.Stdlib) in its `[kotlin.clr.*]` round-trip metadata, but bir2cir carries only the REFERENCE
        // twin — alias so that reference resolves to DotKt.Private.Stdlib (same type shapes).
        var catalog = ManagedReferenceCatalog.Create(refs, "bir2cir", refStdlibAliasesRuntime: true);
        var assemblies = new List<ReferenceAssembly>();
        if (catalog.Entries.Count == 0) return new ReferenceMetadataIndex(assemblies, catalog);
        using var mlc = catalog.CreateMetadataLoadContext();
        foreach (var entry in catalog.Entries)
        {
            var reference = entry.Path;
            var identity = entry.Identity;
            assemblies.Add(new ReferenceAssembly(
                reference,
                identity.Name ?? Path.GetFileNameWithoutExtension(reference),
                identity.Version?.ToString() ?? "",
                ReadDotKtMetadata(reference, mlc)));
        }

        return new ReferenceMetadataIndex(assemblies, catalog);
    }

    static ReferenceDotKtMetadata ReadDotKtMetadata(string reference, MetadataLoadContext mlc)
    {
        var metadata = new ReferenceDotKtMetadata();
        // The substitution index via MetadataLoadContext (a metadata-only reflection read) is the SOLE scan. A former
        // runtime `Assembly.LoadFrom` scan (populating Members/Types/Functions/FileClasses) was REMOVED: it always
        // threw TypeLoadException on the metadata-only ref stdlib (throw-stub bodies + kotlin.* signatures) — logging a
        // spurious "metadata scan failed: TypeLoadException Type: 'kotlin.String'" on every build — and aborted early,
        // and its output fed ONLY dead resolution paths (the unreferenced Resolve(CallSite)/Resolve(TypeSite)/
        // ResolveClrProperty). The live @ClrTypeAlias/@ClrIntrinsic/rule-3 substitution reads exclusively from here.
        ScanSubstitutionMetadata(reference, metadata, mlc);
        return metadata;
    }

    // Populate the substitution index (Aliases / TypeKinds / HelperTypes / MemberBindings) from the ref.dll using a
    // MetadataLoadContext so the metadata-only assembly reads cleanly. Per-type try/catch: one malformed type is
    // skipped, never aborting the whole scan (the failure mode that left Assembly.LoadFrom's index empty).
    static void ScanSubstitutionMetadata(string reference, ReferenceDotKtMetadata metadata, MetadataLoadContext mlc)
    {
        try
        {
            var asm = mlc.LoadFromAssemblyPath(Path.GetFullPath(reference));

            Type[] types;
            try { types = asm.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).Cast<Type>().ToArray(); }

            // DotKt ownership is provenance, not a namespace/type-name guess. ilemit stamps the versioned assembly
            // marker and bir2cir emits a [CompilerGenerated] metadata carrier; require BOTH, matching dll2klib's
            // classifier. A third-party C# assembly may legally define a full-name lookalike and must remain on the
            // ordinary CLR metadata path (including custom awaitables and BCL collection signatures).
            var dotKtAuthored = IsDotKtEmittedAssembly(asm);
            if (dotKtAuthored)
                foreach (var authoredType in types)
                    metadata.DotKtOwners.Add(
                        StripGenericArity(DottedFqn(authoredType.FullName ?? authoredType.Name)));

            var singletonCompanionCarriers = new Dictionary<string, string>(StringComparer.Ordinal);
            var companionRepresentations = dotKtAuthored
                ? ValidateCompanionCarriers(types, asm, out singletonCompanionCarriers,
                    metadata.CompanionCarrierByPhysicalOwner,
                    metadata.CompanionSourceNameByPhysicalOwner,
                    metadata.CompanionPhysicalOwnerBySemanticType,
                    metadata.CompanionSemanticOwnerByCarrier)
                : new Dictionary<Type, bool>();
            foreach (var companion in singletonCompanionCarriers)
                metadata.SingletonCompanionCarrierBySemanticOwner.Add(companion.Key, companion.Value);

            foreach (var type in types)
            {
                try
                {
                    // Index by the REAL Kotlin FQN (kotc emits "kotlin.String" etc. as the type name) so a BIR
                    // member-call owner token matches. A CLR-bound owner carries @ClrTypeAlias (the type-identity
                    // binding) or, for any not-yet-renamed bound class, a class-level @ClrIntrinsic.
                    var ownerFqn = StripGenericArity(type.FullName ?? type.Name);
                    if (dotKtAuthored && type.IsInterface && !type.IsGenericType
                        && HasAttribute(type.GetCustomAttributesData(), CompilerGeneratedAttr)
                        && KotlinTypeOf(type.GetCustomAttributesData(), asm) is TypeNode.Fqn
                        {
                            Args: { Length: > 0 } existentialArgs
                        } existentialSurface
                        && existentialArgs.All(a => a is TypeNode.Star))
                    {
                        var semantic = StripGenericArity(DottedFqn(existentialSurface.Name));
                        var declarations = types.Where(candidate =>
                        {
                            if (HasAttribute(candidate.GetCustomAttributesData(), CompilerGeneratedAttr)) return false;
                            var candidateName = StripGenericArity(DottedFqn(candidate.FullName ?? candidate.Name));
                            return candidateName == semantic
                                && candidate.GetGenericArguments().Length == existentialArgs.Length;
                        }).ToArray();
                        if (declarations.Length != 1)
                            throw new InvalidDataException(
                                $"trusted Kotlin existential '{type.FullName}' resolves semantic owner "
                                + $"'{semantic}'/{existentialArgs.Length} to {declarations.Length} declarations");
                        var physical = ExactPhysicalMetadataName(type);
                        if (!metadata.ExistentialPhysicalBySemanticOwner.TryAdd(semantic, physical)
                            || metadata.ExistentialPhysicalBySemanticOwner[semantic] != physical)
                            throw new InvalidDataException($"duplicate Kotlin existential ABI for '{semantic}'");
                    }
                    // dll2klib never exposes compiler-generated implementation classifiers to Kotlin. Do not let a
                    // same-named inline-materialized closure in the current compilation be rebound to a stale helper
                    // TypeDef from a reference assembly merely because both deterministic names coincide.
                    if (dotKtAuthored && !HasAttribute(type.GetCustomAttributesData(), CompilerGeneratedAttr))
                    {
                        var physicalName = ExactPhysicalMetadataName(type);
                        // KLIB classifiers use dotted nesting, while a reconstructed external classifier can
                        // legitimately re-enter BIR with the CLR `+` separator. Both spellings name the same
                        // trusted DotKt declaration; keep the exact arity-bearing metadata identity as the value.
                        metadata.PhysicalTypeBySemanticName[ownerFqn] = physicalName;
                        metadata.PhysicalTypeBySemanticName[DottedFqn(ownerFqn)] = physicalName;
                    }
                    if (dotKtAuthored && AttrInt32(type.GetCustomAttributesData(), KotlinInnerAttr) is int capturedCount)
                    {
                        var innerName = StripGenericArity(DottedFqn(ownerFqn));
                        metadata.InnerCapturedCount[innerName] = capturedCount;
                        if (type.DeclaringType is Type declaringType)
                            metadata.InnerSemanticOwner[innerName] = StripGenericArity(DottedFqn(
                                declaringType.FullName ?? declaringType.Name));
                    }
                    if (companionRepresentations.TryGetValue(type, out var companionIsStatic))
                        metadata.CompanionStaticByPhysicalOwner.Add(
                            StripGenericArity(DottedFqn(ownerFqn)), companionIsStatic);
                    metadata.TypeKinds[ownerFqn] = TypeKind(type);
                    // Both spellings: the reflection name nests with `+`, every bir2cir type token is DOTTED, and a
                    // NESTED `ref struct` (`Span<T>.Enumerator`, `MemoryExtensions.SpanSplitEnumerator`) is exactly
                    // the shape a spill of `for (x in span)` would mint a field of.
                    if (IsByRefLikeType(type))
                    {
                        metadata.ByRefLikeOwners.Add(ownerFqn);
                        metadata.ByRefLikeOwners.Add(DottedFqn(ownerFqn));
                    }
                    metadata.TypeShapes[DottedFqn(ownerFqn)] = new ReferenceTypeShape(
                        type.IsGenericType ? type.GetGenericArguments().Length : 0,
                        TypeKind(type),
                        DeclarationTypeNode(type.BaseType) as TypeNode.Fqn,
                        type.GetInterfaces().Select(DeclarationTypeNode).OfType<TypeNode.Fqn>().ToArray());
                    if (type.IsGenericType)
                    {
                        var gargs = type.GetGenericArguments();
                        metadata.TypeArity[ownerFqn] = gargs.Length;
                        metadata.TypeParamNames[ownerFqn] = gargs.Select(g => g.Name).ToArray();
                        // The struct-ness ORACLE for a TYPE VARIABLE (#37/#48): record each type-param's CLR constraint
                        // class from GenericParameterAttributes so a `T?` on a struct-constrained param stays Nullable<T>.
                        metadata.TypeParamConstraints[ownerFqn] = gargs.Select(GenericParamConstraintClass).ToArray();
                        // The declared type-BOUND of each type param (`E : Element` -> Element), keyed by the DOTTED FQN
                        // (nested `+` -> `.`) so bir2cir's dotted lookup (StarProjectionBoundLowering) matches. A star
                        // projection `Key<*>` erased to `Key<object>` violates this bound and is repointed to `Key<Element>`.
                        metadata.TypeParamBounds[DottedFqn(ownerFqn)] = gargs.Select(GenericParamBound).ToArray();
                    }
                    var classAlias = ClrAliasOf(type.GetCustomAttributesData());
                    if (classAlias != null)
                    {
                        metadata.Aliases[ownerFqn] = classAlias;
                        metadata.Aliases[DottedFqn(ownerFqn)] = classAlias;
                    }
                    // A compiler-generated implementation classifier carried by an inline body may capture the
                    // enclosing receiver/free values through its sole constructor. Record that exact declaration
                    // shape so StringCharSequenceBridge can adapt a static String value to a CharSequence capture
                    // slot even after #225 nests the classifier. CompilerGenerated + exactly one constructor is the
                    // structural boundary; choosing the first of multiple overloads would be an ownership guess.
                    if (dotKtAuthored && HasAttribute(type.GetCustomAttributesData(), CompilerGeneratedAttr))
                    {
                        var ctors = type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                        if (ctors.Length == 1)
                        {
                            var ctorParams = ctors[0].GetParameters().Select(p => TypeName(p.ParameterType)).ToArray();
                            metadata.CtorParamTypes[ownerFqn] = ctorParams;
                            metadata.CtorParamTypes[DottedFqn(ownerFqn)] = ctorParams;
                            metadata.CtorParamTypes[ExactPhysicalMetadataName(type)] = ctorParams;
                        }
                    }
                    if (ownerFqn.StartsWith("dotkt$ClrH_", StringComparison.Ordinal)) metadata.HelperTypes.Add(ownerFqn);
                    if (HasAttribute(type.GetCustomAttributesData(), RestrictsSuspensionAttr)) metadata.RestrictsSuspensionTypes.Add(ownerFqn);
                    var isFileClass = HasAttribute(type.GetCustomAttributesData(), KotlinFileClassAttr);

                    // `value`/inline class (marked with [KotlinValue], the 2.4.0 carrier of `mods.value`): its single
                    // instance backing field IS the erased value. Record that property's GETTER + the field's CLR conv
                    // token so a `get_<prop>()` call collapses to `conv(<recv>)`. NARROWED to EXACTLY ONE instance field
                    // — a value class has precisely one property/backing field, so requiring a single field picks the
                    // correct underlying type (and refuses to erase off an arbitrary FirstOrDefault if the shape is
                    // unexpected). The GETTER is the accessor of the PROPERTY that OWNS that field: an accessor-routed
                    // property's storage carries the compiler-generated `<data>k__BackingField` name
                    // (BackingFieldRename), which no `"get_" + field.Name` spelling can reach. A field that is NOT an
                    // auto-property's storage (a plain-field/`@ClrField` shape, or a pre-rename assembly) IS the member
                    // itself, so its own name stays the accessor stem — the entry is never dropped, in any shape.
                    if (HasAttribute(type.GetCustomAttributesData(), KotlinValueAttr))
                    {
                        var instanceFields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                        var backing = instanceFields.Length == 1 ? instanceFields[0] : null;
                        if (backing != null && InlineFieldConv(backing.FieldType) is string conv)
                        {
                            var owningProp = type
                                .GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                                .FirstOrDefault(p => BackingFieldRename.Mangle(p.Name) == backing.Name);
                            metadata.InlineBacking[ownerFqn] = (owningProp?.GetMethod?.Name ?? "get_" + backing.Name, conv);
                        }
                    }

                    foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                    {
                        var intrinsic = ClrIntrinsicOf(method.GetCustomAttributesData());
                        var prop = ClrPropertyOf(method.GetCustomAttributesData());
                        var byrefPositions = ByrefPositionsOf(method);
                        // @ClrConv (numeric primitive conversion): the call lowers to a CIL `conv` to the callee's OWN
                        // declared return type (toLong -> the emitted `kotlin.Long` type, ...). Read the marker + capture
                        // the return-type token here (the pre-lowering Kotlin FQN, from THIS reference/metadata dll), so
                        // MemberCallSubstitution can emit `{k:conv, to:<convTo>, e:<recv>}` — the target BirTypeLowering
                        // then lowers to System.Int64/etc. and ilemit picks the conv opcode.
                        var isConv = HasAttribute(method.GetCustomAttributesData(), "kotlin.clr.ClrConv");
                        var convTo = isConv ? TypeName(method.ReturnType) : null;
                        // Default argument VALUES remain authoritative in the selected reference DLL. KotlinDefault
                        // contributes its raw Kotlin-expression BIR; an ordinary ECMA-335 constant contributes a plain
                        // const expression. The reference KLIB carries only DECLARES_DEFAULT_VALUE for frontend
                        // resolution, never either payload.
                        if (CallableDefaultsOf(method) is Dictionary<int, string> defaults)
                            AddKotlinDefaults(metadata, ownerFqn, method.Name, method.GetParameters(), defaults);
                        // The `suspend` bit from the DotKt round-trip [KotlinFunction(flags)] attribute (Suspend = 4,
                        // the flag word ilemit stamps; the dead Assembly.LoadFrom scan read it, this live scan didn't).
                        // Channelled into MemberBinding.Suspend for the coroutine bundle (bundle 6) — no consumer yet.
                        var suspend = (KotlinFunctionFlags(method.GetCustomAttributesData()) & KotlinFunctionSuspendFlag) != 0;
                        if (suspend && Environment.GetEnvironmentVariable("DOTKT_BIR2CIR_DEBUG_SUSPEND") == "1")
                            Console.Error.WriteLine($"bir2cir: ref-scan suspend member {ownerFqn}.{method.Name}/{method.GetParameters().Length} (Suspend=true)");
                        metadata.MemberBindings.Add(new MemberBinding(
                            ownerFqn,
                            method.Name,
                            method.GetParameters().Length,
                            intrinsic,
                            method.IsAbstract,
                            method.IsStatic,
                            method.GetParameters().Select(p => TypeName(p.ParameterType)).ToArray(),
                            prop?.Access ?? 0,
                            prop?.Name,
                            byrefPositions,
                            suspend,
                            isConv,
                            convTo,
                            TypeNodeOf(method.ReturnType),
                            method.GetGenericArguments().Length,
                            method.GetParameters().Select(p => DeclarationTypeNode(p.ParameterType)).ToArray(),
                            method.IsVirtual,
                            dotKtAuthored ? KotlinTypeOf(method.ReturnParameter.GetCustomAttributesData(), method.DeclaringType?.Assembly) : null,
                            // #86 D1 — the positional pre-erasure carrier, per slot. Only a DotKt-authored assembly can
                            // carry it, and only the erasure records it, so a slot without one is simply absent here and
                            // the consumer falls back to the physical declaration (which IS `Erase(decl)` by construction).
                            dotKtAuthored ? CarrierTypeOf(method.ReturnParameter.GetCustomAttributesData(), method.DeclaringType?.Assembly, KotlinNullableGenericAttr) : null,
                            dotKtAuthored
                                ? method.GetParameters().Select(p => CarrierTypeOf(p.GetCustomAttributesData(), method.DeclaringType?.Assembly, KotlinNullableGenericAttr)).ToArray()
                                : null,
                            DeclarationTypeNode(method.ReturnType)));
                        // [KotlinInline] raw-BIR carrier (#71/#75 S1): decode the versioned carrier now (the codec is
                        // BirCarrier, shared) and key it owner|name|pc|ga so InlineSplice can splice this external inline
                        // fn's body at a cross-module call site. This carrier is compiler-internal ABI: an older or
                        // malformed payload is unsupported and never enters a plain-call compatibility path.
                        // ga = generic arity.
                        var inlineCad = method.GetCustomAttributesData().FirstOrDefault(c => c.AttributeType.FullName == KotlinInlineAttr);
                        if (inlineCad != null)
                        {
                            if (inlineCad.ConstructorArguments.Count != 2)
                                throw new InvalidDataException();
                            try
                            {
                                var ver = (string)inlineCad.ConstructorArguments[0].Value!;
                                var content = ReadByteArrayArg(inlineCad.ConstructorArguments[1]);
                                var decoded = DotKt.Bir.BirCarrier.DecodeBody(ver, content);
                                var json = decoded.ToJsonString();
                                // §4.2 (#75 S4b): key `owner|name|pc|ga` -> a LIST of candidate overload payloads. pc/ga come
                                // from the .NET method (the payload counts them identically: an extension receiver rides as a
                                // leading `__self` param). InlineSplice picks the UNIQUE candidate whose decoded `params[i].type`
                                // structurally matches the call's paramSig — the decoded params are kotc-emitted decl nodes, so
                                // they equal the callInline's paramSig exactly (both from `birType(param.type)`).
                                var ikey = ownerFqn + "|" + method.Name + "|" + method.GetParameters().Length + "|" + method.GetGenericArguments().Length;
                                if (!metadata.InlinePayloads.TryGetValue(ikey, out var ilst)) metadata.InlinePayloads[ikey] = ilst = new List<string>();
                                ilst.Add(json);
                            }
                            catch (Exception ex)
                            {
                                throw new InvalidDataException(null, ex);
                            }
                        }
                        // A top-level fun (file-class static) with @ClrIntrinsic. TWO shapes:
                        //   FQ "System.X.Y"  -> a fully-qualified BCL static (isNaN, clrTimestamp); keyed by NAME.
                        //   bare "Name"      -> a member on an EXTENSION receiver (`Array<T>.nativeClone()` ->
                        //                       @ClrIntrinsic("Clone")). Keyed by NAME|recvKey (the first param's type),
                        //                       because the name alone collides across receivers (MutableMap.set->set_Item
                        //                       vs StringBuilder.set->set_Chars). recvKey of the call site is its first arg.
                        if (isFileClass && method.IsStatic && intrinsic != null)
                        {
                            var ps = method.GetParameters();
                            if (intrinsic.Contains('.'))
                            {
                                // Name-only map (first-wins) is retained for single-static intrinsics (isNaN,
                                // clrTimestamp); when a name is seen binding to a DIFFERENT static, mark it ambiguous so
                                // the caller requires an exact-signature match instead (sqrt/abs/pow -> Math vs MathF).
                                if (metadata.TopLevelIntrinsics.TryGetValue(method.Name, out var prior))
                                {
                                    if (prior != intrinsic) metadata.AmbiguousTopLevelIntrinsics.Add(method.Name);
                                }
                                else metadata.TopLevelIntrinsics[method.Name] = intrinsic;
                                // ALSO key by name|<full ParamKey signature> so a call resolves the EXACT overload
                                // (sqrt(Double)->System.Math.Sqrt, sqrt(Float)->System.MathF.Sqrt) and a non-intrinsic
                                // sibling (Double.pow(Int)) misses -> falls through to its real Kotlin body.
                                metadata.TopLevelIntrinsicsBySig.TryAdd(method.Name + "|" + SigKeyOf(ps), intrinsic);
                                if (byrefPositions.Length > 0) metadata.TopLevelIntrinsicByref.TryAdd(method.Name, byrefPositions);
                            }
                            else if (ps.Length >= 1)
                                // Key by name|<full ParamKey signature> (receiver-first, mirroring TopLevelIntrinsicsBySig)
                                // so a call resolves the EXACT overload — `substring(Int)`@ClrIntrinsic does NOT capture a
                                // same-count non-intrinsic sibling `substring(IntRange)` (which then falls to its Kotlin body).
                                metadata.ExtMemberIntrinsics.TryAdd(method.Name + "|" + SigKeyOf(ps), intrinsic);
                        }
                        // A NON-intrinsic top-level fun (a real Kotlin body in a file-class) -> index it by name so an APP
                        // build can attribute a referenced `callStatic owner=null` to this file-class (disambiguated by the
                        // first-param receiver type when overloaded across file-classes). The stdlib self-build never reads it.
                        // #157: this DELIBERATELY has no IsSpecialName exclusion, so a top-level property ACCESSOR (`get_X`/
                        // `set_X` — a file-class static with intrinsic==null) is indexed too. That is what lets a cross-module
                        // top-level `val` read (kotc emits owner:null + prop:get -> reconstructed `get_X`) resolve GENERICALLY
                        // through TryResolveTopLevelStatic (e.g. COROUTINE_SUSPENDED -> IntrinsicsKt), with no per-name special-case.
                        var isCSharpExtension = method.IsStatic && method.GetCustomAttributesData().Any(a =>
                            a.AttributeType.FullName == "System.Runtime.CompilerServices.ExtensionAttribute");
                        if ((isFileClass || isCSharpExtension) && method.IsStatic && intrinsic == null)
                        {
                            var ps = method.GetParameters();
                            var rk = ps.Length >= 1 ? RecvKey(ps[0].ParameterType) : "";
                            // The FINE first-param key (ParamKey space): distinguishes the array overloads a coarse "[]"
                            // recvKey collapses (IntArray->"array:i32", UByteArray->"array:byte", Array<T>->"array:gp") so
                            // owner attribution pins the RIGHT file-class+overload (#153 unsigned-array miscompile).
                            var pk = ps.Length >= 1 ? ParamKey(TypeName(ps[0].ParameterType)) : "";
                            if (!metadata.TopLevelStatics.TryGetValue(method.Name, out var lst))
                                metadata.TopLevelStatics[method.Name] = lst = new List<(string, string, string)>();
                            lst.Add((ownerFqn, rk, pk));
                        }
                        // Collection/array FACTORY markers on a [KotlinFileClass] static (listOf/setOf/mapOf/arrayOf/…):
                        // record name -> kind so MemberCallSubstitution re-emits the newList/newSet/newMap/newArray node
                        // (the recognition kotc used to do via its LIST/SET/MAP/ARRAY_FACTORY tables). Every overload of a
                        // factory name agrees on the kind, so a name key is enough.
                        if (isFileClass && method.IsStatic)
                        {
                            if (AttrStringArg(method.GetCustomAttributesData(), "kotlin.clr.ClrCollectionFactory") is string cf)
                                metadata.CollectionFactories[method.Name] = cf;
                            if (AttrStringArg(method.GetCustomAttributesData(), "kotlin.clr.ClrArrayFactory") is string af)
                            {
                                metadata.ArrayFactories[method.Name] = af;
                                // Element hint for a concrete primitive factory (`intArrayOf`), which carries NO type
                                // argument of its own: it answers the call shapes whose vararg does not arrive as a
                                // `newArray` wrapper for MemberCallSubstitution to read the element off — a lone
                                // spread (`intArrayOf(*xs)`) or a mixed `spreadConcat`. An element LIST, empty or
                                // not, brings its own wrapper. Captured from the factory's array return type
                                // (`kotlin.IntArray` -> element `kotlin.Int`); null for the generic `arrayOf<T>`
                                // (whose element is a type variable — typeArgs[0] covers it there).
                                if (ArrayElemHint(method.ReturnType) is string ah)
                                    metadata.ArrayFactoryElemHints[method.Name] = ah;
                            }
                        }
                    }
                    // @KotlinDefault(index, bir) on a CONSTRUCTOR's params -> the splice source for a `new` that omits a
                    // non-constant default (#235). A ctor has no name of its own, so the key is `<owner>|.ctor|<declared
                    // param count>`: the args array a `new` carries is POSITIONALLY complete (kotc emits a placeholder for
                    // every omitted slot), so its length is that same declared count and each stamped index indexes it.
                    // NonPublic included for the same reason the method scan includes it — an `internal` ctor is still a
                    // legitimate splice target inside its own module's ref dll.
                    // A CONSTRUCTOR has no name of its own, so it is keyed by the `.ctor` pseudo-name (#235). Same scheme as
                    // the method scan above: the `new` carries its resolved ctor's declared parameter types in `argTypes`,
                    // the same ParamKey space, so same-arity ctor overloads resolve rather than collide.
                    foreach (var ctor in type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                    {
                        if (CallableDefaultsOf(ctor) is Dictionary<int, string> cdefaults)
                            AddKotlinDefaults(metadata, ownerFqn, CtorKeyName, ctor.GetParameters(), cdefaults);
                        // #86 D1 — a `new`'s arguments fill the constructor's declaration slots, so the ctor's shape is
                        // indexed exactly as a method's is. `Cell<T>(x: T?)` erases to `.ctor(object)` and its carrier
                        // holds the pre-erasure `T?`.
                        metadata.CtorBindings.Add(new CtorBinding(
                            ownerFqn,
                            ctor.GetParameters().Length,
                            ctor.GetParameters().Select(p => DeclarationTypeNode(p.ParameterType)).ToArray(),
                            dotKtAuthored
                                ? ctor.GetParameters().Select(p => CarrierTypeOf(p.GetCustomAttributesData(), ctor.DeclaringType?.Assembly, KotlinNullableGenericAttr)).ToArray()
                                : null));
                    }
                }
                catch (MalformedTrustedCompanionException) { throw; }
                catch (InvalidDataException) { throw; }
                catch (Exception ex)
                {
                    metadata.Diagnostics.Add($"subst scan skip {type?.FullName}: {ex.GetType().Name}");
                }
            }
        }
        catch (MalformedTrustedCompanionException) { throw; }
        catch (InvalidDataException) { throw; }
        catch (Exception ex)
        {
            metadata.Diagnostics.Add($"{Path.GetFileName(reference)}: subst scan failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    static bool IsDotKtEmittedAssembly(Assembly asm)
    {
        try
        {
            var marked = asm.GetCustomAttributesData().Any(c =>
                c.AttributeType.FullName == DotKtAssemblyMarkerAttr
                && c.ConstructorArguments.Count == 2
                && c.ConstructorArguments[0].Value as string == DotKtAssemblyMarkerKey
                && c.ConstructorArguments[1].Value as string == DotKtAssemblyMarkerValue);
            var carrier = asm.GetType(KotlinFileClassAttr, throwOnError: false, ignoreCase: false);
            return marked && carrier != null && carrier.GetCustomAttributesData()
                .Any(c => c.AttributeType.FullName == CompilerGeneratedAttr);
        }
        catch
        {
            // Classification is an ownership routing hint. An unreadable marker is not authority to reinterpret a
            // foreign assembly as Kotlin; the normal CLR path will surface any actually required metadata failure.
            return false;
        }
    }

    static Dictionary<Type, bool> ValidateCompanionCarriers(
        Type[] types,
        Assembly assembly,
        out Dictionary<string, string> singletonCompanionCarriers,
        Dictionary<string, string> companionCarriersByPhysicalOwner,
        Dictionary<string, string> companionSourceNamesByPhysicalOwner,
        Dictionary<string, string> companionPhysicalOwnerBySemanticType,
        Dictionary<string, string> companionSemanticOwnerByCarrier)
    {
        var physicalTypes = types
            .GroupBy(t => (Name: PhysicalMetadataName(t), Arity: DeclaredGenericArity(t)))
            .ToDictionary(g => g.Key, g => g.ToArray());
        var semanticOwners = new Dictionary<string, Type>(StringComparer.Ordinal);
        var claimedPhysicalOwners = new HashSet<Type>();
        var result = new Dictionary<Type, bool>();
        singletonCompanionCarriers = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var carrierType in types)
        {
            CustomAttributeData carrierAttribute;
            try
            {
                var carrierAttributes = carrierType.GetCustomAttributesData().Where(c =>
                    c.AttributeType.FullName == KotlinCompanionAttr &&
                    c.AttributeType.Assembly == assembly &&
                    HasAttribute(c.AttributeType.GetCustomAttributesData(), CompilerGeneratedAttr))
                    .ToArray();
                if (carrierAttributes.Length > 1)
                    throw new MalformedTrustedCompanionException(
                        $"duplicate trusted [KotlinCompanion] carriers on '{carrierType.FullName}'");
                carrierAttribute = carrierAttributes.SingleOrDefault();
            }
            catch (MalformedTrustedCompanionException) { throw; }
            catch (Exception ex)
            {
                throw new MalformedTrustedCompanionException(
                    $"could not inspect trusted [KotlinCompanion] on '{carrierType.FullName}'", ex);
            }
            if (carrierAttribute == null) continue;

            JsonObject payload;
            try
            {
                if (carrierAttribute.ConstructorArguments.Count != 2 ||
                    carrierAttribute.ConstructorArguments[0].Value is not string version)
                    throw new FormatException("expected (version, byte[]) constructor arguments");
                if (carrierAttribute.NamedArguments.Count != 0)
                    throw new FormatException("named arguments are forbidden");
                payload = BirCarrier.DecodeBody(version, ReadByteArrayArg(carrierAttribute.ConstructorArguments[1]))
                    as JsonObject ?? throw new FormatException("payload must be an object");
            }
            catch (Exception ex)
            {
                throw new MalformedTrustedCompanionException(
                    $"malformed trusted [KotlinCompanion] on '{carrierType.FullName}': {ex.Message}", ex);
            }

            string RequiredString(string property)
            {
                if (payload[property] is not JsonValue value ||
                    !value.TryGetValue<string>(out var text) || string.IsNullOrEmpty(text))
                    throw new MalformedTrustedCompanionException(
                        $"malformed trusted [KotlinCompanion] on '{carrierType.FullName}': '{property}' must be a non-empty string");
                return text;
            }
            var kind = RequiredString("kind");
            var owner = RequiredString("owner");
            var name = RequiredString("name");
            var visibility = RequiredString("visibility");
            var physicalOwner = RequiredString("physicalOwner");
            if (kind is not ("nested" or "sidecar"))
                throw new MalformedTrustedCompanionException(
                    $"malformed trusted [KotlinCompanion] on '{carrierType.FullName}': invalid kind '{kind}'");
            if (!IsSemanticQualifiedName(owner) || !IsSemanticNameSegment(name))
                throw new MalformedTrustedCompanionException(
                    $"malformed trusted [KotlinCompanion] on '{carrierType.FullName}': invalid semantic owner/name");
            if (visibility is not ("public" or "internal" or "private" or "protected" or "protectedInternal"))
                throw new MalformedTrustedCompanionException(
                    $"malformed trusted [KotlinCompanion] on '{carrierType.FullName}': invalid visibility '{visibility}'");
            if (payload["physicalOwnerArity"] is not JsonValue arityValue ||
                !arityValue.TryGetValue<int>(out var physicalOwnerArity) || physicalOwnerArity < 0)
                throw new MalformedTrustedCompanionException(
                    $"malformed trusted [KotlinCompanion] on '{carrierType.FullName}': invalid physicalOwnerArity");
            if (!physicalTypes.TryGetValue((physicalOwner, physicalOwnerArity), out var ownerMatches) ||
                ownerMatches.Length != 1)
                throw new MalformedTrustedCompanionException(
                    $"trusted [KotlinCompanion] owner '{physicalOwner}' arity {physicalOwnerArity} resolved to " +
                    $"{(ownerMatches is null ? 0 : ownerMatches.Length)} physical types");
            var ownerType = ownerMatches[0];
            if (!claimedPhysicalOwners.Add(ownerType))
                throw new MalformedTrustedCompanionException(
                    $"multiple trusted [KotlinCompanion] carriers name owner '{owner}'");
            if (!semanticOwners.TryAdd(owner, ownerType) && semanticOwners[owner] != ownerType)
                throw new MalformedTrustedCompanionException(
                    $"multiple physical types claim Kotlin companion owner '{owner}'");

            // A companion is nested in its physical owner exactly when that owner is non-generic; a generic owner's
            // carrier is hoisted beside it so the singleton cannot multiply across closed instantiations. Either way
            // the carrier itself declares no generic slot, so one `$INSTANCE` exists per Kotlin companion.
            if (kind == "sidecar")
            {
                if (physicalOwnerArity == 0)
                    throw new MalformedTrustedCompanionException(
                        "hoisted trusted [KotlinCompanion] requires a generic physical owner");
                if (carrierType.DeclaringType != null)
                    throw new MalformedTrustedCompanionException(
                        "hoisted trusted [KotlinCompanion] must be a top-level type");
                if (!carrierType.IsPublic)
                    throw new MalformedTrustedCompanionException(
                        "hoisted trusted [KotlinCompanion] carrier must be public");
            }
            else
            {
                if (physicalOwnerArity != 0)
                    throw new MalformedTrustedCompanionException(
                        "nested trusted [KotlinCompanion] requires a non-generic physical owner");
                if (carrierType == ownerType || carrierType.DeclaringType != ownerType)
                    throw new MalformedTrustedCompanionException(
                        "nested trusted [KotlinCompanion] must be an ordinary nested type of its physical owner");
                if (!carrierType.IsNestedPublic)
                    throw new MalformedTrustedCompanionException(
                        "nested trusted [KotlinCompanion] carrier must have NestedPublic visibility");
            }
            if (carrierType.GetGenericArguments().Length != 0)
                throw new MalformedTrustedCompanionException(
                    "trusted [KotlinCompanion] carrier must declare no generic parameters");
            if (!HasTrustedMarker(carrierType, assembly, "DotKt.Runtime.CompilerServices.KotlinObjectAttribute"))
                throw new MalformedTrustedCompanionException(
                    "trusted [KotlinCompanion] requires trusted [KotlinObject]");
            FieldInfo[] instances;
            try
            {
                instances = carrierType.GetFields(BindingFlags.Public | BindingFlags.NonPublic |
                    BindingFlags.Static | BindingFlags.DeclaredOnly)
                    .Where(f => f.Name == "$INSTANCE" && f.IsPublic && IsOpenSelfType(f.FieldType, carrierType))
                    .ToArray();
            }
            catch (Exception ex)
            {
                throw new MalformedTrustedCompanionException(
                    $"could not validate [KotlinCompanion] carrier '{carrierType.FullName}'", ex);
            }
            if (instances.Length != 1)
                throw new MalformedTrustedCompanionException(
                    "trusted [KotlinCompanion] requires one public static self-typed $INSTANCE field");

            companionSemanticOwnerByCarrier.Add(
                StripGenericArity(DottedFqn(carrierType.FullName ?? carrierType.Name)),
                StripGenericArity(owner));

            // Private/internal companions do not participate in downstream call binding. Public and protected
            // companions do; the carrier remains public enough for lifted helper types while the payload restores
            // Kotlin visibility. Every carrier was still validated above so malformed compiler-owned metadata cannot
            // silently alter the scan.
            if (visibility is "public" or "protected" &&
                IsPubliclyVisible(ownerType) && IsPubliclyVisible(carrierType))
            {
                result.Add(carrierType, false);
                var semanticType = StripGenericArity(owner) + ".<companion:" + name + ">";
                companionPhysicalOwnerBySemanticType.Add(semanticType,
                    carrierType.FullName ?? carrierType.Name);
                singletonCompanionCarriers.Add(
                    StripGenericArity(owner),
                    carrierType.FullName ?? carrierType.Name);
                companionCarriersByPhysicalOwner.Add(
                    StripGenericArity(DottedFqn(physicalOwner)),
                    carrierType.FullName ?? carrierType.Name);
                companionSourceNamesByPhysicalOwner.Add(
                    StripGenericArity(DottedFqn(physicalOwner)), name);
            }
        }
        return result;
    }

    static bool IsOpenSelfType(Type fieldType, Type carrierType)
    {
        if (!carrierType.IsGenericTypeDefinition) return fieldType == carrierType;
        if (!fieldType.IsGenericType || fieldType.GetGenericTypeDefinition() != carrierType) return false;
        var args = fieldType.GetGenericArguments();
        return args.Length == carrierType.GetGenericArguments().Length &&
            args.Select((arg, index) => arg.IsGenericParameter &&
                arg.DeclaringMethod == null && arg.GenericParameterPosition == index).All(x => x);
    }

    static readonly char[] ForbiddenSemanticNameCharacters = ['.', '/', '\\', '<', '>', ':', '[', ']', '$'];

    static bool IsSemanticNameSegment(string value) =>
        value.Length != 0 && value.IndexOfAny(ForbiddenSemanticNameCharacters) < 0 &&
        !value.Any(char.IsControl);

    static bool IsSemanticQualifiedName(string value) =>
        value.Split('.', StringSplitOptions.None).All(IsSemanticNameSegment);

    static bool HasTrustedMarker(Type type, Assembly assembly, string marker) =>
        type.GetCustomAttributesData().Any(c => c.AttributeType.FullName == marker &&
            c.AttributeType.Assembly == assembly &&
            HasAttribute(c.AttributeType.GetCustomAttributesData(), CompilerGeneratedAttr));

    static bool IsPubliclyVisible(Type type) => type.IsNested
        ? (type.IsNestedPublic || type.IsNestedFamily || type.IsNestedFamORAssem) &&
            type.DeclaringType != null && IsPubliclyVisible(type.DeclaringType)
        : type.IsPublic;

    static int DeclaredGenericArity(Type type)
    {
        if (!type.IsGenericType) return 0;
        var total = type.GetGenericArguments().Length;
        var inherited = type.DeclaringType is { IsGenericType: true } parent
            ? parent.GetGenericArguments().Length : 0;
        return total - inherited;
    }

    static string PhysicalMetadataName(Type type)
    {
        var simple = StripGenericArity(type.Name);
        if (type.DeclaringType != null) return PhysicalMetadataName(type.DeclaringType) + "+" + simple;
        return string.IsNullOrEmpty(type.Namespace) ? simple : type.Namespace + "." + simple;
    }

    static string ExactPhysicalMetadataName(Type type)
    {
        if (type.DeclaringType != null) return ExactPhysicalMetadataName(type.DeclaringType) + "+" + type.Name;
        return string.IsNullOrEmpty(type.Namespace) ? type.Name : type.Namespace + "." + type.Name;
    }

    // A reflected byte[] ctor argument materializes under MetadataLoadContext as an IReadOnlyList<CustomAttributeTypedArgument>
    // (each element's .Value a boxed byte), not a byte[] — reify it (mirrors ilemit Emitter.CompilerServices.ReadByteArrayArg).
    static byte[] ReadByteArrayArg(CustomAttributeTypedArgument a)
    {
        if (a.Value is byte[] b) return b;
        if (a.Value is IReadOnlyList<CustomAttributeTypedArgument> arr)
        {
            var r = new byte[arr.Count];
            for (int i = 0; i < arr.Count; i++) r[i] = (byte)arr[i].Value!;
            return r;
        }
        throw new FormatException("carrier content is not a byte[]");
    }

    static bool HasAttribute(IList<CustomAttributeData> attrs, string fullName) =>
        attrs.Any(a => a.AttributeType.FullName == fullName);

    // Decode a compiler-owned round-trip [KotlinType] carrier.
    static TypeNode KotlinTypeOf(IList<CustomAttributeData> attrs, Assembly declaringAssembly) =>
        CarrierTypeOf(attrs, declaringAssembly, KotlinTypeAttr);

    // Decode a compiler-owned round-trip TypeNode carrier (`[KotlinType]`, `[KotlinNullableGeneric]` — both ride the
    // same `(version, bytes)` BirCarrier envelope).  Full-name equality is insufficient: a foreign
    // assembly may define a lookalike.  The containing assembly has already passed the DotKt marker + generated
    // carrier test; additionally require this embedded attribute type to come from that assembly and itself carry
    // [CompilerGenerated], matching dll2klib's provenance rule.
    static TypeNode CarrierTypeOf(IList<CustomAttributeData> attrs, Assembly declaringAssembly, string attrFullName)
    {
        if (declaringAssembly == null) return null;
        foreach (var cad in attrs)
        {
            try
            {
                if (cad.AttributeType.FullName != attrFullName
                    || cad.AttributeType.Assembly != declaringAssembly
                    || !HasAttribute(cad.AttributeType.GetCustomAttributesData(), CompilerGeneratedAttr)
                    || cad.ConstructorArguments.Count != 2
                    || cad.ConstructorArguments[0].Value is not string version)
                    continue;
                var content = ReadByteArrayArg(cad.ConstructorArguments[1]);
                return TypeNode.Parse(BirCarrier.DecodeBody(version, content).ToJsonString());
            }
            catch
            {
                // A malformed carrier is not authority to alter the CLR call shape.  Ignore it and retain the ordinary
                // reflected Object return; the downstream verifier will surface any actually required conversion.
            }
        }
        return null;
    }

    static JsonNode CarrierJsonOf(
        IList<CustomAttributeData> attrs,
        Assembly declaringAssembly,
        string attrFullName)
    {
        if (declaringAssembly == null) return null;
        var cad = attrs.FirstOrDefault(c =>
            c.AttributeType.FullName == attrFullName &&
            c.AttributeType.Assembly == declaringAssembly &&
            HasAttribute(c.AttributeType.GetCustomAttributesData(), CompilerGeneratedAttr));
        if (cad == null || cad.ConstructorArguments.Count != 2 ||
            cad.ConstructorArguments[0].Value is not string version)
            return null;
        return BirCarrier.DecodeBody(version, ReadByteArrayArg(cad.ConstructorArguments[1]));
    }

    // The first constructor string argument of the attribute `fullName` (e.g. @ClrCollectionFactory("list") -> "list"),
    // or null when the attribute is absent / carries no string arg. Used for the factory-kind markers.
    static string AttrStringArg(IList<CustomAttributeData> attrs, string fullName)
    {
        var a = attrs.FirstOrDefault(x => x.AttributeType.FullName == fullName);
        return a != null && a.ConstructorArguments.Count > 0 ? a.ConstructorArguments[0].Value as string : null;
    }

    // The element type FQN of an array factory's return type (`kotlin.IntArray` -> "kotlin.Int"), or null when the return
    // is not a concrete array (the generic `arrayOf<T>` returns `Array<T>` whose element is a type variable). Used only as
    // a last-resort element source for a concrete primitive factory call that carries neither a type argument nor a
    // `newArray` vararg wrapper — i.e. one whose vararg was written as a spread.
    static string ArrayElemHint(Type retType)
    {
        try
        {
            if (retType != null && retType.IsArray)
            {
                var el = retType.GetElementType();
                if (el != null && !el.IsGenericParameter) return TypeName(el);
            }
        }
        catch { }
        return null;
    }

    // The class-level CLR binding: @ClrTypeAlias (the type-identity binding); a class-level @ClrIntrinsic is also
    // accepted for any not-yet-renamed bound class. Returns the single ctor-arg (the .NET FQN), or null if not CLR-bound.
    static string ClrAliasOf(IList<CustomAttributeData> attrs)
    {
        var a = attrs.FirstOrDefault(x => x.AttributeType.FullName is "kotlin.clr.ClrTypeAlias" or "kotlin.clr.ClrIntrinsic");
        return a != null && a.ConstructorArguments.Count > 0 ? a.ConstructorArguments[0].Value as string : null;
    }

    // The member-level CLR binding: @ClrIntrinsic("Name") (or AsDynamic). Returns the BCL member name (the call is
    // rewritten to owner.Name), or null when the member carries no intrinsic (a rule-3 candidate).
    static string ClrIntrinsicOf(IList<CustomAttributeData> attrs)
    {
        var a = attrs.FirstOrDefault(x => x.AttributeType.FullName is "kotlin.clr.ClrIntrinsic" or "kotlin.clr.ClrIntrinsicAsDynamic");
        return a != null && a.ConstructorArguments.Count > 0 ? a.ConstructorArguments[0].Value as string : null;
    }

    // The PARAMETER positions (0-based, over the method's declared params) marked @ClrRefArgument — a plain-typed
    // parameter the bound BCL member takes BY REFERENCE (`ref`/`out`). The substituted call wraps these argTypes
    // positions `byref:` so ilemit resolves the ref/out overload + emits the address-load. Empty when none.
    static int[] ByrefPositionsOf(MethodBase method)
    {
        var ps = method.GetParameters();
        List<int> hits = null;
        for (var i = 0; i < ps.Length; i++)
            if (ps[i].GetCustomAttributesData().Any(a => a.AttributeType.FullName == "kotlin.clr.ClrRefArgument"))
                (hits ??= new List<int>()).Add(i);
        return hits?.ToArray() ?? Array.Empty<int>();
    }

    // @KotlinDefault(index, bir) on the method's parameters -> (argPosition -> default-expression BIR-json). Returns null
    // when no parameter carries it. `index` is the parameter's position in the emitted call (extension receiver first);
    // `bir` is the default expression as a raw BIR-json string (opaque here — spliced pre-lowering by DefaultArgSplice).
    static Dictionary<int, string> KotlinDefaultsOf(MethodBase method)
    {
        Dictionary<int, string> map = null;
        foreach (var p in method.GetParameters())
        {
            var a = p.GetCustomAttributesData().FirstOrDefault(x => x.AttributeType.FullName == "kotlin.clr.KotlinDefault");
            if (a == null || a.ConstructorArguments.Count < 2) continue;
            if (a.ConstructorArguments[0].Value is null || a.ConstructorArguments[1].Value is not string bir) continue;
            (map ??= new Dictionary<int, string>())[Convert.ToInt32(a.ConstructorArguments[0].Value)] = bir;
        }
        return map;
    }

    // The complete default-value map for an already selected declaration. KotlinDefault wins because it carries the
    // Kotlin expression (including reads of earlier parameters/receivers); otherwise use the ECMA-335 constant directly.
    // This is deliberately a reference-DLL scan. Neither dll2klib nor kotc materializes a default value.
    static Dictionary<int, string> CallableDefaultsOf(MethodBase method)
    {
        var map = KotlinDefaultsOf(method);
        foreach (var p in method.GetParameters())
        {
            if (map?.ContainsKey(p.Position) == true || !p.HasDefaultValue) continue;
            if (ConstantDefaultBir(p) is not string bir) continue;
            (map ??= new Dictionary<int, string>())[p.Position] = bir;
        }
        return map;
    }

    static string ConstantDefaultBir(ParameterInfo parameter)
    {
        object value;
        try { value = parameter.RawDefaultValue; }
        catch { return null; }
        if (ReferenceEquals(value, DBNull.Value) || ReferenceEquals(value, Missing.Value)) return null;

        var type = parameter.ParameterType;
        if (type.IsEnum)
        {
            type = Enum.GetUnderlyingType(type);
            try { value = Convert.ChangeType(value, type, System.Globalization.CultureInfo.InvariantCulture); }
            catch { return null; }
        }

        JsonNode jsonValue = value switch
        {
            null => null,
            bool v => JsonValue.Create(v),
            char v => JsonValue.Create(v.ToString()),
            string v => JsonValue.Create(v),
            sbyte v => JsonValue.Create((int)v),
            byte v => JsonValue.Create((int)v),
            short v => JsonValue.Create((int)v),
            ushort v => JsonValue.Create((int)v),
            int v => JsonValue.Create(v),
            uint v => JsonValue.Create(unchecked((int)v)),
            long v => JsonValue.Create(v),
            ulong v => JsonValue.Create(unchecked((long)v)),
            float v => JsonValue.Create(v),
            double v => JsonValue.Create(v),
            _ => null,
        };
        if (value is not null && jsonValue is null) return null;
        var declaredType = DeclarationTypeNode(type);
        if (declaredType is null) return null;
        return new JsonObject {
            ["k"] = "const",
            ["type"] = TypeJson.Write(declaredType),
            ["value"] = jsonValue,
        }.ToJsonString();
    }

    // The member-level PROPERTY-accessor binding: @ClrProperty(access, name). `access` is the READ(1)/WRITE(2) flag word;
    // `name` is the .NET property. Returns (access, name) or null when the member carries no @ClrProperty.
    static (int Access, string Name)? ClrPropertyOf(IList<CustomAttributeData> attrs)
    {
        var a = attrs.FirstOrDefault(x => x.AttributeType.FullName == "kotlin.clr.ClrProperty");
        if (a == null || a.ConstructorArguments.Count < 2) return null;
        if (a.ConstructorArguments[1].Value is not string name) return null;
        var access = a.ConstructorArguments[0].Value is null ? 0 : Convert.ToInt32(a.ConstructorArguments[0].Value);
        return (access, name);
    }

    // A receiver-type key for an extension fun's first param, matched against a call's first-arg type. Arrays collapse
    // to "[]", generic params to "gp", a generic type to its open def's stripped FQN. A NESTED type's reflection name
    // ("kotlin.collections.Map`2+Entry`2") is normalized to the BIR semantic hierarchy
    // ("kotlin.collections.Map.Entry") — e.g. the Map.Entry.component1/2 extensions. Before #225 lifted nested
    // types carried a `$`-joined top-level metadata name; once ownership is represented by real CLR nesting, dropping
    // the declaring chain here would turn every sibling `Outer.Entry` into the unrelated `namespace.Entry`.
    static string RecvKey(Type t)
    {
        if (t.IsByRef && t.GetElementType() is Type e) t = e;
        if (t.IsArray) return "[]";
        if (t.IsGenericParameter) return "gp";
        var def = t.IsGenericType ? t.GetGenericTypeDefinition() : t;
        return DottedFqn(StripGenericArity(def.FullName ?? def.Name));
    }

    // A method's full ParamKey-normalized signature ("f64", "f64,f64", "i32", ...), used to overload-disambiguate a
    // top-level @ClrIntrinsic (sqrt(Double) vs sqrt(Float); pow(Double,Double) intrinsic vs pow(Double,Int) real-body).
    // Runs each param's TypeName through ParamKey so the ref.dll declaration and the call's kotc `sig` agree.
    static string SigKeyOf(ParameterInfo[] ps) => string.Join(",", ps.Select(p => ParamKey(TypeName(p.ParameterType))));

    /// A signature key with every position [ParamKey] could not FOLD collapsed to `ref`. The two sides of a
    /// @KotlinDefault lookup describe the same parameter in DIFFERENT spaces — a call site carries kotc's pre-lowering
    /// Kotlin type (`kotlin.collections.List`), a reference assembly its lowered CLR form
    /// (`System.Collections.Generic.IReadOnlyList`) — so only a token from [ParamKey]'s fold table is comparable, and
    /// anything else has to collapse. That still separates an overload differing in a folded position
    /// (`f(String, String)` from `f(String, List&lt;String&gt;)`), which is what the exact key cannot do here. Two
    /// overloads differing only between two DIFFERENT class types collapse together, are recorded as a conflict, and are
    /// refused rather than guessed.
    public static string RelaxedSigKey(string sigKey) =>
        string.Join(",", sigKey.Split(',').Select(RelaxToken));

    static string RelaxToken(string token)
    {
        foreach (var w in new[] { "byref:", "array:" })
            if (token.StartsWith(w, StringComparison.Ordinal)) return w + RelaxToken(token[w.Length..]);
        // NULLABILITY is asymmetric across the two spaces for a REFERENCE type: Kotlin's `String?` is a call-side
        // `nullable:str` but lowers to a plain `System.String` (its nullability rides [Nullable]), so the reference side
        // reads `str`. A nullable VALUE type is `System.Nullable<T>` on both sides and keeps the wrapper.
        if (token.StartsWith("nullable:", StringComparison.Ordinal))
        {
            var inner = RelaxToken(token["nullable:".Length..]);
            return ValueTokens.Contains(inner) ? "nullable:" + inner : inner;
        }
        // An ALLOW-LIST, not "is it dotted": a namespace-less emitted type (`dotkt$CharSequence`) is dotless yet still a
        // class, and its call-side spelling (`kotlin.CharSequence`) differs — collapsing both is what keeps them equal.
        return FoldedTokens.Contains(token) ? token : "ref";
    }

    // Every token [ParamKey] can produce for a type it FOLDED — i.e. one whose two spellings it made equal. A token
    // outside this set is a class identity that only one of the two spaces spells that way.
    static readonly HashSet<string> ValueTokens = new(StringComparer.Ordinal)
        { "i8", "i16", "i32", "i64", "f32", "f64", "bool", "char", "void", "byte", "ushort", "uint", "ulong" };
    static readonly HashSet<string> FoldedTokens = new(ValueTokens, StringComparer.Ordinal) { "str", "obj", "func", "gp" };

    /// Record one declaration's @KotlinDefault carriers under BOTH keys the splice can look up: `owner|name|arity|sigKey`
    /// (the exact overload — a call site reproduces that signature from its own declared parameter vector) and
    /// `owner|name|arity` (the fallback when no signature is available). The arity key is written once; a SECOND
    /// declaration of the same name+arity whose defaults differ marks it CONFLICTED, so the fallback refuses instead of
    /// serving whichever declaration the metadata scan happened to reach last.
    static void AddKotlinDefaults(ReferenceDotKtMetadata metadata, string ownerFqn, string name, ParameterInfo[] ps,
        Dictionary<int, string> defaults)
    {
        var arityKey = ownerFqn + "|" + name + "|" + ps.Length;
        var sig = SigKeyOf(ps);
        // The callee's DECLARED parameter types, for a call site that carries none of its own — a constructor
        // DELEGATION rides the ctor declaration, so `baseArgs` is a bare array with no signature vector. The splice
        // needs them to type the temp it binds each spliced value to.
        Put(arityKey + "|" + sig, defaults);                        // the exact signature
        Put(arityKey + "|~" + RelaxedSigKey(sig), defaults);        // class positions collapsed, for cross-space compare
        if (metadata.KotlinDefaults.TryGetValue(arityKey, out var prior))
        {
            if (!SameDefaults(prior, defaults)) metadata.KotlinDefaultsConflicted.Add(arityKey);
            return;
        }
        metadata.KotlinDefaults[arityKey] = defaults;

        void Put(string key, Dictionary<int, string> d)
        {
            // Two declarations landing on the SAME signature key can only be told apart by a finer key, so this one
            // refuses too rather than serving the last writer.
            if (metadata.KotlinDefaults.TryGetValue(key, out var had))
            {
                if (!SameDefaults(had, d)) metadata.KotlinDefaultsConflicted.Add(key);
                return;
            }
            metadata.KotlinDefaults[key] = d;
        }
    }

    // An @JvmInline backing-field's CLR `conv` target — the ilemit conv opcode token for the field's primitive type
    // (kotlin.Int -> "int", kotlin.Byte -> "sbyte", ...). Null if the field is not a primitive ilemit conv'able.
    static string InlineFieldConv(Type fieldType) => fieldType.FullName switch
    {
        "kotlin.Int" => "int", "kotlin.Long" => "long", "kotlin.Short" => "short", "kotlin.Byte" => "sbyte",
        "kotlin.Char" => "char", "kotlin.Double" => "double", "kotlin.Float" => "float",
        "System.Int32" => "int", "System.Int64" => "long", "System.Int16" => "short", "System.SByte" => "sbyte",
        "System.Char" => "char", "System.Double" => "double", "System.Single" => "float",
        _ => null,
    };

    static int KotlinFunctionFlags(IList<CustomAttributeData> attrs)
    {
        var attr = attrs.FirstOrDefault(a => a.AttributeType.FullName == KotlinFunctionAttr);
        if (attr == null || attr.ConstructorArguments.Count == 0) return 0;
        var value = attr.ConstructorArguments[0].Value;
        return value is int i ? i : 0;
    }

    static int? AttrInt32(IList<CustomAttributeData> attrs, string fullName)
    {
        var attr = attrs.FirstOrDefault(a => a.AttributeType.FullName == fullName);
        if (attr == null || attr.ConstructorArguments.Count == 0 || attr.ConstructorArguments[0].Value == null)
            return null;
        return Convert.ToInt32(attr.ConstructorArguments[0].Value, CultureInfo.InvariantCulture);
    }

    static string TypeName(Type type)
    {
        if (type.IsByRef)
            return "byref:" + TypeName(type.GetElementType()!);
        if (type.IsArray)
            return "array:" + TypeName(type.GetElementType()!);
        if (type.IsGenericParameter)
            return "gp:" + type.Name;
        if (IsDelegate(type))
            return DelegateTypeName(type);
        if (type.IsConstructedGenericType)
        {
            var def = type.GetGenericTypeDefinition();
            var args = type.GetGenericArguments().Select(TypeName).ToList();
            if (def == typeof(Nullable<>))
                return "nullable:" + args[0];
            if (IsFunc(def))
                return "func:" + args[^1] + ":" + string.Join(",", args.Take(args.Count - 1));
            if (IsAction(def))
                return "func:void:" + string.Join(",", args);
            return "clrg:" + StripGenericArity(def.FullName ?? def.Name) + "[" + string.Join(",", args) + "]";
        }

        return PrimitiveBirName(type) ?? StripGenericArity(type.FullName ?? type.Name);
    }

    // A STRUCTURED TypeNode from a reflected ref.dll type — the pure-Kotlin identity kotc would have emitted (the ref
    // surface's types ARE named kotlin.* — kotlin.collections.List<kotlin.String>, kotlin.Int, …). Used to carry a
    // top-level fn / member RETURN type so bir2cir StaticType (#59) can recover a `callStatic`/`callInstance` whose
    // node lacks a `ret` (a non-generic call — kotc emits `ret` only for a generic call). Covers the shapes StaticType
    // needs (Fqn+args for collection detect, nullable, array, primitive, tv); a delegate/func return is left null.
    static TypeNode TypeNodeOf(Type type)
    {
        if (type.IsByRef) return TypeNodeOf(type.GetElementType()!) is TypeNode e0 ? new TypeNode.ByRef(e0) : null;
        if (type.IsArray) return TypeNodeOf(type.GetElementType()!) is TypeNode e1 ? new TypeNode.Array(e1) : null;
        if (type.IsGenericParameter) return null;   // an unresolved fn type-param: no useful static identity
        if (IsDelegate(type)) return null;
        if (type.IsConstructedGenericType)
        {
            var def = type.GetGenericTypeDefinition();
            var args = type.GetGenericArguments().Select(TypeNodeOf).ToArray();
            if (def == typeof(Nullable<>)) return args[0] is TypeNode nv ? new TypeNode.Nullable(nv) : null;
            if (IsFunc(def) || IsAction(def)) return null;
            if (args.Any(a => a == null)) return new TypeNode.Fqn(StripGenericArity(def.FullName ?? def.Name));
            return new TypeNode.Fqn(StripGenericArity(def.FullName ?? def.Name), args);
        }
        var prim = PrimitiveBirName(type);
        return new TypeNode.Fqn(prim ?? StripGenericArity(type.FullName ?? type.Name));
    }

    // Declaration-signature projection used only by bir2cir's reference hierarchy/member index.
    // Unlike TypeNodeOf (a best-effort static-result helper), generic parameters are meaningful
    // here and must retain their CLR owner space and position.
    static TypeNode DeclarationTypeNode(Type type)
    {
        if (type == null) return null;
        if (type.IsByRef) return DeclarationTypeNode(type.GetElementType()!) is TypeNode e0 ? new TypeNode.ByRef(e0) : null;
        if (type.IsArray) return DeclarationTypeNode(type.GetElementType()!) is TypeNode e1 ? new TypeNode.Array(e1) : null;
        if (type.IsGenericParameter)
            return new TypeNode.Tv(type.DeclaringMethod != null ? "method" : "type", type.GenericParameterPosition);
        // Kotlin function types remain `{t:fn}` in CIR, with the exact physical delegate family retained.
        // Unknown/custom CLR delegates stay nominal FQNs below; shape-projecting them would lose their identity.
        var delegateFamily = DelegateFamily(type);
        if (delegateFamily != null)
        {
            var invoke = type.GetMethod("Invoke");
            if (invoke == null) return null;
            var ret = DeclarationTypeNode(invoke.ReturnType);
            var ps = invoke.GetParameters().Select(p => DeclarationTypeNode(p.ParameterType)).ToArray();
            return ret != null && ps.All(p => p != null) ? new TypeNode.Fn(false, ret, ps, null, delegateFamily) : null;
        }
        if (type.IsConstructedGenericType)
        {
            var def = type.GetGenericTypeDefinition();
            var args = type.GetGenericArguments().Select(DeclarationTypeNode).ToArray();
            if (def == typeof(Nullable<>)) return new TypeNode.Nullable(args[0]);
            return new TypeNode.Fqn(DottedFqn(StripGenericArity(def.FullName ?? def.Name)), args);
        }
        var prim = PrimitiveBirName(type);
        return new TypeNode.Fqn(prim ?? DottedFqn(StripGenericArity(type.FullName ?? type.Name)));
    }

    static bool IsFunc(Type type) =>
        type.Namespace == "System" && type.Name.StartsWith("Func`", StringComparison.Ordinal);

    static bool IsAction(Type type) =>
        type.Namespace == "System" && type.Name.StartsWith("Action`", StringComparison.Ordinal);

    static string DelegateFamily(Type type)
    {
        Type def;
        try { def = type.IsGenericType && !type.IsGenericTypeDefinition ? type.GetGenericTypeDefinition() : type; }
        catch { return null; }
        if (def.Namespace == "System")
        {
            if (def.Name == "Action" || def.Name.StartsWith("Action`", StringComparison.Ordinal)) return "System.Action";
            if (def.Name.StartsWith("Func`", StringComparison.Ordinal)) return "System.Func";
        }
        if (def.Namespace == "DotKt.Runtime.CompilerServices")
        {
            if (def.Name.StartsWith("KAction`", StringComparison.Ordinal)) return "DotKt.Runtime.CompilerServices.KAction";
            if (def.Name.StartsWith("KFunc`", StringComparison.Ordinal)) return "DotKt.Runtime.CompilerServices.KFunc";
        }
        return null;
    }

    static bool IsDelegate(Type type)
    {
        for (var cur = type; cur != null; cur = cur.BaseType)
            if (cur.FullName == "System.MulticastDelegate")
                return true;
        return false;
    }

    static string DelegateTypeName(Type type)
    {
        var invoke = type.GetMethod("Invoke");
        if (invoke == null) return PrimitiveBirName(type) ?? StripGenericArity(type.FullName ?? type.Name);
        return "func:" + TypeName(invoke.ReturnType) + ":" + string.Join(",", invoke.GetParameters().Select(p => TypeName(p.ParameterType)));
    }

    static string PrimitiveBirName(Type type)
    {
        if (type == typeof(bool)) return "bool";
        // .NET-aligned 8-bit tokens (#54): "sbyte" is SIGNED = kotlin.Byte (System.SByte); "byte" is UNSIGNED =
        // kotlin.UByte (System.Byte). This matches int/short/long, whose token names already agree with .NET.
        // The unsigned family (ushort/uint/ulong) is here for the same reason.
        if (type == typeof(sbyte)) return "sbyte";
        if (type == typeof(byte)) return "byte";
        if (type == typeof(char)) return "char";
        if (type == typeof(double)) return "double";
        if (type == typeof(float)) return "float";
        if (type == typeof(int)) return "int";
        if (type == typeof(long)) return "long";
        if (type == typeof(object)) return "object";
        if (type == typeof(short)) return "short";
        if (type == typeof(ushort)) return "ushort";
        if (type == typeof(uint)) return "uint";
        if (type == typeof(ulong)) return "ulong";
        if (type == typeof(string)) return "string";
        if (type == typeof(void)) return "void";
        // The REFERENCE stdlib emits the pure-Kotlin primitives as real types whose FullName is literally
        // "kotlin.Int" / "kotlin.String" / ... When such a ref dll is read back, converge those onto the SAME
        // CLR-shorthand token as their BCL twin so a member signature speaks one vocabulary for TypeMatches.
        return PrimitiveBirNameByFullName(type.FullName);
    }

    static string PrimitiveBirNameByFullName(string fullName) => fullName switch
    {
        "kotlin.Boolean" => "bool",
        "kotlin.Byte" => "sbyte",
        "kotlin.Char" => "char",
        "kotlin.Double" => "double",
        "kotlin.Float" => "float",
        "kotlin.Int" => "int",
        "kotlin.Long" => "long",
        "kotlin.Any" => "object",
        "kotlin.Short" => "short",
        "kotlin.String" => "string",
        "kotlin.UByte" => "byte",
        "kotlin.UInt" => "uint",
        "kotlin.ULong" => "ulong",
        "kotlin.UShort" => "ushort",
        "kotlin.Unit" => "void",
        _ => null,
    };

    static string TypeKind(Type type)
    {
        if (type.IsInterface) return "interface";
        if (type.IsEnum) return "enum";
        if (type.IsValueType) return "struct";
        return "class";
    }

    // A BYREF-LIKE type (`ref struct`): a value that may hold a managed pointer, which the CLR forbids as the type of an
    // instance field of a non-byref-like type. The CLR encodes it as `IsByRefLikeAttribute`, so the attribute probe is
    // what actually answers here. `Type.IsByRefLike` follows only as a best-effort second try for the runtime-intrinsic
    // byref-likes (TypedReference/ArgIterator/RuntimeArgumentHandle) that carry no attribute: MetadataLoadContext is not
    // required to implement it, hence the catch-false — a throw means "unknown", which reads as not byref-like.
    const string ByRefLikeAttrFqn = "System.Runtime.CompilerServices.IsByRefLikeAttribute";
    static bool IsByRefLikeType(Type type)
    {
        if (!type.IsValueType) return false;
        try { if (type.GetCustomAttributesData().Any(c => c.AttributeType?.FullName == ByRefLikeAttrFqn)) return true; } catch { }
        try { return type.IsByRefLike; } catch { return false; }
    }

    // The constraint class of a generic parameter (a `GetGenericArguments()` element): "struct" when it carries the
    // value-type constraint (`where T : struct`), "class" when it carries the reference constraint (`where T : class`),
    // else "unconstrained". Drives the tv struct-ness oracle for the nullability fold.
    static string GenericParamConstraintClass(Type gp)
    {
        var a = gp.GenericParameterAttributes;
        if ((a & GenericParameterAttributes.NotNullableValueTypeConstraint) != 0) return "struct";
        if ((a & GenericParameterAttributes.ReferenceTypeConstraint) != 0) return "class";
        return "unconstrained";
    }

    static string StripGenericArity(string value)
    {
        if (value == null || value.IndexOf('`') < 0) return value;
        // A nested generic reflection name has one arity suffix per generic segment
        // (`Map`2+Map$Entry`2`). Truncating at the first backtick collapses the nested declaration to its outer owner
        // and can then apply the outer owner's @ClrTypeAlias to a member signature. Remove only each `N suffix.
        var result = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length;)
        {
            if (value[i] == '`' && i + 1 < value.Length && char.IsAsciiDigit(value[i + 1]))
            {
                i += 2;
                while (i < value.Length && char.IsAsciiDigit(value[i])) i++;
                continue;
            }
            result.Append(value[i++]);
        }
        return result.ToString();
    }

    // The nested-type separator normalizer: a reflected FullName uses `+` between an enclosing type and its nested type
    // (`kotlin.coroutines.CoroutineContext+Key`), while kotc/bir2cir speak dots everywhere. Converge onto dots so a
    // bound-index lookup keyed by kotc's `kotlin.coroutines.CoroutineContext.Key` matches.
    static string DottedFqn(string value) => value.Replace('+', '.');

    // The declared type-BOUND of a generic parameter as a structured TypeNode (`E : Element` -> Fqn(Element)), or null
    // when unconstrained / the sole bound is objectish (System.Object -> no useful restriction) / the bound is a
    // self-referential F-bound. A gp-dependent constraint (`E : Enum<E>`, or a sibling-var bound) has no valid closed
    // generic to substitute a `<*>` arg to, so it returns null — the objectish arg is left unchanged (symmetric with
    // StarProjectionBoundLowering's local ContainsTypeVar skip). The special class/struct/new() constraints are
    // irrelevant here (not a type identity a `<*>` arg can be repointed to). Nested `+` separators are normalized to dots.
    static TypeNode GenericParamBound(Type gp)
    {
        foreach (var c in gp.GetGenericParameterConstraints())
        {
            if (c.IsGenericParameter) continue;                       // a bare sibling type-var bound: no type identity
            if (c.IsConstructedGenericType && c.GetGenericArguments().Any(a => a.IsGenericParameter)) continue;  // F-bound / gp-dependent: no closed form
            if (TypeNodeOf(c) is not TypeNode node) continue;
            if (node is TypeNode.Fqn { Args: null } f && (f.Name is "object" or "System.Object" || DottedFqn(f.Name) == "kotlin.Any")) continue;
            return NormalizeNestedNames(node);
        }
        return null;
    }

    // Recursively converge every Fqn name in a TypeNode onto dotted form (a nested-type bound like Element carries a `+`).
    static TypeNode NormalizeNestedNames(TypeNode t) => t switch
    {
        TypeNode.Fqn f => new TypeNode.Fqn(DottedFqn(f.Name), f.Args?.Select(NormalizeNestedNames).ToArray()),
        TypeNode.Nullable n => new TypeNode.Nullable(NormalizeNestedNames(n.Of)),
        TypeNode.Array a => new TypeNode.Array(NormalizeNestedNames(a.Elem)),
        TypeNode.ByRef b => new TypeNode.ByRef(NormalizeNestedNames(b.Of)),
        _ => t,
    };
}

sealed record ReferenceAssembly(string Path, string Name, string Version, ReferenceDotKtMetadata DotKt);

sealed class ReferenceDotKtMetadata
{
    public readonly List<string> Diagnostics = new();

    // CALL-SUBSTITUTION metadata (sourced from the ref.dll, consumed by MemberCallSubstitution; NOT serialized).
    // ownerFqn (the Kotlin FQN, e.g. "kotlin.String") -> the BCL alias it binds to ("System.String"), from a
    // class-level @ClrTypeAlias (the type-identity binding) or, for a not-yet-renamed bound class, a class-level @ClrIntrinsic.
    public readonly Dictionary<string, string> Aliases = new(StringComparer.Ordinal);
    public readonly Dictionary<string, string> TypeKinds = new(StringComparer.Ordinal);   // ownerFqn -> class/struct/interface/enum
    public readonly HashSet<string> ByRefLikeOwners = new(StringComparer.Ordinal);        // ownerFqn -> is a `ref struct` (see IsByRefLikeFqn)
    public readonly HashSet<string> DotKtOwners = new(StringComparer.Ordinal);             // producer-marked DotKt assembly types
    public readonly Dictionary<string, string> ExistentialPhysicalBySemanticOwner = new(StringComparer.Ordinal);
    public readonly Dictionary<string, bool> CompanionStaticByPhysicalOwner = new(StringComparer.Ordinal);
    public readonly Dictionary<string, string> SingletonCompanionCarrierBySemanticOwner = new(StringComparer.Ordinal);
    public readonly Dictionary<string, string> CompanionCarrierByPhysicalOwner = new(StringComparer.Ordinal);
    public readonly Dictionary<string, string> CompanionSourceNameByPhysicalOwner = new(StringComparer.Ordinal);
    public readonly Dictionary<string, string> CompanionPhysicalOwnerBySemanticType = new(StringComparer.Ordinal);
    public readonly Dictionary<string, string> CompanionSemanticOwnerByCarrier = new(StringComparer.Ordinal);
    public readonly Dictionary<string, int> TypeArity = new(StringComparer.Ordinal);       // ownerFqn -> generic arity
    public readonly Dictionary<string, string[]> TypeParamNames = new(StringComparer.Ordinal); // ownerFqn -> generic param names
    public readonly Dictionary<string, string[]> CtorParamTypes = new(StringComparer.Ordinal); // ownerFqn -> (first) ctor param type names
    public readonly Dictionary<string, string[]> TypeParamConstraints = new(StringComparer.Ordinal); // ownerFqn -> per-param "struct"/"class"/"unconstrained"
    public readonly Dictionary<string, TypeNode[]> TypeParamBounds = new(StringComparer.Ordinal); // DOTTED ownerFqn -> per-param declared bound TypeNode (null when unconstrained/objectish)
    public readonly HashSet<string> HelperTypes = new(StringComparer.Ordinal);            // emitted "dotkt$ClrH_*" rule-3 helpers
    // Types carrying @kotlin.coroutines.RestrictsSuspension (BINARY-retained, so present on the ref.dll). A suspend
    // lambda whose RECEIVER is such a scope (e.g. SequenceScope) gets the RestrictedSuspendLambda SM base (bundle-6 P5).
    public readonly HashSet<string> RestrictsSuspensionTypes = new(StringComparer.Ordinal);
    public readonly List<MemberBinding> MemberBindings = new();                           // per-member @ClrIntrinsic + shape
    public readonly List<CtorBinding> CtorBindings = new();                               // per-ctor declaration shape (#86 D1)
    public readonly Dictionary<string, ReferenceTypeShape> TypeShapes = new(StringComparer.Ordinal);
    public readonly Dictionary<string, string> PhysicalTypeBySemanticName = new(StringComparer.Ordinal);
    public readonly Dictionary<string, int> InnerCapturedCount = new(StringComparer.Ordinal);
    public readonly Dictionary<string, string> InnerSemanticOwner = new(StringComparer.Ordinal);
    // [KotlinInline] raw-BIR payloads (#71/#75): "owner|name|pc|ga" -> the candidate decoded carrier JSONs (one per overload).
    public readonly Dictionary<string, List<string>> InlinePayloads = new(StringComparer.Ordinal);
    // Top-level fun name -> its @ClrIntrinsic fully-qualified static target ("System.Diagnostics.Stopwatch.GetTimestamp").
    // A top-level fun is a static method of a [KotlinFileClass] type; its call site is `callStatic owner=null`.
    public readonly Dictionary<string, string> TopLevelIntrinsics = new(StringComparer.Ordinal);
    public readonly Dictionary<string, string> TopLevelIntrinsicsBySig = new(StringComparer.Ordinal);
    public readonly HashSet<string> AmbiguousTopLevelIntrinsics = new(StringComparer.Ordinal);
    // Top-level @ClrIntrinsic fun name -> the 0-based parameter positions its bound BCL static takes BY REFERENCE
    // (@ClrRefArgument). The substituted clrStatic wraps these argTypes positions `byref:` (tryParseInt32's `out result`,
    // Interlocked's `ref location`, Math.DivRem's `out remainder`). Absent when the fun has no byref parameter.
    public readonly Dictionary<string, int[]> TopLevelIntrinsicByref = new(StringComparer.Ordinal);
    // Bare-@ClrIntrinsic extension fun, keyed "funName|recvKey" (recvKey = the receiver/first-param type) -> the BCL
    // member name. Receiver-keyed because the bare name collides across receivers (set->set_Item vs set->set_Chars).
    public readonly Dictionary<string, string> ExtMemberIntrinsics = new(StringComparer.Ordinal);
    // @JvmInline value-class owner FQN -> (its single backing-field getter "get_data", the field's CLR conv token).
    // The class is ERASED to its primitive CLR form, so `get_data()` is the inline unbox: it collapses to the receiver
    // value conv'd to the field's declared type (a `conv`, never a `ldfld data` — the erased primitive has no field).
    public readonly Dictionary<string, (string Getter, string Conv)> InlineBacking = new(StringComparer.Ordinal);
    // NON-intrinsic top-level funs (real Kotlin bodies in a [KotlinFileClass]) -> their (file-class owner FQN, first-
    // param recvKey). Keyed by fun name. Lets an APP build resolve a referenced `callStatic owner=null` to the file-
    // class it actually lives in (getOrElse -> kotlin.collections._CollectionsKt), disambiguated by the call's receiver
    // type when the name is defined across multiple file-classes (CollectionsKt vs ArraysKt vs MapsKt). NOT consulted in
    // a stdlib self-build (the fun is local there; owner=null + FindStatic finds the sibling).
    public readonly Dictionary<string, List<(string Owner, string RecvKey, string ParamKey)>> TopLevelStatics = new(StringComparer.Ordinal);
    // Collection/array FACTORY top-level funs, keyed by fun NAME -> the factory kind. A @kotlin.clr.ClrCollectionFactory
    // ("list"/"set"/"map") or @kotlin.clr.ClrArrayFactory ("vararg"/"sized") marker on a [KotlinFileClass] static.
    // MemberCallSubstitution reads these on a `callStatic owner=null` (listOf/setOf/mapOf/arrayOf/intArrayOf/arrayOfNulls
    // -> the `{k:newList/newSet/newMap/newArray/newArraySized}` construction node kotc used to synthesize). Keyed by name
    // alone: every overload of a factory name shares the kind, so no receiver disambiguation is needed.
    public readonly Dictionary<string, string> CollectionFactories = new(StringComparer.Ordinal);
    public readonly Dictionary<string, string> ArrayFactories = new(StringComparer.Ordinal);
    public readonly Dictionary<string, string> ArrayFactoryElemHints = new(StringComparer.Ordinal); // concrete-primitive elem (spread call)
    // A defaulted parameter's default-value expression as BIR (from @KotlinDefault), for CROSS-MODULE splice of an
    // omitted argument. Keyed "ownerFqn|methodName|paramCount" -> (argPosition -> BIR-json string). The DefaultArgSplice
    // pass reads this to fill trailing omitted args BEFORE the CharSequence bridge + type lowering (so a String default
    // is coerced exactly like an explicit arg). Rides the ref.dll only (param attrs stripped in the rt build).
    public readonly Dictionary<string, Dictionary<int, string>> KotlinDefaults = new(StringComparer.Ordinal);
    // Keys of [KotlinDefaults] that TWO declarations of the same owner+name+arity carry with DIFFERENT defaults — the key
    // cannot tell them apart, so the splice must refuse instead of filling whichever was enumerated last. Populated for
    // both METHODS and CONSTRUCTORS (same-arity overloads are common; #235).
    public readonly HashSet<string> KotlinDefaultsConflicted = new(StringComparer.Ordinal);
}

// A single ref.dll member's call-substitution shape. Owner is the Kotlin FQN ("kotlin.String"); Intrinsic is the
// @ClrIntrinsic BCL name or null (null + no @ClrProperty + !IsAbstract = a rule-3 hoist candidate). PropertyName (+ the
// READ/WRITE access flags) is set when the member carries @ClrProperty — an EXPLICIT .NET property accessor binding.
// Suspend = the Kotlin `suspend` modifier, read from the DotKt round-trip [KotlinFunction(flags)] attribute
// (Suspend bit = 4) in the LIVE MetadataLoadContext scan. Populated for the Task-based coroutine bundle (bundle 6):
// a cross-module call site must know "is this referenced callee suspend?" (its CLR shape is the Task<T> kickoff).
// NO consumer reads it yet — bundle 6 wires it.
sealed record ReferenceTypeShape(int TypeParamCount, string Kind, TypeNode.Fqn Base, TypeNode.Fqn[] Interfaces);

// The outcome of looking for one member at one owner (#86 D1). `NotDeclared` is the ONLY one that lets the search
// continue to the supertypes: a member declared at this level is the declaration the call binds to whether or not it
// carries erasure facts, and `Refused` is a decision — an overload set or a disagreeing diamond — that no other level
// may overturn.
enum SlotLookup { NotDeclared, Declared, Refused }

// ONE declaration slot as the reader may report it (#86 D1). `Refused` is not `Node == null`: the first says the
// reader saw a carrier and decided it must not be stated, the second says the producer stated nothing. Only the
// second may fall back to anything.
readonly record struct SlotFact(TypeNode Node, bool Refused);

// `ReturnType` is the best-effort STATIC-RESULT projection (TypeNodeOf): it drops a generic parameter, because its
// consumers want a usable concrete identity or nothing. `ReturnTypeNode` is the DECLARATION projection
// (DeclarationTypeNode), the same one `ParamTypeNodes` uses, which keeps generic parameters as `Tv` — a declaration
// the caller substitutes. The two are not interchangeable: `Iterable<E>.iterator()` is `Iterator` in the first and
// `Iterator<!0>` in the second, and only the second says what the call site's type argument completes.
sealed record MemberBinding(string Owner, string Name, int ParamCount, string Intrinsic, bool IsAbstract, bool IsStatic, string[] ParamTypes = null, int PropertyAccess = 0, string PropertyName = null, int[] ByrefPositions = null, bool Suspend = false, bool Conv = false, string ConvTo = null, TypeNode ReturnType = null, int MethodArity = 0, TypeNode[] ParamTypeNodes = null, bool IsVirtual = false, TypeNode KotlinReturnType = null, TypeNode NullableGenericRet = null, TypeNode[] NullableGenericParams = null, TypeNode ReturnTypeNode = null);

// The exact authored binding selected from a complete declaration identity. Carrying this value across the alias-
// companion rewrite prevents a later name+arity lookup from silently selecting a different overload.
sealed record ExactClrMemberBinding(string Intrinsic, int PropertyAccess, string PropertyName,
    bool Conv, string ConvTo, int[] ByrefPositions);

// A referenced CONSTRUCTOR's declaration shape. A `new` is a call whose declaration is the owner's constructor, so the
// nullable-generic realign types its arguments exactly as it types a method call's — and a ctor has no name of its own,
// so the key is owner + declared parameter count. `ParamTypeNodes` is the physical CLR signature with generic
// parameters retained; `NullableGenericParams[i]` is the pre-erasure `[KotlinNullableGeneric]` carrier of that slot
// when it has one.
sealed record CtorBinding(string Owner, int ParamCount, TypeNode[] ParamTypeNodes, TypeNode[] NullableGenericParams);
