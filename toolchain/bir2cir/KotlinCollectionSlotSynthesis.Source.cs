using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;
using SelectedMethodHeader = KotlinOverrideSlotBridge.SelectedMethodHeader;
using SelectedParameterHeader = KotlinOverrideSlotBridge.SelectedParameterHeader;

static partial class KotlinCollectionSlotSynthesis
{
    sealed class SourceDefinition(IBirTypeDeclaration declaration)
    {
        internal IBirTypeDeclaration Declaration => declaration;
        internal string Name => declaration.Name.OrDefault();
        internal string Kind => declaration.Kind.OrDefault();
        internal int Arity => TypeParameterFrame.Count(declaration);
        internal TypeNode.Fqn Base => declaration.Base.OrDefault() as TypeNode.Fqn;
        internal IEnumerable<TypeNode.Fqn> Interfaces => declaration.Interfaces.OrEmpty().OfType<TypeNode.Fqn>();
        internal BirFunctionDeclaration[] Methods => declaration.Methods.OrEmpty();
        // The physical self frame includes the captured outer prefix, not only own parameters.
        internal TypeNode[] OwnArguments => Enumerable.Range(0, Arity).Select(index => (TypeNode)new TypeNode.Tv("type", index)).ToArray();
    }

    sealed record SourceTarget(TypeNode.Fqn Owner, SelectedMethodHeader Method, bool Closed,
        IReadOnlyList<BirOverride> Overrides, TypeNode[] DeclarationSignature = null, string DeclarationId = null,
        TypeNode[] QueryBounds = null);

    internal static void ApplyAll(IReadOnlyList<BirFileDeclaration> roots, ReferenceMetadataIndex refs, ValueTypeOracle isValue,
        ValueRepresentationLowering values,
        Action<IBirTypeDeclaration, IReadOnlyList<BirFunctionDeclaration>> onAppended = null)
    {
        var inherited = KotlinOverrideSlotBridge.SelectedInheritedCollectionMethods(roots, refs, isValue);
        var definitions = new Dictionary<string, SourceDefinition>(StringComparer.Ordinal);
        foreach (var declaration in roots.SelectMany(root => root.Types.OrEmpty()))
            if (declaration.Name.HasValue) definitions[declaration.Name.Value] = new(declaration);
        foreach (var owner in definitions.Values.Where(owner => owner.Kind is "class" or "interface").ToArray())
        {
            if (!owner.Declaration.Methods.HasValue) continue;
            var inheritedSlots = InheritedSourceSlots(owner, definitions);
            var bridges = new List<BirFunctionDeclaration>();
            var interfaces = new HashSet<string>(StringComparer.Ordinal);
            var candidates = inherited.GetValueOrDefault(owner.Name)?.Select(candidate => new SourceTarget(candidate.Owner,
                candidate.Method, true, candidate.Overrides, candidate.DeclarationSignature, candidate.DeclarationId, candidate.QueryBounds)).ToArray();
            foreach (var slot in Slots)
            {
                if (inheritedSlots.Members.Contains(SlotKey(slot)) && DeclaresSourceConcretely(owner, slot) == null) continue;
                if (!TryResolveSourceImplementation(owner, slot, definitions, candidates, out var target)) continue;
                if (owner.Methods.Any(method => method.Name.OrDefault() == slot.Bridge)) continue;
                bridges.Add(SourceBridge(owner, slot, target, definitions, refs, values));
                interfaces.Add(slot.SlotInterface);
            }
            if (bridges.Count == 0) continue;
            var currentInterfaces = owner.Declaration.Interfaces.OrEmpty().ToList();
            foreach (var name in interfaces)
                if (!currentInterfaces.OfType<TypeNode.Fqn>().Any(type => type.Name == name)) currentInterfaces.Add(new TypeNode.Fqn(name));
            owner.Declaration.Interfaces = currentInterfaces.ToArray();
            owner.Declaration.Methods = owner.Methods.Concat(bridges).ToArray();
            onAppended?.Invoke(owner.Declaration, bridges);
        }
    }

