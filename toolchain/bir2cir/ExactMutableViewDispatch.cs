using System;
using System.Linq;
using System.Reflection;
using DotKt.Bir;

// An opaque value slot does not make its ordinary invariant source view unknown. Prove the
// exact eligible native face at runtime, then call the already-selected MethodDef directly.
// Use-site projections and CLR-variant interface tests are not such a proof.
static partial class ExactMutableViewDispatch
{
    static readonly TypeNode Any = new TypeNode.Fqn("kotlin.Any");
    static readonly TypeNode Bool = new TypeNode.Fqn("kotlin.Boolean");

    internal static bool TrySourceView(TypeNode.Fqn owner, ReferenceMetadataIndex refs, Func<TypeNode, TypeNode> projectValue,
        out TypeNode.Fqn view, out int family)
    {
        family = (KotlinTypeWitness.Flags(owner) & ~1) switch { 4 => 5, 8 => 6, 12 => 4, _ => -1 };
        view = null;
        if (family < 0 || owner.Args == null || AliasVarianceRepresentation.HasSourceVariance(owner, refs)) return false;
        var args = refs.NullableTypeFrames.GetValueOrDefault(owner.Name)?.OrdinaryArguments(owner.Args) ?? owner.Args;
        if (args.Length != 1 || HasProjection(args[0])) return false;
        view = new TypeNode.Fqn(family switch {
            4 => "System.Collections.Generic.ISet`1",
            5 => "System.Collections.Generic.ICollection`1",
            _ => "System.Collections.Generic.IList`1",
        }, args);
        // The guard, cache key and exact MemberRef must agree on the complete physical view.
        // In particular, nested opaque Kotlin values are not a different native construction witness here.
        view = ClrMemberResolution.PhysicalInstanceView(refs, (TypeNode.Fqn)projectValue(view));
        return IsInvariant(view, refs);
    }

    internal static TypeNode.Fqn CloseDeclaring(TypeNode.Fqn declaring, TypeNode[] args) =>
        new(declaring.Name, declaring.Args == null ? args : declaring.Args.Select(type => Close(type, args)).ToArray());

    static TypeNode Close(TypeNode type, TypeNode[] args) => type switch {
        TypeNode.Tv { Scope: "type" } variable => args[variable.I],
        TypeNode.Fqn { Args: { } nested } named => new TypeNode.Fqn(named.Name, nested.Select(arg => Close(arg, args)).ToArray()),
        _ => type,
    };

    static bool IsInvariant(TypeNode.Fqn view, ReferenceMetadataIndex refs) =>
        view.Args is { Length: 1 } args && !HasProjection(args[0])
        && refs.ResolveNetType(view.Name, 1) is { IsInterface: true } native
        && native.GetGenericArguments().All(parameter =>
            (parameter.GenericParameterAttributes & GenericParameterAttributes.VarianceMask) == GenericParameterAttributes.None);

    static bool HasProjection(TypeNode type) => type switch {
        TypeNode.Star or TypeNode.Projection => true,
        TypeNode.Nullable n => HasProjection(n.Of), TypeNode.Oblivious o => HasProjection(o.Of),
        TypeNode.Fqn { Args: { } args } => args.Any(HasProjection),
        TypeNode.Array a => HasProjection(a.Elem), TypeNode.ByRef b => HasProjection(b.Of),
        TypeNode.Ptr p => HasProjection(p.Of), TypeNode.Mod m => HasProjection(m.M) || HasProjection(m.Of),
        TypeNode.Fn f => HasProjection(f.Ret) || f.Params.Any(HasProjection)
            || f.Recv != null && HasProjection(f.Recv) || f.Ctx?.Any(HasProjection) == true,
        _ => false,
    };

    internal static void SelfTest()
    {
        // Owning guard/void fallback/receiver-once assertions are exercised by
        // SelfTestSource. Keep the independent projection/frame facts here.
        if (!HasProjection(new TypeNode.Array(new TypeNode.Star())))
            throw new InvalidOperationException("Exact mutable dispatch lost nested projection ownership");
        var closedResult = new TypeNode.Fqn("NestedValue", new TypeNode[] {
            new TypeNode.Tv("type", 0), new TypeNode.Tv("method", 1),
        });
        var closedCall = new BirCallInstance {
            OwnerType = new TypeNode.Fqn("System.Collections.Generic.IList`1", new TypeNode[] { closedResult }),
            Ret = closedResult, Sty = closedResult,
        };
        ConstructedMemberReturnSubstitution.ApplyCall(closedCall);
        if (closedCall.Ret.Value != closedResult || closedCall.Sty.Value != closedResult)
            throw new InvalidOperationException("Exact native result reapplied a caller-owned generic frame");
        Console.WriteLine("[exact mutable dispatch] self-test OK (nested projection, exact caller-owned result)");
    }
}
