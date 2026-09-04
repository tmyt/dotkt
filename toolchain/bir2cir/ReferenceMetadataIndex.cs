using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Collections.Immutable;
using DotKt.Bir;
using DotKt.Toolchain;

internal delegate bool ValueTypeOracle(TypeNode.Fqn type);

sealed partial class ReferenceMetadataIndex
{
    public enum TypeKeyKind
    {
        Named,
        Reference,
        GenericParameter,
        Function,
        Object,
        String,
        Void,
        Int8,
        Int16,
        Int32,
        Int64,
        Float32,
        Float64,
        Boolean,
        Char,
        UInt8,
        UInt16,
        UInt32,
        UInt64,
        ByRef,
        Array,
        Nullable,
    }

    public sealed record TypeKey(TypeKeyKind Kind, string Name = null, TypeKey Element = null);

    public sealed class SignatureKey : IEquatable<SignatureKey>
    {
        readonly TypeKey[] _parameters;

        public SignatureKey(IEnumerable<TypeKey> parameters) => _parameters = parameters.ToArray();

        public bool Equals(SignatureKey other) =>
            other != null && _parameters.SequenceEqual(other._parameters);

        public override bool Equals(object obj) => Equals(obj as SignatureKey);

        public override int GetHashCode()
        {
            var hash = new HashCode();
            foreach (var parameter in _parameters) hash.Add(parameter);
            return hash.ToHashCode();
        }
    }

    public readonly record struct DefaultKey(
        string Owner, string Method, int ParamCount, SignatureKey Signature = null, bool Relaxed = false);

    public sealed record RichEnumMetadata(
        IReadOnlyDictionary<string, string> EntryFields,
        string Name,
        string Ordinal,
        string Values,
        string ValueOf);

    sealed class MalformedTrustedCompanionException : Exception
    {
        public MalformedTrustedCompanionException(string message, Exception inner = null) : base(message, inner) { }
    }

    sealed class MalformedTrustedStaticCarrierException : Exception
    {
        public MalformedTrustedStaticCarrierException(string message, Exception inner = null) : base(message, inner) { }
    }

    const string KotlinFileClassAttr = "DotKt.Runtime.CompilerServices.KotlinFileClassAttribute";
    const string KotlinFunctionAttr = "DotKt.Runtime.CompilerServices.KotlinFunctionAttribute";
    const string KotlinInlineAttr = "DotKt.Runtime.CompilerServices.KotlinInlineAttribute";
    const string KotlinTypeAttr = "DotKt.Runtime.CompilerServices.KotlinTypeAttribute";
    const string KotlinSuspendResultAttr = "DotKt.Runtime.CompilerServices.KotlinSuspendResultAttribute";
    const string KotlinCompanionAttr = "DotKt.Runtime.CompilerServices.KotlinCompanionAttribute";
    const string KotlinCompanionExtensionAttr = "DotKt.Runtime.CompilerServices.KotlinCompanionExtensionAttribute";
    const string KotlinPropertyAccessorAttr = "DotKt.Runtime.CompilerServices.KotlinPropertyAccessorAttribute";
    const string KotlinSourceMethodAttr = "DotKt.Runtime.CompilerServices.KotlinSourceMethodAttribute";
    const string KotlinInnerConstructorFactoryAttr = "DotKt.Runtime.CompilerServices.KotlinInnerConstructorFactoryAttribute";
    const string KotlinDeclarationIdentityAttr = "DotKt.Runtime.CompilerServices.KotlinDeclarationIdentityAttribute";
    const string KotlinConstructorAdapterAttr = "DotKt.Runtime.CompilerServices.KotlinConstructorAdapterAttribute";
    const string KotlinExtensionCoreAttr = "DotKt.Runtime.CompilerServices.KotlinExtensionCoreAttribute";
    const string KotlinStaticCarrierAttr = "DotKt.Runtime.CompilerServices.KotlinStaticCarrierAttribute";
    const string KotlinRichEnumAttr = "DotKt.Runtime.CompilerServices.KotlinRichEnumAttribute";
    const string KotlinBasicEnumAttr = "DotKt.Runtime.CompilerServices.KotlinBasicEnumAttribute";
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
    // Exact projected type identity -> class/struct/interface/enum. A TypeNode keeps generic arguments outside its
    // name, so the identity is the dotted, arity-free FQN PLUS the flattened argument count. Keying only by the name
    // conflates legal CLR declarations such as `Vector` and `Vector<T>`; retaining reflection's `+` separator instead
    // makes the answer depend on whether kotc or the reference-metadata reader minted the token.
    readonly Dictionary<OwnerTypeIdentity, string> _ownerKind = new();
    readonly Dictionary<string, string> _ownerKindByPhysicalOwner = new(StringComparer.Ordinal);
    // The kind of an @ClrTypeAlias declaration is a fact about the SEMANTIC owner, not about whatever unrelated CLR
    // declaration happens to share its arity-free name. Keep it beside the alias rather than recovering it from the
    // all-reference type-kind index in TryResolveClrOwner.
    readonly Dictionary<string, string> _ownerAliasKind = new(StringComparer.Ordinal);
    readonly HashSet<OwnerTypeIdentity> _byRefLikeOwners = new();
    readonly HashSet<string> _byRefLikePhysicalOwners = new(StringComparer.Ordinal);
    readonly HashSet<string> _dotKtOwners = new(StringComparer.Ordinal);              // types authored by a DotKt-emitted assembly
    readonly Dictionary<string, RichEnumMetadata> _richEnums = new(StringComparer.Ordinal);
    readonly Dictionary<string, BasicEnumMetadata> _basicEnums = new(StringComparer.Ordinal);
    // Trusted [KotlinType(G<*,...>)] on a compiler-generated non-generic interface is the explicit existential ABI
    // relation. No physical-name suffix participates in recognition.
    readonly Dictionary<string, string> _existentialPhysicalBySemanticOwner = new(StringComparer.Ordinal);
    readonly HashSet<string> _existentialPhysicalOwners = new(StringComparer.Ordinal);
    readonly Dictionary<string, string> _existentialSemanticByPhysicalOwner = new(StringComparer.Ordinal);
    readonly HashSet<string> _fileClassOwners = new(StringComparer.Ordinal);          // trusted Kotlin top-level declaration hosts
    readonly Dictionary<string, bool> _companionStaticByPhysicalOwner = new(StringComparer.Ordinal);
    readonly Dictionary<string, string> _singletonCompanionCarrierBySemanticOwner = new(StringComparer.Ordinal);
    readonly Dictionary<string, string> _semanticOwnerByCompanionCarrier = new(StringComparer.Ordinal);
    readonly Dictionary<string, string> _companionCarrierByPhysicalOwner = new(StringComparer.Ordinal);
    readonly Dictionary<string, string> _companionSourceNameByPhysicalOwner = new(StringComparer.Ordinal);
    readonly Dictionary<string, string> _companionExtensionMembers = new(StringComparer.Ordinal);
    readonly Dictionary<string, string> _companionPhysicalOwnerBySemanticType = new(StringComparer.Ordinal);
    readonly Dictionary<string, string> _genericStaticCarrierBySemanticOwner = new(StringComparer.Ordinal);
    readonly HashSet<string> _genericStaticCarriers = new(StringComparer.Ordinal);
    readonly Dictionary<string, string> _companionSemanticTypeByPhysicalOwner = new(StringComparer.Ordinal);
    readonly Dictionary<string, int> _ownerArity = new(StringComparer.Ordinal);      // Kotlin FQN -> generic arity
    readonly Dictionary<string, string[]> _ownerTypeParams = new(StringComparer.Ordinal); // Kotlin FQN -> generic param names
    // Exact CLR declarations of referenced owner parameters. C# 14 static-extension grouping types must repeat the
    // receiver block's constraints verbatim; the coarser nullability/star-projection indexes below are insufficient
    // for F-bounds and the CLR class/struct/new() flags.
    readonly Dictionary<string, string> _ownerTypeParamDeclarations = new(StringComparer.Ordinal);
    // A referenced concrete type satisfies the CLR new() constraint exactly when it is a non-abstract reference type
    // with a public parameterless instance constructor, or any value type. This is a physical metadata fact used by
    // ExternalGenericConstraintValidation; Kotlin has no nominal upper bound that can encode it.
    readonly HashSet<OwnerTypeIdentity> _publicParameterlessConstructibleOwners = new();
    readonly HashSet<string> _publicParameterlessConstructiblePhysicalOwners = new(StringComparer.Ordinal);
    // Per owner-FQN, the declared parameter types of its (first/sole) constructor — used to adapt a static-String arg
    // flowing into a CharSequence ctor param of a SPLICED anonymous stdlib object (`dotkt$obj*`, e.g. the anonymous
    // Grouping from `CharSequence.groupingBy` whose ctor captures the receiver as `kotlin.CharSequence`). The spliced
    // The referenced declaration remains the authority for whether a slot is CharSequence; `new.argTypes` is the
    // substituted use-site vector and cannot replace that declaration fact.
    readonly Dictionary<string, TypeNode[]> _ownerCtorParams = new(StringComparer.Ordinal);
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
    // The semantic owner index above deliberately omits CLR arity punctuation. Legal metadata can therefore make it
    // ambiguous (Outer`1+Leaf`1 and Outer+Leaf`2). Current-format ClrExternal tokens carry the exact TypeDef identity;
    // keep a separate physical index so those facts never fall back through the ambiguous semantic spelling.
    readonly Dictionary<string, List<MemberBinding>> _membersByPhysicalOwner = new(StringComparer.Ordinal);
    // Exact ECMA MethodImpl declarations keyed by their compiler-authored body. The frontend says which inherited
    // Kotlin default implementation was selected; this index contributes only the referenced DLL's physical slot
    // allocation for a trusted accessor bridge. No hierarchy/default-body inference is performed here.
    readonly Dictionary<(string Owner, int BodyToken), List<MethodImplBinding>> _methodImplsByBody = new();
    readonly Dictionary<string, MemberBinding> _declarationById = new(StringComparer.Ordinal);
    readonly Dictionary<(string Owner, string SourceName, int MethodArity, bool IsStatic, int ParamCount), int>
        _declarationFamilyCounts = new();

    static (string Owner, string SourceName, int MethodArity, bool IsStatic, int ParamCount)
        DeclarationFamilyOf(MemberBinding binding) =>
        (binding.Owner, binding.DeclarationSourceName, binding.MethodArity, binding.IsStatic, binding.ParamCount);

    public bool TryDeclarationIdentity(
        string id,
        out string physicalName,
        out string owner,
        out string intrinsic,
        out int[] byrefPositions)
    {
        physicalName = null;
        owner = null;
        intrinsic = null;
        byrefPositions = null;
        if (id == null || !_declarationById.TryGetValue(id, out var binding)) return false;
        physicalName = binding.Name;
        owner = binding.DeclarationPhysicalOwner ?? binding.Owner;
        intrinsic = binding.Intrinsic;
        byrefPositions = binding.ByrefPositions;
        return true;
    }

    // UnsafeAccessorAttribute is matched by the CLR against the selected MethodDef's exact physical signature.
    // The frontend-authored declaration identity selects that MethodDef; inherited-owner binding supplies the
    // declaring TypeDef. Expose those already-indexed facts without re-resolving an overload from the use-site
    // signature, whose concrete Kotlin projection may deliberately differ from the physical nullable-TV-erased ABI.
    public bool TryUnsafeAccessorMethod(
        string id,
        string ownerFqn,
        int methodArity,
        bool isStatic,
        out ReferencedUnsafeAccessorMethod declaration)
    {
        declaration = null;
        if (id == null || !_declarationById.TryGetValue(id, out var binding)) return false;
        var physicalOwner = binding.DeclarationPhysicalOwner ?? binding.Owner;
        if (!string.Equals(BareOwnerFqn(physicalOwner), BareOwnerFqn(ownerFqn), StringComparison.Ordinal)
            || binding.MethodArity != methodArity || binding.IsStatic != isStatic
            || binding.ParamTypeNodes == null || binding.ReturnTypeNode == null)
            return false;
        declaration = new ReferencedUnsafeAccessorMethod(
            binding.Name,
            binding.ParamTypeNodes,
            binding.ReturnTypeNode,
            binding.MethodTypeParams,
            binding.NullableGenericRet);
        return true;
    }

    // Class-virtual Kotlin declarations intentionally have no scalar declaration identity: their physical name is
    // shared by an override family rather than allocated per declaration. In that current-format case, select from
    // the producing assembly's semantic declaration carriers using the complete frontend-selected signature. This is
    // the same exact semantic selection used for referenced override slots, and refuses an ambiguous overload set.
    public bool TryUnsafeAccessorVirtualMethod(
        string ownerFqn,
        string sourceMember,
        int methodArity,
        bool isStatic,
        IReadOnlyList<TypeNode> signature,
        TypeNode resolvedReturn,
        TypeNode[] ownerTypeArguments,
        JsonArray selectedTypeParams,
        out ReferencedUnsafeAccessorMethod declaration)
    {
        declaration = null;
        if (!TryMembersByBirOwner(ownerFqn, out var members)) return false;
        var matches = members.Where(member => member.SourcePropertyName == null
                && member.IsStatic == isStatic
                && (member.DeclarationSourceName ?? member.SourceMethodName ?? member.Name) == sourceMember
                && member.MethodArity == methodArity
                && KotlinOverrideSlotBridge.SameMethodTypeParameterShape(
                    member.MethodTypeParams, selectedTypeParams, ownerTypeArguments, ownerTypeArguments)
                && MethodSignatureMatches(member, signature, resolvedReturn, ownerTypeArguments)
                && member.ParamTypeNodes != null && member.ReturnTypeNode != null)
            .ToList();
        if (matches.Count != 1) return false;
        var match = matches[0];
        declaration = new ReferencedUnsafeAccessorMethod(
            match.Name,
            match.ParamTypeNodes,
            match.ReturnTypeNode,
            match.MethodTypeParams,
            match.NullableGenericRet);
        return true;
    }

    // Resolve the exact MethodDef named by the frontend-selected Kotlin declaration identity. The identity selects;
    // the semantic call signature only validates that the selected declaration still has the physical ABI this use
    // expects after every representation pass. A failed validation is not permission to search sibling overloads.
    public bool TryDeclarationIdentityMethod(
        string id,
        int methodArity,
        bool isStatic,
        IReadOnlyList<TypeNode> callSignature,
        out TypeNode[] declarationSignature,
        out MethodInfo declaration,
        out Type declaringOwner,
        out string failure)
    {
        declarationSignature = null;
        declaration = null;
        declaringOwner = null;
        failure = null;
        if (id == null || !_declarationById.TryGetValue(id, out var binding))
        {
            failure = "has no trusted physical binding";
            return false;
        }
        var nullableWitnessCount = binding.NullableWitnessTypeParameterIndices?.Length ?? 0;
        var missingNullableWitnesses = binding.ParamCount - callSignature.Count;
        var completesWithNullableWitnesses = missingNullableWitnesses == nullableWitnessCount
            && missingNullableWitnesses > 0;
        if (binding.MethodArity != methodArity || binding.IsStatic != isStatic
            || (binding.ParamCount != callSignature.Count && !completesWithNullableWitnesses))
        {
            failure = $"selects {binding.Owner}.{binding.Name}`{binding.MethodArity} "
                + $"({(binding.IsStatic ? "static" : "instance")}, {binding.ParamCount} parameter(s)), but the call "
                + $"states arity {methodArity}, {(isStatic ? "static" : "instance")}, "
                + $"{callSignature.Count} parameter(s)";
            return false;
        }
        var completedCallSignature = completesWithNullableWitnesses
            ? callSignature.Concat(Enumerable.Repeat<TypeNode>(new TypeNode.Fqn("kotlin.Boolean"),
                missingNullableWitnesses)).ToArray()
            : callSignature.ToArray();
        var physicalOwner = binding.DeclarationPhysicalOwner ?? binding.Owner;
        var ownerArity = _ownerArity.TryGetValue(BareOwnerFqn(binding.Owner), out var arity) ? arity : 0;
        var owner = ResolveRefType(physicalOwner, ownerArity)
            ?? ResolveRefType(binding.Owner, ownerArity)
            ?? PhysicalTypeNamed(physicalOwner, ownerArity);
        if (owner == null)
        {
            failure = $"selects physical owner '{physicalOwner}', which is absent from the compile-reference universe";
            return false;
        }
        if (owner.IsConstructedGenericType && !owner.IsGenericTypeDefinition)
            owner = owner.GetGenericTypeDefinition();

        List<MethodInfo> matches;
        try
        {
            matches = owner.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static
                    | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(method => method.MetadataToken == binding.MetadataToken)
                .ToList();
        }
        catch (Exception ex)
        {
            failure = $"cannot inspect selected physical owner '{physicalOwner}': {ex.GetType().Name}: {ex.Message}";
            return false;
        }
        if (matches.Count != 1)
        {
            failure = $"selects metadata token 0x{binding.MetadataToken:x8} on '{physicalOwner}', but that token "
                + $"resolved to {matches.Count} MethodDef(s)";
            return false;
        }

        var selected = matches[0];
        var selectedSignature = selected.GetParameters()
            .Select(parameter => DeclarationTypeNode(parameter.ParameterType)).ToArray();
        // A scalar identity already resolves a sole declaration completely. Parameter-shape validation is needed
        // only where a same-source-name declaration family actually has a sibling of the same callable shape; that
        // is the boundary at which falling back to structural overload selection could bind the wrong MethodDef.
        // Requiring semantic reconstruction for every sole declaration would reject intentional physical erasures
        // such as a suspend-function value represented by object.
        var requiresParameterValidation = _declarationFamilyCounts.TryGetValue(
            DeclarationFamilyOf(binding), out var familyCount) && familyCount > 1;
        var physicalMatches = selectedSignature.All(type => type != null)
            && selectedSignature.Select((type, index) =>
                DeclarationDescribesCall(type, completedCallSignature[index])).All(matchesCall => matchesCall);
        // Reified declarations may have compiler-owned physical parameters that are intentionally absent from the
        // Kotlin semantic signature. Such a carrier can still identify the declaration, but it is not a complete
        // validator for this physical call shape.
        var semanticMatches = binding.DeclarationSemanticParams?.Length == callSignature.Count
            && binding.DeclarationSemanticParams.Select((type, index) =>
                SemanticDeclarationDescribesCall(type, callSignature[index])).All(matchesCall => matchesCall);
        var signatureMatches = !requiresParameterValidation || semanticMatches || physicalMatches;
        if (selectedSignature.Any(type => type == null) || !signatureMatches)
        {
            var semanticText = binding.DeclarationSemanticParams == null ? "absent"
                : $"({string.Join(",", binding.DeclarationSemanticParams.Select(TypeNode.ToJson))})";
            failure = $"selects '{physicalOwner}.{selected.Name}' but neither its semantic parameter signature "
                + $"{semanticText} nor its physical parameter signature "
                + $"({string.Join(",", selectedSignature.Select(type => type == null ? "<unresolved>" : TypeNode.ToJson(type)))}) "
                + $"validates the call signature ({string.Join(",", callSignature.Select(TypeNode.ToJson))})";
            return false;
        }

        declarationSignature = selectedSignature;
        declaration = selected;
        declaringOwner = owner;
        return true;
    }
    public int[] NullableWitnessTypeParameterIndices(string id) =>
        id != null && _declarationById.TryGetValue(id, out var binding)
            ? binding.NullableWitnessTypeParameterIndices
            : null;
    public bool TryDeclarationFactory(
        string id,
        out string collectionKind,
        out string arrayKind,
        out string arrayElementHint)
    {
        collectionKind = null;
        arrayKind = null;
        arrayElementHint = null;
        if (id == null || !_declarationById.TryGetValue(id, out var binding)) return false;
        collectionKind = binding.CollectionFactoryKind;
        arrayKind = binding.ArrayFactoryKind;
        arrayElementHint = binding.ArrayFactoryElementHint;
        return collectionKind != null || arrayKind != null;
    }
    // ownerFqn -> declared parameter count -> the ctor declarations of that arity (#86 D1). A list, because a
    // same-arity overload set must be REFUSED rather than resolved by arity alone.
    readonly Dictionary<string, Dictionary<int, List<CtorBinding>>> _ctorsByOwner = new(StringComparer.Ordinal);
    readonly Dictionary<string, List<AliasConstructorAdapter>> _aliasConstructorAdaptersByOwner =
        new(StringComparer.Ordinal);

