// #370: the FIXED BCL members ilemit expands a Kotlin operation into.
//
// `enumValues()` becomes `Enum.GetValues`, string `+` becomes `String.Concat`, a runtime-classifier read becomes
// `Object.GetType`. The source wrote none of them —
// but "did the source write it" is not the question. The question is whether ilemit encodes an EXTERNAL member as a
// CIL operand, and it does, in every one of these.
//
// None of them varies: no type arguments, no overload chosen per call site, the same member every time. So they do
// not need a carrier per node — one table per document, keyed by role, resolved here like everything else. The
// expansion stays in the emitter, which is a question about which layer owns the SHAPE; the member it emits arrives
// named, which is the question this issue is about. The two are separable, and separating them is what lets this
// land without waiting on the intrinsic-binding programme.

using System.Reflection;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Toolchain;

static partial class ClrMemberResolution
{
    // role -> (owner, member, parameter types). A role names what the emitter needs it FOR, so a reader of the
    // emitter can find the entry without knowing the BCL signature by heart.
    static readonly (string Role, string Owner, string Name, string[] Params)[] WellKnownMembers =
    {
        ("String.ConcatArray",    "System.String",   "Concat",            new[] { "System.Object[]" }),
        ("Type.FromHandle",       "System.Type",     "GetTypeFromHandle", new[] { "System.RuntimeTypeHandle" }),
        ("Object.GetType",        "System.Object",   "GetType",           new string[0]),
        ("Object.ToString",       "System.Object",   "ToString",          new string[0]),
        ("Object.GetHashCode",    "System.Object",   "GetHashCode",       new string[0]),
        ("Object.Equals",         "System.Object",   "Equals",            new[] { "System.Object" }),
        ("Enum.GetValues",        "System.Enum",     "GetValues",         new[] { "System.Type" }),
        ("Enum.Parse",            "System.Enum",     "Parse",             new[] { "System.Type", "System.String" }),
    };

    // Constructors with a FIXED owner. `newobj` needs a token exactly as `call` does, so these are the same
    // question — the split that matters is whether the owner varies per site, not whether the member has a name.
    static readonly (string Role, string Owner, string[] Params)[] WellKnownCtors =
    {
        ("Object.ctor",                  "System.Object",                  new string[0]),
        ("IndexOutOfRangeException.ctor","System.IndexOutOfRangeException", new[] { "System.String" }),
        // The OPEN `Nullable<T>..ctor(T)`. A coercion computes the constructed owner from the slot it is filling,
        // which no document states — but the declaration does not vary, and anchoring is mechanical.
        ("NullableT.ctor",               "System.Nullable`1",              new[] { "!0" }),
        ("SpanT.ctorPointer",            "System.Span`1",                  new[] { "System.Void*", "System.Int32" }),
    };

