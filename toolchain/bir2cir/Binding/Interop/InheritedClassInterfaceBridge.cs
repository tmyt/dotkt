using System;
using System.Collections.Generic;
using System.Linq;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

// Materialize a CLR interface slot when Kotlin satisfies it with a NON-VIRTUAL method inherited from a base class.
//
// Kotlin fake-override resolution accepts `class D : B(), I` when B has the matching public concrete function. CLR
// implicit interface implementation cannot bind I.M to an inherited non-virtual method, so D is unloadable unless it
// owns a virtual slot. kotc records the selected inherited implementation and override closure; bir2cir resolves that
// exact declaration in its constructed owner frame. ilemit then emits an ordinary CIR method and wires its declared override; it
// performs no fake-override inference.
//
// Exact formal signature, generic arity and return equality are mandatory. Ambiguity is skipped, never guessed. This is
// hierarchy-driven and contains no library/type/member special cases.
static partial class InheritedClassInterfaceBridge
{
    public static void MaterializeSuspendDeclarations(IReadOnlyList<BirFileDeclaration> roots, ReferenceMetadataIndex refs)
    {
        // Source-known suspend forwarders must exist before inline carriers, signature ownership and hot/cold
        // lowering snapshot declarations. The later exact bridge pass also handles types materialized afterwards.
        var defs = SupertypeGraph.CollectSource(roots);
        var declarations = new Dictionary<string, IBirTypeDeclaration>(StringComparer.Ordinal);
        foreach (var declaration in roots.SelectMany(root => root.Types.OrEmpty()))
            if (Name(declaration) is { } name) declarations[name] = declaration;
        static string Name(IBirTypeDeclaration declaration) => declaration switch {
            BirClassDefinition owner => owner.Name.OrDefault(),
            BirInterfaceDefinition owner => owner.Name.OrDefault(),
            BirEnumDefinition owner => owner.Name.OrDefault(),
            _ => throw new InvalidOperationException("Unaccounted inherited source owner"),
        };
        static BirFunctionDeclaration[] Methods(IBirTypeDeclaration declaration) => declaration switch {
            BirClassDefinition owner => owner.Methods.OrEmpty(),
            BirInterfaceDefinition owner => owner.Methods.OrEmpty(),
            BirEnumDefinition owner => owner.Methods.OrEmpty(),
            _ => throw new InvalidOperationException("Unaccounted inherited source methods"),
        };
        foreach (var cls in defs.Values.Where(d => d.Kind == "class"))
        {
            if (declarations[cls.Name] is not BirClassDefinition { InheritedClassMethods.HasValue: true } owner) continue;
            foreach (var fact in owner.InheritedClassMethods.Value)
            {
                if (fact.Mods.OrDefault()?.Suspend.OrDefault() != true) continue;
                if (fact.InheritedImplementation.OrDefault() is not { } selected
                    || selected.Owner.OrDefault() is not TypeNode.Fqn selectedOwner
                    || selected.Member.OrDefault() is not { } member
                    || !fact.Params.HasValue || !fact.Overrides.HasValue) continue;
                var ownerName = selectedOwner.Name;
                var parameters = fact.Params.Value;
                var overrides = fact.Overrides.Value;
                var owners = SupertypeGraph.Reachable(cls, defs, refs)
                    .Where(edge => !edge.isInterface && edge.spec.Name == ownerName)
                    .Select(edge => edge.spec).Distinct().ToList();
                if (owners.Count != 1) continue;
                var interfaceEdges = overrides.Where(edge =>
                    edge.Owner.OrDefault() is TypeNode.Fqn type
                    && (defs.TryGetValue(type.Name, out var definition) ? definition.Kind == "interface"
                        : refs.IsInterfaceType(new TypeNode.Fqn(type.Name))))
                    .ToList();
                if (interfaceEdges.Count == 0) continue;
                var selectedEdge = overrides.FirstOrDefault(edge =>
                    edge.Owner.OrDefault() is TypeNode.Fqn type && type.Name == ownerName && edge.Member.OrDefault() == member);
                var declarationId = selectedEdge?.DeclarationId.OrDefault();
                var selectedSignature = selected.Sig.OrDefault()
                    ?? throw new InvalidOperationException("selected inherited suspend implementation has no signature");
                var methodArity = selected.TypeParams.OrEmpty().Length;
                var targetIsVirtual = declarations.TryGetValue(ownerName, out var localOwner)
                    ? Methods(localOwner).Where(method =>
                            !method.Static.OrDefault() && method.Name.OrDefault() == member
                            && method.TypeParams.OrEmpty().Length == methodArity
                            && KotlinOverrideSlotBridge.SameSourceMethodTypeParameterShape(method.TypeParams.OrDefault(),
                                selected.TypeParams.OrDefault(), owners[0].Args ?? Array.Empty<TypeNode>(),
                                owners[0].Args ?? Array.Empty<TypeNode>())
                            && method.Params.HasValue
                            && method.Params.Value.Select(p => p.Type.OrDefault()).SequenceEqual(selectedSignature))
                        .Select(method => method.Virtual.OrDefault() || method.Override.OrDefault()).Single()
                    : refs.TrySelectedSourceOverrideDeclaration(ownerName, member, methodArity,
                        parameters.Select(p => p.Type.OrDefault()).ToArray(),
                        owners[0].Args ?? Array.Empty<TypeNode>(), selected.TypeParams.OrDefault(),
                        owners[0].Args ?? Array.Empty<TypeNode>(), true, out var target)
                        ? target.IsVirtual
                        : throw new InvalidOperationException($"selected inherited suspend declaration '{ownerName}.{member}' is unresolved");
                if (targetIsVirtual) continue;
                var call = new BirCallInstance
                {
                    OwnerType = owners[0], Virtual = true, ClrOwnerResolved = true, SuspendCall = true,
                    Recv = new BirThis(), Method = member, Sig = selectedSignature.ToArray(),
                    Args = parameters.Select(p => (IBirExpression)new BirLocal {
                        Name = p.Name.HasValue ? p.Name : OptionalField<string>.Null,
                    }).ToArray(),
                    Ret = fact.Ret, DynRet = fact.Ret, Sty = fact.Ret,
                };
                if (declarationId != null) call.DeclarationId = declarationId;
                if (fact.TypeParams.OrEmpty().Length > 0)
                    call.TypeArgs = Enumerable.Range(0, fact.TypeParams.Value.Length)
                        .Select(i => (TypeNode)new TypeNode.Tv("method", i)).ToArray();
                // An independent declaration occurrence retains every source fact
                // owned by the inherited-method profile; its implementation edge
                // is consumed rather than copied onto the generated declaration.
                var copy = (BirInheritedClassMethod)fact.Rewrite(node => node);
                var bridge = new BirFunctionDeclaration {
                    Name = fact.Member, Params = copy.Params, Ret = copy.Ret, Mods = copy.Mods,
                    PropertyName = copy.PropertyName, PropertyAccessor = copy.PropertyAccessor,
                    PropertyAssociation = copy.PropertyAssociation, TypeParams = copy.TypeParams,
                    RetSuspendFnTypePre = copy.RetSuspendFnTypePre,
                    MethodTypeParameterBoundsPre = copy.MethodTypeParameterBoundsPre,
                    SignatureOverrideDeclarations = copy.SignatureOverrideDeclarations,
                    Static = false, Override = false, Abstract = false, Virtual = true, Vis = "public",
                    SuspendRet = copy.Ret, Attrs = Array.Empty<BirAttribute>(),
                    Overrides = interfaceEdges.Select(edge => (BirOverride)edge.Rewrite(node => node)).ToArray(),
                    Body = new IBirStatement[] { new BirReturn {
                        Value = OptionalField<IBirExpression>.Of(MaterializedExecutable.ReturnedCall(call)),
                    } },
                };
                if (declarationId != null)
                {
                    bridge.DeclarationId = DeclarationIdentityBinding.InheritedProjectionId(cls.Name, new[] { declarationId });
                    bridge.DeclarationSourceName = bridge.Name;
                    bridge.Generated = true;
                }
                owner.Methods = owner.Methods.OrEmpty().Append(bridge).ToArray();
            }
        }
    }

