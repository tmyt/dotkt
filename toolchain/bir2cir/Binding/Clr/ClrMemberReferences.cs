// Binding selects the declaration; this unit states its exact physical identity.
// Preserve metadata FullName, nesting, pointer/array shape and custom modifiers,
// rather than the lossy Kotlin-facing descriptor used during selection.
// Name the declarer, not the receiver, and project receiver arguments along the
// declaration edge here so ilemit only links the stated reference.

using System.Reflection;
using DotKt.Bir;
using DotKt.Toolchain;

// Selected declarations and explicit query/type facts produce exact physical references; no graph writes.
static class ClrMemberReferences
{
    // A member reflected off a CONSTRUCTED generic type reports its signature ALREADY SUBSTITUTED —
    // `ITransformer<string>.Transform` says `string Transform(string)` where the declaration says
    // `T Transform(T)`. A reference carries the OPEN declared signature (ECMA II.9.8: instantiation rides on
    // the type spec, not in the member's own blob), so the declaration is what must be serialized. Reflection
    // has no direct route from one to the other; the metadata token is the identity that survives the
    // substitution, so the definition's member with the same token IS the same declaration.
    internal static T OpenDeclarationOf<T>(T member) where T : MethodBase
    {
        var declaring = member.DeclaringType;
        if (declaring == null || !declaring.IsGenericType || declaring.IsGenericTypeDefinition) return member;
        Type definition;
        try { definition = declaring.GetGenericTypeDefinition(); } catch { return member; }
        if (definition == null || definition == declaring) return member;
        try
        {
            const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic
                | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
            IEnumerable<MethodBase> candidates = member is ConstructorInfo
                ? definition.GetConstructors(all)
                : definition.GetMethods(all);
            foreach (var candidate in candidates)
                if (candidate.MetadataToken == member.MetadataToken && candidate.Module == member.Module)
                    return (T)candidate;
        }
        catch { }
        return member;
    }

    // The field flavour of OpenDeclarationOf: a field read off a constructed generic type reports the
    // substituted field type, and the declaration is what a reference states.
    internal static FieldInfo OpenFieldDeclarationOf(FieldInfo field)
    {
        var declaring = field.DeclaringType;
        if (declaring == null || !declaring.IsGenericType || declaring.IsGenericTypeDefinition) return field;
        Type definition;
        try { definition = declaring.GetGenericTypeDefinition(); } catch { return field; }
        if (definition == null || definition == declaring) return field;
        try
        {
            const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic
                | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
            foreach (var candidate in definition.GetFields(all))
                if (candidate.MetadataToken == field.MetadataToken && candidate.Module == field.Module)
                    return candidate;
        }
        catch { }
        return field;
    }

    internal static MemberRefNode MemberRefOf(ReferenceMetadataIndex references, IReadOnlySet<string> localTypes, MethodBase member, string kind, Type openOwner, TypeNode[] ownerArgs,
        bool? ownerArgumentsAreMethodSlots = null)
    {
        member = OpenDeclarationOf(member);
        // The SHIPPED declaration when the member's assembly has a separate one; otherwise the member itself.
        // Both the name and signature belong to that MethodDef. The declaring head, its instantiation and
        // the physical assembly are already decided from the resolved member.
        var shipped = references.PhysicalTwinOf(member, DeclaringDefOf(references, member)) ?? member;
        var ctor = member as ConstructorInfo;
        var method = shipped as MethodInfo ?? member as MethodInfo;
        if (ctor == null && member as MethodInfo == null)
            throw new InvalidOperationException($"bir2cir: cannot reference '{member}' — neither a method nor a constructor");
        var node = new MemberRefNode(
            Kind: kind,
            Assembly: PhysicalAssemblyOf(references, member),
            DeclaringType: DeclaringTypeRef(references, localTypes, member, openOwner, ownerArgs, ownerArgumentsAreMethodSlots),
            Name: ctor != null ? MemberRefNode.CtorName : shipped.Name,
            GenericArity: member.IsGenericMethod ? member.GetGenericArguments().Length : 0,
            ReturnType: ctor != null
                ? MemberRefNode.Void
                : RefReturnOf(references, localTypes, method),
            CallingConvention: ConventionOf(member),
            ParameterTypes: RefParamsOf(references, localTypes, shipped));
        ValidateDeclaringArity(references, member, node.DeclaringType);
        node.Validate();
        if (node.ParameterTypes.Any(t => SignatureValueTypes.Of(t) is TypeNode.ByRef))
            references.RecordOutputOnlyArguments(node, shipped);
        return node;
    }