    /// <summary>Stamp the fixed-member table on a document root. Every entry resolves or the build stops.</summary>
    public static void ResolveWellKnown(BirFileDeclaration root, ReferenceMetadataIndex refs)
    {
        refs = refs ?? throw new ArgumentNullException(nameof(refs));
        var table = new Dictionary<string, MemberRefNode>(StringComparer.Ordinal);
        foreach (var (role, owner, name, parameters) in WellKnownMembers)
        {
            var open = ResolveOwnerType(new TypeNode.Fqn(owner), refs: refs)
                ?? throw new InvalidOperationException(
                    $"bir2cir: the fixed-member table needs '{owner}' for role '{role}', which does not resolve "
                    + "to a .NET type");
            var wanted = parameters.Select(ParseWellKnownParam).ToList();
            var cands = open.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
                .Where(m => m.Name == name && m.GetParameters().Length == wanted.Count
                    && !m.IsGenericMethodDefinition).ToList();
            // A role that cannot be resolved is a producer defect. Skipping it quietly hands the emitter a
            // table with a hole in it, and the hole only shows up as a missing member at emit time — one layer
            // past the one that could say what went wrong.
            var win = ClrDeclarationMatching.TryPickUnique(refs, cands, wanted, Array.Empty<TypeNode>())
                ?? throw new InvalidOperationException(
                    $"bir2cir: '{owner}.{name}({string.Join(", ", parameters)})' does not resolve to one "
                    + $"declaration for role '{role}'");
            table[role] = ClrMemberReferences.MemberRefOf(refs, _localTypes, win, MemberRefNode.Kinds.Method, open, OwnParameters(open));
        }
        foreach (var (role, owner, parameters) in WellKnownCtors)
        {
            // A name may state its arity — `System.Nullable`1` is a different type from the static `System.Nullable`
            // beside it, and the bare name resolves to the wrong one.
            var open = OwnerOfSpec(refs, owner)
                ?? throw new InvalidOperationException(
                    $"bir2cir: the fixed-member table needs '{owner}' for role '{role}'");
            var wanted = parameters.Select(ParseWellKnownParam).ToList();
            var ownerArgs = OwnParameters(open);
            var cands = open.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .Where(c => !c.IsStatic && c.GetParameters().Length == wanted.Count).ToList();
            var win = ClrDeclarationMatching.TryPickUniqueCtor(refs, cands, wanted, Array.Empty<TypeNode>())
                ?? throw new InvalidOperationException(
                    $"bir2cir: '{owner}..ctor({string.Join(", ", parameters)})' does not resolve to one "
                    + $"declaration for role '{role}'");
            table[role] = ClrMemberReferences.MemberRefOf(refs, _localTypes, win, MemberRefNode.Kinds.Ctor, open, ownerArgs);
        }
        root.WellKnownRefs = PhysicalMemberTable.Create(table);
    }

    // An OPEN generic declaration is named over its own parameters — `IEnumerator`1<!0>` — because that is what
    // the declaration IS. The emitter anchors it onto the owner it is building; the reference states the
    // declaration, exactly as it does everywhere else.
    static TypeNode[] OwnParameters(Type open) =>
        open.IsGenericTypeDefinition
            ? open.GetGenericArguments().Select(p => (TypeNode)new TypeNode.Tv("type", p.GenericParameterPosition)).ToArray()
            : Array.Empty<TypeNode>();

    // `Name`N` -> the arity-N definition; a bare name -> whatever that name alone resolves to.
    static Type OwnerOfSpec(ReferenceMetadataIndex bindingReferences, string spec)
    {
        var tick = spec.IndexOf('`');
        return tick < 0
            ? ResolveOwnerType(new TypeNode.Fqn(spec), refs: bindingReferences)
            : ClrDeclarationMatching.RefDef(bindingReferences, spec[..tick], int.Parse(spec[(tick + 1)..]));
    }

    static TypeNode ParseWellKnownParam(string spec) =>
        spec.StartsWith("!", StringComparison.Ordinal) ? new TypeNode.Tv("type", int.Parse(spec[1..])) :
        spec.EndsWith("*", StringComparison.Ordinal)
            ? new TypeNode.Ptr(ParseWellKnownParam(spec[..^1])) :
        spec.EndsWith("[]", StringComparison.Ordinal)
            ? new TypeNode.Array(new TypeNode.Fqn(spec[..^2]))
            : new TypeNode.Fqn(spec);
}

static partial class ClrMemberResolution
{
    internal static bool TryResolveAliasedInterfaceSlot(ReferenceMetadataIndex refs,
        TypeNode.Fqn physicalOwner, string member, int methodArity, TypeNode[] wantedParams,
        BirTypeParameter[] wantedTypeParams, TypeNode[] wantedOwnerArgs,
        out TypeNode.Fqn declarationOwner, out string declarationMember,
        out TypeNode[] declarationParams, out TypeNode declarationReturn,
        TypeNode[] selectionOwnerArgs = null, TypeNode wantedReturn = null)
        => TryResolveAliasedInterfaceSlotByShape(refs, physicalOwner, member, methodArity, wantedParams,
            wantedTypeParams == null ? null : KotlinOverrideSlotBridge.MethodTypeParameterShapeKey(
                wantedTypeParams, wantedOwnerArgs ?? Array.Empty<TypeNode>()),
            out declarationOwner, out declarationMember, out declarationParams, out declarationReturn,
            selectionOwnerArgs, wantedReturn);