    public bool TryAliasConstructorAdapter(
        string owner, TypeNode[] signature, TypeNode[] ownerArgs, out AliasConstructorAdapter adapter)
    {
        adapter = null;
        owner = BareOwnerFqn(owner);
        if (!_aliasConstructorAdaptersByOwner.TryGetValue(owner, out var declared)) return false;
        var matches = declared
            .Select(candidate => AliasConstructorDelegationExpansion.Specialize(candidate, ownerArgs))
            .Where(candidate => SameTypeSequence(candidate.Signature, signature))
            .GroupBy(AliasAdapterKey, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();
        if (matches.Length > 1)
            throw new InvalidOperationException(
                $"conflicting constructor-adapter metadata for alias '{owner}' signature "
                + $"({string.Join(", ", signature.Select(TypeNode.ToJson))})");
        if (matches.Length == 0) return false;
        adapter = matches[0];
        return true;
    }

    public string CollectionCopyConstructorKind(
        string owner, TypeNode[] signature, TypeNode[] ownerArgs)
    {
        return TryAliasConstructorAdapter(owner, signature, ownerArgs, out var adapter)
            ? adapter.CollectionFactoryKind
            : null;
    }

    static bool SameTypeSequence(IReadOnlyList<TypeNode> left, IReadOnlyList<TypeNode> right)
    {
        if (left.Count != right.Count) return false;
        for (var i = 0; i < left.Count; i++) if (left[i] != right[i]) return false;
        return true;
    }

    static string AliasAdapterKey(AliasConstructorAdapter adapter) => new JsonObject
    {
        ["parameters"] = new JsonArray(adapter.Parameters
            .Select(parameter => (JsonNode)JsonValue.Create(parameter)).ToArray()),
        ["signature"] = new JsonArray(adapter.Signature.Select(TypeJson.Write).ToArray()),
        ["statements"] = adapter.Statements.DeepClone(),
        ["arguments"] = adapter.Arguments.DeepClone(),
        ["terminalSignature"] = new JsonArray(adapter.TerminalSignature.Select(TypeJson.Write).ToArray()),
        ["collectionFactoryKind"] = adapter.CollectionFactoryKind,
    }.ToJsonString();
    // Reference-owner hierarchy in BIR's dotted Kotlin vocabulary.  Calls retain their Kotlin
    // receiver owner in BIR; inherited CLR MemberRefs are selected later by bir2cir, so that pass
    // needs the same constructed base/interface graph for referenced types as it has for local CIR
    // declarations.  Keep the graph as structured TypeNodes -- never reconstruct generic owners in
    // ilemit from reflection strings.
    readonly Dictionary<OwnerTypeIdentity, ReferenceTypeShape> _referenceTypeShapes = new();
    readonly Dictionary<string, ReferenceTypeShape> _referenceTypeShapesByPhysicalOwner =
        new(StringComparer.Ordinal);
    // Source/KLIB vocabulary flattens CLR nesting to dots and drops per-TypeDef arity suffixes. Generated CLR
    // signatures must recover the exact metadata identity here in bir2cir; ilemit must not infer whether a generic
    // argument belongs to an outer or inner TypeDef from the flattened total arity.
    readonly Dictionary<OwnerTypeIdentity, string> _exactPhysicalTypeByDottedName = new();
    readonly Dictionary<string, string> _physicalTypeBySemanticName = new(StringComparer.Ordinal);
    readonly Dictionary<string, int> _innerCapturedCount = new(StringComparer.Ordinal);
    readonly Dictionary<string, string> _innerSemanticOwner = new(StringComparer.Ordinal);
    readonly Dictionary<string, string> _topLevelIntrinsics = new(StringComparer.Ordinal); // top-level fun name -> FQ static
    readonly Dictionary<(string Name, SignatureKey Signature), string> _topLevelIntrinsicsBySig = new();
    readonly HashSet<string> _ambiguousTopLevelIntrinsics = new(StringComparer.Ordinal); // names whose overloads bind to DIFFERENT statics (Math vs MathF)
    readonly Dictionary<string, int[]> _topLevelIntrinsicByref = new(StringComparer.Ordinal); // top-level fun name -> byref param positions
    readonly Dictionary<(string Name, SignatureKey Signature), string> _extMemberIntrinsics = new();
    readonly Dictionary<string, (string Getter, string Conv)> _inlineBacking = new(StringComparer.Ordinal);
    readonly Dictionary<string, List<(string Owner, string RecvKey, TypeKey ParamKey)>> _topLevelStatics = new(StringComparer.Ordinal);
    readonly Dictionary<string, string> _collectionFactories = new(StringComparer.Ordinal); // @ClrCollectionFactory fun name -> "list"/"set"/"map"
    readonly Dictionary<string, string> _arrayFactories = new(StringComparer.Ordinal);       // @ClrArrayFactory fun name -> "vararg"/"sized"
    readonly Dictionary<string, string> _arrayFactoryElemHints = new(StringComparer.Ordinal);// array factory name -> concrete elem FQN (empty-call fallback)
    readonly Dictionary<DefaultKey, Dictionary<int, string>> _kotlinDefaults = new();
    readonly Dictionary<string, Dictionary<int, string>> _kotlinDefaultsByDeclarationId = new(StringComparer.Ordinal);
    // #146: OWNERLESS default-arg index "name|paramCount" -> defaults. DefaultArgSplice now runs at PHASE 1 (before
    // MemberCallSubstitution attributes the owner), so the omitted call is still `owner:null method:col2 sig:[…]`.
    // Built from _kotlinDefaults: a key with a SINGLE owner, or several owners whose defaults AGREE, maps to those
    // defaults; owners that DISAGREE mark it AMBIGUOUS (the splice loud-refuses rather than guess).
    readonly Dictionary<string, Dictionary<int, string>> _kotlinDefaultsOwnerless = new(StringComparer.Ordinal);
    readonly HashSet<string> _kotlinDefaultsAmbiguous = new(StringComparer.Ordinal);
    // OWNERFUL keys two same-arity declarations carry with different defaults (see ReferenceAssemblyMetadata).
    readonly HashSet<DefaultKey> _kotlinDefaultsConflicted = new();
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
    readonly Dictionary<string, string> _inlinePayloadByDeclarationId = new(StringComparer.Ordinal);

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
    readonly Dictionary<string, (bool Found, TypeNode Type, JsonNode Value)> _literalFieldCache =
        new(StringComparer.Ordinal);
    readonly Dictionary<string, bool> _volatileFieldCache = new(StringComparer.Ordinal);
    bool _netInit;

    // The bare FQNs of every type DECLARED in THIS compilation (this run's BIR `types`). A local declaration is the
    // AUTHORITY for its identity — it wins over a referenced .NET/Kotlin dll that exports the SAME FQN (the #15
    // pathological layout: an app whose `**/*.kt` glob pulls in a nested ProjectReference lib's SOURCE — so it compiles
    // `demo.Plain` locally — AND references that lib's dll, which also exports `demo.Plain`). This mirrors the frontend
    // "source wins" fix: ResolveNetType refuses to bind a locally-emitted FQN to the ref, so every sibling resolution
    // routes to the this-assembly-emitted type — `new` stays a local `new` (not `newClr`), and a
    // callInstance/callStatic/field/boundDelegate stays owner-local (NetInteropBinding leaves it for the emitted-type
    // path) instead of reshaping to a `clr*` node against the ref. Set once by the Driver before the transform loop.
    // SCOPE: this filters both reflection-backed resolver axes. The ref.dll's DotKt sidecar indexes (TypeKinds/IsValueType,
    // owner arity, ctor param types) are NOT filtered by this set — in the #15 layout they are populated from the SAME
    // source that produced the local decl, so they agree; a genuinely divergent stale-dll is out of scope (source-wins
    // is still the right precedence there, matching Roslyn CS0436). `@ClrTypeAlias`/`@ClrIntrinsic` maps are empty for a
    // user lib, so TryResolveClrOwner never mis-binds a local user type.
    IReadOnlySet<string> _localEmittedTypes = new HashSet<string>(StringComparer.Ordinal);
    public void SetLocalEmittedTypes(IReadOnlySet<string> fqns) => _localEmittedTypes = fqns
        .Select(fqn => DottedFqn(BareOwnerFqn(fqn)))
        .ToHashSet(StringComparer.Ordinal);

    bool IsLocalEmittedType(string fqn) =>
        _localEmittedTypes.Contains(DottedFqn(BareOwnerFqn(fqn)));

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
            foreach (var kv in asm.DotKt.Aliases)
            {
                _ownerAlias[kv.Key] = kv.Value;
                _ownerAliasKind[kv.Key] = asm.DotKt.AliasKinds.GetValueOrDefault(kv.Key, "class");
            }
            foreach (var kv in asm.DotKt.TypeKinds) _ownerKind[kv.Key] = kv.Value;
            foreach (var kv in asm.DotKt.PhysicalTypeKinds)
                _ownerKindByPhysicalOwner[kv.Key] = kv.Value;
            foreach (var owner in asm.DotKt.ByRefLikeOwners) _byRefLikeOwners.Add(owner);
            foreach (var owner in asm.DotKt.ByRefLikePhysicalOwners)
                _byRefLikePhysicalOwners.Add(owner);
            foreach (var owner in asm.DotKt.DotKtOwners)
                _dotKtOwners.Add(StripGenericArity(DottedFqn(owner)));
            foreach (var kv in asm.DotKt.RichEnums)
            {
                var owner = StripGenericArity(DottedFqn(kv.Key));
                if (!_richEnums.TryAdd(owner, kv.Value))
                {
                    var existing = _richEnums[owner];
                    if (existing.Values != kv.Value.Values || existing.ValueOf != kv.Value.ValueOf ||
                        existing.EntryFields.Count != kv.Value.EntryFields.Count ||
                        existing.EntryFields.Any(entry =>
                            !kv.Value.EntryFields.TryGetValue(entry.Key, out var field) || field != entry.Value))
                        throw new InvalidOperationException($"conflicting Kotlin rich-enum metadata for '{owner}'");
                }
            }
            foreach (var kv in asm.DotKt.BasicEnums)
            {
                var owner = StripGenericArity(DottedFqn(kv.Key));
                if (!_basicEnums.TryAdd(owner, kv.Value))
                {
                    var existing = _basicEnums[owner];
                    if (existing.Underlying != kv.Value.Underlying ||
                        !existing.Entries.SequenceEqual(kv.Value.Entries))
                        throw new InvalidOperationException($"conflicting Kotlin basic-enum metadata for '{owner}'");
                }
            }
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
            foreach (var owner in asm.DotKt.FileClassOwners)
                _fileClassOwners.Add(StripGenericArity(DottedFqn(owner)));
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
            foreach (var kv in asm.DotKt.CompanionExtensionMembers)
            {
                if (!_companionExtensionMembers.TryAdd(kv.Key, kv.Value) &&
                    _companionExtensionMembers[kv.Key] != kv.Value)
                    _companionExtensionMembers[kv.Key] = "";
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
            foreach (var kv in asm.DotKt.GenericStaticCarrierBySemanticOwner)
            {
                var semantic = StripGenericArity(DottedFqn(kv.Key));
                if (!_genericStaticCarrierBySemanticOwner.TryAdd(semantic, kv.Value))
                    throw new InvalidOperationException($"conflicting generic-static carrier for '{semantic}'");
                _genericStaticCarriers.Add(StripGenericArity(DottedFqn(kv.Value)));
            }
            foreach (var kv in asm.DotKt.TypeArity) _ownerArity[kv.Key] = kv.Value;
            foreach (var kv in asm.DotKt.TypeParamNames) _ownerTypeParams[kv.Key] = kv.Value;
            foreach (var kv in asm.DotKt.TypeParamDeclarations) _ownerTypeParamDeclarations[kv.Key] = kv.Value;
            foreach (var owner in asm.DotKt.PublicParameterlessConstructibleOwners)
                _publicParameterlessConstructibleOwners.Add(owner);
            foreach (var owner in asm.DotKt.PublicParameterlessConstructiblePhysicalOwners)
                _publicParameterlessConstructiblePhysicalOwners.Add(owner);
            foreach (var kv in asm.DotKt.CtorParamTypes) _ownerCtorParams[kv.Key] = kv.Value;
            foreach (var kv in asm.DotKt.TypeParamConstraints) _ownerTypeParamConstraints[kv.Key] = kv.Value;
            foreach (var kv in asm.DotKt.TypeParamBounds) _ownerTypeParamBounds[kv.Key] = kv.Value;
            foreach (var h in asm.DotKt.HelperTypes) _helperTypes.Add(h);
            foreach (var s in asm.DotKt.RestrictsSuspensionTypes) _restrictsSuspension.Add(s);
            foreach (var m in asm.DotKt.MemberBindings)
            {
                if (m.DeclarationId is string declarationId)
                {
                    if (!_declarationById.TryAdd(declarationId, m))
                    {
                        var prior = _declarationById[declarationId];
                        // The same Kotlin module can be present through runtime/reference twins or repeated project
                        // references. Identity-derived physical names are identical; accepting that exact duplicate
                        // does not select between overloads or infer source meaning.
                        if (SameDeclarationBinding(prior, m))
                            continue;
                        throw new InvalidOperationException(
                            $"conflicting Kotlin declaration identity '{declarationId}': "
                            + $"'{prior.Owner}.{prior.Name}' and '{m.Owner}.{m.Name}'");
                    }
                    var family = DeclarationFamilyOf(m);
                    _declarationFamilyCounts[family] = _declarationFamilyCounts.TryGetValue(family, out var count)
                        ? count + 1 : 1;
                }
                if (!_membersByOwner.TryGetValue(m.Owner, out var list))
                    _membersByOwner[m.Owner] = list = new List<MemberBinding>();
                list.Add(m);
                if (m.DeclarationPhysicalOwner is string physicalOwner)
                {
                    if (!_membersByPhysicalOwner.TryGetValue(physicalOwner, out var physicalList))
                        _membersByPhysicalOwner[physicalOwner] = physicalList = new List<MemberBinding>();
                    physicalList.Add(m);
                }
            }
            foreach (var implementation in asm.DotKt.MethodImplBindings)
            {
                var key = (StripGenericArity(DottedFqn(implementation.BodyOwner)), implementation.BodyToken);
                if (!_methodImplsByBody.TryGetValue(key, out var implementations))
                    _methodImplsByBody[key] = implementations = new List<MethodImplBinding>();
                implementations.Add(implementation);
            }
            foreach (var c in asm.DotKt.CtorBindings)
            {
                AddCtor(c.Owner, c);
                if (c.PhysicalOwner != c.Owner) AddCtor(c.PhysicalOwner, c);

                void AddCtor(string owner, CtorBinding binding)
                {
                    if (!_ctorsByOwner.TryGetValue(owner, out var byArity))
                        _ctorsByOwner[owner] = byArity = new Dictionary<int, List<CtorBinding>>();
                    if (!byArity.TryGetValue(binding.ParamCount, out var ctors))
                        byArity[binding.ParamCount] = ctors = new List<CtorBinding>();
                    ctors.Add(binding);
                }
            }
            foreach (var adapter in asm.DotKt.AliasConstructorAdapters)
            {
                var owner = BareOwnerFqn(adapter.Owner);
                if (!_aliasConstructorAdaptersByOwner.TryGetValue(owner, out var adapters))
                    _aliasConstructorAdaptersByOwner[owner] = adapters = new List<AliasConstructorAdapter>();
                adapters.Add(adapter.Adapter);
            }
            foreach (var kv in asm.DotKt.TypeShapes) _referenceTypeShapes.TryAdd(kv.Key, kv.Value);
            foreach (var kv in asm.DotKt.PhysicalTypeShapes)
                _referenceTypeShapesByPhysicalOwner.TryAdd(kv.Key, kv.Value);
            foreach (var kv in asm.DotKt.ExactPhysicalTypeByDottedName)
                AddExactPhysicalTypeName(_exactPhysicalTypeByDottedName, kv.Key, kv.Value);
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
                    _topLevelStatics[kv.Key] = lst = new List<(string, string, TypeKey)>();
                lst.AddRange(kv.Value);
            }
            foreach (var key in asm.DotKt.KotlinDefaultsConflicted) _kotlinDefaultsConflicted.Add(key);
            foreach (var kv in asm.DotKt.KotlinDefaults)
            {
                _kotlinDefaults.TryAdd(kv.Key, kv.Value);
                // Only an arity key folds: a signature-keyed entry does not, and a CONSTRUCTOR is never called
                // ownerlessly (a `new` always names its type), so folding `.ctor|pc` would only make every type of the
                // same ctor arity collide with every other.
                if (kv.Key.Signature != null || kv.Key.Method == CtorKeyName) continue;
                var np = kv.Key.Method + "|" + kv.Key.ParamCount;
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
            foreach (var kv in asm.DotKt.KotlinDefaultsByDeclarationId)
                if (!_kotlinDefaultsByDeclarationId.TryAdd(kv.Key, kv.Value)
                    && !SameDefaults(_kotlinDefaultsByDeclarationId[kv.Key], kv.Value))
                    throw new InvalidOperationException($"conflicting defaults for Kotlin declaration identity '{kv.Key}'");
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
            foreach (var kv in asm.DotKt.InlinePayloadsByDeclarationId)
                if (!_inlinePayloadByDeclarationId.TryAdd(kv.Key, kv.Value)
                    && _inlinePayloadByDeclarationId[kv.Key] != kv.Value)
                    throw new InvalidOperationException($"conflicting inline declaration identity '{kv.Key}'");
        }
        // A flattened Kotlin identity cannot distinguish where nested CLR generic slots are owned. Current-format
        // external classifiers carry the exact TypeDef spelling, so exact physical indexes remain authoritative;
        // the semantic fallback must abstain instead of exposing the last scanned sibling.
        foreach (var ambiguous in _exactPhysicalTypeByDottedName
                     .Where(entry => entry.Value == null).Select(entry => entry.Key).ToArray())
        {
            _ownerKind.Remove(ambiguous);
            _byRefLikeOwners.Remove(ambiguous);
            _publicParameterlessConstructibleOwners.Remove(ambiguous);
            _referenceTypeShapes.Remove(ambiguous);
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

    public JsonObject InlineByDeclarationIdentity(string id)
    {
        if (id == null || !_inlinePayloadByDeclarationId.TryGetValue(id, out var json)) return null;
        return JsonNode.Parse(json) as JsonObject
            ?? throw new InvalidDataException($"malformed inline payload for declaration identity '{id}'");
    }

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
    public Dictionary<int, string> KotlinDefaultsFor(string owner, string method, int paramCount,
        SignatureKey sigKey = null, SignatureKey relaxedSigKey = null)
    {
        if (method == null) return null;
        if (owner != null)
        {
            var key = new DefaultKey(owner, method, paramCount);
            // A call carries its callee's declared parameter vector (`sig`/`shapeTypes`, or a `new`'s `argTypes`), so try
            // the SIGNATURE key first — that is what tells same-arity overloads apart. Exact first, then with class
            // positions collapsed (the call's Kotlin spelling and the reference's CLR spelling only compare there), then
            // the arity key, which refuses when two declarations carry it with different defaults.
            if (sigKey != null)
            {
                var exactKey = new DefaultKey(owner, method, paramCount, sigKey);
                if (_kotlinDefaults.TryGetValue(exactKey, out var bySig)
                    && !_kotlinDefaultsConflicted.Contains(exactKey)) return bySig;
                var relaxed = new DefaultKey(owner, method, paramCount, relaxedSigKey, Relaxed: true);
                if (_kotlinDefaults.TryGetValue(relaxed, out var byRelaxed) && !_kotlinDefaultsConflicted.Contains(relaxed))
                    return byRelaxed;
            }
            if (_kotlinDefaultsConflicted.Contains(key)) return null;
            return _kotlinDefaults.TryGetValue(key, out var exact) ? exact : null;
        }
        return _kotlinDefaultsOwnerless.TryGetValue(method + "|" + paramCount, out var ownerless) ? ownerless : null;
    }

    public Dictionary<int, string> KotlinDefaultsForDeclarationIdentity(string id) =>
        id != null ? _kotlinDefaultsByDeclarationId.GetValueOrDefault(id) : null;

    // Resolve the declaration exactly as the later scalar-member binding does, then read optional/default metadata
    // from that MethodDef. A call through a derived receiver names the derived type but can select a base declaration;
    // looking up defaults by the call owner would either miss that declaration or, when the derived type declares a
    // same-name/same-arity overload, attach the sibling overload's value. `true` means a declaration was selected even
    // when it has no representable defaults, so callers must not fall through to a different declaration search.
    public bool TryKotlinDefaultsForSelectedMethod(
        TypeNode.Fqn owner, string method, int methodArity, bool isStatic, IReadOnlyList<TypeNode> callSignature,
        out Dictionary<int, string> defaults, out string[] parameterNames)
    {
        defaults = null;
        parameterNames = null;
        if (!ClrMemberResolution.TryResolveExternalMethodForDefaults(
                this, owner, method, methodArity, isStatic, callSignature, out var declaration))
            return false;
        parameterNames = declaration.GetParameters()
            .Select(parameter => string.IsNullOrEmpty(parameter.Name) ? $"arg{parameter.Position}" : parameter.Name)
            .ToArray();
        defaults = CallableDefaultsOf(declaration, _netMlc);
        return true;
    }

    // A dll2klib surface declaration can be inherited from a public interface even though the CLR class satisfies
    // that slot only through a private explicit MethodImpl body. The call then names the CLASS (Kotlin's inherited
    // member owner), while the authoritative optional/default metadata lives on the interface MethodDef that
    // ClrMemberResolution will later select as the memberRef. Resolve that same fallback here, before CIR member
    // resolution, so DefaultArgSplice can materialise the complete physical argument vector.
    //
    // An accessible class member wins exactly as it does in ClrMemberResolution.Candidates: interface declarations
    // are consulted only when the class exposes no applicable public/protected member. Multiple applicable interface
    // declarations must agree on their defaults; the BIR owner alone cannot distinguish disagreeing slots, so guessing
    // one would attach source semantics from an arbitrary reflection order.
    public Dictionary<int, string> KotlinDefaultsForImplementedInterface(
        string owner, int ownerArity, string method, int paramCount, SignatureKey sigKey, SignatureKey relaxedSigKey)
    {
        if (owner == null || method == null || sigKey == null) return null;
        var open = ResolveRefType(owner, ownerArity);
        if (open == null) return null;

        bool SignatureMatches(MethodInfo candidate)
        {
            if (candidate.Name != method || candidate.GetParameters().Length != paramCount) return false;
            var candidateKey = SigKeyOf(candidate.GetParameters());
            return candidateKey.Equals(sigKey) || SigKeyOf(candidate.GetParameters(), relaxed: true).Equals(relaxedSigKey);
        }

        const BindingFlags ownFlags = BindingFlags.Public | BindingFlags.NonPublic |
                                      BindingFlags.Instance | BindingFlags.FlattenHierarchy;
        MethodInfo[] own;
        try
        {
            own = open.GetMethods(ownFlags)
                .Where(candidate =>
                    (candidate.IsPublic || candidate.IsFamily || candidate.IsFamilyOrAssembly) &&
                    SignatureMatches(candidate))
                .ToArray();
        }
        catch { return null; }
        if (own.Length != 0) return null;

        MethodInfo[] interfaces;
        try
        {
            interfaces = open.GetInterfaces()
                .SelectMany(iface => iface.GetMethods(BindingFlags.Public | BindingFlags.Instance))
                .Where(SignatureMatches)
                .GroupBy(candidate => (candidate.Module, candidate.MetadataToken))
                .Select(group => group.First())
                .ToArray();
        }
        catch { return null; }
        if (interfaces.Length == 0) return null;

        Dictionary<int, string> selected = null;
        foreach (var candidate in interfaces)
        {
            var defaults = CallableDefaultsOf(candidate, _netMlc);
            if (selected == null)
            {
                selected = defaults;
                continue;
            }
            if (defaults == null || !SameDefaults(selected, defaults))
                throw new InvalidOperationException(
                    $"bir2cir: cannot fill an omitted default argument of '{owner}.{method}' (arity {paramCount}) — " +
                    "several implemented interface declarations match that class surface but their defaults disagree; " +
                    "call through a specific interface or pass the argument explicitly");
        }
        if (selected != null && interfaces.Any(candidate => CallableDefaultsOf(candidate, _netMlc) == null))
            throw new InvalidOperationException(
                $"bir2cir: cannot fill an omitted default argument of '{owner}.{method}' (arity {paramCount}) — " +
                "several implemented interface declarations match that class surface but do not all declare the " +
                "same defaults; call through a specific interface or pass the argument explicitly");
        return selected;
    }

    // True when the name+arity cannot identify ONE set of defaults: a genuinely ownerless name carried by >1 owner that
    // disagree, or an OWNERFUL key two same-arity declarations (ctor overloads) carry with different defaults.
    public bool KotlinDefaultsAmbiguous(string owner, string method, int paramCount) =>
        method != null && (owner == null
            ? _kotlinDefaultsAmbiguous.Contains(method + "|" + paramCount)
            : _kotlinDefaultsConflicted.Contains(new DefaultKey(owner, method, paramCount)));

    static bool SameDefaults(Dictionary<int, string> a, Dictionary<int, string> b)
    {
        if (a.Count != b.Count) return false;
        foreach (var kv in a) if (!b.TryGetValue(kv.Key, out var v) || v != kv.Value) return false;
        return true;
    }

    static bool SameDeclarationBinding(MemberBinding a, MemberBinding b) =>
        a.Owner == b.Owner && a.Name == b.Name && a.ParamCount == b.ParamCount
        && a.Intrinsic == b.Intrinsic && a.IsAbstract == b.IsAbstract && a.IsStatic == b.IsStatic
        && a.PropertyAccess == b.PropertyAccess
        && a.PropertyName == b.PropertyName && Same(a.ByrefPositions, b.ByrefPositions)
        && a.Suspend == b.Suspend && a.Conv == b.Conv && a.ConvTo == b.ConvTo
        && Same(a.ReturnType, b.ReturnType) && a.MethodArity == b.MethodArity
        && Same(a.ParamTypeNodes, b.ParamTypeNodes) && a.IsVirtual == b.IsVirtual
        && Same(a.KotlinReturnType, b.KotlinReturnType) && Same(a.NullableGenericRet, b.NullableGenericRet)
        && Same(a.NullableGenericParams, b.NullableGenericParams) && Same(a.ReturnTypeNode, b.ReturnTypeNode)
        && a.MetadataToken == b.MetadataToken && a.SourcePropertyName == b.SourcePropertyName
        && a.AccessorKind == b.AccessorKind && a.AssociatedPropertyName == b.AssociatedPropertyName
        && a.IsPropertyBridge == b.IsPropertyBridge && a.DeclarationId == b.DeclarationId
        && a.DeclarationSourceName == b.DeclarationSourceName
        && a.DeclarationPhysicalOwner == b.DeclarationPhysicalOwner
        && Same(a.DeclarationSemanticParams, b.DeclarationSemanticParams)
        && Same(a.DeclarationSemanticReturn, b.DeclarationSemanticReturn)
        && a.CollectionFactoryKind == b.CollectionFactoryKind && a.ArrayFactoryKind == b.ArrayFactoryKind
        && a.ArrayFactoryElementHint == b.ArrayFactoryElementHint
        && Same(a.SemanticReifiedTypeParameterIndices, b.SemanticReifiedTypeParameterIndices)
        && Same(a.NullableWitnessTypeParameterIndices, b.NullableWitnessTypeParameterIndices);

    static bool Same<T>(T[] a, T[] b) where T : IEquatable<T> =>
        ReferenceEquals(a, b) || a != null && b != null && a.SequenceEqual(b);

    static bool Same(TypeNode a, TypeNode b) =>
        ReferenceEquals(a, b) || a != null && b != null
        && TypeNode.ToJson(a) == TypeNode.ToJson(b);

    static bool Same(TypeNode[] a, TypeNode[] b) =>
        ReferenceEquals(a, b) || a != null && b != null && a.Length == b.Length
        && a.Select(TypeNode.ToJson).SequenceEqual(b.Select(TypeNode.ToJson));

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

    // The open identity of a current structured Fqn's name. Generic arguments live in Fqn.Args; the name may carry
    // CLR metadata arity punctuation, which is the only decoration normalized here.
    public static string BareOwnerFqn(string fqnName) => StripGenericArity(fqnName.Trim());

    // The spelling used to resolve a BIR owner against CLR metadata. Current-format ClrExternal identities already
    // carry the exact TypeDef name (`Outer`1+Leaf`1`) and must remain verbatim; source-authored/Kotlin semantic names
    // still use the arity-aware bare-name probe. Keep this separate from BareOwnerFqn because semantic indexes are
    // intentionally keyed by their arity-free Kotlin identity.
    public static string ReflectedOwnerFqn(string fqnName)
    {
        var name = fqnName.Trim();
        return name.Contains('`') || name.Contains('+') ? name : BareOwnerFqn(name);
    }

    // The top-level-extension receiver KEY of a call's first-sig-arg Fqn — the call-site mirror of the ref-side
    // RecvKey(Type) (used to index/disambiguate TopLevelStatics by receiver type). A specialized primitive-array Fqn
    // (`kotlin.IntArray`/`CharArray`/... + the unsigned specialized arrays) collapses to "[]" — the SAME canonicalization
    // RecvKey(Type) applies to a real `int[]` (IsArray). kotc spells such a receiver as a bare `kotlin.IntArray` Fqn
    // (BirTypeLowering decomposes it to a real array only later), so without this collapse a primitive-array receiver
    // would key as "kotlin.IntArray" and never match the ref.dll's "[]" candidate — leaving `intArrayOf(..).toList()`
    // owner-null AND its return type unresolved (#153). generic `Array<T>` already reaches "[]" (its sig is a TypeNode.Array).
    // (TypeKey's structured Array(Int32) identity is a different key space — not conflated here.)
    public static string RecvKeyOfFqn(string fqnName) =>
        BirTypeLowering.PrimArrayElem.ContainsKey(fqnName) ? "[]" : BareOwnerFqn(fqnName);

    // Resolve a member-call/construction OWNER to its BCL type. True for a @ClrTypeAlias / class-@ClrIntrinsic owner
    // (or a foundational reference primitive). `kind` is the ref.dll type kind (class/struct/interface/enum).
    public bool TryResolveClrOwner(string ownerToken, out string bcl, out string kind)
    {
        var fqn = BareOwnerFqn(ownerToken);
        if (FoundationalRefAliases.TryGetValue(fqn, out bcl)) { kind = "class"; return true; }
        if (_ownerAlias.TryGetValue(fqn, out bcl)) { kind = _ownerAliasKind.GetValueOrDefault(fqn, "class"); return true; }
        bcl = null; kind = null; return false;
    }

    // Resolve a semantic Kotlin alias or a current-format ClrExternal token to the exact CLR TypeDef identity that a
    // CIR member descriptor must name. The type argument count is only a lookup aid for arity-free alias names; it is
    // never used to reconstruct an already exact nested identity.
    public string ExactReflectedOwner(string ownerToken, int typeArgumentCount)
    {
        var candidate = TryResolveClrOwner(ownerToken, out var aliasOwner, out _)
            ? aliasOwner : ReflectedOwnerFqn(ownerToken);
        var type = ResolveNetType(candidate, typeArgumentCount);
        if (type == null) return candidate;
        var definition = type.IsGenericType && !type.IsGenericTypeDefinition
            ? type.GetGenericTypeDefinition() : type;
        return ExactPhysicalMetadataName(definition);
    }

    public bool TryDeclaresAccessibleInstanceMethod(string sourceOwner, int genericArity, string methodName,
        out bool declares)
    {
        declares = false;
        if (string.IsNullOrEmpty(methodName)) return false;
        var owner = TryResolveClrOwner(sourceOwner, out var physicalOwner, out _)
            ? physicalOwner
            : ReflectedOwnerFqn(sourceOwner);
        var type = ResolveNetType(owner, genericArity);
        if (type == null) return false;
        try
        {
            declares = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .Any(method => method.Name == methodName
                    && (method.IsPublic || method.IsFamily || method.IsFamilyOrAssembly));
            return true;
        }
        catch { return false; }
    }

    // Exact declared public instance methods on a referenced class, expressed in the constructed owner's TypeNode
    // frame. KotlinOverrideSlotBridge uses this only to determine whether an INHERITED ordinary CLR method can capture
    // an interface DIM property slot. Returning the declaration vector keeps the physical-signature comparison in
    // bir2cir; no accessor role is inferred from the method name here.
    public IEnumerable<(TypeNode[] Parameters, TypeNode Return, bool IsVirtual, bool IsAbstract)> AccessibleDeclaredInstanceMethods(
        TypeNode.Fqn ownerSpec, string methodName, int methodArity)
    {
        if (ownerSpec == null || string.IsNullOrEmpty(methodName)) yield break;
        var ownerNames = new List<string> { BareOwnerFqn(ownerSpec.Name) };
        if (TryResolveClrOwner(ownerSpec.Name, out var physicalOwner, out _))
            ownerNames.Add(BareOwnerFqn(physicalOwner));
        var args = ownerSpec.Args ?? Array.Empty<TypeNode>();
        var seen = new HashSet<MemberBinding>();
        foreach (var owner in ownerNames.Distinct(StringComparer.Ordinal))
        {
            if (!_membersByOwner.TryGetValue(owner, out var members)) continue;
            foreach (var member in members)
            {
                if (!seen.Add(member) || member.IsStatic || !member.IsPublic
                    || member.Name != methodName || member.MethodArity != methodArity
                    || member.ParamTypeNodes == null || member.ReturnTypeNode == null)
                    continue;
                yield return (
                    member.ParamTypeNodes.Select(type => SupertypeGraph.SubstOwnerTvs(type, args)).ToArray(),
                    SupertypeGraph.SubstOwnerTvs(member.ReturnTypeNode, args),
                    member.IsVirtual,
                    member.IsAbstract);
            }
        }
    }

    // Exact declared Kotlin-source method on a referenced owner, with its physical MethodDef name retained. A
    // compiler-assigned/explicit CLR spelling can differ from the source member selected by Kotlin; callers that
    // synthesize a call need both identities and must not reconstruct one from the other.
    public IEnumerable<(string PhysicalName, TypeNode[] Parameters, TypeNode Return, bool IsVirtual, bool IsAbstract)>
        AccessibleDeclaredKotlinInstanceMethods(TypeNode.Fqn ownerSpec, string sourceMethodName, int methodArity)
    {
        if (ownerSpec == null || string.IsNullOrEmpty(sourceMethodName)) yield break;
        if (!TryMembersByBirOwner(ownerSpec.Name, out var members)) yield break;
        var args = ownerSpec.Args ?? Array.Empty<TypeNode>();
        foreach (var member in members)
        {
            if (member.IsStatic || !member.IsPublic
                || (member.DeclarationSourceName ?? member.SourceMethodName ?? member.Name) != sourceMethodName
                || member.MethodArity != methodArity
                || member.ParamTypeNodes == null || member.ReturnTypeNode == null)
                continue;
            yield return (
                member.Name,
                member.ParamTypeNodes.Select(type => SupertypeGraph.SubstOwnerTvs(type, args)).ToArray(),
                SupertypeGraph.SubstOwnerTvs(member.ReturnTypeNode, args),
                member.IsVirtual,
                member.IsAbstract);
        }
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
        if (HasDotKtOwner(fqn)) return null;
        // LOCAL-OVER-REF (#15): a type DECLARED in this compilation is this-assembly-emitted and is the authority for
        // its identity — never resolve it as an EXTERNAL .NET type off the refs, even when a referenced dll exports the
        // same FQN (the ProjectReference-source-glob layout). Source wins: leave the node routing to the emitted type.
        if (IsLocalEmittedType(fqn)) return null;
        return ProbeNetType(fqn, genericArity);
    }

    // A projected generic whose Kotlin classifier is a trusted @ClrTypeAlias is physically just as external as a
    // classifier authored directly in a CLR assembly: the already-emitted aliased TypeDef cannot implement a DotKt
    // existential carrier. Resolve that physical definition here while keeping ordinary DotKt-authored generics on
    // their metadata-backed nominal-carrier path. Callers must not bypass ResolveNetType's Kotlin-owner guard by
    // guessing from namespace or from the lowered spelling.
    public Type ResolveForeignProjectionType(string sourceOwner, IReadOnlyList<TypeNode> arguments)
    {
        var genericArity = arguments?.Count ?? 0;
        if (TryResolveClrOwner(sourceOwner, out var aliasOwner, out _))
        {
            // Some Kotlin generic surfaces already have a non-generic CLR face selected by BirTypeLowering from
            // their argument shape (Comparable<*> -> System.IComparable). That face is the exact representation;
            // routing the same value through the opaque reflection ABI would discard a valid nominal conversion.
            if (BirTypeLowering.GenericAliasHeadDependsOnLoweredArguments(aliasOwner)
                && !BirTypeLowering.ProjectedAliasHasReifiedGenericHead(
                    sourceOwner, aliasOwner, arguments)) return null;
            return ResolveNetType(aliasOwner, genericArity);
        }
        if (HasDotKtOwner(sourceOwner)) return null;
        return ResolveNetType(ReflectedOwnerFqn(sourceOwner), genericArity);
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
        out string openDeclaringType, out TypeNode declaringView, out int metadataToken, out string runtimeName,
        out string[] runtimeParameterKeys, out TypeNode declarationReturn, out bool returnsVoid)
    {
        openDeclaringType = null;
        declaringView = null;
        metadataToken = 0;
        runtimeName = null;
        runtimeParameterKeys = null;
        declarationReturn = null;
        returnsVoid = false;
        if (sourceOwner?.Args is not { Length: > 0 } ownerArgs || sourceName == null
            || callSignature == null) return false;

        var sourceType = ResolveForeignProjectionType(sourceOwner.Name, ownerArgs);
        if (sourceType == null) return false;
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
                    var declared = propertyAccess is "get" or "set"
                        ? current.GetProperties(flags | BindingFlags.DeclaredOnly)
                            .Where(property => property.Name == sourceName)
                            .Select(property => propertyAccess == "set"
                                ? property.GetSetMethod(nonPublic: false)
                                : property.GetGetMethod(nonPublic: false))
                            .Where(method => method != null)
                        : current.GetMethods(flags | BindingFlags.DeclaredOnly)
                            .Where(method => method.Name == sourceName);
                    candidates.AddRange(declared.Where(method =>
                        method.GetGenericArguments().Length == methodArity
                        && method.GetParameters().Length == callSignature.Count));
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
                    DeclarationTypeNode(p.ParameterType), callSignature[i], ownerArgs))
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
        declaringView = ExactDeclaringView(declaring);
        if (declaring.IsConstructedGenericType) declaring = declaring.GetGenericTypeDefinition();
        // Reflection over a constructed inherited owner substitutes its parameters in the DERIVED owner's frame
        // (`Derived<A,B> : Base<B>` exposes Base.Put(T) as Put(!1)).  The runtime receives the OPEN declaring type,
        // whose declaration key necessarily uses Base's own frame (Put(!0)).  Recover that open MethodDef by token
        // before producing the structural key; otherwise the exact member is rejected before token matching.
        MethodInfo openDeclaration;
        try
        {
            openDeclaration = declaring.GetMethods(flags | BindingFlags.DeclaredOnly)
                .SingleOrDefault(method => method.MetadataToken == selected.MetadataToken);
        }
        catch
        {
            return false;
        }
        if (openDeclaration == null) return false;
        openDeclaringType = ExactPhysicalMetadataName(declaring);
        metadataToken = openDeclaration.MetadataToken;
        runtimeName = openDeclaration.Name;
        runtimeParameterKeys = openDeclaration.GetParameters()
            .Select(parameter => ForeignStarRuntimeTypeKey(parameter.ParameterType)).ToArray();
        declarationReturn = DeclarationTypeNode(selected.ReturnType);
        // `selected` belongs to MetadataLoadContext. Its System.Void Type is not reference/equality-compatible
        // with the runtime's typeof(void), even though both have the same CLR identity; compare metadata names.
        returnsVoid = selected.ReturnType.FullName == "System.Void";
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
            return "a" + (type.IsSZArray ? "s" : "m") + type.GetArrayRank() + "["
                + ForeignStarRuntimeTypeKey(type.GetElementType()) + "]";
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
    static bool ForeignStarDeclarationDescribesCall(TypeNode declaration, TypeNode call,
        IReadOnlyList<TypeNode> ownerArgs)
    {
        // A use-site projection changes the set of operations Kotlin permits, not the identity of the selected CLR
        // MethodDef. Compare the projected argument's bound while retaining the projection on sourceOwner for the
        // reflection/existential representation decision.
        if (declaration is TypeNode.Projection declarationProjection)
            return ForeignStarDeclarationDescribesCall(declarationProjection.Of, call, ownerArgs);
        if (call is TypeNode.Projection callProjection)
            return ForeignStarDeclarationDescribesCall(declaration, callProjection.Of, ownerArgs);
        if (declaration is TypeNode.Oblivious dOb)
            return ForeignStarDeclarationDescribesCall(dOb.Of, call, ownerArgs);
        if (call is TypeNode.Oblivious cOb)
            return ForeignStarDeclarationDescribesCall(declaration, cOb.Of, ownerArgs);
        if (declaration is TypeNode.Nullable dn)
            return call is TypeNode.Nullable cn
                ? ForeignStarDeclarationDescribesCall(dn.Of, cn.Of, ownerArgs)
                : ForeignStarDeclarationDescribesCall(dn.Of, call, ownerArgs);
        if (call is TypeNode.Nullable callNullable)
            return ForeignStarDeclarationDescribesCall(declaration, callNullable.Of, ownerArgs);
        // kotc keeps an already-selected CLR overload's owner slot in `sig` (`Duo<*, String>.Pick(B)` carries
        // `tv(type,1)`, not the substituted String). Compare both declaration and call slots in the source owner's
        // constructed semantic view; otherwise T0 and T1 either both look wildcard-like or neither matches.
        if (call is TypeNode.Tv { Scope: "type" } callOwnerTv)
        {
            if (callOwnerTv.I < 0 || callOwnerTv.I >= ownerArgs.Count
                || ownerArgs[callOwnerTv.I] is TypeNode.Star) return false;
            var supplied = ProjectionBound(ownerArgs[callOwnerTv.I]);
            if (declaration is TypeNode.Tv { Scope: "type" } declarationOwnerTv
                && declarationOwnerTv.I >= 0 && declarationOwnerTv.I < ownerArgs.Count
                && ownerArgs[declarationOwnerTv.I] is not TypeNode.Star)
                return DeclarationDescribesCall(ProjectionBound(ownerArgs[declarationOwnerTv.I]), supplied);
            return ForeignStarDeclarationDescribesCall(declaration, supplied, Array.Empty<TypeNode>());
        }
        if (declaration is TypeNode.Tv { Scope: "type" } ownerTv)
        {
            if (ownerTv.I < 0 || ownerTv.I >= ownerArgs.Count || ownerArgs[ownerTv.I] is TypeNode.Star) return false;
            return DeclarationDescribesCall(ProjectionBound(ownerArgs[ownerTv.I]), call);
        }
        if (declaration is TypeNode.Tv) return true;
        if (declaration is TypeNode.Fqn df && call is TypeNode.Fqn cf)
        {
            if (ParamKey(df) != ParamKey(cf)) return false;
            if (df.Args == null || cf.Args == null) return df.Args == null && cf.Args == null;
            return df.Args.Length == cf.Args.Length
                && df.Args.Select((arg, i) => ForeignStarDeclarationDescribesCall(arg, cf.Args[i], ownerArgs)).All(x => x);
        }
        if (declaration is TypeNode.Array da && call is TypeNode.Array ca)
            return ForeignStarDeclarationDescribesCall(da.Elem, ca.Elem, ownerArgs);
        if (declaration is TypeNode.ByRef db && call is TypeNode.ByRef cb)
            return ForeignStarDeclarationDescribesCall(db.Of, cb.Of, ownerArgs);
        if (declaration is TypeNode.Fn dfn && call is TypeNode.Fn cfn)
            return FunctionDeclarationDescribesCall(dfn, cfn,
                (declared, supplied) => ForeignStarDeclarationDescribesCall(declared, supplied, ownerArgs));
        return DeclarationDescribesCall(declaration, call);
    }

    static TypeNode ProjectionBound(TypeNode type) => type switch
    {
        TypeNode.Projection projection => ProjectionBound(projection.Of),
        TypeNode.Oblivious oblivious => ProjectionBound(oblivious.Of),
        _ => type,
    };

    // Field-backed CLR properties projected by dll2klib use the same clrPropGet/clrPropSet node as real properties.
    // Keep field dispatch exact as well: the runtime receives a metadata token, never a source-name lookup.
    public bool TryForeignStarField(TypeNode.Fqn sourceOwner, string sourceName,
        out string openDeclaringType, out TypeNode declaringView, out int metadataToken, out TypeNode declarationType)
    {
        openDeclaringType = null;
        declaringView = null;
        metadataToken = 0;
        declarationType = null;
        if (sourceOwner?.Args is not { Length: > 0 } ownerArgs || sourceName == null) return false;
        var sourceType = ResolveForeignProjectionType(sourceOwner.Name, ownerArgs);
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
        declaringView = ExactDeclaringView(declaring);
        if (declaring.IsConstructedGenericType) declaring = declaring.GetGenericTypeDefinition();
        openDeclaringType = ExactPhysicalMetadataName(declaring);
        metadataToken = selected.MetadataToken;
        declarationType = DeclarationTypeNode(selected.FieldType);
        return declarationType != null;
    }

    static TypeNode.Fqn ExactDeclaringView(Type type)
    {
        if (DeclarationTypeNode(type) is not TypeNode.Fqn view) return null;
        var definition = type.IsConstructedGenericType ? type.GetGenericTypeDefinition() : type;
        return new TypeNode.Fqn(ExactPhysicalMetadataName(definition), view.Args);
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
        if (IsLocalEmittedType(fqn)) return null;
        return ProbeNetType(fqn, genericArity);
    }

    // A referenced Literal FieldDef has no storage to load. Select its exact Constant-table value and Kotlin surface
    // type here so ConstFieldLowering can author a `const` CIR node; ilemit must not rediscover this representation.
    public bool TryResolveLiteralField(TypeNode.Fqn owner, string name, out TypeNode type, out JsonNode value)
    {
        type = null;
        value = null;
        if (owner == null || string.IsNullOrEmpty(name)) return false;
        var cacheKey = owner.Name + "|" + (owner.Args?.Length ?? 0) + "|" + name;
        if (_literalFieldCache.TryGetValue(cacheKey, out var cached))
        {
            type = cached.Type;
            value = cached.Value?.DeepClone();
            return cached.Found;
        }
        var reflectedOwner = ResolveRefType(owner.Name, owner.Args?.Length ?? 0);
        FieldInfo field;
        try
        {
            field = reflectedOwner?.GetField(name,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.FlattenHierarchy);
        }
        catch { return CacheLiteralMiss(cacheKey, out type, out value); }
        if (field == null || !field.IsLiteral) return CacheLiteralMiss(cacheKey, out type, out value);
        object raw;
        try { raw = field.GetRawConstantValue(); }
        catch { return CacheLiteralMiss(cacheKey, out type, out value); }
        if (raw == null) return CacheLiteralMiss(cacheKey, out type, out value);
        type = KotlinTypeOf(field.GetCustomAttributesData(), field.DeclaringType?.Assembly)
            ?? TypeNodeOf(field.FieldType);
        if (type == null) return CacheLiteralMiss(cacheKey, out type, out value);
        value = LiteralValueNode(raw);
        if (value == null) return CacheLiteralMiss(cacheKey, out type, out value);
        _literalFieldCache[cacheKey] = (true, type, value.DeepClone());
        return true;
    }

    bool CacheLiteralMiss(string cacheKey, out TypeNode type, out JsonNode value)
    {
        _literalFieldCache[cacheKey] = (false, null, null);
        type = null;
        value = null;
        return false;
    }

    // CIR const values use the same compact wire convention as kotc-authored BIR: non-finite floating-point values
    // are strings because JSON has no tokens for them, and unsigned 32/64-bit values are their signed bit patterns
    // because ilemit consumes them with GetInt32/GetInt64 before emitting the representation-identical ldc opcode.
    // Reflection returns the CLR scalar, so normalize it here while bir2cir still owns the physical representation.
    static JsonNode LiteralValueNode(object raw) => raw switch
    {
        double value when double.IsNaN(value) || double.IsInfinity(value) =>
            JsonValue.Create(value.ToString("R", CultureInfo.InvariantCulture)),
        float value when float.IsNaN(value) || float.IsInfinity(value) =>
            JsonValue.Create(value.ToString("R", CultureInfo.InvariantCulture)),
        uint value => JsonValue.Create(unchecked((int)value)),
        ulong value => JsonValue.Create(unchecked((long)value)),
        char value => JsonValue.Create(value.ToString()),
        _ => JsonSerializer.SerializeToNode(raw, raw.GetType()),
    };

    // Volatility of a referenced field is a concrete CLR representation fact. Resolve it while reading the reference
    // universe and carry it into CIR; ilemit must not reopen a FieldInfo and infer the missing prefix at emission time.
    public bool TryResolveVolatileField(TypeNode.Fqn owner, string name)
    {
        if (owner == null || string.IsNullOrEmpty(name)) return false;
        var cacheKey = owner.Name + "|" + (owner.Args?.Length ?? 0) + "|" + name;
        if (_volatileFieldCache.TryGetValue(cacheKey, out var cached)) return cached;
        var reflectedOwner = ResolveRefType(owner.Name, owner.Args?.Length ?? 0);
        FieldInfo field;
        try
        {
            field = reflectedOwner?.GetField(name, BindingFlags.Public | BindingFlags.NonPublic |
                BindingFlags.Static | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
        }
        catch { return _volatileFieldCache[cacheKey] = false; }
        if (field == null) return _volatileFieldCache[cacheKey] = false;
        try
        {
            return _volatileFieldCache[cacheKey] = field.GetRequiredCustomModifiers().Any(type =>
                type.FullName == "System.Runtime.CompilerServices.IsVolatile");
        }
        catch (NotSupportedException) { return _volatileFieldCache[cacheKey] = false; }
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

    public bool IsEnumType(TypeNode.Fqn type)
    {
        if (type == null) return false;
        var identity = OwnerIdentity(type.Name, type.Args?.Length ?? 0);
        string kind;
        if (HasExactOwnerPunctuation(type.Name))
            kind = _ownerKindByPhysicalOwner.GetValueOrDefault(type.Name);
        else if (_exactPhysicalTypeByDottedName.TryGetValue(identity, out var exact))
            kind = exact == null ? null : _ownerKindByPhysicalOwner.GetValueOrDefault(exact);
        else
            kind = _ownerKind.GetValueOrDefault(identity);
        return kind == "enum";
    }

    // Resolve the physical representation of the exact referenced CLR enum selected by a dll2klib
    // [ClrFlagsOperation] declaration. The semantic carrier is trusted compiler metadata, but its signature is still
    // validated against the current target reference universe: it must name a real enum carrying the target corelib's
    // exact System.FlagsAttribute, not a same-FQN lookalike.
    public FlagsEnumRepresentation ResolveFlagsEnum(TypeNode typeNode)
    {
        if (typeNode is not TypeNode.Fqn owner) return null;
        var type = ResolveRefType(owner.Name, owner.Args?.Length ?? 0);
        if (type == null || !type.IsEnum) return null;
        Type underlying;
        Type enumBase;
        IList<CustomAttributeData> attributes;
        try
        {
            underlying = type.GetEnumUnderlyingType();
            enumBase = type.BaseType;
            attributes = type.GetCustomAttributesData();
        }
        catch { return null; }
        var flags = attributes.Where(attribute =>
            attribute.AttributeType.FullName == "System.FlagsAttribute" &&
            enumBase != null &&
            StringComparer.Ordinal.Equals(
                attribute.AttributeType.Assembly.FullName,
                enumBase.Assembly.FullName)).ToArray();
        if (flags.Length != 1) return null;
        var underlyingName = underlying.FullName;
        if (underlyingName is not ("System.SByte" or "System.Byte" or "System.Int16" or "System.UInt16" or
            "System.Int32" or "System.UInt32" or "System.Int64" or "System.UInt64"))
            return null;
        var exactEnum = new TypeNode.Fqn(ExactPhysicalMetadataName(type), owner.Args);
        return new FlagsEnumRepresentation(exactEnum, new TypeNode.Fqn(underlyingName));
    }

    // The shared MLC probe (cache + candidate spellings + forwarder collapse) — the caller applies the owner-universe
    // policy (ResolveNetType excludes kotlin.*/dotkt$ synthetics/local; ResolveRefType excludes only the latter two).
    /// <summary>
    /// The type named by <paramref name="fqn"/> as DECLARED BY a specific reference assembly (#370). An applied
    /// external attribute may state its declaring scope precisely because the FQN alone is ambiguous — a
    /// compiler-synthesized attribute can share it with a private lookalike — so the stated scope selects
    /// rather than the ordinary probe guessing.
    /// </summary>
    public Type ResolveRefTypeIn(string fqn, string assemblySimpleName)
    {
        if (string.IsNullOrEmpty(fqn) || string.IsNullOrEmpty(assemblySimpleName)) return null;
        EnsureNetMlc();
        if (_netMlc == null) return null;
        Type found = null;
        foreach (var asm in _netRefAsms)
        {
            if (!string.Equals(asm.GetName().Name, assemblySimpleName, StringComparison.OrdinalIgnoreCase)) continue;
            var match = SafeGetType(asm, fqn);
            if (match == null) continue;
            // Two references answering to one simple name is an ambiguity, not a race to be first: the whole
            // point of stating the scope was to name ONE declaration. Every sibling resolver in this file
            // refuses the same way.
            if (found != null && found != match)
                throw new InvalidOperationException(
                    $"bir2cir: type '{fqn}' is defined by more than one reference named '{assemblySimpleName}'");
            found = match;
        }
        return found;
    }

    Type ProbeNetType(string fqn, int genericArity)
    {
        // CLR permits a non-generic and one or more generic TypeDefs to share the same source-facing FQN (Task and
        // Task<T> are the common case). The arity is part of the physical type identity; caching only by the stripped
        // FQN lets whichever spelling is requested first poison every later lookup.
        var cacheKey = fqn + "|" + genericArity;
        if (_netTypeCache.TryGetValue(cacheKey, out var cached)) return cached;
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
        _netTypeCache[cacheKey] = found;
        return found;
    }

    // The FQN spellings to probe: the plain name, then the generic-arity backtick form (`List`1`). The exact arity
    // (from the owner token's type-arg count) is tried first; a small fallback range covers a token that dropped its args.
    static IEnumerable<string> NetTypeCandidates(string fqn, int genericArity)
    {
        // A supplied arity is an exact identity fact, not a hint. Probe its backtick spelling before the non-generic
        // namesake; otherwise Task<T> deterministically resolves as Task whenever both TypeDefs exist.
        if (genericArity > 0) yield return fqn + "`" + genericArity;
        yield return fqn;
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

    MetadataLoadContext _physicalMlc;
    Assembly _physicalStdlib; bool _physicalStdlibInit;

    /// <summary>
    /// The SHIPPED declaration of a member resolved against the reference twin, or null when there is none or it
    /// cannot be identified unambiguously.
    /// </summary>
    /// <remarks>
    /// The reference twin declares the Kotlin surface and the runtime twin declares the physical shape; a member
    /// reference must state the latter, because that is the assembly the emitted reference is scoped to and the
    /// one the emitter resolves against. Deriving the physical shape from the surface means undoing every erasure
    /// the reference build applied — the arg-position variance collapse, the generic-classifier and contravariant
    /// collapses, and a value type's nullability, which the surface cannot express at all because `kotlin.Float`
    /// is a class there and `Nullable&lt;class&gt;` is not a type. Each of those was found by a build failing on it.
    ///
    /// So read the shipped declaration instead of reconstructing it. Selection stays here, where it belongs, and
    /// refuses rather than guesses: same declaring type name, same member name, same generic arity, same parameter
    /// count, and exactly one candidate. An ambiguous or absent twin falls back to the reflected member, which is
    /// correct for every reference whose twin is itself (a BCL assembly is its own physical form).
    /// </remarks>
    readonly Dictionary<MemberInfo, MemberInfo> _shippedCache = new();

    public MethodBase PhysicalTwinOf(MethodBase member, Type declaringDef)
    {
        if (_shippedCache.TryGetValue(member, out var cached)) return cached as MethodBase;
        MethodBase found = null;
        try { found = FindShippedMethod(member, declaringDef); } catch { found = null; }
        _shippedCache[member] = found;
        return found;
    }

    MethodBase FindShippedMethod(MethodBase member, Type declaringDef)
    {
        var owner = PhysicalOwnerOf(declaringDef);
        if (owner == null) return null;
        var arity = member.IsGenericMethod ? member.GetGenericArguments().Length : 0;
        var want = member.GetParameters();
        var candidates = (member is ConstructorInfo
                // A type initializer is a constructor to reflection and never a member anything references, so it
                // would otherwise make every parameterless constructor of a type with static state ambiguous.
                ? owner.GetConstructors(MemberProbeFlags).Cast<MethodBase>().Where(c => !c.IsStatic)
                : owner.GetMethods(MemberProbeFlags).Cast<MethodBase>()
                    .Where(m => string.Equals(m.Name, member.Name, StringComparison.Ordinal)))
            // Metadata parameter counts exclude `this`, so without the static bit an instance member and a static
            // one of the same name and arity land in the same bucket and either could answer for the other.
            .Where(m => m.IsStatic == member.IsStatic
                && m.GetParameters().Length == want.Length
                && (m.IsGenericMethod ? m.GetGenericArguments().Length : 0) == arity)
            .ToList();
        if (candidates.Count == 1) return candidates[0];
        if (candidates.Count == 0) return null;
        // Same name, same shape, several of them — a facade like `maxOrNull` has one per element type. Choose by
        // the one thing both twins spell alike: each parameter's alias-resolved type identity. This picks WHICH
        // declaration is meant; the signature still comes from the declaration itself, so a position the surface
        // cannot express is taken from the shipped side either way.
        var key = string.Join(",", want.Select(p => TwinStableKey(p.ParameterType)));
        var matched = candidates
            .Where(m => string.Join(",", m.GetParameters().Select(p => TwinStableKey(p.ParameterType))) == key)
            .ToList();
        return matched.Count == 1 ? matched[0] : null;
    }

    /// <summary>A type's identity in the spelling both twins share: the alias resolved, the shape kept.</summary>
    string TwinStableKey(Type t)
    {
        if (t == null) return "?";
        if (t.IsByRef) return TwinStableKey(t.GetElementType()) + "&";
        if (t.IsPointer) return TwinStableKey(t.GetElementType()) + "*";
        if (t.IsArray) return TwinStableKey(t.GetElementType()) + "[" + t.GetArrayRank() + "]";
        if (t.IsGenericParameter) return "!" + t.GenericParameterPosition;
        var def = t.IsGenericType && !t.IsGenericTypeDefinition ? t.GetGenericTypeDefinition() : t;
        var name = string.Join(".", (def.FullName ?? def.Name).Split('+')
            .Select(seg => { var i = seg.IndexOf('`'); return i >= 0 ? seg[..i] : seg; }));
        if (Aliases.TryGetValue(name, out var bcl)) name = bcl;
        var args = t.IsGenericType && !t.IsGenericTypeDefinition
            ? "<" + string.Join(",", t.GetGenericArguments().Select(TwinStableKey)) + ">" : "";
        return name + args;
    }

    public FieldInfo PhysicalTwinOf(FieldInfo field, Type declaringDef)
    {
        if (_shippedCache.TryGetValue(field, out var cached)) return cached as FieldInfo;
        FieldInfo found = null;
        try
        {
            var owner = PhysicalOwnerOf(declaringDef);
            var hits = owner?.GetFields(MemberProbeFlags)
                .Where(f => string.Equals(f.Name, field.Name, StringComparison.Ordinal)
                    && f.IsStatic == field.IsStatic).ToList();
            found = hits is { Count: 1 } ? hits[0] : null;
        }
        catch { found = null; }
        _shippedCache[field] = found;
        return found;
    }

    const BindingFlags MemberProbeFlags =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    /// <summary>
    /// The shipped form of a declaring type, or null when this type has none.
    /// </summary>
    /// <remarks>
    /// The declaring type must be the ALIAS-RESOLVED one, the same the reference names. An @ClrTypeAlias'd builtin
    /// resolves to its BCL twin and is therefore its own shipped form; looking the raw Kotlin carrier up in the
    /// runtime stdlib instead would read a different type's members into a reference scoped to the BCL.
    ///
    /// Which assemblies HAVE a separate shipped form is the catalog's knowledge, asked rather than restated: a
    /// second twin pair, or a rename, then needs no edit here and cannot disagree with the mapping the reference's
    /// own assembly name comes from.
    /// </remarks>
    Type PhysicalOwnerOf(Type declaringDef)
    {
        if (declaringDef == null) return null;
        var asm = PhysicalStdlibAssembly();
        if (asm == null || declaringDef.Assembly?.GetName()?.Name is not string owning
            || string.Equals(ManagedReferenceCatalog.PhysicalAssemblyName(owning), owning, StringComparison.Ordinal))
            return null;
        var def = declaringDef.IsGenericType && !declaringDef.IsGenericTypeDefinition
            ? declaringDef.GetGenericTypeDefinition() : declaringDef;
        try { return asm.GetType(def.FullName, throwOnError: false); } catch { return null; }
    }

    /// <summary>
    /// The shipped declaration of a type named only by the assembly that ships it. A canonical synthetic
    /// interface is emitted once into the runtime stdlib and referenced from there; the reference twin
    /// describes the Kotlin surface and has no name for it at all, so the reference surface can never
    /// resolve one. Naming a slot on such a type therefore has to read the twin that ships it — the same
    /// type the emitter resolves, reached by the same bare name.
    /// </summary>
    public Type PhysicalTypeNamed(string name, int arity = 0)
    {
        if (string.IsNullOrEmpty(name)) return null;
        var asm = PhysicalStdlibAssembly();
        if (asm == null) return null;
        try
        {
            return asm.GetType(name, throwOnError: false)
                ?? (arity > 0 ? asm.GetType($"{name}`{arity}", throwOnError: false) : null);
        }
        catch { return null; }
    }

    Assembly PhysicalStdlibAssembly()
    {
        if (_physicalStdlibInit) return _physicalStdlib;
        _physicalStdlibInit = true;
        var path = _compileRefs?.PhysicalStdlibPath;
        if (string.IsNullOrEmpty(path)) return null;
        try
        {
            _physicalMlc = _compileRefs.CreatePhysicalStdlibMetadataLoadContext();
            _physicalStdlib = _physicalMlc?.LoadFromAssemblyPath(path);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"bir2cir: warning: could not read the shipped stdlib twin {path} — {ex.GetType().Name}: {ex.Message}");
            _physicalStdlib = null;
        }
        return _physicalStdlib;
    }

    public void DisposeNet()
    {
        try { _physicalMlc?.Dispose(); } catch { }
        _physicalMlc = null;
        try { _netMlc?.Dispose(); } catch { }
        _netMlc = null;
    }

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
    public bool TryKotlinRichEnumStaticApis(string ownerFqn, out string values, out string valueOf)
    {
        var bare = StripGenericArity(DottedFqn(BareOwnerFqn(ownerFqn)));
        if (_richEnums.TryGetValue(bare, out var metadata))
        {
            values = metadata.Values;
            valueOf = metadata.ValueOf;
            return true;
        }
        values = null;
        valueOf = null;
        return false;
    }

    public bool TryKotlinBasicEnum(string ownerFqn, out BasicEnumMetadata metadata) =>
        _basicEnums.TryGetValue(
            StripGenericArity(DottedFqn(BareOwnerFqn(ownerFqn))), out metadata);

    public bool TryKotlinRichEnumStaticApi(
        string ownerFqn, string sourceMemberName, int paramCount, out string physicalMemberName)
    {
        var bare = StripGenericArity(DottedFqn(BareOwnerFqn(ownerFqn)));
        if (_richEnums.TryGetValue(bare, out var metadata))
        {
            if (paramCount == 0 && sourceMemberName == "values")
            {
                physicalMemberName = metadata.Values;
                return true;
            }
            if (paramCount == 1 && sourceMemberName == "valueOf")
            {
                physicalMemberName = metadata.ValueOf;
                return true;
            }
        }
        physicalMemberName = null;
        return false;
    }

    public bool TryKotlinRichEnumEntryField(string ownerFqn, string entryName, out string physicalField)
    {
        var bare = StripGenericArity(DottedFqn(BareOwnerFqn(ownerFqn)));
        if (_richEnums.TryGetValue(bare, out var metadata) &&
            metadata.EntryFields.TryGetValue(entryName, out physicalField))
            return true;
        physicalField = null;
        return false;
    }

    public bool TryKotlinRichEnumInstanceFields(
        string ownerFqn, out string nameField, out string ordinalField)
    {
        var bare = StripGenericArity(DottedFqn(BareOwnerFqn(ownerFqn)));
        if (_richEnums.TryGetValue(bare, out var metadata))
        {
            nameField = metadata.Name;
            ordinalField = metadata.Ordinal;
            return true;
        }
        nameField = null;
        ordinalField = null;
        return false;
    }
    public JsonArray OwnerTypeParamDeclarations(string ownerFqn)
    {
        if (ownerFqn == null) return null;
        var value = _ownerTypeParamDeclarations.GetValueOrDefault(ownerFqn)
            ?? _ownerTypeParamDeclarations.GetValueOrDefault(StripGenericArity(ownerFqn))
            ?? _ownerTypeParamDeclarations.GetValueOrDefault(StripGenericArity(DottedFqn(ownerFqn)));
        return value == null ? null : JsonNode.Parse(value) as JsonArray;
    }

    public bool HasPublicParameterlessConstructor(TypeNode.Fqn owner)
    {
        if (owner == null) return false;
        var identity = OwnerIdentity(owner.Name, owner.Args?.Length ?? 0);
        if (HasExactOwnerPunctuation(owner.Name))
            return _publicParameterlessConstructiblePhysicalOwners.Contains(owner.Name);
        if (_exactPhysicalTypeByDottedName.TryGetValue(identity, out var exact))
            return exact != null && _publicParameterlessConstructiblePhysicalOwners.Contains(exact);
        return _publicParameterlessConstructibleOwners.Contains(identity);
    }

    public bool TryExactPhysicalTypeName(string ownerFqn, int arity, out string exact)
    {
        exact = null;
        return ownerFqn != null &&
            _exactPhysicalTypeByDottedName.TryGetValue(OwnerIdentity(ownerFqn, arity), out exact);
    }

    // Exact CLR metadata identity for a trusted external DotKt classifier. Local source declarations remain
    // authoritative and therefore never rewrite through a same-named reference.
    public IReadOnlyDictionary<string, string> PhysicalTypeNames =>
        _physicalTypeBySemanticName
            .Where(kv => !IsLocalEmittedType(kv.Key))
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
    public TypeNode[] OwnerCtorParamTypes(string ownerFqn)
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
    public bool IsValueType(TypeNode.Fqn type)
    {
        if (type == null) return false;
        if (ValueTypePrimitiveFqns.Contains(type.Name)) return true;
        var identity = OwnerIdentity(type.Name, type.Args?.Length ?? 0);
        string kind;
        if (HasExactOwnerPunctuation(type.Name))
            kind = _ownerKindByPhysicalOwner.GetValueOrDefault(type.Name);
        else if (_exactPhysicalTypeByDottedName.TryGetValue(identity, out var exact))
            kind = exact == null ? null : _ownerKindByPhysicalOwner.GetValueOrDefault(exact);
        else
            kind = _ownerKind.GetValueOrDefault(identity);
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
    public bool IsByRefLikeFqn(TypeNode.Fqn type)
    {
        if (type == null) return false;
        var name = type.Name;
        if (StripGenericArity(name) == BirTypeLowering.SpanIntrinsicFqn)
            name = BirTypeLowering.SpanClrFqn;
        var identity = OwnerIdentity(name, type.Args?.Length ?? 0);
        if (HasExactOwnerPunctuation(name)) return _byRefLikePhysicalOwners.Contains(name);
        if (_exactPhysicalTypeByDottedName.TryGetValue(identity, out var exact))
            return exact != null && _byRefLikePhysicalOwners.Contains(exact);
        return _byRefLikeOwners.Contains(identity);
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

    public bool IsFileClassOwner(string ownerFqn) =>
        ownerFqn != null && _fileClassOwners.Contains(StripGenericArity(DottedFqn(ownerFqn)));

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

    public bool TryCompanionExtensionMember(
        string owner, string receiverJson, string kind, string sourceName, out string physicalName)
    {
        if (_companionExtensionMembers.TryGetValue(
                CompanionExtensionKey(owner, receiverJson, kind, sourceName), out physicalName) &&
            !string.IsNullOrEmpty(physicalName))
            return true;
        physicalName = null;
        return false;
    }

    static string CompanionExtensionKey(string owner, string receiverJson, string kind, string sourceName)
    {
        if (owner == null || receiverJson == null || kind == null || sourceName == null) return "";
        TypeNode receiverType = TypeJson.Read(JsonNode.Parse(receiverJson));
        while (receiverType is TypeNode.Oblivious oblivious) receiverType = oblivious.Of;
        while (receiverType is TypeNode.Nullable nullable) receiverType = nullable.Of;
        var classifier = (receiverType is TypeNode.Fqn fqn ? fqn.Name : null)
            ?? throw new InvalidDataException("companion-extension receiver payload is not a classifier");
        // The source language association is the bare classifier. dll2klib can legitimately rehydrate a generic
        // receiver as C<Any>; its arguments are not declaration identity and must not enter this trusted key.
        var receiver = TypeJson.Fqn(BareOwnerFqn(DottedFqn(classifier))).ToJsonString();
        return StripGenericArity(DottedFqn(owner)) + "\u001f" + receiver + "\u001f" + kind + "\u001f" + sourceName;
    }

    public bool TryCompanionPhysicalOwner(string semanticType, out string physicalOwner) =>
        _companionPhysicalOwnerBySemanticType.TryGetValue(
            StripGenericArity(DottedFqn(semanticType)), out physicalOwner);

    public bool TryGenericStaticCarrier(string semanticOwner, out string physicalOwner) =>
        _genericStaticCarrierBySemanticOwner.TryGetValue(
            StripGenericArity(DottedFqn(semanticOwner)), out physicalOwner);

    public bool IsGenericStaticCarrier(string physicalOwner) =>
        _genericStaticCarriers.Contains(StripGenericArity(DottedFqn(physicalOwner)));

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
        var type = ResolveNetType(ReflectedOwnerFqn(ownerFqn), 0);
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
    // signature mentions an owner type parameter cannot reuse the Kotlin name: on G<object> the erased bridge would
    // collide with the real closed-generic slot, so FBoundStarProjectionErasure gives it an unspeakable physical name.
    //
    // This is reference metadata, not a spelling guess: the trusted type-level [KotlinType(G<*,...>)] relation names
    // the emitted existential owner, then select a unique name+arity slot from its actual
    // member table.  The caller retains the Kotlin vocabulary until bir2cir asks this index for the
    // concrete CIR owner/member pair. The index must consume the source-member/property carrier on that MethodDef;
    // reconstructing a generated spelling would make an unrelated source declaration rename change this binding.
    public bool TryStarProjectionMember(TypeNode.Fqn sourceOwner, string sourceMember, string accessorKind,
        int methodArity,
        IReadOnlyList<TypeNode> authoredSignature, int paramCount, string declarationId,
        out string erasedOwner, out string erasedMember, out TypeNode[] erasedSignature,
        out TypeNode declarationResult, out TypeNode physicalResult)
    {
        erasedOwner = erasedMember = null;
        erasedSignature = null;
        declarationResult = null;
        physicalResult = null;
        if (sourceOwner == null || sourceMember == null) return false;
        if (!TryExistentialPhysicalOwner(sourceOwner.Name, out var candidateOwner)
            || !TryMembersByBirOwner(candidateOwner, out var members)
            || !TryMembersByBirOwner(sourceOwner.Name, out var semanticMembers)) return false;

        var declarations = semanticMembers.Where(m => !m.IsStatic
            && (declarationId != null
                ? m.DeclarationId == declarationId
                : accessorKind is "get" or "set"
                    ? !m.IsPropertyBridge && m.SourcePropertyName == sourceMember && m.AccessorKind == accessorKind
                    : m.SourcePropertyName == null && (m.SourceMethodName ?? m.Name) == sourceMember)
            && m.MethodArity == methodArity && m.ParamCount == paramCount
            && (declarationId != null || authoredSignature == null
                || m.ParamTypeNodes is { } ps && ps.Length == authoredSignature.Count
                && (ps.SequenceEqual(authoredSignature)
                    || ps.Select((p, i) => ForeignStarDeclarationDescribesCall(
                        p, authoredSignature[i], sourceOwner.Args ?? Array.Empty<TypeNode>())).All(x => x))))
            .ToList();
        if (declarations.Count != 1) return false;

        // Select the actual MethodDef on the trusted existential owner through its explicit source identity. Its Name
        // is already the final physical link target; neither the source declaration's allocated name nor an ordinal is
        // sufficient to derive it. In particular, an explicit @ClrName changes the source MethodDef but not the
        // compiler-owned dependent-slot spelling.
        var selectedSourceMember = accessorKind is "get" or "set"
            ? declarations[0].SourcePropertyName ?? sourceMember
            : declarations[0].DeclarationSourceName ?? declarations[0].SourceMethodName ?? sourceMember;
        // Earlier physical lowering may deliberately give the ordinary declaration and its existential slot distinct
        // CLR descriptors. Compare their preserved Kotlin descriptors instead: EraseParams copied that exact source
        // fact onto every synthesized slot before changing its physical type. This remains exact for same-name overloads
        // without reconstructing the representation change from a generated name or accepting a merely unique sibling.
        var declarationParameters = declarations[0].DeclarationSemanticParams
            ?? declarations[0].KotlinParameterTypes
            ?? declarations[0].ParamTypeNodes;
        bool DescribesSelectedDeclaration(MemberBinding candidate) => declarationParameters == null
            || (candidate.KotlinParameterTypes ?? candidate.ParamTypeNodes) is { } candidateParameters
                && candidateParameters.Length == declarationParameters.Length
                && candidateParameters.Select((p, i) => DeclarationDescribesCall(
                    declarationParameters[i], p)).All(x => x);
        bool DescribesSelectedPhysicalDeclaration(MemberBinding candidate) => declarations[0].ParamTypeNodes is { } declarationPhysical
            && candidate.ParamTypeNodes is { } candidatePhysical
            && candidatePhysical.Length == declarationPhysical.Length
            && candidatePhysical.SequenceEqual(declarationPhysical);

        var shapedCandidates = members.Where(m => !m.IsStatic && m.ParamCount == paramCount
            && m.MethodArity == methodArity).ToList();
        List<MemberBinding> candidates;
        if (accessorKind is "get" or "set")
        {
            candidates = shapedCandidates.Where(m => !m.IsPropertyBridge
                && m.SourcePropertyName == selectedSourceMember && m.AccessorKind == accessorKind
                && DescribesSelectedDeclaration(m)).ToList();
        }
        else
        {
            // Owner-dependent slots carry their Kotlin source-method identity. Prefer that authoritative identity over
            // an ordinary sibling whose physical @ClrName happens to equal it. Owner-independent slots intentionally
            // retain the selected declaration's MethodDef name and need no source carrier, so use that exact physical
            // identity only when no semantically matching carried slot exists.
            var carriedCandidates = shapedCandidates.Where(m => m.SourcePropertyName == null
                && m.SourceMethodName == selectedSourceMember && DescribesSelectedDeclaration(m)).ToList();
            candidates = carriedCandidates.Count != 0
                ? carriedCandidates
                : shapedCandidates.Where(m => m.SourcePropertyName == null && m.SourceMethodName == null
                    && m.Name == declarations[0].Name && DescribesSelectedPhysicalDeclaration(m)).ToList();
        }
        if (candidates.Count != 1) return false;
        erasedOwner = candidateOwner;
        erasedMember = candidates[0].Name;
        erasedSignature = candidates[0].ParamTypeNodes ?? Array.Empty<TypeNode>();
        declarationResult = declarations[0].NullableGenericRet
            ?? declarations[0].KotlinReturnType
            ?? declarations[0].ReturnTypeNode;
        physicalResult = candidates[0].ReturnTypeNode;
        return true;
    }

    // A referenced existential outer publishes constructor factories as trusted generated interface slots.  Select
    // the exact slot from the carrier's Kotlin inner classifier + constructor descriptor; the physical method name is
    // only an output.  This deliberately does not infer construction semantics from a `$star$new$...` spelling.
    public bool TryExistentialInnerConstructorFactory(string semanticOuter, TypeNode.Fqn innerType,
        IReadOnlyList<TypeNode> authoredParameters,
        out string physicalOwner, out string physicalMethod, out TypeNode[] physicalParameters,
        out TypeNode physicalResult, out TypeNode[] physicalTypeArguments)
    {
        physicalOwner = physicalMethod = null;
        physicalParameters = null;
        physicalResult = null;
        physicalTypeArguments = null;
        if (semanticOuter == null || innerType == null || authoredParameters == null
            || !TryExistentialPhysicalOwner(semanticOuter, out var owner)
            || !TryMembersByBirOwner(owner, out var members)) return false;
        var arguments = innerType.Args ?? Array.Empty<TypeNode>();
        bool DescribesInnerClassifier(MemberBinding member)
        {
            if (member.InnerConstructorOwner == innerType.Name) return true;
            return TryExactPhysicalTypeName(member.InnerConstructorOwner, arguments.Length, out var exact)
                && exact == innerType.Name;
        }
        bool TryFactoryTypeArguments(MemberBinding member, out TypeNode[] methodArguments)
        {
            methodArguments = null;
            if (member.InnerConstructorTypeArguments is not { } pattern
                || pattern.Length > arguments.Length) return false;
            var capturedCount = arguments.Length - pattern.Length;
            var result = new TypeNode[member.MethodArity];
            var assigned = new bool[result.Length];
            for (var i = 0; i < pattern.Length; i++)
            {
                var actual = arguments[capturedCount + i];
                switch (pattern[i])
                {
                    case FBoundStarProjectionErasure.InnerFactoryBottomTypeArgument:
                        if (actual is not TypeNode.Fqn { Args: null, Name: "kotlin.Nothing" }) return false;
                        break;
                    case var slot when slot >= 0 && slot < result.Length:
                        if (assigned[slot] && result[slot] != actual) return false;
                        result[slot] = actual;
                        assigned[slot] = true;
                        break;
                    default:
                        return false;
                }
            }
            if (assigned.Any(value => !value)) return false;
            methodArguments = result;
            return true;
        }

        var candidates = new List<(MemberBinding Member, TypeNode[] TypeArguments)>();
        foreach (var member in members.Where(member =>
                !member.IsStatic
                && member.InnerConstructorOwner != null && DescribesInnerClassifier(member)
                && member.InnerConstructorParameters is { } semanticParameters
                && semanticParameters.Length == authoredParameters.Count
                && semanticParameters.Select((parameter, index) =>
                        SemanticDeclarationDescribesCall(
                            FBoundStarProjectionErasure.CloseInnerConstructorType(parameter, arguments),
                            authoredParameters[index]))
                    .All(equal => equal)
                && member.ParamTypeNodes != null && member.ReturnTypeNode != null))
            if (TryFactoryTypeArguments(member, out var typeArguments))
                candidates.Add((member, typeArguments));
        if (candidates.Count != 1) return false;
        var (match, selectedTypeArguments) = candidates[0];
        physicalOwner = owner;
        physicalMethod = match.Name;
        physicalParameters = match.ParamTypeNodes;
        physicalResult = match.ReturnTypeNode;
        physicalTypeArguments = selectedTypeArguments;
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
            || !TryMembersByBirOwner(ownerToken, out var members)) return false;
        var candidates = members.Where(m => m.IsStatic == isStatic && m.Name == memberName
            && m.MethodArity == methodArity && m.ParamCount == paramCount
            && m.ParamTypeNodes != null && m.ReturnTypeNode != null).ToList();
        if (candidates.Count != 1) return false;
        var match = candidates[0];
        if (!ContainsExistential(match.ReturnTypeNode)
            && !match.ParamTypeNodes.Any(ContainsExistential)) return false;
        parameters = match.ParamTypeNodes;
        // This is a declaration ABI, so retain its owner/method generic frame. ReturnType is the best-effort static
        // projection and deliberately drops generic arguments; using it here turns e.g. List<T> into a raw List.
        result = match.ReturnTypeNode;
        return true;
    }

    bool ContainsExistential(TypeNode type) => type switch
    {
        TypeNode.Star or TypeNode.Projection => true,
        TypeNode.Fqn f => IsExistentialPhysicalOwner(f.Name)
            || (f.Args?.Any(ContainsExistential) ?? false),
        TypeNode.Nullable n => ContainsExistential(n.Of),
        TypeNode.Oblivious o => ContainsExistential(o.Of),
        TypeNode.Array a => ContainsExistential(a.Elem),
        TypeNode.ByRef b => ContainsExistential(b.Of),
        TypeNode.Ptr p => ContainsExistential(p.Of),
        TypeNode.Mod m => ContainsExistential(m.M) || ContainsExistential(m.Of),
        TypeNode.Fn fn => ContainsExistential(fn.Ret) || fn.Params.Any(ContainsExistential)
            || (fn.Recv != null && ContainsExistential(fn.Recv))
            || (fn.Ctx?.Any(ContainsExistential) ?? false),
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

    public bool TryExternalPropertyAccessor(string sourceOwner, string sourcePropertyName, string accessorKind,
        int paramCount, int methodArity, IReadOnlyList<TypeNode> accessorSignature, TypeNode[] ownerTypeArguments,
        out string physicalOwner, out string physicalPropertyName, out string physicalMethodName)
        => TryExternalPropertyAccessorCore(sourceOwner, sourcePropertyName, accessorKind,
            paramCount, methodArity, accessorSignature, ownerTypeArguments,
            out physicalOwner, out physicalPropertyName, out physicalMethodName);

    // Whether a bir2cir-resolved external MethodDef can be the non-virtual target of a synthesized forwarding body.
    // This is a concrete CLR accessibility/body fact, not default selection: the frontend has already selected the
    // implementation, and the caller uses this only to reject a representation that would call an abstract or private
    // declaration. Exact signature/return matching prevents a callable sibling overload from authorizing the target.
    public bool IsPublicConcreteInstanceMethod(string ownerFqn, string memberName, int methodArity,
        IReadOnlyList<TypeNode> parameters, TypeNode ret)
    {
        if (ownerFqn == null || memberName == null || parameters == null || ret == null
            || !TryMembersByBirOwner(ownerFqn, out var members)) return false;
        var candidates = members.Where(member => !member.IsStatic && member.IsPublic && !member.IsAbstract
                && member.Name == memberName && member.MethodArity == methodArity
                && member.ParamTypeNodes is { } ps && ps.Length == parameters.Count
                && member.ReturnTypeNode != null
                && ps.Select((type, index) => AccessorDeclarationDescribesCall(type, parameters[index])).All(x => x)
                && AccessorDeclarationDescribesCall(member.ReturnTypeNode, ret))
            .ToList();
        return candidates.Count == 1;
    }

    bool TryExternalPropertyAccessorCore(string sourceOwner, string sourcePropertyName, string accessorKind,
        int paramCount, int methodArity, IReadOnlyList<TypeNode> accessorSignature, TypeNode[] ownerTypeArguments,
        out string physicalOwner, out string physicalPropertyName, out string physicalMethodName)
    {
        physicalOwner = null;
        physicalPropertyName = null;
        physicalMethodName = null;
        if (sourceOwner == null || sourcePropertyName == null || accessorKind is not ("get" or "set")) return false;
        var bareOwner = BareOwnerFqn(sourceOwner);
        var ownerArity = ownerTypeArguments?.Length ?? 0;
        Type ownerType;
        if (TryResolveClrOwner(bareOwner, out var aliasOwner, out _))
        {
            physicalOwner = aliasOwner;
            ownerType = ResolveNetType(aliasOwner, ownerArity);
        }
        else
        {
            physicalOwner = ReflectedOwnerFqn(sourceOwner);
            ownerType = ResolveNetType(physicalOwner, ownerArity);
        }
        if (paramCount < 0 || accessorSignature == null) return false;
        string exactMethod = null;
        // A caller carrying the frontend-resolved property signature must never fall back to a name-only sibling.
        // The reference index records the exact MethodSemantics association for ordinary CLR properties as well as
        // compiler-authored carriers, so failure here means no physical binding was stated.
        var indexedOwner = TryResolveClrOwner(bareOwner, out _, out _) ? bareOwner : sourceOwner;
        if (!TryReferencedPropertyPhysicalBinding(indexedOwner, sourcePropertyName, accessorKind, paramCount,
                methodArity, accessorSignature, ownerTypeArguments,
                new HashSet<string>(StringComparer.Ordinal), out physicalPropertyName, out exactMethod))
        {
            return false;
        }
        // Metadata indexing may succeed even when reflection cannot load the referenced owner. The exact physical
        // association is already complete in that case; downstream member resolution re-anchors inherited owners.
        if (ownerType == null)
        {
            if (exactMethod == null) return false;
            physicalMethodName = exactMethod;
            return true;
        }
        var propertyName = physicalPropertyName;
        try
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic
                | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
            MethodInfo AssociatedAccessor(Type type)
            {
                var candidates = type.GetProperties(flags)
                .Where(property => property.Name == propertyName)
                .Select(property => accessorKind == "set" ? property.GetSetMethod(true) : property.GetGetMethod(true))
                .Where(method => method != null && (exactMethod == null || method.Name == exactMethod))
                .GroupBy(method => (method.Module, method.MetadataToken))
                .Select(group => group.First())
                .ToList();
                if (candidates.Count == 1) return candidates[0];
                // Overloaded CLR Property rows may share one accessor method name.  The caller already selected the
                // Kotlin declaration by its complete signature; only the physical owner/name are needed here, and a
                // single agreeing MethodDef spelling is therefore unambiguous.  Different spellings still fail closed.
                var physicalCandidates = candidates
                    .GroupBy(method => (method.DeclaringType, method.Name))
                    .Select(group => group.First()).ToList();
                return physicalCandidates.Count == 1 ? physicalCandidates[0] : null;
            }
            IEnumerable<Type> BaseTypes(Type type)
            {
                for (var current = type.BaseType; current != null; current = current.BaseType)
                    yield return current;
            }

            // Reflection does not inherit PropertyInfo across interface edges. Prefer the owner's own Property row;
            // otherwise walk the exact CLR inheritance graph and accept only one MethodSemantics association. This is
            // metadata consumption, not an accessor-name fallback.
            var method = AssociatedAccessor(ownerType);
            if (method == null)
            {
                var inheritedTypes = ownerType.IsInterface
                    ? ownerType.GetInterfaces().AsEnumerable()
                    : BaseTypes(ownerType).Concat(ownerType.GetInterfaces());
                var inherited = inheritedTypes.Select(AssociatedAccessor).Where(candidate => candidate != null)
                    .GroupBy(candidate => (candidate.Module, candidate.MetadataToken))
                    .Select(group => group.First()).ToList();
                if (inherited.Count != 1) return false;
                method = inherited[0];
            }
            // This value leaves the semantic reference index and becomes a CIR MethodImpl/call owner. Preserve the
            // declaring TypeDef's exact metadata identity, including every nested segment's arity. Returning the bare
            // lookup key here made an exact current-format owner disagree with its own reflected declaration and
            // silently dropped the interface-property MethodImpl bridge.
            physicalOwner = method.DeclaringType?.FullName ?? method.DeclaringType?.Name ?? physicalOwner;
            physicalMethodName = method.Name;
            return true;
        }
        catch { return false; }
    }

    public bool TryNullableGenericPropertySlot(string ownerFqn, string propertyName, string accessorKind,
        bool isStatic, int argCount, int methodArity, IReadOnlyList<TypeNode> accessorSignature,
        TypeNode[] ownerTypeArguments,
        out TypeNode declaredRet, out TypeNode[] declaredParams, out bool[] paramsRefused,
        bool includeUnchanged = false)
    {
        declaredRet = null;
        declaredParams = null;
        paramsRefused = null;
        if (ownerFqn == null || propertyName == null || accessorKind is not ("get" or "set")) return false;
        var path = new HashSet<string>(StringComparer.Ordinal)
            { ReferenceWalkKey(ownerFqn, ownerTypeArguments) };
        if (FindDeclaredSlot(ownerFqn, null, isStatic, argCount, methodArity, path,
                out var ret, out var parameters, out _, propertyName, accessorKind, accessorSignature,
                ownerTypeArguments, includeClosedPropertyReturn: includeUnchanged) != SlotLookup.Declared)
            return false;
        declaredRet = ret.Node;
        declaredParams = parameters.Select(parameter => parameter.Node).ToArray();
        paramsRefused = parameters.Select(parameter => parameter.Refused).ToArray();
        // The override binder also consumes the declaration's exact MethodSemantics name. A nullary getter whose
        // nullable-generic reader has no type rewrite to report must still reach that binding path; ordinary erasure
        // consumers retain the narrower historical contract and see only a slot carrying a type fact/refusal.
        return includeUnchanged
            || declaredRet != null || parameters.Any(parameter => parameter.Node != null || parameter.Refused);
    }

    // The @ClrConv numeric-conversion binding for owner.member: its conv TARGET (the callee's own return-type token, a
    // pre-lowering Kotlin FQN like `kotlin.Long`). Returns true when owner.member (arg count matched when possible) is a
    // @ClrConv-marked conversion — MemberCallSubstitution then emits
    // `{k:conv, to:<convTo>, e:<recv>}`. A conversion is
    // nullary, so arg count is always 0; the arity match is kept for symmetry with the other member lookups.
    public bool TryMemberConv(string ownerFqn, string memberName, int argCount, out TypeNode convTo)
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
    // stricter than the coarser name/arity lookups used where current BIR does not carry a complete declaration vector:
    // deciding whether a nested companion call may cross onto its aliased semantic outer must never let a differently
    // shaped bound overload capture an intrinsic-less real carrier body of the same Kotlin name. Generic arity and the
    // complete declaration vector are both part of the identity; a same-name/same-arity sibling is not evidence.
    public bool HasExactMemberClrBinding(string ownerFqn, string memberName, int methodArity,
        IReadOnlyList<TypeNode> signature) =>
        TryExactMemberClrBinding(ownerFqn, memberName, methodArity, signature, out _);

    internal bool TryExactMemberClrBinding(string ownerFqn, string memberName, int methodArity,
        IReadOnlyList<TypeNode> signature, out ExactClrMemberBinding binding)
        => TryExactMemberClrBinding(ownerFqn, memberName, methodArity, signature, null, out binding);

    // The inherited-call route closes a referenced interface declaration at its exact use-site owner. Its authored
    // parameter vector is still owner-relative in metadata, so substitute that same constructed owner before
    // comparing it with the already-closed call signature. Ordinary direct-owner callers retain the raw overload
    // above; they already express both vectors in one declaration frame.
    internal bool TryExactMemberClrBinding(string ownerFqn, string memberName, int methodArity,
        IReadOnlyList<TypeNode> signature, IReadOnlyList<TypeNode> ownerTypeArguments,
        out ExactClrMemberBinding binding)
    {
        binding = null;
        var matches = ExactBoundMembers(ownerFqn, memberName, methodArity, signature, ownerTypeArguments);
        if (matches.Count > 1)
            throw new InvalidDataException(
                $"ambiguous exact CLR member binding for {ownerFqn}.{memberName}`{methodArity} "
                + $"with {signature?.Count ?? -1} parameter(s)");
        if (matches.Count == 0) return false;
        var match = matches[0];
        binding = new ExactClrMemberBinding(match.Intrinsic, match.PropertyAccess, match.PropertyName,
            match.Conv, match.ConvTo, match.ByrefPositions, match.CountStart, match.CountEnd);
        return true;
    }

    // Callable references are reshaped before MemberCallSubstitution. Give that earlier pass the same exact-overload
    // authority, and only expose an intrinsic method name: properties/conversions have different node vocabularies and
    // cannot be represented by a method delegate without an explicit lowering of their own.
    public bool TryExactMemberIntrinsic(string ownerFqn, string memberName, int methodArity,
        IReadOnlyList<TypeNode> signature, out string intrinsic)
        => TryExactMemberIntrinsic(ownerFqn, memberName, methodArity, signature, null, out intrinsic);

    // An override edge can name a constructed semantic owner. Close the referenced declaration in that exact owner
    // frame before comparing it with the implementation's already-selected parameter vector; selecting another
    // same-name/same-count overload would split declaration rename from MethodImpl allocation.
    internal bool TryExactMemberIntrinsic(string ownerFqn, string memberName, int methodArity,
        IReadOnlyList<TypeNode> signature, IReadOnlyList<TypeNode> ownerTypeArguments, out string intrinsic)
    {
        intrinsic = null;
        if (!TryExactMemberClrBinding(ownerFqn, memberName, methodArity, signature, ownerTypeArguments, out var binding)
            || binding.Intrinsic == null) return false;
        intrinsic = binding.Intrinsic;
        return true;
    }

    List<MemberBinding> ExactBoundMembers(string ownerFqn, string memberName, int methodArity,
        IReadOnlyList<TypeNode> signature, IReadOnlyList<TypeNode> ownerTypeArguments = null)
    {
        if (memberName == null || signature == null || !TryMembersByBirOwner(ownerFqn, out var list))
            return new List<MemberBinding>();
        var candidates = list.Where(m => m.Name == memberName && m.MethodArity == methodArity
            && m.ParamTypeNodes is { } ps && ps.Length == signature.Count
            && (m.Intrinsic != null || m.PropertyName != null || m.Conv)).ToList();
        var ownerArgs = ownerTypeArguments?.ToArray();
        TypeNode[] Parameters(MemberBinding member) => ownerTypeArguments == null
            ? member.ParamTypeNodes
            : member.ParamTypeNodes.Select(type =>
                SupertypeGraph.SubstOwnerTvs(type, ownerArgs)).ToArray();
        var exact = candidates.Where(m => Parameters(m).SequenceEqual(signature)).ToList();
        if (exact.Count > 0) return exact;
        return candidates.Where(m => Parameters(m)
            .Select((p, i) => DeclarationDescribesCall(p, signature[i])).All(x => x)).ToList();
    }

    // Resolve the declaration selected by the frontend, including the generic-parameter constraint vector that is
    // part of its identity.  This is intentionally a direct declaration lookup: the inherited-default fact names the
    // owner that supplies the body, so walking to a same-shaped ancestor would replace the frontend's decision with a
    // new backend resolution.  The returned types are the declaration types before nullable-TV erasure where the
    // trusted carrier states them, and the physical declaration types otherwise.
    public bool TrySelectedMethodDeclaration(string ownerFqn, string sourceMember, int methodArity,
        IReadOnlyList<TypeNode> signature, TypeNode resolvedReturn, TypeNode[] ownerTypeArguments,
        JsonArray selectedTypeParams, out ReferencedMethodDeclaration declaration)
    {
        declaration = null;
        if (!TryMembersByBirOwner(ownerFqn, out var list)) return false;
        var matches = list.Where(member => member.SourcePropertyName == null
                && (member.SourceMethodName ?? member.Name) == sourceMember
                && member.MethodArity == methodArity
                && KotlinOverrideSlotBridge.SameMethodTypeParameterShape(
                    member.MethodTypeParams, selectedTypeParams, ownerTypeArguments, ownerTypeArguments)
                && MethodSignatureMatches(member, signature, resolvedReturn, ownerTypeArguments)
                && member.ParamTypeNodes != null && member.ReturnTypeNode != null)
            .ToList();
        if (matches.Count != 1) return false;
        var match = matches[0];
        declaration = new ReferencedMethodDeclaration(
            match.Name,
            match.ParamTypeNodes.Select((type, index) =>
                match.NullableGenericParams is { } carriers && index < carriers.Length && carriers[index] != null
                    ? carriers[index]
                    : type).ToArray(),
            match.NullableGenericRet ?? match.KotlinReturnType ?? match.ReturnTypeNode,
            match.MethodTypeParams);
        return true;
    }

    // Resolve the exact DIRECT referenced declaration named by a frontend override edge. Covariant return selection
    // cannot compare the implementation return with the slot return — their deliberate difference is why the caller
    // needs the declaration — so identity is source member/property role + method arity + parameter vector. Kotlin
    // does not overload on return type. Inherited declarations are not searched here: kotc emits an override marker
    // for each interface that contributes a direct declaration (including a synthesized redeclaration on an
    // intermediate interface), and each such CLR MethodImpl must name that exact declaring interface.
    public bool TrySelectedOverrideDeclaration(string ownerFqn, string sourceMember, string accessorKind,
        int methodArity, IReadOnlyList<TypeNode> signature, TypeNode[] ownerTypeArguments,
        JsonArray selectedTypeParams, TypeNode[] implementationOwnerTypeArguments,
        bool selectedSuspend, out ReferencedMethodDeclaration declaration)
    {
        declaration = null;
        if (!TryMembersByBirOwner(ownerFqn, out var list)) return false;
        var matches = list.Where(member => !member.IsStatic && !member.IsPropertyBridge
                && (accessorKind == null
                    ? member.SourcePropertyName == null
                        && (member.SourceMethodName ?? member.Name) == sourceMember
                    : member.SourcePropertyName == sourceMember && member.AccessorKind == accessorKind)
                && member.MethodArity == methodArity
                && member.Suspend == selectedSuspend
                && KotlinOverrideSlotBridge.SameMethodTypeParameterShape(
                    member.MethodTypeParams, selectedTypeParams,
                    ownerTypeArguments, implementationOwnerTypeArguments)
                && AccessorSignatureMatches(member, signature, ownerTypeArguments)
                && member.ParamTypeNodes != null && member.ReturnTypeNode != null
                && (!selectedSuspend || member.SuspendReturnType != null))
            .ToList();
        if (matches.Count != 1) return false;
        var match = matches[0];
        declaration = new ReferencedMethodDeclaration(
            match.Name,
            match.ParamTypeNodes.Select((type, index) =>
                match.NullableGenericParams is { } carriers && index < carriers.Length && carriers[index] != null
                    ? carriers[index]
                    : type).ToArray(),
            selectedSuspend
                ? match.SuspendReturnType
                : match.NullableGenericRet ?? match.KotlinReturnType ?? match.ReturnTypeNode,
            match.MethodTypeParams);
        return true;
    }

    // FULL-SIGNATURE @ClrIntrinsic lookup for the member-STRIP: is owner.name(paramKeys) a bound stub? Matches the
    // @ClrIntrinsic member whose canonicalized param types equal the emitted method's — so `StringBuilder.append(Char)`
    // (@ClrIntrinsic, dropped) is distinguished from `append(CharSequence?)` (rule-3, kept), which share name+arity.
    public bool IsBoundStub(string ownerFqn, string memberName, IReadOnlyList<TypeKey> birParamKeys)
    {
        if (!TryMembersByBirOwner(ownerFqn, out var list)) return false;
        return list.Any(m => m.Name == memberName && m.Intrinsic != null && m.ParamTypeNodes != null
            && m.ParamTypeNodes.Length == birParamKeys.Count
            && m.ParamTypeNodes.Select(ParamKey).SequenceEqual(birParamKeys));
    }

    // Canonicalize one already-parsed Fqn identity. No string type grammar is recognized here: wrappers and generic
    // arguments are represented by TypeNode/System.Type and dispatched structurally before this leaf is reached.
    static TypeKey ParamKeyFqn(string t, bool relaxed = false)
    {
        var exact = t switch
        {
            "kotlin.Byte" or "System.SByte" or "sbyte" => new TypeKey(TypeKeyKind.Int8),
            "kotlin.Short" or "System.Int16" or "short" => new TypeKey(TypeKeyKind.Int16),
            "kotlin.Int" or "System.Int32" or "int" => new TypeKey(TypeKeyKind.Int32),
            "kotlin.Long" or "System.Int64" or "long" => new TypeKey(TypeKeyKind.Int64),
            "kotlin.Float" or "System.Single" or "float" => new TypeKey(TypeKeyKind.Float32),
            "kotlin.Double" or "System.Double" or "double" => new TypeKey(TypeKeyKind.Float64),
            "kotlin.Boolean" or "System.Boolean" or "bool" => new TypeKey(TypeKeyKind.Boolean),
            "kotlin.Char" or "System.Char" or "char" => new TypeKey(TypeKeyKind.Char),
            "kotlin.String" or "System.String" or "string" => new TypeKey(TypeKeyKind.String),
            "kotlin.Unit" or "System.Void" or "void" => new TypeKey(TypeKeyKind.Void),
            "kotlin.Any" or "System.Object" or "object" => new TypeKey(TypeKeyKind.Object),
            // Unsigned scalars, folded like every other primitive: the specialized ARRAYS were already folded below, but
            // the element types were not, so a `UInt` parameter keyed as `kotlin.UInt` from a pre-lowering call site and
            // as `uint` from a reference assembly — two spellings of one type that no signature compare could match.
            "kotlin.UByte" or "System.Byte" or "byte" => new TypeKey(TypeKeyKind.UInt8),
            "kotlin.UShort" or "System.UInt16" or "ushort" => new TypeKey(TypeKeyKind.UInt16),
            "kotlin.UInt" or "System.UInt32" or "uint" => new TypeKey(TypeKeyKind.UInt32),
            "kotlin.ULong" or "System.UInt64" or "ulong" => new TypeKey(TypeKeyKind.UInt64),
            // Primitive-array class spellings (kotc lowers to a structured array node, but the ref.dll may reflect the kotlin.IntArray
            // class) -> the same array key so a top-level `sort(IntArray)`@ClrIntrinsic matches by signature.
            "kotlin.IntArray" => new TypeKey(TypeKeyKind.Array, Element: new(TypeKeyKind.Int32)),
            "kotlin.LongArray" => new TypeKey(TypeKeyKind.Array, Element: new(TypeKeyKind.Int64)),
            "kotlin.ByteArray" => new TypeKey(TypeKeyKind.Array, Element: new(TypeKeyKind.Int8)),
            "kotlin.ShortArray" => new TypeKey(TypeKeyKind.Array, Element: new(TypeKeyKind.Int16)),
            "kotlin.FloatArray" => new TypeKey(TypeKeyKind.Array, Element: new(TypeKeyKind.Float32)),
            "kotlin.DoubleArray" => new TypeKey(TypeKeyKind.Array, Element: new(TypeKeyKind.Float64)),
            "kotlin.BooleanArray" => new TypeKey(TypeKeyKind.Array, Element: new(TypeKeyKind.Boolean)),
            "kotlin.CharArray" => new TypeKey(TypeKeyKind.Array, Element: new(TypeKeyKind.Char)),
            // Unsigned specialized arrays (#53): native System.Byte[]/UInt16[]/UInt32[]/UInt64[]. Same array key as
            // their element token so an @ClrIntrinsic signature over the ref.dll spelling matches.
            "kotlin.UByteArray" => new TypeKey(TypeKeyKind.Array, Element: new(TypeKeyKind.UInt8)),
            "kotlin.UShortArray" => new TypeKey(TypeKeyKind.Array, Element: new(TypeKeyKind.UInt16)),
            "kotlin.UIntArray" => new TypeKey(TypeKeyKind.Array, Element: new(TypeKeyKind.UInt32)),
            "kotlin.ULongArray" => new TypeKey(TypeKeyKind.Array, Element: new(TypeKeyKind.UInt64)),
            _ => new TypeKey(TypeKeyKind.Named, StripGenericArity(t)),
        };
        return relaxed && exact.Kind == TypeKeyKind.Named ? new TypeKey(TypeKeyKind.Reference) : exact;
    }

    static TypeKey ParamKey(TypeNode t, bool relaxed) => t switch
    {
        TypeNode.ByRef b => new TypeKey(TypeKeyKind.ByRef, Element: ParamKey(b.Of, relaxed)),
        TypeNode.Array a => new TypeKey(TypeKeyKind.Array, Element: ParamKey(a.Elem, relaxed)),
        TypeNode.Nullable n when relaxed && !IsValueKey(ParamKey(n.Of, false)) => ParamKey(n.Of, true),
        TypeNode.Nullable n => new TypeKey(TypeKeyKind.Nullable, Element: ParamKey(n.Of, relaxed)),
        TypeNode.Fn fn => new TypeKey(fn.Suspend ? TypeKeyKind.Object : TypeKeyKind.Function),
        TypeNode.Tv => new TypeKey(TypeKeyKind.GenericParameter),
        TypeNode.Fqn f => ParamKeyFqn(f.Name, relaxed),
        _ => new TypeKey(TypeKeyKind.Object),
    };

    static TypeKey ParamKey(Type type, bool relaxed)
    {
        if (type.IsByRef) return new TypeKey(TypeKeyKind.ByRef, Element: ParamKey(type.GetElementType()!, relaxed));
        if (type.IsArray) return new TypeKey(TypeKeyKind.Array, Element: ParamKey(type.GetElementType()!, relaxed));
        if (type.IsGenericParameter) return new TypeKey(TypeKeyKind.GenericParameter);
        if (IsDelegate(type)) return new TypeKey(TypeKeyKind.Function);
        if (type.IsConstructedGenericType)
        {
            var def = type.GetGenericTypeDefinition();
            if (IsNullableDefinition(def))
            {
                var inner = type.GetGenericArguments()[0];
                if (relaxed && !IsValueKey(ParamKey(inner, false))) return ParamKey(inner, true);
                return new TypeKey(TypeKeyKind.Nullable, Element: ParamKey(inner, relaxed));
            }
            return ParamKeyFqn(StripGenericArity(def.FullName ?? def.Name), relaxed);
        }
        return ParamKeyFqn(PrimitiveBirName(type) ?? StripGenericArity(type.FullName ?? type.Name), relaxed);
    }

    public static TypeKey ParamKey(TypeNode t) => ParamKey(t, relaxed: false);

    // ParamKey off a structured JSON type slot.
    public static TypeKey ParamKey(JsonNode typeSlot) =>
        TypeJson.Read(typeSlot) is { } type ? ParamKey(type) : null;

    public static SignatureKey SignatureKeyOf(JsonArray signature, bool relaxed = false) =>
        new(signature.Select(type => ParamKey(TypeNode.Parse(type!.ToJsonString()), relaxed)));

    public static TypeKey ReceiverParamKey(JsonNode typeSlot)
        => ReceiverParamKey(TypeNode.Parse(typeSlot.ToJsonString()));

    public static TypeKey ReceiverParamKey(TypeNode type)
    {
        if (type is TypeNode.Nullable nullable) type = nullable.Of;
        return ParamKey(type, relaxed: false);
    }

    static TypeKey ReceiverParamKey(Type type)
    {
        if (type.IsConstructedGenericType && IsNullableDefinition(type.GetGenericTypeDefinition()))
            type = type.GetGenericArguments()[0];
        return ParamKey(type, relaxed: false);
    }

    // A top-level fun (file-class static, called as `callStatic owner=null`) bound by @ClrIntrinsic to a
    // fully-qualified BCL static (e.g. clrTimestamp -> "System.Diagnostics.Stopwatch.GetTimestamp").
    public bool TryTopLevelIntrinsic(string funName, out string fqStatic) =>
        _topLevelIntrinsics.TryGetValue(funName, out fqStatic);

    // Overload-disambiguated variant: a top-level @ClrIntrinsic name that binds to DIFFERENT BCL statics per overload
    // — kotlin.math `sqrt`/`abs`/`pow`/... -> System.Math.* for Double/Int/Long but System.MathF.* for Float. Keyed by
    // name plus the structural signature key resolves the EXACT intrinsic overload (and a non-intrinsic sibling,
    // e.g. `Double.pow(Int)`, correctly misses and falls through to its real Kotlin body).
    public bool TryTopLevelIntrinsicBySig(string funName, SignatureKey sigKey, out string fqStatic) =>
        _topLevelIntrinsicsBySig.TryGetValue((funName, sigKey), out fqStatic);

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
    public bool TryResolveStaticMemberSignature(string ownerFqn, string name, int methodArity, bool isStatic,
        IReadOnlyList<TypeNode> callSignature, out TypeNode[] declarationSignature) =>
        TryResolveStaticMemberSignature(ownerFqn, name, methodArity, isStatic, callSignature, null,
            out declarationSignature, out _, out _);

    /// <summary>
    /// As above, and also hands back the DECLARATION it selected (#370). The parameter vector was the only
    /// thing this ever returned, which left the caller describing a member it had in its hand — and a
    /// description has to be turned back into a member by whoever reads it.
    /// </summary>
    public bool TryResolveStaticMemberSignature(string ownerFqn, string name, int methodArity, bool isStatic,
        IReadOnlyList<TypeNode> callSignature, TypeNode[] ownerTypeArguments, out TypeNode[] declarationSignature,
        out MethodInfo declaration, out Type declaringOwner)
    {
        declarationSignature = null;
        declaration = null;
        declaringOwner = null;
        if (ownerFqn == null || name == null || callSignature == null)
            return false;
        var bareOwner = BareOwnerFqn(ownerFqn);
        var ownerArity = ownerTypeArguments is { Length: > 0 }
            ? ownerTypeArguments.Length
            : _ownerArity.TryGetValue(bareOwner, out var oa) ? oa : 0;
        // A hoisted alias helper exists only in the assembly that ships it — the reference twin carries the alias
        // implementation this pass replaced, never the static it was hoisted into — so the reference surface has no
        // name for it and never will. Read the declaration from the shipped twin, the assembly the call links against.
        // An already-physical owner is authoritative as written. A semantic dotted nested owner is projected through
        // the trusted DotKt type index to its exact `Outer`N+Inner`M` metadata identity — never guessed by trying
        // separator/arity combinations. The same exact name is tried in both twins because a shipped-only helper can
        // legitimately be absent from the reference surface.
        var physicalSpelling = ownerFqn.Contains('`') || ownerFqn.Contains('+');
        string exactOwner = null;
        var hasExactOwner = !physicalSpelling && TryExactPhysicalTypeName(ownerFqn, ownerArity, out exactOwner);
        if (hasExactOwner && exactOwner == null)
            throw new InvalidOperationException(
                $"ambiguous CLR metadata identity for nested type '{bareOwner}' with flattened arity {ownerArity}");
        // A bare semantic name with generic arguments must not win a same-named non-generic declaration
        // (`EventHandler` beside `EventHandler<T>`). Only a spelling that already encodes physical arity/nesting is
        // authoritative before the arity-aware probes.
        var owner = hasExactOwner
            ? ResolveRefType(exactOwner) ?? PhysicalTypeNamed(exactOwner)
            : (physicalSpelling || ownerArity == 0 ? ResolveRefType(ownerFqn) : null)
                ?? ResolveRefType(bareOwner, ownerArity)
                ?? PhysicalTypeNamed(ownerFqn)
                ?? PhysicalTypeNamed(bareOwner, ownerArity);
        if (owner == null)
            return false;
        // Instance as well as static: a call on a referenced Kotlin owner — every method on an `object`, reached
        // through its INSTANCE — is as external as a static one, and the receiver rides the node's `recv` rather
        // than the signature, so the two searches differ only in which members they look at.
        var candidates = owner.GetMethods(BindingFlags.Public | BindingFlags.NonPublic
                | BindingFlags.Static | BindingFlags.Instance)
            .Where(m => m.IsStatic == isStatic && m.Name == name && m.GetGenericArguments().Length == methodArity
                && m.GetParameters().Length == callSignature.Count
                && (m.IsPublic || m.IsFamily || m.IsFamilyOrAssembly))
            .Select(m => (method: m, ps: m.GetParameters().Select(p => DeclarationTypeNode(p.ParameterType)).ToArray()))
            .Where(c => c.ps.All(p => p != null))
            .ToList();
        // A member the owner reaches through an interface it implements is declared on that interface, not on the
        // owner — ArrayDeque<T> states addLast itself but Add, Insert, RemoveAt and set_Item come from IList<T>.
        // The reference is to the declaring type either way, so when the owner answers for nothing, ask the
        // interfaces it implements. Only when the owner itself has no candidate, so an owner-declared member is
        // never displaced by an interface one.
        if (candidates.Count == 0)
            candidates = owner.GetInterfaces()
                .SelectMany(i => i.GetMethods(BindingFlags.Public | BindingFlags.Instance))
                .Where(m => m.IsStatic == isStatic && m.Name == name && m.GetGenericArguments().Length == methodArity
                    && m.GetParameters().Length == callSignature.Count
                    && (m.IsPublic || m.IsFamily || m.IsFamilyOrAssembly))
                .Select(m => (method: m, ps: m.GetParameters().Select(p => DeclarationTypeNode(p.ParameterType)).ToArray()))
                .Where(c => c.ps.All(p => p != null))
                .ToList();
        // The call may already have been renamed to its PHYSICAL spelling while the reference surface still
        // describes the Kotlin one — ArrayDeque implements MutableList<T> there and has no Add at all, though the
        // assembly that ships it does. When the reference surface knows the owner but not the member, ask the
        // shipped twin, which is the assembly the call links against.
        if (candidates.Count == 0 && PhysicalTypeNamed(bareOwner, ownerArity) is { } shipped && shipped != owner)
        {
            owner = shipped;
            candidates = shipped.GetMethods(BindingFlags.Public | BindingFlags.NonPublic
                    | BindingFlags.Static | BindingFlags.Instance)
                .Where(m => m.IsStatic == isStatic && m.Name == name && m.GetGenericArguments().Length == methodArity
                    && m.GetParameters().Length == callSignature.Count
                    && (m.IsPublic || m.IsFamily || m.IsFamilyOrAssembly))
                .Select(m => (method: m, ps: m.GetParameters().Select(p => DeclarationTypeNode(p.ParameterType)).ToArray()))
                .Where(c => c.ps.All(p => p != null))
                .ToList();
        }
        if (candidates.Count == 0)
            return false;

        TypeNode[] ConstructedParameters((MethodInfo method, TypeNode[] ps) candidate) =>
            ownerTypeArguments == null
                ? candidate.ps
                : candidate.ps.Select(type => SupertypeGraph.SubstOwnerTvs(type, ownerTypeArguments)).ToArray();
        // Depending on which frontend/reference path produced the call, its vector can remain in the declaration
        // frame (`type#0`) or already be closed by the constructed owner (`String`). Both exactly describe the same
        // MethodDef. Preserve declaration-frame lookup when it already selects a candidate; only close the owner
        // frame when that lookup answers for nothing. Combining both result sets can make different overloads look
        // equally applicable even though the incoming vector unambiguously uses the declaration frame.
        var exact = candidates.Where(c => c.ps.SequenceEqual(callSignature)).ToList();
        if (exact.Count == 0)
            exact = candidates.Where(c => ConstructedParameters(c).SequenceEqual(callSignature)).ToList();
        var compatible = exact;
        if (compatible.Count == 0)
            compatible = candidates.Where(c => c.ps
                .Select((p, i) => DeclarationDescribesCall(p, callSignature[i])).All(x => x)).ToList();
        if (compatible.Count == 0)
            compatible = candidates.Where(c => ConstructedParameters(c)
                .Select((p, i) => DeclarationDescribesCall(p, callSignature[i])).All(x => x)).ToList();
        var source = compatible.Count > 0 ? compatible : candidates;
        // Type.GetMethods includes inherited declarations.  When this exact owner declares a matching member, normal
        // CLR member lookup selects that declaration (including a `new` forwarding slot) rather than treating the
        // hidden base member as a second overload.  Keep inherited candidates only when the stated owner has no
        // matching declaration of its own.  This choice is based on metadata ownership, not source names or order.
        var declaredHere = source.Where(c => c.method.DeclaringType == owner).ToList();
        if (declaredHere.Count > 0) source = declaredHere;
        // Declarations that are the SAME MEMBER collapse to one — the duplicate expect/actual rows a merged
        // stdlib produces. Sameness is judged on the physical metadata identity, not on the rendered parameter
        // vector: that vector strips generic arity, flattens `+` nesting, drops array rank and knows nothing of
        // custom modifiers, so grouping by it would merge declarations that ARE different members and then
        // hand one of them over as an exact identity. Two distinct members still refuse, which is the point.
        var shapes = source
            .GroupBy(c => c.method.DeclaringType?.FullName + "|" + MetadataSignatureKey(c.method), StringComparer.Ordinal)
            .Select(g => g.First())
            .ToList();
        if (shapes.Count != 1)
            return false;
        declarationSignature = shapes[0].ps;
        declaration = shapes[0].method;
        declaringOwner = owner;
        return true;
    }

    // A member's physical signature as metadata states it: the parameter and return types by their own names,
    // with by-ref, pointer, array rank and custom modifiers intact. Used to decide whether two declarations are
    // one member; the document's rendered vector cannot answer that because it is lossy in exactly those places.
    static string MetadataSignatureKey(MethodInfo method)
    {
        var sb = new StringBuilder();
        // The calling convention is part of the signature: a vararg member is not the fixed-arity one beside it.
        sb.Append(method.Name).Append('`').Append(method.GetGenericArguments().Length)
          .Append('[').Append(method.CallingConvention).Append("](");
        foreach (var p in method.GetParameters())
        {
            AppendMetadataSlot(sb, p.ParameterType, p.GetRequiredCustomModifiers(), p.GetOptionalCustomModifiers());
            sb.Append(',');
        }
        sb.Append("):");
        // The RETURN carries modifiers too — `ref readonly` is modreq(InAttribute) there and nowhere else — so
        // omitting them merges a member with the one it differs from only in that.
        var ret = method.ReturnParameter;
        AppendMetadataSlot(sb, method.ReturnType,
            ret?.GetRequiredCustomModifiers() ?? Type.EmptyTypes,
            ret?.GetOptionalCustomModifiers() ?? Type.EmptyTypes);
        return sb.ToString();
    }

    static void AppendMetadataSlot(StringBuilder sb, Type t, Type[] required, Type[] optional)
    {
        AppendMetadataType(sb, t);
        foreach (var m in required) sb.Append(" modreq(").Append(m.FullName).Append(')');
        foreach (var m in optional) sb.Append(" modopt(").Append(m.FullName).Append(')');
    }

    static void AppendMetadataType(StringBuilder sb, Type t)
    {
        if (t == null) { sb.Append("<null>"); return; }
        if (t.IsByRef) { AppendMetadataType(sb, t.GetElementType()); sb.Append('&'); return; }
        if (t.IsPointer) { AppendMetadataType(sb, t.GetElementType()); sb.Append('*'); return; }
        if (t.IsArray)
        {
            AppendMetadataType(sb, t.GetElementType());
            int rank; try { rank = t.GetArrayRank(); } catch { rank = 1; }
            bool sz; try { sz = t.IsSZArray; } catch { sz = rank == 1; }
            // `T[]` and `T[*]` are different types at the same rank, so the key has to say which.
            sb.Append(sz ? "[]" : "[*" + rank + "]");
            return;
        }
        if (t.IsGenericParameter) { sb.Append(t.DeclaringMethod != null ? "!!" : "!").Append(t.GenericParameterPosition); return; }
        sb.Append(t.FullName ?? t.Name);
        if (t.IsGenericType && !t.IsGenericTypeDefinition)
        {
            sb.Append('<');
            foreach (var a in t.GetGenericArguments()) { AppendMetadataType(sb, a); sb.Append(','); }
            sb.Append('>');
        }
    }

    // BIR's resolved Kotlin descriptor can retain semantic nullability that the metadata-only ref declaration has
    // already erased (`T?` parameter -> !!T, function return T? -> object). Compare only those ABI-equivalent seams;
    // nominal/function shape and Tv scope/index remain exact so sibling overloads cannot collapse.
    static bool DeclarationDescribesCall(TypeNode declaration, TypeNode call)
    {
        if (declaration == call) return true;
        // A star projection states no bound, so it describes whatever the declaration says — the erasure the
        // reference twin shows as `object` is one such answer, not a different type. Without this a
        // Comparable<*> selector could not meet compareBy's Comparable<object> parameter.
        if (call is TypeNode.Star) return true;
        if (declaration is TypeNode.Projection dp)
            return DeclarationDescribesCall(dp.Of, call);
        if (call is TypeNode.Projection cp)
            return DeclarationDescribesCall(declaration, cp.Of);
        if (declaration is TypeNode.Oblivious dOb)
            return DeclarationDescribesCall(dOb.Of, call);
        if (call is TypeNode.Oblivious cOb)
            return DeclarationDescribesCall(declaration, cOb.Of);
        // Reflection's declaration vocabulary can retain Nullable<T> as an ordinary constructed FQN while the
        // frontend descriptor uses BIR's structural nullable wrapper. They are one CLR value-type slot. Normalize
        // this seam before the reference-nullability rules below; otherwise a derived same-arity overload can become
        // the sole fallback candidate even though the frontend selected an inherited declaration.
        if (declaration is TypeNode.Fqn { Name: "System.Nullable", Args.Length: 1 } physicalNullable
            && call is TypeNode.Nullable callNullable)
            return DeclarationDescribesCall(physicalNullable.Args[0], callNullable.Of);
        if (declaration is TypeNode.Nullable declarationNullable
            && call is TypeNode.Fqn { Name: "System.Nullable", Args.Length: 1 } physicalCallNullable)
            return DeclarationDescribesCall(declarationNullable.Of, physicalCallNullable.Args[0]);
        // A method variable may already be erased to object in the reflected MethodDef, at the head or recursively
        // inside another physical type (Result<object> versus the selected Kotlin Result<T>, for example). Recognize
        // that stated physical boundary before nullable recursion. Identity has already selected this MethodDef, so
        // this validates its erasure rather than admitting the object slot as an overload-selection wildcard.
        if (declaration is TypeNode.Fqn { Args: null } tvErasure
            && ParamKey(tvErasure).Kind == TypeKeyKind.Object
            && call is TypeNode.Tv or TypeNode.Nullable { Of: TypeNode.Tv })
            return true;
        if (call is TypeNode.Nullable cNull)
        {
            // Nullability of a REFERENCE slot is not part of CLR identity, so a call stating T? still describes a
            // declaration of T. A nullable VALUE slot is different: `Int?` is System.Nullable<Int32>, not Int32, and
            // must not let an intrinsic `f(Int)` capture a frontend-selected real-body `f(Int?)`. Type variables keep
            // the historical erasure seam (`T?` may be reflected as T), and arrays are reference types even when their
            // element is a value type.
            if (declaration is not TypeNode.Nullable && !IsValueKey(ParamKey(cNull.Of)))
                return DeclarationDescribesCall(declaration, cNull.Of);
        }
        // A Kotlin primitive-array CLASS and the CLR array it IS are one type under two spellings, and which one
        // arrives here depends only on how far the call has been lowered — a call still stating kotlin.IntArray
        // meets a declaration the reference twin reflects as int[]. The kinds differ, so no same-kind arm below
        // can see them, and ParamKey is already the single place that knows the two are the same type.
        if (declaration is TypeNode.Array != call is TypeNode.Array && ParamKey(declaration) == ParamKey(call))
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
            return FunctionDeclarationDescribesCall(dfn, cfn, DeclarationDescribesCall);
        return false;
    }

    // A function type's Recv/Params split is Kotlin vocabulary, not part of its CLR delegate identity. The reference
    // assembly can state only Action<P>/Func<P,R>, so reflection reconstructs P as an ordinary parameter even when
    // the frontend-selected Kotlin declaration stated P.() -> R. Compare the one physical argument sequence shared
    // by both representations; keeping the source-level split here makes a selected overload unmatchable whenever a
    // sibling overload leaves more than one same-name candidate standing.
    static bool FunctionDeclarationDescribesCall(TypeNode.Fn declaration, TypeNode.Fn call,
        Func<TypeNode, TypeNode, bool> describes)
    {
        if (declaration.Suspend != call.Suspend) return false;
        if (declaration.Clr != null && call.Clr != null && declaration.Clr != call.Clr) return false;
        var declarationParameters = declaration.DelegateParams;
        var callParameters = call.DelegateParams;
        return declarationParameters.Length == callParameters.Length
            && describes(declaration.Ret, call.Ret)
            && declarationParameters.Select((parameter, index) =>
                describes(parameter, callParameters[index])).All(matches => matches);
    }

    // A declaration-identity carrier preserves Kotlin's pre-representation signature. Keep that contract exact,
    // except for a function type's receiver/parameter partition: both spellings denote the same CLR delegate ABI and
    // dll2klib may restore either one. Apply that normalization recursively so nested generic/function slots validate
    // without turning the validation into a second overload-selection pass.
    static bool SemanticDeclarationDescribesCall(TypeNode declaration, TypeNode call)
    {
        if (declaration == call) return true;
        // Either side may already have crossed a representation pass (for example kotlin.CharSequence versus its
        // dotkt$CharSequence CLR view). Identity has already selected the MethodDef, so this is an ABI-equivalence
        // validation rather than candidate matching; accept the established equivalence in either direction.
        if (DeclarationDescribesCall(declaration, call) || DeclarationDescribesCall(call, declaration)) return true;
        if (declaration is TypeNode.Projection declarationProjection)
            return SemanticDeclarationDescribesCall(
                declarationProjection.Of,
                call is TypeNode.Projection callProjection ? callProjection.Of : call);
        if (call is TypeNode.Projection callProjectionOnly)
            return SemanticDeclarationDescribesCall(declaration, callProjectionOnly.Of);
        if (declaration is TypeNode.Fqn df && call is TypeNode.Fqn cf)
            return df.Name == cf.Name && df.Args != null && cf.Args != null
                && df.Args.Length == cf.Args.Length
                && df.Args.Select((type, index) =>
                    SemanticDeclarationDescribesCall(type, cf.Args[index])).All(matches => matches);
        if (declaration is TypeNode.Nullable dn && call is TypeNode.Nullable cn)
            return SemanticDeclarationDescribesCall(dn.Of, cn.Of);
        if (declaration is TypeNode.Oblivious dob && call is TypeNode.Oblivious cob)
            return SemanticDeclarationDescribesCall(dob.Of, cob.Of);
        if (declaration is TypeNode.Array da && call is TypeNode.Array ca)
            return da.Rank == ca.Rank && da.SzArray == ca.SzArray
                && SemanticDeclarationDescribesCall(da.Elem, ca.Elem);
        if (declaration is TypeNode.ByRef db && call is TypeNode.ByRef cb)
            return SemanticDeclarationDescribesCall(db.Of, cb.Of);
        if (declaration is TypeNode.Ptr dp && call is TypeNode.Ptr cp)
            return SemanticDeclarationDescribesCall(dp.Of, cp.Of);
        if (declaration is TypeNode.Mod dm && call is TypeNode.Mod cm)
            return dm.Req == cm.Req
                && SemanticDeclarationDescribesCall(dm.M, cm.M)
                && SemanticDeclarationDescribesCall(dm.Of, cm.Of);
        if (declaration is TypeNode.Fn dfn && call is TypeNode.Fn cfn)
            return FunctionDeclarationDescribesCall(dfn, cfn, SemanticDeclarationDescribesCall);
        return false;
    }

    public bool TryResolveTopLevelStatic(string funName, string recvKey, TypeKey firstParamKey, out string owner)
    {
        owner = null;
        if (!_topLevelStatics.TryGetValue(funName, out var cands) || cands.Count == 0) return false;
        if (cands.Count == 1) { owner = cands[0].Owner; return true; }
        // When the coarse recvKey collapsed an ARRAY receiver to "[]" it is lossy — IntArray/CharArray/... AND the
        // unsigned specialized arrays AND the generic Array<T> all share "[]", so the plain recvKey loop below would pin
        // the FIRST array overload (the signed generic `toList<T>(T[])`) for EVERY array call, miscompiling an unsigned
        // `ubyteArrayOf(..).toList()` onto _ArraysKt's uninstantiated generic. The fine first-param ParamKey pins the
        // exact file-class+overload (UByteArray -> Array(UInt8) -> UArraysKt). Only "[]" is lossy; a normal owner recvKey
        // is already exact, so the extra discriminator applies only to the lossy array key. (#153)
        if (recvKey == "[]" && firstParamKey != null)
            foreach (var c in cands)
                if (c.ParamKey == firstParamKey) { owner = c.Owner; return true; }
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

    public bool TryResolveTopLevelProperty(string propertyName, string accessorKind, string preferredOwner,
        int paramCount, int methodArity, IReadOnlyList<TypeNode> accessorSignature,
        out string owner, out string physicalMethodName)
    {
        owner = null;
        physicalMethodName = null;
        if (propertyName == null || accessorKind is not ("get" or "set")) return false;
        // A projected KLIB property carries its producer file class explicitly. That dispatch identity is stronger
        // than a compilation-wide same-name search and must not be discarded merely because the call remains on the
        // owner-null top-level substitution axis. When no owner was available, the exact source property/role and
        // complete frontend-resolved signature still select from authoritative MethodSemantics associations.
        var owners = preferredOwner == null
            ? _membersByOwner.Where(pair => IsFileClassOwner(pair.Key))
            : _membersByOwner.Where(pair =>
                DottedFqn(BareOwnerFqn(pair.Key)) == DottedFqn(BareOwnerFqn(preferredOwner))
                && IsFileClassOwner(pair.Key));
        var candidates = owners
            .SelectMany(pair => pair.Value.Where(member => member.IsStatic
                && !member.IsPropertyBridge && member.SourcePropertyName == propertyName
                && member.AccessorKind == accessorKind
                && member.ParamCount == paramCount && member.MethodArity == methodArity
                && AccessorSignatureMatches(member, accessorSignature, Array.Empty<TypeNode>()))
                .Select(member => (Owner: pair.Key, Member: member)))
            .ToList();
        var identities = candidates.Select(candidate => (candidate.Owner, candidate.Member.Name))
            .Distinct().ToList();
        if (identities.Count != 1) return false;
        owner = identities[0].Owner;
        physicalMethodName = identities[0].Name;
        return true;
    }

    // The declared RETURN type of a bound member (owner.name, matched by arg count then by name), from the ref.dll —
    // used by StaticType (#59) to recover a call / field read whose BIR node carries NO `ret` (kotc emits `ret` only for
    // a GENERIC call). null when the owner/member is unknown or its return type was not structurable (a delegate/gp).
    // `firstParamKey` (the call's structural first-argument key) disambiguates a same-name/same-arity overload set that a coarse
    // name+count match would resolve to the WRONG sibling: the primitive-array `IntArray.toList` (first param `int[]` ->
    // Array(Int32), returning `List<Int>`) vs the generic `Array<out T>.toList` (first param Array(GenericParameter),
    // returning `List<Tv>`) — both in ArraysKt. Picking the generic sibling's `List<Tv>` leaves the element unbound and
    // erases it to `object`, so `println(intArrayOf(1,2).toList())` wrapped in clrCollToString<object> then rejects the
    // `IReadOnlyList<int32>` stack (#153). PREFER the first-param-key match; fall back to the coarse first-match when no
    // key is supplied or none matches (monotone — only previously-arbitrary picks change).
    public TypeNode TryMemberReturn(string ownerFqn, string name, int argCount, TypeKey firstParamKey = null)
    {
        if (ownerFqn == null || !TryMembersByBirOwner(ownerFqn, out var list)) return null;
        if (firstParamKey != null
            && list.FirstOrDefault(b => b.Name == name && b.ParamCount == argCount && b.ReturnType != null
                    && b.ParamTypeNodes is { Length: > 0 } && ReceiverParamKey(b.ParamTypeNodes[0]) == firstParamKey) is { } keyed)
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
            || !TryMembersByBirOwner(ownerFqn, out var list))
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
        out TypeNode declaredRet, out TypeNode[] declaredParams, out bool[] paramsRefused,
        bool includeUnchanged = false, IReadOnlyList<TypeNode> resolvedSignature = null,
        TypeNode resolvedReturn = null, TypeNode[] ownerTypeArguments = null,
        JsonArray selectedTypeParams = null, TypeNode[] selectedOwnerTypeArguments = null)
    {
        declaredRet = null;
        declaredParams = null;
        paramsRefused = null;
        if (ownerFqn == null || name == null) return false;
        var path = new HashSet<string>(StringComparer.Ordinal)
            { ReferenceWalkKey(ownerFqn, ownerTypeArguments) };
        if (FindDeclaredSlot(ownerFqn, name, isStatic, argCount, methodArity, path, out var ret, out var ps,
                out _,
                methodSignature: resolvedSignature, methodReturn: resolvedReturn,
                ownerTypeArguments: ownerTypeArguments, includeUnchangedMethod: includeUnchanged,
                selectedTypeParams: selectedTypeParams,
                selectedOwnerTypeArguments: selectedOwnerTypeArguments)
            != SlotLookup.Declared)
            return false;
        declaredRet = ret.Node;
        declaredParams = ps.Select(p => p.Node).ToArray();
        paramsRefused = ps.Select(p => p.Refused).ToArray();
        // Declared, but with nothing this reader may state about it — the caller has no use for that. A REFUSAL is
        // something to state, though: it is what stops the caller reaching for the descriptor instead.
        return declaredRet != null || ps.Any(p => p.Node != null || p.Refused);
    }

    // Override-slot consumers need the same exact declaration selection as TryNullableGenericSlot, plus the selected
    // MethodDef identity. This matters when the Kotlin source name and CLR slot name differ. The identity is produced
    // by the same walk and therefore cannot drift from the slot facts; no caller re-resolves it from the physical
    // signature.
    public bool TrySelectedNullableGenericSlot(string ownerFqn, string name, bool isStatic, int argCount,
        int methodArity, IReadOnlyList<TypeNode> resolvedSignature, TypeNode resolvedReturn,
        TypeNode[] ownerTypeArguments, JsonArray selectedTypeParams, TypeNode[] selectedOwnerTypeArguments,
        out TypeNode declaredRet, out TypeNode[] declaredParams, out bool[] paramsRefused,
        out string physicalMember, out JsonArray declarationTypeParams)
    {
        declaredRet = null;
        declaredParams = null;
        paramsRefused = null;
        physicalMember = null;
        declarationTypeParams = null;
        if (ownerFqn == null || name == null) return false;
        var path = new HashSet<string>(StringComparer.Ordinal)
            { ReferenceWalkKey(ownerFqn, ownerTypeArguments) };
        if (FindDeclaredSlot(ownerFqn, name, isStatic, argCount, methodArity, path,
                out var ret, out var parameters, out var declaration,
                ownerTypeArguments: ownerTypeArguments, includeUnchangedMethod: true,
                methodSignature: resolvedSignature, methodReturn: resolvedReturn,
                selectedTypeParams: selectedTypeParams,
                selectedOwnerTypeArguments: selectedOwnerTypeArguments) != SlotLookup.Declared
            || declaration == null)
            return false;
        declaredRet = ret.Node;
        declaredParams = parameters.Select(parameter => parameter.Node).ToArray();
        paramsRefused = parameters.Select(parameter => parameter.Refused).ToArray();
        physicalMember = declaration.PhysicalMember;
        declarationTypeParams = declaration.TypeParams?.DeepClone() as JsonArray;
        return declaredRet != null || parameters.Any(parameter => parameter.Node != null || parameter.Refused);
    }

    // The DIRECT supertypes of a referenced type, as constructed specs in that type's OWN type-parameter frame, plus
    // whether each is an interface. The override-slot bridge walks these so a class implementing `Derived<Int>` — where
    // the slot is declared on `Derived`'s own base `Sink` — reaches `Sink<Int>` as a spec of its own: a MethodImpl must
    // name the interface that DECLARES the slot, and the emitter looks the directive up under exactly that spec.
    // Empty for a type this index does not know, which is a supertype no bridge decision may be made about.
    public IEnumerable<(TypeNode.Fqn spec, bool isInterface)> ReferencedSupertypes(TypeNode.Fqn owner)
    {
        if (owner == null || !TryReferenceTypeShapeValue(owner, out var shape))
            yield break;
        foreach (var i in shape.Interfaces ?? Array.Empty<TypeNode.Fqn>()) yield return (i, true);
        if (shape.Base != null) yield return (shape.Base, false);
    }

    SlotLookup FindDeclaredSlot(string ownerFqn, string name, bool isStatic, int argCount, int methodArity,
        HashSet<string> path, out SlotFact declaredRet, out SlotFact[] declaredParams,
        out MethodSlotIdentity declaredMethod,
        string propertyName = null, string accessorKind = null, IReadOnlyList<TypeNode> accessorSignature = null,
        TypeNode[] ownerTypeArguments = null, bool includeClosedPropertyReturn = false,
        bool includeUnchangedMethod = false, IReadOnlyList<TypeNode> methodSignature = null,
        TypeNode methodReturn = null, JsonArray selectedTypeParams = null,
        TypeNode[] selectedOwnerTypeArguments = null)
    {
        declaredRet = default;
        declaredParams = null;
        declaredMethod = null;
        var lookupOwner = HasExactOwnerPunctuation(ownerFqn) ? ownerFqn : BareOwnerFqn(ownerFqn);
        if (TryMembersByBirOwner(lookupOwner, out var list))
        {
            var declaredHere = list.Where(m =>
                    (propertyName == null
                        ? (m.SourceMethodName ?? m.Name) == name
                        : !m.IsPropertyBridge && m.SourcePropertyName == propertyName
                            && m.AccessorKind == accessorKind)
                    && m.IsStatic == isStatic
                    && m.ParamCount == argCount
                    && m.MethodArity == methodArity
                    && m.ParamTypeNodes != null
                    && m.ParamTypeNodes.Length == argCount
                    && (propertyName != null || selectedTypeParams == null
                        || KotlinOverrideSlotBridge.SameMethodTypeParameterShape(
                            m.MethodTypeParams, selectedTypeParams,
                            ownerTypeArguments, selectedOwnerTypeArguments)))
                .ToArray();
            var shapeMatches = declaredHere.Where(m =>
                    propertyName == null
                        ? MethodSignatureMatches(m, methodSignature, methodReturn, ownerTypeArguments)
                        : AccessorSignatureMatches(m, accessorSignature, ownerTypeArguments))
                .ToArray();
            // Some authoritative CLR aliases deliberately erase their Kotlin owner parameter from the physical
            // declaration (`Comparable<T>` -> non-generic IComparable.CompareTo(object)). When owner/name/arity names
            // exactly one declaration, that identity is already complete and the erased physical parameter is not a
            // reason to discard it. A real overload set still requires the frontend-resolved signature to select one;
            // if it selects none, refuse HERE rather than walking to an unrelated ancestor.
            if (shapeMatches.Length == 0 && propertyName == null && declaredHere.Length == 1)
                shapeMatches = declaredHere;
            // Declared HERE, ambiguously: refuse outright rather than walking upward, where an unrelated base member
            // of the same shape would look like an answer to a call this type's own overload set already owns.
            if (shapeMatches.Length > 1 || shapeMatches.Length == 0 && declaredHere.Length != 0)
                return SlotLookup.Refused;
            if (shapeMatches.Length == 1)
            {
                var member = shapeMatches[0];
                declaredRet = propertyName != null && includeClosedPropertyReturn
                    ? new SlotFact(member.NullableGenericRet ?? member.KotlinReturnType ?? member.ReturnTypeNode, false)
                    : propertyName == null && includeUnchangedMethod
                        ? new SlotFact(member.NullableGenericRet ?? member.KotlinReturnType ?? member.ReturnTypeNode, false)
                        : DeclaredSlot(member.NullableGenericRet, member.ReturnTypeNode);
                declaredParams = new SlotFact[argCount];
                for (var i = 0; i < argCount; i++)
                    declaredParams[i] = propertyName == null
                        ? includeUnchangedMethod
                            ? new SlotFact(member.NullableGenericParams?[i] ?? member.ParamTypeNodes[i], false)
                            : DeclaredSlot(member.NullableGenericParams?[i], member.ParamTypeNodes[i])
                        : ExactPropertyParameterSlot(member.NullableGenericParams?[i], member.ParamTypeNodes[i],
                            accessorSignature?[i]);
                if (propertyName == null)
                    declaredMethod = new MethodSlotIdentity(member.Name,
                        member.MethodTypeParams?.DeepClone() as JsonArray);
                // DECLARED HERE TERMINATES THE SEARCH, facts or no facts. A concrete member that shadows or
                // implements an inherited namesake IS the declaration the call binds to; continuing upward because
                // this one happens to carry no erasure fact would hand the call the BASE's carrier and rewrite a
                // descriptor the derived member never had.
                return SlotLookup.Declared;
            }
        }
        if (!TryReferenceTypeShapeValue(new TypeNode.Fqn(lookupOwner, ownerTypeArguments), out var shape))
            return SlotLookup.NotDeclared;
        // Reflection reports the interface set TRANSITIVELY, so one hop reaches every interface declaration; the base
        // chain is walked one link at a time. Every supertype that answers is collected and they must AGREE — an
        // inherited member the call cannot distinguish is not a declaration this pass may act on.
        SlotFact foundRet = default;
        SlotFact[] foundParams = null;
        MethodSlotIdentity foundMethod = null;
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
            var superTypeArguments = ConstructedSupertypeArguments(super, ownerTypeArguments);
            var key = ReferenceWalkKey(super.Name, superTypeArguments);
            if (!path.Add(key)) continue;
            var found = FindDeclaredSlot(super.Name, name, isStatic, argCount, methodArity, path,
                out var sret, out var sps, out var smethod, propertyName, accessorKind, accessorSignature,
                superTypeArguments, includeClosedPropertyReturn, includeUnchangedMethod,
                methodSignature, methodReturn, selectedTypeParams, selectedOwnerTypeArguments);
            path.Remove(key);
            if (found == SlotLookup.Refused) return SlotLookup.Refused;
            if (found != SlotLookup.Declared) continue;
            var mret = MapThroughSupertype(sret, super.Args);
            var mps = sps.Select(p => MapThroughSupertype(p, super.Args)).ToArray();
            var mmethod = smethod == null ? null : new MethodSlotIdentity(smethod.PhysicalMember,
                KotlinOverrideSlotBridge.SubstituteOwnerTypeParameterConstraints(smethod.TypeParams, super.Args));
            if (answers++ == 0)
            {
                foundRet = mret;
                foundParams = mps;
                foundMethod = mmethod;
                continue;
            }
            if (!SameSlots(foundRet, foundParams, mret, mps)
                || !SameMethodIdentity(foundMethod, mmethod)) return SlotLookup.Refused;
        }
        if (answers == 0) return SlotLookup.NotDeclared;
        declaredRet = foundRet;
        declaredParams = foundParams;
        declaredMethod = foundMethod;
        return SlotLookup.Declared;
    }

    static bool SameMethodIdentity(MethodSlotIdentity left, MethodSlotIdentity right)
    {
        if (left == null || right == null) return left == right;
        return left.PhysicalMember == right.PhysicalMember
            && KotlinOverrideSlotBridge.SameMethodTypeParameterShape(
                left.TypeParams, right.TypeParams, Array.Empty<TypeNode>(), Array.Empty<TypeNode>());
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
        var lookupOwner = HasExactOwnerPunctuation(ownerFqn) ? ownerFqn : BareOwnerFqn(ownerFqn);
        if (!_ctorsByOwner.TryGetValue(lookupOwner, out var byArity))
        {
            if (HasExactOwnerPunctuation(ownerFqn)) return false;
            var matches = _ctorsByOwner.Where(kv => DottedFqn(kv.Key) == lookupOwner).Take(2).ToList();
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

    // An exact property accessor lookup is tied to a Property row and its associated MethodSemantics method, then
    // checked against the complete frontend-resolved signature in the constructed owner's type frame. For a fixed
    // parameter the reflection spelling is already CLR vocabulary (`System.String`) while this pass still operates
    // on BIR vocabulary (`kotlin.String`), so retain that explicit semantic parameter instead of translating the
    // physical name backward. Open generic positions still come from the declaration and are substituted normally.
    static SlotFact ExactPropertyParameterSlot(TypeNode carrier, TypeNode physical, TypeNode resolvedSignature)
        => new(carrier ?? OpenPhysical(physical) ?? resolvedSignature, false);

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
        TypeNode.Fn fn => ContainsTv(fn.Ret) || fn.Params.Any(ContainsTv)
            || (fn.Recv != null && ContainsTv(fn.Recv)) || (fn.Ctx?.Any(ContainsTv) ?? false),
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
        TypeNode.Array a => new TypeNode.Array(Canonical(a.Elem), a.Rank, a.SzArray),
        TypeNode.Nullable n => new TypeNode.Nullable(Canonical(n.Of)),
        TypeNode.Oblivious o => new TypeNode.Oblivious(Canonical(o.Of)),
        TypeNode.ByRef b => new TypeNode.ByRef(Canonical(b.Of)),
        TypeNode.Fn fn => new TypeNode.Fn(fn.Suspend, Canonical(fn.Ret), fn.Params.Select(Canonical).ToArray(),
            fn.Recv == null ? null : Canonical(fn.Recv), fn.Clr, fn.Ctx?.Select(Canonical).ToArray()),
        _ => t,
    };

    // The declared RETURN type of a top-level fun (a `callStatic owner=null`), resolved via its file-class owner then the
    // member's return type. `recvKey` = the call's first sig-param bare owner (disambiguates overloads across file-classes);
    // `argCount` = the sig's total param count (receiver + args), matching the ref.dll static's ParamCount. null if unresolved.
    public TypeNode TryTopLevelReturn(string funName, string recvKey, int argCount, TypeKey firstParamKey = null) =>
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
    public bool TryExtMemberIntrinsic(string funName, SignatureKey sigKey, out string member) =>
        _extMemberIntrinsics.TryGetValue((funName, sigKey), out member);

    // An @JvmInline value class's backing-field getter call (`x.get_data()`): the inline UNBOX. Returns the CLR conv
    // token for the field's declared type so the call collapses to `conv(recv)` (the erased primitive IS the value).
    public bool TryInlineFieldGetter(string ownerFqn, string member, out string conv)
    {
        conv = null;
        return _inlineBacking.TryGetValue(ownerFqn, out var info) && member == info.Getter && (conv = info.Conv) != null;
    }

    public bool TryInlineFieldGetter(string ownerFqn, string propertyName, string accessorKind, out string conv)
    {
        conv = null;
        if (accessorKind != "get" || !_inlineBacking.TryGetValue(ownerFqn, out var info)
            || !TryMembersByBirOwner(ownerFqn, out var members)) return false;
        return members.Any(member => member.Name == info.Getter
                && !member.IsPropertyBridge && member.SourcePropertyName == propertyName
                && member.AccessorKind == accessorKind)
            && (conv = info.Conv) != null;
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

    public bool TryRule3PropertyAccessor(string ownerFqn, string propertyName, string accessorKind,
        out string physicalMethodName)
    {
        physicalMethodName = null;
        if (!TryMembersByBirOwner(ownerFqn, out var list)) return false;
        var candidates = list.Where(member => member.SourcePropertyName == propertyName
            && !member.IsPropertyBridge && member.AccessorKind == accessorKind
            && member.Intrinsic == null && member.PropertyName == null
            && !member.Conv && !member.IsAbstract).Select(member => member.Name)
            .Distinct(StringComparer.Ordinal).ToList();
        if (candidates.Count != 1) return false;
        physicalMethodName = candidates[0];
        return true;
    }

    // Exact accessor identity carried by a referenced Kotlin declaration's PropertyInfo/MethodSemantics association.
    // The reference assembly intentionally keeps the Kotlin declaration shape: an implementation such as
    // ArrayDeque.size therefore retains its dedicated Kotlin accessor even though a separate MethodImpl maps that
    // MethodDef to ICollection<T>.get_Count. Return the declaration's own associated MethodDef, never the interface
    // slot it implements. The caller excludes directly @ClrTypeAlias-bound owners, whose CLR Property allocation is
    // handled by the external-member path instead.
    public bool TryKotlinPropertyAccessor(string ownerFqn, string propertyName, string accessorKind, int paramCount,
        int methodArity, IReadOnlyList<TypeNode> accessorSignature, TypeNode[] ownerTypeArguments,
        out string physicalMethodName, out bool isVirtual)
    {
        physicalMethodName = null;
        isVirtual = false;
        if (!TryMembersByBirOwner(ownerFqn, out var list)) return false;
        var declarations = list.Where(member => member.SourcePropertyName == propertyName
                && !member.IsPropertyBridge && member.AccessorKind == accessorKind && member.ParamCount == paramCount
                && member.MethodArity == methodArity)
            .ToList();
        if (accessorSignature == null && declarations.Count > 1) return false;
        var candidates = declarations.Where(member => AccessorSignatureMatches(
                member, accessorSignature, ownerTypeArguments))
            .Select(member => (member.Name, member.IsVirtual)).Distinct().ToList();
        if (candidates.Count != 1) return false;
        physicalMethodName = candidates[0].Name;
        isVirtual = candidates[0].IsVirtual;
        return true;
    }

    // A referenced DotKt interface can carry a private exact MethodImpl bridge from its dedicated Kotlin accessor to
    // a foreign CLR property slot. The frontend-selected fake-override fact identifies the Kotlin declaration; this
    // lookup then returns only MethodImpl rows whose trusted accessor carrier identifies a bridge for that declaration.
    // It deliberately does not inspect method bodies or decide whether the declaration is a default implementation.
    public IReadOnlyList<ReferencedPropertyMethodImpl> ReferencedPropertyMethodImpls(string ownerFqn,
        string propertyName, string accessorKind, int paramCount, int methodArity,
        IReadOnlyList<TypeNode> accessorSignature, TypeNode[] ownerTypeArguments)
    {
        var owner = StripGenericArity(DottedFqn(BareOwnerFqn(ownerFqn)));
        if (!_dotKtOwners.Contains(owner) || !TryMembersByBirOwner(owner, out var members))
            return Array.Empty<ReferencedPropertyMethodImpl>();
        var sources = members.Where(member => !member.IsPropertyBridge
                && member.SourcePropertyName == propertyName && member.AccessorKind == accessorKind
                && member.ParamCount == paramCount && member.MethodArity == methodArity
                && AccessorSignatureMatches(member, accessorSignature, ownerTypeArguments))
            .ToList();
        if (sources.Count != 1)
            return Array.Empty<ReferencedPropertyMethodImpl>();
        var bridges = members.Where(member => member.IsPropertyBridge
                && member.SourcePropertyName == propertyName && member.AccessorKind == accessorKind
                && member.ParamCount == paramCount && member.MethodArity == methodArity
                && member.ParamTypeNodes != null && member.ReturnTypeNode != null
                && member.SourcePropertyAssociation != null
                && member.SourcePropertyAssociation == sources[0].PropertyAssociation)
            .ToList();
        var result = new List<ReferencedPropertyMethodImpl>();
        foreach (var bridge in bridges)
        {
            if (!_methodImplsByBody.TryGetValue((owner, bridge.MetadataToken), out var implementations)) continue;
            foreach (var implementation in implementations)
            {
                JsonArray declarationTypeParams = null;
                if (TryMembersByBirOwner(implementation.DeclarationOwner.Name, out var declarationMembers))
                {
                    var declarationCandidates = declarationMembers.Where(member =>
                            member.Name == implementation.DeclarationMember
                            && member.MethodArity == bridge.MethodArity
                            && member.ParamCount == bridge.ParamCount
                            && AccessorSignatureMatches(member, bridge.ParamTypeNodes,
                                implementation.DeclarationOwner.Args))
                        .ToList();
                    if (declarationCandidates.Count == 1)
                        declarationTypeParams = declarationCandidates[0].MethodTypeParams;
                }
                result.Add(new ReferencedPropertyMethodImpl(
                    sources[0].Name,
                    implementation.DeclarationOwner,
                    implementation.DeclarationMember,
                    bridge.ParamTypeNodes,
                    bridge.ReturnTypeNode,
                    bridge.MethodArity,
                    declarationTypeParams));
            }
        }
        return result.Distinct().ToArray();
    }

    // Property identity and get/set role do not distinguish same-name context/member-extension overloads. The
    // frontend-resolved accessor signature is the remaining semantic discriminator. Type-variable positions are
    // completed by the constructed owner/method and therefore do not distinguish declarations here; every nominal
    // non-variable position must agree. Physical accessor spellings never participate in this decision.
    static bool AccessorSignatureMatches(MemberBinding member, IReadOnlyList<TypeNode> signature,
        TypeNode[] ownerTypeArguments)
    {
        if (signature == null) return true;
        if (member.ParamTypeNodes == null || member.ParamTypeNodes.Length != signature.Count) return false;
        for (var i = 0; i < signature.Count; i++)
        {
            var declared = member.NullableGenericParams is { } carriers && i < carriers.Length && carriers[i] != null
                ? carriers[i]
                : member.ParamTypeNodes[i];
            if (declared == null) return false;
            if (ownerTypeArguments != null)
                declared = SupertypeGraph.SubstOwnerTvs(declared, ownerTypeArguments);
            if (!AccessorDeclarationDescribesCall(declared, signature[i])) return false;
        }
        return true;
    }

    static bool MethodSignatureMatches(MemberBinding member, IReadOnlyList<TypeNode> signature,
        TypeNode resolvedReturn, TypeNode[] ownerTypeArguments)
    {
        if (!AccessorSignatureMatches(member, signature, ownerTypeArguments)) return false;
        if (resolvedReturn == null) return true;
        var declared = member.NullableGenericRet ?? member.KotlinReturnType ?? member.ReturnTypeNode;
        if (declared == null) return false;
        if (ownerTypeArguments != null)
            declared = SupertypeGraph.SubstOwnerTvs(declared, ownerTypeArguments);
        return AccessorDeclarationDescribesCall(declared, resolvedReturn);
    }

    internal static bool AccessorDeclarationDescribesCall(TypeNode declaration, TypeNode call)
    {
        if (declaration is TypeNode.Tv) return true;
        if (declaration is TypeNode.Oblivious dOb)
            return AccessorDeclarationDescribesCall(dOb.Of, call);
        if (call is TypeNode.Oblivious cOb)
            return AccessorDeclarationDescribesCall(declaration, cOb.Of);
        if (declaration is TypeNode.Nullable dn)
            return call is TypeNode.Nullable cn
                ? AccessorDeclarationDescribesCall(dn.Of, cn.Of)
                : AccessorDeclarationDescribesCall(dn.Of, call);
        if (call is TypeNode.Nullable callNullable)
            return AccessorDeclarationDescribesCall(declaration, callNullable.Of);
        if (declaration is TypeNode.Fqn df && call is TypeNode.Fqn cf)
        {
            if (ParamKey(df) != ParamKey(cf)) return false;
            if (df.Args == null || cf.Args == null) return df.Args == null && cf.Args == null;
            return df.Args.Length == cf.Args.Length
                && df.Args.Select((arg, i) => AccessorDeclarationDescribesCall(arg, cf.Args[i])).All(x => x);
        }
        if (declaration is TypeNode.Array da && call is TypeNode.Array ca)
            return AccessorDeclarationDescribesCall(da.Elem, ca.Elem);
        if (declaration is TypeNode.ByRef db && call is TypeNode.ByRef cb)
            return AccessorDeclarationDescribesCall(db.Of, cb.Of);
        if (declaration is TypeNode.Fn dfn && call is TypeNode.Fn cfn)
            return FunctionDeclarationDescribesCall(dfn, cfn, AccessorDeclarationDescribesCall);
        return DeclarationDescribesCall(declaration, call);
    }

    bool TryReferencedPropertyPhysicalBinding(string ownerFqn, string propertyName, string accessorKind,
        int paramCount, int methodArity, IReadOnlyList<TypeNode> accessorSignature,
        TypeNode[] ownerTypeArguments, HashSet<string> path,
        out string physicalPropertyName, out string physicalMethodName)
    {
        physicalPropertyName = null;
        physicalMethodName = null;
        var lookupOwner = HasExactOwnerPunctuation(ownerFqn)
            ? ownerFqn : DottedFqn(BareOwnerFqn(ownerFqn));
        var pathKey = ReferenceWalkKey(lookupOwner, ownerTypeArguments);
        if (!path.Add(pathKey)) return false;
        try
        {
            (string Property, string Method)? associated = null;
            if (TryMembersByBirOwner(lookupOwner, out var members))
            {
                var direct = members.Where(member => member.SourcePropertyName == propertyName
                        && !member.IsPropertyBridge && member.AccessorKind == accessorKind
                        && member.ParamCount == paramCount
                        && member.MethodArity == methodArity
                        && AccessorSignatureMatches(member, accessorSignature, ownerTypeArguments))
                    .ToList();
                if (direct.Count > 1) return false;
                if (direct.Count == 1)
                {
                    var member = direct[0];
                    if (member.Intrinsic != null)
                    {
                        physicalPropertyName = member.Intrinsic;
                        // The annotation states the target CLR Property, not its MethodDef spelling. Leave the method
                        // unresolved so TryExternalPropertyAccessorCore reads the exact MethodSemantics association.
                        return true;
                    }
                    var requiredAccess = accessorKind == "set" ? 2 : 1;
                    if (member.PropertyName != null && (member.PropertyAccess & requiredAccess) != 0)
                    {
                        physicalPropertyName = member.PropertyName;
                        // @ClrProperty likewise names a Property row. Its accessor method is recovered from that row,
                        // never projected through Kotlin's dedicated accessor naming policy.
                        return true;
                    }
                    // For an ordinary CLR Property, or a compiler-authored property without a separate intrinsic,
                    // MethodSemantics is the authoritative physical association. SourcePropertyName may come from a
                    // Kotlin carrier and therefore need not equal the CLR Property row's metadata name.
                    if (member.AssociatedPropertyName != null)
                        associated = (member.AssociatedPropertyName, member.Name);
                }
            }

            var inherited = new HashSet<(string Property, string Method)>();
            if (TryReferenceTypeShapeValue(new TypeNode.Fqn(lookupOwner, ownerTypeArguments), out var shape))
                foreach (var super in Supertypes(shape))
                    if (TryReferencedPropertyPhysicalBinding(super.Name, propertyName, accessorKind, paramCount,
                            methodArity, accessorSignature,
                            ConstructedSupertypeArguments(super, ownerTypeArguments), path,
                            out var candidateProperty, out var candidateMethod))
                        inherited.Add((candidateProperty, candidateMethod));
            if (inherited.Count == 1)
            {
                (physicalPropertyName, physicalMethodName) = inherited.Single();
                return true;
            }
            if (inherited.Count > 1) return false;

            // A source-facing reference Property row may coexist with an inherited CLR binding on an aliased type
            // (`List.size` is represented as `size` in the reference surface but binds through `Collection` to
            // `Count` at runtime). Only use the direct MethodSemantics association after the complete explicit CLR
            // binding hierarchy has had first refusal.
            if (associated is { } directAssociation)
            {
                (physicalPropertyName, physicalMethodName) = directAssociation;
                return true;
            }

            // Kotlin permits a new `var` setter to override a getter-only `val`. No setter MethodSemantics row exists
            // in that ancestor, but the getter still owns the physical CLR Property allocation. Resolve that exact
            // getter identity (the setter signature without its final value parameter) and allocate the other role of
            // the same property. The getter walk is one-way and cannot recurse back into this setter fallback.
            if (accessorKind == "set" && paramCount > 0
                && (accessorSignature == null || accessorSignature.Count > 0))
            {
                var getterSignature = accessorSignature?.Take(accessorSignature.Count - 1).ToArray();
                if (TryReferencedPropertyPhysicalBinding(ownerFqn, propertyName, "get", paramCount - 1,
                        methodArity, getterSignature, ownerTypeArguments,
                        new HashSet<string>(StringComparer.Ordinal),
                        out var getterProperty, out _))
                {
                    physicalPropertyName = getterProperty;
                    // The getter proves the Property allocation; the setter's exact MethodDef is still read from that
                    // external Property row by the caller.
                    return true;
                }
            }
            return false;
        }
        finally
        {
            path.Remove(pathKey);
        }
    }

    string ReferenceWalkKey(string owner, TypeNode[] arguments)
    {
        var arity = arguments?.Length ?? 0;
        if (HasExactOwnerPunctuation(owner)) return ReflectedOwnerFqn(owner) + "|" + arity;
        if (_exactPhysicalTypeByDottedName.TryGetValue(OwnerIdentity(owner, arity), out var exact)
            && exact != null) return exact + "|" + arity;
        var semantic = OwnerIdentity(owner, arity);
        return semantic.Name + "|" + semantic.Arity;
    }

    // `super.Args` is expressed in the current owner's declaration frame.  Carry the caller's constructed owner
    // arguments through that edge before matching an accessor on the referenced supertype.  Without this, a type-scoped
    // `T` remains a wildcard and can make `Base<T>.p` spuriously collide with a nominal same-name overload.
    static TypeNode[] ConstructedSupertypeArguments(TypeNode.Fqn super, TypeNode[] ownerTypeArguments)
    {
        if (super.Args == null) return Array.Empty<TypeNode>();
        if (ownerTypeArguments == null) return null;
        return super.Args.Select(argument => SupertypeGraph.SubstOwnerTvs(argument, ownerTypeArguments)).ToArray();
    }

    // Whether the ref.dll owner DECLARES its own concrete (non-abstract, nullary, instance) `iterator()` — a real slot a
    // `this.iterator()`/`x.iterator()` binds to directly, so MemberCallSubstitution must NOT reroute it to the base-Iterator
    // ClrIteratorBridge (which would drop the `MutableIterator` remove()/set() members). The post-#169 concrete
    // LinkedHashSet is the case an APP sees non-locally; the AbstractMutable{Collection,Set} bases keep iterator() ABSTRACT
    // (IsAbstract) so they still reroute. Mirrors the local-decl scan MemberCallSubstitution does for same-file owners.
    public bool DeclaresConcreteIterator(string ownerToken) =>
        ownerToken != null && TryMembersByBirOwner(ownerToken, out var list)
        && list.Any(m => m.Name == "iterator" && m.ParamCount == 0 && !m.IsAbstract && !m.IsStatic);

    // Exact referenced declaration lookup for inherited-member owner binding.  The signature is
    // structural (including type-vs-method Tv scope/index), not a name/arity guess, so overloads
    // remain distinct.  Multiple identical candidates are treated as ambiguous and refused.
    public bool DeclaresExactInstanceMember(string ownerToken, string memberName, int methodArity,
        IReadOnlyList<TypeNode> signature, TypeNode[] ownerTypeArguments)
    {
        if (ownerToken == null || memberName == null || IsAliasedOwner(ownerToken)
            || !TryMembersByBirOwner(ownerToken, out var list)) return false;
        var candidates = list.Where(m => !m.IsStatic && m.Name == memberName && m.MethodArity == methodArity
            && m.ParamTypeNodes is { Length: var length } && length == signature.Count).ToList();
        var matches = candidates.Where(m => m.ParamTypeNodes
            .Select((p, i) => p == signature[i]).All(x => x)).ToList();
        if (matches.Count == 0)
            matches = candidates.Where(m => m.ParamTypeNodes
                .Select((p, i) => SupertypeGraph.SubstOwnerTvs(p, ownerTypeArguments) == signature[i]).All(x => x))
                .ToList();
        return matches.Count == 1;
    }

    // Property twin of DeclaresExactInstanceMember. Source property identity and accessor role select the
    // declaration; the CLR accessor spelling is a later bir2cir output and never participates in lookup.
    public bool DeclaresExactInstancePropertyAccessor(string ownerToken, string propertyName, string accessorKind,
        int methodArity, IReadOnlyList<TypeNode> signature, TypeNode[] ownerTypeArguments)
    {
        if (ownerToken == null || propertyName == null || accessorKind is not ("get" or "set")
            || IsAliasedOwner(ownerToken)
            || !TryMembersByBirOwner(ownerToken, out var list)) return false;
        return list.Count(m => !m.IsStatic && !m.IsPropertyBridge && m.SourcePropertyName == propertyName
            && m.AccessorKind == accessorKind && m.MethodArity == methodArity
            && m.ParamTypeNodes is { } ps && ps.Length == signature.Count
            && AccessorSignatureMatches(m, signature, ownerTypeArguments)) == 1;
    }

    // Whether the exact referenced declaration is virtual. When BIR omitted a declaration signature (common for
    // nullary property accessors), accept only a unique name/method-arity/parameter-count match. This is a CLR
    // dispatch fact consumed by bir2cir; ilemit must not rediscover it from reflection while emitting.
    public bool DeclaresVirtualInstanceMember(string ownerToken, string memberName, int methodArity,
        IReadOnlyList<TypeNode> signature, int paramCount)
    {
        if (ownerToken == null || memberName == null || IsAliasedOwner(ownerToken)
            || !TryMembersByBirOwner(ownerToken, out var list)) return false;
        var candidates = list.Where(m => !m.IsStatic && m.Name == memberName
            && m.MethodArity == methodArity && m.ParamCount == paramCount);
        if (signature != null)
            candidates = candidates.Where(m => m.ParamTypeNodes is { } ps && ps.Length == signature.Count
                && ps.Select((p, i) => p == signature[i]).All(x => x));
        var matches = candidates.ToList();
        return matches.Count == 1 && matches[0].IsVirtual;
    }

    // Property twin of DeclaresVirtualInstanceMember. With no call signature, require the complete
    // property/role/generic-arity/parameter-count shape to identify one declaration on this exact owner.
    public bool DeclaresVirtualInstancePropertyAccessor(string ownerToken, string propertyName, string accessorKind,
        int methodArity, IReadOnlyList<TypeNode> signature, int paramCount, TypeNode[] ownerTypeArguments)
    {
        if (ownerToken == null || propertyName == null || accessorKind is not ("get" or "set")
            || IsAliasedOwner(ownerToken)
            || !TryMembersByBirOwner(ownerToken, out var list)) return false;
        var candidates = list.Where(m => !m.IsStatic && !m.IsPropertyBridge && m.SourcePropertyName == propertyName
            && m.AccessorKind == accessorKind && m.MethodArity == methodArity && m.ParamCount == paramCount);
        if (signature != null)
            candidates = candidates.Where(m => AccessorSignatureMatches(m, signature, ownerTypeArguments));
        var matches = candidates.ToList();
        return matches.Count == 1 && matches[0].IsVirtual;
    }

    // Return the declaration-shape of a referenced owner, normalized to BIR's dotted nested-type
    // spelling.  The returned base/interfaces may contain type-scoped Tvs and are substituted by
    // InheritedMemberOwnerBinding exactly like locally declared supertypes.
    public bool TryReferenceTypeShape(TypeNode.Fqn owner, out int typeParamCount, out string kind,
        out TypeNode.Fqn baseType, out TypeNode.Fqn[] interfaces)
    {
        if (owner != null && !IsAliasedOwner(owner.Name)
            && TryReferenceTypeShapeValue(owner, out var shape))
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

    bool TryReferenceTypeShapeValue(TypeNode.Fqn owner, out ReferenceTypeShape shape)
    {
        if (HasExactOwnerPunctuation(owner.Name)
            && _referenceTypeShapesByPhysicalOwner.TryGetValue(owner.Name, out shape)) return true;
        return _referenceTypeShapes.TryGetValue(
            OwnerIdentity(owner.Name, owner.Args?.Length ?? 0), out shape);
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
        if (HasExactOwnerPunctuation(ownerFqn))
            return _membersByPhysicalOwner.TryGetValue(ownerFqn, out members);
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

    static bool HasExactOwnerPunctuation(string ownerFqn) =>
        ownerFqn != null && (ownerFqn.Contains('`') || ownerFqn.Contains('+'));

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
            ValidateCompanionMetadata(reference);
            IndexMethodImplMetadata(reference, metadata);
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

            if (dotKtAuthored)
            {
                var bySemanticName = types.GroupBy(
                        t => StripGenericArity(DottedFqn(t.FullName ?? t.Name)), StringComparer.Ordinal)
                    .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
                foreach (var carrierType in types)
                {
                    var payload = TrustedStaticCarrierPayload(carrierType, asm);
                    if (payload == null) continue;
                    var owner = payload["owner"].GetValue<string>();
                    var physical = StripGenericArity(DottedFqn(carrierType.FullName ?? carrierType.Name));
                    if (!carrierType.IsPublic)
                        throw new MalformedTrustedStaticCarrierException(
                            $"trusted [KotlinStaticCarrier] '{carrierType.FullName}' must be public");
                    if (string.IsNullOrEmpty(owner) || carrierType.IsGenericType ||
                        !HasAttribute(carrierType.GetCustomAttributesData(), CompilerGeneratedAttr) ||
                        !bySemanticName.TryGetValue(StripGenericArity(DottedFqn(owner)), out var semanticTypes) ||
                        semanticTypes.Length != 1 || !semanticTypes[0].IsGenericTypeDefinition)
                        throw new MalformedTrustedStaticCarrierException(
                            $"malformed trusted [KotlinStaticCarrier] on '{carrierType.FullName}'");
                    ValidateStaticCarrierMembers(carrierType);
                    var semanticOwner = StripGenericArity(DottedFqn(owner));
                    if (!metadata.GenericStaticCarrierBySemanticOwner.TryAdd(semanticOwner, physical))
                        throw new MalformedTrustedStaticCarrierException(
                            $"duplicate trusted [KotlinStaticCarrier] for semantic owner '{semanticOwner}'");
                }
            }

            var semanticOwnerByStaticCarrier = metadata.GenericStaticCarrierBySemanticOwner
                .ToDictionary(
                    kv => StripGenericArity(DottedFqn(kv.Value)),
                    kv => StripGenericArity(DottedFqn(kv.Key)),
                    StringComparer.Ordinal);

            foreach (var type in types)
            {
                try
                {
                    // Index by the REAL Kotlin FQN (kotc emits "kotlin.String" etc. as the type name) so a BIR
                    // member-call owner token matches. A CLR-bound owner carries @ClrTypeAlias (the type-identity
                    // binding) or, for any not-yet-renamed bound class, a class-level @ClrIntrinsic.
                    var ownerFqn = StripGenericArity(type.FullName ?? type.Name);
                    var exactPhysicalOwner = ExactPhysicalMetadataName(type);
                    AddExactPhysicalTypeName(metadata.ExactPhysicalTypeByDottedName,
                        OwnerIdentity(ownerFqn, type.IsGenericType ? type.GetGenericArguments().Length : 0),
                        exactPhysicalOwner);
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
                    var indexedOwnerFqn = semanticOwnerByStaticCarrier.GetValueOrDefault(
                        StripGenericArity(DottedFqn(ownerFqn))) ?? ownerFqn;
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
                    if (dotKtAuthored && RichEnumMetadataOf(type, asm) is { } richEnum)
                    {
                        var semanticOwner = StripGenericArity(DottedFqn(ownerFqn));
                        if (!metadata.RichEnums.TryAdd(semanticOwner, richEnum))
                            throw new InvalidDataException(
                                $"duplicate trusted [KotlinRichEnum] for '{semanticOwner}'");
                    }
                    if (dotKtAuthored && BasicEnumMetadataOf(type, asm) is { } basicEnum)
                    {
                        var semanticOwner = StripGenericArity(DottedFqn(ownerFqn));
                        if (!metadata.BasicEnums.TryAdd(semanticOwner, basicEnum))
                            throw new InvalidDataException(
                                $"duplicate trusted [KotlinBasicEnum] for '{semanticOwner}'");
                    }
                    if (companionRepresentations.TryGetValue(type, out var companionIsStatic))
                        metadata.CompanionStaticByPhysicalOwner.Add(
                            StripGenericArity(DottedFqn(ownerFqn)), companionIsStatic);
                    var typeDeclarationIdentity = OwnerIdentity(ownerFqn,
                        type.IsGenericType ? type.GetGenericArguments().Length : 0);
                    var declarationKind = TypeKind(type);
                    metadata.TypeKinds[typeDeclarationIdentity] = declarationKind;
                    metadata.PhysicalTypeKinds[exactPhysicalOwner] = declarationKind;
                    if (type.IsValueType || !type.IsAbstract && !type.IsInterface &&
                        type.GetConstructor(Type.EmptyTypes) is ConstructorInfo { IsPublic: true })
                    {
                        metadata.PublicParameterlessConstructibleOwners.Add(
                            typeDeclarationIdentity);
                        metadata.PublicParameterlessConstructiblePhysicalOwners.Add(exactPhysicalOwner);
                    }
                    // Both spellings: the reflection name nests with `+`, every bir2cir type token is DOTTED, and a
                    // NESTED `ref struct` (`Span<T>.Enumerator`, `MemoryExtensions.SpanSplitEnumerator`) is exactly
                    // the shape a spill of `for (x in span)` would mint a field of.
                    if (IsByRefLikeType(type))
                    {
                        metadata.ByRefLikeOwners.Add(typeDeclarationIdentity);
                        metadata.ByRefLikePhysicalOwners.Add(exactPhysicalOwner);
                    }
                    var semanticTypeShape = new ReferenceTypeShape(
                        type.IsGenericType ? type.GetGenericArguments().Length : 0,
                        TypeKind(type),
                        DeclarationTypeNode(type.BaseType) as TypeNode.Fqn,
                        type.GetInterfaces().Select(DeclarationTypeNode).OfType<TypeNode.Fqn>().ToArray());
                    metadata.TypeShapes[typeDeclarationIdentity] = semanticTypeShape;
                    // Inheritance edges are declaration identities just like member owners. The exact index retains
                    // the reflected TypeDef spelling so current-format override markers traverse a physical graph
                    // without falling back to arity-free names. Keep the semantic graph separately: Kotlin aliases
                    // intentionally walk that vocabulary before bir2cir selects their CLR representation.
                    metadata.PhysicalTypeShapes[exactPhysicalOwner] = new ReferenceTypeShape(
                        semanticTypeShape.TypeParamCount,
                        semanticTypeShape.Kind,
                        ExactDeclaringView(type.BaseType),
                        type.GetInterfaces().Select(ExactDeclaringView).OfType<TypeNode.Fqn>().ToArray());
                    if (type.IsGenericType)
                    {
                        var gargs = type.GetGenericArguments();
                        metadata.TypeArity[ownerFqn] = gargs.Length;
                        metadata.TypeArity[DottedFqn(ownerFqn)] = gargs.Length;
                        metadata.TypeParamNames[ownerFqn] = gargs.Select(g => g.Name).ToArray();
                        metadata.TypeParamNames[DottedFqn(ownerFqn)] = gargs.Select(g => g.Name).ToArray();
                        var typeParamDeclarations = new JsonArray(
                            gargs.Select(GenericParamDeclaration).ToArray()).ToJsonString();
                        metadata.TypeParamDeclarations[ownerFqn] = typeParamDeclarations;
                        metadata.TypeParamDeclarations[DottedFqn(ownerFqn)] = typeParamDeclarations;
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
                        metadata.AliasKinds[ownerFqn] = TypeKind(type);
                        metadata.AliasKinds[DottedFqn(ownerFqn)] = TypeKind(type);
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
                            var ctorParams = ctors[0].GetParameters().Select(p => DeclarationTypeNode(p.ParameterType)).ToArray();
                            metadata.CtorParamTypes[ownerFqn] = ctorParams;
                            metadata.CtorParamTypes[DottedFqn(ownerFqn)] = ctorParams;
                            metadata.CtorParamTypes[ExactPhysicalMetadataName(type)] = ctorParams;
                        }
                    }
                    if (ownerFqn.StartsWith("dotkt$ClrH_", StringComparison.Ordinal)) metadata.HelperTypes.Add(ownerFqn);
                    if (HasAttribute(type.GetCustomAttributesData(), RestrictsSuspensionAttr)) metadata.RestrictsSuspensionTypes.Add(ownerFqn);
                    var isFileClass = HasAttribute(type.GetCustomAttributesData(), KotlinFileClassAttr);
                    if (dotKtAuthored && isFileClass)
                        metadata.FileClassOwners.Add(StripGenericArity(DottedFqn(ownerFqn)));
                    if (dotKtAuthored)
                        IndexCompanionExtensionMembers(type, ownerFqn, isFileClass, metadata);
                    IndexCSharp14StaticExtensionMembers(type, ownerFqn, metadata, dotKtAuthored);

                    // `value`/inline class (marked with [KotlinValue], the 2.4.0 carrier of `mods.value`): its single
                    // instance backing field IS the erased value. Record that property's GETTER + the field's CLR conv
                    // token so the semantic property getter collapses to `conv(<recv>)`. NARROWED to EXACTLY ONE instance field
                    // — a value class has precisely one property/backing field, so requiring a single field picks the
                    // correct underlying type (and refuses to erase off an arbitrary FirstOrDefault if the shape is
                    // unexpected). The GETTER is the accessor of the PROPERTY that OWNS that field: an accessor-routed
                    // property's storage carries the compiler-generated `<data>k__BackingField` name. The owning
                    // PropertyInfo and its exact GetMethod association are therefore required; no field-name fallback
                    // can identify the Kotlin accessor.
                    if (HasAttribute(type.GetCustomAttributesData(), KotlinValueAttr))
                    {
                        var instanceFields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                        var backing = instanceFields.Length == 1 ? instanceFields[0] : null;
                        if (backing != null && InlineFieldConv(backing.FieldType) is string conv)
                        {
                            var owningProp = type
                                .GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                                .FirstOrDefault(p => BackingFieldRename.Mangle(p.Name) == backing.Name);
                            if (owningProp?.GetGetMethod(true) is MethodInfo getter)
                                metadata.InlineBacking[ownerFqn] = (getter.Name, conv);
                        }
                    }

                    const BindingFlags declaredMemberFlags = BindingFlags.Public | BindingFlags.NonPublic
                        | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;
                    // MethodSemantics is an association table. Index it once per Type instead of rescanning every
                    // PropertyInfo for every MethodInfo below. A malformed/ambiguous method associated with more than
                    // one property states no source identity; never select whichever reflection happens to return first.
                    var propertyAccessors = new Dictionary<int, (string Name, string Kind, bool IsIndexer)?>();
                    void IndexPropertyAccessor(MethodInfo accessor, string propertyName, string kind, bool isIndexer)
                    {
                        if (accessor == null) return;
                        var association = (propertyName, kind, isIndexer);
                        if (propertyAccessors.TryGetValue(accessor.MetadataToken, out var existing)
                            && existing != association)
                            propertyAccessors[accessor.MetadataToken] = null;
                        else
                            propertyAccessors[accessor.MetadataToken] = association;
                    }
                    foreach (var property in type.GetProperties(declaredMemberFlags))
                    {
                        var isIndexer = property.GetIndexParameters().Length > 0;
                        IndexPropertyAccessor(property.GetGetMethod(true), property.Name, "get", isIndexer);
                        IndexPropertyAccessor(property.GetSetMethod(true), property.Name, "set", isIndexer);
                    }

                    foreach (var method in type.GetMethods(declaredMemberFlags))
                    {
                        propertyAccessors.TryGetValue(method.MetadataToken, out var owningProperty);
                        var carriedProperty = dotKtAuthored
                            ? KotlinPropertyAccessorPayload(method.GetCustomAttributesData(), method.DeclaringType?.Assembly)
                            : null;
                        var sourceMethodName = dotKtAuthored &&
                            !HasAttribute(method.GetCustomAttributesData(), CompilerGeneratedAttr)
                            ? KotlinSourceMethodName(method.GetCustomAttributesData(), method.DeclaringType?.Assembly)
                            : null;
                        var innerConstructorFactory = dotKtAuthored
                            ? KotlinInnerConstructorFactoryPayload(
                                method.GetCustomAttributesData(), method.DeclaringType?.Assembly)
                            : null;
                        // dll2klib projects a parameterized CLR Property as Kotlin operator `get`/`set` functions.
                        // Preserve that source identity while retaining the associated MethodDef's exact physical name
                        // (`get_Item`, a custom indexer accessor, etc.) for MethodImpl allocation.
                        if (sourceMethodName == null && carriedProperty is null && owningProperty?.IsIndexer == true)
                            sourceMethodName = owningProperty.Value.Kind;
                        // Declaration identity is also required for a non-public target embedded in a public default
                        // argument/inline carrier. The consumer must retarget the generated UnsafeAccessor to the
                        // producer's allocated MethodDef without resolving again from its erased signature.
                        var declarationIdentity = dotKtAuthored
                            ? KotlinDeclarationIdentityPayload(method.GetCustomAttributesData(), method.DeclaringType?.Assembly,
                                method.GetGenericArguments().Length)
                            : null;
                        var intrinsic = ClrIntrinsicOf(method.GetCustomAttributesData());
                        var prop = ClrPropertyOf(method.GetCustomAttributesData());
                        var byrefPositions = ByrefPositionsOf(method);
                        var countRange = CountRangeOf(method);
                        var collectionFactoryKind = isFileClass && method.IsStatic
                            ? AttrStringArg(method.GetCustomAttributesData(), "kotlin.clr.ClrCollectionFactory")
                            : null;
                        var arrayFactoryKind = isFileClass && method.IsStatic
                            ? AttrStringArg(method.GetCustomAttributesData(), "kotlin.clr.ClrArrayFactory")
                            : null;
                        var arrayFactoryElementHint = arrayFactoryKind == null
                            ? null
                            : ArrayElemHint(method.ReturnType);
                        // @ClrConv (numeric primitive conversion): the call lowers to a CIL `conv` to the callee's OWN
                        // declared return type (toLong -> the emitted `kotlin.Long` type, ...). Read the marker + capture
                        // the return-type token here (the pre-lowering Kotlin FQN, from THIS reference/metadata dll), so
                        // MemberCallSubstitution can emit `{k:conv, to:<convTo>, e:<recv>}` — the target BirTypeLowering
                        // then lowers to System.Int64/etc. and ilemit picks the conv opcode.
                        var isConv = HasAttribute(method.GetCustomAttributesData(), "kotlin.clr.ClrConv");
                        var convTo = isConv ? DeclarationTypeNode(method.ReturnType) : null;
                        // Default argument VALUES remain authoritative in the selected reference DLL. KotlinDefault
                        // contributes its raw Kotlin-expression BIR; an ordinary ECMA-335 constant contributes a plain
                        // const expression. The reference KLIB carries only DECLARES_DEFAULT_VALUE for frontend
                        // resolution, never either payload.
                        if (CallableDefaultsOf(method, mlc) is Dictionary<int, string> defaults)
                        {
                            AddKotlinDefaults(metadata, indexedOwnerFqn, method.Name, method.GetParameters(), defaults);
                            if (exactPhysicalOwner != indexedOwnerFqn)
                                AddKotlinDefaults(metadata, exactPhysicalOwner, method.Name, method.GetParameters(), defaults);
                            if (declarationIdentity?.Id is string declarationId
                                && !metadata.KotlinDefaultsByDeclarationId.TryAdd(declarationId, defaults))
                                throw new InvalidDataException($"duplicate defaults for Kotlin declaration identity '{declarationId}'");
                        }
                        // The `suspend` bit from the DotKt round-trip [KotlinFunction(flags)] attribute (Suspend = 4,
                        // the flag word ilemit stamps; the dead Assembly.LoadFrom scan read it, this live scan didn't).
                        // Channelled into MemberBinding.Suspend for the coroutine bundle (bundle 6) — no consumer yet.
                        var suspend = (KotlinFunctionFlags(method.GetCustomAttributesData()) & KotlinFunctionSuspendFlag) != 0;
                        var suspendReturn = dotKtAuthored && suspend
                            ? CarrierTypeOf(method.GetCustomAttributesData(), method.DeclaringType?.Assembly,
                                KotlinSuspendResultAttr)
                                ?? throw new InvalidDataException(
                                    $"trusted suspend MethodDef '{method.DeclaringType?.FullName}.{method.Name}' has no logical-result carrier")
                            : null;
                        if (suspend && Environment.GetEnvironmentVariable("DOTKT_BIR2CIR_DEBUG_SUSPEND") == "1")
                            Console.Error.WriteLine($"bir2cir: ref-scan suspend member {ownerFqn}.{method.Name}/{method.GetParameters().Length} (Suspend=true)");
                        metadata.MemberBindings.Add(new MemberBinding(
                            indexedOwnerFqn,
                            method.Name,
                            method.GetParameters().Length,
                            intrinsic,
                            method.IsAbstract,
                            method.IsStatic,
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
                            suspendReturn,
                            // #86 D1 — the positional pre-erasure carrier, per slot. Only a DotKt-authored assembly can
                            // carry it, and only the erasure records it, so a slot without one is simply absent here and
                            // the consumer falls back to the physical declaration (which IS `Erase(decl)` by construction).
                            dotKtAuthored ? CarrierTypeOf(method.ReturnParameter.GetCustomAttributesData(), method.DeclaringType?.Assembly, KotlinNullableGenericAttr) : null,
                            dotKtAuthored
                                ? method.GetParameters().Select(p => CarrierTypeOf(p.GetCustomAttributesData(), method.DeclaringType?.Assembly, KotlinNullableGenericAttr)).ToArray()
                                : null,
                            DeclarationTypeNode(method.ReturnType),
                            method.MetadataToken,
                            carriedProperty?.Name ?? owningProperty?.Name,
                            carriedProperty?.Kind ?? owningProperty?.Kind,
                            owningProperty?.Name,
                            carriedProperty?.SourceAssociation != null,
                            method.IsPublic,
                            carriedProperty?.Association,
                            carriedProperty?.SourceAssociation,
                            sourceMethodName,
                            new JsonArray(method.GetGenericArguments()
                                .Select(GenericParamDeclaration).ToArray()),
                            declarationIdentity?.Id,
                            declarationIdentity?.Name,
                            exactPhysicalOwner,
                            declarationIdentity?.SemanticParams,
                            declarationIdentity?.SemanticReturn,
                            collectionFactoryKind,
                            arrayFactoryKind,
                            arrayFactoryElementHint,
                            countRange?.Start ?? -1,
                            countRange?.End ?? -1,
                            declarationIdentity?.SemanticReifiedTypeParameterIndices,
                            declarationIdentity?.NullableWitnessTypeParameterIndices,
                            dotKtAuthored
                                ? method.GetParameters().Select(p =>
                                    KotlinTypeOf(p.GetCustomAttributesData(), method.DeclaringType?.Assembly)
                                    ?? DeclarationTypeNode(p.ParameterType)).ToArray()
                                : null,
                            innerConstructorFactory?.Inner,
                            innerConstructorFactory?.Parameters,
                            innerConstructorFactory?.TypeArguments));
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
                                var ikey = indexedOwnerFqn + "|" + method.Name + "|" + method.GetParameters().Length + "|" + method.GetGenericArguments().Length;
                                if (!metadata.InlinePayloads.TryGetValue(ikey, out var ilst)) metadata.InlinePayloads[ikey] = ilst = new List<string>();
                                ilst.Add(json);
                                if (declarationIdentity?.Id is string declarationId
                                    && !metadata.InlinePayloadsByDeclarationId.TryAdd(declarationId, json))
                                    throw new InvalidDataException($"duplicate inline declaration identity '{declarationId}'");
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
                                metadata.TopLevelIntrinsicsBySig.TryAdd((method.Name, SigKeyOf(ps)), intrinsic);
                                if (byrefPositions.Length > 0) metadata.TopLevelIntrinsicByref.TryAdd(method.Name, byrefPositions);
                            }
                            else if (ps.Length >= 1)
                                // Key by name|<full ParamKey signature> (receiver-first, mirroring TopLevelIntrinsicsBySig)
                                // so a call resolves the EXACT overload — `substring(Int)`@ClrIntrinsic does NOT capture a
                                // same-count non-intrinsic sibling `substring(IntRange)` (which then falls to its Kotlin body).
                                metadata.ExtMemberIntrinsics.TryAdd((method.Name, SigKeyOf(ps)), intrinsic);
                        }
                        // A NON-intrinsic top-level fun (a real Kotlin body in a file-class) -> index it by name so an APP
                        // build can attribute a referenced `callStatic owner=null` to this file-class (disambiguated by the
                        // first-param receiver type when overloaded across file-classes). The stdlib self-build never reads it.
                        // #157: this DELIBERATELY has no IsSpecialName exclusion, so a top-level property accessor (a
                        // file-class static with intrinsic==null) is indexed too. That is what lets a cross-module
                        // top-level `val` read (kotc emits owner:null + prop:get; bir2cir allocates the physical name) resolve GENERICALLY
                        // through TryResolveTopLevelStatic (e.g. COROUTINE_SUSPENDED -> IntrinsicsKt), with no per-name special-case.
                        var isCSharpExtension = method.IsStatic && method.GetCustomAttributesData().Any(a =>
                            a.AttributeType.FullName == "System.Runtime.CompilerServices.ExtensionAttribute");
                        if ((isFileClass || isCSharpExtension) && method.IsStatic && intrinsic == null)
                        {
                            var ps = method.GetParameters();
                            var rk = ps.Length >= 1 ? RecvKey(ps[0].ParameterType) : "";
                            // The FINE first-param key (ParamKey space): distinguishes the array overloads a coarse "[]"
                            // recvKey collapses arrays, so the structural key distinguishes IntArray, UByteArray, and Array<T>
                            // owner attribution pins the RIGHT file-class+overload (#153 unsigned-array miscompile).
                            var pk = ps.Length >= 1 ? ReceiverParamKey(ps[0].ParameterType) : null;
                            if (!metadata.TopLevelStatics.TryGetValue(method.Name, out var lst))
                                metadata.TopLevelStatics[method.Name] = lst = new List<(string, string, TypeKey)>();
                            lst.Add((ownerFqn, rk, pk));
                        }
                        // Collection/array FACTORY markers on a [KotlinFileClass] static (listOf/setOf/mapOf/arrayOf/…):
                        // record name -> kind so MemberCallSubstitution re-emits the newList/newSet/newMap/newArray node
                        // (the recognition kotc used to do via its LIST/SET/MAP/ARRAY_FACTORY tables). Every overload of a
                        // factory name agrees on the kind, so a name key is enough.
                        if (isFileClass && method.IsStatic)
                        {
                            if (collectionFactoryKind is string cf)
                                metadata.CollectionFactories[method.Name] = cf;
                            if (arrayFactoryKind is string af)
                            {
                                metadata.ArrayFactories[method.Name] = af;
                                // Element hint for a concrete primitive factory (`intArrayOf`), which carries NO type
                                // argument of its own: it answers the call shapes whose vararg does not arrive as a
                                // `newArray` wrapper for MemberCallSubstitution to read the element off — a lone
                                // spread (`intArrayOf(*xs)`) or a mixed `spreadConcat`. An element LIST, empty or
                                // not, brings its own wrapper. Captured from the factory's array return type
                                // (`kotlin.IntArray` -> element `kotlin.Int`); null for the generic `arrayOf<T>`
                                // (whose element is a type variable — typeArgs[0] covers it there).
                                if (arrayFactoryElementHint is string ah)
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
                        if (CallableDefaultsOf(ctor, mlc) is Dictionary<int, string> cdefaults)
                        {
                            AddKotlinDefaults(metadata, ownerFqn, CtorKeyName, ctor.GetParameters(), cdefaults);
                            if (exactPhysicalOwner != ownerFqn)
                                AddKotlinDefaults(metadata, exactPhysicalOwner, CtorKeyName, ctor.GetParameters(), cdefaults);
                        }
                        if (dotKtAuthored && AliasConstructorAdapterOf(
                                ctor.GetCustomAttributesData(), ctor.DeclaringType?.Assembly) is { } adapter)
                            metadata.AliasConstructorAdapters.Add(new(DottedFqn(ownerFqn), adapter));
                        // #86 D1 — a `new`'s arguments fill the constructor's declaration slots, so the ctor's shape is
                        // indexed exactly as a method's is. `Cell<T>(x: T?)` erases to `.ctor(object)` and its carrier
                        // holds the pre-erasure `T?`.
                        metadata.CtorBindings.Add(new CtorBinding(
                            ownerFqn,
                            exactPhysicalOwner,
                            ctor.GetParameters().Length,
                            ctor.GetParameters().Select(p => DeclarationTypeNode(p.ParameterType)).ToArray(),
                            dotKtAuthored
                                ? ctor.GetParameters().Select(p => CarrierTypeOf(p.GetCustomAttributesData(), ctor.DeclaringType?.Assembly, KotlinNullableGenericAttr)).ToArray()
                                : null));
                    }
                }
                catch (MalformedTrustedCompanionException) { throw; }
                catch (MalformedTrustedStaticCarrierException) { throw; }
                catch (InvalidDataException) { throw; }
                catch (Exception ex)
                {
                    metadata.Diagnostics.Add($"subst scan skip {type?.FullName}: {ex.GetType().Name}");
                }
            }
        }
        catch (MalformedTrustedCompanionException) { throw; }
        catch (MalformedTrustedStaticCarrierException) { throw; }
        catch (InvalidDataException) { throw; }
        catch (Exception ex)
        {
            metadata.Diagnostics.Add($"{Path.GetFileName(reference)}: subst scan failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // CustomAttribute.Parent can name metadata rows that reflection has no object model for (notably InterfaceImpl
    // and GenericParamConstraint). Validate the trusted companion-extension and static carriers over the complete ECMA
    // table before MLC projects the assembly. The shared raw reader also validates the exact constructor blobs
    // (including zero named arguments), so bir2cir and dll2klib accept precisely the same carrier envelopes rather than
    // two reflection-dependent subsets.
    static void ValidateCompanionMetadata(string reference)
    {
        using var stream = File.OpenRead(reference);
        using var pe = new PEReader(stream, PEStreamOptions.PrefetchMetadata);
        if (!pe.HasMetadata) return;
        var reader = pe.GetMetadataReader();
        var attrs = new MetadataAttributes(reader);
        if (!attrs.IsDotKtAssembly) return;

        attrs.ValidateCarrierTargets(
            KotlinCompanionExtensionAttr,
            HandleKind.MethodDefinition,
            HandleKind.FieldDefinition);
        attrs.ValidateCarrierTargets(
            KotlinExtensionCoreAttr,
            HandleKind.MethodDefinition);
        attrs.ValidateCarrierTargets(
            KotlinPropertyAccessorAttr,
            HandleKind.MethodDefinition);
        attrs.ValidateCarrierTargets(
            KotlinSourceMethodAttr,
            HandleKind.MethodDefinition);
        attrs.ValidateCarrierTargets(
            KotlinInnerConstructorFactoryAttr,
            HandleKind.MethodDefinition);
        attrs.ValidateCarrierTargets(
            KotlinDeclarationIdentityAttr,
            HandleKind.MethodDefinition);
        attrs.ValidateCarrierTargets(
            KotlinSuspendResultAttr,
            HandleKind.MethodDefinition);
        attrs.ValidateCarrierTargets(
            KotlinStaticCarrierAttr,
            HandleKind.TypeDefinition);
        attrs.ValidateCarrierTargets(
            KotlinRichEnumAttr,
            HandleKind.TypeDefinition);
        attrs.ValidateCarrierTargets(
            KotlinBasicEnumAttr,
            HandleKind.TypeDefinition);
        foreach (var typeHandle in reader.TypeDefinitions)
        {
            var type = reader.GetTypeDefinition(typeHandle);
            using (attrs.CarrierDocument(typeHandle, KotlinStaticCarrierAttr)) { }
            using (attrs.CarrierDocument(typeHandle, KotlinRichEnumAttr)) { }
            using (attrs.CarrierDocument(typeHandle, KotlinBasicEnumAttr)) { }
            foreach (var method in type.GetMethods())
            {
                using (attrs.CarrierDocument(method, KotlinCompanionExtensionAttr)) { }
                using (attrs.CarrierDocument(method, KotlinExtensionCoreAttr)) { }
                using (attrs.CarrierDocument(method, KotlinPropertyAccessorAttr)) { }
                using (attrs.CarrierDocument(method, KotlinSourceMethodAttr)) { }
                using (attrs.CarrierDocument(method, KotlinInnerConstructorFactoryAttr)) { }
                using (attrs.CarrierDocument(method, KotlinDeclarationIdentityAttr)) { }
            }
            foreach (var field in type.GetFields())
                using (attrs.CarrierDocument(field, KotlinCompanionExtensionAttr)) { }
        }
    }

    static void IndexMethodImplMetadata(string reference, ReferenceDotKtMetadata metadata)
    {
        using var stream = File.OpenRead(reference);
        using var pe = new PEReader(stream, PEStreamOptions.PrefetchMetadata);
        if (!pe.HasMetadata) return;
        var reader = pe.GetMetadataReader();
        if (!new MetadataAttributes(reader).IsDotKtAssembly) return;
        var provider = new MethodImplOwnerTypeProvider();
        foreach (var typeHandle in reader.TypeDefinitions)
        {
            var type = reader.GetTypeDefinition(typeHandle);
            var bodyOwner = MetadataDefinitionName(reader, typeHandle);
            foreach (var implementationHandle in type.GetMethodImplementations())
            {
                var implementation = reader.GetMethodImplementation(implementationHandle);
                if (implementation.MethodBody.Kind != HandleKind.MethodDefinition) continue;
                if (!TryMethodImplDeclaration(reader, provider, implementation.MethodDeclaration,
                        out var declarationOwner, out var declarationMember))
                    continue;
                metadata.MethodImplBindings.Add(new MethodImplBinding(
                    bodyOwner,
                    MetadataTokens.GetToken(implementation.MethodBody),
                    declarationOwner,
                    declarationMember));
            }
        }
    }

    static bool TryMethodImplDeclaration(MetadataReader reader, MethodImplOwnerTypeProvider provider,
        EntityHandle declaration, out TypeNode.Fqn owner, out string member)
    {
        owner = null;
        member = null;
        EntityHandle parent;
        switch (declaration.Kind)
        {
            case HandleKind.MemberReference:
            {
                var reference = reader.GetMemberReference((MemberReferenceHandle)declaration);
                member = reader.GetString(reference.Name);
                parent = reference.Parent;
                break;
            }
            case HandleKind.MethodDefinition:
            {
                var definition = reader.GetMethodDefinition((MethodDefinitionHandle)declaration);
                member = reader.GetString(definition.Name);
                parent = definition.GetDeclaringType();
                break;
            }
            default:
                return false;
        }
        TypeNode decoded = parent.Kind switch
        {
            HandleKind.TypeDefinition => provider.GetTypeFromDefinition(
                reader, (TypeDefinitionHandle)parent, (byte)SignatureTypeKind.Class),
            HandleKind.TypeReference => provider.GetTypeFromReference(
                reader, (TypeReferenceHandle)parent, (byte)SignatureTypeKind.Class),
            HandleKind.TypeSpecification => reader.GetTypeSpecification((TypeSpecificationHandle)parent)
                .DecodeSignature(provider, default(MethodImplGenericContext)),
            _ => null,
        };
        owner = decoded as TypeNode.Fqn;
        return owner != null && !string.IsNullOrEmpty(member);
    }

    static string MetadataDefinitionName(MetadataReader reader, TypeDefinitionHandle handle)
    {
        var definition = reader.GetTypeDefinition(handle);
        var simple = StripGenericArity(reader.GetString(definition.Name));
        var parent = definition.GetDeclaringType();
        if (!parent.IsNil) return MetadataDefinitionName(reader, parent) + "." + simple;
        var ns = reader.GetString(definition.Namespace);
        return string.IsNullOrEmpty(ns) ? simple : ns + "." + simple;
    }

    readonly record struct MethodImplGenericContext;

    sealed class MethodImplOwnerTypeProvider : ISignatureTypeProvider<TypeNode, MethodImplGenericContext>
    {
        public TypeNode GetArrayType(TypeNode elementType, ArrayShape shape) => new TypeNode.Array(elementType);
        public TypeNode GetByReferenceType(TypeNode elementType) => new TypeNode.ByRef(elementType);
        public TypeNode GetFunctionPointerType(MethodSignature<TypeNode> signature) =>
            new TypeNode.Fqn("System.IntPtr");
        public TypeNode GetGenericInstantiation(TypeNode genericType, ImmutableArray<TypeNode> typeArguments) =>
            genericType is TypeNode.Fqn f ? new TypeNode.Fqn(f.Name, typeArguments.ToArray()) : genericType;
        public TypeNode GetGenericMethodParameter(MethodImplGenericContext genericContext, int index) =>
            new TypeNode.Tv("method", index);
        public TypeNode GetGenericTypeParameter(MethodImplGenericContext genericContext, int index) =>
            new TypeNode.Tv("type", index);
        public TypeNode GetModifiedType(TypeNode modifier, TypeNode unmodifiedType, bool isRequired) => unmodifiedType;
        public TypeNode GetPinnedType(TypeNode elementType) => elementType;
        public TypeNode GetPointerType(TypeNode elementType) => new TypeNode.Ptr(elementType);
        public TypeNode GetPrimitiveType(PrimitiveTypeCode typeCode) => new TypeNode.Fqn(typeCode switch
        {
            PrimitiveTypeCode.Boolean => "System.Boolean",
            PrimitiveTypeCode.Byte => "System.Byte",
            PrimitiveTypeCode.SByte => "System.SByte",
            PrimitiveTypeCode.Int16 => "System.Int16",
            PrimitiveTypeCode.UInt16 => "System.UInt16",
            PrimitiveTypeCode.Int32 => "System.Int32",
            PrimitiveTypeCode.UInt32 => "System.UInt32",
            PrimitiveTypeCode.Int64 => "System.Int64",
            PrimitiveTypeCode.UInt64 => "System.UInt64",
            PrimitiveTypeCode.Single => "System.Single",
            PrimitiveTypeCode.Double => "System.Double",
            PrimitiveTypeCode.Char => "System.Char",
            PrimitiveTypeCode.String => "System.String",
            PrimitiveTypeCode.Object => "System.Object",
            PrimitiveTypeCode.IntPtr => "System.IntPtr",
            PrimitiveTypeCode.UIntPtr => "System.UIntPtr",
            PrimitiveTypeCode.Void => "System.Void",
            PrimitiveTypeCode.TypedReference => "System.TypedReference",
            _ => throw new InvalidDataException($"unsupported primitive in MethodImpl owner: {typeCode}"),
        });
        public TypeNode GetSZArrayType(TypeNode elementType) => new TypeNode.Array(elementType);
        public TypeNode GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) =>
            new TypeNode.Fqn(MetadataDefinitionName(reader, handle));
        public TypeNode GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) =>
            new TypeNode.Fqn(MetadataReferenceName(reader, handle));
        public TypeNode GetTypeFromSpecification(MetadataReader reader, MethodImplGenericContext genericContext,
            TypeSpecificationHandle handle, byte rawTypeKind) =>
            reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);

        static string MetadataReferenceName(MetadataReader reader, TypeReferenceHandle handle)
        {
            var reference = reader.GetTypeReference(handle);
            var simple = StripGenericArity(reader.GetString(reference.Name));
            if (reference.ResolutionScope.Kind == HandleKind.TypeReference)
                return MetadataReferenceName(reader, (TypeReferenceHandle)reference.ResolutionScope) + "." + simple;
            var ns = reader.GetString(reference.Namespace);
            return string.IsNullOrEmpty(ns) ? simple : ns + "." + simple;
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

    static AliasConstructorAdapter AliasConstructorAdapterOf(
        IList<CustomAttributeData> attributes, Assembly declaringAssembly)
    {
        if (declaringAssembly == null) return null;
        var carrier = attributes.FirstOrDefault(candidate =>
            candidate.AttributeType.FullName == KotlinConstructorAdapterAttr
            && candidate.AttributeType.Assembly == declaringAssembly
            && HasAttribute(candidate.AttributeType.GetCustomAttributesData(), CompilerGeneratedAttr));
        if (carrier == null) return null;
        try
        {
            if (carrier.ConstructorArguments.Count != 2)
                throw new FormatException("constructor-adapter carrier does not have (version, bytes)");
            var version = carrier.ConstructorArguments[0].Value as string
                ?? throw new FormatException("constructor-adapter carrier version is not a string");
            var payload = BirCarrier.DecodeBody(
                version, ReadByteArrayArg(carrier.ConstructorArguments[1])) as JsonObject
                ?? throw new FormatException("constructor-adapter carrier body is not an object");
            var parameters = (payload["parameters"] as JsonArray)?.Select(node =>
                    (node as JsonValue)?.GetValue<string>()
                    ?? throw new FormatException("constructor-adapter parameter is not a string"))
                .ToArray() ?? throw new FormatException("constructor-adapter carrier has no parameters");
            var signature = ReadCarrierTypes(payload, "signature");
            var statements = payload["statements"] as JsonArray
                ?? throw new FormatException("constructor-adapter carrier has no statements");
            var arguments = payload["arguments"] as JsonArray
                ?? throw new FormatException("constructor-adapter carrier has no arguments");
            var terminal = ReadCarrierTypes(payload, "terminalSignature");
            var collectionFactoryKind = payload["collectionFactoryKind"] is JsonValue kindValue
                ? kindValue.GetValue<string>()
                : null;
            if (parameters.Length != signature.Length)
                throw new FormatException("constructor-adapter parameter/signature lengths differ");
            return new AliasConstructorAdapter(
                parameters, signature, (JsonArray)statements.DeepClone(),
                (JsonArray)arguments.DeepClone(), terminal, collectionFactoryKind);
        }
        catch (Exception ex) when (ex is not InvalidDataException)
        {
            throw new InvalidDataException("malformed trusted constructor-adapter metadata", ex);
        }
    }

    static TypeNode[] ReadCarrierTypes(JsonObject payload, string key)
    {
        if (payload[key] is not JsonArray values)
            throw new FormatException($"constructor-adapter carrier has no {key}");
        return values.Select(TypeJson.Read).Select(type => type
            ?? throw new FormatException($"constructor-adapter {key} contains no structured type")).ToArray();
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

    static void IndexCompanionExtensionMembers(
        Type type, string ownerFqn, bool isFileClass, ReferenceDotKtMetadata metadata)
    {
        // Constructors share MethodDef as their ECMA-335 parent kind, but reflection deliberately excludes them from
        // GetMethods.  Inspect both instance constructors and the type initializer explicitly so the trusted-carrier
        // validator accepts the same physical member set as dll2klib's MetadataReader walk.  No constructor is an
        // ordinary Kotlin companion-extension function, even when its carrier payload is otherwise well-formed.
        foreach (var constructor in type.GetConstructors(
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            if (CompanionExtensionPayload(constructor.GetCustomAttributesData(), type.Assembly) != null)
                throw new InvalidDataException(
                    $"malformed trusted [KotlinCompanionExtension] on constructor '{type.FullName}.{constructor.Name}'");
        if (type.TypeInitializer is ConstructorInfo typeInitializer &&
            CompanionExtensionPayload(typeInitializer.GetCustomAttributesData(), type.Assembly) != null)
            throw new InvalidDataException(
                $"malformed trusted [KotlinCompanionExtension] on constructor '{type.FullName}.{typeInitializer.Name}'");

        foreach (var method in type.GetMethods(
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance |
            BindingFlags.DeclaredOnly))
        {
            var payload = CompanionExtensionPayload(method.GetCustomAttributesData(), type.Assembly);
            if (payload == null) continue;
            if (!isFileClass || !method.IsStatic || method.IsSpecialName || payload.Kind == "field")
                throw new InvalidDataException(
                    $"malformed trusted [KotlinCompanionExtension] on method '{type.FullName}.{method.Name}'");
            AddCompanionExtensionMember(metadata, ownerFqn, payload, method.Name);
        }

        foreach (var field in type.GetFields(
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance |
            BindingFlags.DeclaredOnly))
        {
            var payload = CompanionExtensionPayload(field.GetCustomAttributesData(), type.Assembly);
            if (payload == null) continue;
            if (!isFileClass || !field.IsStatic || payload.Kind != "field")
                throw new InvalidDataException(
                    $"malformed trusted [KotlinCompanionExtension] on field '{type.FullName}.{field.Name}'");
            AddCompanionExtensionMember(metadata, ownerFqn, payload, field.Name);
        }
    }

    static void IndexCSharp14StaticExtensionMembers(
        Type container,
        string ownerFqn,
        ReferenceDotKtMetadata metadata,
        bool dotKtAuthored)
    {
        const string extensionAttribute = "System.Runtime.CompilerServices.ExtensionAttribute";
        const string markerAttribute = "System.Runtime.CompilerServices.ExtensionMarkerAttribute";
        if (container.DeclaringType != null || !container.IsAbstract || !container.IsSealed ||
            !HasAttribute(container.GetCustomAttributesData(), extensionAttribute))
            return;

        foreach (var group in container.GetNestedTypes(
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
        {
            var declarations = group.GetMethods(
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Cast<MemberInfo>()
                .Concat(group.GetProperties(
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly))
                .Select(member => (Member: member, Markers: CSharp14MarkerNames(member, markerAttribute)))
                .Where(entry => entry.Markers.Length != 0)
                .ToArray();
            if (declarations.Length == 0) continue;
            if (!group.IsSpecialName || !HasAttribute(group.GetCustomAttributesData(), extensionAttribute))
                throw new InvalidDataException(
                    $"malformed C# 14 static extension graph at '{group.FullName}': invalid grouping type");
            var markerNames = declarations.SelectMany(entry => entry.Markers)
                .Distinct(StringComparer.Ordinal).ToArray();
            if (markerNames.Length != 1)
                throw new InvalidDataException(
                    $"malformed C# 14 static extension graph at '{group.FullName}': ambiguous receiver marker");
            var markerMatches = group.GetNestedTypes(
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                .Where(type => type.Name == markerNames[0]).ToArray();
            if (markerMatches.Length != 1)
                throw new InvalidDataException(
                    $"malformed C# 14 static extension graph at '{group.FullName}': receiver marker does not resolve");
            var marker = markerMatches[0];
            var markerMethods = marker.GetMethods(
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Where(method => method.Name == "<Extension>$").ToArray();
            if (markerMethods.Length != 1 || !marker.IsSpecialName || !marker.IsAbstract || !marker.IsSealed)
                throw new InvalidDataException(
                    $"malformed C# 14 static extension graph at '{marker.FullName}': invalid receiver marker");
            var receiverMarker = markerMethods[0];
            var markerIsGenerated = HasAttribute(receiverMarker.GetCustomAttributesData(), CompilerGeneratedAttr);
            var markerBodyIsSignatureOnly = CSharp14MarkerBodyIsSignatureOnly(receiverMarker);
            if (!receiverMarker.IsStatic || !receiverMarker.IsSpecialName ||
                receiverMarker.ReturnType.FullName != "System.Void" ||
                receiverMarker.GetGenericArguments().Length != 0 || receiverMarker.GetParameters().Length != 1 ||
                !markerIsGenerated || !markerBodyIsSignatureOnly)
                throw new InvalidDataException(
                    $"malformed C# 14 static extension graph at '{marker.FullName}': invalid marker method " +
                    $"(static={receiverMarker.IsStatic}, special={receiverMarker.IsSpecialName}, " +
                    $"return={receiverMarker.ReturnType.FullName}, generic={receiverMarker.GetGenericArguments().Length}, " +
                    $"params={receiverMarker.GetParameters().Length}, generated={markerIsGenerated}, " +
                    $"signatureOnly={markerBodyIsSignatureOnly})");
            var blockParameters = group.GetGenericArguments();
            if (!CSharp14GenericParametersMatch(
                    blockParameters, marker.GetGenericArguments(), implementationBlockArity: 0))
                throw new InvalidDataException(
                    $"malformed C# 14 static extension graph at '{marker.FullName}': marker constraints differ");

            var receiverName = CSharp14KotlinClassifier(receiverMarker.GetParameters()[0].ParameterType);
            var receiverJson = TypeJson.Fqn(receiverName).ToJsonString();
            var propertyAccessors = new HashSet<MethodInfo>();
            foreach (var property in group.GetProperties(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                var propertyMarkers = CSharp14MarkerNames(property, markerAttribute);
                var accessors = property.GetAccessors(nonPublic: true);
                if (propertyMarkers.Length == 0)
                {
                    if (accessors.Any(accessor => CSharp14MarkerNames(accessor, markerAttribute).Length != 0))
                        throw new InvalidDataException(
                            $"malformed C# 14 static extension graph at '{group.FullName}': unmarked Property row");
                    continue;
                }
                if (propertyMarkers.Length != 1 || propertyMarkers[0] != marker.Name || accessors.Length == 0 ||
                    accessors.Any(accessor =>
                        CSharp14MarkerNames(accessor, markerAttribute) is not [var value] || value != marker.Name))
                    throw new InvalidDataException(
                        $"malformed C# 14 static extension graph at '{group.FullName}': inconsistent property markers");
                foreach (var accessor in accessors) propertyAccessors.Add(accessor);
                var getter = property.GetMethod ?? throw new InvalidDataException(
                    $"malformed C# 14 static extension graph at '{group.FullName}': property has no getter");
                if (!getter.IsStatic) continue;
                var getterImplementation = CSharp14Implementation(container, group, getter, blockParameters.Length);
                getterImplementation = CSharp14KotlinImplementation(
                    container, group, getter, getterImplementation, dotKtAuthored);
                AddCompanionExtensionMember(metadata, ownerFqn,
                    new CompanionExtensionPayloadInfo(receiverJson, property.Name, "get"),
                    getterImplementation.Name);
                if (property.SetMethod is { } setter)
                {
                    var setterImplementation = CSharp14Implementation(container, group, setter, blockParameters.Length);
                    setterImplementation = CSharp14KotlinImplementation(
                        container, group, setter, setterImplementation, dotKtAuthored);
                    AddCompanionExtensionMember(metadata, ownerFqn,
                        new CompanionExtensionPayloadInfo(receiverJson, property.Name, "set"),
                        setterImplementation.Name);
                }
            }

            foreach (var declaration in group.GetMethods(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                var declarationMarkers = CSharp14MarkerNames(declaration, markerAttribute);
                if (declarationMarkers.Length != 1 || declarationMarkers[0] != marker.Name)
                    throw new InvalidDataException(
                        $"malformed C# 14 static extension graph at '{group.FullName}': unmarked callable declaration");
                if (propertyAccessors.Contains(declaration)) continue;
                if (!declaration.IsStatic) continue;
                var implementation = CSharp14Implementation(container, group, declaration, blockParameters.Length);
                implementation = CSharp14KotlinImplementation(
                    container, group, declaration, implementation, dotKtAuthored);
                AddCompanionExtensionMember(metadata, ownerFqn,
                    new CompanionExtensionPayloadInfo(receiverJson, declaration.Name, "function"),
                    implementation.Name);
            }
        }
    }

    static string[] CSharp14MarkerNames(MemberInfo member, string markerAttribute)
    {
        var attributes = member.GetCustomAttributesData()
            .Where(attribute => attribute.AttributeType.FullName == markerAttribute)
            .ToArray();
        var result = new string[attributes.Length];
        for (var index = 0; index < attributes.Length; index++)
        {
            var attribute = attributes[index];
            if (attribute.ConstructorArguments.Count != 1 ||
                attribute.ConstructorArguments[0].Value is not string value ||
                string.IsNullOrEmpty(value) || attribute.NamedArguments.Count != 0)
                throw new InvalidDataException("malformed [ExtensionMarker] attribute");
            result[index] = value;
        }
        return result;
    }

    static MethodInfo CSharp14Implementation(
        Type container,
        Type group,
        MethodInfo declaration,
        int blockArity)
    {
        if (!CSharp14DeclarationBodyIsSignatureOnly(declaration))
            throw new InvalidDataException(
                $"malformed C# 14 static extension graph at '{group.FullName}': declaration '{declaration.Name}' is callable");
        var matches = container.GetMethods(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(candidate => candidate.Name == declaration.Name && !candidate.IsSpecialName)
            .Where(candidate => CSharp14SignaturesMatch(declaration, candidate, blockArity))
            .ToArray();
        if (matches.Length != 1)
            throw new InvalidDataException(
                $"malformed C# 14 static extension graph at '{group.FullName}': declaration '{declaration.Name}' " +
                $"resolves to {matches.Length} implementations");
        return matches[0];
    }

    static MethodInfo CSharp14KotlinImplementation(
        Type container,
        Type group,
        MethodInfo declaration,
        MethodInfo wrapper,
        bool dotKtAuthored)
    {
        // KotlinExtensionCore is an internal DotKt edge, not part of the standard C# 14 graph. A foreign assembly may
        // legally define a same-named attribute; without the assembly marker it has no authority to redirect Kotlin
        // calls away from the standard implementation. Keep this trust boundary aligned with dll2klib's raw reader.
        if (!dotKtAuthored) return wrapper;
        var coreName = KotlinExtensionCoreName(wrapper.GetCustomAttributesData(), container.Assembly);
        if (coreName == null) return wrapper;
        var matches = container.GetMethods(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(candidate => candidate.Name == coreName && !candidate.IsSpecialName)
            .Where(candidate => CSharp14CoreSignatureMatches(declaration, candidate))
            .ToArray();
        if (matches.Length != 1)
            throw new InvalidDataException(
                $"malformed C# 14 static extension graph at '{group.FullName}': Kotlin core '{coreName}' " +
                $"resolves to {matches.Length} methods");
        return matches[0];
    }

    static bool CSharp14CoreSignatureMatches(MethodInfo declaration, MethodInfo core)
    {
        if (!core.IsStatic ||
            (core.Attributes & MethodAttributes.MemberAccessMask) !=
                (declaration.Attributes & MethodAttributes.MemberAccessMask) ||
            core.GetGenericArguments().Length != declaration.GetGenericArguments().Length ||
            CSharp14TypeKey(declaration.ReturnType, implementation: false, blockArity: 0) !=
                CSharp14TypeKey(core.ReturnType, implementation: true, blockArity: 0))
            return false;
        var declarationParameters = declaration.GetParameters();
        var coreParameters = core.GetParameters();
        if (declarationParameters.Length != coreParameters.Length) return false;
        for (var index = 0; index < declarationParameters.Length; index++)
            if (CSharp14TypeKey(declarationParameters[index].ParameterType, implementation: false, blockArity: 0) !=
                CSharp14TypeKey(coreParameters[index].ParameterType, implementation: true, blockArity: 0))
                return false;
        return CSharp14GenericParametersMatch(
            declaration.GetGenericArguments(), core.GetGenericArguments(), implementationBlockArity: 0);
    }

    static bool CSharp14SignaturesMatch(MethodInfo declaration, MethodInfo implementation, int blockArity)
    {
        if ((declaration.Attributes & MethodAttributes.MemberAccessMask) !=
                (implementation.Attributes & MethodAttributes.MemberAccessMask) ||
            implementation.GetGenericArguments().Length != blockArity + declaration.GetGenericArguments().Length ||
            CSharp14TypeKey(declaration.ReturnType, implementation: false, blockArity) !=
                CSharp14TypeKey(implementation.ReturnType, implementation: true, blockArity))
            return false;
        var declarationParameters = declaration.GetParameters();
        var implementationParameters = implementation.GetParameters();
        if (declarationParameters.Length != implementationParameters.Length) return false;
        for (var index = 0; index < declarationParameters.Length; index++)
            if (CSharp14TypeKey(declarationParameters[index].ParameterType, implementation: false, blockArity) !=
                CSharp14TypeKey(implementationParameters[index].ParameterType, implementation: true, blockArity))
                return false;
        var declarationGeneric = declaration.GetGenericArguments();
        var implementationGeneric = implementation.GetGenericArguments();
        return CSharp14GenericParametersMatch(
                declaration.DeclaringType!.GetGenericArguments(),
                implementationGeneric.Take(blockArity).ToArray(),
                blockArity) &&
            CSharp14GenericParametersMatch(
                declarationGeneric,
                implementationGeneric.Skip(blockArity).ToArray(),
                blockArity);
    }

    static bool CSharp14GenericParametersMatch(
        Type[] declarations,
        Type[] implementations,
        int implementationBlockArity)
    {
        if (declarations.Length != implementations.Length) return false;
        for (var index = 0; index < declarations.Length; index++)
        {
            if (declarations[index].GenericParameterAttributes != implementations[index].GenericParameterAttributes)
                return false;
            var left = declarations[index].GetGenericParameterConstraints()
                .Select(type => CSharp14TypeKey(type, implementation: false, implementationBlockArity))
                .OrderBy(value => value, StringComparer.Ordinal);
            var right = implementations[index].GetGenericParameterConstraints()
                .Select(type => CSharp14TypeKey(type, implementation: implementationBlockArity != 0,
                    implementationBlockArity))
                .OrderBy(value => value, StringComparer.Ordinal);
            if (!left.SequenceEqual(right, StringComparer.Ordinal)) return false;
        }
        return true;
    }

    static string CSharp14TypeKey(Type type, bool implementation, int blockArity)
    {
        if (type.IsByRef) return "byref<" + CSharp14TypeKey(type.GetElementType()!, implementation, blockArity) + ">";
        if (type.IsPointer) return "ptr<" + CSharp14TypeKey(type.GetElementType()!, implementation, blockArity) + ">";
        if (type.IsArray) return $"array:{type.GetArrayRank()}<" +
            CSharp14TypeKey(type.GetElementType()!, implementation, blockArity) + ">";
        if (type.IsGenericParameter)
        {
            var index = type.GenericParameterPosition;
            if (!implementation)
                return type.DeclaringMethod is null ? $"!{index}" : $"!!{index}";
            return index < blockArity ? $"!{index}" : $"!!{index - blockArity}";
        }
        if (type.IsConstructedGenericType)
            return StripGenericArity(type.GetGenericTypeDefinition().FullName ?? type.Name) + "<" +
                string.Join(",", type.GetGenericArguments().Select(argument =>
                    CSharp14TypeKey(argument, implementation, blockArity))) + ">";
        return type.FullName ?? type.Name;
    }

    static string CSharp14KotlinClassifier(Type receiver)
    {
        if (receiver.IsConstructedGenericType) receiver = receiver.GetGenericTypeDefinition();
        return PrimitiveBirName(receiver) switch
        {
            "bool" => "kotlin.Boolean",
            "sbyte" => "kotlin.Byte",
            "byte" => "kotlin.UByte",
            "char" => "kotlin.Char",
            "double" => "kotlin.Double",
            "float" => "kotlin.Float",
            "int" => "kotlin.Int",
            "long" => "kotlin.Long",
            "object" => "kotlin.Any",
            "short" => "kotlin.Short",
            "string" => "kotlin.String",
            "ushort" => "kotlin.UShort",
            "uint" => "kotlin.UInt",
            "ulong" => "kotlin.ULong",
            _ => DottedFqn(StripGenericArity(receiver.FullName ?? receiver.Name)),
        };
    }

    static bool CSharp14MarkerBodyIsSignatureOnly(MethodInfo method)
    {
        byte[] body;
        try { body = method.GetMethodBody()?.GetILAsByteArray(); }
        catch (BadImageFormatException) { return true; }
        return body is null or [0x2A] || CSharp14KnownThrowStub(body);
    }

    static bool CSharp14DeclarationBodyIsSignatureOnly(MethodInfo method)
    {
        byte[] body;
        try { body = method.GetMethodBody()?.GetILAsByteArray(); }
        catch (BadImageFormatException) { return true; }
        return body is null || CSharp14KnownThrowStub(body);
    }

    static bool CSharp14KnownThrowStub(byte[] body) =>
        body is [0x14, 0x7A] ||
        body.Length == 6 && body[0] == 0x73 && body[^1] == 0x7A;

    sealed record CompanionExtensionPayloadInfo(string ReceiverJson, string Name, string Kind);
    sealed record PropertyAccessorPayloadInfo(string Name, string Kind, string Association,
        string SourceAssociation);
    sealed record DeclarationIdentityPayloadInfo(string Id, string Name, TypeNode[] SemanticParams,
        TypeNode SemanticReturn, int[] SemanticReifiedTypeParameterIndices,
        int[] NullableWitnessTypeParameterIndices);

    static DeclarationIdentityPayloadInfo KotlinDeclarationIdentityPayload(
        IList<CustomAttributeData> attrs, Assembly declaringAssembly, int methodGenericArity = int.MaxValue)
    {
        var payload = CarrierJsonOf(attrs, declaringAssembly, KotlinDeclarationIdentityAttr) as JsonObject;
        if (payload == null) return null;
        if (payload.Count is < 2 or > 5 ||
            payload["id"] is not JsonValue idValue || !idValue.TryGetValue<string>(out var id) ||
            payload["name"] is not JsonValue nameValue || !nameValue.TryGetValue<string>(out var name)
            || payload.Any(kv => kv.Key is not ("id" or "name" or "signature" or "reified" or "nullableWitness"))
            || payload["signature"] is JsonNode signature && signature is not JsonObject
            || payload["reified"] is JsonNode reifiedNode && reifiedNode is not JsonArray
            || payload["nullableWitness"] is JsonNode witnessNode && witnessNode is not JsonArray
            || string.IsNullOrEmpty(id) || string.IsNullOrEmpty(name))
            throw new InvalidDataException(
                $"malformed [KotlinDeclarationIdentity] payload: {payload.ToJsonString()}");
        var reified = payload["reified"] is JsonArray reifiedArray
            ? reifiedArray.Select(node => node is JsonValue value && value.TryGetValue<int>(out var index) && index >= 0
                ? index
                : throw new InvalidDataException("malformed [KotlinDeclarationIdentity] reified index"))
                .ToArray()
            : Array.Empty<int>();
        if (reified.Distinct().Count() != reified.Length)
            throw new InvalidDataException("duplicate [KotlinDeclarationIdentity] reified index");
        if (reified.Any(index => index >= methodGenericArity))
            throw new InvalidDataException("[KotlinDeclarationIdentity] reified index exceeds method generic arity");
        var nullableWitness = payload["nullableWitness"] is JsonArray witnessArray
            ? witnessArray.Select(node => node is JsonValue value && value.TryGetValue<int>(out var index) && index >= 0
                ? index
                : throw new InvalidDataException(
                    "malformed [KotlinDeclarationIdentity] nullable-witness index"))
                .ToArray()
            : Array.Empty<int>();
        if (nullableWitness.Distinct().Count() != nullableWitness.Length)
            throw new InvalidDataException("duplicate [KotlinDeclarationIdentity] nullable-witness index");
        if (nullableWitness.Any(index => index >= methodGenericArity))
            throw new InvalidDataException(
                "[KotlinDeclarationIdentity] nullable-witness index exceeds method generic arity");
        TypeNode[] semanticParams = null;
        TypeNode semanticReturn = null;
        if (payload["signature"] is JsonObject semanticSignature)
        {
            if (semanticSignature.Count != 2
                || semanticSignature.Any(kv => kv.Key is not ("params" or "ret"))
                || semanticSignature["params"] is not JsonArray parameters
                || semanticSignature["ret"] is not JsonNode returnType)
                throw new InvalidDataException(
                    $"malformed [KotlinDeclarationIdentity] semantic signature: {semanticSignature.ToJsonString()}");
            try
            {
                semanticParams = parameters.Select(parameter => parameter == null
                        ? throw new FormatException("null parameter type")
                        : TypeNode.Parse(parameter.ToJsonString()))
                    .ToArray();
                semanticReturn = TypeNode.Parse(returnType.ToJsonString());
            }
            catch (Exception ex) when (ex is FormatException or InvalidOperationException
                or KeyNotFoundException or JsonException)
            {
                throw new InvalidDataException(
                    $"malformed [KotlinDeclarationIdentity] semantic signature: {semanticSignature.ToJsonString()}", ex);
            }
        }
        return new DeclarationIdentityPayloadInfo(
            id, name, semanticParams, semanticReturn, reified, nullableWitness);
    }

    static PropertyAccessorPayloadInfo KotlinPropertyAccessorPayload(
        IList<CustomAttributeData> attrs, Assembly declaringAssembly)
    {
        var trusted = attrs.Where(c =>
            c.AttributeType.FullName == KotlinPropertyAccessorAttr &&
            c.AttributeType.Assembly == declaringAssembly &&
            HasAttribute(c.AttributeType.GetCustomAttributesData(), CompilerGeneratedAttr)).ToArray();
        if (trusted.Length == 0) return null;
        if (trusted.Length != 1)
            throw new InvalidDataException("expected exactly one trusted [KotlinPropertyAccessor]");
        var payload = CarrierJsonOf(attrs, declaringAssembly, KotlinPropertyAccessorAttr) as JsonObject
            ?? throw new InvalidDataException("malformed [KotlinPropertyAccessor] payload");
        if (payload.Count is not (3 or 4) ||
            payload["name"] is not JsonValue nameValue || !nameValue.TryGetValue<string>(out var name) ||
            payload["kind"] is not JsonValue kindValue || !kindValue.TryGetValue<string>(out var kind) ||
            payload["association"] is not JsonValue associationValue ||
                !associationValue.TryGetValue<string>(out var association) ||
            string.IsNullOrEmpty(name) || string.IsNullOrEmpty(association) || kind is not ("get" or "set"))
            throw new InvalidDataException("malformed [KotlinPropertyAccessor] payload");
        string sourceAssociation = null;
        if (payload.Count == 4 && payload["sourceAssociation"] is JsonValue sourceValue)
        {
            if (!sourceValue.TryGetValue<string>(out sourceAssociation) || string.IsNullOrEmpty(sourceAssociation))
                throw new InvalidDataException("malformed [KotlinPropertyAccessor] source association");
        }
        else if (payload.Count == 4 || payload.ContainsKey("sourceAssociation"))
            throw new InvalidDataException("malformed [KotlinPropertyAccessor] source association");
        return new PropertyAccessorPayloadInfo(name, kind, association, sourceAssociation);
    }

    static string KotlinSourceMethodName(
        IList<CustomAttributeData> attrs, Assembly declaringAssembly)
    {
        var trusted = attrs.Where(c =>
            c.AttributeType.FullName == KotlinSourceMethodAttr &&
            c.AttributeType.Assembly == declaringAssembly &&
            HasAttribute(c.AttributeType.GetCustomAttributesData(), CompilerGeneratedAttr)).ToArray();
        if (trusted.Length == 0) return null;
        if (trusted.Length != 1)
            throw new InvalidDataException("expected exactly one trusted [KotlinSourceMethod]");
        var payload = CarrierJsonOf(attrs, declaringAssembly, KotlinSourceMethodAttr) as JsonObject
            ?? throw new InvalidDataException("malformed [KotlinSourceMethod] payload");
        if (payload.Count != 1 ||
            payload["name"] is not JsonValue nameValue ||
            !nameValue.TryGetValue<string>(out var name) || string.IsNullOrEmpty(name))
            throw new InvalidDataException("malformed [KotlinSourceMethod] payload");
        return name;
    }

    sealed record InnerConstructorFactoryPayloadInfo(
        string Inner, TypeNode[] Parameters, int[] TypeArguments);

    static InnerConstructorFactoryPayloadInfo KotlinInnerConstructorFactoryPayload(
        IList<CustomAttributeData> attrs, Assembly declaringAssembly)
    {
        var trusted = attrs.Where(c =>
            c.AttributeType.FullName == KotlinInnerConstructorFactoryAttr &&
            c.AttributeType.Assembly == declaringAssembly &&
            HasAttribute(c.AttributeType.GetCustomAttributesData(), CompilerGeneratedAttr)).ToArray();
        if (trusted.Length == 0) return null;
        if (trusted.Length != 1)
            throw new InvalidDataException("expected exactly one trusted [KotlinInnerConstructorFactory]");
        var payload = CarrierJsonOf(attrs, declaringAssembly, KotlinInnerConstructorFactoryAttr) as JsonObject
            ?? throw new InvalidDataException("malformed [KotlinInnerConstructorFactory] payload");
        if (payload.Count != 3
            || payload["inner"] is not JsonValue innerValue
            || !innerValue.TryGetValue<string>(out var inner) || string.IsNullOrEmpty(inner)
            || payload["params"] is not JsonArray parameters
            || payload["typeArgs"] is not JsonArray typeArguments)
            throw new InvalidDataException("malformed [KotlinInnerConstructorFactory] payload");
        var parsed = parameters.Select(TypeJson.Read).ToArray();
        var parsedTypeArguments = typeArguments.Select(argument =>
            argument is JsonValue value && value.TryGetValue<int>(out var position) ? position : int.MinValue).ToArray();
        if (parsed.Any(parameter => parameter == null)
            || parsedTypeArguments.Any(position =>
                position < FBoundStarProjectionErasure.InnerFactoryBottomTypeArgument))
            throw new InvalidDataException("malformed [KotlinInnerConstructorFactory] parameter descriptor");
        return new InnerConstructorFactoryPayloadInfo(inner, parsed, parsedTypeArguments);
    }

    static string KotlinExtensionCoreName(
        IList<CustomAttributeData> attrs, Assembly declaringAssembly)
    {
        var trusted = attrs.Where(c =>
            c.AttributeType.FullName == KotlinExtensionCoreAttr &&
            c.AttributeType.Assembly == declaringAssembly &&
            HasAttribute(c.AttributeType.GetCustomAttributesData(), CompilerGeneratedAttr)).ToArray();
        if (trusted.Length == 0) return null;
        if (trusted.Length != 1)
            throw new InvalidDataException("expected exactly one trusted [KotlinExtensionCore]");
        var payload = CarrierJsonOf(attrs, declaringAssembly, KotlinExtensionCoreAttr) as JsonObject
            ?? throw new InvalidDataException("malformed [KotlinExtensionCore] payload");
        if (payload.Count != 1 ||
            payload["name"] is not JsonValue nameValue ||
            !nameValue.TryGetValue<string>(out var name) || string.IsNullOrEmpty(name))
            throw new InvalidDataException("malformed [KotlinExtensionCore] payload");
        return name;
    }

    static CompanionExtensionPayloadInfo CompanionExtensionPayload(
        IList<CustomAttributeData> attrs, Assembly declaringAssembly)
    {
        var trusted = attrs.Where(c =>
            c.AttributeType.FullName == KotlinCompanionExtensionAttr &&
            c.AttributeType.Assembly == declaringAssembly &&
            HasAttribute(c.AttributeType.GetCustomAttributesData(), CompilerGeneratedAttr)).ToArray();
        if (trusted.Length == 0) return null;
        if (trusted.Length != 1)
            throw new InvalidDataException("expected exactly one trusted [KotlinCompanionExtension]");

        var payload = CarrierJsonOf(attrs, declaringAssembly, KotlinCompanionExtensionAttr) as JsonObject
            ?? throw new InvalidDataException("malformed [KotlinCompanionExtension] payload");
        if (payload.Count != 3 || payload["receiver"] == null ||
            payload["name"] is not JsonValue nameValue || !nameValue.TryGetValue<string>(out var name) ||
            payload["kind"] is not JsonValue kindValue || !kindValue.TryGetValue<string>(out var kind) ||
            string.IsNullOrEmpty(name) || kind is not ("function" or "get" or "set" or "field"))
            throw new InvalidDataException("malformed [KotlinCompanionExtension] payload");
        if (TypeJson.Read(payload["receiver"]) is not TypeNode.Fqn { Args: null } receiverType)
            throw new InvalidDataException("companion-extension receiver is not a bare classifier type");
        var receiver = TypeJson.Write(receiverType).ToJsonString();
        return new CompanionExtensionPayloadInfo(receiver, name, kind);
    }

    static void AddCompanionExtensionMember(
        ReferenceDotKtMetadata metadata,
        string owner,
        CompanionExtensionPayloadInfo payload,
        string physicalName)
    {
        var key = CompanionExtensionKey(owner, payload.ReceiverJson, payload.Kind, payload.Name);
        if (!metadata.CompanionExtensionMembers.TryAdd(key, physicalName) &&
            metadata.CompanionExtensionMembers[key] != physicalName)
            // The source-name index is intentionally incomplete: Kotlin overloads may erase to distinct physical
            // names under #395. Mark that structural key ambiguous; frontend-selected declaration identity remains
            // the authoritative cross-module binding and name-only consumers fail closed.
            metadata.CompanionExtensionMembers[key] = "";
    }

    static BasicEnumMetadata BasicEnumMetadataOf(Type type, Assembly declaringAssembly)
    {
        var trusted = type.GetCustomAttributesData().Where(attribute =>
            attribute.AttributeType.FullName == KotlinBasicEnumAttr &&
            attribute.AttributeType.Assembly == declaringAssembly &&
            HasAttribute(attribute.AttributeType.GetCustomAttributesData(), CompilerGeneratedAttr)).ToArray();
        if (trusted.Length == 0) return null;
        if (trusted.Length != 1)
            throw new InvalidDataException("expected exactly one trusted [KotlinBasicEnum]");
        var payload = CarrierJsonOf(type.GetCustomAttributesData(), declaringAssembly, KotlinBasicEnumAttr) as JsonObject
            ?? throw new InvalidDataException("malformed [KotlinBasicEnum] payload");
        if (!type.IsEnum || type.IsGenericType || payload.Count != 2 ||
            payload["underlying"] is not JsonValue underlyingValue ||
            !underlyingValue.TryGetValue<string>(out var underlying) || string.IsNullOrEmpty(underlying) ||
            payload["entries"] is not JsonArray entries || type.GetEnumUnderlyingType().FullName != underlying)
            throw new InvalidDataException(
                $"malformed [KotlinBasicEnum] on '{type.FullName}': expected a non-generic enum, exact underlying type, and entries");

        var ordered = new List<BasicEnumEntry>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        var physicalValues = new HashSet<string>(StringComparer.Ordinal);
        for (var ordinal = 0; ordinal < entries.Count; ordinal++)
        {
            if (entries[ordinal] is not JsonObject entry || entry.Count != 3 ||
                entry["name"] is not JsonValue nameValue ||
                !nameValue.TryGetValue<string>(out var name) || string.IsNullOrEmpty(name) || !names.Add(name) ||
                entry["ordinal"] is not JsonValue ordinalValue ||
                !ordinalValue.TryGetValue<int>(out var declaredOrdinal) || declaredOrdinal != ordinal ||
                entry["physicalValue"] is not JsonValue physicalValue ||
                !physicalValue.TryGetValue<string>(out var text) || string.IsNullOrEmpty(text) ||
                !physicalValues.Add(text))
                throw new InvalidDataException(
                    $"malformed [KotlinBasicEnum] on '{type.FullName}': entries require unique name/ordinal/value triples in declaration order");
            var fields = type.GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Where(field => field.Name == name && field.FieldType == type && field.IsLiteral).ToArray();
            if (fields.Length != 1 || EnumConstantText(fields[0].GetRawConstantValue()) != text)
                throw new InvalidDataException(
                    $"malformed [KotlinBasicEnum] on '{type.FullName}': entry '{name}' does not match its literal FieldDef");
            ordered.Add(new BasicEnumEntry(name, ordinal, text));
        }
        var literalNames = type.GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(field => field.IsLiteral && field.FieldType == type)
            .Select(field => field.Name).ToHashSet(StringComparer.Ordinal);
        if (!literalNames.SetEquals(names))
            throw new InvalidDataException(
                $"malformed [KotlinBasicEnum] on '{type.FullName}': entry map does not match literal fields");
        return new BasicEnumMetadata(underlying, ordered);
    }

    static string EnumConstantText(object value) => value switch
    {
        sbyte v => v.ToString(CultureInfo.InvariantCulture),
        byte v => v.ToString(CultureInfo.InvariantCulture),
        short v => v.ToString(CultureInfo.InvariantCulture),
        ushort v => v.ToString(CultureInfo.InvariantCulture),
        int v => v.ToString(CultureInfo.InvariantCulture),
        uint v => v.ToString(CultureInfo.InvariantCulture),
        long v => v.ToString(CultureInfo.InvariantCulture),
        ulong v => v.ToString(CultureInfo.InvariantCulture),
        _ => throw new InvalidDataException("malformed enum literal constant"),
    };

    static RichEnumMetadata RichEnumMetadataOf(Type type, Assembly declaringAssembly)
    {
        var trusted = type.GetCustomAttributesData().Where(attribute =>
            attribute.AttributeType.FullName == KotlinRichEnumAttr &&
            attribute.AttributeType.Assembly == declaringAssembly &&
            HasAttribute(attribute.AttributeType.GetCustomAttributesData(), CompilerGeneratedAttr)).ToArray();
        if (trusted.Length == 0) return null;
        if (trusted.Length != 1)
            throw new InvalidDataException("expected exactly one trusted [KotlinRichEnum]");
        var payload = CarrierJsonOf(type.GetCustomAttributesData(), declaringAssembly, KotlinRichEnumAttr) as JsonObject
            ?? throw new InvalidDataException("malformed [KotlinRichEnum] payload");
        if (payload.Count != 5 || payload["entries"] is not JsonArray entries ||
            payload["name"] is not JsonValue nameFieldValue ||
            !nameFieldValue.TryGetValue<string>(out var nameField) || string.IsNullOrEmpty(nameField) ||
            payload["ordinal"] is not JsonValue ordinalFieldValue ||
            !ordinalFieldValue.TryGetValue<string>(out var ordinalField) || string.IsNullOrEmpty(ordinalField) ||
            payload["values"] is not JsonValue valuesValue ||
            !valuesValue.TryGetValue<string>(out var values) || string.IsNullOrEmpty(values) ||
            payload["valueOf"] is not JsonValue valueOfValue ||
            !valueOfValue.TryGetValue<string>(out var valueOf) || string.IsNullOrEmpty(valueOf))
            throw new InvalidDataException(
                "malformed [KotlinRichEnum] payload: expected entries plus name/ordinal fields and values/valueOf APIs");
        if (!type.IsClass || type.IsEnum || type.IsGenericType)
            throw new InvalidDataException(
                $"malformed [KotlinRichEnum] on '{type.FullName}': expected a non-generic reference class");

        var entryFields = new Dictionary<string, string>(StringComparer.Ordinal);
        var physicalFields = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in entries)
        {
            if (node is not JsonObject entry || entry.Count != 2 ||
                entry["name"] is not JsonValue nameValue ||
                !nameValue.TryGetValue<string>(out var name) || string.IsNullOrEmpty(name) ||
                entry["field"] is not JsonValue fieldValue ||
                !fieldValue.TryGetValue<string>(out var fieldName) || string.IsNullOrEmpty(fieldName) ||
                !entryFields.TryAdd(name, fieldName) || !physicalFields.Add(fieldName))
                throw new InvalidDataException(
                    "malformed [KotlinRichEnum] payload: entries require unique non-empty name/field pairs");
            var fields = type.GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Where(field => field.Name == fieldName && field.FieldType == type && field.IsInitOnly).ToArray();
            if (fields.Length != 1)
                throw new InvalidDataException(
                    $"malformed [KotlinRichEnum] on '{type.FullName}': entry field '{fieldName}' " +
                    "must be uniquely public, static, initonly, and self-typed");
        }

        if (!physicalFields.Add(nameField) || !physicalFields.Add(ordinalField))
            throw new InvalidDataException(
                "malformed [KotlinRichEnum] payload: physical fields must be distinct");
        void RequireMetadataField(string fieldName, IReadOnlySet<string> fieldTypes)
        {
            var fields = type.GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(field => field.Name == fieldName && fieldTypes.Contains(field.FieldType.FullName) && field.IsInitOnly)
                .ToArray();
            if (fields.Length != 1)
                throw new InvalidDataException(
                    $"malformed [KotlinRichEnum] on '{type.FullName}': metadata field '{fieldName}' " +
                    $"must be uniquely public, instance, initonly, and {string.Join("/", fieldTypes)}-typed");
        }
        RequireMetadataField(nameField, new HashSet<string>(StringComparer.Ordinal) { "System.String", "kotlin.String" });
        RequireMetadataField(ordinalField, new HashSet<string>(StringComparer.Ordinal) { "System.Int32", "kotlin.Int" });

        var generatedValues = type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(method => method.Name == values && method.ReturnType.IsArray &&
                method.ReturnType.GetArrayRank() == 1 && method.ReturnType.GetElementType() == type &&
                !method.IsGenericMethod && method.GetParameters().Length == 0 &&
                HasAttribute(method.GetCustomAttributesData(), CompilerGeneratedAttr)).ToArray();
        var generatedValueOf = type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(method => method.Name == valueOf && method.ReturnType == type &&
                !method.IsGenericMethod &&
                method.GetParameters() is [var parameter] &&
                parameter.ParameterType.FullName is "System.String" or "kotlin.String" &&
                HasAttribute(method.GetCustomAttributesData(), CompilerGeneratedAttr)).ToArray();
        if (generatedValues.Length != 1 || generatedValueOf.Length != 1 ||
            generatedValues[0].MetadataToken == generatedValueOf[0].MetadataToken)
            throw new InvalidDataException(
                $"malformed [KotlinRichEnum] on '{type.FullName}': compiler-generated values/valueOf APIs are missing or ambiguous");
        return new RichEnumMetadata(entryFields, nameField, ordinalField, values, valueOf);
    }

    static JsonObject TrustedStaticCarrierPayload(Type carrierType, Assembly declaringAssembly)
    {
        try
        {
            var attrs = carrierType.GetCustomAttributesData().Where(c =>
                c.AttributeType.FullName == KotlinStaticCarrierAttr &&
                c.AttributeType.Assembly == declaringAssembly &&
                HasAttribute(c.AttributeType.GetCustomAttributesData(), CompilerGeneratedAttr))
                .ToArray();
            if (attrs.Length == 0) return null;
            if (attrs.Length != 1)
                throw new FormatException("expected exactly one trusted attribute");
            var attr = attrs[0];
            if (attr.ConstructorArguments.Count != 2 ||
                attr.ConstructorArguments[0].Value is not string version)
                throw new FormatException("expected (version, byte[]) constructor arguments");
            if (attr.NamedArguments.Count != 0)
                throw new FormatException("named arguments are forbidden");
            if (BirCarrier.DecodeBody(version, ReadByteArrayArg(attr.ConstructorArguments[1])) is not JsonObject payload ||
                payload.Count != 1 || payload["owner"] is not JsonValue ownerValue ||
                !ownerValue.TryGetValue<string>(out var owner) || string.IsNullOrEmpty(owner))
                throw new FormatException("expected exactly one non-empty string 'owner'");
            return payload;
        }
        catch (MalformedTrustedStaticCarrierException) { throw; }
        catch (Exception ex)
        {
            throw new MalformedTrustedStaticCarrierException(
                $"malformed trusted [KotlinStaticCarrier] on '{carrierType.FullName}'", ex);
        }
    }

    static void ValidateStaticCarrierMembers(Type carrierType)
    {
        const BindingFlags declared = BindingFlags.Public | BindingFlags.NonPublic |
            BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;
        if (carrierType.GetFields(declared).Any(field => !field.IsStatic) ||
            carrierType.GetMethods(declared).Any(method => !method.IsStatic) ||
            carrierType.GetConstructors(declared).Any(ctor => !ctor.IsStatic) ||
            carrierType.GetProperties(declared).Any(property =>
                property.GetAccessors(nonPublic: true) is not { Length: > 0 } accessors ||
                accessors.Any(accessor => !accessor.IsStatic)))
            throw new MalformedTrustedStaticCarrierException(
                $"trusted [KotlinStaticCarrier] '{carrierType.FullName}' contains an instance declaration");
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
                if (el != null && !el.IsGenericParameter && TypeNodeOf(el) is TypeNode.Fqn { Args: null } f)
                    return f.Name;
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

    // The member-level CLR binding: @ClrIntrinsic("Name"). Returns the BCL member name (the call is
    // rewritten to owner.Name), or null when the member carries no intrinsic (a rule-3 candidate).
    static string ClrIntrinsicOf(IList<CustomAttributeData> attrs)
    {
        var a = attrs.FirstOrDefault(x => x.AttributeType.FullName is "kotlin.clr.ClrIntrinsic");
        return a != null && a.ConstructorArguments.Count > 0 ? a.ConstructorArguments[0].Value as string : null;
    }

    // The PARAMETER positions (0-based, over the method's declared params) marked @ClrRefArgument — a plain-typed
    // parameter the bound BCL member takes BY REFERENCE (`ref`/`out`). The substituted call wraps these argTypes
    // positions in ByRef nodes so ilemit resolves the ref/out overload + emits the address-load. Empty when none.
    static int[] ByrefPositionsOf(MethodBase method)
    {
        var ps = method.GetParameters();
        List<int> hits = null;
        for (var i = 0; i < ps.Length; i++)
            if (ps[i].GetCustomAttributesData().Any(a => a.AttributeType.FullName == "kotlin.clr.ClrRefArgument"))
                (hits ??= new List<int>()).Add(i);
        return hits?.ToArray() ?? Array.Empty<int>();
    }

    // One parameter of an @ClrIntrinsic declaration may state that its Kotlin exclusive-end value occupies a CLR
    // count slot. The marker sits on the END parameter and names the START parameter. Keep the pair on the exact
    // declaration binding; a name/arity side index would recreate the overload collapse this metadata is meant to
    // avoid. Multiple markers or an invalid/non-earlier start are malformed compiler-provided stdlib metadata.
    static (int Start, int End)? CountRangeOf(MethodBase method)
    {
        (int Start, int End)? result = null;
        var ps = method.GetParameters();
        for (var end = 0; end < ps.Length; end++)
        {
            var attr = ps[end].GetCustomAttributesData().FirstOrDefault(a =>
                a.AttributeType.FullName == "kotlin.clr.ClrCountFromExclusiveEnd");
            if (attr == null) continue;
            if (result != null || ClrIntrinsicOf(method.GetCustomAttributesData()) == null
                || attr.ConstructorArguments.Count != 1
                || attr.ConstructorArguments[0].Value is not int start || start < 0 || start >= end
                // The reference build preserves Kotlin primitive declaration types (`kotlin.Int`); runtime builds
                // later lower those to System.Int32. Validate the shared semantic/physical primitive identity rather
                // than requiring one build phase's spelling.
                || ParamKey(ps[start].ParameterType, relaxed: false).Kind != TypeKeyKind.Int32
                || ParamKey(ps[end].ParameterType, relaxed: false).Kind != TypeKeyKind.Int32)
                throw new InvalidDataException(
                    $"invalid @ClrCountFromExclusiveEnd on {method.DeclaringType?.FullName}.{method.Name} parameter {end}");
            result = (start, end);
        }
        return result;
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
    static Dictionary<int, string> CallableDefaultsOf(MethodBase method, MetadataLoadContext mlc)
    {
        var map = KotlinDefaultsOf(method);
        foreach (var p in method.GetParameters())
        {
            if (map?.ContainsKey(p.Position) == true || !p.HasDefaultValue) continue;
            if (ConstantDefaultBir(p, mlc) is not string bir) continue;
            (map ??= new Dictionary<int, string>())[p.Position] = bir;
        }
        return map;
    }

    static string ConstantDefaultBir(ParameterInfo parameter, MetadataLoadContext mlc)
    {
        object value;
        try { value = parameter.RawDefaultValue; }
        catch { return null; }
        if (ReferenceEquals(value, DBNull.Value) || ReferenceEquals(value, Missing.Value)) return null;

        var type = parameter.ParameterType;
        var declaredType = DeclarationTypeNode(type);
        if (declaredType is null) return null;

        // A Constant row may carry CLASS(null) for a value-type parameter. Reflection consequently reports null for
        // C#'s `DateTime value = default`, but ldnull does not inhabit that slot. `default` is CIR's general zero-value
        // expression: ilemit uses initobj for a value type / generic parameter and ldnull only for a reference type.
        // Nullable<T> is a value type too, so this also realizes its null/default representation directly rather than
        // relying on a boxed-null round trip at the call boundary.
        if (value is null)
        {
            bool isValueType;
            if (type.IsGenericParameter) isValueType = true;
            else
            {
                // MetadataLoadContext can need the base-type chain to classify a nominal type. An incomplete reference
                // universe must make this default unrepresentable, not silently reinterpret an unknown slot as a
                // reference and recreate the invalid ldnull emission this path exists to prevent.
                try { isValueType = type.IsValueType; }
                catch { return null; }
            }
            if (isValueType)
                return new JsonObject
                {
                    ["k"] = "default",
                    ["type"] = TypeJson.Write(declaredType),
                }.ToJsonString();
        }

        // A non-null Constant row on Nullable<V> describes a V value, not a literal whose stack type is the
        // constructed Nullable<V>. Emit the exact V constant and construct the wrapper explicitly. Typing the leaf as
        // Nullable<V> bypasses later coercion (source and target already appear equal) and makes ilemit fall through to
        // ldnull because no literal opcode exists for the structured slot.
        Type nullableElement = null;
        try
        {
            if (type.IsConstructedGenericType && IsNullableDefinition(type.GetGenericTypeDefinition()))
                nullableElement = type.GetGenericArguments().SingleOrDefault();
        }
        catch { return null; }
        if (nullableElement != null)
        {
            var elementType = DeclarationTypeNode(nullableElement);
            var elementValue = MetadataConstantNode(nullableElement, elementType, value, mlc);
            if (elementType == null || elementValue == null) return null;
            return new JsonObject
            {
                ["k"] = "nullableWrap",
                ["elem"] = TypeJson.Write(elementType),
                ["e"] = elementValue,
            }.ToJsonString();
        }

        return MetadataConstantNode(type, declaredType, value, mlc)?.ToJsonString();
    }

    static JsonObject MetadataConstantNode(Type type, TypeNode declaredType, object value, MetadataLoadContext mlc)
    {
        if (type == null || declaredType == null) return null;
        // Null reaches this helper only for a reference-typed slot: value-type null/defaults are handled above.
        if (value == null)
            return new JsonObject
            {
                ["k"] = "const",
                ["type"] = TypeJson.Write(declaredType),
                ["value"] = null,
            };

        bool isEnum;
        try { isEnum = type.IsEnum; }
        catch { return null; }
        if (isEnum)
        {
            Type underlying;
            try
            {
                underlying = Enum.GetUnderlyingType(type);
            }
            catch { return null; }
            // An enum Constant row must contain the exact ECMA-335 carrier for its underlying type. Do not let
            // Convert.ToInt* reinterpret an unrelated custom-constant value merely because it happens to be numeric.
            if (!LiteralValueInhabits(underlying, value)) return null;
            var physical = EnumConstantText(value, underlying);
            if (physical == null) return null;
            // An ECMA-335 enum constant is the underlying bits interpreted in the DECLARED enum slot. It need not name
            // an entry (flags combinations commonly do not), so carry the exact physical value instead of recovering
            // an entry identity. EnumValueLowering uses this same CIR form for named external enum entries, and ilemit
            // emits it one-to-one while returning the declared enum stack type.
            return new JsonObject
            {
                ["k"] = "enumValue",
                ["type"] = TypeJson.Write(declaredType),
                ["underlying"] = underlying.FullName ?? underlying.Name,
                ["physicalValue"] = physical,
            };
        }

        // DecimalConstantAttribute is surfaced by both runtime reflection and MetadataLoadContext as an actual
        // System.Decimal. Decimal has no ECMA-335 literal opcode, so materialize the exact 96-bit coefficient, sign,
        // and scale through its public (int lo, int mid, int hi, bool isNegative, byte scale) constructor. Keep this
        // conditioned on the declared slot: a custom constant of the same runtime value attached to another slot is
        // malformed/unrepresentable metadata, not permission to change that slot's type.
        if (type.FullName == "System.Decimal" && value is decimal decimalValue)
        {
            var bits = decimal.GetBits(decimalValue);
            return new JsonObject
            {
                ["k"] = "new",
                ["type"] = TypeJson.Write(declaredType),
                ["argTypes"] = new JsonArray
                {
                    TypeJson.Fqn("System.Int32"), TypeJson.Fqn("System.Int32"), TypeJson.Fqn("System.Int32"),
                    TypeJson.Fqn("System.Boolean"), TypeJson.Fqn("System.Byte"),
                },
                ["args"] = new JsonArray
                {
                    MetadataConst("System.Int32", bits[0]),
                    MetadataConst("System.Int32", bits[1]),
                    MetadataConst("System.Int32", bits[2]),
                    MetadataConst("System.Boolean", (bits[3] & unchecked((int)0x80000000)) != 0),
                    MetadataConst("System.Byte", (bits[3] >> 16) & 0xff),
                },
            };
        }

        // DateTimeConstantAttribute likewise surfaces an actual DateTime. Its metadata contract carries ticks (and
        // therefore an Unspecified kind), exactly the public DateTime(long ticks) construction below. A zero/default
        // DateTime took the generic null/value-type path above, which intentionally emits initobj instead.
        if (type.FullName == "System.DateTime" && value is DateTime dateTimeValue)
            return new JsonObject
            {
                ["k"] = "new",
                ["type"] = TypeJson.Write(declaredType),
                ["argTypes"] = new JsonArray { TypeJson.Fqn("System.Int64") },
                ["args"] = new JsonArray { MetadataConst("System.Int64", dateTimeValue.Ticks) },
            };

        // ilemit emits a const from its declared CIR type and must not reinterpret the JSON value. Validate the exact
        // reflection carrier here, where the CLR parameter declaration and its Constant/custom-constant row coexist.
        // A reference slot is the one legal exception to exact identity: CLR optional metadata can surface, for
        // example, an Int32 or String carrier on an Object parameter. Resolve it in this same MetadataLoadContext,
        // prove the declared slot accepts it, then make the required boxing/upcast explicit in CIR. A mismatched value-type,
        // enum, or generic slot remains unrepresentable rather than being converted or reinterpreted.
        if (!LiteralValueInhabits(type, value))
        {
            bool isValueType;
            try { isValueType = type.IsValueType; }
            catch { return null; }
            if (isValueType || type.IsGenericParameter || value.GetType().FullName is not string carrierName)
                return null;

            Type carrierType;
            try { carrierType = mlc?.CoreAssembly.GetType(carrierName, throwOnError: false, ignoreCase: false); }
            catch { return null; }
            bool acceptsCarrier;
            try { acceptsCarrier = carrierType != null && type.IsAssignableFrom(carrierType); }
            catch { return null; }
            if (!acceptsCarrier) return null;

            var carrierDeclaredType = DeclarationTypeNode(carrierType);
            var carrierValue = MetadataConstantNode(carrierType, carrierDeclaredType, value, mlc);
            if (carrierDeclaredType == null || carrierValue == null) return null;
            return new JsonObject
            {
                ["k"] = "cast",
                ["type"] = TypeJson.Write(declaredType),
                ["e"] = carrierValue,
            };
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
            float v when float.IsNaN(v) || float.IsInfinity(v) =>
                JsonValue.Create(v.ToString("R", CultureInfo.InvariantCulture)),
            double v when double.IsNaN(v) || double.IsInfinity(v) =>
                JsonValue.Create(v.ToString("R", CultureInfo.InvariantCulture)),
            float v => JsonValue.Create(v),
            double v => JsonValue.Create(v),
            _ => null,
        };
        if (value is not null && jsonValue is null) return null;
        return new JsonObject
        {
            ["k"] = "const",
            ["type"] = TypeJson.Write(declaredType),
            ["value"] = jsonValue,
        };
    }

    static bool LiteralValueInhabits(Type type, object value) => type.FullName switch
    {
        "System.Boolean" => value is bool,
        "System.Char" => value is char,
        "System.SByte" => value is sbyte,
        "System.Byte" => value is byte,
        "System.Int16" => value is short,
        "System.UInt16" => value is ushort,
        "System.Int32" => value is int,
        "System.UInt32" => value is uint,
        "System.Int64" => value is long,
        "System.UInt64" => value is ulong,
        "System.Single" => value is float,
        "System.Double" => value is double,
        "System.String" => value is string,
        _ => false,
    };

    static JsonObject MetadataConst(string type, object value) => new()
    {
        ["k"] = "const",
        ["type"] = TypeJson.Fqn(type),
        ["value"] = JsonValue.Create(value),
    };

    // MetadataLoadContext types are metadata-only and cannot be passed to Convert.ChangeType as a runtime Type.
    // Normalize through the exact legal enum-underlying identity instead; this also makes signedness and width
    // explicit before the bit pattern becomes the invariant-culture CIR string.
    static string EnumConstantText(object value, Type underlying)
    {
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        try
        {
            return underlying.FullName switch
            {
                "System.SByte" => Convert.ToSByte(value, culture).ToString(culture),
                "System.Byte" => Convert.ToByte(value, culture).ToString(culture),
                "System.Int16" => Convert.ToInt16(value, culture).ToString(culture),
                "System.UInt16" => Convert.ToUInt16(value, culture).ToString(culture),
                "System.Int32" => Convert.ToInt32(value, culture).ToString(culture),
                "System.UInt32" => Convert.ToUInt32(value, culture).ToString(culture),
                "System.Int64" => Convert.ToInt64(value, culture).ToString(culture),
                "System.UInt64" => Convert.ToUInt64(value, culture).ToString(culture),
                _ => null,
            };
        }
        catch { return null; }
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

    // A method's structural comparison signature, used to overload-disambiguate a top-level @ClrIntrinsic
    // (sqrt(Double) vs sqrt(Float); pow(Double,Double) intrinsic vs pow(Double,Int) real-body).
    static SignatureKey SigKeyOf(ParameterInfo[] ps, bool relaxed = false) =>
        new(ps.Select(p => ParamKey(p.ParameterType, relaxed)));

    /// A signature key with every nominal position that cannot be folded collapsed to [TypeKeyKind.Reference]. The two sides of a
    /// @KotlinDefault lookup describe the same parameter in DIFFERENT spaces — a call site carries kotc's pre-lowering
    /// Kotlin type (`kotlin.collections.List`), a reference assembly its lowered CLR form
    /// (`System.Collections.Generic.IReadOnlyList`) — so only a structurally folded kind is comparable, and
    /// anything else has to collapse. That still separates an overload differing in a folded position
    /// (`f(String, String)` from `f(String, List&lt;String&gt;)`), which is what the exact key cannot do here. Two
    /// overloads differing only between two DIFFERENT class types collapse together, are recorded as a conflict, and are
    /// refused rather than guessed.
    static bool IsValueKey(TypeKey key) => key.Kind is
        TypeKeyKind.Int8 or TypeKeyKind.Int16 or TypeKeyKind.Int32 or TypeKeyKind.Int64 or
        TypeKeyKind.Float32 or TypeKeyKind.Float64 or TypeKeyKind.Boolean or TypeKeyKind.Char or
        TypeKeyKind.UInt8 or TypeKeyKind.UInt16 or TypeKeyKind.UInt32 or TypeKeyKind.UInt64;

    /// Record one declaration's @KotlinDefault carriers under BOTH keys the splice can look up: `owner|name|arity|sigKey`
    /// (the exact overload — a call site reproduces that signature from its own declared parameter vector) and
    /// `owner|name|arity` (the fallback when no signature is available). The arity key is written once; a SECOND
    /// declaration of the same name+arity whose defaults differ marks it CONFLICTED, so the fallback refuses instead of
    /// serving whichever declaration the metadata scan happened to reach last.
    static void AddKotlinDefaults(ReferenceDotKtMetadata metadata, string ownerFqn, string name, ParameterInfo[] ps,
        Dictionary<int, string> defaults)
    {
        var arityKey = new DefaultKey(ownerFqn, name, ps.Length);
        var sig = SigKeyOf(ps);
        // The callee's DECLARED parameter types, for a call site that carries none of its own — a constructor
        // DELEGATION rides the ctor declaration, so `baseArgs` is a bare array with no signature vector. The splice
        // needs them to type the temp it binds each spliced value to.
        Put(new DefaultKey(ownerFqn, name, ps.Length, sig), defaults);
        Put(new DefaultKey(ownerFqn, name, ps.Length, SigKeyOf(ps, relaxed: true), Relaxed: true), defaults);
        if (metadata.KotlinDefaults.TryGetValue(arityKey, out var prior))
        {
            if (!SameDefaults(prior, defaults)) metadata.KotlinDefaultsConflicted.Add(arityKey);
            return;
        }
        metadata.KotlinDefaults[arityKey] = defaults;

        void Put(DefaultKey key, Dictionary<int, string> d)
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
        "kotlin.Int" => "int",
        "kotlin.Long" => "long",
        "kotlin.Short" => "short",
        "kotlin.Byte" => "sbyte",
        "kotlin.Char" => "char",
        "kotlin.Double" => "double",
        "kotlin.Float" => "float",
        "System.Int32" => "int",
        "System.Int64" => "long",
        "System.Int16" => "short",
        "System.SByte" => "sbyte",
        "System.Char" => "char",
        "System.Double" => "double",
        "System.Single" => "float",
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

    // A STRUCTURED TypeNode from a reflected ref.dll type — the pure-Kotlin identity kotc would have emitted (the ref
    // surface's types ARE named kotlin.* — kotlin.collections.List<kotlin.String>, kotlin.Int, …). Used to carry a
    // top-level fn / member RETURN type so bir2cir StaticType (#59) can recover a `callStatic`/`callInstance` whose
    // node lacks a `ret` (a non-generic call — kotc emits `ret` only for a generic call). Covers the shapes StaticType
    // needs (Fqn+args for collection detect, nullable, array, primitive, tv); a delegate/func return is left null.
    static TypeNode TypeNodeOf(Type type)
    {
        if (type.IsByRef) return TypeNodeOf(type.GetElementType()!) is TypeNode e0 ? new TypeNode.ByRef(e0) : null;
        if (type.IsPointer) return TypeNodeOf(type.GetElementType()!) is TypeNode ep ? new TypeNode.Ptr(ep) : null;
        if (type.IsArray)
        {
            if (TypeNodeOf(type.GetElementType()!) is not TypeNode e1) return null;
            return type.IsSZArray ? new TypeNode.Array(e1) : TypeNode.Array.General(e1, type.GetArrayRank());
        }
        if (type.IsGenericParameter) return null;   // an unresolved fn type-param: no useful static identity
        if (IsDelegate(type)) return null;
        if (type.IsConstructedGenericType)
        {
            var def = type.GetGenericTypeDefinition();
            var args = type.GetGenericArguments().Select(TypeNodeOf).ToArray();
            if (IsNullableDefinition(def)) return args[0] is TypeNode nv ? new TypeNode.Nullable(nv) : null;
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
        if (type.IsPointer)
            return DeclarationTypeNode(type.GetElementType()!) is TypeNode ep ? new TypeNode.Ptr(ep) : null;
        if (type.IsArray)
        {
            if (DeclarationTypeNode(type.GetElementType()!) is not TypeNode e1) return null;
            return type.IsSZArray ? new TypeNode.Array(e1) : TypeNode.Array.General(e1, type.GetArrayRank());
        }
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
            if (IsNullableDefinition(def)) return new TypeNode.Nullable(args[0]);
            return new TypeNode.Fqn(DottedFqn(StripGenericArity(def.FullName ?? def.Name)), args);
        }
        var prim = PrimitiveBirName(type);
        return new TypeNode.Fqn(prim ?? DottedFqn(StripGenericArity(type.FullName ?? type.Name)));
    }

    internal static void SelfTest()
    {
        var reflectedTypeParameter = typeof(List<>).GetGenericArguments()[0];
        var reflectedArrays = new[]
        {
            reflectedTypeParameter.MakeArrayType(2),
            reflectedTypeParameter.MakeArrayType(1),
            reflectedTypeParameter.MakeArrayType(),
        };
        var expectedArrays = new TypeNode[]
        {
            TypeNode.Array.General(new TypeNode.Tv("type", 0), 2),
            TypeNode.Array.General(new TypeNode.Tv("type", 0), 1),
            new TypeNode.Array(new TypeNode.Tv("type", 0)),
        };
        for (var i = 0; i < reflectedArrays.Length; i++)
            if (DeclarationTypeNode(reflectedArrays[i]) != expectedArrays[i])
                throw new InvalidOperationException(
                    "ReferenceMetadataIndex self-test dropped a reflected general-array rank/vector facet");

        var closedGeneralArray = typeof(string).MakeArrayType(2);
        if (TypeNodeOf(closedGeneralArray)
            != TypeNode.Array.General(new TypeNode.Fqn("string"), 2))
            throw new InvalidOperationException(
                "ReferenceMetadataIndex self-test dropped a static-result general-array rank/vector facet");

        var openContextFunction = new TypeNode.Fn(
            Suspend: false,
            Ret: new TypeNode.Fqn("System.Object"),
            Params: Array.Empty<TypeNode>(),
            Clr: "System.Func",
            Ctx: new TypeNode[] { new TypeNode.Tv("type", 0) });
        var expectedContextFunction = new TypeNode.Fn(
            Suspend: false,
            Ret: new TypeNode.Fqn("object"),
            Params: Array.Empty<TypeNode>(),
            Clr: "System.Func",
            Ctx: new TypeNode[] { new TypeNode.Tv("type", 0) });
        if (OpenPhysical(openContextFunction) != expectedContextFunction)
            throw new InvalidOperationException(
                "ReferenceMetadataIndex self-test dropped an open function context or CLR family");

        Console.WriteLine("[reference declaration types] self-test OK (general arrays + function facets)");
    }

    static bool IsFunc(Type type) =>
        type.Namespace == "System" && type.Name.StartsWith("Func`", StringComparison.Ordinal);

    // Reference types live in a MetadataLoadContext and are not reference-equal to runtime typeof(...) values.
    // Nullable is a metadata identity, so recognize its generic definition by that identity at the reflection edge.
    static bool IsNullableDefinition(Type type) =>
        type?.IsGenericTypeDefinition == true && type.FullName == "System.Nullable`1";

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

    internal readonly record struct OwnerTypeIdentity(string Name, int Arity);

    static OwnerTypeIdentity OwnerIdentity(string name, int arity) =>
        new(DottedFqn(StripGenericArity(name)), arity);

    // A flattened Kotlin/BIR identity does not encode which nested TypeDef segment owns each generic slot.
    // Preserve that loss of information as an ambiguous null entry instead of letting reflection or reference order
    // select between legal spellings such as Outer`1+Leaf`1 and Outer+Leaf`2.
    static void AddExactPhysicalTypeName(
        Dictionary<OwnerTypeIdentity, string> index, OwnerTypeIdentity identity, string exact)
    {
        if (!index.TryAdd(identity, exact) && index[identity] != exact)
            index[identity] = null;
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

    internal static JsonNode GenericParamDeclaration(Type gp)
    {
        var declaration = new JsonObject { ["name"] = gp.Name };
        var constraints = new JsonArray();
        foreach (var constraint in gp.GetGenericParameterConstraints())
            if (DeclarationTypeNode(constraint) is TypeNode node)
                constraints.Add(TypeJson.Write(NormalizeNestedNames(node)));
        if (constraints.Count != 0) declaration["constraints"] = constraints;

        var special = new JsonArray();
        var attrs = gp.GenericParameterAttributes;
        if ((attrs & GenericParameterAttributes.ReferenceTypeConstraint) != 0) special.Add("class");
        if ((attrs & GenericParameterAttributes.NotNullableValueTypeConstraint) != 0) special.Add("struct");
        if ((attrs & GenericParameterAttributes.DefaultConstructorConstraint) != 0) special.Add("new");
        if ((attrs & GenericParameterAttributes.AllowByRefLike) != 0) special.Add("allowsRefStruct");
        if (special.Count != 0) declaration["specialConstraints"] = special;
        return declaration;
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
    public readonly Dictionary<string, string> AliasKinds = new(StringComparer.Ordinal);
    public readonly Dictionary<ReferenceMetadataIndex.OwnerTypeIdentity, string> TypeKinds = new();
    public readonly Dictionary<string, string> PhysicalTypeKinds = new(StringComparer.Ordinal);
    public readonly HashSet<ReferenceMetadataIndex.OwnerTypeIdentity> ByRefLikeOwners = new();
    public readonly HashSet<string> ByRefLikePhysicalOwners = new(StringComparer.Ordinal);
    public readonly HashSet<string> DotKtOwners = new(StringComparer.Ordinal);             // producer-marked DotKt assembly types
    public readonly Dictionary<string, ReferenceMetadataIndex.RichEnumMetadata> RichEnums = new(StringComparer.Ordinal);
    public readonly Dictionary<string, BasicEnumMetadata> BasicEnums = new(StringComparer.Ordinal);
    public readonly Dictionary<string, string> ExistentialPhysicalBySemanticOwner = new(StringComparer.Ordinal);
    public readonly HashSet<string> FileClassOwners = new(StringComparer.Ordinal);         // trusted [KotlinFileClass] types
    public readonly Dictionary<string, bool> CompanionStaticByPhysicalOwner = new(StringComparer.Ordinal);
    public readonly Dictionary<string, string> SingletonCompanionCarrierBySemanticOwner = new(StringComparer.Ordinal);
    public readonly Dictionary<string, string> CompanionCarrierByPhysicalOwner = new(StringComparer.Ordinal);
    public readonly Dictionary<string, string> CompanionSourceNameByPhysicalOwner = new(StringComparer.Ordinal);
    // owner + semantic receiver + member role + Kotlin source name -> exact MethodDef/FieldDef name.
    public readonly Dictionary<string, string> CompanionExtensionMembers = new(StringComparer.Ordinal);
    public readonly Dictionary<string, string> CompanionPhysicalOwnerBySemanticType = new(StringComparer.Ordinal);
    public readonly Dictionary<string, string> GenericStaticCarrierBySemanticOwner = new(StringComparer.Ordinal);
    public readonly Dictionary<string, string> CompanionSemanticOwnerByCarrier = new(StringComparer.Ordinal);
    public readonly Dictionary<string, int> TypeArity = new(StringComparer.Ordinal);       // ownerFqn -> generic arity
    public readonly Dictionary<string, string[]> TypeParamNames = new(StringComparer.Ordinal); // ownerFqn -> generic param names
    public readonly Dictionary<string, string> TypeParamDeclarations = new(StringComparer.Ordinal); // ownerFqn -> exact descriptor array JSON
    public readonly HashSet<ReferenceMetadataIndex.OwnerTypeIdentity> PublicParameterlessConstructibleOwners = new();
    public readonly HashSet<string> PublicParameterlessConstructiblePhysicalOwners = new(StringComparer.Ordinal);
    public readonly Dictionary<string, TypeNode[]> CtorParamTypes = new(StringComparer.Ordinal); // ownerFqn -> sole ctor parameter types
    public readonly Dictionary<string, string[]> TypeParamConstraints = new(StringComparer.Ordinal); // ownerFqn -> per-param "struct"/"class"/"unconstrained"
    public readonly Dictionary<string, TypeNode[]> TypeParamBounds = new(StringComparer.Ordinal); // DOTTED ownerFqn -> per-param declared bound TypeNode (null when unconstrained/objectish)
    public readonly HashSet<string> HelperTypes = new(StringComparer.Ordinal);            // emitted "dotkt$ClrH_*" rule-3 helpers
    // Types carrying @kotlin.coroutines.RestrictsSuspension (BINARY-retained, so present on the ref.dll). A suspend
    // lambda whose RECEIVER is such a scope (e.g. SequenceScope) gets the RestrictedSuspendLambda SM base (bundle-6 P5).
    public readonly HashSet<string> RestrictsSuspensionTypes = new(StringComparer.Ordinal);
    public readonly List<MemberBinding> MemberBindings = new();                           // per-member @ClrIntrinsic + shape
    public readonly List<MethodImplBinding> MethodImplBindings = new();                   // trusted exact accessor bridge -> CLR slot
    public readonly List<CtorBinding> CtorBindings = new();                               // per-ctor declaration shape (#86 D1)
    public readonly List<ReferencedAliasConstructorAdapter> AliasConstructorAdapters = new();
    public readonly Dictionary<ReferenceMetadataIndex.OwnerTypeIdentity, ReferenceTypeShape> TypeShapes = new();
    public readonly Dictionary<string, ReferenceTypeShape> PhysicalTypeShapes = new(StringComparer.Ordinal);
    public readonly Dictionary<ReferenceMetadataIndex.OwnerTypeIdentity, string> ExactPhysicalTypeByDottedName = new();
    public readonly Dictionary<string, string> PhysicalTypeBySemanticName = new(StringComparer.Ordinal);
    public readonly Dictionary<string, int> InnerCapturedCount = new(StringComparer.Ordinal);
    public readonly Dictionary<string, string> InnerSemanticOwner = new(StringComparer.Ordinal);
    // [KotlinInline] raw-BIR payloads (#71/#75): "owner|name|pc|ga" -> the candidate decoded carrier JSONs (one per overload).
    public readonly Dictionary<string, List<string>> InlinePayloads = new(StringComparer.Ordinal);
    public readonly Dictionary<string, string> InlinePayloadsByDeclarationId = new(StringComparer.Ordinal);
    // Top-level fun name -> its @ClrIntrinsic fully-qualified static target ("System.Diagnostics.Stopwatch.GetTimestamp").
    // A top-level fun is a static method of a [KotlinFileClass] type; its call site is `callStatic owner=null`.
    public readonly Dictionary<string, string> TopLevelIntrinsics = new(StringComparer.Ordinal);
    public readonly Dictionary<(string Name, ReferenceMetadataIndex.SignatureKey Signature), string> TopLevelIntrinsicsBySig = new();
    public readonly HashSet<string> AmbiguousTopLevelIntrinsics = new(StringComparer.Ordinal);
    // Top-level @ClrIntrinsic fun name -> the 0-based parameter positions its bound BCL static takes BY REFERENCE
    // (@ClrRefArgument). The substituted clrStatic wraps these argTypes positions in ByRef nodes (tryParseInt32's `out result`,
    // Interlocked's `ref location`, Math.DivRem's `out remainder`). Absent when the fun has no byref parameter.
    public readonly Dictionary<string, int[]> TopLevelIntrinsicByref = new(StringComparer.Ordinal);
    // Bare-@ClrIntrinsic extension fun, keyed "funName|recvKey" (recvKey = the receiver/first-param type) -> the BCL
    // member name. Receiver-keyed because the bare name collides across receivers (set->set_Item vs set->set_Chars).
    public readonly Dictionary<(string Name, ReferenceMetadataIndex.SignatureKey Signature), string> ExtMemberIntrinsics = new();
    // @JvmInline value-class owner FQN -> (its single backing-field getter "get_data", the field's CLR conv token).
    // The class is ERASED to its primitive CLR form, so `get_data()` is the inline unbox: it collapses to the receiver
    // value conv'd to the field's declared type (a `conv`, never a `ldfld data` — the erased primitive has no field).
    public readonly Dictionary<string, (string Getter, string Conv)> InlineBacking = new(StringComparer.Ordinal);
    // NON-intrinsic top-level funs (real Kotlin bodies in a [KotlinFileClass]) -> their (file-class owner FQN, first-
    // param recvKey). Keyed by fun name. Lets an APP build resolve a referenced `callStatic owner=null` to the file-
    // class it actually lives in (getOrElse -> kotlin.collections._CollectionsKt), disambiguated by the call's receiver
    // type when the name is defined across multiple file-classes (CollectionsKt vs ArraysKt vs MapsKt). NOT consulted in
    // a stdlib self-build (the fun is local there; owner=null + FindStatic finds the sibling).
    public readonly Dictionary<string, List<(string Owner, string RecvKey, ReferenceMetadataIndex.TypeKey ParamKey)>> TopLevelStatics = new(StringComparer.Ordinal);
    // Collection/array FACTORY top-level funs, keyed by fun NAME -> the factory kind. A @kotlin.clr.ClrCollectionFactory
    // ("list"/"set"/"map") or @kotlin.clr.ClrArrayFactory ("vararg"/"sized") marker on a [KotlinFileClass] static.
    // MemberCallSubstitution reads these on a `callStatic owner=null` (listOf/setOf/mapOf/arrayOf/intArrayOf/arrayOfNulls)
    // and realizes the corresponding `{k:newList/newSet/newMap/newArray/newArraySized}` CIR construction. Keyed by name
    // alone: every overload of a factory name shares the kind, so no receiver disambiguation is needed.
    public readonly Dictionary<string, string> CollectionFactories = new(StringComparer.Ordinal);
    public readonly Dictionary<string, string> ArrayFactories = new(StringComparer.Ordinal);
    public readonly Dictionary<string, string> ArrayFactoryElemHints = new(StringComparer.Ordinal); // concrete-primitive elem (spread call)
    // A defaulted parameter's default-value expression as BIR (from @KotlinDefault), for CROSS-MODULE splice of an
    // omitted argument. Keyed "ownerFqn|methodName|paramCount" -> (argPosition -> BIR-json string). The DefaultArgSplice
    // pass reads this to fill trailing omitted args BEFORE the CharSequence bridge + type lowering (so a String default
    // is coerced exactly like an explicit arg). Rides the ref.dll only (param attrs stripped in the rt build).
    public readonly Dictionary<ReferenceMetadataIndex.DefaultKey, Dictionary<int, string>> KotlinDefaults = new();
    public readonly Dictionary<string, Dictionary<int, string>> KotlinDefaultsByDeclarationId = new(StringComparer.Ordinal);
    // Keys of [KotlinDefaults] that TWO declarations of the same owner+name+arity carry with DIFFERENT defaults — the key
    // cannot tell them apart, so the splice must refuse instead of filling whichever was enumerated last. Populated for
    // both METHODS and CONSTRUCTORS (same-arity overloads are common; #235).
    public readonly HashSet<ReferenceMetadataIndex.DefaultKey> KotlinDefaultsConflicted = new();
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

// Exact MethodDef selected together with a nullable-generic slot. TypeParams are kept in the current referenced
// owner's declaration frame and are mapped through each supertype edge in lockstep with the slot types.
sealed record MethodSlotIdentity(string PhysicalMember, JsonArray TypeParams);

// `ReturnType` is the best-effort STATIC-RESULT projection (TypeNodeOf): it drops a generic parameter, because its
// consumers want a usable concrete identity or nothing. `ReturnTypeNode` is the DECLARATION projection
// (DeclarationTypeNode), the same one `ParamTypeNodes` uses, which keeps generic parameters as `Tv` — a declaration
// the caller substitutes. The two are not interchangeable: `Iterable<E>.iterator()` is `Iterator` in the first and
// `Iterator<!0>` in the second, and only the second says what the call site's type argument completes.
sealed record MemberBinding(string Owner, string Name, int ParamCount, string Intrinsic, bool IsAbstract, bool IsStatic, int PropertyAccess = 0, string PropertyName = null, int[] ByrefPositions = null, bool Suspend = false, bool Conv = false, TypeNode ConvTo = null, TypeNode ReturnType = null, int MethodArity = 0, TypeNode[] ParamTypeNodes = null, bool IsVirtual = false, TypeNode KotlinReturnType = null, TypeNode SuspendReturnType = null, TypeNode NullableGenericRet = null, TypeNode[] NullableGenericParams = null, TypeNode ReturnTypeNode = null, int MetadataToken = 0, string SourcePropertyName = null, string AccessorKind = null, string AssociatedPropertyName = null, bool IsPropertyBridge = false, bool IsPublic = false, string PropertyAssociation = null, string SourcePropertyAssociation = null, string SourceMethodName = null, JsonArray MethodTypeParams = null, string DeclarationId = null, string DeclarationSourceName = null, string DeclarationPhysicalOwner = null, TypeNode[] DeclarationSemanticParams = null, TypeNode DeclarationSemanticReturn = null, string CollectionFactoryKind = null, string ArrayFactoryKind = null, string ArrayFactoryElementHint = null, int CountStart = -1, int CountEnd = -1, int[] SemanticReifiedTypeParameterIndices = null, int[] NullableWitnessTypeParameterIndices = null, TypeNode[] KotlinParameterTypes = null, string InnerConstructorOwner = null, TypeNode[] InnerConstructorParameters = null, int[] InnerConstructorTypeArguments = null);

sealed record ReferencedMethodDeclaration(string PhysicalMember, TypeNode[] Parameters, TypeNode Return,
    JsonArray TypeParams);

sealed record ReferencedUnsafeAccessorMethod(string PhysicalMember, TypeNode[] Parameters, TypeNode Return,
    JsonArray TypeParams, TypeNode NullableGenericReturn);

sealed record MethodImplBinding(string BodyOwner, int BodyToken, TypeNode.Fqn DeclarationOwner,
    string DeclarationMember);

sealed record ReferencedPropertyMethodImpl(string SourceMember, TypeNode.Fqn DeclarationOwner, string DeclarationMember,
    TypeNode[] Parameters, TypeNode Return, int MethodArity, JsonArray TypeParams);

// The exact authored binding selected from a complete declaration identity. Carrying this value across the alias-
// companion rewrite prevents a later name+arity lookup from silently selecting a different overload.
sealed record ExactClrMemberBinding(string Intrinsic, int PropertyAccess, string PropertyName,
    bool Conv, TypeNode ConvTo, int[] ByrefPositions, int CountStart, int CountEnd);

// A referenced CONSTRUCTOR's declaration shape. A `new` is a call whose declaration is the owner's constructor, so the
// nullable-generic realign types its arguments exactly as it types a method call's — and a ctor has no name of its own,
// so the key is owner + declared parameter count. `ParamTypeNodes` is the physical CLR signature with generic
// parameters retained; `NullableGenericParams[i]` is the pre-erasure `[KotlinNullableGeneric]` carrier of that slot
// when it has one.
sealed record CtorBinding(string Owner, string PhysicalOwner, int ParamCount, TypeNode[] ParamTypeNodes,
    TypeNode[] NullableGenericParams);

sealed record ReferencedAliasConstructorAdapter(string Owner, AliasConstructorAdapter Adapter);