    // The caller already has emitted owner arguments. Map the open declaration edge
    // before substitution so these arguments never re-enter runtime alias lowering.
    internal static MemberRefNode MethodOnPhysicalOwner(ReferenceMetadataIndex refs, IReadOnlySet<string> localTypes,
        MethodInfo method, Type openOwner, TypeNode[] arguments)
    {
        var frame = Enumerable.Range(0, arguments.Length).Select(i => (TypeNode)new TypeNode.Tv("type", i)).ToArray();
        var reference = MemberRefOf(refs, localTypes, method, MemberRefNode.Kinds.Method, openOwner, frame);
        return arguments.Length == 0 ? reference : reference with {
            DeclaringType = SupertypeGraph.SubstOwnerTvs(reference.DeclaringType, arguments),
        };
    }

    // A metadata build links the MethodDef in the compile image itself, not its shipped twin.
    // Its signature stays open; only the declaring edge is instantiated in the caller's frame.
    internal static MemberRefNode MetadataDeclaration(ReferenceMetadataIndex refs, MethodBase selected, TypeNode[] arguments)
    {
        var edge = selected.DeclaringType;
        var member = OpenDeclarationOf(selected);
        var signature = refs.MetadataMethodSignature(member);
        var ownerArgs = edge.GetGenericArguments().Select(ReferenceMetadataReader.MetadataEdgeArgument)
            .Select(arg => SupertypeGraph.SubstOwnerTvs(arg, arguments)).ToArray();
        var ctor = member is ConstructorInfo;
        var reference = new MemberRefNode(ctor ? MemberRefNode.Kinds.Ctor : MemberRefNode.Kinds.Method,
            member.Module.Assembly.GetName().Name, new TypeNode.Fqn(member.DeclaringType.FullName,
                ownerArgs.Length == 0 ? null : ownerArgs), ctor ? MemberRefNode.CtorName : member.Name,
            member.IsGenericMethod ? member.GetGenericArguments().Length : 0,
            ctor || signature.Return is TypeNode.Fqn { Name: "System.Void" } ? MemberRefNode.Void : signature.Return,
            ConventionOf(member), signature.Parameters);
        reference.Validate();
        return reference;
    }

    internal static MemberRefNode FieldRefOf(ReferenceMetadataIndex references, IReadOnlySet<string> localTypes, FieldInfo field, Type openOwner, TypeNode[] ownerArgs)
    {
        field = OpenFieldDeclarationOf(field);
        var node = new MemberRefNode(
            Kind: MemberRefNode.Kinds.Field,
            Assembly: PhysicalAssemblyOf(references, field),
            DeclaringType: DeclaringTypeRef(references, localTypes, field, openOwner, ownerArgs),
            Name: field.Name,
            GenericArity: 0,
            // A field's "return" is its declared type — the same crossing a parameter of that type would be.
            ReturnType: ShippedFieldType(references, localTypes, field));
        ValidateDeclaringArity(references, field, node.DeclaringType);
        node.Validate();
        return node;
    }