    internal static bool TryResolveAliasedInterfaceSlotByShape(ReferenceMetadataIndex refs,
        TypeNode.Fqn physicalOwner, string member, int methodArity, TypeNode[] wantedParams,
        string wantedShape,
        out TypeNode.Fqn declarationOwner, out string declarationMember,
        out TypeNode[] declarationParams, out TypeNode declarationReturn,
        TypeNode[] selectionOwnerArgs = null, TypeNode wantedReturn = null)
    {
        declarationOwner = null;
        declarationMember = null;
        declarationParams = null;
        declarationReturn = null;
        refs = refs ?? throw new ArgumentNullException(nameof(refs));
        var open = ResolveOwnerType(physicalOwner, refs: refs) ?? refs.PhysicalTypeNamed(physicalOwner.Name);
        if (open == null || !open.IsInterface) return false;
        var ownerArgs = physicalOwner.Args ?? Array.Empty<TypeNode>();
        var declarationOwners = new[] { open }.Concat(open.GetInterfaces())
            .Select(owner => owner.IsGenericType && !owner.IsGenericTypeDefinition
                ? owner.GetGenericTypeDefinition() : owner)
            .GroupBy(owner => (owner.Module, owner.MetadataToken)).Select(group => group.First());
        var candidates = new List<(MethodInfo Method, MemberRefNode Reference,
            TypeNode[] Parameters, TypeNode Return, TypeNode[] DeclaringArgs)>();
        foreach (var method in declarationOwners.SelectMany(owner => owner.GetMethods(
                     BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)))
        {
            if (!IsPublicOrProtected(method) || method.Name != member
                || method.GetGenericArguments().Length != methodArity
                || method.GetParameters().Length != wantedParams.Length) continue;
            var reference = MemberRefOf(refs, method, MemberRefNode.Kinds.Method, open, ownerArgs);
            var declaringArgs = (reference.DeclaringType as TypeNode.Fqn)?.Args ?? Array.Empty<TypeNode>();
            var parameters = reference.ParameterTypes
                .Select(parameter => SupertypeGraph.SubstOwnerTvs(parameter, declaringArgs)).ToArray();
            var returnType = SupertypeGraph.SubstOwnerTvs(reference.ReturnType, declaringArgs);
            if (wantedReturn != null && !SameInterfaceSlotType(returnType, wantedReturn)) continue;
            // Select the declaration in the binding's ordinary frame, then instantiate that SAME
            // MethodDef in the physical storage frame. Comparing the latter to Kotlin scalar
            // parameters would lose Add(E) when the interface is ICollection<S(E)>.
            var selectionArgs = declaringArgs;
            if (selectionOwnerArgs != null)
            {
                var selectionOwner = ClrMemberReferences.DeclaringTypeRef(refs, _localTypes, method, open, selectionOwnerArgs) as TypeNode.Fqn;
                selectionArgs = selectionOwner?.Args ?? Array.Empty<TypeNode>();
            }
            var selectionParameters = reference.ParameterTypes
                .Select(parameter => SupertypeGraph.SubstOwnerTvs(parameter, selectionArgs)).ToArray();
            if (!selectionParameters.SequenceEqual(wantedParams, InterfaceSlotTypeComparer.Instance)) continue;
            if (wantedShape != null)
            {
                var declaredTypeParams = method.GetGenericArguments()
                    .Select(ReferenceMetadataReader.GenericParamSourceDeclaration).ToArray();
                if (KotlinOverrideSlotBridge.MethodTypeParameterShapeKey(declaredTypeParams, selectionArgs)
                    != wantedShape)
                    continue;
            }
            candidates.Add((method, reference, parameters, returnType, declaringArgs));
        }
        var distinct = ClrDeclarationMatching.MostDerived(candidates
            .GroupBy(candidate => (candidate.Method.Module, candidate.Method.MetadataToken))
            .Select(group => group.First().Method).ToList());
        if (distinct.Count > 1)
            throw new InvalidOperationException(
                $"bir2cir: aliased interface slot '{physicalOwner.Name}.{member}`{methodArity}' resolves to "
                + $"{distinct.Count} declarations with the requested parameter vector");
        if (distinct.Count == 0) return false;
        var selectedMethod = distinct[0];
        var selected = candidates.Single(candidate => candidate.Method.Module == selectedMethod.Module
            && candidate.Method.MetadataToken == selectedMethod.MetadataToken);
        declarationOwner = selected.Reference.DeclaringType as TypeNode.Fqn;
        if (declarationOwner == null) return false;
        declarationMember = selected.Reference.Name;
        declarationParams = selected.Parameters;
        declarationReturn = selected.Return;
        return true;
    }


