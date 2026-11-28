using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

sealed partial class ProjectedCallableEntries
{
    readonly HashSet<string> operationTypes = new(StringComparer.Ordinal);
    readonly Dictionary<string, (int OwnerRow, int Input, TypeNode Bound)> upperOperations = new(StringComparer.Ordinal);

    internal void CloseUpperOperations(BirClassDefinition storage, FBoundStarProjectionErasure.ProjectedStorageOwner plan)
    {
        if (!storage.Interfaces.OrEmpty().OfType<TypeNode.Fqn>().Any(f => upperOperations.ContainsKey(f.Name))) return;
        var rows = TypeParameterFrame.Declarations(storage).ToArray();
        TypeNode.Fqn Close(TypeNode.Fqn f, BirFunctionDeclaration method = null)
        {
            if (!upperOperations.TryGetValue(f.Name, out var op)) return f;
            bool Accepts(TypeNode t) => t == op.Bound || Proves(t, op.Bound);
            if (rows[op.OwnerRow].Constraints.OrEmpty().Any(Accepts)) return f;
            var n = Array.FindIndex(plan.Slots, s => s.OwnerIndex == op.OwnerRow && (s.Evidence ?? [s.Bound]).Any(Accepts));
            if (n < 0) return null;
            var witness = new TypeNode.Tv("type", plan.Entry.PrimaryArity + n);
            if (method != null)
            {
                var input = method.TypeParams.Value[op.Input];
                input.Constraints = input.Constraints.Value.Select(t => t == f.Args[^1] ? witness : t).Distinct().ToArray();
            }
            return f with { Args = f.Args[..^1].Append(witness).ToArray() };
        }
        storage.Interfaces = storage.Interfaces.OrEmpty().Select(t => t is TypeNode.Fqn f ? Close(f) : t).Where(t => t != null).ToArray();
        storage.Methods = storage.Methods.OrEmpty().Where(m => {
            if (m.ClrInterfaceImpls.OrEmpty() is not [var d] || d.Owner.OrDefault() is not TypeNode.Fqn f ||
                !upperOperations.ContainsKey(f.Name)) return true;
            if (Close(f, m) is not {} application) return false;
            d.Owner = application;
            return true;
        }).ToArray();
    }
    readonly HashSet<BirMethodImpl> replacedAdapterSlots = new(ReferenceEqualityComparer.Instance);
    readonly Dictionary<BirMethodImpl, HashSet<string>> replacedLocalSlots = new(ReferenceEqualityComparer.Instance);