    // A nested metadata name does not encode its constructed arity. C# may carry enclosing parameters into a
    // nested declaration (`Outer`1+Inner`1` has two arguments), while a static Kotlin nested declaration may
    // introduce an independent set (`MutableMap`2+MutableEntry`2` has only two). The producer has the resolved
    // TypeDef, so validate against that fact here instead of asking the wire validator to infer it from a name.
    static void ValidateDeclaringArity(ReferenceMetadataIndex references, MemberInfo member, TypeNode declaringType)
    {
        if (declaringType is not TypeNode.Fqn f) return;
        var expected = DeclaringDefOf(references, member).GetGenericArguments().Length;
        var actual = f.Args?.Length ?? 0;
        if (expected != actual)
            throw new InvalidOperationException(
                $"bir2cir: declaring type '{f.Name}' of '{DescribeMember(member)}' has {expected} metadata "
                + $"parameter(s), but its memberRef carries {actual} argument(s)");
    }

    static TypeNode ShippedFieldType(ReferenceMetadataIndex references, IReadOnlySet<string> localTypes, FieldInfo field)
    {
        var shipped = references.PhysicalTwinOf(field, DeclaringDefOf(references, field)) ?? field;
        return references.NativeFieldSignature(shipped) ?? Modified(references, localTypes, ClrDeclarationMatching.RefTypeOf(references, localTypes, shipped.FieldType),
            shipped.GetRequiredCustomModifiers(), shipped.GetOptionalCustomModifiers());
    }

    // ---- the identity's three parts -----------------------------------------------------------------

    // The convention bit. A vararg member is RECORDED rather than refused: the frontend accepted the program,
    // so a backend that aborts here would be rejecting source over a fact it could simply state. Whether such
    // a signature can be emitted is the emitter's question, asked where it can be answered.
    static string ConventionOf(MethodBase member) =>
        (member.CallingConvention & CallingConventions.VarArgs) != 0
            ? (member.IsStatic ? MemberRefNode.VarargStatic : MemberRefNode.VarargInstance)
            : (member.IsStatic ? MemberRefNode.Static : MemberRefNode.Instance);

    // The DEFINITION of the type that declares the member, alias-resolved once. Both the assembly and the
    // declaring head are facts about this one type, and computing it twice is how the two drift apart.
    static Type DeclaringDefOf(ReferenceMetadataIndex references, MemberInfo member) => ClrDeclarationMatching.SafeDef(ClrDeclarationMatching.AliasResolve(references, DeclaringTypeOf(member)));

    // The assembly the EMITTED reference must be scoped to. Resolution reads whichever file this tool was
    // given; the program loads whatever ships. ManagedReferenceCatalog owns the one place those differ.
    //
    // NOTE for the consuming step: during a stdlib self-build the reference stdlib is on --compile-refs while
    // the assembly being emitted IS its runtime twin, so a member resolved there would name the module under
    // construction. No such member occurs in the corpus today; the emitter is where that has to become "this
    // is a TypeDef", because only the emitter knows what it is building.
    static string PhysicalAssemblyOf(ReferenceMetadataIndex references, MemberInfo member)
    {
        var declaring = DeclaringDefOf(references, member);
        var name = declaring.Assembly?.GetName()?.Name;
        if (string.IsNullOrEmpty(name))
            throw new InvalidOperationException(
                $"bir2cir: declaring type '{declaring}' of '{DescribeMember(member)}' has no assembly identity");
        return ManagedReferenceCatalog.PhysicalAssemblyName(name);
    }

    static Type DeclaringTypeOf(MemberInfo member) =>
        member.DeclaringType ?? throw new InvalidOperationException(
            $"bir2cir: resolved member '{DescribeMember(member)}' has no declaring type");