    // These are current header views, not source-eligibility registrations.
    // Both owning binding clocks use this same selection plan.
    sealed record InheritedLocalMethod(string Name, bool Static, int Arity, TypeNode[] Parameters);
    sealed record InheritedExternalCallPlan(MethodInfo Declaration, MemberRefNode Reference, TypeNode[] OwnerArgs);


    static InheritedExternalCallPlan PlanInheritedExternalCall(TypeNode.Fqn owner, string name, int methodArity,
        TypeNode[] callSig, IReadOnlyDictionary<string, SupertypeGraph.Def> defs,
        IReadOnlyDictionary<string, InheritedLocalMethod[]> methods, ReferenceMetadataIndex refs)
    {
        if (!defs.TryGetValue(owner.Name, out var ownerDef)) return null;
        var ownerArgs = owner.Args ?? (ownerDef.Arity == 0
            ? Array.Empty<TypeNode>()
            : Enumerable.Range(0, ownerDef.Arity).Select(i => (TypeNode)new TypeNode.Tv("type", i)).ToArray());
        // Reachable() starts in a definition's own type-parameter frame.  Calls, however, can name a constructed
        // local owner (`Local<string>`), so project its first edges into the call's frame before walking onward.
        var projectedOwner = new SupertypeGraph.Def
        {
            Name = ownerDef.Name,
            Kind = ownerDef.Kind,
            Arity = ownerDef.Arity,
            Base = ownerDef.Base == null ? null
                : SupertypeGraph.SubstOwnerTvs(ownerDef.Base, ownerArgs) as TypeNode.Fqn,
            Interfaces = ownerDef.Interfaces.Select(iface =>
                (TypeNode.Fqn)SupertypeGraph.SubstOwnerTvs(iface, ownerArgs)).ToArray(),
        };
        var reachable = SupertypeGraph.Reachable(projectedOwner, defs, refs).ToList();

        // A declaration in this compilation owns the identity.  Never let a reflected base with the same shape
        // displace it merely because the call's local owner does not declare the method itself.
        if (LocalDeclares(ownerDef, methods[owner.Name], ownerArgs, name, methodArity, callSig)
            || reachable.Any(entry => defs.TryGetValue(entry.spec.Name, out var local)
                && LocalDeclares(local, methods[entry.spec.Name], entry.spec.Args, name, methodArity, callSig)))
            return null;

        var answers = new List<InheritedExternalCallPlan>();
        foreach (var (spec, _) in reachable)
        {
            if (defs.ContainsKey(spec.Name)) continue;
            // Resolve against this exact constructed graph edge.  The reference index's ordinary call lookup is
            // receiver-oriented and may legitimately answer from an implemented interface; here the graph has
            // already identified each declaration owner, so declared-only reflection avoids counting it twice.
            var open = ResolveOwnerType(spec, refs: refs) ?? refs.PhysicalTypeNamed(spec.Name);
            if (open == null) continue;
            var candidates = open.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
                    | BindingFlags.DeclaredOnly)
                .Where(method => method.Name == name
                    && method.GetGenericArguments().Length == methodArity
                    && method.GetParameters().Length == callSig.Length
                    && IsPublicOrProtected(method))
                .ToList();
            var declaration = ClrDeclarationMatching.TryPickUnique(refs, candidates, callSig.ToList(), spec.Args ?? Array.Empty<TypeNode>());
            if (declaration == null) continue;
            var reference = ClrMemberReferences.MemberRefOf(refs, _localTypes, declaration, MemberRefNode.Kinds.Method, open,
                spec.Args ?? Array.Empty<TypeNode>());
            answers.Add(new(declaration, reference, spec.Args ?? Array.Empty<TypeNode>()));
        }
        // A derived interface can redeclare a base slot with the same source signature.  That is not ambiguity:
        // ordinary member lookup selects the most-derived declaration.  Apply the same existing metadata rule here,
        // before comparing the complete scalar references; unrelated declarations remain an unresolved ambiguity.
        var mostDerived = ClrDeclarationMatching.MostDerived(answers.Select(answer => answer.Declaration)
            .GroupBy(method => (method.Module, method.MetadataToken)).Select(group => group.First()).ToList());
        if (mostDerived.Count == 1)
        {
            var selected = mostDerived[0];
            answers = answers.Where(answer => answer.Declaration.Module == selected.Module
                && answer.Declaration.MetadataToken == selected.MetadataToken).ToList();
        }
        var distinctAnswers = answers.Select(answer => answer.Reference).Distinct().ToList();
        // Multiple distinct external declarations are a semantic dispatch question, not an emitter choice.  The
        // upstream declaration identity must distinguish that case; leave it unstamped so the fail-closed audit
        // reports the unresolved site instead of picking by graph order.
        return distinctAnswers.Count == 1 ? answers[0] : null;
    }


    internal static void InheritedGenericResultSelfTest()
    {
        var ordinary = new TypeNode.Tv("method", 0);
        var storage = new TypeNode.Tv("method", 2);
        var owner = new TypeNode.Fqn("System.Collections.Generic.List`1", new TypeNode[] { storage });
        var declared = new TypeNode.Tv("type", 0);
        if (PlanInheritedGenericResult(ordinary, owner, declared, Array.Empty<TypeNode>()) != storage)
            throw new InvalidOperationException("Inherited generic result did not preserve its caller/CLR frames");
        if (PlanInheritedGenericResult(storage, owner, declared, Array.Empty<TypeNode>()) != null)
            throw new InvalidOperationException("Inherited generic result duplicated an unchanged conversion");
        // Actual-call/receiver identity and its registered continuation are
        // checked with the real selected List<T> declaration by the source hook.
        Console.WriteLine("[inherited generic result] self-test OK (shared scalar plan, closed CLR return, caller frame)");
    }

    static TypeNode PlanInheritedGenericResult(TypeNode logical, TypeNode declaringType, TypeNode declaredReturn,
        TypeNode[] methodArguments)
    {
        if (logical is not TypeNode.Tv || declaringType is not TypeNode.Fqn declaringOwner || declaredReturn == null) return null;
        var physical = FBoundStarProjectionErasure.SubstituteDeclarationTypeArguments(declaredReturn,
            declaringOwner.Args ?? Array.Empty<TypeNode>(), methodArguments);
        return physical is TypeNode.Tv && physical != logical ? physical : null;
    }

    static bool LocalDeclares(SupertypeGraph.Def def, IReadOnlyList<InheritedLocalMethod> methods,
        TypeNode[] ownerArgs, string name, int methodArity,
        IReadOnlyList<TypeNode> callSig)
    {
        var args = ownerArgs ?? (def.Arity == 0
            ? Array.Empty<TypeNode>()
            : Enumerable.Range(0, def.Arity).Select(i => (TypeNode)new TypeNode.Tv("type", i)).ToArray());
        foreach (var method in methods)
        {
            if (method.Static || method.Name != name || method.Arity != methodArity
                || method.Parameters == null || method.Parameters.Length != callSig.Count) continue;
            var declared = method.Parameters;
            if (declared.Any(type => type == null)) continue;
            var wanted = callSig.Select(SupertypeGraph.TypeKey);
            if (declared.Select(SupertypeGraph.TypeKey).SequenceEqual(wanted)
                || declared.Select(type => SupertypeGraph.SubstOwnerTvs(type, args))
                    .Select(SupertypeGraph.TypeKey).SequenceEqual(callSig.Select(SupertypeGraph.TypeKey)))
                return true;
        }
        return false;
    }

    internal static bool SameInterfaceSlotType(TypeNode metadata, TypeNode descriptor) =>
        CanonicalInterfaceSlotType(metadata).Equals(CanonicalInterfaceSlotType(descriptor));

    static TypeNode CanonicalInterfaceSlotType(TypeNode type) =>
        CanonicalInterfaceSlotTypeCore(BirTypeLowering.CanonicalPhysicalSlotType(type));

    static TypeNode CanonicalInterfaceSlotTypeCore(TypeNode type) => type switch
    {
        // A constructed TypeNode already carries arity in Args. Reflection preserves the metadata backtick suffix;
        // CIR-authored descriptors use the semantic bare owner. They are one CLI TypeSpec, not two identities.
        TypeNode.Fqn { Args: { } args } f => new TypeNode.Fqn(
            ReferenceMetadataIndex.BareOwnerFqn(f.Name),
            args.Select(CanonicalInterfaceSlotTypeCore).ToArray()),
        TypeNode.Array array => new TypeNode.Array(
            CanonicalInterfaceSlotTypeCore(array.Elem), array.Rank, array.SzArray),
        // A lowered nullable value slot and metadata's Nullable`1<V> denote the same CLI TypeSpec.
        TypeNode.Nullable nullable => new TypeNode.Fqn("System.Nullable",
            new[] { CanonicalInterfaceSlotTypeCore(nullable.Of) }),
        TypeNode.Oblivious oblivious => new TypeNode.Oblivious(CanonicalInterfaceSlotTypeCore(oblivious.Of)),
        TypeNode.ByRef byRef => new TypeNode.ByRef(CanonicalInterfaceSlotTypeCore(byRef.Of)),
        TypeNode.Ptr pointer => new TypeNode.Ptr(CanonicalInterfaceSlotTypeCore(pointer.Of)),
        TypeNode.Mod modifier => new TypeNode.Mod(modifier.Req,
            CanonicalInterfaceSlotTypeCore(modifier.M), CanonicalInterfaceSlotTypeCore(modifier.Of)),
        TypeNode.Fn { Clr: not null } function =>
            CanonicalInterfaceSlotTypeCore(BirTypeLowering.DelegateFqnOf(function)),
        TypeNode.Fn function => new TypeNode.Fn(function.Suspend,
            CanonicalInterfaceSlotTypeCore(function.Ret),
            function.Params.Select(CanonicalInterfaceSlotTypeCore).ToArray(),
            function.Recv == null ? null : CanonicalInterfaceSlotTypeCore(function.Recv),
            function.Clr, function.Ctx?.Select(CanonicalInterfaceSlotTypeCore).ToArray()),
        _ => type,
    };

    sealed class InterfaceSlotTypeComparer : IEqualityComparer<TypeNode>
    {
        public static readonly InterfaceSlotTypeComparer Instance = new();
        public bool Equals(TypeNode left, TypeNode right) =>
            left != null && right != null && SameInterfaceSlotType(left, right);
        public int GetHashCode(TypeNode type) =>
            CanonicalInterfaceSlotType(type).GetHashCode();
    }
}
