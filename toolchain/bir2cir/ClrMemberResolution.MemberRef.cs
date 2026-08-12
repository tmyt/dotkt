// #370: turning a member this pass RESOLVED into the one scalar reference CIR carries.
//
// Everything here answers a single question — "which declaration is this, exactly?" — and answers it once,
// so that no later layer re-combines pieces of an identity. Re-combining candidates is member selection, and
// that decision is settled here, against the compile-reference universe, or it is not settled at all.
//
// EVERY NAME IN HERE IS THE TARGET'S OWN. The transitional `memberSig` vocabulary next door spells a type
// the way the rest of the document does — arity backtick stripped, `+` nesting flattened to `.`, delegates
// rewritten as Kotlin function types, `System.Nullable`1` collapsed to a nullability wrapper. That is the
// right vocabulary for a document about a Kotlin program, and the wrong one for an identity, because it is
// LOSSY in exactly the places identity lives:
//
//   * `Outer`1+Inner` loses its nested half — stripping at the first backtick leaves plain `Outer`, a name
//     that either resolves to a different type or to none. This is not hypothetical; it was in the corpus.
//   * `Ns.Outer+Inner` and a genuine `Ns.Outer.Inner` become the same string.
//   * A pointer degrades to the FQN "System.Int32*", every array to a vector, custom modifiers vanish — each
//     one a way for two distinct members to look like one.
//
// So a reference spells every type — the declaring head, every parameter, the return, every generic argument
// and every custom modifier — as the target's VERBATIM metadata FullName, with `ptr`, array `rank` and
// in-position `mod` intact. The one spelling that is not verbatim is the void return, which names a type
// that can appear nowhere else in a signature and therefore makes no member ambiguous.
//
// `MemberSigOf` is deliberately left alone. Changing what it emits would change the bytes of every existing
// descriptor, and this reference is what replaces it — not a reason to churn it.
//
// The DECLARER, not the receiver, is what gets named. A member the receiver merely inherits is anchored on
// the type that declares it, with the receiver's type arguments projected along the declaration edge — which
// is why the reference needs no base walk, interface fan-out or most-derived rule downstream.

using System.Reflection;
using System.Text.Json.Nodes;
using DotKt.Bir;
using DotKt.Toolchain;

static partial class ClrMemberResolution
{
    /// <summary>The scalar reference for a resolved method/constructor, ready to be stamped on a node.</summary>
    internal static JsonNode MemberRefJson(MethodBase member, string kind, Type openOwner, TypeNode[] ownerArgs)
        => MemberRefOf(member, kind, openOwner, ownerArgs).Write();

    /// <summary>The scalar reference for a resolved field.</summary>
    internal static JsonNode FieldRefJson(FieldInfo field, Type openOwner, TypeNode[] ownerArgs)
        => FieldRefOf(field, openOwner, ownerArgs).Write();

    internal static MemberRefNode MemberRefOf(MethodBase member, string kind, Type openOwner, TypeNode[] ownerArgs)
    {
        var ctor = member as ConstructorInfo;
        var method = member as MethodInfo;
        if (ctor == null && method == null)
            throw new InvalidOperationException($"bir2cir: cannot reference '{member}' — neither a method nor a constructor (#370)");
        var node = new MemberRefNode(
            Kind: kind,
            Assembly: PhysicalAssemblyOf(member),
            DeclaringType: DeclaringTypeRef(member, openOwner, ownerArgs),
            Name: ctor != null ? MemberRefNode.CtorName : member.Name,
            GenericArity: member.IsGenericMethod ? member.GetGenericArguments().Length : 0,
            ReturnType: ctor != null
                ? MemberRefNode.Void
                : RefReturnOf(method),
            CallingConvention: ConventionOf(member),
            ParameterTypes: RefParamsOf(member));
        node.Validate();
        return node;
    }

