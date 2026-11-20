using static ReferenceMetadataReader;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using System.Collections.Immutable;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;
using DotKt.Toolchain;

internal delegate bool ValueTypeOracle(TypeNode.Fqn type);

sealed partial class ReferenceMetadataIndex : IDisposable
{

    readonly List<ReferenceAssembly> _assemblies;
    readonly ManagedReferenceCatalog _compileRefs;
    internal string CompileStdlibName => _compileRefs.CompileStdlibName;

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
    readonly HashSet<string> _conflictingRichEnums = new(StringComparer.Ordinal);
    readonly Dictionary<string, BasicEnumMetadata> _basicEnums = new(StringComparer.Ordinal);
    readonly HashSet<string> _conflictingBasicEnums = new(StringComparer.Ordinal);
    // Trusted [KotlinType(G<*,...>)] on a compiler-generated non-generic interface is the explicit existential ABI
    // relation. No physical-name suffix participates in recognition.
    readonly Dictionary<string, string> _existentialPhysicalBySemanticOwner = new(StringComparer.Ordinal);
    readonly Dictionary<string, List<(string Owner, int[] Slots, string Declaration, string Invoke)>> _managedReferenceContracts = new(StringComparer.Ordinal);
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
    readonly Dictionary<string, IReadOnlyList<BirTypeParameter>> _ownerTypeParamDeclarations = new(StringComparer.Ordinal);
    readonly Dictionary<string, NullableRepresentationFrame> _ownerNullableFrames = new(StringComparer.Ordinal);
    public IReadOnlyDictionary<string, NullableRepresentationFrame> NullableTypeFrames => _ownerNullableFrames;
    readonly Dictionary<string, NullableRepresentationFrame> _kotlinConstructorOwnerFrames = new(StringComparer.Ordinal);
    public IReadOnlyDictionary<string, NullableRepresentationFrame> KotlinConstructorOwnerFrames => _kotlinConstructorOwnerFrames;
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

    internal TypeNode[] DeclarationSourceParameters(string id) =>
        id != null && _declarationById.TryGetValue(id, out var binding)
            ? binding.DeclarationSemanticParams : null;

    internal TypeNode[] DeclarationOverrideParameters(string id)
    {
        if (id == null || !_declarationById.TryGetValue(id, out var binding)) return null;
        if (binding.DeclarationSemanticParams != null) return binding.DeclarationSemanticParams;
        // Virtual families retain their source slots on the parameter carriers
        // rather than an identity-allocated semantic signature. Consume those
        // explicit facts; do not invert physical generic positions.
        return binding.NullableGenericParams?.Any(parameter => parameter != null) == true
            ? binding.ParamTypeNodes.Select((parameter, index) =>
                binding.NullableGenericParams[index] ?? parameter).ToArray()
            : null;
    }

    internal (string Name, TypeNode[] Parameters, TypeNode Return) AliasHelperSourceSignature(string id, string ownerName)
    {
        if (!_declarationById.TryGetValue(id, out var binding) || binding.IsStatic
            || BareOwnerFqn(binding.Owner) != BareOwnerFqn(ownerName))
            throw new InvalidOperationException($"Alias helper source '{id}' does not select an instance declaration on '{ownerName}'");
        var signature = PhysicalDeclarationSignature(binding);
        return (binding.Name, signature.Parameters, signature.Return);
    }

    static (TypeNode[] Parameters, TypeNode Return) PhysicalDeclarationSignature(MemberBinding binding) =>
        (binding.SignatureParameters, binding.SignatureReturn);

    internal TypeNode DeclarationSignatureSlot(ParameterInfo parameter) =>
        ReadDeclarationSignatureSlot(parameter, _nativeSignatures);

    static TypeNode ReadDeclarationSignatureSlot(ParameterInfo parameter, NativeSignatureReader signatures)
    {
        if (parameter.Member is MethodBase method && signatures.Method(method) is { } native)
            return parameter.Position < 0 ? native.Return : native.Parameters[parameter.Position];
        TypeNode type = DeclarationTypeNode(parameter.ParameterType);
        foreach (var modifier in parameter.GetOptionalCustomModifiers().Reverse())
            type = new TypeNode.Mod(false, DeclarationTypeNode(modifier), type);
        foreach (var modifier in parameter.GetRequiredCustomModifiers().Reverse())
            type = new TypeNode.Mod(true, DeclarationTypeNode(modifier), type);
        return type;
    }

    // Borrowed read-only declaration facts, as in the shared decoder's contract.
    internal NativeSignatureReader.Signature NativeMethodSignature(MethodBase method) => _nativeSignatures.Method(method);
    internal TypeNode NativeFieldSignature(FieldInfo field) => _nativeSignatures.Field(field);

    internal ConstructorInfo SelectConstructorDeclaration(IEnumerable<ConstructorInfo> constructors, string identity) =>
        SelectDeclarationTwin(constructors, constructor => KotlinDeclarationIdentityPayload(
            constructor.GetCustomAttributesData(), constructor.DeclaringType?.Assembly, 0)?.Id, identity);

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

    internal TypeNode DeclarationIdentityReturn(string id, string owner, int methodArity, bool isStatic) =>
        id != null && _declarationById.TryGetValue(id, out var binding)
            && BareOwnerFqn(binding.DeclarationPhysicalOwner ?? binding.Owner) == BareOwnerFqn(owner)
            && binding.MethodArity == methodArity && binding.IsStatic == isStatic
                ? binding.ReturnTypeNode : null;

    readonly Dictionary<(string Owner, string Member), TypeNode> _collectionSlotReturns = new();

    internal TypeNode CollectionSlotReturn(string owner, string member)
    {
        if (_collectionSlotReturns.TryGetValue((owner, member), out var result)) return result;
        // The slot belongs to the non-generic TypeDef in the stdlib, not to other
        // references exposing an identically spelled owner at a different arity.
        var carrier = ResolveRefTypeIn(owner, CompileStdlibName);
        var method = carrier?.GetMethod(member, BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);
        return _collectionSlotReturns[(owner, member)] = method == null ? null : DeclarationTypeNode(method.ReturnType);
    }

    internal bool IsGenericScalarReturn(string id) =>
        _declarationById.TryGetValue(id, out var binding) && binding.KotlinReturnType is TypeNode.Tv
        && binding.ReturnTypeNode is TypeNode.Fqn { Name: "object" or "System.Object" };

    internal int[] GenericScalarParameters(string id) =>
        _declarationById.TryGetValue(id, out var binding) && binding.KotlinParameterTypes is { } source
        && binding.ParamTypeNodes is { } physical && source.Length == physical.Length
            ? Enumerable.Range(0, source.Length).Where(index => source[index] is TypeNode.Tv
                && physical[index] is TypeNode.Fqn { Name: "object" or "System.Object" }).ToArray()
            : Array.Empty<int>();

    // A preserved override edge selects this declaration before signature erasure. Return its complete open CLR
    // signature, including modifiers; the consumer closes the selected declaring owner through its supertype graph.
    internal bool TryDeclarationSignatureSlot(string id, out ReferencedDeclarationSlot declaration)
    {
        declaration = null;
        if (id == null || !_declarationById.TryGetValue(id, out var binding) || binding.IsStatic) return false;
        var owner = binding.DeclarationPhysicalOwner ?? binding.Owner;
        declaration = new ReferencedDeclarationSlot(owner, binding.Name, binding.MethodArity,
            binding.SignatureParameters ?? throw new InvalidOperationException($"Declaration '{id}' has no signature parameters"),
            binding.SignatureReturn ?? throw new InvalidOperationException($"Declaration '{id}' has no signature return"),
            _ownerKindByPhysicalOwner[owner] == "interface", binding.SignatureAllocated);
        return true;
    }