    static TypeNode[] EffectiveArgs(TypeNode.Fqn spec, int arity)
    {
        if (arity == 0) return Array.Empty<TypeNode>();
        return spec.Args is { } args && args.Length == arity ? args : null;
    }

    static TypeNode SubstOwnerTvs(TypeNode type, TypeNode[] args) => type switch
    {
        TypeNode.Tv { Scope: "type" } tv when tv.I >= 0 && tv.I < args.Length => args[tv.I],
        TypeNode.Fqn f when f.Args is not null => new TypeNode.Fqn(f.Name, f.Args.Select(a => SubstOwnerTvs(a, args)).ToArray()),
        TypeNode.Nullable n => new TypeNode.Nullable(SubstOwnerTvs(n.Of, args)),
        TypeNode.Oblivious o => new TypeNode.Oblivious(SubstOwnerTvs(o.Of, args)),
        TypeNode.Array a => new TypeNode.Array(SubstOwnerTvs(a.Elem, args)),
        TypeNode.ByRef b => new TypeNode.ByRef(SubstOwnerTvs(b.Of, args)),
        TypeNode.Fn fn => new TypeNode.Fn(fn.Suspend, SubstOwnerTvs(fn.Ret, args),
            fn.Params.Select(p => SubstOwnerTvs(p, args)).ToArray(),
            fn.Recv == null ? null : SubstOwnerTvs(fn.Recv, args)),
        _ => type,
    };

}
