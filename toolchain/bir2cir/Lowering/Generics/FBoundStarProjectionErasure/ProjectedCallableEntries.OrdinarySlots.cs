using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

sealed partial class ProjectedCallableEntries
{
    readonly HashSet<string> operationTypes = new(StringComparer.Ordinal);

    // Slot synthesis can already have supplied a forwarding DIM. Once the
    // selected slot is replaced, retire exactly its descriptor. A bridge
    // shared with a different slot still has that other job to do.
    void RetireInterfaceForwarder(IBirTypeDeclaration owner, TypeNode.Fqn application, ReferencedDeclarationSlot slot)
    {
        if (owner.Kind.OrDefault() != "interface") return;
        TypeNode Close(TypeNode t) => SupertypeGraph.SubstOwnerTvs(t, application.Args ?? []);
        bool Matches(BirMethodImpl directive) => directive.Owner.OrDefault() == application &&
            directive.Member.OrDefault() == slot.Name && directive.Arity.OrDefault() == slot.Arity &&
            directive.Params.OrEmpty().SequenceEqual(slot.Parameters.Select(Close)) && directive.Ret.OrDefault() == Close(slot.Return);
        foreach (var bridge in owner.Methods.OrEmpty().Where(m => m.Generated.OrDefault() && m.ClrInterfaceSlotBridge.OrDefault()))
        {
            var old = bridge.ClrInterfaceImpls.OrEmpty();
            var active = old.Where(d => !Matches(d)).ToArray();
            if (old.Length == active.Length) continue;
            if (active.Length == 0) owner.Methods = owner.Methods.Value.Where(m => !ReferenceEquals(m, bridge)).ToArray();
            else bridge.ClrInterfaceImpls = active;
        }
    }