    internal static MemberRefNode FieldRefOf(FieldInfo field, Type openOwner, TypeNode[] ownerArgs)
    {
        var node = new MemberRefNode(
            Kind: MemberRefNode.Kinds.Field,
            Assembly: PhysicalAssemblyOf(field),
            DeclaringType: DeclaringTypeRef(field, openOwner, ownerArgs),
            Name: field.Name,
            GenericArity: 0,
            // A field's "return" is its declared type — the same crossing a parameter of that type would be.
            ReturnType: Modified(RefTypeOf(field.FieldType),
                field.GetRequiredCustomModifiers(), field.GetOptionalCustomModifiers()));
        node.Validate();
        return node;
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
    static Type DeclaringDefOf(MemberInfo member) => SafeDef(AliasResolve(DeclaringTypeOf(member)));

    // The assembly the EMITTED reference must be scoped to. Resolution reads whichever file this tool was
    // given; the program loads whatever ships. ManagedReferenceCatalog owns the one place those differ.
    //
    // NOTE for the consuming step: during a stdlib self-build the reference stdlib is on --compile-refs while
    // the assembly being emitted IS its runtime twin, so a member resolved there would name the module under
    // construction. No such member occurs in the corpus today; the emitter is where that has to become "this
    // is a TypeDef", because only the emitter knows what it is building.
    static string PhysicalAssemblyOf(MemberInfo member)
    {
        var declaring = DeclaringDefOf(member);
        var name = declaring.Assembly?.GetName()?.Name;
        if (string.IsNullOrEmpty(name))
            throw new InvalidOperationException(
                $"bir2cir: declaring type '{declaring}' of '{DescribeMember(member)}' has no assembly identity (#370)");
        return ManagedReferenceCatalog.PhysicalAssemblyName(name);
    }

    static Type DeclaringTypeOf(MemberInfo member) =>
        member.DeclaringType ?? throw new InvalidOperationException(
            $"bir2cir: resolved member '{DescribeMember(member)}' has no declaring type (#370)");

    // The declaring type, instantiated as the USE SITE sees it. The receiver's arguments are projected along
    // the declaration edge, so `List<string>.Add` is declared on `List`1<string>` while an accessor the
    // receiver inherits from `IReadOnlyCollection<T>` is declared on `IReadOnlyCollection`1<string>` — the
    // projection ilemit would otherwise have to redo from the receiver, which is where its base-interface
    // fallbacks came from.
    static TypeNode DeclaringTypeRef(MemberInfo member, Type openOwner, TypeNode[] ownerArgs)
    {
        var declaring = DeclaringDefOf(member);
        var head = PhysicalTypeName(declaring);
        var args = ownerArgs ?? Array.Empty<TypeNode>();
        var openDefinition = openOwner == null ? null : SafeDef(openOwner);
        // The receiver IS the declarer: its own arguments apply unchanged.
        if (openDefinition != null && openDefinition == declaring)
            return new TypeNode.Fqn(head, args.Length == 0 ? null : args);
        // Otherwise recover HOW the declarer's parameters relate to the receiver's from the receiver's own
        // graph. The member alone identifies WHICH type declares it; only the graph says how it is instantiated.
        var edge = DeclarationEdge(openDefinition, declaring);
        if (edge != null && edge.IsGenericType)
        {
            var projected = edge.GetGenericArguments().Select(a => SubstOwnerParamsPhysical(a, args)).ToArray();
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
    static Type DeclarationEdge(Type openOwner, Type declaringDef)
    {
        if (openOwner == null) return null;
        for (var b = openOwner; b != null; b = SafeBase(b))
            if (SafeDef(AliasResolve(b)) == declaringDef) return b;
        foreach (var iface in SafeInterfaces(openOwner))
            if (SafeDef(AliasResolve(iface)) == declaringDef) return iface;
        return null;
    }

    // `SubstOwnerParams`'s projection in the reference's own spelling: an owner-definition generic parameter
    // becomes the receiver's argument at that position, everything else keeps the target's verbatim name.
    static TypeNode SubstOwnerParamsPhysical(Type t, TypeNode[] ownerArgs)
    {
        t = AliasResolve(t);
        if (t.IsGenericParameter)
            return t.GenericParameterPosition < ownerArgs.Length
                ? ownerArgs[t.GenericParameterPosition]
                : new TypeNode.Tv("type", t.GenericParameterPosition);
        if (t.IsByRef) return new TypeNode.ByRef(SubstOwnerParamsPhysical(t.GetElementType(), ownerArgs));
        if (t.IsPointer) return new TypeNode.Ptr(SubstOwnerParamsPhysical(t.GetElementType(), ownerArgs));
        if (t.IsArray)
        {
            var elem = SubstOwnerParamsPhysical(t.GetElementType(), ownerArgs);
            int rank = SafeArrayRank(t);
            return rank > 1 ? new TypeNode.Array(elem, rank) : new TypeNode.Array(elem);
        }
        if (t.IsGenericType && !t.IsGenericTypeDefinition)
            return new TypeNode.Fqn(PhysicalTypeName(t.GetGenericTypeDefinition()),
                t.GetGenericArguments().Select(a => SubstOwnerParamsPhysical(a, ownerArgs)).ToArray());
        return new TypeNode.Fqn(PhysicalTypeName(t));
    }

    static Type SafeBase(Type t) { try { return t.BaseType; } catch { return null; } }

    // ---- signature ----------------------------------------------------------------------------------

    static TypeNode[] RefParamsOf(MethodBase member) =>
        member.GetParameters()
            .Select(p => Modified(RefTypeOf(p.ParameterType),
                p.GetRequiredCustomModifiers(), p.GetOptionalCustomModifiers()))
            .ToArray();

    static TypeNode RefReturnOf(MethodInfo method)
    {
        var ret = method.ReturnType;
        // `void` is the canonical spelling of System.Void and nothing else. A member that genuinely returns
        // kotlin.Unit returns a CLASS, and normalizing it here would erase the very distinction a return type
        // is carried to make.
        var node = ret == null || ret == typeof(void) || ret.FullName == "System.Void"
            ? MemberRefNode.Void
            : RefTypeOf(ret);
        var rp = method.ReturnParameter;
        return rp == null ? node
            : Modified(node, rp.GetRequiredCustomModifiers(), rp.GetOptionalCustomModifiers());
    }

    // Custom modifiers wrap the position they modify: `in DateTime` is modreq(InAttribute) around a by-ref
    // DateTime, and dropping it makes that member identical to the by-value overload next to it.
    //
    // Reflection exposes modifiers as two SETS, required and optional, not as the interleaved sequence the
    // metadata blob carries — so the nesting here is required-inside-optional by construction, not by
    // metadata order. Both sides of this pipeline read the same two lists the same way, which is what makes
    // the shape comparable; it is not a faithful rendering of the blob's order, and a member distinguished
    // ONLY by the interleaving of a modreq and a modopt would be beyond what reflection can tell us.
    static TypeNode Modified(TypeNode node, Type[] required, Type[] optional)
    {
        foreach (var m in required) node = new TypeNode.Mod(true, RefTypeOf(m), node);
        foreach (var m in optional) node = new TypeNode.Mod(false, RefTypeOf(m), node);
        return node;
    }

    // A resolved member's declared type, spelled as the TARGET spells it (see the header). No arity stripping,
    // no `+`-to-`.` flattening, no delegate rewritten as a Kotlin function type, no `System.Nullable`1`
    // collapsed to a nullability wrapper: each of those is a document convention for talking about a Kotlin
    // program, and each one merges types this reference has to keep apart.
    static TypeNode RefTypeOf(Type t)
    {
        t = AliasResolve(t);
        if (t.IsByRef) return new TypeNode.ByRef(RefTypeOf(t.GetElementType()));
        if (t.IsPointer) return new TypeNode.Ptr(RefTypeOf(t.GetElementType()));
        if (t.IsArray)
        {
            var elem = RefTypeOf(t.GetElementType());
            int rank = SafeArrayRank(t);
            // Reflection reports rank 1 for BOTH `T[]` and the rare `T[*]`; the vector spelling is the one
            // this compiler can produce, and inventing a distinction reflection cannot see would be worse
            // than the shared spelling.
            return rank > 1 ? new TypeNode.Array(elem, rank) : new TypeNode.Array(elem);
        }
        if (t.IsGenericParameter)
            return new TypeNode.Tv(t.DeclaringMethod != null ? "method" : "type", t.GenericParameterPosition);
        if (t.IsGenericType && !t.IsGenericTypeDefinition)
            return new TypeNode.Fqn(PhysicalTypeName(t.GetGenericTypeDefinition()),
                t.GetGenericArguments().Select(RefTypeOf).ToArray());
        return new TypeNode.Fqn(PhysicalTypeName(t));
    }

    static int SafeArrayRank(Type t) { try { return t.GetArrayRank(); } catch { return 1; } }

    // A type's own metadata name, verbatim. Every name inside a reference is a lookup key in the target
    // universe rather than a document type spelling, so neither the arity backtick nor `+` nesting is
    // normalized away — normalizing either is what merged `Outer`1+Inner` into `Outer`.
    static string PhysicalTypeName(Type t) => t.FullName ?? t.Name;

    static string DescribeMember(MemberInfo member)
    {
        try { return $"{member.DeclaringType}::{member.Name}"; }
        catch { return member?.Name ?? "<null>"; }
    }
}
