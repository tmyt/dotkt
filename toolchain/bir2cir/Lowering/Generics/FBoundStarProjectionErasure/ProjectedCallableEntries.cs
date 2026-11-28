using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;
using Scope = CallableWitnessScope;

// Exact Kotlin declarations keep their CLR constraints. Projected callers
// may select a companion body with separate class and interface witnesses.
// Both entries are published by the producer; callers use declaration IDs,
// never a search for a matching member name or the runtime type of a value.
sealed partial class ProjectedCallableEntries
{
    sealed record Entry(string Name, ProjectedMethodPlan Plan, BirTypeParameter[] Original,
        NullableRepresentationFrame Representations, TypeNode[] Signature);
    sealed record Body(IBirTypeDeclaration Owner, BirFunctionDeclaration Method, Entry Entry);
    readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);
    readonly Dictionary<string, (IBirTypeDeclaration Owner, BirFunctionDeclaration Method)> originalSlots = new(StringComparer.Ordinal);
    readonly Dictionary<BirFunctionDeclaration, ProjectedMethodPlan> declarationPlans = new(ReferenceEqualityComparer.Instance);
    readonly Dictionary<string, Entry> emittedEntries = new(StringComparer.Ordinal);
    readonly Dictionary<string, ReferencedDeclarationSlot> localSlots = new(StringComparer.Ordinal);
    readonly HashSet<string> exactFacades = new(StringComparer.Ordinal);
    readonly Dictionary<string, BirTypeParameter[]> ownerConstraints = new(StringComparer.Ordinal);
    readonly Dictionary<string, string> kinds;
    readonly Dictionary<string, BirClassDefinition> classes;
    readonly ReferenceMetadataIndex references;
    readonly IReadOnlyDictionary<string, FBoundStarProjectionErasure.ProjectedStorageOwner> storageOwners;
    readonly Lazy<IReadOnlyDictionary<string, SupertypeGraph.Def>> classHierarchy;
    readonly bool referenceBuild;
    readonly CapturedGenericInvocation.PreparedInvocation invocations;

    internal static string EntryId(string id) => DeclarationIdentityBinding.PhysicalOnlyId(id, "constraint-call");
    readonly GenericRepresentationPolicy values;

    internal static bool HasFixedAbi(BirFunctionDeclaration local, string id, ReferenceMetadataIndex references) =>
        local != null ? local.HasNativeOverrideSlot || local.RequiresClrOverride.OrDefault()
        : references.TryDeclarationIdentity(id, out _, out _, out _, out _) &&
          !references.TryDeclarationIdentity(EntryId(id), out _, out _, out _, out _);

    internal ProjectedCallableEntries(BirFileDeclaration[] files, ReferenceMetadataIndex references, bool referenceBuild,
        CapturedGenericInvocation.PreparedInvocation invocations,
        IReadOnlyDictionary<string, FBoundStarProjectionErasure.ProjectedStorageOwner> storageOwners,
        GenericRepresentationPolicy values, Relay[] relays)
    {
        this.references = references;
        this.invocations = invocations;
        this.storageOwners = storageOwners;
        this.values = values;
        classHierarchy = new(() => SupertypeGraph.CollectSource(files));
        this.referenceBuild = referenceBuild;
        classes = files.SelectMany(f => f.Types.OrEmpty()).OfType<BirClassDefinition>()
            .Where(t => t.Name.HasValue).ToLookup(t => t.Name.Value, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        kinds = files.SelectMany(f => f.Types.OrEmpty()).Where(t => t.Name.HasValue)
            .ToLookup(t => t.Name.Value, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Kind.OrDefault(), StringComparer.Ordinal);
        var bodies = new List<Body>();
        foreach (var type in files.SelectMany(f => f.Types.OrEmpty()))
            foreach (var method in type.Methods.OrEmpty())
                if (method.RoundtripDeclarationIdentity.OrDefault()?.DeclarationId.OrDefault() is string identity)
                    originalSlots.Add(identity, (type, method));
        CollectMaterialRelays(files, relays);
        foreach (var file in files)
        {
            if (file.Methods.HasValue) file.Methods = Declare(file.Methods.Value, null, bodies, localSlots);
            foreach (var type in file.Types.OrEmpty())
                if (type.Methods.HasValue) type.Methods = Declare(type.Methods.Value, type, bodies, localSlots);
        }
        // Access stubs must describe the selected projected MethodDef exactly,
        // including its extra generic witnesses. Keep the target private.
        BirFunctionDeclaration[] DeclareAccessors(BirFunctionDeclaration[] methods)
        {
            List<BirFunctionDeclaration> result = null;
            foreach (var m in methods)
            {
                if (m.ProjectedAccessBinding is not { } binding || Find(binding.Target) is not { } target) continue;
                result ??= new(methods);
                var name = m.Name.Value + "Projected";
                for (var i = 0; result.Any(f => f.Name.OrDefault() == name); i++) name = m.Name.Value + "Projected" + i;
                // Method UnsafeAccessors (instance and static) prefix the target owner.
                var plan = target.Plan with { AddressOperands = target.Plan.AddressOperands.Select(i => i + 1).ToArray() };
                var entry = target with { Name = name, Plan = plan, Signature = AddressSignature(plan, m) };
                var id = EntryId(binding.Accessor);
                entries.Add(binding.Accessor, entry);
                emittedEntries.Add(id, entry);
                var copy = (BirFunctionDeclaration)m.DeepClone();
                copy.Name = name;
                copy.TypeParams = entry.Plan.CloneParameters();
                entry.Plan.ApplyAddressFormals(copy.Params.OrEmpty());
                ApplyNativeSignature(copy, plan);
                RoundtripMetadata.StampProjectedMethod(copy, entry.Plan);
                copy.ProjectedAccessBinding = null;
                if (copy.Extern.OrDefault()) DeclarationIdentityBinding.RewriteUnsafeAccessorTarget(copy, target.Name);
                else copy.RoundtripDeclarationIdentity = new BirRoundtripDeclarationIdentity { DeclarationId = id, DeclarationSourceName = name };
                result.Add(copy);
            }
            return result?.ToArray() ?? methods;
        }
        foreach (var file in files)
        {
            if (file.Methods.HasValue) file.Methods = DeclareAccessors(file.Methods.Value);
            foreach (var type in file.Types.OrEmpty())
                if (type.Methods.HasValue) type.Methods = DeclareAccessors(type.Methods.Value);
        }
        var hierarchy = bodies.Any(b => b.Method.SelectedGenericOverrides is { Count: > 0 })
            ? classHierarchy.Value : null;
        foreach (var (owner, method, entry) in bodies)
        {
            if (owner == null || method.SelectedGenericOverrides is not { Count: > 0 } slots) continue;
            foreach (var id in slots.Distinct(StringComparer.Ordinal))
            {
                bool local = localSlots.TryGetValue(id, out var slot);
                if (!local && !references.TryDeclarationSignatureSlot(EntryId(id), out slot))
                {
                    if (entry != null) DeclareOrdinaryInterfaceBridge(files, hierarchy, owner, method, entry, id);
                    continue;
                }
                if (entry == null && !slot.IsInterface) continue;
                foreach (var constructed in SelectedConstructions(hierarchy, owner, method, id, slot.Owner, local ? null : EntryId(id)))
                {
                    TypeNode Close(TypeNode t) => SupertypeGraph.SubstOwnerTvs(t, constructed.Args ?? []);
                    var descriptor = new BirMethodImpl { Owner = constructed, Member = slot.Name, Arity = slot.Arity,
                        Params = slot.Parameters.Select(Close).ToArray(), Ret = Close(slot.Return) };
                    if (!local && !slot.IsInterface)
                    {
                        var selected = references.SelectedPhysicalMethod(EntryId(id)) ??
                            throw new InvalidOperationException("Selected projected base MethodDef is absent");
                        descriptor.MemberRef = BirMemberReference.FromPhysical(ClrMemberResolution.MemberRefOf(null, references,
                            selected, "method", selected.DeclaringType, constructed.Args));
                    }
                    if (slot.IsInterface && originalSlots.TryGetValue(id, out var source) && !source.Method.Virtual.OrDefault())
                        RetireSlotForwarder(owner, constructed, new(source.Owner.Name.Value, source.Method.Name.Value,
                            source.Method.TypeParams.OrEmpty().Length, source.Method.Params.OrEmpty().Select(FunctionSignatureIdentity.SignatureType).ToArray(),
                            source.Method.Ret.Value, true, false), id);
                    else if (!local && references.SelectedPhysicalMethod(id) is { IsVirtual: false } or { IsFinal: true } &&
                        references.TryDeclarationSignatureSlot(id, out var facade)) RetireSlotForwarder(owner, constructed, facade, id);
                    var contract = (local ? entries[id].Plan : references.ProjectedMethod(EntryId(id))).OnOwner(constructed.Args ?? []);
                    if (entry == null)
                    {
                        DeclareFixedSlotForwarder(files, owner, method, descriptor, contract, local ? entries[id].Representations :
                            references.NullableMethodFrame(contract.RepresentationDeclaration ?? id), id);
                        continue;
                    }
                    if (slot.IsInterface && !Supplies(contract, entry.Plan))
                    {
                        DeclareOrdinaryInterfaceBridge(files, hierarchy, owner, method, entry, id, slot, contract, constructed);
                        continue;
                    }
                    var arguments = contract.ForwardTo(entry.Plan, references.WitnessConstraintKey);
                    TypeNode PhysicalSlot(TypeNode t) => ClrMemberResolution.MethodImplComparisonType(t, references);
                    if (owner.Kind.OrDefault() == "interface" ||
                        !descriptor.Params.Value.Select(PhysicalSlot).SequenceEqual(method.Params.OrEmpty().Select(p => PhysicalSlot(FunctionSignatureIdentity.SignatureType(p))))
                        || PhysicalSlot(descriptor.Ret.Value) != PhysicalSlot(method.Ret.Value))
                        arguments ??= Enumerable.Range(0, contract.Parameters.Length).Select(i => (TypeNode)new TypeNode.Tv("method", i)).ToArray();
                    if (arguments != null)
                    {
                        AddSlotForwarder(owner, method, descriptor, contract, arguments, slot.IsInterface, id);
                        continue;
                    }
                    if (slot.IsInterface)
                    {
                        // A final Kotlin method can fill a carrier slot directly.
                        // MethodImpl needs a virtual body, not a new override point.
                        if (!method.Virtual.OrDefault()) { method.Virtual = true; method.ClrFinal = true; }
                        method.ClrInterfaceImpls = method.ClrInterfaceImpls.OrEmpty().Append(descriptor).ToArray();
                    }
                    else
                    {
                        method.ClrBaseImpls = method.ClrBaseImpls.OrEmpty().Append(descriptor).ToArray();
                        if (method.Name.Value == slot.Name) method.Override = true;
                    }
                }
            }
        }
    }

    bool Supplies(ProjectedMethodPlan from, ProjectedMethodPlan to) =>
        to.NativeIndices.All(from.NativeIndices.Contains) && to.ReferenceIndices.All(from.ReferenceIndices.Contains) &&
        to.Facets.All(t => from.Facets.Any(s => s.OwnerIndex == t.OwnerIndex &&
            references.WitnessConstraintKey(s.Bound) == references.WitnessConstraintKey(t.Bound)));

    TypeNode.Fqn[] SelectedConstructions(IReadOnlyDictionary<string, SupertypeGraph.Def> hierarchy,
        IBirTypeDeclaration owner, BirFunctionDeclaration method, string id, string declaringOwner, string referencedId)
        => SupertypeGraph.SelectedOverrideOwners(hierarchy[owner.Name.Value], hierarchy, references,
            method.ConstructedOverrideOwners, id, declaringOwner, referencedId);

    void DeclareFixedSlotForwarder(BirFileDeclaration[] files, IBirTypeDeclaration owner, BirFunctionDeclaration method,
        BirMethodImpl descriptor, ProjectedMethodPlan contract, NullableRepresentationFrame frame, string id)
    {
        // The selected interface owns the projected rows. The native virtual method
        // remains the sole override point and takes one material argument per source parameter.
        var rows = method.TypeParams.Value;
        var primary = Enumerable.Range(0, rows.Length).Select(i => new TypeNode.Tv("method", frame?.SourcePosition(i) ?? i)).ToArray();
        var actual = primary.Select(contract.NativeArgument).ToArray();
        var proofs = new List<BirMethodWitness>();
        var missing = new List<(int Position, TypeNode Bound)>();
        for (int i = 0; i < rows.Length; i++)
        {
            var required = rows[i].Constraints.OrEmpty().Select(t =>
                FBoundStarProjectionErasure.SubstituteDeclarationTypeArguments(t, [], actual)).ToArray();
            var position = ((TypeNode.Tv)actual[i]).I;
            var available = contract.Parameters[position].Constraints.OrEmpty();
            var bounds = required.Where(b => b is TypeNode.Fqn).ToArray();
            var unproved = bounds.Where(b => !available.Any(t => Proves(t, b))).ToArray();
            if (unproved.Length == 0) continue;
            missing.AddRange(unproved.Select(b => (position, b)));
            // The dynamic path reopens the nominal argument; it no longer has
            // the constraints that were statically proved on the material row.
            proofs.Add(new() { Argument = i, Primary = primary[i], Interfaces = bounds });
        }
        if (proofs.Count == 0 || NeedsTaskView(method, descriptor.Ret.Value, actual))
        {
            AddSlotForwarder(owner, method, descriptor, contract, actual, true, id);
            return;
        }
        var bridge = NewSlotForwarder(owner, method, descriptor, contract.CloneParameters(), true);
        bridge.HasProjectedAddressFrame = contract.AddressOperands.Length != 0;
        bridge.Body = referenceBuild ? RefBodySquash.SourceThrowStubBody(references) : ForwardBody(owner, bridge, method, actual, adaptResult: false);
        var fallback = bridge.Body.Value[0];
        void DeferFallback()
        {
            if (!referenceBuild) bridge.FixedSlotFallback = (Array.IndexOf(bridge.Body.Value, fallback),
                method.RoundtripDeclarationIdentity.Value.DeclarationId.Value, proofs.ToArray(), contract.AddressOperands);
        }

        // A concrete nominal witness can satisfy the whole conjunction even if
        // its published row cannot express it. Only genuinely split calls need
        // the same runtime intersection operation as a direct native call.
        var anchors = missing.Distinct().ToArray();
        bool open = false;
        foreach (var (_, bound) in anchors) BirGraphNameFields.VisitTypeNodes(bound, t => open |= t is TypeNode.Tv { Scope: "method" });
        if (open) { DeferFallback(); return; }
        BirFunctionDeclaration Operation(TypeNode.Fqn op, string name, BirTypeParameter[] parameters) =>
            NewSlotForwarder(owner, method, descriptor with { Owner = op, Member = name, Arity = parameters.Length }, parameters, true);
        var exact = WithConstraintEvidence(files, owner, method, bridge, anchors, Operation, otherwise: bridge.Body.Value);
        DeferFallback();
        exact.HasProjectedAddressFrame = bridge.HasProjectedAddressFrame;
        exact.Body = referenceBuild ? RefBodySquash.SourceThrowStubBody(references) : ForwardBody(owner, exact, method, actual);
    }

    void AddSlotForwarder(IBirTypeDeclaration owner, BirFunctionDeclaration target, BirMethodImpl descriptor,
        ProjectedMethodPlan contract, TypeNode[] arguments, bool isInterface, string id)
    {
        // The MethodImpl keeps the selected declaration's ABI. Dispatch to the
        // Kotlin body with its own value parameters and witness ordering.
        var bridge = NewSlotForwarder(owner, target, descriptor, contract.CloneParameters(), isInterface);
        bridge.HasProjectedAddressFrame = contract.AddressOperands.Length != 0;
        bridge.Body = referenceBuild ? RefBodySquash.SourceThrowStubBody(references) :
            isInterface && NeedsTaskView(target, descriptor.Ret.Value, arguments)
                ? ProjectedTaskBody(owner, bridge, target, (TypeNode.Fqn)descriptor.Owner.Value, contract, id)
                : ForwardBody(owner, bridge, target, arguments);
    }

    // Only an authored suspend bridge materializes a completed value; an ordinary
    // Kotlin method returning Task<T> must retain that task and its identity.
    static bool NeedsTaskView(BirFunctionDeclaration target, TypeNode result, TypeNode[] arguments) =>
        target.SuspendBridge.OrDefault() && result is TypeNode.Fqn { Args.Length: 1 } &&
        result != FBoundStarProjectionErasure.SubstituteDeclarationTypeArguments(target.Ret.Value, [], arguments);

    IBirStatement[] ProjectedTaskBody(IBirTypeDeclaration owner, BirFunctionDeclaration bridge, BirFunctionDeclaration target,
        TypeNode.Fqn instance, ProjectedMethodPlan contract, string id)
    {
        var implementation = target.LocalBodyEntry?.Id ?? target.RoundtripDeclarationIdentity.OrDefault()?.DeclarationId.OrDefault();
        var completion = ((TypeNode.Fqn)bridge.Ret.Value).Args.Single();
        bool text = StringCharSequenceBridge.CanRestoreMaterializedValue(new TypeNode.Fqn("kotlin.Any"), completion);
        var template = owner.Methods.Value.Single(m => m.ProjectedTaskView is { } view && view == (implementation, id) &&
            m.Ret.OrDefault() is TypeNode.Fqn { Args: [var result] } && (text ? result == completion : result is TypeNode.Tv));
        var body = template.Body.Value.Select(s => (IBirStatement)((BirGraphValue)s).DeepClone()).ToArray();
        if (((TypeNode.Fqn)template.Ret.Value).Args[0] is TypeNode.Tv variable)
            foreach (var s in body) TypedExecutableFrames.RewriteDeclarationFrameInPlace((BirGraphValue)s,
                t => TypedExecutableFrames.RewriteType(t, v => v == variable ? completion : v));
        var call = CompletionInvocation(body) ?? throw new InvalidOperationException("Missing cold call in suspend completion view");
        WireCompletionInvocation(call, bridge, instance, contract, id);
        return body;
    }

    static BirCallInstance CompletionInvocation(IBirStatement[] body)
    {
        BirCallInstance call = null;
        foreach (var s in body) ((BirGraphValue)s).Visit(n => {
            if (n is BirCallInstance { TaskBridgeColdInvocation: true } c)
            {
                if (call != null) throw new InvalidOperationException("Ambiguous cold call in suspend completion view");
                call = c;
            }
        });
        return call;
    }

    void WireCompletionInvocation(BirCallInstance c, BirFunctionDeclaration bridge, TypeNode.Fqn instance, ProjectedMethodPlan contract, string id)
    {
        var coldId = id + "|cold";
        var projection = contract == null ? null : Find(coldId);
        var physicalId = projection == null ? coldId : EntryId(coldId);
        ReferencedDeclarationSlot cold = null;
        if (projection != null) localSlots.TryGetValue(coldId, out cold);
        else if (originalSlots.TryGetValue(coldId, out var original))
            cold = new(original.Owner.Name.Value, original.Method.Name.Value, original.Method.TypeParams.OrEmpty().Length,
                original.Method.Params.OrEmpty().Select(FunctionSignatureIdentity.SignatureType).ToArray(), original.Method.Ret.Value, true, false);
        bool local = cold != null;
        if (!local && !references.TryDeclarationSignatureSlot(physicalId, out cold))
            throw new InvalidOperationException("Projected suspend interface declaration lacks its cold slot");
        var selected = local ? null : references.SelectedPhysicalMethod(physicalId);
        var actual = (projection == null ? null : contract.ForwardTo(projection.Plan.OnOwner(instance.Args ?? []), references.WitnessConstraintKey)) ??
            Enumerable.Range(0, cold.Arity).Select(i => (TypeNode)new TypeNode.Tv("method", i)).ToArray();
        var continuation = c.Args.Value[^1];
        c.OwnerType = instance; c.Recv = new BirThis(); c.Method = cold.Name; c.Virtual = true;
        c.TypeArgs = actual; c.Sig = cold.Parameters.Select(t => SupertypeGraph.SubstOwnerTvs(t, instance.Args ?? [])).ToArray();
        c.CalleeParams = cold.Parameters; c.CalleeRet = cold.Return;
        c.MemberRef = selected == null ? default(OptionalField<BirMemberReference>) :
            BirMemberReference.FromPhysical(ClrMemberResolution.MemberRefOf(null, references, selected, "method", selected.DeclaringType, instance.Args));
        c.Args = bridge.Params.Value.Select(p => (IBirExpression)new BirLocal { Name = p.Name }).Append(continuation).ToArray();
    }

    static BirFunctionDeclaration NewSlotForwarder(IBirTypeDeclaration owner, BirFunctionDeclaration target,
        BirMethodImpl descriptor, BirTypeParameter[] parameters, bool isInterface)
    {
        var names = owner.Methods.Value.Select(m => m.Name.OrDefault()).ToHashSet(StringComparer.Ordinal);
        var name = target.Name.Value + "$forward";
        for (int suffix = 0; names.Contains(name); suffix++) name = target.Name.Value + "$forward" + suffix;
        var bridge = new BirFunctionDeclaration {
            Name = name, Params = descriptor.Params.Value.Select((type, i) => new BirParameter {
                Name = target.Params.Value[i].Name, Type = BirGraphValue.CopyType(type) }).ToArray(),
            Ret = descriptor.Ret, TypeParams = parameters,
            Vis = "private", Virtual = true, Abstract = false, ClrFinal = true, Static = false, Generated = true,
        };
        if (isInterface) bridge.ClrInterfaceImpls = new[] { descriptor };
        else bridge.ClrBaseImpls = new[] { descriptor };
        if (owner.Kind.OrDefault() == "interface") bridge.ClrInterfaceSlotBridge = true;
        owner.Methods = owner.Methods.Value.Append(bridge).ToArray();
        return bridge;
    }

    ProjectedClassStorageFrame Frame(BirTypeParameter[] parameters, bool locations = false) => ProjectedClassStorageFrame.Plan(parameters,
        (_, bound) => (bound, kinds.GetValueOrDefault(bound.Name) ?? (references.IsInterfaceType(bound) ? "interface" :
            references.IsClassType(bound) || references.PhysicalTypeForProof(bound.Name, bound.Args?.Length ?? 0)?.IsClass == true
                ? "class" : null)), locations)?.WithDeclaredProofs(values, references.PhysicalConstraintType);

    NullableRepresentationFrame RepresentationsFor(BirFunctionDeclaration m) =>
        m.LocalBodyEntry?.Frame ?? m.ColdRepresentation?.Frame ?? m.RoundtripDeclarationIdentity.OrDefault()?.NullableRepresentationFrame.OrDefault()
        ?? (m.MaterialFrameOrigin is { } id && originalSlots.TryGetValue(id, out var origin) ? RepresentationsFor(origin.Method) : null);

    // Future overrides can observe the material type even if the current virtual
    // body doesn't. Nonvirtual relays need it only on an actual forwarding edge.
    IEnumerable<int> IndependentVirtualArguments(BirFunctionDeclaration method)
    {
        if (relayedPositions.TryGetValue(method, out var positions))
            foreach (var i in positions.Order()) yield return i;
        if (method.MaterialFrameOrigin is { } id && originalSlots.TryGetValue(id, out var origin)) method = origin.Method;
        if (!method.SourceCallableVirtual || RepresentationsFor(method) is not { } frame) yield break;
        foreach (var source in frame.StorageIndices.Order())
        {
            var position = frame.SourcePosition(source);
            if (method.OwnerDependentSourceSlots?.ContainsKey(source) == true ||
                values.NeedsOpenMethodStorage(method.TypeParams.Value[position].Constraints.OrEmpty().OfType<TypeNode.Fqn>()))
                yield return position;
        }
    }

    internal readonly record struct Relay(string Caller, int Position, string Target, int Argument);
    readonly Dictionary<BirFunctionDeclaration, HashSet<int>> relayedPositions = new(ReferenceEqualityComparer.Instance);

    // Take the selected edges before reference-body retirement. Signatures and
    // material forwarding must be identical with or without executable bodies.
    internal static Relay[] CaptureMaterialRelays(BirFileDeclaration[] files)
    {
        var uses = new HashSet<Relay>();
        foreach (var f in files)
        foreach (var m in f.Methods.OrEmpty().Concat(f.Types.OrEmpty().SelectMany(t => t.Methods.OrEmpty())))
        {
            var id = m.LocalBodyEntry?.Id ?? m.DeclarationId.OrDefault();
            if (id == null || m.TypeParams.OrEmpty().Length == 0) continue;
            foreach (var statement in m.Body.OrEmpty()) ((BirGraphValue)statement).Visit(n => {
                if ((n.SelectedGenericCallable ?? n.DeclarationIdentityFact.OrDefault()) is not {} target ||
                    n.ApplicationTypeArguments.OrDefault() is not {} actual) return;
                for (int i = 0; i < actual.Length; i++)
                    if (actual[i] is TypeNode.Tv { Scope: "method" } v) uses.Add(new(id, v.I, target, i));
            });
        }
        return uses.ToArray();
    }

    void CollectMaterialRelays(BirFileDeclaration[] files, Relay[] relays)
    {
        var uses = relays.ToLookup(r => (r.Target, r.Argument));
        var local = new Dictionary<string,BirFunctionDeclaration>(StringComparer.Ordinal);
        var pending = new Queue<(string,int)>();
        var known = new HashSet<(string,int)>();
        void Need(string id, int i) { if (known.Add((id,i))) pending.Enqueue((id,i)); }
        foreach (var f in files)
        foreach (var m in f.Methods.OrEmpty().Concat(f.Types.OrEmpty().SelectMany(t => t.Methods.OrEmpty())))
        {
            var id = m.LocalBodyEntry?.Id ?? m.RoundtripDeclarationIdentity.OrDefault()?.DeclarationId.OrDefault();
            if (id == null) continue;
            local[id] = m;
            foreach (var i in IndependentVirtualArguments(m)) Need(id,i);
        }
        foreach (var id in relays.Select(r => r.Target).Distinct().Where(id => !local.ContainsKey(id)))
            if (references.ProjectedMethod(EntryId(id)) is {} target)
                foreach (var i in target.NativeIndices) Need(id,i);
        while (pending.TryDequeue(out var key))
            foreach (var r in uses[key])
            {
                if (!local.TryGetValue(r.Caller, out var m) || HasFixedAbi(m,r.Caller,references) ||
                    RepresentationsFor(m) is not {} frame || r.Position >= frame.PhysicalArity ||
                    frame.SourceIndex(r.Position) is not int source || frame.SourcePosition(source) != r.Position ||
                    !frame.StorageIndices.Contains(source)) continue;
                if (!relayedPositions.TryGetValue(m, out var positions)) relayedPositions[m] = positions = [];
                if (positions.Add(r.Position)) Need(r.Caller,r.Position);
            }
    }

    ProjectedClassStorageFrame MethodFrame(BirFunctionDeclaration m)
    {
        var parameters = m.TypeParams.OrEmpty();
        var representations = RepresentationsFor(m);
        bool NeedsIndependentOrdinary(int source)
        {
            var ordinary = new TypeNode.Tv("method", representations.SourcePosition(source));
            var nullable = representations.NullableVariable(new TypeNode.Tv("method", source));
            bool Mentions(TypeNode type, TypeNode variable)
            {
                var found = false;
                BirGraphNameFields.VisitTypeNodes(type, t => found |= t == variable);
                return found;
            }
            // A function-signature modifier identifies an overload but does not
            // store the argument. Only the value inside it can require a witness.
            var signature = m.Params.OrEmpty().Select(p => SignatureValueTypes.Of(p.Type.Value))
                .Append(SignatureValueTypes.Of(m.Ret.Value));
            if (!signature.Any(t => Mentions(t, nullable))) return false;
            // An otherwise unbounded, erased argument can use its nullable
            // witness directly. A nominal bound or an observable CLR signature
            // (for example List<B> as well as List<B?>) fixes B independently.
            return parameters[ordinary.I].Constraints.OrEmpty().Any(c => c != nullable && !FBoundStarProjectionErasure.IsObjectish(c)) ||
                parameters[ordinary.I].SpecialConstraints.OrEmpty().Length != 0 ||
                parameters.Any(p => p.Constraints.OrEmpty().Any(t => Mentions(t, ordinary))) ||
                signature.Any(t => Mentions(t, ordinary));
        }
        bool NativeClassArgument()
        {
            if (!parameters.Any(Nominal)) return false;
            foreach (var type in ProjectedMethodPlan.MaterialSignature(m.Params.OrEmpty(), m.Ret.OrDefault()))
            {
                bool needs = false;
                ProjectedMethodPlan.NativeUses(type, references, (v, _, _, invariant) => {
                    needs |= invariant && v.Scope == "method" && Nominal(parameters[v.I]);
                    return null;
                });
                if (needs) return true;
            }
            return false;
        }
        var frame = Frame(parameters,
            !m.Extern.OrDefault() && (IndependentVirtualArguments(m).Any() || representations?.NullableIndices.Any(NeedsIndependentOrdinary) == true ||
                m.Params.OrEmpty().Any(p => ProjectedMethodPlan.HasMethodLocation(p, references)) || NativeClassArgument()));
        return frame;
    }

    bool Nominal(BirTypeParameter p) => p.Constraints.OrEmpty().OfType<TypeNode.Fqn>().Any(b =>
        !FBoundStarProjectionErasure.IsObjectish(b) && (kinds.TryGetValue(b.Name, out var k) ? k == "class" : references.IsClassType(b)));

    ProjectedMethodPlan DeclarationPlan(IBirTypeDeclaration owner, BirFunctionDeclaration method, ProjectedClassStorageFrame frame)
    {
        if (declarationPlans.TryGetValue(method, out var plan)) return plan;
        if (method.ImplicitExistentialImplementationId is string origin)
        {
            var implementation = originalSlots[origin];
            return declarationPlans[method] = DeclarationPlan(implementation.Owner, implementation.Method, MethodFrame(implementation.Method));
        }
        // A CLR class has only one inherited base slot. Keep it as the
        // override point; interface MethodImpls can adapt to that frame.
        bool IsInterface(string id) => originalSlots.TryGetValue(id, out var local)
            ? local.Owner.Kind.OrDefault() == "interface" :
                references.TryDeclarationSignatureSlot(EntryId(id), out var slot) && slot.IsInterface;
        var interfaces = new List<ProjectedMethodPlan>();
        IEnumerable<string> contracts = method.SelectedGenericOverrides ?? [];
        // An inherited forwarding body calls the selected base MethodDef;
        // it must meet that entry's constraints without overriding its slot.
        if (method.LocalBodyEntry.HasValue && method.MaterialFrameOrigin is { } body &&
            references.TryDeclarationSignatureSlot(EntryId(body), out var selectedBody) && !selectedBody.IsInterface)
            contracts = contracts.Prepend(body);
        foreach (var id in contracts.OrderBy(IsInterface))
        {
            string declaringOwner;
            if (originalSlots.TryGetValue(id, out var local))
            {
                // This slot mirrors our declaration, so it cannot supply an
                // independent inherited contract when choosing our plan.
                if (local.Method.ImplicitExistentialImplementationId is string implementationId
                    && ReferenceEquals(originalSlots[implementationId].Method, method)) continue;
                plan = DeclarationPlan(local.Owner, local.Method, MethodFrame(local.Method));
                if (plan == null) continue;
                declaringOwner = local.Owner.Name.Value;
            }
            else if (references.TryDeclarationSignatureSlot(EntryId(id), out var slot))
            {
                plan = references.ProjectedMethod(EntryId(id));
                declaringOwner = slot.Owner;
            }
            else continue;
            var graph = classHierarchy.Value;
            foreach (var application in SelectedConstructions(graph, owner, method, id, declaringOwner, local.Method == null ? EntryId(id) : null))
            {
                var closed = plan.OnOwner(application.Args ?? []);
                if (!IsInterface(id)) return declarationPlans[method] = closed;
                interfaces.Add(closed);
            }
        }
        if (interfaces.Count != 0)
        {
            if (interfaces.FirstOrDefault(p => interfaces.All(q => Supplies(p, q))) is { } covering)
                return declarationPlans[method] = covering;
            // Independent interfaces may each publish only part of the evidence
            // available to the implementing body. Split the combined requirements
            // once; neither interface's declaration order selects the body's ABI.
            var parameters = interfaces[0].Parameters.Take(interfaces[0].PrimaryArity)
                .Select(p => (BirTypeParameter)p.DeepClone()).ToArray();
            for (int i = 0; i < parameters.Length; ++i)
                parameters[i].Constraints = interfaces.SelectMany(p => p.Parameters[i].Constraints.OrEmpty()
                    .Concat(p.Facets.Where(f => f.OwnerIndex == i).Select(f => f.Bound))).Distinct().ToArray();
            frame = Frame(parameters, true);
        }
        return declarationPlans[method] = frame == null ? null : ProjectedMethodPlan.Declare(frame, method.Params.OrEmpty(), references,
            RepresentationsFor(method),
            method.Ret.OrDefault(), Nominal, values,
            IndependentVirtualArguments(method).Concat(interfaces.SelectMany(p => p.NativeIndices)));
    }

    BirFunctionDeclaration[] Declare(BirFunctionDeclaration[] source, IBirTypeDeclaration owner, List<Body> bodies,
        Dictionary<string, ReferencedDeclarationSlot> slots)
    {
        var result = new List<BirFunctionDeclaration>(source);
        var names = source.Select(m => m.Name.OrDefault()).ToHashSet(StringComparer.Ordinal);
        foreach (var m in source)
        {
            var identity = m.RoundtripDeclarationIdentity.OrDefault();
            var bodyEntry = m.LocalBodyEntry;
            var carrierSlot = m.ExistentialCallable == ExistentialCallableRole.Slot;
            var carrierBridge = m.ExistentialCallable == ExistentialCallableRole.Forwarder;
            if (bodyEntry == null && carrierBridge && m.SelectedGenericOverrides is { Count: > 0 } implemented)
                bodyEntry = (DeclarationIdentityBinding.InheritedProjectionId(owner.Name.Value, implemented), null);
            if (HasFixedAbi(m, null, references))
            {
                if (owner != null && m.TypeParams.OrEmpty().Length != 0 && m.SelectedGenericOverrides is { Count: > 0 })
                    bodies.Add(new(owner, m, null));
                continue;
            }
            if ((bodyEntry?.Id ?? identity?.DeclarationId.OrDefault()) is not string id ||
                bodyEntry == null && !carrierSlot && id.Contains(DeclarationIdentityBinding.PhysicalOnlySuffix, StringComparison.Ordinal) ||
                m.TypeParams.OrEmpty() is not { Length: > 0 } parameters) continue;
            var contractSource = m.ImplicitExistentialImplementationId is string origin ? originalSlots[origin].Method : m;
            if (!ReferenceEquals(contractSource, m))
                m.TypeParams = parameters = contractSource.TypeParams.Value.Select(p => (BirTypeParameter)p.DeepClone()).ToArray();
            var ownFrame = MethodFrame(m);
            var plan = DeclarationPlan(owner, m, ownFrame);
            if (plan == null) continue;
            // This is also the override point for C# subclasses: use an identifier
            // that C# source can name. The binding is still by identity, not suffix.
            var name = m.Name.Value + "Projected";
            for (var i = 0; !names.Add(name); i++) name = m.Name.Value + "Projected" + i;
            if (contractSource.SelectedGenericOverrides is { Count: > 0 })
            {
                for (var i = 0; i < plan.PrimaryArity; i++)
                {
                    var row = parameters[i];
                    foreach (var required in plan.Parameters[i].Constraints.OrEmpty().OfType<TypeNode.Fqn>())
                        if (!row.Constraints.OrEmpty().Any(c => c == required || Proves(c, required)))
                            row.Constraints = row.Constraints.OrEmpty().Where(c => !Proves(required, c)).Append(required).ToArray();
                }
                foreach (var facet in plan.Facets)
                {
                    // The exact facade supplies this same argument for each nominal
                    // facet. These are the requirements of its selected virtual ABI.
                    var row = parameters[facet.OwnerIndex];
                    row.Constraints = row.Constraints.OrEmpty().Concat(facet.Evidence).Distinct().ToArray();
                }
            }
            var methodFrame = RepresentationsFor(contractSource);
            // The downstream entry must consult the same ordinary/nullable/storage
            // frame as local calls. A mirrored slot has no semantic frame of its own.
            plan = plan with { RepresentationDeclaration = contractSource.ColdRepresentation?.Id ?? m.ImplicitExistentialImplementationId ?? m.MaterialFrameOrigin };
            var entry = new Entry(name, plan, parameters, methodFrame, AddressSignature(plan, m));
            if (contractSource.SelectedGenericOverrides is { Count: > 0 } && entry.Representations is { } representations)
                foreach (var sourceIndex in representations.StorageIndices)
                {
                    // The exact forwarder passes its storage unchanged to the inherited
                    // projected slot. Its constraints belong to that slot, not to the
                    // overriding source spelling (which may now name one concrete class).
                    var row = representations.Variable(new TypeNode.Tv("method", sourceIndex), NullableRepresentationFrame.Role.Storage).I;
                    parameters[row].Constraints = plan.Parameters[row].Constraints;
                }
            if (contractSource.SelectedGenericOverrides is { Count: > 0 } && m.ColdEntryMachine is { } machine &&
                classes.TryGetValue(machine.Name, out var sm))
            {
                var captured = TypeParameterFrame.Declarations(sm).Skip(machine.OwnerArity).ToArray();
                for (int i = 0; i < parameters.Length; ++i)
                    captured[i].Constraints = parameters[i].Constraints.OrEmpty().Select(t => TypedExecutableFrames.RewriteType(t,
                        v => v.Scope == "method" ? new TypeNode.Tv("type", machine.OwnerArity + v.I) : v)).ToArray();
            }
            entries.Add(id, entry);
            emittedEntries.Add(EntryId(id), entry);
            var copy = (BirFunctionDeclaration)m.DeepClone();
            copy.Name = name;
            copy.Generated = true;
            copy.TypeParams = entry.Plan.CloneParameters();
            entry.Plan.ApplyAddressFormals(copy.Params.OrEmpty());
            ApplyNativeSignature(copy, plan);
            copy.ClrInterfaceImpls = default;
            copy.ClrBaseImpls = default;
            copy.ClrOverrideRef = default;
            copy.RequiresClrOverride = default;
            copy.Override = false;
            copy.Attrs = default;
            RoundtripMetadata.StampProjectedMethod(copy, entry.Plan);
            if (bodyEntry is { } local) copy.LocalBodyEntry = (EntryId(id), local.Frame);
            else copy.RoundtripDeclarationIdentity = new BirRoundtripDeclarationIdentity {
                DeclarationId = EntryId(id), DeclarationSourceName = name };
            if (owner != null && !m.Static.OrDefault())
            {
                if (carrierSlot || carrierBridge) { copy.Virtual = true; copy.ClrFinal = carrierBridge; }
                else if (m.SourceCallableVirtual)
                {
                    exactFacades.Add(id);
                    MakeExactFacade(owner, m, copy, entry.Plan);
                }
                else if (m.Virtual.OrDefault())
                {
                    // The exact method implements a compiler-generated carrier
                    // slot, but the source member has no override point.
                    m.ClrFinal = true;
                    copy.Virtual = false;
                    copy.ClrFinal = default;
                }
            }
            result.Add(copy);
            bodies.Add(new(owner, copy, entry));
            if (owner != null && !copy.Static.OrDefault())
                slots.Add(id, new(owner.Name.Value, name, copy.TypeParams.Value.Length, copy.Params.OrEmpty()
                    .Select(FunctionSignatureIdentity.SignatureType).ToArray(), copy.Ret.Value,
                    owner.Kind.OrDefault() == "interface", false));
        }
        return result.ToArray();
    }

    void ApplyNativeSignature(BirFunctionDeclaration method, ProjectedMethodPlan plan)
    {
        if (plan.NativeIndices.Length == 0) return;
        foreach (var parameter in method.Params.OrEmpty()) parameter.Type = plan.NativeUse(parameter.Type.Value, references);
        method.Ret = plan.NativeUse(method.Ret.Value, references);
    }

    // There is only one virtual slot. The exact entry retains both constraints
    // and forwards its already-proved arguments, even when called from C#.
    void MakeExactFacade(IBirTypeDeclaration owner, BirFunctionDeclaration exact,
        BirFunctionDeclaration implementation, ProjectedMethodPlan plan)
    {
        exact.FacadeModalityFlags = exact.Abstract.OrDefault() ? KotlinFacadeModality.Abstract :
            exact.ClrFinal.OrDefault() ? 0 : KotlinFacadeModality.Open;
        exact.Abstract = false;
        // A generic owner's existential interface may still require the exact
        // signature. A final CLR slot implements it without exposing a second
        // override point. An interface's own exact entry is a nonvirtual default.
        exact.Virtual = owner.Kind.OrDefault() != "interface";
        exact.Override = false;
        exact.ClrFinal = exact.Virtual.OrDefault();
        exact.ClrBaseImpls = default;
        exact.ClrInterfaceImpls = default;
        if (referenceBuild) { exact.Body = RefBodySquash.SourceThrowStubBody(references); return; }
        exact.Body = ForwardBody(owner, exact, implementation, ExactFacadeArguments(exact, plan));
    }

    static TypeNode[] ExactFacadeArguments(BirFunctionDeclaration exact, ProjectedMethodPlan plan)
    {
        var original = Enumerable.Range(0, exact.TypeParams.Value.Length).Select(i => (TypeNode)new TypeNode.Tv("method", i)).ToArray();
        var exactLocations = plan.AddressOperands.Select(i => ((TypeNode.ByRef)SignatureValueTypes.Of(exact.Params.Value[i].Type.Value)).Of);
        return original.Concat(plan.Facets.Select(f => original[f.OwnerIndex]))
            .Concat(plan.NativeIndices.Select(i => original[i])).Concat(exactLocations)
            .Concat(plan.ReferenceIndices.Select(_ => new TypeNode.Fqn("System.Object")))
            .Concat(plan.UpperArguments((i, _) => original[i])).ToArray();
    }

    static IBirStatement[] ForwardBody(IBirTypeDeclaration owner, BirFunctionDeclaration exact,
        BirFunctionDeclaration implementation, TypeNode[] arguments, bool adaptResult = true)
    {
        var ownerArity = TypeParameterFrame.Count(owner);
        var ownerType = new TypeNode.Fqn(owner.Name.Value, ownerArity == 0 ? null :
            Enumerable.Range(0, ownerArity).Select(i => (TypeNode)new TypeNode.Tv("type", i)).ToArray());
        return ForwardBody(ownerType, new BirThis(), exact, implementation, arguments, adaptResult: adaptResult);
    }

    static IBirStatement[] ForwardBody(TypeNode.Fqn ownerType, IBirExpression receiver, BirFunctionDeclaration exact,
        BirFunctionDeclaration implementation, TypeNode[] arguments, BirMemberReference member = null, bool adaptResult = true)
    {
        var call = new BirCallInstance { OwnerType = ownerType, Method = implementation.Name, Virtual = true,
            Recv = OptionalField<IBirExpression>.Of(receiver), Ret = exact.Ret, CalleeRet = implementation.Ret,
            TypeArgs = arguments,
            Sig = implementation.Params.OrEmpty().Select(FunctionSignatureIdentity.SignatureType).ToArray(),
            Args = exact.Params.OrEmpty().Select(p => (IBirExpression)new BirLocal { Name = p.Name }).ToArray() };
        if (member != null) call.MemberRef = member;
        var result = FBoundStarProjectionErasure.SubstituteDeclarationTypeArguments(implementation.Ret.Value, ownerType.Args ?? [], arguments);
        var adapted = adaptResult ? StringCharSequenceBridge.AdaptPhysicalSlotReturn(call, result, exact.Ret.Value) : null;
        if (adapted != null) call.Ret = result;
        return exact.Ret.Value is TypeNode.Fqn { Name: "void" or "System.Void" }
            ? new IBirStatement[] { new BirExpressionStatement { Expr = OptionalField<IBirExpression>.Of(call) }, new BirReturn() }
            : new IBirStatement[] { new BirReturn { Value = OptionalField<IBirExpression>.Of(adapted ?? call) } };
    }

    void BindRemainingApplications(BirFileDeclaration[] files)
    {
        foreach (var file in files) file.Visit(n => {
            if (n.MethodWitnessFacts.HasValue || n.MaterialMethodArguments != null || n is BirCallInstance s && s.Super.OrDefault())
                Bind(n, default, default);
        });
    }

    // A native MethodSpec can mention both the owning class's split slots
    // and those of the current Kotlin method. Bind its witnesses once, with
    // both scopes live, before either kind of argument can be erased.
    internal void RewriteBody(BirGraphValue root, IBirTypeDeclaration owner, BirTypeParameter[] ownerParameters,
        ProjectedClassStorageFrame.InterfaceSlot[] ownerSlots, Func<string,string> allocate,
        List<IBirTypeDeclaration> generated, List<BirFunctionDeclaration> helpers, IReadOnlyDictionary<int, TypeNode.Tv> capturedNullable = null,
        IReadOnlyDictionary<int, Scope.MaterialWitness> capturedNative = null, IReadOnlyDictionary<int, TypeNode.Tv> capturedStorage = null)
    {
        var ownerScope = new Scope(ownerParameters, ownerSlots, CapturedNullable: capturedNullable, CapturedNative: capturedNative, CapturedStorage: capturedStorage);
        BirGraphValue Rewrite(BirGraphValue node, Scope methodScope, Dictionary<string, TypeNode> locations)
        {
            if (node is IBirExecutablePayload or BirMemberReference) return node;
            if (node is BirFunctionDeclaration method) {
                var entry = MethodEntry(method);
                methodScope = entry != null ? new(entry.Plan.Parameters[..entry.Plan.PrimaryArity], entry.Plan.Facets,
                    entry.Representations, NativePlan: entry.Plan) : new(method.TypeParams.OrEmpty(), []);
                locations = entry?.Plan.AddressOperands is { Length: > 0 } positions ? positions.ToDictionary(
                    i => method.Params.Value[i].Name.Value, i => ((TypeNode.ByRef)method.Params.Value[i].Type.Value).Of) : null;
                // The machine captures this method's lexical frame just like a lambda.
                // Its constructor, local and invokeSuspend receiver must use one TypeSpec.
                if (entry != null && method.ColdEntryMachine is { } machine)
                {
                    var scope = methodScope;
                    TypeNode.Fqn target = null;
                    TypeNode.Fqn CloseMachine(TypeNode.Fqn f)
                    {
                        if (f.Name != machine.Name || f.Args is not { } args) return f;
                        return target ??= ProjectLexicalType(f, args, ownerScope, scope, allocate, generated) ?? f;
                    }
                    foreach (var statement in method.Body.OrEmpty())
                        ((BirGraphValue)statement).Visit(n => n.RewriteOwnTypeFieldsInPlace((_, role, t) =>
                            role == BirGenericFrameRole.Lexical ? SourceDeclarationSignature.RewriteType(t, CloseMachine) : t));
                }
            }
            bool material = methodScope.NativePlan?.NativeIndices.Length > 0 || capturedNative != null;
            bool splitStorage = methodScope.Slots?.Length > 0 && methodScope.Representations?.StorageIndices.Count > 0
                || capturedStorage != null;
            bool dependent = methodScope.Representations?.NullableIndices.Count > 0 || capturedNullable != null || material;
            bool splitUse = node is BirGraphNode && (dependent || splitStorage)
                || (capturedNative != null || capturedStorage != null) && node is (BirFieldDeclaration or BirParameter or BirFunctionDeclaration);
            TypeNode Close(TypeNode type) {
                var closed = dependent ? SourceDeclarationSignature.RewriteType(type,
                    f => CloseDependentOwner(f, ownerScope, methodScope)) : type;
                return material || splitStorage ? ProjectedMethodPlan.NativeUses(closed, references,
                    (v, row, args, _) => NativeBodyArgument(v, row, args, ownerScope, methodScope)) : closed;
            }
            if (splitUse) node.RewriteOwnTypeFieldsInPlace((field, role, t) =>
                role == BirGenericFrameRole.Lexical && field is not ("calleeRet" or "calleeParams") ? Close(t) : t);
            node.VisitChildSlots(slot => {
                // The executable use supplies the owner. The parameter/result
                // vectors and declaration-side override descriptors are foreign.
                if (splitUse && slot.Value is BirMemberReference member && member.DeclaringType.HasValue)
                    member.DeclaringType = Close(member.DeclaringType.Value);
                else slot.Replace(Rewrite(slot.Value, methodScope, locations));
            });
            if (node is BirNew construction)
            {
                if (ReferencedConstruction(construction, ownerScope, methodScope) is { } factory) return factory;
                // Cloning the local class preserves the selected constructor's index.
                // Close its declaring type in the same lexical frame as an imported factory.
                if (construction.LocalCtorIndex.HasValue &&
                    construction.Type.OrDefault() is TypeNode.Fqn { Args: { } arguments } selected &&
                    storageOwners.TryGetValue(selected.Name, out var destination) &&
                    ForwardConstructor(destination.Entry, arguments, ownerScope, methodScope) is { } closed)
                    construction.Type = new TypeNode.Fqn(destination.Implementation, closed);
            }
            if (node is BirNewClosure or BirNewSam)
                ProjectClosureCreation(node, ownerScope, methodScope, allocate, generated);
            if (Bind(node, ownerScope, methodScope, locations != null)) return node;
            var dependency = invocations.BindMaterialArguments(node, ownerScope, methodScope, references);
            dependency |= invocations.NeedsNullableRecovery(node, ownerScope, methodScope, references);
            if ((node.MethodWitnessFacts.HasValue || dependency) && CapturedGenericInvocation.Invocation.Admits(node) &&
                invocations.FixedIntersection(new(node), owner?.Name.Value, owner == null ? [] : TypeParameterFrame.Declarations(owner).ToArray(),
                    references, null, generated, classScope: ownerScope, methodScope: methodScope,
                    allocate: allocate, lexicalDefinition: owner, projectingBody: true, locations: locations,
                    splitDependency: dependency) is { } fixedCall) return fixedCall;
            return CapturedGenericInvocation.ProjectStorageCall(node, ownerScope, methodScope, references, allocate, generated, owner, helpers, locations);
        }
        Rewrite(root, default, null);
    }

    TypeNode.Fqn CloseDependentOwner(TypeNode.Fqn application, Scope owner, Scope method)
    {
        if (application.Args is not { Length: > 1 } args) return application;
        TypeNode[] closed = null;
        for (int i = 0; i < args.Length; i++)
        {
            if (Scope.ReferenceValue(args[i], owner, method, b => kinds.TryGetValue(b.Name, out var kind)
                ? kind == "class" : references.IsClassType(b)) is not { } value || !args.Contains(value)) continue;
            if (!ownerConstraints.TryGetValue(application.Name, out var rows))
                ownerConstraints[application.Name] = rows = classes.TryGetValue(application.Name, out var local)
                    ? TypeParameterFrame.Declarations(local).ToArray() : references.PhysicalOwnerTypeParametersInPhysicalFrame(application.Name, nativeNames: true);
            if (rows?.Length != args.Length || rows[i].SpecialConstraints.OrEmpty().Length != 0 ||
                rows[i].Constraints.OrEmpty().Where(b => !FBoundStarProjectionErasure.IsObjectish(b)).ToArray() is not
                    [TypeNode.Tv { Scope: "type" } upper] || args[upper.I] != value) continue;
            (closed ??= args.ToArray())[i] = value;
        }
        return closed == null ? application : application with { Args = closed };
    }

    Entry MethodEntry(BirFunctionDeclaration method) =>
        (method.LocalBodyEntry?.Id ?? method.RoundtripDeclarationIdentity.OrDefault()?.DeclarationId.OrDefault()) is string id
            ? emittedEntries.GetValueOrDefault(id) : null;

    internal void BindExactOwnerMethods(IBirTypeDeclaration owner, BirTypeParameter[] ownerParameters)
    {
        foreach (var method in owner.Methods.OrEmpty())
            if (MethodEntry(method) == null)
                method.Visit(n => Bind(n, new(ownerParameters, []), new(method.TypeParams.OrEmpty(), [])));
    }

    // A facade that is no longer virtual, or an ordinary slot now served by a
    // projected forwarder, no longer needs its old adapter assignment. Keep
    // the group while any of its other selected slots is still live.
    internal void RetireReplacedOverrides(BirFileDeclaration[] files, HashSet<string> emittedTypes)
    {
        foreach (var owner in files.SelectMany(f => f.Types.OrEmpty()))
            if (owner.Methods.OrEmpty().Any(m => m.ProjectedTaskView != null))
                owner.Methods = owner.Methods.Value.Where(m => m.ProjectedTaskView == null).ToArray();
        if (exactFacades.Count == 0 && replacedAdapterSlots.Count == 0 && replacedLocalSlots.Count == 0) return;
        HashSet<string> machines = new(StringComparer.Ordinal);
        foreach (var owner in files.SelectMany(f => f.Types.OrEmpty())) RetireAdapterGroups(owner, machines);
        if (machines.Count == 0) return;
        foreach (var file in files)
            foreach (var method in file.Methods.OrEmpty().Concat(file.Types.OrEmpty().SelectMany(t => t.Methods.OrEmpty())))
                if (method.ColdEntryMachine is { } live) machines.Remove(live.Name);
        foreach (var file in files)
            if (file.Types.OrEmpty().Any(t => machines.Contains(t.Name.OrDefault())))
                file.Types = file.Types.Value.Where(t => !machines.Contains(t.Name.OrDefault())).ToArray();
        emittedTypes.ExceptWith(machines);

    }

    void RetireAdapterGroups(IBirTypeDeclaration owner, HashSet<string> machines)
    {
        bool Obsolete(BirMethodImpl d) => replacedAdapterSlots.Contains(d)
            || d.LocalOverrideTargets is { Count: > 0 } ids && ids.All(id => exactFacades.Contains(id)
                || replacedLocalSlots.TryGetValue(d, out var retired) && retired.Contains(id));
        HashSet<object> unused = null;
        foreach (var m in owner.Methods.OrEmpty())
        {
            var interfaces = m.ClrInterfaceImpls.OrEmpty();
            var bases = m.ClrBaseImpls.OrEmpty();
            if (!interfaces.Any(Obsolete) && !bases.Any(Obsolete)) continue;
            if (interfaces.Length != 0) m.ClrInterfaceImpls = interfaces.Where(d => !Obsolete(d)).ToArray();
            if (bases.Length != 0) m.ClrBaseImpls = bases.Where(d => !Obsolete(d)).ToArray();
            if (m.OverrideAdapterGroup is { } group)
                (unused ??= new()).Add(group);
        }
        if (unused == null) return;
        // Hot entry, cold entry and cold state machine implement one adapter.
        // A surviving slot on either entry keeps the whole group reachable.
        foreach (var m in owner.Methods.Value)
            if (m.ClrInterfaceImpls.OrEmpty().Length != 0 || m.ClrBaseImpls.OrEmpty().Length != 0)
                unused.Remove(m.OverrideAdapterGroup);
        if (unused.Count == 0) return;
        foreach (var m in owner.Methods.Value)
            if (unused.Contains(m.OverrideAdapterGroup) && m.ColdEntryMachine is { } machine) machines.Add(machine.Name);
        owner.Methods = owner.Methods.Value.Where(m => !unused.Contains(m.OverrideAdapterGroup)).ToArray();
    }

    // Projected storage is a second physical TypeDef. Bind the selected method
    // on that TypeDef before its copied Kotlin declaration identities are retired.
    internal void FinishFixedSlot(IBirTypeDeclaration owner, BirFunctionDeclaration method,
        HashSet<string> emitted, List<IBirTypeDeclaration> generated)
    {
        if (method.FixedSlotFallback is not {} deferred) return;
        var target = owner.Methods.Value.Single(m => m.RoundtripDeclarationIdentity.OrDefault()?.DeclarationId.OrDefault() == deferred.Target);
        var locations = deferred.Locations.ToDictionary(i => method.Params.Value[i].Name.Value,
            i => ((TypeNode.ByRef)SignatureValueTypes.Of(method.Params.Value[i].Type.Value)).Of);
        ((BirGraphValue)method.Body.Value[deferred.Statement]).VisitOwnChildSlots((_, _, slot) => {
            var call = (BirCallInstance)slot.Value;
            call.MethodWitnessFacts = deferred.Proofs;
            var invoke = invocations.FixedIntersection(new(call), owner.Name.Value, TypeParameterFrame.Declarations(owner).ToArray(),
                references, emitted, generated, lexicalDefinition: owner, locations: locations, physicalTarget: target);
            var result = FBoundStarProjectionErasure.SubstituteDeclarationTypeArguments(target.Ret.Value,
                ((TypeNode.Fqn)call.OwnerType.Value).Args ?? [], call.TypeArgs.Value);
            slot.Replace((BirGraphValue)StringCharSequenceBridge.AdaptPhysicalSlotReturn((IBirExpression)invoke, result, method.Ret.Value) ?? invoke);
        });
        method.FixedSlotFallback = null;
    }

    internal void Finish(BirFileDeclaration[] files, HashSet<string> emittedTypes)
    {
        emittedTypes.UnionWith(operationTypes);
        // Even a consumer with no locally projected declaration can select an
        // imported weak slot. Close it in the caller's actual lexical frame.
        if (referenceBuild) { BindRemainingApplications(files); return; }
        foreach (var file in files)
        {
            var generated = new List<IBirTypeDeclaration>();
            void Process(BirFunctionDeclaration[] methods, IBirTypeDeclaration owner)
            {
                var helpers = new List<BirFunctionDeclaration>();
                var ordinal = 0;
                string Allocate(string role) {
                    string name;
                    do name = (owner?.Name.Value ?? file.FileClass.Value) + "$" + role + ordinal++; while (!emittedTypes.Add(name));
                    return name;
                }
                foreach (var method in methods)
                    if (method.FixedSlotFallback != null) FinishFixedSlot(owner, method, emittedTypes, generated);
                    else if (MethodEntry(method) != null) RewriteBody(method, owner,
                        owner == null ? [] : TypeParameterFrame.Declarations(owner).ToArray(), [], Allocate, generated, helpers);
                    else if (method.LocalBodyEntry.HasValue)
                        method.Visit(n => Bind(n, new(owner == null ? [] : TypeParameterFrame.Declarations(owner).ToArray(), []),
                            new(method.TypeParams.OrEmpty(), [])));
                    else method.Visit(n => {
                        if (n.SelectedGenericCallable is {} selected && Find(selected)?.Plan.ReferenceIndices is { Length: > 0 })
                            Bind(n, new(owner == null ? [] : TypeParameterFrame.Declarations(owner).ToArray(), []),
                                new(method.TypeParams.OrEmpty(), []));
                    });
                if (helpers.Count != 0) owner.Methods = owner.Methods.OrEmpty().Concat(helpers).ToArray();
            }
            Process(file.Methods.OrEmpty(), null);
            foreach (var type in file.Types.OrEmpty()) Process(type.Methods.OrEmpty(), type);
            if (generated.Count != 0) file.Types = file.Types.OrEmpty().Concat(generated).ToArray();
        }
        BindRemainingApplications(files);
    }

    static TypeNode[] AddressSignature(ProjectedMethodPlan plan, BirFunctionDeclaration declaration) =>
        plan.AddressOperands.Length == 0 && plan.NativeIndices.Length == 0 ? [] : declaration.Params.Value.Select(FunctionSignatureIdentity.SignatureType).ToArray();

    Entry Find(string id)
    {
        if (entries.TryGetValue(id, out var found)) return found;
        if (!references.TryDeclarationIdentity(EntryId(id), out var name, out _, out _, out _) ||
            references.PhysicalMethodTypeParametersInPhysicalFrame(id, nativeNames: true) is not { } parameters ||
            references.ProjectedMethod(EntryId(id)) is not { } plan) return null;
        return entries[id] = new(name, plan, parameters, references.NullableMethodFrame(plan.RepresentationDeclaration ?? id),
            plan.AddressOperands.Length == 0 && plan.NativeIndices.Length == 0 ? [] : references.SelectedPhysicalMethod(id).GetParameters()
                .Select(references.DeclarationSignatureSlot).ToArray());
    }

    TypeNode Proof(TypeNode ordinary, TypeNode bound, Scope ownerScope, Scope methodScope, TypeNode[] required = null)
    {
        var evidence = Scope.For(ordinary, ownerScope, methodScope).ToArray();
        return evidence.OrderByDescending(e => e.Slot.Bound == bound).FirstOrDefault(e =>
            (required ?? [bound]).All(c => e.Slot.Proves(c, Proves))).Variable;
    }

    bool Proves(TypeNode source, TypeNode target) =>
        ProjectedConstructorPlan.Proves(source, target, classHierarchy.Value, references);

    TypeNode NativeBodyArgument(TypeNode.Tv source, BirTypeParameter formal, TypeNode[] arguments,
        Scope owner, Scope method)
    {
        if (Scope.MaterialFact(source, owner, method) is { } established) return established.Variable;
        if (Scope.Storage(source, owner, method) is not { } storage || formal.SpecialConstraints.OrEmpty().Length != 0
            || formal.Constraints.OrEmpty() is not { Length: > 0 } constraints) return source;
        // The native application has a single CLR argument for the whole conjunction.
        // Separate interface witnesses are not interchangeable with that stored value type.
        var scope = source.Scope == "method" ? method : owner;
        var required = constraints.Select(c => SupertypeGraph.SubstOwnerTvs(c, arguments)).ToArray();
        bool Satisfies(TypeNode.Tv variable) => required.All(c =>
            scope.Parameters[variable.I].Constraints.OrEmpty().Any(bound => Proves(bound, c)));
        return !Satisfies(source) && Satisfies(storage) ? storage : source;
    }

    TypeNode[] ForwardConstructor(ProjectedConstructorPlan plan, TypeNode[] actual, Scope owner, Scope method)
    {
        if (!plan.Facets.Any(f => actual[f.Primary] is TypeNode.Tv tv &&
                (tv.Scope == "method" ? method : owner).Slots?.Any(s => s.OwnerIndex == tv.I) == true)) return null;
        return plan.Close(actual, (f, required) =>
        {
            if (!f.NativeArgument || actual[f.Primary] is not TypeNode.Tv v)
                return Proof(actual[f.Primary], required, owner, method) ?? actual[f.Primary];
            var scope = v.Scope == "method" ? method : owner;
            var requiredRows = f.Constraints.Select(c => SupertypeGraph.SubstOwnerTvs(c, actual)).ToArray();
            if (Scope.MaterialFact(v, owner, method) is { } material &&
                requiredRows.All(c => material.Formal.Constraints.OrEmpty().Any(b => Proves(b, c))))
                return material.Variable;
            var slots = scope.Slots ?? [];
            var sameValue = slots.Select((slot, i) => (slot, i))
                .Where(row => row.slot.OwnerIndex == v.I && row.slot.NativeArgument).ToArray();
            if (sameValue.Length == 0)
            {
                // A method's interface facets can be independent. Its physical storage
                // parameter, when present, can prove the native conjunction on one type.
                if (Scope.Storage(v, owner, method) is { } storage)
                {
                    var bounds = scope.Parameters[storage.I].Constraints.OrEmpty();
                    if (f.Constraints.All(c => bounds.Any(b => ProjectedConstructorPlan.Proves(b,
                        SupertypeGraph.SubstOwnerTvs(c, actual), classHierarchy.Value, references)))) return storage;
                }
                return Proof(v, required, owner, method) ?? v;
            }
            // All native uses of this source argument share one CLR value witness.
            // The open caller formal must prove the callee's whole conjunction.
            foreach (var row in sameValue)
                if (requiredRows.All(c => row.slot.Proves(c, Proves)))
                    return new TypeNode.Tv(v.Scope, scope.Parameters.Length + row.i);
            throw new InvalidOperationException("Projected constructor argument lacks its declared native interface proof");
        });
    }

    BirCallStatic ReferencedConstruction(BirNew creation, Scope owner, Scope method)
    {
        if (creation.ReferencedProjectedFactoryId is not { } id ||
            creation.Type.OrDefault() is not TypeNode.Fqn { Args: { } actual }) return null;
        var selected = references.SelectedPhysicalMethod(id);
        var plan = references.ConstructorPlan(id, nativeNames: true) ?? throw new InvalidOperationException($"No projected constructor for '{id}'");
        if (ForwardConstructor(plan, actual, owner, method) is not { } arguments) return null;
        var target = ClrMemberResolution.MemberRefOf(null, references, selected, "method", selected.DeclaringType, null);
        var signature = target.ParameterTypes.Select(t =>
            FBoundStarProjectionErasure.SubstituteDeclarationTypeArguments(t, [], arguments)).ToArray();
        return new BirCallStatic { Owner = target.DeclaringType, Method = target.Name, TypeArgs = arguments,
            Args = creation.Args, Sig = signature, Ret = target.ReturnType, MemberRef = BirMemberReference.FromPhysical(target) };
    }

    void ProjectClosureCreation(BirGraphValue node, Scope owner, Scope method, Func<string,string> allocate,
        List<IBirTypeDeclaration> generated)
    {
        if (method.Slots is not { Length: > 0 } && owner.Slots is not { Length: > 0 } &&
            method.Representations?.NullableIndices.Count is not > 0 && method.NativePlan?.NativeIndices.Length is not > 0 &&
            owner.CapturedNullable == null && owner.CapturedNative == null) return;
        var target = node is BirNewClosure c ? c.ClosureType.OrDefault() : ((BirNewSam)node).SamType.OrDefault();
        if (target is not TypeNode.Fqn named || node.ApplicationTypeArguments.OrDefault() is not { } args) return;
        if (ProjectLexicalType(named, args, owner, method, allocate, generated) is not { } projected) return;
        node.ApplicationTypeArguments = projected.Args;
        if (node is BirNewClosure lambda) lambda.ClosureType = new TypeNode.Fqn(projected.Name);
        else ((BirNewSam)node).SamType = new TypeNode.Fqn(projected.Name);
    }

    TypeNode.Fqn ProjectLexicalType(TypeNode.Fqn named, TypeNode[] args, Scope owner, Scope method,
        Func<string,string> allocate, List<IBirTypeDeclaration> generated)
    {
        BirClassDefinition Definition(string name) => classes.GetValueOrDefault(name)
            ?? generated.OfType<BirClassDefinition>().FirstOrDefault(t => t.Name.OrDefault() == name);
        if (Definition(named.Name) is not { } original) return null;
        var parameters = TypeParameterFrame.Declarations(original).ToArray();
        if (parameters.Length != args.Length) return null;
        // Capture placement has re-bound the lexical frame to this closure's
        // TypeDef. Re-bind each split edge too, using the creation's actual
        // arguments; the exact closure keeps its original constraints.
        Dictionary<int,TypeNode.Tv> lost = null;
        for (int i = 0; i < args.Length; i++)
            if (Scope.SplitNullable(args[i], owner, method) is { } companion)
                foreach (var upper in parameters[i].Constraints.OrEmpty().OfType<TypeNode.Tv>())
                    if (upper.Scope == "type" && upper.I < args.Length && args[upper.I] == companion)
                        (lost ??= new())[i] = upper;
        var material = args.Select((arg, i) => (index: i, witness: Scope.MaterialFact(arg, owner, method)))
            .Where(r => r.witness != null).ToArray();
        if (Frame(parameters, lost != null || material.Length != 0) is not { } frame) return null;
        var prefix = original.CapturedTypeParams.OrEmpty().Length;
        var split = frame.Interfaces.Where(s => s.OwnerIndex >= prefix && Proof(args[s.OwnerIndex],
            SupertypeGraph.SubstOwnerTvs(s.Bound, args), owner, method) != null).ToArray();
        if (split.Length == 0 && lost == null && material.Length == 0) return null;
        var copy = (BirClassDefinition)original.DeepClone();
        copy.Name = allocate("closure");
        copy.RoundtripTypeHeaderPending = default;
        copy.RoundtripSupertypesPre = default;
        var full = ProjectedClassStorageFrame.Declare(parameters, split);
        if (lost != null)
            foreach (var (ordinary, nullable) in lost)
                full[ordinary].Constraints = full[ordinary].Constraints.OrEmpty().Where(b => b != nullable).ToArray();
        split = split.Select((s, i) => s with { Evidence = full[parameters.Length + i].Constraints.OrEmpty() }).ToArray();
        var arguments = args.Concat(split.Select(s => Proof(args[s.OwnerIndex],
            SupertypeGraph.SubstOwnerTvs(s.Bound, args), owner, method))).Concat(material.SelectMany(r =>
            r.witness.Reference == null ? new[] { r.witness.Variable } : new[] { r.witness.Variable, r.witness.Reference })).ToArray();
        Dictionary<int, Scope.MaterialWitness> captured = null;
        foreach (var (index, witness) in material)
        {
            var row = (BirTypeParameter)witness.Formal.DeepClone();
            if (row.Constraints.HasValue) row.Constraints = row.Constraints.Value.Select(c => TypedExecutableFrames.RewriteType(c, v =>
                Array.IndexOf(arguments, v) is var i && i >= 0 ? new TypeNode.Tv("type", i)
                    : throw new InvalidOperationException("Material argument constraint is outside the captured frame"))).ToArray();
            (captured ??= new()).Add(index, new(new TypeNode.Tv("type", full.Length), row,
                witness.Reference == null ? null : new TypeNode.Tv("type", full.Length + 1)));
            full = full.Append(row).ToArray();
            if (witness.Reference != null)
                full = full.Append(new BirTypeParameter { Name = "$refMaterial" + index, SpecialConstraints = new[] { "class" } }).ToArray();
        }
        copy.TypeParams = full[prefix..];
        if (prefix != 0) copy.CapturedTypeParams = full[..prefix];
        var children = new List<IBirTypeDeclaration>();
        Dictionary<int, TypeNode.Tv> storage = null;
        for (int i = 0; i < args.Length; ++i)
            if (Scope.Storage(args[i], owner, method) is { } witness && Array.IndexOf(args, witness) is var j && j >= 0)
                (storage ??= new())[i] = new TypeNode.Tv("type", j);
        var width = full.Length - parameters.Length;
        FBoundStarProjectionErasure.ProjectLexicalClosures(copy, named.Name, width, full, Definition, allocate, children);
        copy.Visit(n => n.RewriteOwnTypeFieldsInPlace((field, _, type) =>
            n is BirMemberReference && field != "declaringType" ? type :
            SourceDeclarationSignature.RewriteType(type, f => f.Name == named.Name && f.Args?.Length == args.Length
                ? new TypeNode.Fqn(copy.Name.Value, f.Args.Concat(Enumerable.Range(args.Length, width)
                    .Select(i => (TypeNode)new TypeNode.Tv("type", i))).ToArray()) : f)));
        foreach (var closure in children.Append(copy).OfType<BirClassDefinition>())
        {
            classes.Add(closure.Name.Value, closure);
            var helpers = new List<BirFunctionDeclaration>();
            RewriteBody(closure, closure, full[..parameters.Length], split, allocate, generated, helpers, lost, captured, storage);
            if (helpers.Count != 0) closure.Methods = closure.Methods.OrEmpty().Concat(helpers).ToArray();
            generated.Add(closure);
        }
        return new TypeNode.Fqn(copy.Name.Value, arguments);
    }

    bool Bind(BirGraphValue node, Scope ownerScope, Scope methodScope, bool locationBody = false)
    {
        if (node.SelectedGenericCallable is not { } id ||
            node.ApplicationTypeArguments.OrDefault() is not { } actual) return false;
        if (Find(id) is not { } target || actual.Length != target.Original.Length) return false;
        var member = node switch {
            BirCallStatic s => s.MemberRef.OrDefault(), BirCallInstance c => c.MemberRef.OrDefault(),
            BirNewDelegate d => d.MemberRef.OrDefault(), BirNewBoundDelegate d => d.MemberRef.OrDefault(),
            BirConstrainedCall c => c.MemberRef.OrDefault(), _ => null };
        var ownerType = member?.DeclaringType.OrDefault() ?? FBoundStarProjectionErasure.SourceSelectedOwner(node);
        var ownerArgs = (ownerType as TypeNode.Fqn)?.Args ?? [];
        bool Declares(TypeNode argument, TypeNode bound) => argument is TypeNode.Tv v
            && (v.Scope == "method" ? methodScope : ownerScope).Parameters is { } parameters
            && v.I < parameters.Length && parameters[v.I].Constraints.OrEmpty().Contains(bound);
        var application = target.Plan.Close(actual, ownerArgs, node.MethodWitnessFacts.OrEmpty(),
            target.Original, target.Representations, node is BirCallInstance super && super.Super.OrDefault() ||
                locationBody && target.Plan.AddressOperands.Length != 0 || RequiresNullableSplit() ||
                target.Plan.NativeIndices.Any(i => Material(i) != actual[i]) ||
                target.Plan.ReferenceIndices.Any(i => Upper(i, false, actual[i]) != actual[i] || Upper(i, true, Material(i)) != Material(i)),
            (t, b, rows) => Proof(t, b, ownerScope, methodScope, rows), Declares,
            Proves);
        TypeNode Material(int i) => node.MaterialMethodArguments?.GetValueOrDefault(i) ??
            Scope.Material(actual[i], ownerScope, methodScope);
        TypeNode Upper(int i, bool material, TypeNode arg)
        {
            if (actual[i] is TypeNode.Tv { Scope: "method" } input && methodScope.NativePlan is {} plan &&
                plan.UpperPosition(input.I, material) is >= 0 and var p &&
                arg == (material ? plan.NativeArgument(input) : input)) return new TypeNode.Tv("method", p);
            var sourceIndex = target.Representations?.PhysicalSlot(i).SourceIndex ?? i;
            foreach (var candidate in (node as BirCallInstance)?.SelectedOwnerUppers?.GetValueOrDefault(sourceIndex) ?? [])
            {
                var upper = references.PhysicalConstraintType(candidate);
                if (arg == upper || Declares(arg, upper) || Proves(arg, upper)) return upper;
            }
            foreach (var edge in target.Original[i].Constraints.OrEmpty().OfType<TypeNode.Tv>())
            {
                var upper = (edge.Scope == "method" ? actual : ownerArgs).ElementAtOrDefault(edge.I);
                if (upper != null && (arg == upper || Declares(arg, upper) || Proves(arg, upper))) return upper;
            }
            if (arg is TypeNode.Tv v && (v.Scope == "method" ? methodScope : ownerScope).Parameters?.ElementAtOrDefault(v.I)
                ?.Constraints.OrEmpty().OfType<TypeNode.Tv>().FirstOrDefault() is { } lexical) return lexical;
            return arg;
        }
        TypeNode ReferenceMaterial(int i)
        {
            var m = Material(i);
            var proof = Scope.MaterialFact(actual[i], ownerScope, methodScope);
            if (proof?.Variable == m && proof.Reference != null) return proof.Reference;
            if (m is TypeNode.Array or TypeNode.Fn) return m;
            if (m is TypeNode.Fqn f && (kinds.GetValueOrDefault(f.Name) is "interface" or "class" ||
                references.IsInterfaceType(f) || references.IsClassType(f))) return m;
            if (m is TypeNode.Tv v)
            {
                var rows = v.Scope == "method" ? methodScope.NativePlan?.Parameters ?? methodScope.Parameters : ownerScope.Parameters;
                var row = proof?.Variable == m ? proof.Formal : rows?.ElementAtOrDefault(v.I);
                if (row != null && (row.SpecialConstraints.OrEmpty().Contains("class") || Nominal(row))) return m;
                if (m != actual[i]) throw new InvalidOperationException("Split open material has no forwarded reference evidence");
            }
            return new TypeNode.Fqn("System.Object");
        }
        bool RequiresNullableSplit()
        {
            if (target.Representations is not { } layout) return false;
            foreach (var source in layout.NullableIndices)
            {
                var ordinary = layout.SourcePosition(source);
                var nullable = layout.NullableVariable(new TypeNode.Tv("method", source));
                if (target.Original[ordinary].Constraints.OrEmpty().Contains(nullable) &&
                    !target.Plan.Parameters[ordinary].Constraints.OrEmpty().Contains(nullable) &&
                    Scope.SplitNullable(actual[ordinary], ownerScope, methodScope) is { } lexical &&
                    lexical == actual[nullable.I]) return true;
            }
            return false;
        }
        node.ApplicationTypeArguments = application.Arguments;
        node.MethodWitnessFacts = default;
        if (!application.Projected) return true;
        if (target.Plan.AddressOperands.Length != 0 || target.Plan.NativeIndices.Length != 0)
        {
            // Specialize the location arguments again when the actual field or
            // local's CLR storage has been bound. Operand evaluation is unchanged.
            var locationTypes = target.Plan.AddressArguments(target.Signature, ownerArgs, actual);
            node.ApplicationTypeArguments = application.Arguments.Concat(target.Plan.NativeIndices.Select(Material)).Concat(locationTypes)
                .Concat(target.Plan.ReferenceIndices.Select(ReferenceMaterial))
                .Concat(target.Plan.UpperArguments((i, material) => Upper(i, material, material ? Material(i) : application.Arguments[i]))).ToArray();
            // The selected MethodDef has an open location variable here, even
            // when the exact entry used the primary method variable instead.
            TypeNode[] Open(OptionalField<TypeNode[]> sig) => target.Plan.SelectedSignature(sig.OrDefault() ?? target.Signature, references);
            OptionalField<TypeNode[]> Link(OptionalField<TypeNode[]> descriptor) =>
                descriptor.HasValue ? OptionalField<TypeNode[]>.Of(Open(descriptor)) : descriptor;
            OptionalField<TypeNode> Result(OptionalField<TypeNode> ret) =>
                ret.HasValue ? OptionalField<TypeNode>.Of(target.Plan.NativeUse(ret.Value, references)) : ret;
            switch (node) {
                case BirCallStatic c: c.Sig = Open(c.Sig); c.CalleeParams = Link(c.CalleeParams); c.CalleeRet = Result(c.CalleeRet); break;
                case BirCallInstance c: c.Sig = Open(c.Sig); c.CalleeParams = Link(c.CalleeParams); c.CalleeRet = Result(c.CalleeRet); break;
                case BirConstrainedCall c: c.Sig = Open(c.Sig); c.CalleeParams = Link(c.CalleeParams); c.CalleeRet = Result(c.CalleeRet); break;
                case BirNewDelegate d: d.Sig = Open(d.Sig); d.CalleeParams = Link(d.CalleeParams); d.CalleeRet = Result(d.CalleeRet); break;
                case BirNewBoundDelegate d: d.Sig = Open(d.Sig); d.CalleeParams = Link(d.CalleeParams); d.CalleeRet = Result(d.CalleeRet); break;
            }
            if (target.Plan.AddressOperands.Length != 0 && node is (BirCallStatic or BirCallInstance or BirConstrainedCall))
                node.LocationOperandFacts = target.Plan.AddressOperands;
        }
        var selected = member == null ? null : references.SelectedPhysicalMethod(EntryId(id)) as System.Reflection.MethodInfo;
        if (member != null && selected == null)
            throw new InvalidOperationException("Selected projected MethodDef is absent");
        var reference = selected == null ? default(OptionalField<BirMemberReference>) :
            OptionalField<BirMemberReference>.Of(BirMemberReference.FromPhysical(ClrMemberReferences.MethodOnPhysicalOwner(
                references, null, selected, selected.DeclaringType, ownerArgs)));
        switch (node) {
            case BirCallStatic s: s.Method = target.Name; if (reference.HasValue) s.MemberRef = reference; break;
            case BirCallInstance c: c.Method = target.Name; if (reference.HasValue) c.MemberRef = reference; break;
            case BirConstrainedCall c: c.Method = target.Name; if (reference.HasValue) c.MemberRef = reference; break;
            case BirNewDelegate d: d.Method = target.Name; if (reference.HasValue) d.MemberRef = reference; break;
            case BirNewBoundDelegate d: d.Method = target.Name; if (reference.HasValue) d.MemberRef = reference; break;
            default: throw new InvalidOperationException("Expected a selected generic call");
        }
        return true;
    }
}