    internal bool IsPublicVirtualDeclaration(string id) =>
        _declarationById.TryGetValue(id, out var binding) && binding.IsPublic && binding.IsVirtual;

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
            || binding.SignatureParameters == null || binding.SignatureReturn == null)
            return false;
        declaration = new ReferencedUnsafeAccessorMethod(
            binding.Name,
            binding.SignatureParameters,
            binding.SignatureReturn,
            CloneReferencedTypeParameters(binding.MethodTypeParams),
            binding.NullableGenericRet,
            binding.IsPublic);
        return true;
    }

    // Class-virtual Kotlin declarations intentionally have no scalar declaration identity: their physical name is
    // shared by an override family rather than allocated per declaration. In that current-format case, select from
    // the producing assembly's semantic declaration carriers using the complete frontend-selected signature. This is
    // the same exact semantic selection used for referenced override slots, and refuses an ambiguous overload set.

    MemberBinding SelectUnsafeAccessorVirtualMethod(string ownerFqn, string sourceMember, int methodArity,
        bool isStatic, IReadOnlyList<TypeNode> signature, TypeNode resolvedReturn, TypeNode[] ownerTypeArguments,
        string selectedParameterShape)
    {
        if (!TryMembersByBirOwner(ownerFqn, out var members)) return null;
        var matches = members.Where(member => member.SourcePropertyName == null
                && member.IsStatic == isStatic
                && (member.DeclarationSourceName ?? member.SourceMethodName ?? member.Name) == sourceMember
                && member.MethodArity == methodArity
                && KotlinOverrideSlotBridge.MethodTypeParameterShapeKey(member.MethodTypeParams, ownerTypeArguments)
                    == selectedParameterShape
                && MethodSignatureMatches(member, signature, resolvedReturn, ownerTypeArguments)
                && member.SignatureParameters != null && member.SignatureReturn != null)
            .ToList();
        return matches.Count == 1 ? matches[0] : null;
    }

    internal ConstructorInfo UnsafeAccessorConstructor(TypeNode.Fqn owner, string identity)
    {
        var definition = ClrMemberResolution.ResolveOwnerType(owner, this)
            ?? throw new InvalidOperationException($"UnsafeAccessor constructor owner '{owner.Name}' has no referenced TypeDef");
        return SelectConstructorDeclaration(definition.GetConstructors(
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance), identity);
    }

    // Resolve the exact MethodDef named by the frontend-selected Kotlin declaration identity. The identity selects;
    // the semantic call signature only validates that the selected declaration still has the physical ABI this use
    // expects after every representation pass. A failed validation is not permission to search sibling overloads.
    public bool TryDeclarationIdentityMethod(
        IReadOnlySet<string> localTypes,
        string id,
        int methodArity,
        bool isStatic,
        IReadOnlyList<TypeNode> callSignature,
        Type callOwner,
        TypeNode[] ownerArguments,
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
            ? callSignature.Concat(Enumerable.Repeat<TypeNode>(KotlinRuntimeTypeWitness.Type,
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
        // A selected carrier slot also admits its recorded source classifier's construction. Other parameters
        // still validate in the exact physical frame; do not turn different generic positions into wildcards.
        var physicalMatches = selectedSignature.All(type => type != null)
            && selectedSignature.Select((type, index) =>
                DeclarationDescribesCall(type, completedCallSignature[index])
                || IsExistentialValueParameter(type)
                    && SemanticDeclarationDescribesCall(type, completedCallSignature[index])).All(matchesCall => matchesCall);
        // A super call can carry the selected declaration's constructed signature rather than its open
        // descriptor. Validate that exact instantiation through the same inheritance projection used by the
        // emitted memberRef; a type variable is not a wildcard and must not select another overload.
        if (!physicalMatches && selectedSignature.All(type => type != null)
            && ClrMemberReferences.DeclaringTypeRef(this, localTypes, selected, callOwner, ownerArguments) is TypeNode.Fqn constructed)
            physicalMatches = ConstructedDeclarationDescribesCall(selectedSignature, completedCallSignature,
                constructed.Args ?? Array.Empty<TypeNode>());
        // Reified declarations may have compiler-owned physical parameters that are intentionally absent from the
        // Kotlin semantic signature. Such a carrier can still identify the declaration, but it is not a complete
        // validator for this physical call shape.
        var semanticMatches = binding.DeclarationSemanticParams?.Length == callSignature.Count
            && binding.DeclarationSemanticParams.Select((type, index) =>
                SemanticDeclarationDescribesCall(type, callSignature[index])).All(matchesCall => matchesCall);
        // The source descriptor and this call's selected-declaration signature can
        // sit on opposite sides of frame materialization. Project the preserved
        // descriptor through its producer-owned frame, including function value
        // slots; never make different variable indices into matching wildcards.
        if (!semanticMatches && binding.DeclarationSemanticParams?.Length == callSignature.Count)
            semanticMatches = MaterializedDeclarationDescribesCall(binding, callSignature);
        // Generated cold entries retain each parameter's trusted type carrier, including suspend
        // function shapes whose CLR value slot is object. Validate those authored facts too;
        // do not treat object as a wildcard for a function or search a sibling overload.
        // A call can already project its scalar slots while retaining function carriers.
        // Validate each position against that exact MethodDef's physical or semantic slot.
        var parameterCarriersMatch = binding.KotlinParameterTypes?.Length == callSignature.Count
            && binding.KotlinParameterTypes.Select((type, index) =>
                SemanticDeclarationDescribesCall(type, callSignature[index])
                || selectedSignature[index] != null
                    && DeclarationDescribesCall(selectedSignature[index], completedCallSignature[index]))
                .All(matchesCall => matchesCall);
        var signatureMatches = !requiresParameterValidation || semanticMatches || physicalMatches || parameterCarriersMatch;
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
    bool MaterializedDeclarationDescribesCall(MemberBinding binding, IReadOnlyList<TypeNode> signature)
    {
        var policy = new GenericRepresentationPolicy(_ownerAlias, this);
        var types = new NullableRepresentationTypes(SourceHierarchyFrame(binding.Owner), binding.NullableFrame,
            _ownerNullableFrames, IsValueType, policy: policy,
            nativeOwnerSlots: policy.UsesNativeOwnerSlots(binding.Owner));
        return binding.DeclarationSemanticParams.Length == signature.Count
            && binding.DeclarationSemanticParams.Select((type, index) =>
                SemanticDeclarationDescribesCall(types.Slot(type), signature[index])).All(matches => matches);
    }
    static bool ConstructedDeclarationDescribesCall(IReadOnlyList<TypeNode> declaration,
        IReadOnlyList<TypeNode> call, TypeNode[] ownerArguments) => declaration.Count == call.Count
        && declaration.Select((type, index) => DeclarationDescribesCall(
            SupertypeGraph.SubstOwnerTvs(type, ownerArguments), call[index])).All(matches => matches);
    public int[] NullableWitnessTypeParameterIndices(string id) =>
        id != null && _declarationById.TryGetValue(id, out var binding)
            ? binding.NullableWitnessTypeParameterIndices
            : null;
    public int[] ReflectedWitnessIndices(string id) =>
        id != null && _declarationById.TryGetValue(id, out var binding) ? binding.ReflectedWitnessIndices : null;
    public NullableRepresentationFrame NullableMethodFrame(string id) =>
        id != null && _declarationById.TryGetValue(id, out var binding) ? binding.NullableFrame : null;

    internal MemberBinding AuthoredKotlinHelper(string owner, string name, int sourceArity,
        IReadOnlyList<TypeNode> sourceParameters, bool isStatic = true)
    {
        if (!TryMembersByBirOwner(owner, out var members))
            throw new InvalidOperationException($"Compiler-authored helper owner '{owner}' is absent");
        var matches = members.Where(member => member.IsStatic == isStatic && member.DeclarationId != null
            && (member.DeclarationSourceName ?? member.Name) == name
            && (member.NullableFrame?.SourceArity ?? member.MethodArity) == sourceArity
            && member.ParamCount == sourceParameters.Count).Where(member => {
                var parameters = member.DeclarationSemanticParams ?? member.KotlinParameterTypes ?? member.ParamTypeNodes;
                return parameters != null && parameters.Select((parameter, index) =>
                    SourceDeclarationDescribesCall(member.DeclarationSemanticParams == null
                        ? member.NullableGenericParams?[index] ?? parameter : parameter,
                        sourceParameters[index])).All(match => match);
            }).ToArray();
        if (matches.Length != 1)
            throw new InvalidOperationException($"Compiler-authored helper '{owner}.{name}' has {matches.Length} exact source declarations");
        return matches[0];
    }
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

    public int[] DeclarationFactoryVarargPositions(string id) =>
        _declarationById[id].VarargPositions;
    // ownerFqn -> declared parameter count -> the ctor declarations of that arity (#86 D1). A list, because a
    // same-arity overload set must be REFUSED rather than resolved by arity alone.
    readonly Dictionary<string, Dictionary<int, List<CtorBinding>>> _ctorsByOwner = new(StringComparer.Ordinal);
    readonly Dictionary<string, List<BirConstructorAdapterPayload>> _aliasConstructorAdaptersByOwner =
        new(StringComparer.Ordinal);

    public bool TryAliasConstructorAdapter(
        string owner, TypeNode[] signature, TypeNode[] ownerArgs, out BirConstructorAdapterPayload adapter)
    {
        adapter = null;
        owner = BareOwnerFqn(owner);
        if (!_aliasConstructorAdaptersByOwner.TryGetValue(owner, out var declared)) return false;
        var matches = declared
            .Select(candidate => AliasConstructorDelegationExpansion.Specialize(candidate, ownerArgs))
            .Where(candidate => SameTypeSequence(candidate.Signature.Value, signature))
            .ToArray();
        if (matches.Length > 1 && matches.Skip(1).Any(candidate => !ConstructorAdapterCarrier.Same(matches[0], candidate)))
            throw new InvalidOperationException(
                $"conflicting constructor-adapter metadata for alias '{owner}' signature "
                + $"({string.Join(", ", signature.Select(TypeNode.ToJson))})");
        if (matches.Length == 0) return false;
        adapter = matches[0];
        return true;
    }

    static bool SameTypeSequence(IReadOnlyList<TypeNode> left, IReadOnlyList<TypeNode> right)
    {
        if (left.Count != right.Count) return false;
        for (var i = 0; i < left.Count; i++) if (left[i] != right[i]) return false;
        return true;
    }

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
    IReadOnlyDictionary<string, string> _physicalTypeNamesSnapshot;
    readonly Dictionary<string, int> _innerCapturedCount = new(StringComparer.Ordinal);
    readonly Dictionary<string, string> _innerSemanticOwner = new(StringComparer.Ordinal);
    readonly Dictionary<string, string> _topLevelIntrinsics = new(StringComparer.Ordinal); // top-level fun name -> FQ static
    readonly Dictionary<(string Name, SignatureKey Signature), string> _topLevelIntrinsicsBySig = new();
    readonly HashSet<string> _ambiguousTopLevelIntrinsics = new(StringComparer.Ordinal); // names whose overloads bind to DIFFERENT statics (Math vs MathF)
    readonly Dictionary<string, int[]> _topLevelIntrinsicByref = new(StringComparer.Ordinal); // top-level fun name -> byref param positions
    readonly Dictionary<(string Name, SignatureKey Signature), string> _extMemberIntrinsics = new();
    readonly Dictionary<string, (string Getter, string Conv)> _inlineBacking = new(StringComparer.Ordinal);
    readonly Dictionary<string, List<(string Owner, string RecvKey, TypeKey ParamKey)>> _topLevelStatics = new(StringComparer.Ordinal);
    readonly Dictionary<string, string> _collectionFactories = new(StringComparer.Ordinal); // @ClrCollectionFactory fun name -> "list"/"set"
    readonly ReferenceExecutablePayloads _executablePayloads;

    // ---- .NET-interop resolution (A2 / #61): the LONG-LIVED metadata universe over the exact compile references.
    // NetInteropBinding resolves a dll2klib-projected owner FQN
    // ("System.Console", "Kfc.App") to a metadata-only System.Reflection.Type here and reflects its member SHAPE
    // (static/instance/property/field/indexer/generic) to bind the plain callStatic/callInstance kotc emitted by
    // identity into the CLR-codegen `clr*` vocabulary. kotc no longer decides the .NET call shape (layer purity —
    // this is the SAME "emit the identity, bind in bir2cir" pattern as the stdlib ref.dll, one axis over).
    // Reflection handles are borrowed from this fixed reference universe.
    // Pipeline owns its lifetime through this index, including exceptional exit.
    readonly ReferenceMetadataUniverse _universe;
    NativeSignatureReader _nativeSignatures;
    readonly Dictionary<string, (bool Found, TypeNode Type, BirLiteral Value)> _literalFieldCache =
        new(StringComparer.Ordinal);
    readonly Dictionary<string, bool> _volatileFieldCache = new(StringComparer.Ordinal);

    // The bare FQNs of every type DECLARED in THIS compilation (this run's BIR `types`). A local declaration is the
    // AUTHORITY for its identity — it wins over a referenced .NET/Kotlin dll that exports the SAME FQN (the #15
    // pathological layout: an app whose `**/*.kt` glob pulls in a nested ProjectReference lib's SOURCE — so it compiles
    // `demo.Plain` locally — AND references that lib's dll, which also exports `demo.Plain`). This mirrors the frontend
    // "source wins" fix: ResolveNetType refuses to bind a locally-emitted FQN to the ref, so every sibling resolution
    // routes to the this-assembly-emitted type — `new` stays a local `new` (not `newClr`), and a
    // callInstance/callStatic/field/boundDelegate stays owner-local (NetInteropBinding leaves it for the emitted-type
    // path) instead of reshaping to a `clr*` node against the ref. Replaced before transformation and after late synthesis.
    // SCOPE: this filters both reflection-backed resolver axes. The ref.dll's DotKt sidecar indexes (TypeKinds/IsValueType,
    // owner arity, ctor param types) are NOT filtered by this set — in the #15 layout they are populated from the SAME
    // source that produced the local decl, so they agree; a genuinely divergent stale-dll is out of scope (source-wins
    // is still the right precedence there, matching Roslyn CS0436). `@ClrTypeAlias`/`@ClrIntrinsic` maps are empty for a
    // user lib, so TryResolveClrOwner never mis-binds a local user type.
    IReadOnlySet<string> _localEmittedTypes = new HashSet<string>(StringComparer.Ordinal);
    public void SetLocalEmittedTypes(IReadOnlySet<string> fqns)
    {
        _localEmittedTypes = fqns.Select(fqn => DottedFqn(BareOwnerFqn(fqn)))
            .ToHashSet(StringComparer.Ordinal);
        _physicalTypeNamesSnapshot = null;
    }

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

    ReferenceMetadataIndex(List<ReferenceAssembly> assemblies, ManagedReferenceCatalog compileRefs,
        NativeSignatureReader nativeSignatures = null)
    {
        _nativeSignatures = nativeSignatures ?? new NativeSignatureReader();
        _assemblies = assemblies;
        _compileRefs = compileRefs;
        _universe = new ReferenceMetadataUniverse(compileRefs);
        _executablePayloads = new ReferenceExecutablePayloads(assemblies.Select(assembly =>
            new ReferenceExecutablePayloads.ScanInput(assembly.DotKt.KotlinDefaults,
                assembly.DotKt.KotlinDefaultsConflicted, assembly.DotKt.KotlinDefaultsByDeclarationId,
                assembly.DotKt.InlinePayloads, assembly.DotKt.InlinePayloadsByDeclarationId)));
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
                    if (existing.Name != kv.Value.Name || existing.Ordinal != kv.Value.Ordinal ||
                        existing.Values != kv.Value.Values || existing.ValueOf != kv.Value.ValueOf ||
                        existing.EntryFields.Count != kv.Value.EntryFields.Count ||
                        existing.EntryFields.Any(entry =>
                            !kv.Value.EntryFields.TryGetValue(entry.Key, out var field) || field != entry.Value))
                        _conflictingRichEnums.Add(owner);
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
                        _conflictingBasicEnums.Add(owner);
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
            foreach (var (semantic, contracts) in asm.DotKt.ManagedReferenceContracts)
            {
                if (!_managedReferenceContracts.TryGetValue(semantic, out var current))
                    _managedReferenceContracts.Add(semantic, current = new());
                current.AddRange(contracts);
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
            foreach (var kv in asm.DotKt.KotlinConstructorOwnerFrames)
                _kotlinConstructorOwnerFrames[kv.Key] = kv.Value;
            foreach (var kv in asm.DotKt.TypeParamNames) _ownerTypeParams[kv.Key] = kv.Value;
            foreach (var kv in asm.DotKt.TypeParamDeclarations)
                _ownerTypeParamDeclarations[kv.Key] = kv.Value == null ? null
                    : Array.AsReadOnly(CloneReferencedTypeParameters(kv.Value));
            foreach (var kv in asm.DotKt.NullableFrames)
            {
                if (_ownerNullableFrames.TryGetValue(kv.Key, out var prior) && !SameFrame(prior, kv.Value))
                    throw new InvalidOperationException($"Conflicting nullable representation frame for '{kv.Key}'");
                _ownerNullableFrames[kv.Key] = kv.Value;
            }
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
                    _aliasConstructorAdaptersByOwner[owner] = adapters = new List<BirConstructorAdapterPayload>();
                adapters.Add(adapter.Adapter);
            }
            foreach (var kv in asm.DotKt.TypeShapes) _referenceTypeShapes.TryAdd(kv.Key, kv.Value);
            foreach (var kv in asm.DotKt.PhysicalTypeShapes)
                _referenceTypeShapesByPhysicalOwner.TryAdd(kv.Key, kv.Value);
            foreach (var kv in asm.DotKt.ExactPhysicalTypeByDottedName)
                AddExactPhysicalTypeName(_exactPhysicalTypeByDottedName, kv.Key, kv.Value);
            foreach (var kv in asm.DotKt.PhysicalTypeBySemanticName)
            {
                _physicalTypeBySemanticName.TryAdd(kv.Key, kv.Value);
                if (asm.DotKt.GenericStaticCarrierBySemanticOwner.TryGetValue(
                        StripGenericArity(DottedFqn(kv.Key)), out var staticCarrier))
                {
                    var declaration = StripGenericArity(DottedFqn(kv.Value));
                    if (!_genericStaticCarrierBySemanticOwner.TryAdd(declaration, staticCarrier)
                        && _genericStaticCarrierBySemanticOwner[declaration] != staticCarrier)
                        throw new InvalidOperationException($"conflicting generic-static carrier for '{declaration}'");
                }
                // A relocated declaration may already be named by its exact CLR identity when a containing
                // collection slot is projected. Both identities retain the same trusted existential contract.
                if (asm.DotKt.ExistentialPhysicalBySemanticOwner.TryGetValue(
                        StripGenericArity(DottedFqn(kv.Key)), out var existential))
                {
                    var declaration = StripGenericArity(DottedFqn(kv.Value));
                    var carrier = StripGenericArity(DottedFqn(existential));
                    if (!_existentialPhysicalBySemanticOwner.TryAdd(declaration, carrier)
                        && _existentialPhysicalBySemanticOwner[declaration] != carrier)
                        throw new InvalidOperationException($"conflicting Kotlin existential ABI for '{declaration}'");
                }
            }
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
            foreach (var kv in asm.DotKt.CollectionFactories) _collectionFactories.TryAdd(kv.Key, kv.Value);
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
    }

    // Name index used by MapVarianceRealign for calls without a declaration identity.
    // Collection construction itself requires TryDeclarationFactory's exact selected binding.
    public string CollectionFactoryKind(string funName) => _collectionFactories.GetValueOrDefault(funName);

    // The @KotlinDefault BIR splice map for a call's callee — (argPosition -> decoded default-expression BIR). #146:
    // dll2klib-projected calls already carry their exact file-facade `ownerType` in BIR, so use that structural identity
    // first. Truly ownerless Kotlin calls retain the conservative name+arity index; conflicting owners remain ambiguous
    // and are refused. Running this at phase 1 does not imply throwing away an owner kotc has already projected.
    public Dictionary<int, IBirExpression> KotlinDefaultValuesFor(string owner, string method, int paramCount,
        SignatureKey sigKey = null, SignatureKey relaxedSigKey = null) =>
        _executablePayloads.DefaultValues(owner, method, paramCount, sigKey, relaxedSigKey);

    public Dictionary<int, IBirExpression> KotlinDefaultValuesForDeclarationIdentity(string id) =>
        _executablePayloads.DefaultValuesByIdentity(id);

    // Resolve the declaration exactly as the later scalar-member binding does, then read optional/default metadata
    // from that MethodDef. A call through a derived receiver names the derived type but can select a base declaration;
    // looking up defaults by the call owner would either miss that declaration or, when the derived type declares a
    // same-name/same-arity overload, attach the sibling overload's value. `true` means a declaration was selected even
    // when it has no representable defaults, so callers must not fall through to a different declaration search.
    public bool TryKotlinDefaultValuesForSelectedMethod(
        TypeNode.Fqn owner, string method, int methodArity, bool isStatic, IReadOnlyList<TypeNode> callSignature,
        out Dictionary<int, IBirExpression> defaults, out string[] parameterNames, string propertyKind = null)
    {
        defaults = null;
        parameterNames = null;
        if (!ClrMemberResolution.TryResolveExternalMethodForDefaults(
                null, this, owner, method, methodArity, isStatic, callSignature, out var declaration, propertyKind))
            return false;
        parameterNames = declaration.GetParameters()
            .Select(parameter => string.IsNullOrEmpty(parameter.Name) ? $"arg{parameter.Position}" : parameter.Name)
            .ToArray();
        defaults = CallableDefaultsOf(declaration, _universe.CoreType);
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
    public Dictionary<int, IBirExpression> KotlinDefaultValuesForImplementedInterface(
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

        Dictionary<int, IBirExpression> selected = null;
        foreach (var candidate in interfaces)
        {
            var defaults = CallableDefaultsOf(candidate, _universe.CoreType);
            if (selected == null)
            {
                selected = defaults;
                continue;
            }
            if (defaults == null || !ExecutableBirCodec.SameDefaults(selected, defaults))
                throw new InvalidOperationException(
                    $"bir2cir: cannot fill an omitted default argument of '{owner}.{method}' (arity {paramCount}) — " +
                    "several implemented interface declarations match that class surface but their defaults disagree; " +
                    "call through a specific interface or pass the argument explicitly");
        }
        if (selected != null && interfaces.Any(candidate => CallableDefaultsOf(candidate, _universe.CoreType) == null))
            throw new InvalidOperationException(
                $"bir2cir: cannot fill an omitted default argument of '{owner}.{method}' (arity {paramCount}) — " +
                "several implemented interface declarations match that class surface but do not all declare the " +
                "same defaults; call through a specific interface or pass the argument explicitly");
        return selected;
    }

    // True when the name+arity cannot identify ONE set of defaults: a genuinely ownerless name carried by >1 owner that
    // disagree, or an OWNERFUL key two same-arity declarations (ctor overloads) carry with different defaults.
    public bool KotlinDefaultsAmbiguous(string owner, string method, int paramCount) =>
        _executablePayloads.DefaultsAmbiguous(owner, method, paramCount);

    // A PE row token is module-local, not part of the selected declaration's ABI.
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
        && a.SourcePropertyName == b.SourcePropertyName
        && a.AccessorKind == b.AccessorKind && a.AssociatedPropertyName == b.AssociatedPropertyName
        && a.IsPropertyBridge == b.IsPropertyBridge && a.DeclarationId == b.DeclarationId
        && a.DeclarationSourceName == b.DeclarationSourceName
        && a.DeclarationPhysicalOwner == b.DeclarationPhysicalOwner
        && a.SignatureAllocated == b.SignatureAllocated
        && Same(a.DeclarationSemanticParams, b.DeclarationSemanticParams)
        && Same(a.DeclarationSemanticReturn, b.DeclarationSemanticReturn)
        && a.CollectionFactoryKind == b.CollectionFactoryKind && a.ArrayFactoryKind == b.ArrayFactoryKind
        && a.ArrayFactoryElementHint == b.ArrayFactoryElementHint
        && Same(a.VarargPositions, b.VarargPositions)
        && Same(a.SemanticReifiedTypeParameterIndices, b.SemanticReifiedTypeParameterIndices)
        && Same(a.NullableWitnessTypeParameterIndices, b.NullableWitnessTypeParameterIndices)
        && Same(a.ReflectedWitnessIndices, b.ReflectedWitnessIndices)
        && SameFrame(a.NullableFrame, b.NullableFrame);

    static bool SameFrame(NullableRepresentationFrame a, NullableRepresentationFrame b) =>
        ReferenceEquals(a, b) || a != null && b != null && a.SourceArity == b.SourceArity
            && a.NullableIndices.SequenceEqual(b.NullableIndices)
            && a.StorageIndices.SequenceEqual(b.StorageIndices)
            && a.NullableStorageIndices.SequenceEqual(b.NullableStorageIndices)
            && a.PhysicalOrder.SequenceEqual(b.PhysicalOrder);

    static bool Same<T>(T[] a, T[] b) where T : IEquatable<T> =>
        ReferenceEquals(a, b) || a != null && b != null && a.SequenceEqual(b);

    static bool Same(TypeNode a, TypeNode b) =>
        ReferenceEquals(a, b) || a != null && b != null
        && TypeNode.ToJson(a) == TypeNode.ToJson(b);

    static bool Same(TypeNode[] a, TypeNode[] b) =>
        ReferenceEquals(a, b) || a != null && b != null && a.Length == b.Length
        && a.Select((type, index) => Same(type, b[index])).All(equal => equal);

    // Cross-assembly suspend-call resolution (bundle-6 P3 wave 2a): does the referenced owner declare a suspend
    // member of this name? The cold entry is the naming-convention linkage (`<name>$dotkt_suspend` on the same
    // owner type), keyed off the [KotlinFunction(Suspend)] flag scanned into MemberBinding.Suspend. Used by
    // SuspendColdLowering to rewrite a cross-assembly `x.g()` suspend call to `x.g$dotkt_suspend(…, completion)`.
    public bool HasSuspendMember(string owner, string name) =>
        owner != null && TryMembersByBirOwner(owner, out var list)
        && list.Any(m => m.Suspend && string.Equals(m.Name, name, StringComparison.Ordinal));

    // Exact MethodDef identity, not a same-name overload: only a trusted logical-result carrier identifies
    // the compiler-generated Task wrapper as suspend ABI rather than an ordinary CLR generic result.
    internal bool HasLogicalSuspendResult(MethodInfo method) =>
        method.DeclaringType?.FullName is string owner && TryMembersByBirOwner(owner, out var members)
        && members.Any(member => member.MetadataToken == method.MetadataToken
            && member.Suspend && member.SuspendReturnType != null);

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
            // Probe the GENERIC-arity spellings too: a `clr*` owner token is the bare FQN (`lib.Sub`), but the
            // reflected CLR type of a generic subtype is `lib.Sub`1` — a plain GetType(asm, "lib.Sub") misses it,
            // so the hierarchy walk (and thus R1b's cold-ABI existence guard) would false-negative on a suspend
            // member inherited from a super through a GENERIC referenced subtype. Try the plain name then `n`1..8`.
            var start = _universe.FirstType(owner, 0);
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

    internal string ValueAdapter(string declarationId) => declarationId != null
        && _declarationById.TryGetValue(declarationId, out var declaration) ? declaration.ValueAdapter : null;

    internal LateinitStateTarget LateinitStateForDeclaration(string id) =>
        _declarationById.TryGetValue(id, out var getter) && getter.LateinitState is { } target
            ? target
            : throw new InvalidOperationException($"Kotlin lateinit declaration '{id}' has no initialization-state binding");

    internal IEnumerable<MemberBinding> ValueMembers => _declarationById.Values.Where(member => member.ValueAdapter != null);

    internal bool UsesObjectValueSlots(string owner) => _ownerAlias.GetValueOrDefault(owner) == "System.Object"
        && _assemblies.Any(assembly => assembly.DotKt.ValueRepresentations.ContainsKey(owner));

    // A compiler-authored nominal bridge is identified by its exact MethodImpl declaration,
    // not its spelling or body. Protected bridges remain callable nonvirtually by descendants.
    internal MemberBinding ValueSlotBridge(string owner, string slotOwner, string slotMember)
    {
        owner = StripGenericArity(DottedFqn(BareOwnerFqn(owner)));
        if (!TryMembersByBirOwner(owner, out var members)) return null;
        return members.SingleOrDefault(member => _methodImplsByBody.TryGetValue((owner, member.MetadataToken), out var rows)
            && rows.Any(row => row.DeclarationOwner.Name == slotOwner && row.DeclarationMember == slotMember));
    }

    // A trusted class alias declares Kotlin meaning even when the physical class has additional
    // operational interfaces. Derive map-only storage from that declaration, not concrete CLR names
    // or an assumed KeyValuePair element type. Interface aliases describe contracts, not storage.
    IReadOnlyList<string> _kotlinMapStorageDefinitions;

    public IReadOnlyList<string> KotlinMapStorageDefinitions()
    {
        if (_kotlinMapStorageDefinitions != null) return _kotlinMapStorageDefinitions;
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (identity, shape) in _referenceTypeShapes)
        {
            if (!_ownerAlias.ContainsKey(identity.Name) || shape.Kind != "class") continue;
            var names = new HashSet<string>(StringComparer.Ordinal);
            var pending = new Queue<TypeNode.Fqn>();
            foreach (var face in shape.Interfaces ?? Array.Empty<TypeNode.Fqn>()) pending.Enqueue(face);
            if (shape.Base != null) pending.Enqueue(shape.Base);
            while (pending.TryDequeue(out var face))
            {
                if (!names.Add(face.Name)) continue;
                foreach (var (parent, _) in ReferencedSupertypes(face)) pending.Enqueue(parent);
            }
            if (names.Overlaps(new[] { "kotlin.collections.Map", "kotlin.collections.MutableMap" })
                && !names.Contains("kotlin.collections.Iterable"))
                result.Add(ExactReflectedOwner(identity.Name, identity.Arity));
        }
        return _kotlinMapStorageDefinitions = result.OrderBy(name => name, StringComparer.Ordinal).ToArray();
    }

    public IReadOnlyList<string> KotlinEqualityStorageDefinitions(string classifier)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (identity, shape) in _referenceTypeShapes)
        {
            if (!_ownerAlias.ContainsKey(identity.Name) || shape.Kind != "class") continue;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var pending = new Queue<TypeNode.Fqn>(shape.Interfaces ?? Array.Empty<TypeNode.Fqn>());
            if (shape.Base != null) pending.Enqueue(shape.Base);
            while (pending.TryDequeue(out var face))
            {
                if (!seen.Add(face.Name)) continue;
                foreach (var (parent, _) in ReferencedSupertypes(face)) pending.Enqueue(parent);
            }
            if (seen.Contains("kotlin.collections." + classifier)
                || seen.Contains("kotlin.collections.Mutable" + classifier))
                result.Add(ExactReflectedOwner(identity.Name, identity.Arity));
        }
        return result.OrderBy(name => name, StringComparer.Ordinal).ToArray();
    }

    HashSet<string> _kotlinHashStorageDefinitions;

    public bool IsKotlinHashStorageOwner(string owner)
    {
        if (!TryResolveClrOwner(owner, out var physical, out var kind) || kind != "class") return false;
        _kotlinHashStorageDefinitions ??= KotlinEqualityStorageDefinitions("Map")
            .Concat(KotlinEqualityStorageDefinitions("Set"))
            .Select(BareOwnerFqn).ToHashSet(StringComparer.Ordinal);
        return _kotlinHashStorageDefinitions.Contains(BareOwnerFqn(physical));
    }

    // The static counterpart of kotlinCollectionStorageMatches. Reflection supplies the exact
    // constructed interface identities, including substitutions through foreign base classes.
    // A Kotlin subclass must not turn an inherited alias's storage into a permanent nominal marker.
    public bool HasIndependentForeignCollectionFace(TypeNode.Fqn owner, string name, int arity = 0)
    {
        var type = ResolveNetType(ReflectedOwnerFqn(owner.Name), owner.Args?.Length ?? 0)
            ?? throw new InvalidOperationException($"missing reflected foreign collection owner '{owner.Name}'");
        var storageDefinitions = KotlinMapStorageDefinitions();
        var storageFaces = new HashSet<Type>();
        for (var current = type; current != null; current = current.BaseType)
        {
            var definition = current.IsGenericType ? current.GetGenericTypeDefinition() : current;
            if (!storageDefinitions.Contains(ExactPhysicalMetadataName(definition))) continue;
            storageFaces.UnionWith(current.GetInterfaces());
            break;
        }
        return type.GetInterfaces().Append(type).Any(face =>
            BareOwnerFqn(ExactPhysicalMetadataName(face.IsGenericType ? face.GetGenericTypeDefinition() : face)) == name
            && (face.IsGenericType ? face.GetGenericArguments().Length : 0) == arity
            && !storageFaces.Contains(face));
    }

    // ---- Call-substitution lookups (consumed by MemberCallSubstitution) ----

    // The open identity of a current structured Fqn's name. Generic arguments live in Fqn.Args; the name may carry
    // CLR metadata arity punctuation, which is the only decoration normalized here.
        public static string BareOwnerFqn(string fqnName) => ReferenceMetadataReader.BareOwnerFqn(fqnName);

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
            ? aliasOwner
            : !IsLocalEmittedType(ownerToken) && _physicalTypeBySemanticName.TryGetValue(ownerToken, out var recordedOwner)
                ? recordedOwner : ReflectedOwnerFqn(ownerToken);
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
    public IEnumerable<(string PhysicalName, TypeNode[] Parameters, TypeNode Return, TypeNode SemanticReturn, bool IsVirtual, bool IsAbstract)>
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
                SupertypeGraph.SubstOwnerTvs(member.KotlinReturnType ?? member.ReturnTypeNode, args),
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
            // A collapsed alias head is an operational/declaration face, not a constructed generic owner.
            // Ordinary variant values may still use opaque storage and their declaration-owned classifier;
            // this path must not invent a closed native owner from that storage.
            if (BirTypeLowering.AliasHeadRequiresSourceIdentity(sourceOwner, aliasOwner)
                && !BirTypeLowering.ProjectedAliasHasReifiedGenericHead(
                    sourceOwner, aliasOwner, arguments, Aliases)) return null;
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
        out string[] runtimeParameterKeys, out TypeNode declarationReturn, out bool returnsVoid,
        out MethodInfo selectedDeclaration)
    {
        openDeclaringType = null;
        declaringView = null;
        metadataToken = 0;
        runtimeName = null;
        runtimeParameterKeys = null;
        declarationReturn = null;
        returnsVoid = false;
        selectedDeclaration = null;
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
        selectedDeclaration = openDeclaration;
        openDeclaringType = ExactPhysicalMetadataName(declaring);
        metadataToken = openDeclaration.MetadataToken;
        runtimeName = openDeclaration.Name;
        runtimeParameterKeys = openDeclaration.GetParameters()
            .Select(parameter => ForeignStarRuntimeTypeKey(parameter.ParameterType)).ToArray();
        declarationReturn = DeclarationTypeNode(selected.ReturnType, nominalDelegates: true);
        // `selected` belongs to MetadataLoadContext. Its System.Void Type is not reference/equality-compatible
        // with the runtime's typeof(void), even though both have the same CLR identity; compare metadata names.
        returnsVoid = selected.ReturnType.FullName == "System.Void";
        return true;
    }

    // Metadata tokens are the fastest exact identity when compile-time and runtime modules are the same physical
    // image. Reference assemblies are allowed to assign different tokens from their implementation twin, however.
    // Carry this structural declaration key as the exact fallback; the runtime compares the selected declaration's
    // name/arity/parameter TYPES and never chooses an overload from runtime argument values.
    internal static string ForeignStarRuntimeTypeKey(Type type)
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
    bool ForeignStarDeclarationDescribesCall(TypeNode declaration, TypeNode call,
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
        // An earlier physical value-slot rewrite can already have replaced a nested Kotlin generic application with
        // its metadata-declared existential carrier (Result<T> -> Result$star). That carrier is the declaration's
        // exact physical projection, not a second source-level overload shape, so it still describes the selected
        // declaration while the outer member is being bound.
        if (declaration is TypeNode.Fqn declarationApplication
            && call is TypeNode.Fqn { Args: null } physicalCarrier
            && TryExistentialPhysicalOwner(declarationApplication.Name, out var declaredCarrier)
            && string.Equals(declaredCarrier, physicalCarrier.Name, StringComparison.Ordinal)) return true;
        if (declaration is TypeNode.Nullable dn)
            return call is TypeNode.Nullable cn
                ? ForeignStarDeclarationDescribesCall(dn.Of, cn.Of, ownerArgs)
                : ForeignStarDeclarationDescribesCall(dn.Of, call, ownerArgs);
        if (call is TypeNode.Nullable callNullable)
            return ForeignStarDeclarationDescribesCall(declaration, callNullable.Of, ownerArgs);
        // Nullability annotations do not change an imported CLR declaration slot. After peeling them, an exact
        // structural slot identity is still exact even when its owner argument is hidden by a projection.
        if (declaration.Equals(call)) return true;
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
        declarationType = DeclarationTypeNode(selected.FieldType, nominalDelegates: true);
        return declarationType != null;
    }

    // W1-S2 (#46): resolve a STDLIB-owner clr* member's declaring type off the ref.dll — WITHOUT the `kotlin.*`
    // exclusion that `ResolveNetType` applies (that exclusion keeps NetInteropBinding from reshaping a stdlib call; it
    // does NOT apply to ClrMemberResolution, which runs AFTER substitution and only reflects a member's DECLARED sig).
    // Used for a clr* node IteratorConsumerNormalization deliberately keeps on its `kotlin.collections.Iterator` owner for
    // the rt-stdlib link. Still honors the local-emitted skip (a self-build's own kotlin.* type is authored, not reflected)
    // Generated declarations carried by inline bodies may also belong to a referenced assembly. Resolve their
    // exact metadata identity like any other external type; a generated-name prefix does not decide ownership.
    // Null when the type is not in the ref universe.
    public Type ResolveRefType(string fqn, int genericArity = 0)
    {
        if (string.IsNullOrEmpty(fqn)) return null;
        if (IsLocalEmittedType(fqn)) return null;
        return ProbeNetType(fqn, genericArity);
    }

    // A dll2klib-projected CLR delegate is a nominal Kotlin fun interface, but its physical referenced TypeDef is a
    // System.MulticastDelegate subclass. Consumers that realize SAM construction/invocation ask that exact metadata
    // question here; no function-shape or source-name inference participates.
    public bool IsClrDelegate(TypeNode.Fqn type)
    {
        if (type == null) return false;
        var arity = type.Args?.Length ?? 0;
        var owner = PhysicalTypeNames.TryGetValue(type.Name, out var mapped)
            ? mapped
            : TryExactPhysicalTypeName(type.Name, arity, out var exact) && exact != null
                ? exact
                : type.Name;
        var reflected = ResolveRefType(owner, owner.Contains('`') || owner.Contains('+') ? 0 : arity);
        if (reflected == null) return false;
        try { return IsDelegate(reflected); }
        catch { return false; }
    }

    // A referenced Literal FieldDef has no storage to load. Select its exact Constant-table value and Kotlin surface
    // type here so ConstFieldLowering can author an owning BIR constant; ilemit must not rediscover this representation.
    internal bool TryResolveLiteralFieldValue(TypeNode.Fqn owner, string name, out TypeNode type, out BirLiteral value)
    {
        type = null;
        value = null;
        if (owner == null || string.IsNullOrEmpty(name)) return false;
        var cacheKey = owner.Name + "|" + (owner.Args?.Length ?? 0) + "|" + name;
        if (_literalFieldCache.TryGetValue(cacheKey, out var cached))
        {
            type = cached.Type;
            value = cached.Value;
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
        value = LiteralValue(raw);
        if (value == null) return CacheLiteralMiss(cacheKey, out type, out value);
        _literalFieldCache[cacheKey] = (true, type, value);
        return true;
    }

    bool CacheLiteralMiss(string cacheKey, out TypeNode type, out BirLiteral value)
    {
        _literalFieldCache[cacheKey] = (false, null, null);
        type = null;
        value = null;
        return false;
    }

    // Reference constants enter the same typed scalar vocabulary as Kotlin constants.
    // Retain floating bits; unsigned widths are still normalized by their owning physical binding.
    internal static JsonNode LiteralValueNode(object raw) => LiteralValue(raw)?.ToDom();

    // CLI Constant-table primitives enter one semantic scalar representation.
    // The unsigned reinterpretation is the existing physical literal policy.
    internal static BirLiteral LiteralValue(object raw) => raw switch {
        double value => new Float64Literal(BitConverter.DoubleToInt64Bits(value)),
        float value => new Float32Literal(BitConverter.SingleToInt32Bits(value)),
        uint value => new BirLiteral.IntegerLiteral(unchecked((int)value)),
        ulong value => new BirLiteral.IntegerLiteral(unchecked((long)value)),
        char value => new BirLiteral.IntegerLiteral(value),
        string value => new BirLiteral.TextLiteral(value),
        bool value => new BirLiteral.BooleanLiteral(value),
        sbyte value => new BirLiteral.IntegerLiteral(value),
        byte value => new BirLiteral.IntegerLiteral(value),
        short value => new BirLiteral.IntegerLiteral(value),
        ushort value => new BirLiteral.IntegerLiteral(value),
        int value => new BirLiteral.IntegerLiteral(value),
        long value => new BirLiteral.IntegerLiteral(value),
        _ => null,
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
            EnumConstantText(raw));
    }

    // dll2klib presents foreign enum entries in FieldDef order, not Enum.GetValues' unsigned numeric order.
    // Aliases remain separate declarations here even though their physical CLR values are indistinguishable.
    public bool TryNativeEnumEntries(string fqn, out BasicEnumMetadata metadata)
    {
        metadata = null;
        var type = ResolveNetType(fqn);
        if (type == null || !type.IsEnum) return false;
        var fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(field => field.IsLiteral && field.FieldType == type && !field.Name.StartsWith('<')
                && (field.IsPublic || field.IsFamily || field.IsFamilyOrAssembly))
            .OrderBy(field => field.MetadataToken);
        metadata = new BasicEnumMetadata(type.GetEnumUnderlyingType().FullName,
            fields.Select((field, ordinal) => new BasicEnumEntry(field.Name, ordinal,
                EnumConstantText(field.GetRawConstantValue()))).ToArray());
        return true;
    }

    public bool IsEnumType(TypeNode.Fqn type) => ReferencedTypeKind(type) == "enum";
    public bool IsClassType(TypeNode.Fqn type) => ReferencedTypeKind(type) == "class";

    internal bool IsNonPublicStorageType(TypeNode.Fqn type)
    {
        var arity = type.Args?.Length ?? 0;
        var name = TryResolveClrOwner(type.Name, out var alias, out _) ? alias : RecordedPhysicalTypeName(type.Name);
        if (!HasExactOwnerPunctuation(name) && TryExactPhysicalTypeName(name, arity, out var exact) && exact != null)
            name = exact;
        // For a nested TypeDef the captured outer arguments are not its own arity.
        return ResolveRefType(name, HasExactOwnerPunctuation(name) ? 0 : arity) is { IsVisible: false };
    }

    public bool IsInterfaceType(TypeNode.Fqn type)
    {
        var kind = ReferencedTypeKind(type);
        return kind != null ? kind == "interface"
            : type != null && ProbeNetType(type.Name, type.Args?.Length ?? 0)?.IsInterface == true;
    }

    string ReferencedTypeKind(TypeNode.Fqn type)
    {
        if (type == null) return null;
        var identity = OwnerIdentity(type.Name, type.Args?.Length ?? 0);
        string kind;
        if (HasExactOwnerPunctuation(type.Name))
            kind = _ownerKindByPhysicalOwner.GetValueOrDefault(type.Name);
        else if (_exactPhysicalTypeByDottedName.TryGetValue(identity, out var exact))
            kind = exact == null ? null : _ownerKindByPhysicalOwner.GetValueOrDefault(exact);
        else
            kind = _ownerKind.GetValueOrDefault(identity);
        return kind;
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
    // policy (ResolveNetType excludes kotlin.*/dotkt$ synthetics/local; ResolveRefType excludes local declarations).
    /// <summary>
    /// The type named by <paramref name="fqn"/> as DECLARED BY a specific reference assembly (#370). An applied
    /// external attribute may state its declaring scope precisely because the FQN alone is ambiguous — a
    /// compiler-synthesized attribute can share it with a private lookalike — so the stated scope selects
    /// rather than the ordinary probe guessing.
    /// </summary>
    public Type ResolveRefTypeIn(string fqn, string assemblySimpleName)
    {
        if (string.IsNullOrEmpty(fqn) || string.IsNullOrEmpty(assemblySimpleName)) return null;
        return _universe.ResolveTypeIn(fqn, assemblySimpleName);
    }

    Type ProbeNetType(string fqn, int genericArity) => _universe.ResolveType(fqn, genericArity);

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
    /// So read the shipped declaration instead of reconstructing it. Authored declarations and generated
    /// existential slots retain their trusted IDs in both twins: exactly one runtime MethodDef must carry the
    /// selected ID, regardless of its name or signature. A BCL declaration has no separate owner and remains its
    /// own physical form.
    /// </remarks>
    readonly Dictionary<MemberInfo, MemberInfo> _shippedCache = new();

    public MethodBase PhysicalTwinOf(MethodBase member, Type declaringDef)
    {
        if (_shippedCache.TryGetValue(member, out var cached)) return cached as MethodBase;
        var found = FindShippedMethod(member, declaringDef);
        _shippedCache[member] = found;
        return found;
    }

    MethodBase FindShippedMethod(MethodBase member, Type declaringDef)
    {
        var owner = PhysicalOwnerOf(declaringDef);
        if (owner == null) return null;
        var arity = member.IsGenericMethod ? member.GetGenericArguments().Length : 0;
        var identity = KotlinDeclarationIdentityPayload(member.GetCustomAttributesData(),
            member.DeclaringType?.Assembly, arity)?.Id;
        if (identity != null)
        {
            var declarations = member is ConstructorInfo
                ? owner.GetConstructors(MemberProbeFlags).Cast<MethodBase>()
                : owner.GetMethods(MemberProbeFlags).Cast<MethodBase>();
            return SelectDeclarationTwin(declarations, candidate =>
                KotlinDeclarationIdentityPayload(candidate.GetCustomAttributesData(),
                    candidate.DeclaringType?.Assembly,
                    candidate.IsGenericMethod ? candidate.GetGenericArguments().Length : 0)?.Id,
                identity);
        }
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

    internal static T SelectDeclarationTwin<T>(IEnumerable<T> declarations, Func<T, string> identityOf,
        string identity)
    {
        var matches = declarations.Where(declaration => identityOf(declaration) == identity).ToArray();
        return matches.Length == 1 ? matches[0] : throw new InvalidOperationException(
            $"Physical declaration identity '{identity}' selects {matches.Length} runtime MethodDefs");
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
        var asm = _universe.PhysicalStdlib;
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
        var asm = _universe.PhysicalStdlib;
        if (asm == null) return null;
        try
        {
            return asm.GetType(name, throwOnError: false)
                ?? (arity > 0 ? asm.GetType($"{name}`{arity}", throwOnError: false) : null);
        }
        catch { return null; }
    }

    public void Dispose()
    {
        _shippedCache.Clear();
        _literalFieldCache.Clear(); _volatileFieldCache.Clear(); _awaitPlanCache.Clear();
        _nativeSignatures = null;
        _universe.Dispose();
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
        if (TryKotlinRichEnum(ownerFqn, out var metadata))
        {
            values = metadata.Values;
            valueOf = metadata.ValueOf;
            return true;
        }
        values = null;
        valueOf = null;
        return false;
    }

    bool TryKotlinRichEnum(string ownerFqn, out RichEnumMetadata metadata)
    {
        metadata = null;
        if (IsLocalEmittedType(ownerFqn)) return false;
        var owner = StripGenericArity(DottedFqn(BareOwnerFqn(ownerFqn)));
        // Private declarations in unrelated DLLs may share a source name. Keep their
        // metadata, but diagnose ambiguity only when a referenced operation selects it.
        if (_conflictingRichEnums.Contains(owner))
            throw new InvalidOperationException($"conflicting Kotlin rich-enum metadata for '{owner}'");
        return _richEnums.TryGetValue(owner, out metadata);
    }

    public bool TryKotlinBasicEnum(string ownerFqn, out BasicEnumMetadata metadata)
    {
        metadata = null;
        if (IsLocalEmittedType(ownerFqn)) return false;
        var owner = StripGenericArity(DottedFqn(BareOwnerFqn(ownerFqn)));
        // Unrelated DLLs may contain same-named private enums. Their maps are ambiguous only if an external
        // operation actually selects that owner; merely referencing both DLLs must not override local declarations.
        if (_conflictingBasicEnums.Contains(owner))
            throw new InvalidOperationException($"conflicting Kotlin basic-enum metadata for '{owner}'");
        return _basicEnums.TryGetValue(owner, out metadata);
    }

    public bool TryKotlinRichEnumStaticApi(
        string ownerFqn, string sourceMemberName, int paramCount, out string physicalMemberName)
    {
        if (TryKotlinRichEnum(ownerFqn, out var metadata))
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
        if (TryKotlinRichEnum(ownerFqn, out var metadata) &&
            metadata.EntryFields.TryGetValue(entryName, out physicalField))
            return true;
        physicalField = null;
        return false;
    }

    public bool TryKotlinRichEnumInstanceFields(
        string ownerFqn, out string nameField, out string ordinalField)
    {
        if (TryKotlinRichEnum(ownerFqn, out var metadata))
        {
            nameField = metadata.Name;
            ordinalField = metadata.Ordinal;
            return true;
        }
        nameField = null;
        ordinalField = null;
        return false;
    }
    IReadOnlyList<BirTypeParameter> OwnerTypeParameterRows(string ownerFqn)
    {
        if (ownerFqn == null) return null;
        return _ownerTypeParamDeclarations.GetValueOrDefault(ownerFqn)
            ?? _ownerTypeParamDeclarations.GetValueOrDefault(StripGenericArity(ownerFqn))
            ?? _ownerTypeParamDeclarations.GetValueOrDefault(StripGenericArity(DottedFqn(ownerFqn)));
    }

    public BirTypeParameter[] OwnerTypeParamDeclarations(string ownerFqn) =>
        CloneReferencedTypeParameters(OwnerTypeParameterRows(ownerFqn));

    // Source declaration facts retain CLR parameter positions. Map source indices
    // through NullableRepresentationFrame.SourcePosition before indexing this list.
    internal IReadOnlyList<BirTypeParameter> OwnerTypeParametersInPhysicalFrame(string ownerFqn)
        => OwnerTypeParameterRows(ownerFqn);

    // Proof obligations come from the shipped declaration, not the Kotlin reference surface. In particular,
    // stdlib reference/runtime twins need not carry the same physical GenericParamConstraint rows.
    Type PhysicalOwnerForParameterFacts(string owner) => owner == null ? null : PhysicalGenericOwner(owner, OwnerArity(owner));

    // Variance is a physical GenericParam fact, including for Kotlin-authored owners. ResolveNetType intentionally
    // excludes those owners for semantic call binding; the reference twin's flags are not runtime proof either.
    internal string PhysicalOwnerTypeParameterVariance(string owner, int arity, int index)
    {
        var type = PhysicalGenericOwner(owner, arity);
        var parameters = type?.IsGenericTypeDefinition == true ? type.GetGenericArguments() : null;
        if (parameters == null || index < 0 || index >= parameters.Length) return null;
        return (parameters[index].GenericParameterAttributes & GenericParameterAttributes.VarianceMask) switch
        {
            GenericParameterAttributes.Covariant => "out",
            GenericParameterAttributes.Contravariant => "in",
            _ => null,
        };
    }

    // Only for already-selected physical representation facts, never semantic call classification.
    // A nominal carrier is an actual physical TypeDef, not a source classifier needing a declaration map.
    internal Type PhysicalTypeForProof(string owner, int arity)
    {
        if (owner == null || IsLocalEmittedType(owner)) return null;
        var type = ResolveRefType(owner, arity) ?? PhysicalTypeNamed(owner, arity);
        return type == null ? null : PhysicalProofOwner(type);
    }

    Type PhysicalGenericOwner(string owner, int arity)
    {
        if (owner == null || IsLocalEmittedType(owner)) return null;
        var physicalName = owner;
        // Only admitted Kotlin declarations own referenced proof rows. Compiler implementation types can be
        // synthesized locally after the initial projection and can share private names in multiple libraries.
        // The declaration map also supplies exact nested/relocated TypeDef identities for public Kotlin owners.
        if (HasDotKtOwner(owner)
            && !_physicalTypeBySemanticName.TryGetValue(StripGenericArity(DottedFqn(owner)), out physicalName))
            return null;
        var type = ResolveRefType(physicalName, arity) ?? PhysicalTypeNamed(physicalName, arity);
        if (type == null) return null;
        return PhysicalProofOwner(type);
    }

    MethodBase PhysicalMethodForParameterFacts(string declarationIdentity)
    {
        if (declarationIdentity == null || !_declarationById.TryGetValue(declarationIdentity, out var declaration))
            return null;
        var ownerName = declaration.DeclarationPhysicalOwner ?? declaration.Owner;
        var owner = ResolveRefType(ownerName, OwnerArity(ownerName))
            ?? PhysicalTypeNamed(ownerName, OwnerArity(ownerName));
        if (owner == null) return null;
        var selected = owner.GetMethods(MemberProbeFlags)
            .Single(method => method.MetadataToken == declaration.MetadataToken);
        var physicalOwner = PhysicalProofOwner(owner);
        if (physicalOwner == null) return null;
        var physical = physicalOwner == owner ? selected : PhysicalTwinOf(selected, owner)
            ?? throw new InvalidOperationException($"Selected declaration '{declarationIdentity}' has no shipped MethodDef");
        return physical;
    }

    Type PhysicalProofOwner(Type reference)
    {
        var assembly = reference.Assembly.GetName().Name;
        // A reference-only declaration absent from its required runtime twin has no physical rows. It must not
        // become a runtime proof merely because PhysicalOwnerOf also returns null for ordinary non-twin assemblies.
        return ManagedReferenceCatalog.PhysicalAssemblyName(assembly) == assembly ? reference : PhysicalOwnerOf(reference);
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

    // Top-level arity is already in the type arguments. Preserve nested segments, but do not
    // merge top-level Kotlin interfaces just because they have the same CLR representation.
    internal string HierarchyComparisonName(string name, int arity)
    {
        if (name.Contains('+')) return name;
        if (!IsLocalEmittedType(name) && TryExactPhysicalTypeName(name, arity, out var exact)
            && exact?.Contains('+') == true) return exact;
        return BareOwnerFqn(name);
    }

    // Exact CLR metadata identity for a trusted external DotKt classifier. Local source declarations remain
    // authoritative and therefore never rewrite through a same-named reference.
    // Reference mappings are complete at construction; replacing the local inventory starts a new snapshot lifetime.
    public IReadOnlyDictionary<string, string> PhysicalTypeNames =>
        _physicalTypeNamesSnapshot ??= _physicalTypeBySemanticName
            .Where(kv => !IsLocalEmittedType(kv.Key))
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);

    internal string RecordedPhysicalTypeName(string sourceName) => !IsLocalEmittedType(sourceName)
        && _physicalTypeBySemanticName.TryGetValue(sourceName, out var physical) ? physical : sourceName;

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

    // Address-taking selects the actual declared FieldDef, never its synthesized ordinary-value getter.
    internal bool HasReferencedFieldLocation(TypeNode.Fqn owner, string name, bool isStatic)
    {
        if (owner == null || name == null) return false;
        var physical = RecordedPhysicalTypeName(owner.Name);
        var declaration = ResolveRefType(physical, physical.Contains('`') || physical.Contains('+')
            ? 0 : owner.Args?.Length ?? 0);
        var field = declaration?.GetField(name, BindingFlags.Public | BindingFlags.NonPublic
            | BindingFlags.DeclaredOnly | (isStatic ? BindingFlags.Static : BindingFlags.Instance));
        return field != null && !field.IsLiteral;
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
    // A direct field projection must not select an authored/custom property getter.
    public bool TryStarProjectionField(TypeNode.Fqn sourceOwner, string sourceName, string accessorKind,
        out string erasedOwner, out string erasedMember, out TypeNode[] erasedSignature,
        out TypeNode declarationResult, out TypeNode physicalResult)
    {
        erasedOwner = erasedMember = null;
        erasedSignature = null;
        declarationResult = physicalResult = null;
        if (sourceOwner == null || sourceName == null || accessorKind is not ("get" or "set")
            || !TryExistentialPhysicalOwner(sourceOwner.Name, out var carrier)
            || !TryMembersByBirOwner(carrier, out var members)
            || !TryExistentialSemanticOwner(carrier, out var semanticOwner)) return false;
        var reflectedOwner = ResolveRefType(sourceOwner.Name, sourceOwner.Args?.Length ?? 0);
        var field = reflectedOwner?.GetField(sourceName,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
        if (field == null || accessorKind == "set" && field.IsInitOnly) return false;
        var fieldType = KotlinTypeOf(field.GetCustomAttributesData(), field.DeclaringType?.Assembly)
            ?? DeclarationTypeNode(field.FieldType);
        var slots = members.Where(member => !member.IsStatic && member.MethodArity == 0
            && member.SourcePropertyName == sourceName && member.AccessorKind == accessorKind
            && member.PropertyAssociation == semanticOwner + ":field:" + sourceName
            && member.ParamCount == (accessorKind == "get" ? 0 : 1)).ToList();
        if (fieldType == null || slots.Count != 1) return false;
        var selected = slots[0];
        erasedOwner = carrier;
        erasedMember = selected.Name;
        erasedSignature = selected.ParamTypeNodes;
        declarationResult = accessorKind == "get" ? fieldType : new TypeNode.Fqn("kotlin.Unit");
        physicalResult = selected.ReturnTypeNode ?? selected.ReturnType;
        return true;
    }

    public bool TryManagedReferenceContract(TypeNode.Fqn sourceOwner, string sourceMember, string accessorKind,
        int methodArity, IReadOnlyList<TypeNode> signature, int parameterCount, string declarationId,
        out TypeNode.Fqn owner, out MemberBinding member, out string invoke)
    {
        owner = null;
        member = null;
        invoke = null;
        if (!_managedReferenceContracts.TryGetValue(StripGenericArity(DottedFqn(sourceOwner.Name)), out var contracts)
            || sourceOwner.Args == null) return false;
        var matches = new List<(TypeNode.Fqn, MemberBinding, string)>();
        foreach (var contract in contracts)
        {
            if (declarationId != null && declarationId != contract.Declaration) continue;
            if (!TryMembersByBirOwner(contract.Owner, out var members)) continue;
            foreach (var candidate in members.Where(candidate => !candidate.IsStatic
                && candidate.MethodArity == methodArity && candidate.ParamCount == parameterCount))
            {
                if (declarationId != null)
                {
                    // Each marked interface owns one operational slot, linked to its source declaration by the
                    // type carrier. The source spelling is still independently carried on the physical MethodDef.
                    if (accessorKind is "get" or "set"
                        ? candidate.SourcePropertyName != sourceMember || candidate.AccessorKind != accessorKind
                        : candidate.SourceMethodName != sourceMember) continue;
                }
                else
                {
                    if (accessorKind is "get" or "set"
                        ? candidate.SourcePropertyName != sourceMember || candidate.AccessorKind != accessorKind
                        : candidate.SourceMethodName != sourceMember) continue;
                    var source = candidate.KotlinParameterTypes ?? candidate.ParamTypeNodes;
                    if (signature == null || source == null || source.Length != signature.Count
                        || !source.Select((parameter, index) => ForeignStarDeclarationDescribesCall(
                            candidate.NullableGenericParams?[index] ?? parameter,
                            signature[index], sourceOwner.Args)).All(match => match)) continue;
                }
                if (contract.Slots.Any(index => index >= sourceOwner.Args.Length))
                    throw new InvalidOperationException("Managed-reference contract application has an incomplete owner frame");
                matches.Add((new TypeNode.Fqn(contract.Owner,
                    contract.Slots.Select(index => sourceOwner.Args[index]).ToArray()), candidate, contract.Invoke));
            }
        }
        if (matches.Count != 1) return false;
        (owner, member, invoke) = matches[0];
        return true;
    }

    internal bool TryInheritedStarSlot(string receiverCarrier, string id, int arity, int parameterCount,
        out string owner, out MemberBinding slot, out TypeNode sourceResult)
    {
        owner = null; slot = null; sourceResult = null;
        if (!_declarationById.TryGetValue(id, out var source) || source.IsStatic
            || source.MethodArity != arity || source.ParamCount != parameterCount) return false;
        var receiver = new TypeNode.Fqn(receiverCarrier);
        if (!TryReferenceTypeShape(receiver, out _, out _, out _, out var interfaces)) return false;
        // Reflection's GetInterfaces already includes all transitive interfaces.
        foreach (var application in interfaces.Prepend(receiver))
        {
            if (!TryExistentialSemanticOwner(application.Name, out var semantic)) continue;
            var physicalId = FBoundStarProjectionErasure.ExistentialSlotIdentity(id, semantic);
            if (_declarationById.TryGetValue(physicalId, out var member) && member.IsPublic && !member.IsStatic
                && member.MethodArity == arity && member.ParamCount == parameterCount && member.ParamTypeNodes != null
                && member.ReturnTypeNode != null && BareOwnerFqn(member.Owner) == BareOwnerFqn(application.Name))
            {
                if (slot != null) return false;
                slot = member; owner = application.Name;
            }
        }
        sourceResult = source.NullableGenericRet ?? source.SuspendReturnType ?? source.KotlinReturnType ?? source.ReturnTypeNode;
        return slot != null && sourceResult != null;
    }

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
            || !TryMembersByBirOwner(candidateOwner, out var members)) return false;
        TryMembersByBirOwner(sourceOwner.Name, out var semanticMembers);
        semanticMembers ??= new List<MemberBinding>();

        // Both selection steps describe the Kotlin declaration. A projected physical parameter such as
        // Sink$star cannot identify an authored Sink<T> signature before the existential slot is selected.
        static TypeNode[] SourceParameters(MemberBinding member)
        {
            var parameters = member.DeclarationSemanticParams
                ?? member.KotlinParameterTypes ?? member.ParamTypeNodes;
            return member.DeclarationSemanticParams != null || parameters == null ? parameters
                : parameters.Select((parameter, index) =>
                    member.NullableGenericParams?[index] ?? parameter).ToArray();
        }

        var declarations = semanticMembers.Where(m => !m.IsStatic
            && (declarationId != null
                ? m.DeclarationId == declarationId
                : accessorKind is "get" or "set"
                    ? !m.IsPropertyBridge && m.SourcePropertyName == sourceMember && m.AccessorKind == accessorKind
                    : m.SourcePropertyName == null && (m.SourceMethodName ?? m.Name) == sourceMember)
            && m.MethodArity == methodArity && m.ParamCount == paramCount
            && (declarationId != null || authoredSignature == null
                || SourceParameters(m) is { } ps && ps.Length == authoredSignature.Count
                && (ps.SequenceEqual(authoredSignature)
                    || ps.Select((p, i) => ForeignStarDeclarationDescribesCall(
                        p, authoredSignature[i], sourceOwner.Args ?? Array.Empty<TypeNode>())).All(x => x))))
            .ToList();
        if (declarations.Count == 0 && declarationId == null && methodArity == 0
            && paramCount == (accessorKind == "get" ? 0 : 1))
            return TryStarProjectionField(sourceOwner, sourceMember, accessorKind,
                out erasedOwner, out erasedMember, out erasedSignature, out declarationResult, out physicalResult);
        if (declarations.Count != 1) return false;

        // Select the actual MethodDef on the trusted existential owner through its explicit source identity. Its Name
        // is already the final physical link target; neither the source declaration's allocated name nor an ordinal is
        // sufficient to derive it. In particular, an explicit @ClrName changes the source MethodDef but not the
        // compiler-owned dependent-slot spelling.
        // A declaration-id-selected call can reach this late binding after Kotlin property-call syntax has already
        // been allocated away. The selected declaration still carries the authoritative accessor role; use it to
        // select the corresponding existential MethodDef rather than treating the accessor as an ordinary method.
        var selectedAccessorKind = accessorKind ?? declarations[0].AccessorKind;
        var selectedSourceMember = selectedAccessorKind is "get" or "set"
            ? declarations[0].SourcePropertyName ?? sourceMember
            : declarations[0].DeclarationSourceName ?? declarations[0].SourceMethodName ?? sourceMember;
        // Earlier physical lowering may deliberately give the ordinary declaration and its existential slot distinct
        // CLR descriptors. Compare their preserved Kotlin descriptors instead: EraseParams copied that exact source
        // fact onto every synthesized slot before changing its physical type. This remains exact for same-name overloads
        // without reconstructing the representation change from a generated name or accepting a merely unique sibling.
        var declarationParameters = SourceParameters(declarations[0]);
        bool DescribesSelectedDeclaration(MemberBinding candidate) => declarationParameters == null
            || (candidate.KotlinParameterTypes ?? candidate.ParamTypeNodes) is { } candidateParameters
                && candidateParameters.Length == declarationParameters.Length
                && candidateParameters.Select((p, i) => SourceDeclarationDescribesCall(
                    declarationParameters[i], candidate.NullableGenericParams?[i] ?? p)).All(x => x);
        bool DescribesSelectedPhysicalDeclaration(MemberBinding candidate) => declarations[0].ParamTypeNodes is { } declarationPhysical
            && candidate.ParamTypeNodes is { } candidatePhysical
            && candidatePhysical.Length == declarationPhysical.Length
            && candidatePhysical.SequenceEqual(declarationPhysical);

        var shapedCandidates = members.Where(m => !m.IsStatic && m.ParamCount == paramCount
            && m.MethodArity == methodArity).ToList();
        List<MemberBinding> candidates;
        if (selectedAccessorKind is "get" or "set")
        {
            candidates = shapedCandidates.Where(m => !m.IsPropertyBridge
                && m.SourcePropertyName == selectedSourceMember && m.AccessorKind == selectedAccessorKind
                && DescribesSelectedDeclaration(m)).ToList();
        }
        else if (declarations[0].DeclarationId is string selectedIdentity)
        {
            // The selected source and its allocated existential MethodDef share an authored edge, even when
            // intrinsic/indexer lowering gave them different names. A known declaration ID is never a name lookup.
            // Source metadata and the physical slot can use different classifier vocabularies. Their explicit
            // declaration association, not another signature match across those vocabularies, owns this edge.
            candidates = shapedCandidates.Where(m => m.SourcePropertyName == null
                && m.SourceMethodDeclaration == selectedIdentity).ToList();
        }
        else
        {
            // Generated declarations without a Kotlin declaration ID retain their explicit source-method name or
            // unchanged physical descriptor. This role is distinct from selected source declarations above.
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
        erasedSignature = candidates[0].SignatureParameters ?? candidates[0].ParamTypeNodes ?? Array.Empty<TypeNode>();
        declarationResult = declarations[0].NullableGenericRet
            ?? declarations[0].SuspendReturnType
            ?? declarations[0].KotlinReturnType
            ?? declarations[0].ReturnTypeNode;
        // Selection happens while calls still have Kotlin suspend semantics. The Task MethodDef is the metadata
        // owner, not the value produced by the later cold-entry call; use its explicit logical result carrier.
        physicalResult = candidates[0].SuspendReturnType ?? candidates[0].ReturnTypeNode;
        return true;
    }

    // A referenced existential outer publishes constructor factories as trusted generated interface slots.  Select
    // the exact slot from the carrier's Kotlin inner classifier + selected constructor identity. Neither closing
    // generic arguments nor erasing function signatures may reopen overload selection. The method name is output.
    public bool TryExistentialInnerConstructorFactory(string semanticOuter, TypeNode.Fqn innerType,
        string constructorDeclaration,
        out string physicalOwner, out string physicalMethod, out TypeNode[] physicalParameters,
        out TypeNode physicalResult, out TypeNode[] physicalTypeArguments,
        out TypeNode.Fqn referenceOwner, out string referenceInvoke)
    {
        physicalOwner = physicalMethod = null;
        physicalParameters = null;
        physicalResult = null;
        physicalTypeArguments = null;
        referenceOwner = null;
        referenceInvoke = null;
        if (semanticOuter == null || innerType == null || constructorDeclaration == null
            || !TryExistentialPhysicalOwner(semanticOuter, out var owner)) return false;
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

        var candidates = new List<(MemberBinding Member, TypeNode[] TypeArguments,
            string Owner, int[] Slots, string Invoke)>();
        void SelectFactories(string candidateOwner, int[] slots, string invoke)
        {
            if (!TryMembersByBirOwner(candidateOwner, out var members)) return;
            foreach (var member in members.Where(member =>
                !member.IsStatic
                && member.InnerConstructorOwner != null && DescribesInnerClassifier(member)
                && member.InnerConstructorDeclaration == constructorDeclaration
                && member.ParamTypeNodes != null && member.ReturnTypeNode != null))
                if (TryFactoryTypeArguments(member, out var typeArguments))
                {
                    if (slots != null && slots.Any(index =>
                            index >= arguments.Length - member.InnerConstructorTypeArguments.Length))
                        throw new InvalidOperationException("Managed-reference factory has an incomplete captured owner frame");
                    candidates.Add((member, typeArguments, candidateOwner, slots, invoke));
                }
        }
        SelectFactories(owner, null, null);
        if (_managedReferenceContracts.TryGetValue(StripGenericArity(DottedFqn(semanticOuter)), out var contracts))
            foreach (var contract in contracts) SelectFactories(contract.Owner, contract.Slots, contract.Invoke);
        if (candidates.Count != 1) return false;
        var (match, selectedTypeArguments, selectedOwner, selectedSlots, selectedInvoke) = candidates[0];
        physicalOwner = selectedOwner;
        physicalMethod = match.Name;
        physicalParameters = match.ParamTypeNodes;
        physicalResult = match.ReturnTypeNode;
        physicalTypeArguments = selectedTypeArguments;
        if (selectedSlots != null)
        {
            referenceOwner = new TypeNode.Fqn(selectedOwner, selectedSlots.Select(index => arguments[index]).ToArray());
            referenceInvoke = selectedInvoke;
        }
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
            physicalOwner = ExactReflectedOwner(sourceOwner, ownerArity);
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

    internal bool TryDeclarationClrBinding(string id, out ExactClrMemberBinding binding)
    {
        binding = null;
        if (id == null || !_declarationById.TryGetValue(id, out var declaration)
            || declaration.Intrinsic == null && declaration.PropertyName == null && !declaration.Conv)
            return false;
        binding = new ExactClrMemberBinding(declaration.Intrinsic, declaration.PropertyAccess,
            declaration.PropertyName, declaration.Conv, declaration.ConvTo, declaration.ByrefPositions,
            declaration.CountStart, declaration.CountEnd);
        return true;
    }

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

    internal bool TryProjectedIndexerSlot(TypeNode.Fqn owner, string sourceMember, int methodArity,
        IReadOnlyList<TypeNode> signature, out string accessor, out TypeNode slotReturn)
    {
        accessor = null;
        slotReturn = null;
        if (sourceMember is not ("get" or "set") || methodArity != 0 || signature == null
            || ResolveNetType(ReflectedOwnerFqn(owner.Name), owner.Args?.Length ?? 0) is not Type reflectedOwner
            || !TryMembersByBirOwner(owner.Name, out var members)) return false;
        var overridableTokens = reflectedOwner.GetMethods(BindingFlags.Public | BindingFlags.NonPublic
                | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(method => method.IsVirtual && !method.IsFinal
                && (method.IsPublic || method.IsFamily || method.IsFamilyOrAssembly))
            .Select(method => method.MetadataToken).ToHashSet();
        // SourceMethodName comes from the indexed Property's MethodSemantics association, not its CLR spelling.
        // Close the declaration in the override owner's frame; type variables are identities, not wildcards.
        var matches = members.Where(member => !member.IsStatic && member.IsVirtual
            && overridableTokens.Contains(member.MetadataToken)
            && member.AssociatedPropertyName != null && member.SourceMethodName == sourceMember
            && member.AccessorKind == sourceMember && member.MethodArity == methodArity
            && member.ParamTypeNodes is { } parameters && parameters.Length == signature.Count
            && parameters.Select((type, index) => SourceDeclarationDescribesCall(
                SupertypeGraph.SubstOwnerTvs(type, owner.Args ?? Array.Empty<TypeNode>()), signature[index]))
                .All(match => match)).ToList();
        if (matches.Count > 1)
            throw new InvalidDataException($"ambiguous projected indexer slot for {owner.Name}.{sourceMember}");
        if (matches.Count == 0) return false;
        accessor = matches[0].Name;
        slotReturn = SupertypeGraph.SubstOwnerTvs(matches[0].ReturnTypeNode,
            owner.Args ?? Array.Empty<TypeNode>());
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

    TypeNode QueryParameterBound(MemberBinding member, string ownerName, int index)
    {
        var parameters = member.KotlinParameterTypes ?? member.NullableGenericParams;
        if (!KotlinCollectionSlotSynthesis.UsesErasedQueryCarrier(member.ParamTypeNodes[index])
            || parameters == null || index >= parameters.Length
            || parameters[index] is not (TypeNode.Tv or TypeNode.Nullable { Of: TypeNode.Tv })) return null;
        if (!HasDotKtOwner(ownerName) || !TryReferenceTypeShapeValue(new TypeNode.Fqn(ownerName), out var shape)) return null;
        var ownerFacts = shape.SourceFacts == null ? null : SourceTypeFacts.ReadFrozenText(shape.SourceFacts.ToJsonString());
        var owner = new ErasedElementValues.BoundDeclaration(ownerFacts?.NullableFrame,
            OwnerTypeParametersInPhysicalFrame(ownerName)?.Select(parameter => parameter.Constraints.OrEmpty()).ToArray() ?? [],
            ownerFacts?.Bounds);
        var method = new ErasedElementValues.BoundDeclaration(member.NullableFrame,
            member.MethodTypeParams?.Select(parameter => parameter.Constraints.OrEmpty()).ToArray() ?? [],
            member.SemanticMethodTypeParams?.Select((parameter, boundIndex) => (boundIndex, parameter.Constraints.OrEmpty()))
                .ToDictionary(row => row.boundIndex, row => row.Item2));
        return KotlinCollectionSlotSynthesis.GenericQueryBound(parameters[index], method, owner,
            type => type is TypeNode.Fqn named && IsInterfaceType(named), NullableTypeFrames, IsValueType);
    }

    // Resolve the declaration selected by the frontend, including the generic-parameter constraint vector that is
    // part of its identity.  This is intentionally a direct declaration lookup: the inherited-default fact names the
    // owner that supplies the body, so walking to a same-shaped ancestor would replace the frontend's decision with a
    // new backend resolution.  The returned types are the declaration types before nullable-TV erasure where the
    // trusted carrier states them, and the physical declaration types otherwise.
    // Referenced bounds use the declaration's source owner frame; selected bounds use the caller-stated owner frame.
    internal bool TrySelectedSourceMethodDeclaration(string ownerFqn, string sourceMember, int methodArity,
        IReadOnlyList<TypeNode> signature, TypeNode resolvedReturn, TypeNode[] ownerTypeArguments,
        BirTypeParameter[] selectedTypeParams, TypeNode[] selectedOwnerTypeArguments,
        out ReferencedMethodDeclaration declaration, string propertyAccessor = null, string selectedDeclarationId = null)
        => TrySelectedMethodDeclarationByShape(ownerFqn, sourceMember, methodArity, signature, resolvedReturn,
            ownerTypeArguments, KotlinOverrideSlotBridge.MethodTypeParameterShapeKey(selectedTypeParams, selectedOwnerTypeArguments),
            out declaration, propertyAccessor, selectedDeclarationId);

    bool TrySelectedMethodDeclarationByShape(string ownerFqn, string sourceMember, int methodArity,
        IReadOnlyList<TypeNode> signature, TypeNode resolvedReturn, TypeNode[] ownerTypeArguments,
        string selectedShape, out ReferencedMethodDeclaration declaration, string propertyAccessor,
        string selectedDeclarationId = null)
    {
        declaration = null;
        if (!TryMembersByBirOwner(ownerFqn, out var list)) return false;
        var sourceOwnerArguments = SourceHierarchyFrame(ownerFqn)?.OrdinaryArguments(ownerTypeArguments)
            ?? ownerTypeArguments;
        var matches = list.Where(member => (propertyAccessor == null
                    ? member.SourcePropertyName == null
                        && (member.DeclarationSourceName ?? member.SourceMethodName ?? member.Name) == sourceMember
                    : (member.SourcePropertyName ?? member.PropertyName) == sourceMember
                        && member.AccessorKind == propertyAccessor)
                && (member.NullableFrame?.SourceArity ?? member.MethodArity) == methodArity
                && KotlinOverrideSlotBridge.MethodTypeParameterShapeKey(
                    member.SemanticMethodTypeParams ?? member.MethodTypeParams, sourceOwnerArguments) == selectedShape
                // The selected Kotlin declaration is independent of the CLR carrier at this use site.
                && (selectedDeclarationId is not null ? member.DeclarationId == selectedDeclarationId
                    : MethodSignatureMatches(member, signature, resolvedReturn, ownerTypeArguments, kotlinParameters: true))
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
            CloneReferencedTypeParameters(match.MethodTypeParams), match.ReturnTypeNode is not TypeNode.Fqn { Name: "void" or "System.Void", Args: null },
            match.IsVirtual, match.ParamTypeNodes, match.ReturnTypeNode, match.NullableFrame,
            PhysicalDeclarationSignature(match).Parameters, match.DeclarationId)
        { ParameterNullableFlags = match.ParameterNullableFlags, QueryParameterBound = index => QueryParameterBound(match, ownerFqn, index) };
        return true;
    }

    // Resolve the exact DIRECT referenced declaration named by a frontend override edge. Covariant return selection
    // cannot compare the implementation return with the slot return — their deliberate difference is why the caller
    // needs the declaration — so identity is source member/property role + method arity + parameter vector. Kotlin
    // does not overload on return type. Inherited declarations are not searched here: kotc emits an override marker
    // for each interface that contributes a direct declaration (including a synthesized redeclaration on an
    // intermediate interface), and each such CLR MethodImpl must name that exact declaring interface.
    // Early declaration materialization compares Kotlin constraints; post-erasure slot bridges compare CLR
    // constraints. The caller states its phase so the two sides are always compared in the same representation.
    internal bool TrySelectedSourceOverrideDeclaration(string ownerFqn, string sourceMember, int methodArity,
        IReadOnlyList<TypeNode> signature, TypeNode[] ownerTypeArguments, BirTypeParameter[] selectedTypeParams,
        TypeNode[] implementationOwnerTypeArguments, bool selectedSuspend, out ReferencedMethodDeclaration declaration,
        string accessorKind = null, string selectedDeclarationId = null)
        => TrySelectedOverrideDeclarationByShape(ownerFqn, sourceMember, accessorKind, methodArity, signature,
            ownerTypeArguments, KotlinOverrideSlotBridge.MethodTypeParameterShapeKey(selectedTypeParams,
                implementationOwnerTypeArguments), selectedSuspend, out declaration, semanticConstraints: true,
            selectedDeclarationId: selectedDeclarationId);

    internal bool TrySelectedOverrideDeclarationByShape(string ownerFqn, string sourceMember, string accessorKind,
        int methodArity, IReadOnlyList<TypeNode> signature, TypeNode[] ownerTypeArguments, string selectedShape,
        bool selectedSuspend, out ReferencedMethodDeclaration declaration, bool semanticConstraints,
        string selectedDeclarationId)
    {
        declaration = null;
        if (!TryMembersByBirOwner(ownerFqn, out var list)) return false;
        var matches = list.Where(member => !member.IsStatic && !member.IsPropertyBridge
                && (selectedDeclarationId == null || member.DeclarationId == selectedDeclarationId)
                && (accessorKind == null
                    ? member.SourcePropertyName == null
                        && (member.DeclarationSourceName ?? member.SourceMethodName ?? member.Name) == sourceMember
                    : member.SourcePropertyName == sourceMember && member.AccessorKind == accessorKind)
                && (semanticConstraints ? member.NullableFrame?.SourceArity ?? member.MethodArity
                    : member.MethodArity) == methodArity
                && member.Suspend == selectedSuspend
                && KotlinOverrideSlotBridge.MethodTypeParameterShapeKey(
                    semanticConstraints ? member.SemanticMethodTypeParams ?? member.MethodTypeParams : member.MethodTypeParams,
                    ownerTypeArguments, semanticConstraints ? null : PhysicalConstraintType) == selectedShape
                // An explicit override identity selects the declaration even when its value slots were projected.
                // Comparing its exact construction to the implementer's carrier must not undo that selection.
                && (selectedDeclarationId != null
                    || AccessorSignatureMatches(member, signature, ownerTypeArguments, kotlinParameters: true))
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
            CloneReferencedTypeParameters(match.MethodTypeParams), match.ReturnTypeNode is not TypeNode.Fqn { Name: "void" or "System.Void", Args: null },
            match.IsVirtual, match.ParamTypeNodes, match.ReturnTypeNode, match.NullableFrame,
            PhysicalDeclarationSignature(match).Parameters, match.DeclarationId)
        { ParameterNullableFlags = match.ParameterNullableFlags, QueryParameterBound = index => QueryParameterBound(match, ownerFqn, index) };
        return true;
    }

    // Late bridge selection compares physical constraints, even while local declarations still use Kotlin
    // classifier names. Lower both sides into the same vocabulary; early semantic selection remains exact.
    internal TypeNode PhysicalConstraintType(TypeNode type) => BirTypeLowering.CanonicalPhysicalSlotType(
        BirTypeLowering.LowerPhysicalType(type, Aliases, IsValueType, PhysicalTypeNames,
            typeArg: true, nullableFrames: NullableTypeFrames));

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

    public static SignatureKey SignatureKeyOf(JsonArray signature, bool relaxed = false) =>
        SignatureKeyOf(signature.Select(TypeJson.Read).ToArray(), relaxed);

    public static SignatureKey SignatureKeyOf(IReadOnlyList<TypeNode> signature, bool relaxed = false) =>
        new(signature.Select(type => ParamKey(type, relaxed)));

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
    static bool DeclarationDescribesCall(TypeNode declaration, TypeNode call) =>
        DeclarationDescribesCallCore(declaration, call, true);

    // Selecting a generated slot compares source declarations, not a known declaration with its erasure.
    // In particular Any? and T? must not select the same slot merely because both can become object.
    internal static bool SourceDeclarationDescribesCall(TypeNode declaration, TypeNode candidate) =>
        DeclarationDescribesCallCore(declaration, candidate, false)
        || DeclarationDescribesCallCore(candidate, declaration, false);

    static bool DeclarationDescribesCallCore(TypeNode declaration, TypeNode call, bool allowVariableErasure)
    {
        bool Describes(TypeNode left, TypeNode right) => DeclarationDescribesCallCore(left, right, allowVariableErasure);
        if (declaration == call) return true;
        // A star projection states no bound, so it describes whatever the declaration says — the erasure the
        // reference twin shows as `object` is one such answer, not a different type. Without this a
        // Comparable<*> selector could not meet compareBy's Comparable<object> parameter.
        if (call is TypeNode.Star) return allowVariableErasure;
        if (declaration is TypeNode.Projection dp)
            return Describes(dp.Of, call);
        if (call is TypeNode.Projection cp)
            return Describes(declaration, cp.Of);
        if (declaration is TypeNode.Oblivious dOb)
            return Describes(dOb.Of, call);
        if (call is TypeNode.Oblivious cOb)
            return Describes(declaration, cOb.Of);
        // Reflection's declaration vocabulary can retain Nullable<T> as an ordinary constructed FQN while the
        // frontend descriptor uses BIR's structural nullable wrapper. They are one CLR value-type slot. Normalize
        // this seam before the reference-nullability rules below; otherwise a derived same-arity overload can become
        // the sole fallback candidate even though the frontend selected an inherited declaration.
        if (declaration is TypeNode.Fqn { Name: "System.Nullable", Args.Length: 1 } physicalNullable
            && call is TypeNode.Nullable callNullable)
            return Describes(physicalNullable.Args[0], callNullable.Of);
        if (declaration is TypeNode.Nullable declarationNullable
            && call is TypeNode.Fqn { Name: "System.Nullable", Args.Length: 1 } physicalCallNullable)
            return Describes(declarationNullable.Of, physicalCallNullable.Args[0]);
        // A method variable may already be erased to object in the reflected MethodDef, at the head or recursively
        // inside another physical type (Result<object> versus the selected Kotlin Result<T>, for example). Recognize
        // that stated physical boundary before nullable recursion. Identity has already selected this MethodDef, so
        // this validates its erasure rather than admitting the object slot as an overload-selection wildcard.
        if (allowVariableErasure && declaration is TypeNode.Fqn { Args: null } tvErasure
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
                return Describes(declaration, cNull.Of);
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
                && dfqn.Args.Select((p, i) => Describes(p, cfqn.Args[i])).All(x => x);
        }
        if (declaration is TypeNode.Nullable dn && call is TypeNode.Nullable cn)
            return Describes(dn.Of, cn.Of);
        if (declaration is TypeNode.Array da && call is TypeNode.Array ca)
            return Describes(da.Elem, ca.Elem);
        if (declaration is TypeNode.ByRef db && call is TypeNode.ByRef cb)
            return Describes(db.Of, cb.Of);
        if (declaration is TypeNode.Fn dfn && call is TypeNode.Fn cfn)
            return FunctionDeclarationDescribesCall(dfn, cfn, Describes);
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
    bool SemanticDeclarationDescribesCall(TypeNode declaration, TypeNode call)
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
        {
            // Companion declarations and materialized values can use different spellings of the same carrier.
            // Validate that identity through trusted metadata, never by reconstructing a nested type name.
            if (df.Args == null && cf.Args == null
                && !IsLocalEmittedType(df.Name) && !IsLocalEmittedType(cf.Name)
                && (TryCompanionPhysicalOwner(df.Name, out var declarationCompanion)
                    || TryCompanionMetadataCarrier(df.Name, out declarationCompanion))
                && (TryCompanionPhysicalOwner(cf.Name, out var callCompanion)
                    || TryCompanionMetadataCarrier(cf.Name, out callCompanion)))
                return declarationCompanion == callCompanion;
            // A preserved source descriptor can meet an already-projected value slot. The
            // producer's explicit existential-owner metadata establishes this equivalence;
            // neither a carrier name convention nor an erased object slot does so.
            if (cf.Args == null && TryExistentialPhysicalOwner(df.Name, out var declarationCarrier)
                && declarationCarrier == cf.Name
                || df.Args == null && TryExistentialPhysicalOwner(cf.Name, out var callCarrier)
                && callCarrier == df.Name) return true;
            // Either signature can already carry the producer-bound metadata identity. Compare both through
            // the recorded correspondence; physical declaration versus semantic use is equally legitimate.
            var sameOwner = RecordedPhysicalTypeName(df.Name) == RecordedPhysicalTypeName(cf.Name);
            if (!sameOwner || df.Args == null || cf.Args == null) return false;
            if (df.Args.Length == cf.Args.Length && df.Args.Select((type, index) =>
                    SemanticDeclarationDescribesCall(type, cf.Args[index])).All(matches => matches))
                return true;
            // The selected declaration's Kotlin carrier has source arguments; a materialized use carries the
            // producer's physical companion frame. Compare its authored ordinary positions, never infer them
            // from a backtick arity or assume companions form a suffix (nested owners can interleave them).
            var callArguments = cf.Args;
            if (SourceHierarchyFrame(cf.Name) is { } frame
                && df.Args.Length == frame.SourceArity && callArguments.Length == frame.PhysicalArity)
                callArguments = frame.OrdinaryArguments(callArguments);
            return df.Args.Length == callArguments.Length
                && df.Args.Select((type, index) =>
                    SemanticDeclarationDescribesCall(type, callArguments[index])).All(matches => matches);
        }
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

    static bool IsUncheckedGenericObjectGetter(MemberBinding member) =>
        member.AccessorKind == "get" && !member.IsPropertyBridge
        && IsObjectType(member.ReturnTypeNode) && member.KotlinReturnType is TypeNode.Tv;

    internal bool IsUncheckedGenericObjectGetter(string id, string owner, bool isStatic, int argCount, int methodArity) =>
        id != null && _declarationById.TryGetValue(id, out var member)
        && BareOwnerFqn(member.DeclarationPhysicalOwner ?? member.Owner) == BareOwnerFqn(owner)
        && member.IsStatic == isStatic && member.ParamCount == argCount && member.MethodArity == methodArity
        && IsUncheckedGenericObjectGetter(member);

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
        BirTypeParameter[] selectedTypeParams = null, TypeNode[] selectedOwnerTypeArguments = null,
        string selectedDeclarationId = null, bool includeUnchangedReturn = true)
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
                selectedOwnerTypeArguments: selectedOwnerTypeArguments,
                selectedDeclarationId: selectedDeclarationId, includeUnchangedMethodReturn: includeUnchangedReturn)
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
    internal bool TrySelectedNullableGenericSlotByShape(string ownerFqn, string name, bool isStatic, int argCount,
        int methodArity, IReadOnlyList<TypeNode> resolvedSignature, TypeNode resolvedReturn,
        TypeNode[] ownerTypeArguments, string selectedShape,
        out TypeNode declaredRet, out TypeNode[] declaredParams, out bool[] paramsRefused,
        out string physicalMember, out BirTypeParameter[] declarationTypeParams, out bool returnsValue,
        out TypeNode[] signatureParameters,
        bool semanticConstraints = false, string selectedPhysicalMember = null, string selectedDeclarationId = null)
    {
        var found = TrySelectedNullableGenericSlotCore(ownerFqn, name, isStatic, argCount, methodArity,
            resolvedSignature, resolvedReturn, ownerTypeArguments, selectedShape,
            out declaredRet, out declaredParams, out paramsRefused, out physicalMember, out declarationTypeParams,
            out returnsValue, out signatureParameters, semanticConstraints, selectedPhysicalMember, selectedDeclarationId);
        return found;
    }

    // Selected results own independent descriptor leaves; neither a source
    // executable nor another metadata-selection catalog is authored here.
    internal static BirTypeParameter[] CloneReferencedTypeParameters(IReadOnlyList<BirTypeParameter> parameters) =>
        parameters?.Select(parameter => (BirTypeParameter)parameter.DeepClone()).ToArray();

    bool TrySelectedNullableGenericSlotCore(string ownerFqn, string name, bool isStatic, int argCount,
        int methodArity, IReadOnlyList<TypeNode> resolvedSignature, TypeNode resolvedReturn,
        TypeNode[] ownerTypeArguments, string selectedShape,
        out TypeNode declaredRet, out TypeNode[] declaredParams, out bool[] paramsRefused,
        out string physicalMember, out BirTypeParameter[] declarationTypeParams, out bool returnsValue,
        out TypeNode[] signatureParameters,
        bool semanticConstraints, string selectedPhysicalMember, string selectedDeclarationId)
    {
        declaredRet = null;
        declaredParams = null;
        paramsRefused = null;
        physicalMember = null;
        declarationTypeParams = null;
        returnsValue = false;
        signatureParameters = null;
        if (ownerFqn == null || name == null) return false;
        var path = new HashSet<string>(StringComparer.Ordinal)
            { ReferenceWalkKey(ownerFqn, ownerTypeArguments) };
        if (FindDeclaredSlot(ownerFqn, name, isStatic, argCount, methodArity, path,
                out var ret, out var parameters, out var declaration,
                ownerTypeArguments: ownerTypeArguments, includeUnchangedMethod: true,
                methodSignature: resolvedSignature, methodReturn: resolvedReturn,
                selectedShape: selectedShape,
                semanticConstraints: semanticConstraints,
                selectedPhysicalMember: selectedPhysicalMember,
                selectedDeclarationId: selectedDeclarationId, physicalOverride: !semanticConstraints) != SlotLookup.Declared
            || declaration == null)
            return false;
        declaredRet = ret.Node;
        declaredParams = parameters.Select(parameter => parameter.Node).ToArray();
        paramsRefused = parameters.Select(parameter => parameter.Refused).ToArray();
        physicalMember = declaration.PhysicalMember;
        returnsValue = declaration.ReturnsValue;
        declarationTypeParams = CloneReferencedTypeParameters(declaration.TypeParams);
        signatureParameters = declaration.SignatureParameters;
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
        TypeNode methodReturn = null, BirTypeParameter[] selectedTypeParams = null,
        TypeNode[] selectedOwnerTypeArguments = null, bool semanticConstraints = false,
        string selectedPhysicalMember = null, string selectedDeclarationId = null,
        bool includeUnchangedMethodReturn = true, string selectedShape = null, bool physicalOverride = false)
    {
        declaredRet = default;
        declaredParams = null;
        declaredMethod = null;
        var lookupOwner = HasExactOwnerPunctuation(ownerFqn) ? ownerFqn : BareOwnerFqn(ownerFqn);
        if (TryMembersByBirOwner(lookupOwner, out var list))
        {
            var declaredHere = list.Where(m =>
                    (propertyName == null
                        ? selectedPhysicalMember != null
                            ? m.Name == selectedPhysicalMember
                            : (m.SourceMethodName ?? m.Name) == name
                        : !m.IsPropertyBridge && m.SourcePropertyName == propertyName
                            && m.AccessorKind == accessorKind)
                    && (selectedDeclarationId == null || m.DeclarationId == selectedDeclarationId)
                    && m.IsStatic == isStatic
                    && m.ParamCount == argCount
                    && m.MethodArity == methodArity
                    && m.ParamTypeNodes != null
                    && m.ParamTypeNodes.Length == argCount
                    && (propertyName != null || selectedTypeParams == null && selectedShape == null
                        || KotlinOverrideSlotBridge.MethodTypeParameterShapeKey(
                            semanticConstraints ? m.SemanticMethodTypeParams ?? m.MethodTypeParams : m.MethodTypeParams,
                            ownerTypeArguments, semanticConstraints ? null : PhysicalConstraintType)
                            == (selectedShape ?? KotlinOverrideSlotBridge.MethodTypeParameterShapeKey(selectedTypeParams,
                                selectedOwnerTypeArguments, semanticConstraints ? null : PhysicalConstraintType))))
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
                // A nullable companion frame is already materialized in this MethodDef. Re-erasing its semantic
                // carrier would replace the companion by object and lose substitution stability again.
                var explicitNullableFrame = member.NullableFrame != null
                    || _ownerNullableFrames.ContainsKey(member.Owner)
                    || _ownerNullableFrames.ContainsKey(lookupOwner);
                // A selected suspend projection is already a physical hot/cold MethodDef. Its logical Kotlin
                // result carrier belongs to the source declaration, not to the Task-returning MethodImpl row.
                // The exact unchecked-cast getter has an independent consumer projection. Reporting the closed
                // Object slot as a nullable erasure would overwrite the instantiated Kotlin expression type.
                declaredRet = physicalOverride ? new SlotFact(member.ReturnTypeNode, false)
                    : propertyName != null && !includeClosedPropertyReturn && IsUncheckedGenericObjectGetter(member)
                    ? default
                    : (includeUnchangedMethodReturn || includeClosedPropertyReturn)
                        && member.KotlinReturnType is TypeNode.Tv && IsObjectType(member.ReturnTypeNode)
                    ? new SlotFact(member.ReturnTypeNode, false)
                    : explicitNullableFrame
                    ? FramedSlot(member.NullableGenericRet, member.ReturnTypeNode,
                        selectedPhysicalMember == null, includeUnchangedMethodReturn)
                    : selectedPhysicalMember != null
                    ? new SlotFact(member.ReturnTypeNode, false)
                    : propertyName != null && includeClosedPropertyReturn
                    ? new SlotFact(member.NullableGenericRet ?? member.KotlinReturnType ?? member.ReturnTypeNode, false)
                    : propertyName == null && includeUnchangedMethod && includeUnchangedMethodReturn
                        ? new SlotFact(member.NullableGenericRet ?? member.KotlinReturnType ?? member.ReturnTypeNode, false)
                        : DeclaredSlot(member.NullableGenericRet, member.ReturnTypeNode);
                declaredParams = new SlotFact[argCount];
                for (var i = 0; i < argCount; i++)
                    declaredParams[i] = physicalOverride || propertyName == null && IsExistentialValueParameter(member.ParamTypeNodes[i])
                        ? new SlotFact(member.ParamTypeNodes[i], false)
                        : explicitNullableFrame
                        ? FramedSlot(member.NullableGenericParams?[i], member.ParamTypeNodes[i], selectedPhysicalMember == null)
                        : selectedPhysicalMember != null
                        ? new SlotFact(member.ParamTypeNodes[i], false)
                        : propertyName == null
                        ? includeUnchangedMethod
                            ? new SlotFact(member.NullableGenericParams?[i] ?? member.ParamTypeNodes[i], false)
                            : DeclaredSlot(member.NullableGenericParams?[i], member.ParamTypeNodes[i])
                        : ExactPropertyParameterSlot(member.NullableGenericParams?[i], member.ParamTypeNodes[i],
                            accessorSignature?[i]);
                if (propertyName == null)
                    declaredMethod = new MethodSlotIdentity(member.Name,
                        CloneReferencedTypeParameters(semanticConstraints ? member.SemanticMethodTypeParams ?? member.MethodTypeParams
                            : member.MethodTypeParams),
                        member.ReturnTypeNode != null
                            && member.ReturnTypeNode is not TypeNode.Fqn { Name: "void" or "System.Void", Args: null },
                        member.SignatureParameters);
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
                methodSignature, methodReturn, selectedTypeParams, selectedOwnerTypeArguments, semanticConstraints,
                selectedPhysicalMember, selectedDeclarationId, includeUnchangedMethodReturn, selectedShape, physicalOverride);
            path.Remove(key);
            if (found == SlotLookup.Refused) return SlotLookup.Refused;
            if (found != SlotLookup.Declared) continue;
            var mret = MapThroughSupertype(sret, super.Args);
            var mps = sps.Select(p => MapThroughSupertype(p, super.Args)).ToArray();
            var mmethod = smethod == null ? null : new MethodSlotIdentity(smethod.PhysicalMember,
                KotlinOverrideSlotBridge.SubstituteOwnerTypeParameterConstraints(smethod.TypeParams, super.Args),
                smethod.ReturnsValue,
                smethod.SignatureParameters.Select(parameter => SupertypeGraph.SubstOwnerTvs(parameter, super.Args)).ToArray());
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
        return left.PhysicalMember == right.PhysicalMember && left.ReturnsValue == right.ReturnsValue
            && left.SignatureParameters.SequenceEqual(right.SignatureParameters)
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
        var explicitNullableFrame = _ownerNullableFrames.ContainsKey(ctor.Owner)
            || _ownerNullableFrames.ContainsKey(lookupOwner);
        var facts = new SlotFact[argCount];
        for (var i = 0; i < argCount; i++)
            facts[i] = explicitNullableFrame ? new SlotFact(Canonical(ctor.ParamTypeNodes[i]), false)
                : DeclaredSlot(ctor.NullableGenericParams?[i], ctor.ParamTypeNodes[i]);
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

    // A materialized frame does not grant nullable-erasure ownership of an unrelated closed return ABI
    // (for example unchecked Any? as T). Keep its caller projection for the physical value-flow pass.
    static SlotFact FramedSlot(TypeNode carrier, TypeNode physical, bool allowScalarCarrier, bool includeClosed = true)
        => allowScalarCarrier && carrier is TypeNode.Nullable { Of: TypeNode.Tv }
            ? DeclaredSlot(carrier, physical)
            : new SlotFact(includeClosed ? Canonical(physical) : OpenPhysical(physical), false);

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
        if (!TryKotlinPropertyAccessorBinding(ownerFqn, propertyName, accessorKind, paramCount,
            methodArity, accessorSignature, ownerTypeArguments, out var selected)) return false;
        physicalMethodName = selected.Name;
        isVirtual = selected.IsVirtual;
        return true;
    }

    internal bool TryKotlinPropertyAccessorBinding(string ownerFqn, string propertyName, string accessorKind, int paramCount,
        int methodArity, IReadOnlyList<TypeNode> accessorSignature, TypeNode[] ownerTypeArguments,
        out MemberBinding selected)
    {
        selected = null;
        if (!TryMembersByBirOwner(ownerFqn, out var list)) return false;
        var declarations = list.Where(member => member.SourcePropertyName == propertyName
                && !member.IsPropertyBridge && member.AccessorKind == accessorKind && member.ParamCount == paramCount
                && member.MethodArity == methodArity)
            .ToList();
        if (accessorSignature == null && declarations.Count > 1) return false;
        var candidates = declarations.Where(member => AccessorSignatureMatches(
                member, accessorSignature, ownerTypeArguments))
            .GroupBy(member => (member.Name, member.IsVirtual, member.MetadataToken)).ToList();
        if (candidates.Count != 1) return false;
        selected = candidates[0].First();
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
        return ReferencedMethodImplRows(owner, sources[0], bridges);
    }

    // A private generated method's explicit source-declaration association is independent of its erased signature.
    // Consume only that exact relation; same-name source overloads are never a bridge-selection fallback.
    public IReadOnlyList<ReferencedPropertyMethodImpl> ReferencedMethodImpls(string ownerFqn, string sourceDeclaration)
    {
        var owner = StripGenericArity(DottedFqn(BareOwnerFqn(ownerFqn)));
        if (sourceDeclaration == null || !_dotKtOwners.Contains(owner)
            || !TryMembersByBirOwner(owner, out var members)) return Array.Empty<ReferencedPropertyMethodImpl>();
        var sources = members.Where(member => member.DeclarationId == sourceDeclaration).ToList();
        if (sources.Count != 1) return Array.Empty<ReferencedPropertyMethodImpl>();
        return ReferencedMethodImplRows(owner, sources[0], members.Where(member =>
            member.SourceMethodDeclaration == sourceDeclaration
            && member.ParamTypeNodes != null && member.ReturnTypeNode != null));
    }

    IReadOnlyList<ReferencedPropertyMethodImpl> ReferencedMethodImplRows(string owner, MemberBinding source,
        IEnumerable<MemberBinding> bridges)
    {
        var result = new List<ReferencedPropertyMethodImpl>();
        foreach (var bridge in bridges)
        {
            if (!_methodImplsByBody.TryGetValue((owner, bridge.MetadataToken), out var implementations)) continue;
            foreach (var implementation in implementations)
            {
                BirTypeParameter[] declarationTypeParams = null;
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
                        declarationTypeParams = CloneReferencedTypeParameters(declarationCandidates[0].MethodTypeParams);
                }
                result.Add(new ReferencedPropertyMethodImpl(
                    source.Name,
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
        TypeNode[] ownerTypeArguments, bool kotlinParameters = false)
    {
        if (signature == null) return true;
        if (member.ParamTypeNodes == null || member.ParamTypeNodes.Length != signature.Count) return false;
        for (var i = 0; i < signature.Count; i++)
        {
            var declared = member.NullableGenericParams is { } carriers && i < carriers.Length && carriers[i] != null
                ? carriers[i]
                : kotlinParameters && member.KotlinParameterTypes is { } sourceParameters
                    ? sourceParameters[i]
                : member.ParamTypeNodes[i];
            if (declared == null) return false;
            if (ownerTypeArguments != null)
                declared = SupertypeGraph.SubstOwnerTvs(declared, ownerTypeArguments);
            if (!AccessorDeclarationDescribesCall(declared, signature[i])) return false;
        }
        return true;
    }

    bool MethodSignatureMatches(MemberBinding member, IReadOnlyList<TypeNode> signature,
        TypeNode resolvedReturn, TypeNode[] ownerTypeArguments, bool kotlinParameters = false)
    {
        if (!AccessorSignatureMatches(member, signature, ownerTypeArguments, kotlinParameters)) return false;
        if (resolvedReturn == null) return true;
        var declared = member.NullableGenericRet ?? member.KotlinReturnType ?? member.ReturnTypeNode;
        if (declared == null) return false;
        if (ownerTypeArguments != null)
            declared = SupertypeGraph.SubstOwnerTvs(declared, ownerTypeArguments);
        // This comparison consumes a Kotlin method-return fact, not a physical value type. CLR void projects
        // to non-null Unit here; do not restore that equivalence in parameter keys or delegate result types.
        if (declared is TypeNode.Fqn { Args: null, Name: "void" or "System.Void" }
            && resolvedReturn is TypeNode.Fqn { Args: null, Name: "kotlin.Unit" })
            return true;
        return AccessorDeclarationDescribesCall(declared, resolvedReturn)
            // The inherited implementation fact can already carry a projected
            // classifier. The producer's recorded correspondence, not an
            // arbitrary return-type wildcard, relates it to the Kotlin getter.
            || declared is TypeNode.Fqn && resolvedReturn is TypeNode.Fqn
                && SemanticDeclarationDescribesCall(declared, resolvedReturn);
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

    // Exact referenced declaration lookup for inherited-member owner binding.  The signature is
    // structural (including type-vs-method Tv scope/index), not a name/arity guess, so overloads
    // remain distinct.  Multiple identical candidates are treated as ambiguous and refused.
    public bool DeclaresExactInstanceMember(string ownerToken, string memberName, int methodArity,
        IReadOnlyList<TypeNode> signature, TypeNode[] ownerTypeArguments, bool sourceNames = false)
    {
        if (ownerToken == null || memberName == null || IsAliasedOwner(ownerToken)
            || !TryMembersByBirOwner(ownerToken, out var list)) return false;
        // Protocol recognition precedes physical declaration binding; an accessor is not a source method slot.
        var candidates = list.Where(m => !m.IsStatic && (!sourceNames || m.AccessorKind == null)
            && (sourceNames ? m.DeclarationSourceName ?? m.SourceMethodName ?? m.Name : m.Name) == memberName
            && m.MethodArity == methodArity
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
        if (_physicalTypeBySemanticName.TryGetValue(ownerFqn, out var recorded)
            && _membersByPhysicalOwner.TryGetValue(recorded, out members)) return true;
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
        MetadataDeclaredMembers.EnsureAvailable();
        // bir2cir is a ref-READER: a consumed cross-module DotKt library references the RUNTIME stdlib
        // (DotKt.Stdlib) in its `[kotlin.clr.*]` round-trip metadata, but bir2cir carries only the REFERENCE
        // twin — alias so that reference resolves to DotKt.Private.Stdlib (same type shapes).
        var catalog = ManagedReferenceCatalog.Create(refs, "bir2cir", refStdlibAliasesRuntime: true);
        var assemblies = new List<ReferenceAssembly>();
        var nativeSignatures = new NativeSignatureReader();
        if (catalog.Entries.Count == 0) return new ReferenceMetadataIndex(assemblies, catalog, nativeSignatures);
        using var mlc = catalog.CreateMetadataLoadContext();
        foreach (var entry in catalog.Entries)
        {
            var reference = entry.Path;
            var identity = entry.Identity;
            assemblies.Add(new ReferenceAssembly(
                reference,
                identity.Name ?? Path.GetFileNameWithoutExtension(reference),
                identity.Version?.ToString() ?? "",
                ReferenceMetadataReader.Read(reference, mlc, nativeSignatures.Method,
                    parameter => ReadDeclarationSignatureSlot(parameter, nativeSignatures))));
        }

        return new ReferenceMetadataIndex(assemblies, catalog, nativeSignatures);
    }

    internal Type FunctionSignatureDiscriminator(ParameterInfo parameter)
    {
        var source = KotlinTypeOf(parameter.GetCustomAttributesData(), parameter.Member.DeclaringType?.Assembly);
        if (source == null || !FunctionValueRepresentation.ContainsOrdinaryFunction(TypeJson.Write(source)))
            return null;
        // The compiler-authored function slot carries its pre-carrier identity as a custom modifier.
        // Do not infer it from the shared delegate storage type or from another overload's body.
        var modifiers = parameter.GetOptionalCustomModifiers();
        if (modifiers.Length != 1)
            throw new InvalidDataException("A Kotlin function parameter must carry one declaration signature discriminator");
        return modifiers[0];
    }

    static void SelfTestRichEnumSelection()
    {
        static BirConstant DefaultText(string text) => new() {
            Type = new TypeNode.Fqn("System.String"), Value = new BirLiteral.TextLiteral(text),
        };
        static string ReadDefault(Dictionary<int, IBirExpression> values) =>
            values?.GetValueOrDefault(0) is BirConstant { Value.Value: BirLiteral.TextLiteral text } ? text.Value : null;
        if (Same(new TypeNode[] { null }, new TypeNode[] { new TypeNode.Fqn("System.String") }))
            throw new InvalidOperationException("An absent nullable-generic carrier matched a concrete type");
        var first = new RichEnumMetadata(new Dictionary<string, string> { ["FIRST"] = "firstField" },
            "nameField", "ordinalField", "valuesApi", "parseApi");
        var second = first with { EntryFields = new Dictionary<string, string> { ["OTHER"] = "otherField" } };
        var a = new ReferenceDotKtMetadata();
        var b = new ReferenceDotKtMetadata();
        a.RichEnums["CollisionRich"] = first;
        b.RichEnums["CollisionRich"] = second;
        a.RichEnums["NameConflict"] = first;
        b.RichEnums["NameConflict"] = first with { Name = "differentNameField" };
        a.RichEnums["OrdinalConflict"] = first;
        b.RichEnums["OrdinalConflict"] = first with { Ordinal = "differentOrdinalField" };
        a.RichEnums["UniqueInternalRich"] = first;
        a.RichEnums["IdenticalRich"] = first;
        b.RichEnums["IdenticalRich"] = first;
        a.KotlinDefaultsByDeclarationId["default-conflict"] = new() { [0] = DefaultText("a") };
        b.KotlinDefaultsByDeclarationId["default-conflict"] = new() { [0] = DefaultText("b") };
        a.KotlinDefaultsByDeclarationId["unique-default"] = new() { [0] = DefaultText("retained") };
        var exact = new SignatureKey(new[] { ParamKeyFqn("kotlin.collections.List") });
        var relaxed = new SignatureKey(new[] { ParamKeyFqn("kotlin.collections.List", relaxed: true) });
        var projected = new SignatureKey(new[] { ParamKeyFqn("System.Collections.Generic.IReadOnlyList") });
        a.KotlinDefaults[new("fold.A", "pick", 1, exact)] = new() { [0] = DefaultText("exact") };
        a.KotlinDefaults[new("fold.A", "pick", 1, relaxed, Relaxed: true)] = new() { [0] = DefaultText("relaxed") };
        foreach (var (owner, metadata) in new[] { ("fold.A", a), ("fold.B", b) })
        {
            metadata.KotlinDefaults[new(owner, "same", 1)] = new() { [0] = DefaultText("same") };
            metadata.KotlinDefaults[new(owner, "different", 1)] = new() { [0] = DefaultText(owner) };
            metadata.KotlinDefaults[new(owner, CtorKeyName, 1)] = new() { [0] = DefaultText(owner) };
        }
        var conflicted = new DefaultKey("fold.A", "conflicted", 1);
        a.KotlinDefaults[conflicted] = new() { [0] = DefaultText("unused") };
        a.KotlinDefaultsConflicted.Add(conflicted);
        a.InlinePayloadsByDeclarationId["inline-conflict"] = new BirInlinePayload { Body = Array.Empty<IBirStatement>() };
        b.InlinePayloadsByDeclarationId["inline-conflict"] = new BirInlinePayload { Body = new IBirStatement[] { new BirReturn() } };
        a.InlinePayloadsByDeclarationId["unique-inline"] = new BirInlinePayload { Body = Array.Empty<IBirStatement>() };
        var sharedDeclaration = new MemberBinding("CollisionRich", "read", 1, null, false, false,
            DeclarationId: "same-declaration", ReturnTypeNode: new TypeNode.Fqn("System.Int32"),
            MetadataToken: 0x06000001, ParamTypeNodes: new TypeNode[] { new TypeNode.Fqn("System.String") },
            NullableGenericParams: new TypeNode[] { null });
        a.MemberBindings.Add(sharedDeclaration);
        b.MemberBindings.Add(sharedDeclaration with {
            MetadataToken = 0x06000005, NullableGenericParams = new TypeNode[] { null },
        });
        var assemblies = new List<ReferenceAssembly> {
            new("a.dll", "a", "1", a), new("b.dll", "b", "1", b),
        };
        var catalog = ManagedReferenceCatalog.Create(Array.Empty<string>(), "bir2cir", refStdlibAliasesRuntime: true);
        foreach (var order in new[] { assemblies, assemblies.AsEnumerable().Reverse().ToList() })
        {
            // Building an index must not reject an unused collision or discard other metadata.
            var index = new ReferenceMetadataIndex(order, catalog);
            if (ReadDefault(index.KotlinDefaultValuesFor("fold.A", "pick", 1, exact, relaxed)) != "exact"
                || ReadDefault(index.KotlinDefaultValuesFor("fold.A", "pick", 1, projected, relaxed)) != "relaxed"
                || ReadDefault(index.KotlinDefaultValuesFor(null, "same", 1)) != "same"
                || index.KotlinDefaultsAmbiguous(null, "same", 1)
                || index.KotlinDefaultValuesFor(null, "different", 1) != null || !index.KotlinDefaultsAmbiguous(null, "different", 1)
                || index.KotlinDefaultValuesFor(null, "conflicted", 1) != null || !index.KotlinDefaultsAmbiguous(null, "conflicted", 1)
                || ReadDefault(index.KotlinDefaultValuesFor("fold.A", CtorKeyName, 1)) != "fold.A"
                || ReadDefault(index.KotlinDefaultValuesFor("fold.B", CtorKeyName, 1)) != "fold.B"
                || index.KotlinDefaultValuesFor(null, CtorKeyName, 1) != null || index.KotlinDefaultsAmbiguous(null, CtorKeyName, 1))
                throw new InvalidOperationException("Default lookup lost signature precedence, ownerless conflict handling or constructor separation");
            if (index.KotlinDefaultValuesForDeclarationIdentity("unique-default")?[0] is not BirConstant {
                    Value.Value: BirLiteral.TextLiteral { Value: "retained" } }
                || index.InlineValueByDeclarationIdentity("unique-inline")?.Body.OrDefault()?.Length != 0)
                throw new InvalidOperationException("Unused collisions discarded unrelated inline/default metadata");
            var typedDefaults = index.KotlinDefaultValuesForDeclarationIdentity("unique-default");
            ((BirConstant)typedDefaults[0]).Value = new BirLiteral.TextLiteral("mutated");
            typedDefaults.Clear();
            if (index.KotlinDefaultValuesForDeclarationIdentity("unique-default")[0] is not BirConstant {
                    Value.Value: BirLiteral.TextLiteral { Value: "retained" } })
                throw new InvalidOperationException("Default metadata getters expose mutable cached authority");
            foreach (var owner in new[] { "UniqueInternalRich", "IdenticalRich" })
                if (!index.TryKotlinRichEnumStaticApis(owner, out var values, out var parse)
                    || values != first.Values || parse != first.ValueOf
                    || !index.TryKotlinRichEnumStaticApi(owner, "valueOf", 1, out var api) || api != first.ValueOf
                    || !index.TryKotlinRichEnumEntryField(owner, "FIRST", out var field) || field != "firstField"
                    || !index.TryKotlinRichEnumInstanceFields(owner, out var name, out var ordinal)
                    || name != first.Name || ordinal != first.Ordinal)
                    throw new InvalidOperationException("Rich-enum indexing lost unambiguous producer metadata");
            Func<string, bool>[] queries = {
                owner => index.TryKotlinRichEnumStaticApis(owner, out _, out _),
                owner => index.TryKotlinRichEnumStaticApi(owner, "values", 0, out _),
                owner => index.TryKotlinRichEnumEntryField(owner, "FIRST", out _),
                owner => index.TryKotlinRichEnumInstanceFields(owner, out _, out _),
            };
            foreach (var owner in new[] { "CollisionRich", "NameConflict", "OrdinalConflict" })
                foreach (var query in queries)
                {
                    var ambiguous = false;
                    try { query(owner); }
                    catch (InvalidOperationException) { ambiguous = true; }
                    if (!ambiguous) throw new InvalidOperationException("Rich-enum selection chose ambiguous metadata");
                }
            foreach (Action query in new Action[] {
                () => index.KotlinDefaultValuesForDeclarationIdentity("default-conflict"),
                () => index.InlineValueByDeclarationIdentity("inline-conflict"),
            })
            {
                var ambiguous = false;
                try { query(); }
                catch (InvalidOperationException) { ambiguous = true; }
                if (!ambiguous) throw new InvalidOperationException("Selection chose ambiguous inline/default metadata");
            }
            index.SetLocalEmittedTypes(new HashSet<string> { "CollisionRich", "UniqueInternalRich" });
            if (queries.Any(query => query("CollisionRich") || query("UniqueInternalRich")))
                throw new InvalidOperationException("Referenced rich-enum metadata replaced a local declaration");
        }
    }

    internal static void SelfTest()
    {
        CarrierScanSelfTest();
        SelectedSourceConstraintShapeSelfTest();
        SelfTestPhysicalTypeNameSnapshots();
        SelfTestRichEnumSelection();
        ClrMemberResolution.NativeSignatureLifetimeSelfTest();
        var nativeDescriptors = Build(new[] { typeof(object).Assembly.Location });
        nativeDescriptors._ownerAlias["ProofObject"] = "System.Object";
        nativeDescriptors._ownerAlias["kotlin.Int"] = "System.Int32";
        var aliasCall = new BirGenericStatic { Type = new TypeNode.Fqn("System.Runtime.CompilerServices.Unsafe"), Method = "As",
            TypeArgs = new TypeNode[] { new TypeNode.Tv("method", 0) },
            ResolvedMemberParams = new TypeNode[] { new TypeNode.Fqn("ProofObject") } };
        var primitiveCall = new BirGenericStatic { Type = new TypeNode.Fqn("System.MemoryExtensions"), Method = "AsSpan",
            TypeArgs = new TypeNode[] { new TypeNode.Tv("method", 0) }, ResolvedMemberParams = new TypeNode[] {
                new TypeNode.Array(new TypeNode.Tv("method", 0)), new TypeNode.Fqn("kotlin.Int"), new TypeNode.Fqn("kotlin.Int") } };
        var aliasFacts = ClrMemberResolution.SelectedMethodTypeParametersInPhysicalFrame(null, aliasCall, nativeDescriptors);
        if (aliasFacts.Length != 1
            || ClrMemberResolution.SelectedMethodTypeParametersInPhysicalFrame(null, primitiveCall, nativeDescriptors).Length != 1)
            throw new InvalidOperationException("Native proof selection lost formal alias/primitive physical lowering");
        if (!aliasFacts[0].SpecialConstraints.OrEmpty().Contains("class"))
            throw new InvalidOperationException("Native proof selection required a source spelling for a CLR-only flag");
        // The typed parameter-facts self-test covers open owner variables,
        // missing descriptors and mismatched override frames. Keep the closed
        // primitive-alias owner case here beside its formal alias setup.
        var closedProofArgument = new TypeNode.Fqn("kotlin.String");
        TypeNode[] ConvertParameters(TypeNode argument) => new TypeNode[] {
            new TypeNode.Fqn("System.Converter`2", new TypeNode[] { argument, new TypeNode.Tv("method", 0) }),
        };
        var closedProofOwner = new TypeNode.Fqn("System.Collections.Generic.List`1", new TypeNode[] { closedProofArgument });
        var closedProofCall = new BirGenericInstance { Type = closedProofOwner, Method = "ConvertAll",
            TypeArgs = new TypeNode[] { new TypeNode.Tv("method", 0) },
            ResolvedMemberParams = ConvertParameters(new TypeNode.Tv("type", 0)) };
        if (ClrMemberResolution.SelectedMethodTypeParametersInPhysicalFrame(null, closedProofOwner, "ConvertAll", 1, true,
                ConvertParameters(closedProofArgument), nativeDescriptors, new TypeNode[] { closedProofArgument }).Length != 1
            || ClrMemberResolution.SelectedMethodTypeParametersInPhysicalFrame(null, closedProofCall, nativeDescriptors).Length != 1)
            throw new InvalidOperationException("Native proof selection confused override and open call owner frames");
        var invalidDescriptor = aliasCall with { ResolvedMemberParams = new TypeNode[] { new TypeNode.Fqn("System.Int32") } };
        var rejected = false;
        try { ClrMemberResolution.SelectedMethodTypeParametersInPhysicalFrame(null, invalidDescriptor, nativeDescriptors); }
        catch (InvalidOperationException) { rejected = true; }
        if (!rejected) throw new InvalidOperationException("Native proof selection guessed from a unique method name");
        // PhysicalParameterFactsSelfTest covers admitted shipped declarations,
        // foreign rows and local/unadmitted exclusions through the typed getter.
        var returnSource = new TypeNode.Tv("method", 0);
        var returnStorage = new TypeNode.Tv("method", 1);
        var objectReturn = new TypeNode.Fqn("System.Object");
        if (FramedSlot(returnSource, objectReturn, true, false).Node != null
            || FramedSlot(returnSource, objectReturn, true).Node != Canonical(objectReturn)
            || FramedSlot(returnSource, returnStorage, true, false).Node != returnStorage
            || FramedSlot(new TypeNode.Nullable(returnSource), objectReturn, true, false).Node
                != new TypeNode.Nullable(returnSource))
            throw new InvalidOperationException("Materialized frame stole closed return ownership or lost scalar/open slots");
        var allocatedIdentity = ParseDeclarationIdentityPayload(new JsonObject {
            ["id"] = "allocated-slot", ["name"] = "accept",
            [DeclarationSignatureIdentity.AllocationMetadataKey] = true,
        }, 0);
        var unallocatedIdentity = ParseDeclarationIdentityPayload(new JsonObject {
            ["id"] = "function-slot", ["name"] = "accept",
        }, 0);
        if (!allocatedIdentity.SignatureAllocated || unallocatedIdentity.SignatureAllocated)
            throw new InvalidOperationException("Declaration signature allocation lost its explicit owning role");
        {
            var index = Build(Array.Empty<string>());
            index._ownerKindByPhysicalOwner["ReferencedSlot"] = "interface";
            var signature = new TypeNode.Mod(false, new TypeNode.Fqn("ExistingFunctionIdentity"),
                new TypeNode.Fqn("System.Object"));
            var binding = new MemberBinding("ReferencedSlot", "accept", 1, null, true, false) {
                SignatureParameters = new TypeNode[] { signature }, SignatureReturn = new TypeNode.Fqn("void"),
            };
            index._declarationById["referenced-slot"] = binding;
            var implementation = new BirFunctionDeclaration { Name = "accept", DeclarationId = "implementation",
                DeclarationSourceName = "accept", SignatureOverrideDeclarations = new[] { "referenced-slot" },
                Params = new[] { new BirParameter { Name = "value", Type = new TypeNode.Fqn("System.Object") } },
                Ret = new TypeNode.Fqn("void"), Body = Array.Empty<IBirStatement>() };
            var owner = new BirClassDefinition { Name = "Implementation", Kind = "class",
                Interfaces = new TypeNode[] { new TypeNode.Fqn("ReferencedSlot") }, Methods = new[] { implementation } };
            var root = new BirFileDeclaration { Types = new IBirTypeDeclaration[] { owner } };
            var plan = new DeclarationSignatureIdentity.SignatureAllocationPlan(new Dictionary<string, TypeNode[]>());
            DeclarationSignatureIdentity.BindSelectedOverridesSource(new[] { root }, plan, index, _ => false,
                (_, _, _) => { }, _ => { }, _ => { });
            if (owner.Methods.Value.Length != 1)
                throw new InvalidOperationException("Existing function identity acquired a second signature-allocation bridge");
            implementation.SignatureOverrideDeclarations = new[] { "referenced-slot" };
            index._declarationById["referenced-slot"] = binding with { SignatureAllocated = true };
            DeclarationSignatureIdentity.BindSelectedOverridesSource(new[] { root }, plan, index, _ => false,
                (_, _, _) => { }, _ => { }, _ => { });
            if (owner.Methods.Value.Length != 2 || owner.Methods.Value[1].ClrInterfaceImpls.Value.Length != 1)
                throw new InvalidOperationException("A referenced signature allocation lost its explicit slot obligation");
        }
        SelfTestSourceHierarchyFrames();
        {
            var index = Build(Array.Empty<string>());
            var root = new TypeNode.Tv("type", 1);
            var nullableRoot = new TypeNode.Tv("type", 2);
            BirTypeParameter[] Bounds(TypeNode bound) => new[] { new BirTypeParameter {
                Name = "T", Constraints = new TypeNode[] { bound },
            } };
            var declaringArgs = new TypeNode[] { root, nullableRoot };
            var callerArgs = new TypeNode[] { new TypeNode.Tv("type", 0), root };
            var result = new TypeNode.Fqn("kotlin.Int");
            index._membersByOwner["SelectedOwnerFrame"] = new List<MemberBinding> {
                new("SelectedOwnerFrame", "count", 0, null, false, false, MethodArity: 1,
                    ParamTypeNodes: Array.Empty<TypeNode>(), ReturnTypeNode: result,
                    MethodTypeParams: Bounds(new TypeNode.Tv("type", 0)),
                    SemanticMethodTypeParams: Bounds(new TypeNode.Tv("type", 0))),
            };
            index._ownerNullableFrames["SelectedOwnerFrame"] = new NullableRepresentationFrame(1, new[] { 0 });
            if (!index.TrySelectedSourceMethodDeclaration("SelectedOwnerFrame", "count", 1,
                    Array.Empty<TypeNode>(), result, declaringArgs, Bounds(root), callerArgs, out _)
                || index.TrySelectedSourceMethodDeclaration("SelectedOwnerFrame", "count", 1,
                    Array.Empty<TypeNode>(), result, declaringArgs, Bounds(root), declaringArgs, out _)
                || index.TrySelectedSourceMethodDeclaration("SelectedOwnerFrame", "count", 1,
                    Array.Empty<TypeNode>(), result, declaringArgs, Bounds(new TypeNode.Tv("type", 0)), callerArgs, out _))
                throw new InvalidOperationException("Selected method constraints mixed declaration and caller owner frames");
            index._ownerNullableFrames["SelectedOwnerFrame"] = new NullableRepresentationFrame(1,
                new[] { 0 }, physicalOrder: new[] { 1, 0 });
            index._membersByOwner["SelectedOwnerFrame"][0] = index._membersByOwner["SelectedOwnerFrame"][0] with {
                MethodTypeParams = Bounds(new TypeNode.Tv("type", 1)),
            };
            if (!index.TrySelectedSourceMethodDeclaration("SelectedOwnerFrame", "count", 1,
                    Array.Empty<TypeNode>(), result, new TypeNode[] { nullableRoot, root }, Bounds(root), callerArgs,
                    out var permutedDeclaration)
                || permutedDeclaration.TypeParams[0].Constraints.Value[0] != new TypeNode.Tv("type", 1))
                throw new InvalidOperationException("Selected source constraints ignored the declared owner frame permutation");
        }
        {
            var index = Build(Array.Empty<string>());
            var source = new TypeNode.Tv("method", 0);
            BirTypeParameter[] Frame(params string[] names) => names
                .Select(name => new BirTypeParameter { Name = name }).ToArray();
            index._membersByOwner["InheritedFrame"] = new List<MemberBinding> {
                new("InheritedFrame", "echo", 1, null, false, false,
                    Suspend: true, MethodArity: 2,
                    ParamTypeNodes: new TypeNode[] { new TypeNode.Tv("method", 1) },
                    NullableGenericParams: new TypeNode[] { source },
                    ReturnTypeNode: new TypeNode.Fqn("System.Object"), SuspendReturnType: source,
                    MethodTypeParams: Frame("T", "storageT"),
                    SemanticMethodTypeParams: Frame("T"),
                    NullableFrame: new NullableRepresentationFrame(1, Array.Empty<int>(),
                        storageIndices: new[] { 0 })),
            };
            if (!index.TrySelectedOverrideDeclarationByShape("InheritedFrame", "echo", null, 1,
                    new TypeNode[] { source }, Array.Empty<TypeNode>(),
                    KotlinOverrideSlotBridge.MethodTypeParameterShapeKey(Frame("T"), Array.Empty<TypeNode>()),
                    true, out var selected, semanticConstraints: true, selectedDeclarationId: null)
                || selected.Parameters[0] != source
                || selected.PhysicalParameters[0] != new TypeNode.Tv("method", 1)
                || index.TrySelectedOverrideDeclarationByShape("InheritedFrame", "echo", null, 1,
                    new TypeNode[] { source }, Array.Empty<TypeNode>(),
                    KotlinOverrideSlotBridge.MethodTypeParameterShapeKey(Frame("T"), Array.Empty<TypeNode>()),
                    true, out _, semanticConstraints: false, selectedDeclarationId: null))
                throw new InvalidOperationException("Inherited slot selection mixed source and physical generic arities");
            if (!index.TrySelectedSourceMethodDeclaration("InheritedFrame", "echo", 1,
                    new TypeNode[] { source }, source, Array.Empty<TypeNode>(), Frame("T"),
                    Array.Empty<TypeNode>(), out var inherited)
                || inherited.PhysicalParameters[0] != new TypeNode.Tv("method", 1)
                || index.TrySelectedSourceMethodDeclaration("InheritedFrame", "echo", 2,
                    new TypeNode[] { source }, source, Array.Empty<TypeNode>(), Frame("T"),
                    Array.Empty<TypeNode>(), out _))
                throw new InvalidOperationException("Inherited implementation selection lost its source arity or selected physical parameter");
            index._existentialPhysicalBySemanticOwner["Stored"] = "StoredCarrier";
            index._membersByOwner["CarrierGetter"] = new List<MemberBinding> {
                new("CarrierGetter", "read", 0, null, false, false,
                    SourcePropertyName: "value", AccessorKind: "get",
                    ParamTypeNodes: Array.Empty<TypeNode>(),
                    NullableGenericRet: new TypeNode.Fqn("Stored", new TypeNode[] { new TypeNode.Tv("type", 0) }),
                    ReturnTypeNode: new TypeNode.Fqn("StoredCarrier")),
            };
            if (!index.TrySelectedSourceMethodDeclaration("CarrierGetter", "value", 0,
                    Array.Empty<TypeNode>(), new TypeNode.Fqn("StoredCarrier"), new TypeNode[] { source },
                    Array.Empty<BirTypeParameter>(), Array.Empty<TypeNode>(), out _, propertyAccessor: "get")
                || index.TrySelectedSourceMethodDeclaration("CarrierGetter", "value", 0,
                    Array.Empty<TypeNode>(), new TypeNode.Fqn("OtherCarrier"), new TypeNode[] { source },
                    Array.Empty<BirTypeParameter>(), Array.Empty<TypeNode>(), out _, propertyAccessor: "get"))
                throw new InvalidOperationException("Owning selected declaration changed the same getter constraint-shape decision");
        }
        {
            var index = Build(Array.Empty<string>());
            var logical = new TypeNode.Tv("type", 0);
            var storage = new TypeNode.Tv("type", 1);
            index._membersByOwner["NativeSlots"] = new List<MemberBinding> {
                new("NativeSlots", "accept", 1, "Accept", true, false,
                    ParamTypeNodes: new TypeNode[] { logical }),
            };
            index._declarationById["selected-value-slot"] = new MemberBinding(
                "StoredSlots", "accept", 1, null, false, false,
                ParamTypeNodes: new TypeNode[] { storage },
                NullableGenericParams: new TypeNode[] { logical });
            var call = new JsonObject {
                ["k"] = "callInstance", ["method"] = "accept",
                [DeclarationIdentityBinding.Key] = "selected-value-slot",
                ["sig"] = new JsonArray(TypeJson.Write(storage)), ["args"] = new JsonArray(new JsonObject()),
            };
            var overrides = JsonNode.Parse("""
            [{"owner":{"t":"fqn","name":"NativeSlots","args":[{"t":"tv","scope":"type","i":0}]},
              "member":"accept","kind":"method","arity":1}]
            """)!.AsArray();
            if (DeclarationRename.ResolveSlot(call, overrides, index) != "Accept")
                throw new InvalidOperationException("Override call selection used the storage slot instead of its selected source descriptor");
            call.Remove(DeclarationIdentityBinding.Key);
            if (DeclarationRename.ResolveSlot(call, overrides, index) != null)
                throw new InvalidOperationException("Override selection equated distinct generic positions without declaration evidence");
        }
        {
            var index = Build(Array.Empty<string>());
            var source = new TypeNode.Tv("method", 0);
            var value = new TypeNode.Fqn("kotlin.String");
            var frame = new NullableRepresentationFrame(1, Array.Empty<int>(),
                physicalOrder: new[] { 1, 0 }, storageIndices: new[] { 0 });
            var binding = new MemberBinding("FrameCalls", "select", 3, null, false, true,
                DeclarationSemanticParams: new TypeNode[] {
                    new TypeNode.Array(new TypeNode.Fn(false, value, new TypeNode[] { source })),
                    new TypeNode.Array(source), new TypeNode.ByRef(source),
                }, NullableFrame: frame);
            TypeNode[] Call(TypeNode functionArgument, TypeNode referent) => new TypeNode[] {
                new TypeNode.Array(new TypeNode.Fn(false, value, new[] { functionArgument })),
                new TypeNode.Array(new TypeNode.Tv("method", 1)), new TypeNode.ByRef(referent),
            };
            if (!index.MaterializedDeclarationDescribesCall(binding,
                    Call(new TypeNode.Tv("method", 0), new TypeNode.Tv("method", 1)))
                || index.MaterializedDeclarationDescribesCall(binding,
                    Call(new TypeNode.Tv("method", 1), new TypeNode.Tv("method", 1)))
                || index.MaterializedDeclarationDescribesCall(binding,
                    Call(new TypeNode.Tv("method", 0), new TypeNode.Tv("method", 0))))
                throw new InvalidOperationException("Selected declaration validation lost exact value/native frame roles");
        }
        var sourceCharSequenceFunction = new TypeNode.Fn(false, new TypeNode.Fqn("kotlin.CharSequence"),
            new TypeNode[] { new TypeNode.Fqn("kotlin.Int") });
        var physicalCharSequenceFunction = new TypeNode.Fn(false, new TypeNode.Fqn(SharedSyntheticSynthesis.CharSeq),
            new TypeNode[] { new TypeNode.Fqn("System.Int32") });
        if (!SourceDeclarationDescribesCall(new TypeNode.Nullable(sourceCharSequenceFunction),
                new TypeNode.Nullable(physicalCharSequenceFunction))
            || SourceDeclarationDescribesCall(sourceCharSequenceFunction,
                new TypeNode.Fn(false, new TypeNode.Fqn("System.String"), physicalCharSequenceFunction.Params))
            || SourceDeclarationDescribesCall(sourceCharSequenceFunction,
                new TypeNode.Fn(false, physicalCharSequenceFunction.Ret, new TypeNode[] { new TypeNode.Fqn("System.String") })))
            throw new InvalidOperationException("CharSequence representation changed exact function signature identity");
        var ownerSlot = new TypeNode.Tv("type", 0);
        var stringSlot = new TypeNode.Fqn("System.String");
        var intSlot = new TypeNode.Fqn("System.Int32");
        if (!ConstructedDeclarationDescribesCall(new[] { ownerSlot }, new TypeNode[] { stringSlot }, new TypeNode[] { stringSlot })
            || ConstructedDeclarationDescribesCall(new[] { ownerSlot }, new TypeNode[] { intSlot }, new TypeNode[] { stringSlot }))
            throw new InvalidOperationException("Constructed declaration validation lost exact owner substitution");
        {
            var carrierIndex = Build(Array.Empty<string>());
            var source = new TypeNode.Tv("method", 0);
            var function = TypeNode.Parse("""{"t":"fn","suspend":true,"params":[],"ret":{"t":"fqn","name":"kotlin.Unit"}}""");
            var binding = new MemberBinding("ColdCarrier", "read", 2, null, false, false,
                MethodArity: 2, ParamTypeNodes: new TypeNode[] { new TypeNode.Fqn("object"), new TypeNode.Fqn("object") },
                KotlinParameterTypes: new TypeNode[] { source, function });
            carrierIndex._declarationById.Add("scalar", binding);
            carrierIndex._declarationById.Add("native", binding with { ParamTypeNodes = new TypeNode[] { source, new TypeNode.Fqn("object") } });
            if (!carrierIndex.GenericScalarParameters("scalar").SequenceEqual(new[] { 0 })
                || carrierIndex.GenericScalarParameters("native").Length != 0)
                throw new InvalidOperationException("Referenced scalar projection lost its exact physical parameter proof");
            JsonObject Call(params string[] ids) => new()
            {
                ["k"] = "callInstance", ["method"] = "read",
                ["sig"] = new JsonArray(TypeJson.Write(source), TypeJson.Write(function)),
                ["overrides"] = new JsonArray(ids.Select(id => (JsonNode)new JsonObject
                    { [DeclarationIdentityBinding.Key] = id }).ToArray()),
            };
            var opaque = Call("scalar");
            var mixed = Call("scalar", "native");
            var root = new JsonObject { ["fileClass"] = "ScalarCaller", ["methods"] = new JsonArray(new JsonObject
            {
                ["name"] = "test", ["params"] = new JsonArray(), ["ret"] = TypeJson.Fqn("void"),
                ["body"] = new JsonArray(new JsonObject { ["k"] = "exprStmt", ["expr"] = opaque },
                    new JsonObject { ["k"] = "exprStmt", ["expr"] = mixed }),
            }) };
            var typedRoot = BirGraphDom.ReadBirFileDeclaration(root);
            KotlinGenericScalarSlots.ApplyAll(new[] { typedRoot }, GenericRepresentationPolicy.CreateSource(
                new[] { typedRoot }, new Dictionary<string, string>(), carrierIndex), carrierIndex);
            var calls = typedRoot.Methods.Value[0].Body.Value.OfType<BirExpressionStatement>().Select(statement => statement.Expr.Value).ToArray();
            opaque = ((BirCallInstance)calls[0]).ToDom();
            mixed = ((BirCallInstance)calls[1]).ToDom();
            if (TypeJson.Read(opaque["sig"][0]) is not TypeNode.Fqn { Name: "object" }
                || TypeJson.Read(opaque["sig"][1]) != function || TypeJson.Read(mixed["sig"][0]) != source)
                throw new InvalidOperationException("Fake override projection guessed a parameter contract not shared by its declarations");
        }
        var nullableSourceVariable = new TypeNode.Nullable(new TypeNode.Tv("type", 0));
        var nullableSourceAny = new TypeNode.Nullable(new TypeNode.Fqn("kotlin.Any"));
        if (SourceDeclarationDescribesCall(nullableSourceVariable, new TypeNode.Fqn("object"))
            || SourceDeclarationDescribesCall(nullableSourceAny, nullableSourceVariable)
            || !SourceDeclarationDescribesCall(nullableSourceAny, new TypeNode.Fqn("System.Object")))
            throw new InvalidOperationException("Source declaration slot selection admitted type-variable erasure");
        {
            var aliasIndex = Build(Array.Empty<string>());
            const string companionSource = "probe.Owner.<companion:Named>";
            const string companionPhysical = "probe.Owner+$Named";
            aliasIndex._companionPhysicalOwnerBySemanticType[companionSource] = companionPhysical;
            aliasIndex._companionSemanticTypeByPhysicalOwner["probe.Owner.$Named"] = companionSource;
            aliasIndex._companionPhysicalOwnerBySemanticType["probe.Other.<companion:Named>"] = "probe.Other+$Named";
            aliasIndex._companionSemanticTypeByPhysicalOwner["probe.Other.$Named"] = "probe.Other.<companion:Named>";
            var semanticCompanion = new TypeNode.Fqn(companionSource);
            foreach (var physical in new[] { companionPhysical, "probe.Owner.$Named" })
                if (!aliasIndex.SemanticDeclarationDescribesCall(semanticCompanion, new TypeNode.Fqn(physical))
                    || !aliasIndex.SemanticDeclarationDescribesCall(new TypeNode.Fqn(physical), semanticCompanion))
                    throw new InvalidOperationException("Companion signature lost its trusted physical identity");
            if (aliasIndex.SemanticDeclarationDescribesCall(semanticCompanion, new TypeNode.Fqn("probe.Owner+$Other"))
                || aliasIndex.SemanticDeclarationDescribesCall(semanticCompanion, new TypeNode.Fqn("probe.Other+$Named"))
                || aliasIndex.SemanticDeclarationDescribesCall(semanticCompanion,
                    new TypeNode.Fqn(companionPhysical, new TypeNode[] { new TypeNode.Fqn("kotlin.Int") })))
                throw new InvalidOperationException("Companion signature admitted an unrecorded carrier or generic arguments");
            const string owner = "probe.Alias";
            aliasIndex._ownerAlias[owner] = "probe.Native";
            aliasIndex._ownerArity[owner] = 3;
            aliasIndex._ownerNullableFrames[owner] = new NullableRepresentationFrame(
                2, new[] { 0 }, new[] { 0, 2, 1 });
            var first = new TypeNode.Tv("method", 2);
            var companion = new TypeNode.Tv("method", 3);
            var second = new TypeNode.Tv("method", 4);
            var sourceOwner = new TypeNode.Fqn(owner, new TypeNode[] { first, companion, second });
            aliasIndex._physicalTypeBySemanticName[owner] = "probe.Alias`3";
            aliasIndex._physicalTypeBySemanticName["probe.Export"] = "probe.Export`1";
            if (aliasIndex.ExactReflectedOwner("probe.Export", 1) != "probe.Export`1"
                || aliasIndex.ExactReflectedOwner(owner, 3) != "probe.Native")
                throw new InvalidOperationException("Exact reflected owner lost recorded identity or CLR alias precedence");
            aliasIndex._ownerNullableFrames["probe.Alias`3"] = aliasIndex._ownerNullableFrames[owner];
            var semanticOwner = new TypeNode.Fqn(owner, new TypeNode[] { first, second });
            var physicalOwner = new TypeNode.Fqn("probe.Alias`3", sourceOwner.Args);
            if (!aliasIndex.SemanticDeclarationDescribesCall(semanticOwner, physicalOwner)
                || aliasIndex.SemanticDeclarationDescribesCall(semanticOwner,
                    new TypeNode.Fqn("probe.Alias`3", new TypeNode[] { second, companion, first }))
                || aliasIndex.SemanticDeclarationDescribesCall(semanticOwner,
                    new TypeNode.Fqn("probe.Other`3", sourceOwner.Args)))
                throw new InvalidOperationException("Declaration signature lost exact owner/frame correspondence");
            if (MemberCallSubstitution.ClrOwnerType(aliasIndex, sourceOwner)
                is not TypeNode.Fqn { Name: "probe.Native", Args: { } projected }
                || !projected.SequenceEqual(new TypeNode[] { first, second }))
                throw new InvalidOperationException("CLR alias lost its explicit source/physical argument correspondence");
            if (!sourceOwner.Args.SequenceEqual(new TypeNode[] { first, companion, second }))
                throw new InvalidOperationException("CLR alias projection mutated the implementation helper frame");
            var lowered = BirTypeLowering.LowerPhysicalType(sourceOwner, aliasIndex.Aliases,
                _ => false, null, typeArg: false, nullableFrames: aliasIndex.NullableTypeFrames);
            if (lowered is not TypeNode.Fqn { Name: "probe.Native", Args: { } loweredArguments }
                || !loweredArguments.SequenceEqual(new TypeNode[] { first, second }))
                throw new InvalidOperationException("CLR type projection disagrees with alias member owner projection");
            var reference = BirTypeLowering.LowerPhysicalType(sourceOwner, aliasIndex.Aliases,
                _ => false, null, typeArg: false, refBuild: true, nullableFrames: aliasIndex.NullableTypeFrames);
            if (reference is not TypeNode.Fqn { Name: owner, Args: { } referenceArguments }
                || !referenceArguments.SequenceEqual(sourceOwner.Args))
                throw new InvalidOperationException("Reference type projection discarded the Kotlin implementation frame");
            var annotationType = new TypeNode.Fqn("probe.Pair", new TypeNode[] {
                sourceOwner, new TypeNode.Nullable(new TypeNode.Fqn("kotlin.String")),
            });
            var annotationFlags = NullableFlags.Compute(annotationType, _ => false,
                annotationArguments: type => BirTypeLowering.AnnotationArguments(type, aliasIndex.Aliases,
                    _ => false, nullableFrames: aliasIndex.NullableTypeFrames));
            if (annotationFlags?.ToJsonString() != "[1,1,1,1,2]")
                throw new InvalidOperationException("CLR alias companions shifted a following nullable annotation: " + annotationFlags);
            var referenceFlags = NullableFlags.Compute(annotationType, _ => false,
                annotationArguments: type => BirTypeLowering.AnnotationArguments(type, aliasIndex.Aliases,
                    _ => false, refBuild: true, nullableFrames: aliasIndex.NullableTypeFrames));
            if (referenceFlags?.ToJsonString() != "[1,1,1,1,1,2]")
                throw new InvalidOperationException("Reference annotations discarded their implementation frame");
            var nullableAlias = new TypeNode.Fqn(owner, new TypeNode[] {
                new TypeNode.Nullable(new TypeNode.Fqn("kotlin.String")), companion, second,
            });
            var nullableAliasFlags = NullableFlags.Compute(nullableAlias, _ => false,
                annotationArguments: type => BirTypeLowering.AnnotationArguments(type, aliasIndex.Aliases,
                    _ => false, nullableFrames: aliasIndex.NullableTypeFrames));
            if (nullableAliasFlags?.ToJsonString() != "[1,2,1]")
                throw new InvalidOperationException("Alias argument projection discarded a retained nullable wrapper");
            aliasIndex._ownerAlias[owner] = "System.IComparable";
            aliasIndex._ownerArity[owner] = 2;
            aliasIndex._ownerNullableFrames[owner] = new NullableRepresentationFrame(1, new[] { 0 });
            var deferredOwner = new TypeNode.Fqn(owner, new TypeNode[] { first, companion });
            if (MemberCallSubstitution.ClrOwnerType(aliasIndex, deferredOwner)
                is not TypeNode.Fqn { Name: owner, Args: { } deferredArguments }
                || !deferredArguments.SequenceEqual(deferredOwner.Args))
                throw new InvalidOperationException("Deferred CLR classifier lost its semantic argument frame");
        }
        var nullableFrame = new NullableRepresentationFrame(1, new[] { 0 });
        var sourceReturn = new TypeNode.Nullable(new TypeNode.Tv("method", 0));
        var framePayload = new JsonObject {
            ["id"] = "frame-test", ["name"] = "sourceMethod",
            ["signature"] = new JsonObject { ["params"] = new JsonArray(), ["ret"] = TypeJson.Write(sourceReturn) },
            ["reified"] = new JsonArray(0), ["nullableWitness"] = new JsonArray(0),
            [NullableRepresentationFrame.MetadataKey] = nullableFrame.ToJson(),
        };
        var parsedFrame = ParseDeclarationIdentityPayload(framePayload, 2);
        if (!SameFrame(parsedFrame.NullableFrame, nullableFrame) || parsedFrame.SemanticReturn != sourceReturn)
            throw new InvalidOperationException("Reference metadata reader lost nullable frame or original signature");
        void MustRejectFrame(Action action)
        {
            try { action(); }
            catch (InvalidDataException) { return; }
            throw new InvalidOperationException("Reference metadata reader accepted inconsistent current frame");
        }
        MustRejectFrame(() => ParseDeclarationIdentityPayload(framePayload, 1));
        var outOfRange = (JsonObject)framePayload.DeepClone();
        outOfRange["reified"] = new JsonArray(1);
        MustRejectFrame(() => ParseDeclarationIdentityPayload(outOfRange, 2));
        var noSignature = (JsonObject)framePayload.DeepClone();
        noSignature.Remove("signature");
        MustRejectFrame(() => ParseDeclarationIdentityPayload(noSignature, 2));
        if (!SameFrame(ReadNullableFrame(nullableFrame.ToJson(), 2), nullableFrame))
            throw new InvalidOperationException("Type frame metadata reader lost explicit correspondence");
        var storageFrame = new NullableRepresentationFrame(1, new[] { 0 }, storageIndices: new[] { 0 },
            nullableStorageIndices: new[] { 0 });
        var storagePayload = (JsonObject)framePayload.DeepClone();
        storagePayload[NullableRepresentationFrame.MetadataKey] = storageFrame.ToJson();
        if (!SameFrame(ParseDeclarationIdentityPayload(storagePayload, 4).NullableFrame, storageFrame)
            || !SameFrame(ReadNullableFrame(storageFrame.ToJson(), 4), storageFrame))
            throw new InvalidOperationException("Reference metadata reader lost storage representation correspondence");
        if (SameFrame(new NullableRepresentationFrame(2, Array.Empty<int>(), storageIndices: new[] { 0 }),
            new NullableRepresentationFrame(2, Array.Empty<int>(), storageIndices: new[] { 1 })))
            throw new InvalidOperationException("Different storage frame demands compared equal");
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

        if (GenericParamSourceDeclaration(typeof(IEnumerable<>).GetGenericArguments()[0]).Variance.Value != "out"
            || GenericParamSourceDeclaration(typeof(IComparer<>).GetGenericArguments()[0]).Variance.Value != "in"
            || GenericParamSourceDeclaration(typeof(List<>).GetGenericArguments()[0]).Variance.IsPresent)
            throw new InvalidOperationException(
                "ReferenceMetadataIndex self-test dropped reflected generic-parameter variance");

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

        Console.WriteLine("[reference declaration types] self-test OK (generic variance + array/function facets)");
    }
}

sealed record ReferenceAssembly(string Path, string Name, string Version, ReferenceDotKtMetadata DotKt);

sealed class ReferenceDotKtMetadata
{
    public readonly Dictionary<string, ValueRepresentationBinding> ValueRepresentations = new(StringComparer.Ordinal);
    public readonly List<string> Diagnostics = new();

    // CALL-SUBSTITUTION metadata (sourced from the ref.dll, consumed by MemberCallSubstitution; NOT serialized).
    // ownerFqn (the Kotlin FQN, e.g. "kotlin.String") -> the BCL alias it binds to ("System.String"), from a
    // class-level @ClrTypeAlias (the type-identity binding) or, for a not-yet-renamed bound class, a class-level @ClrIntrinsic.
    public readonly Dictionary<string, string> Aliases = new(StringComparer.Ordinal);
    public readonly Dictionary<string, string> AliasKinds = new(StringComparer.Ordinal);
    public readonly Dictionary<ReferenceMetadataReader.OwnerTypeIdentity, string> TypeKinds = new();
    public readonly Dictionary<string, string> PhysicalTypeKinds = new(StringComparer.Ordinal);
    public readonly HashSet<ReferenceMetadataReader.OwnerTypeIdentity> ByRefLikeOwners = new();
    public readonly HashSet<string> ByRefLikePhysicalOwners = new(StringComparer.Ordinal);
    public readonly HashSet<string> DotKtOwners = new(StringComparer.Ordinal);             // producer-marked DotKt assembly types
    public readonly Dictionary<string, ReferenceMetadataReader.RichEnumMetadata> RichEnums = new(StringComparer.Ordinal);
    public readonly Dictionary<string, BasicEnumMetadata> BasicEnums = new(StringComparer.Ordinal);
    public readonly Dictionary<string, string> ExistentialPhysicalBySemanticOwner = new(StringComparer.Ordinal);
    public readonly Dictionary<string, List<(string Owner, int[] Slots, string Declaration, string Invoke)>> ManagedReferenceContracts = new(StringComparer.Ordinal);
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
    public readonly Dictionary<string, BirTypeParameter[]> TypeParamDeclarations = new(StringComparer.Ordinal); // ownerFqn -> exact typed descriptor rows
    public readonly Dictionary<string, NullableRepresentationFrame> NullableFrames = new(StringComparer.Ordinal);
    public readonly Dictionary<string, NullableRepresentationFrame> KotlinConstructorOwnerFrames = new(StringComparer.Ordinal);
    public readonly HashSet<ReferenceMetadataReader.OwnerTypeIdentity> PublicParameterlessConstructibleOwners = new();
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
    public readonly Dictionary<ReferenceMetadataReader.OwnerTypeIdentity, ReferenceTypeShape> TypeShapes = new();
    public readonly Dictionary<string, ReferenceTypeShape> PhysicalTypeShapes = new(StringComparer.Ordinal);
    public readonly Dictionary<ReferenceMetadataReader.OwnerTypeIdentity, string> ExactPhysicalTypeByDottedName = new();
    public readonly Dictionary<string, string> PhysicalTypeBySemanticName = new(StringComparer.Ordinal);
    public readonly Dictionary<string, int> InnerCapturedCount = new(StringComparer.Ordinal);
    public readonly Dictionary<string, string> InnerSemanticOwner = new(StringComparer.Ordinal);
    // Writer-owned decoded templates, read-only after scan. Splice readers
    // receive independent graphs through ReferenceExecutablePayloads queries.
    public readonly Dictionary<string, List<BirInlinePayload>> InlinePayloads = new(StringComparer.Ordinal);
    public readonly Dictionary<string, BirInlinePayload> InlinePayloadsByDeclarationId = new(StringComparer.Ordinal);
    // Top-level fun name -> its @ClrIntrinsic fully-qualified static target ("System.Diagnostics.Stopwatch.GetTimestamp").
    // A top-level fun is a static method of a [KotlinFileClass] type; its call site is `callStatic owner=null`.
    public readonly Dictionary<string, string> TopLevelIntrinsics = new(StringComparer.Ordinal);
    public readonly Dictionary<(string Name, ReferenceMetadataReader.SignatureKey Signature), string> TopLevelIntrinsicsBySig = new();
    public readonly HashSet<string> AmbiguousTopLevelIntrinsics = new(StringComparer.Ordinal);
    // Top-level @ClrIntrinsic fun name -> the 0-based parameter positions its bound BCL static takes BY REFERENCE
    // (@ClrRefArgument). The substituted clrStatic wraps these argTypes positions in ByRef nodes (tryParseInt32's `out result`,
    // Interlocked's `ref location`, Math.DivRem's `out remainder`). Absent when the fun has no byref parameter.
    public readonly Dictionary<string, int[]> TopLevelIntrinsicByref = new(StringComparer.Ordinal);
    // Bare-@ClrIntrinsic extension fun, keyed "funName|recvKey" (recvKey = the receiver/first-param type) -> the BCL
    // member name. Receiver-keyed because the bare name collides across receivers (set->set_Item vs set->set_Chars).
    public readonly Dictionary<(string Name, ReferenceMetadataReader.SignatureKey Signature), string> ExtMemberIntrinsics = new();
    // @JvmInline value-class owner FQN -> (its single backing-field getter "get_data", the field's CLR conv token).
    // The class is ERASED to its primitive CLR form, so `get_data()` is the inline unbox: it collapses to the receiver
    // value conv'd to the field's declared type (a `conv`, never a `ldfld data` — the erased primitive has no field).
    public readonly Dictionary<string, (string Getter, string Conv)> InlineBacking = new(StringComparer.Ordinal);
    // NON-intrinsic top-level funs (real Kotlin bodies in a [KotlinFileClass]) -> their (file-class owner FQN, first-
    // param recvKey). Keyed by fun name. Lets an APP build resolve a referenced `callStatic owner=null` to the file-
    // class it actually lives in (getOrElse -> kotlin.collections._CollectionsKt), disambiguated by the call's receiver
    // type when the name is defined across multiple file-classes (CollectionsKt vs ArraysKt vs MapsKt). NOT consulted in
    // a stdlib self-build (the fun is local there; owner=null + FindStatic finds the sibling).
    public readonly Dictionary<string, List<(string Owner, string RecvKey, ReferenceMetadataReader.TypeKey ParamKey)>> TopLevelStatics = new(StringComparer.Ordinal);
    // Collection factory names used by map realignment, not construction lowering.
    // Selected factory identity and parameter packing live in MemberBinding.
    public readonly Dictionary<string, string> CollectionFactories = new(StringComparer.Ordinal);
    // A defaulted parameter's default-value expression as BIR (from @KotlinDefault), for CROSS-MODULE splice of an
    // omitted argument. Keyed "ownerFqn|methodName|paramCount" -> (argPosition -> decoded BIR). The DefaultArgSplice
    // pass reads this to fill trailing omitted args BEFORE the CharSequence bridge + type lowering (so a String default
    // is coerced exactly like an explicit arg). Rides the ref.dll only (param attrs stripped in the rt build).
    public readonly Dictionary<ReferenceMetadataReader.DefaultKey, Dictionary<int, IBirExpression>> KotlinDefaults = new();
    public readonly Dictionary<string, Dictionary<int, IBirExpression>> KotlinDefaultsByDeclarationId = new(StringComparer.Ordinal);
    // Keys of [KotlinDefaults] that TWO declarations of the same owner+name+arity carry with DIFFERENT defaults — the key
    // cannot tell them apart, so the splice must refuse instead of filling whichever was enumerated last. Populated for
    // both METHODS and CONSTRUCTORS (same-arity overloads are common; #235).
    public readonly HashSet<ReferenceMetadataReader.DefaultKey> KotlinDefaultsConflicted = new();
}

// A single ref.dll member's call-substitution shape. Owner is the Kotlin FQN ("kotlin.String"); Intrinsic is the
// @ClrIntrinsic BCL name or null (null + no @ClrProperty + !IsAbstract = a rule-3 hoist candidate). PropertyName (+ the
// READ/WRITE access flags) is set when the member carries @ClrProperty — an EXPLICIT .NET property accessor binding.
// Suspend = the Kotlin `suspend` modifier, read from the DotKt round-trip [KotlinFunction(flags)] attribute
// (Suspend bit = 4) in the LIVE MetadataLoadContext scan. Populated for the Task-based coroutine bundle (bundle 6):
// a cross-module call site must know "is this referenced callee suspend?" (its CLR shape is the Task<T> kickoff).
// NO consumer reads it yet — bundle 6 wires it.
sealed record ReferenceTypeShape(int TypeParamCount, string Kind, TypeNode.Fqn Base, TypeNode.Fqn[] Interfaces,
    JsonObject SourceFacts = null);

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
sealed record MethodSlotIdentity(string PhysicalMember, BirTypeParameter[] TypeParams, bool ReturnsValue,
    TypeNode[] SignatureParameters);

// `ReturnType` is the best-effort STATIC-RESULT projection (TypeNodeOf): it drops a generic parameter, because its
// consumers want a usable concrete identity or nothing. `ReturnTypeNode` is the DECLARATION projection
// (DeclarationTypeNode), the same one `ParamTypeNodes` uses, which keeps generic parameters as `Tv` — a declaration
// the caller substitutes. The two are not interchangeable: `Iterable<E>.iterator()` is `Iterator` in the first and
// `Iterator<!0>` in the second, and only the second says what the call site's type argument completes.
sealed record MemberBinding(string Owner, string Name, int ParamCount, string Intrinsic, bool IsAbstract, bool IsStatic, int PropertyAccess = 0, string PropertyName = null, int[] ByrefPositions = null, bool Suspend = false, bool Conv = false, TypeNode ConvTo = null, TypeNode ReturnType = null, int MethodArity = 0, TypeNode[] ParamTypeNodes = null, bool IsVirtual = false, TypeNode KotlinReturnType = null, TypeNode SuspendReturnType = null, TypeNode NullableGenericRet = null, TypeNode[] NullableGenericParams = null, TypeNode ReturnTypeNode = null, int MetadataToken = 0, string SourcePropertyName = null, string AccessorKind = null, string AssociatedPropertyName = null, bool IsPropertyBridge = false, bool IsPublic = false, string PropertyAssociation = null, string SourcePropertyAssociation = null, string SourceMethodName = null, BirTypeParameter[] MethodTypeParams = null, string DeclarationId = null, string DeclarationSourceName = null, string DeclarationPhysicalOwner = null, TypeNode[] DeclarationSemanticParams = null, TypeNode DeclarationSemanticReturn = null, string CollectionFactoryKind = null, string ArrayFactoryKind = null, string ArrayFactoryElementHint = null, int CountStart = -1, int CountEnd = -1, int[] SemanticReifiedTypeParameterIndices = null, int[] NullableWitnessTypeParameterIndices = null, TypeNode[] KotlinParameterTypes = null, string InnerConstructorOwner = null, string InnerConstructorDeclaration = null, int[] InnerConstructorTypeArguments = null, BirTypeParameter[] SemanticMethodTypeParams = null, NullableRepresentationFrame NullableFrame = null)
{
    // Capture the selected reference MethodDef while scanning it. A local type may shadow its owner
    // during stdlib self-compilation; resolving that owner again is not declaration identity.
    public TypeNode[] SignatureParameters { get; init; }
    public string SourceMethodDeclaration { get; init; }
    public int[] ReflectedWitnessIndices { get; init; }
    public string ValueAdapter { get; init; }
    public string ValueSlot { get; init; }
    public bool SignatureAllocated { get; init; }
    public LateinitStateTarget LateinitState { get; init; }
    public TypeNode SignatureReturn { get; init; }
    public string PhysicalVisibility { get; init; }
    public byte[][] ParameterNullableFlags { get; init; }
    public int[] VarargPositions { get; init; } = Array.Empty<int>();
}

sealed record ReferencedMethodDeclaration(string PhysicalMember, TypeNode[] Parameters, TypeNode Return,
    BirTypeParameter[] TypeParams, bool ReturnsValue, bool IsVirtual, TypeNode[] PhysicalParameters,
    TypeNode PhysicalReturn, NullableRepresentationFrame NullableFrame, TypeNode[] SignatureParameters,
    string DeclarationId)
{
    public byte[][] ParameterNullableFlags { get; init; }
    public Func<int, TypeNode> QueryParameterBound { get; init; }
}

sealed record ReferencedUnsafeAccessorMethod(string PhysicalMember, TypeNode[] Parameters, TypeNode Return,
    BirTypeParameter[] TypeParams, TypeNode NullableGenericReturn, bool IsPublic);

sealed record ReferencedDeclarationSlot(string Owner, string Name, int Arity, TypeNode[] Parameters,
    TypeNode Return, bool IsInterface, bool SignatureAllocated);

sealed record MethodImplBinding(string BodyOwner, int BodyToken, TypeNode.Fqn DeclarationOwner,
    string DeclarationMember);

sealed record ReferencedPropertyMethodImpl(string SourceMember, TypeNode.Fqn DeclarationOwner, string DeclarationMember,
    TypeNode[] Parameters, TypeNode Return, int MethodArity, BirTypeParameter[] TypeParams);

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

sealed record ReferencedAliasConstructorAdapter(string Owner, BirConstructorAdapterPayload Adapter);