    // The declaring type, instantiated as the USE SITE sees it. The receiver's arguments are projected along
    // the declaration edge, so `List<string>.Add` is declared on `List`1<string>` while an accessor the
    // receiver inherits from `IReadOnlyCollection<T>` is declared on `IReadOnlyCollection`1<string>` — the
    // projection ilemit would otherwise have to redo from the receiver, which is where its base-interface
    // fallbacks came from.
    // Owner arguments can reach this early resolver before the full-tree lowering. A memberRef is already physical
    // CIR, so lower the WHOLE argument through the canonical rule — not only the function-type case that first made
    // the omission visible. A root function shape additionally becomes the nominal delegate type that instantiates
    // the owner; nested function nodes remain valid signature types and ilemit maps them through their carried clr.
    internal static TypeNode PhysicalOwnerArg(ReferenceMetadataIndex references, IReadOnlySet<string> localTypes, TypeNode arg, bool typeArg)
    {
        var lowered = BirTypeLowering.LowerPhysicalType(arg, references.Aliases, references.IsValueType,
            references.PhysicalTypeNames, typeArg, localTypes, nullableFrames: references.NullableTypeFrames);
        return lowered is TypeNode.Fn fn
            ? BirTypeLowering.DelegateFqnOf(fn)
                ?? throw new InvalidOperationException("bir2cir: a lowered owner function argument has no CLR delegate family")
            : lowered;
    }

    internal static TypeNode DeclaringTypeRef(ReferenceMetadataIndex references, IReadOnlySet<string> localTypes, MemberInfo member, Type openOwner, TypeNode[] ownerArgs,
        bool? ownerArgumentsAreMethodSlots = null)
    {
        var declaring = DeclaringDefOf(references, member);
        var head = ClrDeclarationMatching.PhysicalTypeName(declaring);
        // A type ARGUMENT has to be stated physically like everything else in a reference. Owner arguments may reach
        // here straight off the pre-lowering call node; the carrier itself is not rewritten downstream.
        // The owner's arguments occupy the same positions here as they do when the full owner TypeNode is
        // lowered later. Most generic arguments are storage slots (Root-V applies); delegate and KProperty
        // arguments are method slots (Root-H applies). Treating every owner argument as storage rewrites e.g.
        // KProperty1<List<T>, R> to KProperty1<IList<T>, R>, although its get receiver remains IReadOnlyList<T>.
        var methodSlots = ownerArgumentsAreMethodSlots
            ?? (openOwner != null && openOwner.IsGenericType && ClrDeclarationMatching.ArgumentsAreMethodSlots(openOwner));
        var argsAreSlots = !methodSlots;
        var args = (ownerArgs ?? Array.Empty<TypeNode>())
            .Select(a => PhysicalOwnerArg(references, localTypes, a, typeArg: argsAreSlots)).ToArray();
        var openDefinition = openOwner == null ? null : ClrDeclarationMatching.SafeDef(openOwner);
        // The receiver IS the declarer: its own arguments apply unchanged.
        if (openDefinition != null && openDefinition == declaring)
            return new TypeNode.Fqn(head, args.Length == 0 ? null : args);
        // Otherwise recover HOW the declarer's parameters relate to the receiver's from the receiver's own
        // graph. The member alone identifies WHICH type declares it; only the graph says how it is instantiated.
        var edge = DeclarationEdge(references, openDefinition, declaring);
        if (edge != null && edge.IsGenericType)
        {
            var projected = edge.GetGenericArguments().Select(a => SubstOwnerParamsPhysical(references, localTypes, a, args)).ToArray();
            return new TypeNode.Fqn(head, projected);
        }
        // A generic declarer reached through no edge we can read (a probe-closed owner, a synthetic view): name
        // it over its own parameters rather than inventing an instantiation.
        if (declaring.IsGenericTypeDefinition)
        {
            var own = declaring.GetGenericArguments()
                .Select(p => (TypeNode)new TypeNode.Tv("type", p.GenericParameterPosition)).ToArray();
            return new TypeNode.Fqn(head, own);
        }
        return new TypeNode.Fqn(head);
    }