    static bool SourceOverrides(IEnumerable<BirOverride> overrides, Slot slot) => overrides.Any(marker =>
        MatchesOverride(marker.Owner.OrDefault(), marker.Member.OrDefault(), marker.Kind.OrDefault(),
            marker.Arity.HasValue ? marker.Arity.Value : null, slot));

    static BirFunctionDeclaration DeclaresSourceConcretely(SourceDefinition owner, Slot slot)
    {
        var matches = owner.Methods.Where(method => !method.Static.OrDefault() && !method.Abstract.OrDefault()
            && SourceOverrides(method.Overrides.OrEmpty(), slot)).ToArray();
        if (matches.Length > 1)
            throw new InvalidOperationException($"bir2cir: '{owner.Name}' has {matches.Length} concrete declarations overriding "
                + $"'{slot.DeclaringInterface}.{slot.Member}' — the Kotlin slot to implement is ambiguous. That is a "
                + "frontend-fact defect, not something to resolve by picking one.");
        return matches.SingleOrDefault();
    }

    static InheritedSlotState InheritedSourceSlots(SourceDefinition owner, IReadOnlyDictionary<string, SourceDefinition> definitions)
    {
        var result = new InheritedSlotState();
        var seen = new HashSet<string>(StringComparer.Ordinal) { owner.Name };
        for (var application = owner.Base; application != null && seen.Add(application.Name);)
        {
            if (!definitions.TryGetValue(application.Name, out var parent)) break;
            foreach (var iface in parent.Interfaces)
                if (SlotInterfaces.Contains(iface.Name)) result.Interfaces.Add(iface.Name);
            // A not-yet-visited base still answers from its current declarations.
            foreach (var slot in Slots)
                if (DeclaresSourceConcretely(parent, slot) != null) result.Members.Add(SlotKey(slot));
            foreach (var method in parent.Methods)
            foreach (var implementation in method.ClrInterfaceImpls.OrEmpty())
                if (implementation.Owner.OrDefault() is TypeNode.Fqn iface && implementation.Member.HasValue)
                    result.Members.Add(iface.Name + "\0" + implementation.Member.Value);
            application = parent.Base;
        }
        return result;
    }

    static bool TryResolveSourceImplementation(SourceDefinition owner, Slot slot,
        IReadOnlyDictionary<string, SourceDefinition> definitions, IReadOnlyList<SourceTarget> selected, out SourceTarget target)
    {
        if (DeclaresSourceConcretely(owner, slot) is { } own)
        {
            target = new(new TypeNode.Fqn(owner.Name, owner.Arity == 0 ? null : owner.OwnArguments),
                SelectedMethodHeader.FromSource(own), false, own.Overrides.OrEmpty());
            return true;
        }
        var candidates = selected?.Where(candidate => SourceOverrides(candidate.Overrides, slot)).ToArray();
        if (candidates is { Length: 1 }) { target = candidates[0]; return true; }
        // External defaults carry their own slot interfaces; only current local declarations are searched here.
        foreach (var (application, definition) in LocalSourceSupertypeClosure(owner, definitions))
            if (DeclaresSourceConcretely(definition, slot) is { } method)
            {
                target = new(application, SelectedMethodHeader.FromSource(method), false, method.Overrides.OrEmpty());
                return true;
            }
        target = null;
        return false;
    }

