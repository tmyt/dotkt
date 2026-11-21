using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class CapturedGenericInvocation
{
    internal static BirGraphValue ProjectStorageCall(BirGraphValue call,
        ProjectedClassStorageFrame.InterfaceSlot[] intersections, ReferenceMetadataIndex refs,
        Func<string,string> allocate, List<IBirTypeDeclaration> generated, BirClassDefinition lexical,
        List<BirFunctionDeclaration> lexicalMethods)
    {
        if (!Invocation.Admits(call)) return call;
        var site = new Invocation(call);
        var (member, arguments, values, receiver) = (site.Member, site.TypeArguments.OrDefault(),
            site.Arguments.OrDefault(), site.Receiver);
        if (member?.DeclaringType.OrDefault() is not TypeNode.Fqn owner || arguments == null || values == null) return call;
        var candidates = arguments.Select((argument, index) => (argument, index))
            .Where(row => row.argument is TypeNode.Tv { Scope: "type" } variable && intersections.Any(slot => slot.OwnerIndex == variable.I))
            .ToArray();
        if (candidates.Length == 0 || !refs.TryResolveStaticMemberSignature(owner.Name, member.Name.Value, arguments.Length,
            receiver == null, member.ParameterTypes.Value, owner.Args, out _, out var selected, out var declaringOwner)) return call;
        var resolved = BirMemberReference.FromPhysical(ClrMemberResolution.MemberRefOf(null, refs, selected, "method", declaringOwner, owner.Args));
        if (!member.StructurallyEquals(resolved)) throw new InvalidOperationException("Projected generic call lost its selected MethodDef");
        var generic = selected.GetGenericArguments();
        var lost = candidates.Where(row => generic[row.index].GetGenericParameterConstraints().Any(t => t.IsInterface))
            .Select(row => row.index).ToHashSet();
        bool UsesLostSlot(System.Type type) =>
            type.IsGenericParameter ? type.DeclaringMethod != null && lost.Contains(type.GenericParameterPosition)
            : type.HasElementType ? UsesLostSlot(type.GetElementType())
            : type.IsGenericType && type.GetGenericArguments().Any(UsesLostSlot);
        // A dependent Y : X must be closed against the recovered X, not its
        // former class bound. An unaffected, explicitly concrete Y stays exact.
        int previous;
        do {
            previous = lost.Count;
            foreach (var (_, index) in candidates)
                if (generic[index].GetGenericParameterConstraints().Any(UsesLostSlot)) lost.Add(index);
        } while (previous != lost.Count);
        var missing = lost.Order().ToArray();
        if (missing.Length == 0) return call;
        // A different parameter can force this storage class to split. Preserve
        // each authored native argument whose own primary still proves its facets.
        var facets = (from i in missing
            from facet in intersections
            where facet.OwnerIndex == ((TypeNode.Tv)arguments[i]).I
            select (Slot: i, facet.Bound)).ToArray();
        var formal = selected.GetParameters();
        if ((receiver != null && selected.DeclaringType.IsValueType)
            || formal.Any(p => RequiresTypedSlot(p.ParameterType)) || RequiresTypedSlot(selected.ReturnType))
            return ProjectAddressCall(site, selected, owner, facets, refs, allocate, generated);

        // The original native method is selected at compile time. Open only
        // its type argument on the actual object at the call boundary: the
        // projected class's primary slot guarantees the class, not its interfaces.
        var unit = selected.ReturnType.FullName == "System.Void";
        var name = unit ? "starProjectionInvokeIntersectionUnit" : "starProjectionInvokeIntersection";
        var runtimeType = new TypeNode.Fqn("DotKt.Runtime.CompilerServices.StarProjectionType");
        var any = new TypeNode.Nullable(new TypeNode.Fqn("kotlin.Any"));
        var lookup = new TypeNode[] { any, runtimeType, new TypeNode.Fqn("kotlin.Int"), new TypeNode.Fqn("kotlin.String"),
            new TypeNode.Array(new TypeNode.Fqn("kotlin.String")), new TypeNode.Array(runtimeType), new TypeNode.Fqn("kotlin.IntArray"),
            new TypeNode.Array(runtimeType), new TypeNode.Array(any) };
        if (!refs.TryResolveStaticMemberSignature(RuntimeOwner, name, 0, true, lookup, null, out _, out var helper, out var declaring))
            throw new InvalidOperationException($"Projected invocation has no runtime binding for '{name}'");
        var target = BirMemberReference.FromPhysical(ClrMemberResolution.MemberRefOf(null, refs, helper, "method", declaring, null));
        var lookupOwner = owner;
        var lookupName = selected.Name;
        var lookupToken = selected.MetadataToken;
        if (site.Super)
        {
            // MethodInfo.Invoke dispatches virtually. Put the nonvirtual call on
            // its lexical this, then close and invoke just that private forwarder.
            TypeNode Close(TypeNode t) => SupertypeGraph.SubstOwnerTvs(t, owner.Args ?? []);
            var frame = generic.Select(p => CloneParameter(ReferenceMetadataReader.GenericParamDeclaration(p, native: true), Close)).ToArray();
            var signature = member.ParameterTypes.Value.Select(t => Close(SignatureValueTypes.Of(t))).ToArray();
            var ret = Close(SignatureValueTypes.Of(member.ReturnType.Value));
            var forward = site.SuperForward(signature, ret, generic.Length, false);
            var number = lexicalMethods.Count;
            do lookupName = "dotkt$projectedSuper$" + number++;
            while (lexical.Methods.OrEmpty().Concat(lexicalMethods).Any(m => m.Name.OrDefault() == lookupName));
            lexicalMethods.Add(Method(lookupName, frame, signature, ret, Body(forward, unit, false), false, "private"));
            lookupOwner = new TypeNode.Fqn(lexical.Name.Value, Enumerable.Range(0, TypeParameterFrame.Count(lexical))
                .Select(i => (TypeNode)new TypeNode.Tv("type", i)).ToArray());
            lookupToken = 0; // A generated local declaration has no MethodDef token until emission.
        }
        var invoke = new BirNativeStatic {
            Type = new TypeNode.Fqn(RuntimeOwner), Method = name, MemberRef = target,
            Ret = target.ReturnType, ResolvedMemberReturn = target.ReturnType,
            Args = new IBirExpression[] { receiver ?? Null(), ClassRef(lookupOwner), Number(lookupToken), Text(lookupName),
                NewArray(String, formal.Select(p => (IBirExpression)Text(ReferenceMetadataIndex.ForeignStarRuntimeTypeKey(p.ParameterType)))),
                NewArray(Type, arguments.Select(t => (IBirExpression)ClassRef(t))),
                NewArray(Int, facets.Select(f => (IBirExpression)Number(f.Slot))),
                NewArray(Type, facets.Select(f => (IBirExpression)ClassRef(f.Bound))), NewArray(Object, values) },
        };
        ForeignStarProjectionBinding.RequireRuntimeFallback();
        var result = FBoundStarProjectionErasure.SubstituteDeclarationTypeArguments(member.ReturnType.Value, owner.Args ?? [], arguments);
        return unit || IsObject(result) ? invoke : Cast(result, invoke);
    }
}
