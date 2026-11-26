using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

sealed partial class ProjectedCallableEntries
{
    internal void BindStorageOverrides(BirClassDefinition exact, BirClassDefinition storage,
        FBoundStarProjectionErasure.ProjectedStorageOwner plan, IReadOnlyDictionary<string, string> localCarriers)
    {
        if (plan.Overrides.Length == 0 || exact.Interfaces.OrEmpty().SequenceEqual(storage.Interfaces.OrEmpty())) return;
        var methods = storage.Methods.OrEmpty();
        var bodies = methods.Where(m => m.RoundtripDeclarationIdentity.OrDefault()?.DeclarationId.OrDefault() != null)
            .ToDictionary(m => m.RoundtripDeclarationIdentity.Value.DeclarationId.Value, StringComparer.Ordinal);
        TypeNode Key(TypeNode t) => ClrMemberResolution.MethodImplComparisonType(t, references);
        HashSet<TypeNode> Faces(BirClassDefinition owner) => SupertypeGraph.Reachable(
            new SupertypeGraph.Def { Base = (TypeNode.Fqn)owner.Base.OrDefault(), Interfaces = owner.Interfaces.OrEmpty().Cast<TypeNode.Fqn>().ToArray() },
            classHierarchy.Value, references).Where(e => e.isInterface).Select(e => Key(e.spec)).ToHashSet();
        var surviving = Faces(storage);
        var removed = Faces(exact);
        removed.ExceptWith(surviving);
        if (removed.Count == 0) return;
        ReferencedDeclarationSlot Slot(string id, bool projected)
        {
            if (projected && localSlots.TryGetValue(id, out var companion)) return companion;
            if (!projected && originalSlots.TryGetValue(id, out var local))
                return Local(local.Owner, local.Method);
            return references.TryDeclarationSignatureSlot(projected ? EntryId(id) : id, out var slot) ? slot : null;
        }
        static ReferencedDeclarationSlot Local(IBirTypeDeclaration owner, BirFunctionDeclaration m) =>
            new(owner.Name.Value, m.Name.Value, m.TypeParams.OrEmpty().Length,
                m.Params.OrEmpty().Select(FunctionSignatureIdentity.SignatureType).ToArray(), m.Ret.Value,
                owner.Kind.OrDefault() == "interface", false);
        foreach (var edge in plan.Overrides)
        {
            var semantic = edge.ParentOwner;
            if (!localCarriers.TryGetValue(semantic, out var carrier))
            {
                if (!references.TryExistentialPhysicalOwner(semantic, out carrier) ||
                    !references.TryExistentialSemanticOwner(carrier, out semantic)) continue;
            }
            if (!surviving.Contains(Key(new TypeNode.Fqn(carrier))) || !bodies.TryGetValue(edge.Body, out var method)
                || Slot(edge.Parent, false) is not { IsInterface: true } declaration) continue;
            var applications = SelectedConstructions(classHierarchy.Value, exact, method, edge.Parent, declaration.Owner,
                originalSlots.ContainsKey(edge.Parent) ? null : edge.Parent).Where(f => removed.Contains(Key(f))).ToArray();
            if (applications.Length == 0) continue;
            var parentSlot = FBoundStarProjectionErasure.ExistentialSlotIdentity(edge.Parent, semantic);
            Bind(edge.Body, edge.OwnSlot, parentSlot, edge.Parent, applications);
            if (edge.Suspend) Bind(edge.Body + "|cold", edge.OwnSlot + "|cold", parentSlot + "|cold", edge.Parent + "|cold", applications);
        }

        void Bind(string bodyId, string ownId, string parentId, string exactId, TypeNode.Fqn[] applications)
        {
            BindEntry(bodyId, ownId, parentId, exactId, applications, false);
            BindEntry(EntryId(bodyId), ownId, parentId, exactId, applications, true);
        }
        void BindEntry(string bodyId, string ownId, string parentId, string exactId, TypeNode.Fqn[] applications, bool projected)
        {
            if (!bodies.TryGetValue(bodyId, out var body) || Slot(parentId, projected) is not { } parent) return;
            var localSlot = Slot(ownId, projected);
            if (localSlot == null) return;
            var exactSlot = Slot(exactId, projected);
            BirMethodImpl Descriptor(ReferencedDeclarationSlot slot) => new() {
                Owner = new TypeNode.Fqn(slot.Owner), Member = slot.Name, Arity = slot.Arity, Params = slot.Parameters, Ret = slot.Return
            };
            bool IsSlot(BirMethodImpl row, ReferencedDeclarationSlot slot, TypeNode.Fqn instance = null)
            {
                instance ??= new TypeNode.Fqn(slot.Owner);
                TypeNode Close(TypeNode t) => Key(SupertypeGraph.SubstOwnerTvs(t, instance.Args ?? []));
                return row.Owner.OrDefault() is TypeNode.Fqn f && Key(f) == Key(instance)
                    && row.Member.Value == slot.Name && row.Arity.OrDefault() == slot.Arity
                    && row.Params.OrEmpty().Select(Key).SequenceEqual(slot.Parameters.Select(Close)) && Key(row.Ret.Value) == Close(slot.Return);
            }
            bool Matches(BirFunctionDeclaration method, ReferencedDeclarationSlot slot) =>
                method.TypeParams.OrEmpty().Length == slot.Arity && Key(method.Ret.Value) == Key(slot.Return) &&
                method.Params.OrEmpty().Select(p => Key(FunctionSignatureIdentity.SignatureType(p))).SequenceEqual(slot.Parameters.Select(Key));
            var previous = methods.Where(m => m.ExistentialCallable == ExistentialCallableRole.Forwarder &&
                m.ClrInterfaceImpls.OrEmpty().Any(r => IsSlot(r, localSlot))).ToArray();
            if (exactSlot != null)
                foreach (var m in methods.Where(m => m.ClrInterfaceImpls.HasValue))
                    m.ClrInterfaceImpls = m.ClrInterfaceImpls.Value.Where(r => !applications.Any(f => IsSlot(r, exactSlot, f))).ToArray();
            // A copied suspend adapter can itself call the erased cold slot. Bind
            // that slot to the authored body; retain the selected adapter when
            // its ABI actually converts values (e.g. distinct closed Task<T>s).
            foreach (var slot in new[] { localSlot, parent })
            {
                var target = Matches(body, slot) ? body : previous.SingleOrDefault(m => Matches(m, slot));
                if (target == null) throw new InvalidOperationException($"No compatible implementation of projected storage slot '{slot.Owner}.{slot.Name}'");
                foreach (var bridge in previous)
                    if (bridge != target) bridge.ClrInterfaceImpls = bridge.ClrInterfaceImpls.OrEmpty().Where(r => !IsSlot(r, slot)).ToArray();
                if (!target.ClrInterfaceImpls.OrEmpty().Any(r => IsSlot(r, slot)))
                    target.ClrInterfaceImpls = target.ClrInterfaceImpls.OrEmpty().Append(Descriptor(slot)).ToArray();
                if (!target.Virtual.OrDefault()) { target.Virtual = true; target.ClrFinal = true; }
            }
        }
    }
}