    // Earlier slot synthesis may already have supplied a forwarder for the
    // selected declaration. Replace only that assignment; a bridge
    // shared with another interface or base slot still has work to do.
    void RetireSlotForwarder(IBirTypeDeclaration owner, TypeNode.Fqn application, ReferencedDeclarationSlot slot, string id)
    {
        TypeNode Close(TypeNode t) => SupertypeGraph.SubstOwnerTvs(t, application.Args ?? []);
        bool Matches(BirMethodImpl directive) => directive.Owner.OrDefault() == application &&
            directive.Member.OrDefault() == slot.Name && directive.Arity.OrDefault() == slot.Arity &&
            directive.Params.OrEmpty().SequenceEqual(slot.Parameters.Select(Close)) && directive.Ret.OrDefault() == Close(slot.Return);
        var dim = slot.IsInterface && owner.Kind.OrDefault() == "interface";
        foreach (var bridge in owner.Methods.OrEmpty().Where(m => m.Generated.OrDefault() &&
            (m.OverrideAdapterGroup != null || dim && m.ClrInterfaceSlotBridge.OrDefault())))
        {
            var old = slot.IsInterface ? bridge.ClrInterfaceImpls.OrEmpty() : bridge.ClrBaseImpls.OrEmpty();
            if (bridge.OverrideAdapterGroup != null)
            {
                foreach (var d in old.Where(d => d.Owner.OrDefault() == application))
                    if (d.LocalOverrideTargets is { Count: > 0 } targets)
                    {
                        // Local overloads can acquire modifiers after adapter synthesis.
                        if (!targets.Contains(id)) continue;
                        if (!replacedLocalSlots.TryGetValue(d, out var retired)) replacedLocalSlots[d] = retired = new(StringComparer.Ordinal);
                        retired.Add(id);
                    }
                    else if (Matches(d)) replacedAdapterSlots.Add(d);
                continue;
            }
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
            // The physical entry supplies the slot, but the source declaration
            // owns the ordinary/nullable/storage frame shared by its entries.
            selectedFrame = references.NullableMethodFrame(contract?.RepresentationDeclaration
                ?? target.OverrideRepresentationDeclarations?.GetValueOrDefault(id) ?? id);
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
        if (selected.SourceArity != destination.SourceArity)
            throw new InvalidOperationException($"Selected override source arities disagree: {slot.Owner}.{slot.Name} ({selected.SourceArity}), {owner.Name.Value}.{target.Name.Value} ({destination.SourceArity})");
        var weakRows = contract?.CloneParameters() ?? rows.Select(p => CapturedGenericInvocation.CloneParameter(p, Close)).ToArray();
        if (projectedSlot != null) slot = projectedSlot;
        RetireSlotForwarder(owner, selectedOwner, slot, projectedSlot == null ? id : EntryId(id));
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
        int NativeInput(int targetIndex)
        {
            var primary = Position(targetIndex);
            if (contract != null && Array.IndexOf(contract.NativeIndices, primary) is var i && i >= 0)
                return contract.PrimaryArity + contract.Facets.Length + i;
            var storage = Position(targetIndex, true);
            var native = plan.Parameters[plan.PrimaryArity + plan.Facets.Length + Array.IndexOf(plan.NativeIndices, targetIndex)];
            // An interface-only storage formal does not prove a CLR reference
            // constraint. Use the reference-only primary in that case; the
            // operation bridge below retains any additional interface bounds.
            return native.SpecialConstraints.OrEmpty().Contains("class")
                && !weakRows[storage].SpecialConstraints.OrEmpty().Contains("class")
                && (Nominal(plan.Parameters[targetIndex]) || Nominal(weakRows[primary]) || weakRows[primary].SpecialConstraints.OrEmpty().Contains("class"))
                ? primary : storage;
        }
        int[] inputs = Enumerable.Range(0, plan.PrimaryArity).Select(i => Position(i))
            .Concat(plan.Facets.Select(FacetInput)).Concat(plan.NativeIndices.Select(NativeInput)).ToArray();
        var formals = slot.Parameters.Select((t, i) => new BirParameter { Name = target.Params.Value[i].Name, Type = Close(t) }).ToArray();
        var ret = Close(slot.Return);
        TypeNode.Tv Var(int i) => new("method", i);
        TypeNode[] Arguments() => inputs.Select(i => (TypeNode)Var(i))
            .Concat(plan.AddressOperands.Select(i => ((TypeNode.ByRef)SignatureValueTypes.Of(formals[i].Type.Value)).Of))
            .Concat(plan.ReferenceIndices.Select(i => contract?.ReferencePosition(Position(i)) is >= 0 and var r
                ? (TypeNode)Var(r) : new TypeNode.Fqn("System.Object")))
            .Concat(plan.UpperArguments((i, material) => (TypeNode)Var(inputs[material ? ((TypeNode.Tv)plan.NativeArgument(Var(i))).I : i]))).ToArray();
        var weak = Bridge(selectedOwner, slot.Name, weakRows);
        if (NeedsTaskView(target, ret, Arguments()))
        {
            weak.Body = referenceBuild ? RefBodySquash.SourceThrowStubBody(references) :
                ProjectedTaskBody(owner, weak, target, selectedOwner, contract, id);
            return;
        }
        var optional = plan.NativeIndices.Select((i, n) => (Target: i, Primary: Position(i), Native: plan.PrimaryArity + plan.Facets.Length + n,
            Reference: contract?.ReferencePosition(Position(i)) ?? -1))
            .Where(p => p.Reference >= 0 && plan.Parameters[p.Native].SpecialConstraints.OrEmpty().Contains("class")).ToArray();
        (int Position, TypeNode Bound)[] Missing(BirTypeParameter[] parameters, IEnumerable<int> targetRows)
        {
            var proofs = new List<(int, TypeNode)>();
            foreach (var row in targetRows)
            foreach (var bound in plan.Parameters[row].Constraints.OrEmpty().OfType<TypeNode.Fqn>())
            {
                var required = TypedExecutableFrames.RewriteType(bound, v => v.Scope == "method" && v.I < inputs.Length ?
                    Var(inputs[v.I]) : null);
                if (parameters[inputs[row]].Constraints.OrEmpty().Any(c => Proves(c, required))) continue;
                bool open = false;
                BirGraphNameFields.VisitTypeNodes(required, t => open |= t is TypeNode.Tv { Scope: "method" });
                if (!open) proofs.Add((inputs[row], required));
            }
            return proofs.Distinct().ToArray();
        }
        // Each optional material costs one forwarding step, not a copy of the
        // Kotlin body for every combination. Establish the nominal proofs first
        // so a true CLR entry can use its exact primary even for an open caller.
        var receiver = Enrich(weak, Missing(weakRows, optional.Select(p => p.Target)));
        int Choose(IBirExpression condition, int yes, int no, bool reference)
        {
            var parameters = receiver.TypeParams.Value;
            var next = Bridge(null, null, parameters.Select(row => (BirTypeParameter)row.DeepClone())
                .Append(new BirTypeParameter { Name = "$chosen" + parameters.Length,
                    SpecialConstraints = reference ? new[] { "class" } : Array.Empty<string>() }).ToArray());
            var frame = Enumerable.Range(0, parameters.Length).Select(i => (TypeNode)Var(i)).ToArray();
            if (referenceBuild) receiver.Body = RefBodySquash.SourceThrowStubBody(references);
            else
            {
                IBirStatement[] With(int i) => ForwardBody(owner, receiver, next, frame.Append(Var(i)).ToArray());
                receiver.Body = new IBirStatement[] { new BirIf { Branches = new[] { new BirBranch {
                    Cond = OptionalField<IBirExpression>.Of(condition), Body = With(yes) } } } }.Concat(With(no)).ToArray();
            }
            receiver = next;
            return parameters.Length;
        }
        foreach (var p in optional)
        {
            var test = new BirBinaryOperation { Op = "===", Lhs = new BirClassReference { Type = Var(inputs[p.Native]) },
                Rhs = new BirClassReference { Type = Var(p.Reference) } };
            inputs[p.Native] = Choose(test, p.Reference, p.Primary, true);
            // A fixed CLR entry may have supplied erased Kotlin storage. It
            // remains preferable to the exact material when it actually meets
            // this implementation's storage bounds (e.g. Leaf$star vs Leaf<int>).
            var storage = Position(p.Target, true);
            if (storage == p.Primary) continue;
            var uses = Enumerable.Range(0, inputs.Length).Where(i => inputs[i] == storage).ToArray();
            var requirements = Missing(receiver.TypeParams.Value, uses);
            if (requirements.Length == 0) continue;
            var type = new TypeNode.Fqn("System.Type");
            var boolean = new TypeNode.Fqn("System.Boolean");
            var operation = ClrMemberResolution.PlanArrayStorageCall(null, references,
                "IsAssignableFrom", ["System.Type"], "System.Type", false);
            var member = BirMemberReference.FromPhysical(operation.Reference);
            IBirExpression accepts = null;
            foreach (var (_, bound) in requirements)
            {
                var check = new BirCallInstance { OwnerType = type, Method = "IsAssignableFrom", Virtual = true,
                    Recv = new BirClassReference { Type = bound }, Args = new IBirExpression[] { new BirClassReference { Type = Var(storage) } },
                    Sig = member.ParameterTypes.Value, Ret = boolean, CalleeRet = member.ReturnType.Value,
                    CalleeParams = member.ParameterTypes.Value, MemberRef = member };
                accepts = accepts == null ? check : new BirConditional { Type = boolean, Cond = check,
                    Then = OptionalField<IBirExpression>.Of(accepts), Else = new BirConstant { Type = boolean, Value = new BirLiteral.BooleanLiteral(false) } };
            }
            var chosen = Choose(accepts, storage, inputs[p.Native], false);
            foreach (int i in uses) inputs[i] = chosen;
        }
        var body = Enrich(receiver, Missing(receiver.TypeParams.Value, Enumerable.Range(0, inputs.Length)));
        body.Body = referenceBuild ? RefBodySquash.SourceThrowStubBody(references) : ForwardBody(owner, body, target, Arguments());

        BirFunctionDeclaration Enrich(BirFunctionDeclaration caller, (int Position, TypeNode Bound)[] anchors)
        {
            if (anchors.Length == 0) return caller;
            var ownerRows = TypeParameterFrame.Declarations(owner).ToArray();
            var selectedEdges = local.Method != null ? local.Method.OwnerDependentSourceSlots
                : references.SelectedOwnerUpperSlots(id, selectedOwner.Name);
            var layout = local.Method == null ? references.NullableTypeFrames.GetValueOrDefault(selectedOwner.Name) : null;
            var rigid = new List<(int, TypeNode)>();
            var dependent = new List<((int Position, TypeNode Bound) Anchor, int Upper, TypeNode Owner)>();
            foreach (var a in anchors)
            {
                (int Upper, TypeNode Owner)? relation = null;
                foreach (var i in plan.ReferenceIndices)
                foreach (bool material in new[] { false, true })
                {
                    var index = material ? ((TypeNode.Tv)plan.NativeArgument(Var(i))).I : i;
                    if (inputs[index] != a.Position || contract?.UpperPosition(Position(i), material) is not (>= 0 and var u)) continue;
                    var source = destination.PhysicalSlot(i).SourceIndex;
                    var declared = (selectedEdges?.GetValueOrDefault(source) ?? []).Select(o => layout?.SourcePosition(o) ?? o)
                        .Select(o => physicalOwner.ElementAtOrDefault(o));
                    foreach (var candidate in declared.Concat((target.OwnerDependentSourceSlots?.GetValueOrDefault(source) ?? [])
                        .Select(o => (TypeNode)new TypeNode.Tv("type", o))))
                    {
                        var proofs = candidate is TypeNode.Tv { Scope: "type" } v ? ownerRows.ElementAtOrDefault(v.I)?.Constraints.OrEmpty()
                            : candidate is TypeNode.Fqn ? [candidate] : null;
                        if (proofs?.Any(c => c == a.Bound || Proves(c, a.Bound)) == true)
                        { relation = (u, candidate); break; }
                    }
                }
                if (relation is {} r) dependent.Add((a, r.Upper, r.Owner)); else rigid.Add(a);
            }
            if (rigid.Count != 0) caller = Step(caller, rigid.ToArray());
            // Variance never converts Apply<IFace> to Apply<ValueStruct>.
            // When B : V, invoke through Apply<V> with B unchanged. Handle
            // each independent bound in turn rather than cloning the body for
            // every combination of reference and value arguments.
            foreach (var d in dependent) caller = Step(caller, [d.Anchor], d.Upper, d.Owner);
            return caller;
        }
        BirFunctionDeclaration Step(BirFunctionDeclaration caller, (int Position, TypeNode Bound)[] anchors, int upper = -1, TypeNode own = null)
        {
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
            foreach (var p in caller.TypeParams.Value) foreach (var bound in p.Constraints.OrEmpty()) Capture(bound);
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
                var result = caller.TypeParams.Value.Select(p => inOperation ? RebindRow(p) : (BirTypeParameter)p.DeepClone()).ToArray();
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
            var implementation = Bridge(implemented, invoke.Name.Value, Strengthen(anchors.Select(a => a.Bound).ToArray()));
            var pass = Enumerable.Range(0, caller.TypeParams.Value.Length).Select(i => (TypeNode)Var(i)).ToArray();
            IBirStatement[] Through(TypeNode.Fqn view) => ForwardBody(view, new BirCast { Type = view, E = new BirThis(), Sty = view }, caller,
                invoke with { Params = formals, Ret = ret }, pass);
            caller.Body = referenceBuild ? RefBodySquash.SourceThrowStubBody(references) : Through(viewed);
            if (upper >= 0)
            {
                // Two distinct operation definitions: V may itself be the
                // fallback interface, so their closed MethodImpls can unify.
                var upperName = opName + "$upper";
                for (int n = 0; kinds.ContainsKey(upperName) || !operationTypes.Add(upperName); n++) upperName = opName + "$upper" + n;
                var viaOwner = new TypeNode.Fqn(upperName, inContext.Append(own).ToArray());
                var viaUpper = new TypeNode.Fqn(upperName, inContext.Append(Var(upper)).ToArray());
                file.Types = file.Types.Value.Append((BirInterfaceDefinition)op.DeepClone() with { Name = upperName }).ToArray();
                owner.Interfaces = owner.Interfaces.Value.Append(viaOwner).ToArray();
                var proved = Bridge(viaOwner, invoke.Name.Value, Strengthen([own]));
                proved.Body = referenceBuild ? RefBodySquash.SourceThrowStubBody(references) : ForwardBody(owner, proved, implementation, pass);
                if (own is TypeNode.Tv { Scope: "type" } v)
                    upperOperations.Add(upperName, (v.I, anchors[0].Position, anchors[0].Bound));
                if (!referenceBuild)
                    caller.Body = new IBirStatement[] { new BirIf { Branches = new[] { new BirBranch {
                        Cond = OptionalField<IBirExpression>.Of(new BirIsInstance { Type = viaUpper, E = new BirThis() }),
                        Body = Through(viaUpper) } } } }.Concat(caller.Body.Value).ToArray();
            }
            return implementation;
        }
        BirFunctionDeclaration Bridge(TypeNode.Fqn slotOwner, string member, BirTypeParameter[] parameters)
        {
            var names = owner.Methods.Value.Select(m => m.Name.OrDefault()).ToHashSet(StringComparer.Ordinal);
            var prefix = target.Name.Value + "$forward";
            var name = prefix;
            for (int i = 0; !names.Add(name); ++i) name = prefix + i;
            var bridge = new BirFunctionDeclaration { Name = name, TypeParams = parameters, Params = formals.Select(p => (BirParameter)p.DeepClone()).ToArray(),
                Ret = ret, Static = false, Abstract = false, Virtual = slotOwner != null, ClrFinal = slotOwner != null, Vis = "private", Generated = true,
                ClrInterfaceImpls = slotOwner == null ? default(OptionalField<BirMethodImpl[]>) : new[] { new BirMethodImpl { Owner = slotOwner, Member = member, Arity = parameters.Length,
                    Params = formals.Select(p => p.Type.Value).ToArray(), Ret = ret } } };
            owner.Methods = owner.Methods.Value.Append(bridge).ToArray();
            if (slotOwner != null && owner.Kind.OrDefault() == "interface") bridge.ClrInterfaceSlotBridge = true;
            return bridge;
        }
    }
}
