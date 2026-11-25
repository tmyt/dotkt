using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

sealed partial class ProjectedCallableEntries
{
    readonly HashSet<string> operationTypes = new(StringComparer.Ordinal);

    // A selected interface MethodDef can lack the projected body's companion rows.
    // Its caller nevertheless supplied the exact method argument, not its upper
    // bound. Reintroduce the source relation A : K via a contravariant operation
    // implemented at the proven owner K, and invoke that operation at A.
    void DeclareOrdinaryInterfaceBridge(BirFileDeclaration[] files, IReadOnlyDictionary<string, SupertypeGraph.Def> hierarchy,
        IBirTypeDeclaration owner, BirFunctionDeclaration target, Entry entry, string id)
    {
        ReferencedDeclarationSlot slot;
        BirTypeParameter[] rows;
        IReadOnlyDictionary<int, TypeNode[]> bounds;
        NullableRepresentationFrame selectedFrame;
        Func<TypeNode.Fqn, TypeNode[]> sourceArguments;
        if (originalSlots.TryGetValue(id, out var local))
        {
            if (local.Owner.Kind.OrDefault() != "interface") return;
            var m = local.Method;
            rows = m.TypeParams.OrEmpty();
            bounds = (m.RoundtripMethodBounds.OrDefault() ?? m.MethodTypeParameterBoundsPre.OrDefault())?.Bounds;
            selectedFrame = m.RoundtripDeclarationIdentity.OrDefault()?.NullableRepresentationFrame.OrDefault();
            slot = new(local.Owner.Name.Value, m.Name.Value, rows.Length,
                m.Params.OrEmpty().Select(FunctionSignatureIdentity.SignatureType).ToArray(), m.Ret.Value, true, false);
            var type = (BirInterfaceDefinition)local.Owner;
            var ownerFrame = type.RoundtripSupertypesPre.OrDefault()?.NullableFrame;
            sourceArguments = t => ownerFrame?.OrdinaryArguments(t.Args ?? []) ?? t.Args ?? [];
        }
        else
        {
            if (!references.TryDeclarationSignatureSlot(id, out slot) || !slot.IsInterface) return;
            rows = references.PhysicalMethodTypeParametersInPhysicalFrame(id, nativeNames: true);
            bounds = references.SelectedSourceMethodBounds(id);
            selectedFrame = references.NullableMethodFrame(id);
            sourceArguments = references.SourceArgumentsOfPhysicalOwner;
        }
        if (bounds == null || rows == null || rows.Length == 0) return;
        var selectedOwner = SupertypeGraph.Reachable(hierarchy[owner.Name.Value], hierarchy, references)
            .Select(edge => edge.spec).Distinct().Single(s => s.Name == slot.Owner);
        var sourceOwner = sourceArguments(selectedOwner);
        var physicalOwner = selectedOwner.Args ?? [];
        TypeNode Close(TypeNode t) => SupertypeGraph.SubstOwnerTvs(t, physicalOwner);
        var selected = selectedFrame ?? new NullableRepresentationFrame(rows.Length, []);
        var destination = entry.Representations ?? new NullableRepresentationFrame(entry.Original.Length, []);
        if (selected.SourceArity != destination.SourceArity) throw new InvalidOperationException("Selected override source arities disagree");
        // Nondependent virtual contracts do not grant the caller any additional
        // proofs. A nominal class bound is what permits a CLR variance conversion.
        var proofs = bounds.OrderBy(p => p.Key).SelectMany(p => p.Value.OfType<TypeNode.Tv>()
            .Where(v => v.Scope == "type" && Nominal(rows[selected.SourcePosition(p.Key)]))
            .Select(v => (Position: selected.SourcePosition(p.Key), Bound: sourceOwner[v.I]))).ToList();
        if (proofs.Count == 0) return;
        var weakRows = rows.Select(p => CapturedGenericInvocation.CloneParameter(p, Close)).ToArray();
        // A separately represented storage argument is not the nominal A.
        // Preserve that argument, transporting only its missing interface proofs.
        for (int position = 0; position < selected.PhysicalArity; position++)
        {
            var role = selected.PhysicalSlot(position);
            if (role.Representation == NullableRepresentationFrame.Role.Ordinary) continue;
            var row = Enumerable.Range(0, destination.PhysicalArity).FirstOrDefault(i => destination.PhysicalSlot(i) == role, -1);
            if (row < 0) continue;
            foreach (var required in entry.Plan.Parameters[row].Constraints.OrEmpty().OfType<TypeNode.Fqn>())
            {
                if (weakRows[position].Constraints.OrEmpty().Contains(required)) continue;
                bool dependsOnMethod = false;
                BirGraphNameFields.VisitTypeNodes(required, t => dependsOnMethod |= t is TypeNode.Tv { Scope: "method" });
                if (!dependsOnMethod) proofs.Add((position, required));
            }
        }
        var anchors = proofs.Distinct().ToArray();
        var file = files.Single(f => f.Types.OrEmpty().Contains(owner));
        var stem = owner.Name.Value + "$" + target.Name.Value + "$operation";
        var opName = stem;
        for (int n = 0; kinds.ContainsKey(opName) || !operationTypes.Add(opName); ++n) opName = stem + n;
        var context = TypeParameterFrame.Declarations(owner).Select(p => (BirTypeParameter)p.DeepClone()).ToArray();
        foreach (var p in context) p.Variance = default;
        TypeNode[] inContext = Enumerable.Range(0, context.Length).Select(i => (TypeNode)new TypeNode.Tv("type", i)).ToArray();
        var operationRows = context.Concat(anchors.Select((_, i) => new BirTypeParameter { Name = "$bound" + i, Variance = "in" })).ToArray();
        var implemented = new TypeNode.Fqn(opName, inContext.Concat(anchors.Select(a => a.Bound)).ToArray());
        var viewed = new TypeNode.Fqn(opName, inContext.Concat(anchors.Select(a => (TypeNode)new TypeNode.Tv("method", a.Position))).ToArray());
        BirTypeParameter[] Strengthen(TypeNode[] arguments)
        {
            var result = weakRows.Select(p => (BirTypeParameter)p.DeepClone()).ToArray();
            for (int i = 0; i < anchors.Length; i++)
            {
                var p = result[anchors[i].Position];
                p.Constraints = p.Constraints.OrEmpty().Append(arguments[i]).Distinct().ToArray();
            }
            return result;
        }
        var formals = slot.Parameters.Select((t, i) => new BirParameter { Name = target.Params.Value[i].Name, Type = Close(t) }).ToArray();
        var ret = Close(slot.Return);
        var invoke = new BirFunctionDeclaration { Name = "Apply", TypeParams = Strengthen(anchors.Select((_, i) => (TypeNode)new TypeNode.Tv("type", context.Length + i)).ToArray()),
            Params = formals, Ret = ret, Abstract = true, Virtual = true, Static = false, Vis = "public", Generated = true };
        var op = new BirInterfaceDefinition { Kind = "interface", Name = opName, Vis = "internal", Generated = true,
            TypeParams = operationRows, Methods = new[] { invoke }, Interfaces = Array.Empty<TypeNode>() };
        file.Types = file.Types.Value.Append(op).ToArray();
        owner.Interfaces = owner.Interfaces.OrEmpty().Append(implemented).ToArray();
        BirFunctionDeclaration Bridge(TypeNode.Fqn slotOwner, string member, BirTypeParameter[] parameters)
        {
            var names = owner.Methods.Value.Select(m => m.Name.OrDefault()).ToHashSet(StringComparer.Ordinal);
            var prefix = target.Name.Value + "$forward";
            var name = prefix;
            for (int i = 0; !names.Add(name); ++i) name = prefix + i;
            var bridge = new BirFunctionDeclaration { Name = name, TypeParams = parameters, Params = formals.Select(p => (BirParameter)p.DeepClone()).ToArray(),
                Ret = ret, Static = false, Virtual = true, ClrFinal = true, Vis = "private", Generated = true,
                ClrInterfaceImpls = new[] { new BirMethodImpl { Owner = slotOwner, Member = member, Arity = parameters.Length,
                    Params = formals.Select(p => p.Type.Value).ToArray(), Ret = ret } } };
            owner.Methods = owner.Methods.Value.Append(bridge).ToArray();
            return bridge;
        }
        var exact = Bridge(implemented, invoke.Name.Value, Strengthen(anchors.Select(a => a.Bound).ToArray()));
        var weak = Bridge(selectedOwner, slot.Name, weakRows);
        if (referenceBuild) { exact.Body = RefBodySquash.SourceThrowStubBody(); weak.Body = RefBodySquash.SourceThrowStubBody(); return; }
        TypeNode FromPrimary(int i)
        {
            var role = destination.PhysicalSlot(i);
            var pos = Enumerable.Range(0, selected.PhysicalArity).FirstOrDefault(j => selected.PhysicalSlot(j) == role, -1);
            return new TypeNode.Tv("method", pos >= 0 ? pos : selected.SourcePosition(role.SourceIndex));
        }
        var plan = entry.Plan;
        var args = Enumerable.Range(0, plan.PrimaryArity).Select(FromPrimary).Concat(plan.Facets.Select(f => FromPrimary(f.OwnerIndex)))
            .Concat(plan.NativeIndices.Select(FromPrimary)).Concat(plan.AddressOperands.Select(i => ((TypeNode.ByRef)SignatureValueTypes.Of(formals[i].Type.Value)).Of)).ToArray();
        exact.Body = ForwardBody(owner, exact, target, args);
        weak.Body = ForwardBody(viewed, new BirCast { Type = viewed, E = new BirThis(), Sty = viewed },
            weak, invoke, Enumerable.Range(0, rows.Length).Select(i => (TypeNode)new TypeNode.Tv("method", i)).ToArray());
    }
}
