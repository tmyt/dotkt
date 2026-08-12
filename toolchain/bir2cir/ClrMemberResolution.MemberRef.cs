// #370: turning a member this pass RESOLVED into the one scalar reference CIR carries.
//
// Everything here answers a single question — "which declaration is this, exactly?" — and answers it once,
// so that no later layer re-combines pieces of an identity. Re-combining candidates is member selection, and
// that decision is settled here, against the compile-reference universe, or it is not settled at all.
//
// Two deliberate differences from the transitional `memberSig` vocabulary next door:
//
//   * FIDELITY. `MemberSigOf` flattens shapes Kotlin cannot spell — every array becomes an SZ array, a
//     pointer becomes the FQN string "System.Int32*", and custom modifiers are dropped. Each of those makes
//     two distinct members look like one, which is survivable for a descriptor that is only one half of a
//     structural match but fatal for an identity that must select on its own. So the reference walks types
//     with `ptr`, array `rank` and in-position `mod` intact. `MemberSigOf` itself is left ALONE: changing
//     what it emits would change the bytes of every existing descriptor, and this reference is what replaces
//     it, not a reason to churn it.
//
//   * THE DECLARING HEAD IS PHYSICAL. A signature leaf is an ordinary CIR type node (the stripped-arity
//     spelling every other type slot uses); the declaring type's own name is the target's verbatim metadata
//     FullName, arity backtick and `+` nesting included — exactly what `memberOwner` already carries. That is
//     the key the target universe is indexed by, so a consumer resolves it directly instead of probing arities.
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
        // A vararg signature is refused rather than encoded: nothing in this source language reaches one, and a
        // silently wrong convention would produce a valid-looking reference to a member nobody meant.
        if ((member.CallingConvention & CallingConventions.VarArgs) != 0)
            throw new InvalidOperationException(
                $"bir2cir: '{DescribeMember(member)}' has a vararg calling convention, which a member reference does not encode (#370)");
        var node = new MemberRefNode(
            Kind: kind,
            Assembly: PhysicalAssemblyOf(member),
            DeclaringType: DeclaringTypeRef(member, openOwner, ownerArgs),
            Name: ctor != null ? MemberRefNode.CtorName : member.Name,
            GenericArity: member.IsGenericMethod ? member.GetGenericArguments().Length : 0,
            ReturnType: ctor != null
                ? MemberRefNode.Void
                : RefReturnOf(method),
            CallingConvention: member.IsStatic ? MemberRefNode.Static : MemberRefNode.Instance,
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

    // The assembly the EMITTED reference must be scoped to. Resolution reads whichever file this tool was
    // given; the program loads whatever ships. ManagedReferenceCatalog owns the one place those differ.
    static string PhysicalAssemblyOf(MemberInfo member)
    {
        var declaring = SafeDef(AliasResolve(DeclaringTypeOf(member)));
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
        var declaring = SafeDef(AliasResolve(DeclaringTypeOf(member)));
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
            var projected = edge.GetGenericArguments().Select(a => SubstOwnerParams(a, args)).ToArray();
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
    static Type DeclarationEdge(Type openOwner, Type declaringDef)
    {
        if (openOwner == null) return null;
        for (var b = openOwner; b != null; b = SafeBase(b))
            if (SafeDef(b) == declaringDef) return b;
        foreach (var iface in SafeInterfaces(openOwner))
            if (SafeDef(iface) == declaringDef) return iface;
        return null;
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
        var node = ret == null || ret == typeof(void) || ret.FullName is "System.Void" or "kotlin.Unit"
            ? MemberRefNode.Void
            : RefTypeOf(ret);
        var rp = method.ReturnParameter;
        return rp == null ? node
            : Modified(node, rp.GetRequiredCustomModifiers(), rp.GetOptionalCustomModifiers());
    }

    // Custom modifiers wrap the position they modify, outermost-last in the order reflection reports them —
    // the same two lists, read the same way, on the writing and the reading side. `in DateTime` is
    // modreq(InAttribute) around a by-ref DateTime, and dropping it makes that member identical to the
    // by-value overload next to it.
    static TypeNode Modified(TypeNode node, Type[] required, Type[] optional)
    {
        foreach (var m in required) node = new TypeNode.Mod(true, RefTypeOf(m), node);
        foreach (var m in optional) node = new TypeNode.Mod(false, RefTypeOf(m), node);
        return node;
    }

    // A resolved member's declared type in the CIR type vocabulary, with the shapes `MemberSigOf` flattens
    // kept intact (see the header): pointers, array rank, and — via Modified above — custom modifiers.
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
        if (IsShapeDelegate(t)) return DelegateFn(t);
        if (t.IsGenericType && !t.IsGenericTypeDefinition)
        {
            var def = t.GetGenericTypeDefinition();
            var args = t.GetGenericArguments().Select(RefTypeOf).ToArray();
            if (def.FullName == "System.Nullable`1") return new TypeNode.Nullable(args[0]);
            return new TypeNode.Fqn(StripArity(Dotted(def.FullName ?? def.Name)), args);
        }
        return new TypeNode.Fqn(StripArity(Dotted(t.FullName ?? t.Name)));
    }

    static int SafeArrayRank(Type t) { try { return t.GetArrayRank(); } catch { return 1; } }

    // The declarer's own metadata name, verbatim: this one is a lookup key in the target universe, not a
    // document type spelling, so neither the arity backtick nor `+` nesting is normalized away.
    static string PhysicalTypeName(Type t) => t.FullName ?? t.Name;

    static string DescribeMember(MemberInfo member)
    {
        try { return $"{member.DeclaringType}::{member.Name}"; }
        catch { return member?.Name ?? "<null>"; }
    }
}