    // A selected interface MethodDef can lack the projected body's companion rows.
    // Its caller nevertheless supplied the exact method argument, not its upper
    // bound. A contravariant operation transports only the missing constraints,
    // keeping the nominal and material arguments in their selected positions.
    void DeclareOrdinaryInterfaceBridge(BirFileDeclaration[] files, IReadOnlyDictionary<string, SupertypeGraph.Def> hierarchy,
        IBirTypeDeclaration owner, BirFunctionDeclaration target, Entry entry, string id,
        ReferencedDeclarationSlot projectedSlot = null, ProjectedMethodPlan contract = null, TypeNode.Fqn selectedOwner = null)
    {
        ReferencedDeclarationSlot slot;
        BirTypeParameter[] rows;
        NullableRepresentationFrame selectedFrame;
        if (originalSlots.TryGetValue(id, out var local))
        {
            if (local.Owner.Kind.OrDefault() != "interface") return;
            var m = local.Method;
            rows = m.TypeParams.OrEmpty();
            selectedFrame = RepresentationsFor(m);
            slot = new(local.Owner.Name.Value, m.Name.Value, rows.Length,
                m.Params.OrEmpty().Select(FunctionSignatureIdentity.SignatureType).ToArray(), m.Ret.Value, true, false);
        }
        else
        {
            if (!references.TryDeclarationSignatureSlot(id, out slot) || !slot.IsInterface) return;
            rows = references.PhysicalMethodTypeParametersInPhysicalFrame(id, nativeNames: true);
            selectedFrame = references.NullableMethodFrame(contract?.RepresentationDeclaration ?? id);
        }
        if (rows is not { Length: > 0 } || selectedFrame == null || !selectedFrame.StorageIndices.Any()) return;
        if (selectedOwner == null)
        {
            foreach (var application in SelectedConstructions(hierarchy, owner, target, id, slot.Owner, local.Method == null ? id : null))
                DeclareOrdinaryInterfaceBridge(files, hierarchy, owner, target, entry, id, projectedSlot, contract, application);
            return;
        }
        var physicalOwner = selectedOwner.Args ?? [];
        TypeNode Close(TypeNode t) => SupertypeGraph.SubstOwnerTvs(t, physicalOwner);
        var selected = selectedFrame;
        var destination = entry.Representations ?? new NullableRepresentationFrame(entry.Original.Length, []);
        if (selected.SourceArity != destination.SourceArity) throw new InvalidOperationException("Selected override source arities disagree");
        var weakRows = contract?.CloneParameters() ?? rows.Select(p => CapturedGenericInvocation.CloneParameter(p, Close)).ToArray();
        if (projectedSlot != null) slot = projectedSlot;
        RetireInterfaceForwarder(owner, selectedOwner, slot);
        int Position(int index, bool material = false)
        {
            var role = destination.PhysicalSlot(index);
            return material && selected.StorageIndices.Contains(role.SourceIndex)
                ? selected.Variable(new TypeNode.Tv("method", role.SourceIndex), NullableRepresentationFrame.Role.Storage).I
                : selected.Variable(new TypeNode.Tv("method", role.SourceIndex), role.Representation).I;
        }
        var plan = entry.Plan;
        int FacetInput(ProjectedClassStorageFrame.WitnessSlot targetFacet)
        {
            int primary = Position(targetFacet.OwnerIndex);
            if (contract != null && Array.FindIndex(contract.Facets, f => f.OwnerIndex == primary &&
                references.WitnessConstraintKey(f.Bound) == references.WitnessConstraintKey(targetFacet.Bound)) is var i && i >= 0)
                return contract.PrimaryArity + i;
            return Position(targetFacet.OwnerIndex, true);
        }
        int NativeInput(int targetIndex) =>
            contract != null && Array.IndexOf(contract.NativeIndices, Position(targetIndex)) is var i && i >= 0
                ? contract.PrimaryArity + contract.Facets.Length + i : Position(targetIndex, true);
        int[] inputs = Enumerable.Range(0, plan.PrimaryArity).Select(i => Position(i))
            .Concat(plan.Facets.Select(FacetInput)).Concat(plan.NativeIndices.Select(NativeInput)).ToArray();
        var proofs = new List<(int Position, TypeNode Bound)>();
        for (int row = 0; row < inputs.Length; row++)
        {
            int position = inputs[row];
            foreach (var declared in plan.Parameters[row].Constraints.OrEmpty().OfType<TypeNode.Fqn>())
            {
                var required = TypedExecutableFrames.RewriteType(declared, v => v.Scope == "method" && v.I < inputs.Length ?
                    new TypeNode.Tv("method", inputs[v.I]) : null);
                if (weakRows[position].Constraints.OrEmpty().Any(c => Proves(c, required))) continue;
                bool dependsOnMethod = false;
                BirGraphNameFields.VisitTypeNodes(required, t => dependsOnMethod |= t is TypeNode.Tv { Scope: "method" });
                if (!dependsOnMethod) proofs.Add((position, required));
            }
        }
        var anchors = proofs.Distinct().ToArray();
        var formals = slot.Parameters.Select((t, i) => new BirParameter { Name = target.Params.Value[i].Name, Type = Close(t) }).ToArray();
        var ret = Close(slot.Return);
        var args = inputs.Select(i => (TypeNode)new TypeNode.Tv("method", i))
            .Concat(plan.AddressOperands.Select(i => ((TypeNode.ByRef)SignatureValueTypes.Of(formals[i].Type.Value)).Of)).ToArray();
        if (contract != null && NeedsTaskView(target, ret, args))
        {
            var view = Bridge(selectedOwner, slot.Name, weakRows);
            view.Body = referenceBuild ? RefBodySquash.SourceThrowStubBody(references) :
                ProjectedTaskBody(owner, view, target, selectedOwner, contract, id);
            return;
        }
        if (anchors.Length == 0)
        {
            var direct = Bridge(selectedOwner, slot.Name, weakRows);
            direct.Body = referenceBuild ? RefBodySquash.SourceThrowStubBody(references) : ForwardBody(owner, direct, target, args);
            return;
        }
        var file = files.Single(f => f.Types.OrEmpty().Contains(owner));
        var stem = owner.Name.Value + "$" + target.Name.Value + "$operation";
        var opName = stem;
        for (int n = 0; kinds.ContainsKey(opName) || !operationTypes.Add(opName); ++n) opName = stem + n;
        // This operation lives outside the owner and may also be used from its
        // split implementation. Capture the free owner variables of Apply, not
        // an unrelated exact class constraint its callers cannot prove.
        var ownerRows = TypeParameterFrame.Declarations(owner).ToArray();
        var free = new SortedSet<int>();
        void Capture(TypeNode type) => BirGraphNameFields.VisitTypeNodes(type, t => {
            if (t is TypeNode.Tv { Scope: "type" } v && free.Add(v.I))
                foreach (var bound in ownerRows[v.I].Constraints.OrEmpty()) Capture(bound);
        });
        Capture(ret);
        foreach (var p in formals) Capture(p.Type.Value);
        foreach (var p in weakRows) foreach (var bound in p.Constraints.OrEmpty()) Capture(bound);
        var indices = free.ToArray();
        TypeNode Rebind(TypeNode t) => TypedExecutableFrames.RewriteType(t, v =>
            v.Scope == "type" ? new TypeNode.Tv("type", Array.BinarySearch(indices, v.I)) : v);
        BirTypeParameter RebindRow(BirTypeParameter p) => p with { Constraints = p.Constraints.OrEmpty().Select(Rebind).ToArray() };
        var context = indices.Select(i => RebindRow(ownerRows[i]) with { Variance = default }).ToArray();
        TypeNode[] inContext = indices.Select(i => (TypeNode)new TypeNode.Tv("type", i)).ToArray();
        var operationRows = context.Concat(anchors.Select((_, i) => new BirTypeParameter { Name = "$bound" + i, Variance = "in" })).ToArray();
        var implemented = new TypeNode.Fqn(opName, inContext.Concat(anchors.Select(a => a.Bound)).ToArray());
        var viewed = new TypeNode.Fqn(opName, inContext.Concat(anchors.Select(a => (TypeNode)new TypeNode.Tv("method", a.Position))).ToArray());
        BirTypeParameter[] Strengthen(TypeNode[] arguments, bool inOperation = false)
        {
            var result = weakRows.Select(p => inOperation ? RebindRow(p) : (BirTypeParameter)p.DeepClone()).ToArray();
            for (int i = 0; i < anchors.Length; i++)
            {
                var p = result[anchors[i].Position];
                p.Constraints = p.Constraints.OrEmpty().Append(arguments[i]).Distinct().ToArray();
            }
            return result;
        }
        var invoke = new BirFunctionDeclaration { Name = "Apply", TypeParams = Strengthen(anchors.Select((_, i) => (TypeNode)new TypeNode.Tv("type", context.Length + i)).ToArray(), true),
            Params = formals.Select(p => p with { Type = Rebind(p.Type.Value) }).ToArray(), Ret = Rebind(ret), Abstract = true, Virtual = true, Static = false, Vis = "public", Generated = true };
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
                Ret = ret, Static = false, Abstract = false, Virtual = true, ClrFinal = true, Vis = "private", Generated = true,
                ClrInterfaceImpls = new[] { new BirMethodImpl { Owner = slotOwner, Member = member, Arity = parameters.Length,
                    Params = formals.Select(p => p.Type.Value).ToArray(), Ret = ret } } };
            owner.Methods = owner.Methods.Value.Append(bridge).ToArray();
            if (owner.Kind.OrDefault() == "interface") bridge.ClrInterfaceSlotBridge = true;
            return bridge;
        }
        var exact = Bridge(implemented, invoke.Name.Value, Strengthen(anchors.Select(a => a.Bound).ToArray()));
        var weak = Bridge(selectedOwner, slot.Name, weakRows);
        if (referenceBuild) { exact.Body = RefBodySquash.SourceThrowStubBody(references); weak.Body = RefBodySquash.SourceThrowStubBody(references); return; }
        exact.Body = ForwardBody(owner, exact, target, args);
        weak.Body = ForwardBody(viewed, new BirCast { Type = viewed, E = new BirThis(), Sty = viewed },
            weak, invoke with { Params = formals, Ret = ret }, Enumerable.Range(0, weakRows.Length).Select(i => (TypeNode)new TypeNode.Tv("method", i)).ToArray());
    }
}
