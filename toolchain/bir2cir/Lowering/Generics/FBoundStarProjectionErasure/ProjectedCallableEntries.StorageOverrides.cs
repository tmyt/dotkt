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
        TypeNode.Fqn[] Faces(BirClassDefinition owner) => SupertypeGraph.Reachable(
            new SupertypeGraph.Def { Base = (TypeNode.Fqn)owner.Base.OrDefault(), Interfaces = owner.Interfaces.OrEmpty().Cast<TypeNode.Fqn>().ToArray() },
            classHierarchy.Value, references).Where(e => e.isInterface).Select(e => e.spec).ToArray();
        bool Declares(TypeNode.Fqn f, string name) => Key(f) == Key(f with { Name = name });
        var storageFaces = Faces(storage);
        var surviving = storageFaces.Select(Key).ToHashSet();
        var removed = Faces(exact).Select(Key).ToHashSet();
        removed.ExceptWith(surviving);
        if (removed.Count == 0) return;
        var wired = new HashSet<(string Body, string Slot, TypeNode Owner)>();
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
                || Slot(edge.Parent, false) is not { IsInterface: true } declaration
                || !removed.OfType<TypeNode.Fqn>().Any(f => Declares(f, declaration.Owner))) continue;
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
            BirMethodImpl Descriptor(ReferencedDeclarationSlot slot, TypeNode.Fqn f) => new() {
                Owner = f, Member = slot.Name, Arity = slot.Arity, Params = slot.Parameters.Select(t => SupertypeGraph.SubstOwnerTvs(t, f.Args ?? [])).ToArray(),
                Ret = SupertypeGraph.SubstOwnerTvs(slot.Return, f.Args ?? [])
            };
            bool IsSlot(BirMethodImpl row, ReferencedDeclarationSlot slot, TypeNode.Fqn instance = null)
            {
                instance ??= new TypeNode.Fqn(slot.Owner);
                TypeNode Close(TypeNode t) => Key(SupertypeGraph.SubstOwnerTvs(t, instance.Args ?? []));
                return row.Owner.OrDefault() is TypeNode.Fqn f && Key(f) == Key(instance)
                    && row.Member.Value == slot.Name && row.Arity.OrDefault() == slot.Arity
                    && row.Params.OrEmpty().Select(Key).SequenceEqual(slot.Parameters.Select(Close)) && Key(row.Ret.Value) == Close(slot.Return);
            }
            bool Matches(BirFunctionDeclaration method, BirMethodImpl slot) =>
                method.TypeParams.OrEmpty().Length == slot.Arity.Value && Key(method.Ret.Value) == Key(slot.Ret.Value) &&
                method.Params.OrEmpty().Select(p => Key(FunctionSignatureIdentity.SignatureType(p))).SequenceEqual(slot.Params.Value.Select(Key));
            var locals = storageFaces.Where(f => Declares(f, localSlot.Owner)).ToArray();
            var previous = methods.Where(m => m.ExistentialCallable == ExistentialCallableRole.Forwarder &&
                m.ClrInterfaceImpls.OrEmpty().Any(r => locals.Any(f => IsSlot(r, localSlot, f)))).ToArray();
            var inherited = (from a in applications from m in methods
                where exactSlot != null && m.ClrInterfaceImpls.OrEmpty().Any(r => IsSlot(r, exactSlot, a))
                select (Application: a, Method: m)).ToArray();
            if (exactSlot != null)
                foreach (var m in methods.Where(m => m.ClrInterfaceImpls.HasValue))
                    m.ClrInterfaceImpls = m.ClrInterfaceImpls.Value.Where(r => !applications.Any(f => IsSlot(r, exactSlot, f))).ToArray();
            // A copied suspend adapter can itself call the erased cold slot. Bind
            // that slot to the authored body; retain the selected adapter when
            // its ABI actually converts values (e.g. distinct closed Task<T>s).
            var parents = applications.SelectMany(a => SupertypeGraph.ReachableFromClass(a, classHierarchy.Value, references))
                .Where(e => e.isInterface && Declares(e.spec, parent.Owner)).Select(e => e.spec).DistinctBy(Key).ToArray();
            foreach (var (slotId, slot, f) in locals.Select(f => (ownId, localSlot, f)).Concat(parents.Select(f => (parentId, parent, f))))
            {
                if (!wired.Add((bodyId, slotId, Key(f)))) continue;
                if (surviving.Add(Key(f))) storage.Interfaces = storage.Interfaces.OrEmpty().Append(f).ToArray();
                var descriptor = Descriptor(slot, f);
                if (!projected && slotId == parentId && Find(slotId) is { } parentEntry && Slot(slotId, true) is { } projectedSlot &&
                    KotlinOverrideSlotBridge.MethodTypeParameterShapeKey(parentEntry.Original, f.Args, references.WitnessConstraintKey) !=
                    KotlinOverrideSlotBridge.MethodTypeParameterShapeKey(body.TypeParams.OrEmpty(), [], references.WitnessConstraintKey))
                {
                    // The parent need not expose every constraint of the implementation.
                    // Its ordinary facade supplies its own proof to its projected slot.
                    var rows = parentEntry.Original.Select(p => CapturedGenericInvocation.CloneParameter(p,
                        t => SupertypeGraph.SubstOwnerTvs(t, f.Args ?? []))).ToArray();
                    var facade = NewSlotForwarder(storage, body, descriptor, rows, isInterface: true);
                    var companion = Descriptor(projectedSlot, f);
                    var callee = new BirFunctionDeclaration { Name = projectedSlot.Name, Ret = companion.Ret,
                        Params = companion.Params.Value.Select(t => new BirParameter { Type = t }).ToArray() };
                    var selected = localSlots.ContainsKey(slotId) ? null : references.SelectedPhysicalMethod(EntryId(slotId));
                    var member = selected == null ? null : BirMemberReference.FromPhysical(ClrMemberResolution.MemberRefOf(
                        null, references, selected, "method", selected.DeclaringType, f.Args));
                    facade.Body = referenceBuild ? RefBodySquash.SourceThrowStubBody(references) : ForwardBody(f, new BirThis(),
                        facade, callee, ExactFacadeArguments(facade, parentEntry.Plan), member);
                    continue;
                }
                var target = Matches(body, descriptor) ? body : previous.SingleOrDefault(m => Matches(m, descriptor));
                ProjectedMethodPlan selectedPlan = null;
                if (projected && slotId == parentId)
                    foreach (var (application, selected) in inherited)
                    {
                        if (MethodEntry(selected) != null || !Matches(selected, descriptor) ||
                            !SupertypeGraph.ReachableFromClass(application, classHierarchy.Value, references)
                                .Any(e => e.isInterface && Key(e.spec) == Key(f))) continue;
                        // Reuse the selected MethodImpl: it already transports witnesses
                        // that this interface, unlike the implementation, does not expose.
                        target = selected;
                        selectedPlan = Find(exactId).Plan.OnOwner(application.Args ?? []);
                        break;
                    }
                if (target == null) throw new InvalidOperationException($"No compatible implementation of projected storage slot '{slot.Owner}.{slot.Name}'");
                foreach (var bridge in previous)
                    if (bridge != target) bridge.ClrInterfaceImpls = bridge.ClrInterfaceImpls.OrEmpty().Where(r => !IsSlot(r, slot, f)).ToArray();
                if (projected)
                {
                    var contract = Find(slotId).Plan.OnOwner(f.Args ?? []);
                    var destination = selectedPlan ?? MethodEntry(target).Plan;
                    if (contract.ForwardTo(destination, references.WitnessConstraintKey) is { } arguments)
                    {
                        target.ClrInterfaceImpls = target.ClrInterfaceImpls.OrEmpty().Where(r => !IsSlot(r, slot, f)).ToArray();
                        AddSlotForwarder(storage, target, descriptor, contract, arguments, isInterface: true, slotId);
                        continue;
                    }
                }
                if (!target.ClrInterfaceImpls.OrEmpty().Any(r => IsSlot(r, slot, f)))
                    target.ClrInterfaceImpls = target.ClrInterfaceImpls.OrEmpty().Append(descriptor).ToArray();
                if (!target.Virtual.OrDefault()) { target.Virtual = true; target.ClrFinal = true; }
            }
        }
    }
}