    static IEnumerable<(TypeNode.Fqn, SourceDefinition)> LocalSourceSupertypeClosure(SourceDefinition owner,
        IReadOnlyDictionary<string, SourceDefinition> definitions)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal) { owner.Name };
        var pending = new Queue<TypeNode.Fqn>();
        void Enqueue(SourceDefinition from, TypeNode[] arguments)
        {
            if (from.Base != null) pending.Enqueue((TypeNode.Fqn)Subst(from.Base, arguments));
            foreach (var iface in from.Interfaces) pending.Enqueue((TypeNode.Fqn)Subst(iface, arguments));
        }
        Enqueue(owner, owner.OwnArguments);
        while (pending.TryDequeue(out var application))
        {
            if (!seen.Add(application.Name) || !definitions.TryGetValue(application.Name, out var definition)) continue;
            yield return (application, definition);
            var arguments = application.Args ?? Array.Empty<TypeNode>();
            if (arguments.Length == definition.Arity) Enqueue(definition, arguments);
        }
    }

    static ErasedElementValues.BoundDeclaration SourceMethodBounds(SelectedMethodHeader method) => new(
        method.NullableRepresentationFrame.OrDefault(), method.TypeParams.OrEmpty().Select(parameter => parameter.Constraints.OrEmpty()).ToArray(),
        method.MethodTypeParameterBoundsPre.OrDefault()?.Bounds);

    static ErasedElementValues.BoundDeclaration SourceOwnerBounds(SourceDefinition owner)
    {
        var facts = NullableRepresentationViews.TypeFacts((BirGraphValue)owner.Declaration);
        return new(facts?.NullableFrame, TypeParameterFrame.Declarations(owner.Declaration)
            .Select(parameter => parameter.Constraints.OrEmpty()).ToArray(), facts?.Bounds);
    }

    static BirFunctionDeclaration SourceBridge(SourceDefinition owner, Slot slot, SourceTarget target,
        IReadOnlyDictionary<string, SourceDefinition> definitions, ReferenceMetadataIndex refs, ValueRepresentationLowering values)
    {
        var parameters = target.Method.Params.OrEmpty();
        var declared = parameters.Select(parameter => parameter.Type.OrDefault()).ToArray();
        if (declared.Length != slot.Arity || declared.Any(type => type == null)
            || slot.Arity != 0 && slot.Parameters.Length != slot.Arity)
            throw new InvalidOperationException($"bir2cir: the declaration implementing '{slot.DeclaringInterface}.{slot.Member}' on '{owner.Name}' has "
                + $"{declared.Length} readable parameter(s), but the slot declares {slot.Arity} parameter(s) and {slot.Parameters.Length} carrier fact(s).");
        var arguments = target.Owner.Args ?? Array.Empty<TypeNode>();
        TypeNode InCaller(TypeNode type) => target.Closed ? type : Subst(type, arguments);
        TypeNode QueryGuard(int index)
        {
            var parameter = parameters[index];
            if (!target.Closed && UsesErasedQueryCarrier(declared[index])
                && definitions.TryGetValue(target.Owner.Name, out var definition)
                && (parameter.KotlinType.OrDefault() ?? parameter.NullableGeneric.OrDefault()) is { } semantic
                && GenericQueryBound(semantic, SourceMethodBounds(target.Method), SourceOwnerBounds(definition),
                    type => type is TypeNode.Fqn named && (definitions.TryGetValue(named.Name, out var local)
                        ? local.Kind == "interface" : refs?.IsInterfaceType(named) == true), refs?.NullableTypeFrames) is { } bound)
                return InCaller(bound);
            return InCaller(QueryGuardType(declared[index], target.QueryBounds?.ElementAtOrDefault(index), parameter.NullableFlags.OrDefault()));
        }
        var bridgeParameters = new List<BirParameter>();
        var signature = new List<TypeNode>();
        var callSignature = new List<TypeNode>();
        var callArguments = new List<IBirExpression>();
        for (var index = 0; index < declared.Length; index++)
        {
            var physical = InCaller(declared[index]);
            var carrier = slot.Parameters[index];
            var name = "p" + index;
            var optionalAny = new TypeNode.Nullable(new TypeNode.Fqn("kotlin.Any"));
            var remapping = new TypeNode.Fn(false, optionalAny, new TypeNode[] { optionalAny, optionalAny });
            var slotType = carrier == ParameterCarrier.MapRemapping ? remapping
                : carrier != ParameterCarrier.Exact ? (TypeNode)new TypeNode.Fqn("kotlin.Any") : physical;
            var parameter = new BirParameter { Name = name, Type = BirGraphValue.CopyType(slotType) };
            if (carrier == ParameterCarrier.MapRemapping) parameter.FunctionSignatureType = BirGraphValue.CopyType(remapping);
            bridgeParameters.Add(parameter);
            signature.Add(carrier == ParameterCarrier.MapRemapping
                ? new TypeNode.Mod(false, FunctionSignatureIdentity.DeclarationType(remapping), BirGraphValue.CopyType(remapping))
                : BirGraphValue.CopyType(slotType));
            callSignature.Add(BirGraphValue.CopyType(physical));
            callArguments.Add(carrier switch {
                ParameterCarrier.Exact => SourceLocal(name),
                ParameterCarrier.ErasedValue or ParameterCarrier.ErasedInput or ParameterCarrier.MapRemapping
                    => new BirCast { Type = BirGraphValue.CopyType(physical), E = SourceLocal(name) },
                ParameterCarrier.ErasedCollection => SourceCollectionArgument(slot, parameters[index], physical, name),
                _ => throw new InvalidOperationException($"bir2cir: unknown collection slot carrier {carrier}"),
            });
        }
        var targetReturn = InCaller(target.Method.Ret.OrDefault() ?? new TypeNode.Fqn("kotlin.Boolean"));
        var slotReturn = slot.EraseReturn ? slot.NullableReturn
            ? new TypeNode.Nullable(new TypeNode.Fqn("kotlin.Any")) : (TypeNode)new TypeNode.Fqn("kotlin.Any") : targetReturn;
        var call = new BirCallInstance { OwnerType = BirGraphValue.CopyType(target.Owner), Virtual = true, ClrOwnerResolved = true,
            Recv = new BirThis(), Method = target.Method.ExplicitClrName.OrDefault() ?? target.Method.Name.OrDefault(),
            Sig = callSignature.ToArray(), Ret = BirGraphValue.CopyType(targetReturn), Args = callArguments.ToArray() };
        if (target.Closed)
        {
            call.DynRet = BirGraphValue.CopyType(targetReturn);
            call.CalleeParams = target.DeclarationSignature == null ? OptionalField<TypeNode[]>.Null
                : OptionalField<TypeNode[]>.Of(target.DeclarationSignature.Select(BirGraphValue.CopyType).ToArray());
        }
        if (slot.Kind == "getter" && (!target.Closed || target.Method.ExplicitClrName.OrDefault() == target.Method.Name.OrDefault()))
        {
            if (!target.Method.Property(out var property, out var accessor))
                throw new InvalidOperationException("Map getter bridge has no declaration-owned property identity");
            call.Method = property; call.Prop = accessor;
            KotlinPropertyAccessors.PreserveCallIdentity(call, property, accessor);
        }
        var identity = target.DeclarationId ?? target.Method.DeclarationId.OrDefault();
        if (identity != null) call.DeclarationId = identity;
        IBirExpression forwarded = slot.EraseReturn ? new BirCast { Type = BirGraphValue.CopyType(slotReturn), E = call } : call;
        // Query barriers must run before the argument cast; incompatible erased values return the member's miss value.
        for (var index = declared.Length - 1; index >= 0; index--)
            if (slot.Parameters[index] == ParameterCarrier.ErasedValue)
                forwarded = new BirConditional { Type = BirGraphValue.CopyType(slotReturn),
                    Cond = SourceErasedValueCompatible(QueryGuard(index), "p" + index),
                    Then = OptionalField<IBirExpression>.Of(forwarded), Else = OptionalField<IBirExpression>.Of(SourceErasedQueryMiss(slot)) };
        var bridge = new BirFunctionDeclaration { Name = slot.Bridge, Static = false, Override = false, Virtual = true,
            Abstract = false, ObjectOverride = false, Vis = "private", Generated = true, Params = bridgeParameters.ToArray(),
            Ret = BirGraphValue.CopyType(slotReturn), Body = new IBirStatement[] { new BirReturn { Value = OptionalField<IBirExpression>.Of(forwarded) } },
            Attrs = Array.Empty<BirAttribute>(), PhysicalSlotBridge = true,
            ClrInterfaceImpls = new[] { new BirMethodImpl { Owner = new TypeNode.Fqn(slot.SlotInterface), Member = slot.SlotMember,
                Arity = 0, Params = signature.ToArray(), Ret = BirGraphValue.CopyType(slotReturn) } } };
        if (owner.Kind == "interface") bridge.ClrInterfaceSlotBridge = true;
        // Match the detached old boundary: no class lexical frame or whole-file
        // replay. The generated method's own frame is the complete input here.
        values?.BindMaterializedOperations(bridge);
        return MaterializedExecutable.NormalizeSource(bridge);
    }

    static BirLocal SourceLocal(string name) => new() { Name = name };
    static BirConstant SourceBool(bool value) => new() { Type = new TypeNode.Fqn("kotlin.Boolean"), Value = new BirLiteral.BooleanLiteral(value) };
    static BirConstant SourceNull() => new() { Type = new TypeNode.Nullable(new TypeNode.Fqn("kotlin.Any")), Value = new BirLiteral.NullLiteral() };

    static BirConditional SourceErasedValueCompatible(TypeNode declared, string name) => new() {
        Type = new TypeNode.Fqn("kotlin.Boolean"), Cond = new BirBinaryOperation { Op = "===", Lhs = SourceLocal(name), Rhs = SourceNull() },
        Then = OptionalField<IBirExpression>.Of(SourceNullFits(declared)), Else = new BirIsInstance { Type = BirGraphValue.CopyType(declared), E = SourceLocal(name) },
    };

    static IBirExpression SourceNullFits(TypeNode declared) => declared switch {
        TypeNode.Nullable or TypeNode.Oblivious => SourceBool(true),
        TypeNode.Projection projection => SourceNullFits(projection.Of),
        TypeNode.Tv => new BirCallStatic { Owner = new TypeNode.Fqn("DotKt.Runtime.CompilerServices.StarProjectionRuntimeKt"),
            Method = "starProjectionTypeAcceptsNull", Sig = new TypeNode[] { new TypeNode.Fqn("System.Type") },
            Ret = new TypeNode.Fqn("kotlin.Boolean"), Args = new IBirExpression[] { new BirClassReference { Type = BirGraphValue.CopyType(declared) } } },
        _ => SourceBool(false),
    };

    static IBirExpression SourceErasedQueryMiss(Slot slot) => slot.SlotMember switch {
        "dotktContains" or "dotktMapContainsKey" or "dotktMapContainsValue" or "dotktMapRemoveEntry" or "dotktMapReplaceEntry" => SourceBool(false),
        "dotktMapGet" or "dotktMapRemove" or "dotktMapReplace" => SourceNull(),
        "dotktMapGetOrDefault" => SourceLocal("p1"),
        "dotktIndexOf" or "dotktLastIndexOf" => new BirConstant { Type = new TypeNode.Fqn("kotlin.Int"), Value = new BirLiteral.IntegerLiteral(-1) },
        _ => throw new InvalidOperationException($"bir2cir: erased-value slot '{slot.SlotInterface}.{slot.SlotMember}' has no query-miss value"),
    };

    static BirLocal SourceCollectionArgument(Slot slot, SelectedParameterHeader parameter, TypeNode declared, string name)
    {
        ValidateCollectionArgument(slot, parameter.KotlinType.OrDefault() ?? parameter.NullableGeneric.OrDefault() ?? parameter.Type.OrDefault(), declared);
        return SourceLocal(name);
    }
}