    // The constructed ancestor of <paramref name="openOwner"/> whose definition is the declarer, with its
    // arguments still expressed over the owner definition's own parameters.
    // Both sides are alias-resolved before comparison: an @ClrTypeAlias'd base or interface resolves to its BCL
    // twin exactly as the declaring type did, and comparing a resolved declarer against an unresolved graph
    // would find no edge and silently fall back to naming the declarer over its own parameters.
    static Type DeclarationEdge(ReferenceMetadataIndex references, Type openOwner, Type declaringDef)
    {
        if (openOwner == null) return null;
        for (var b = openOwner; b != null; b = SafeBase(b))
            if (ClrDeclarationMatching.SafeDef(ClrDeclarationMatching.AliasResolve(references, b)) == declaringDef) return b;
        foreach (var iface in ClrDeclarationMatching.SafeInterfaces(openOwner))
            if (ClrDeclarationMatching.SafeDef(ClrDeclarationMatching.AliasResolve(references, iface)) == declaringDef) return iface;
        return null;
    }

    // `SubstOwnerParams`'s projection in the reference's own spelling: an owner-definition generic parameter
    // becomes the receiver's argument at that position, everything else keeps the target's verbatim name.
    // The declaring edge's own arguments, projected onto the receiver's. A storage slot, so the collapse applies.
    static TypeNode SubstOwnerParamsPhysical(ReferenceMetadataIndex references, IReadOnlySet<string> localTypes, Type t, TypeNode[] ownerArgs) =>
        ClrDeclarationMatching.PhysicalTypeOf(references, localTypes, t, ownerArgs ?? Array.Empty<TypeNode>(), typeArg: true);

    static Type SafeBase(Type t) { try { return t.BaseType; } catch { return null; } }

    // ---- signature ----------------------------------------------------------------------------------

    internal static TypeNode[] RefParamsOf(ReferenceMetadataIndex references, IReadOnlySet<string> localTypes, MethodBase member) =>
        references.NativeMethodSignature(member)?.Parameters ?? member.GetParameters()
            .Select(p => Modified(references, localTypes, ClrDeclarationMatching.RefTypeOf(references, localTypes, p.ParameterType),
                p.GetRequiredCustomModifiers(), p.GetOptionalCustomModifiers()))
            .ToArray();

    internal static TypeNode RefReturnOf(ReferenceMetadataIndex references, IReadOnlySet<string> localTypes, MethodInfo method)
    {
        if (references.NativeMethodSignature(method) is { } native) return native.Return;
        var ret = method.ReturnType;
        // `void` is the canonical spelling of System.Void and nothing else. A member that genuinely returns
        // kotlin.Unit returns a CLASS, and normalizing it here would erase the very distinction a return type
        // is carried to make.
        var node = ret == null || ret == typeof(void) || ret.FullName == "System.Void"
            ? MemberRefNode.Void
            : ClrDeclarationMatching.RefTypeOf(references, localTypes, ret);
        var rp = method.ReturnParameter;
        return rp == null ? node
            : Modified(references, localTypes, node, rp.GetRequiredCustomModifiers(), rp.GetOptionalCustomModifiers());
    }

    // Custom modifiers wrap the position they modify: `in DateTime` is modreq(InAttribute) around a by-ref
    // DateTime, and dropping it makes that member identical to the by-value overload next to it.
    //
    // Reflection exposes modifiers as two SETS, required and optional, not as the interleaved sequence the
    // metadata blob carries — so the nesting here is required-inside-optional by construction, not by
    // metadata order. Both sides of this pipeline read the same two lists the same way, which is what makes
    // the shape comparable; it is not a faithful rendering of the blob's order, and a member distinguished
    // ONLY by the interleaving of a modreq and a modopt would be beyond what reflection can tell us.
    static TypeNode Modified(ReferenceMetadataIndex references, IReadOnlySet<string> localTypes, TypeNode node, Type[] required, Type[] optional)
    {
        foreach (var m in required) node = new TypeNode.Mod(true, ClrDeclarationMatching.RefTypeOf(references, localTypes, m), node);
        foreach (var m in optional) node = new TypeNode.Mod(false, ClrDeclarationMatching.RefTypeOf(references, localTypes, m), node);
        return node;
    }

    static string DescribeMember(MemberInfo member)
    {
        try { return $"{member.DeclaringType}::{member.Name}"; }
        catch { return member?.Name ?? "<null>"; }
    }
}
