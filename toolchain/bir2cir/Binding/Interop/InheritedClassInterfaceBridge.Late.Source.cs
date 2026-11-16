using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class InheritedClassInterfaceBridge
{
    // These are THIS phase's physical declarations, not the original source
    // inventory. Methods remain live as forwarders are appended to an owner.
    sealed class SourceDefinition(IBirTypeDeclaration declaration)
    {
        internal IBirTypeDeclaration Declaration => declaration;
        internal string Name => declaration.Name.OrDefault();
        internal string Kind => declaration.Kind.OrDefault();
        // Match this owner's late collector, not the earlier hierarchy's
        // captured + own source frame. FBound already supplied the live vector.
        internal int Arity => declaration.TypeParams.OrEmpty().Length;
        internal TypeNode.Fqn Base => declaration.Base.OrDefault() as TypeNode.Fqn;
        internal IEnumerable<TypeNode.Fqn> Interfaces => declaration.Interfaces.OrEmpty().OfType<TypeNode.Fqn>();
        internal BirFunctionDeclaration[] Methods => declaration.Methods.OrEmpty();
        internal TypeNode[] OwnArguments => Enumerable.Range(0, Arity).Select(index => (TypeNode)new TypeNode.Tv("type", index)).ToArray();
    }

    readonly record struct SourceMethodMatch(TypeNode.Fqn ConstructedOwner, BirFunctionDeclaration Method);

    internal static void ApplyAll(IReadOnlyList<BirFileDeclaration> roots,
        Action<IBirTypeDeclaration, IReadOnlyList<BirFunctionDeclaration>> onAppended = null)
    {
        var definitions = new Dictionary<string, SourceDefinition>(StringComparer.Ordinal);
        foreach (var declaration in roots.SelectMany(root => root.Types.OrEmpty()))
            if (declaration.Name.HasValue) definitions[declaration.Name.Value] = new SourceDefinition(declaration);
        var classes = definitions.Values.Where(definition => definition.Kind == "class").ToArray();
        // Finish every owned slot first. A derived class must not observe an
        // inherited nonvirtual method which will become virtual in this pass.
        foreach (var owner in classes) NormalizeOwnedSourceSlots(owner, definitions);
        foreach (var owner in classes)
        {
            var appended = AddSourceBridges(owner, definitions);
            if (appended.Count != 0) onAppended?.Invoke(owner.Declaration, appended);
        }
    }

    static IEnumerable<TypeNode.Fqn> ReachableSourceInterfaces(SourceDefinition owner,
        IReadOnlyDictionary<string, SourceDefinition> definitions)
    {
        var pending = new Queue<TypeNode.Fqn>(owner.Interfaces);
        var seen = new HashSet<TypeNode.Fqn>();
        while (pending.TryDequeue(out var application))
        {
            if (!seen.Add(application)) continue;
            yield return application;
            if (!definitions.TryGetValue(application.Name, out var definition)) continue;
            var arguments = EffectiveArgs(application, definition.Arity);
            if (arguments == null) continue;
            foreach (var parent in definition.Interfaces)
                pending.Enqueue((TypeNode.Fqn)SubstOwnerTvs(parent, arguments));
        }
    }

    static IEnumerable<BirFunctionDeclaration> ExactSourceMethods(IEnumerable<BirFunctionDeclaration> methods,
        string name, int arity, TypeNode[] parameters, TypeNode result, TypeNode[] ownerArguments, TypeNode[] signature = null)
    {
        foreach (var method in methods)
        {
            if (method.Name.OrDefault() != name || method.TypeParams.OrEmpty().Length != arity
                || !method.Params.HasValue || method.Params.Value.Length != parameters.Length) continue;
            var exact = true;
            for (var index = 0; index < parameters.Length; index++)
            {
                var parameter = method.Params.Value[index];
                if (parameter.Type.OrDefault() is not { } type || SubstOwnerTvs(type, ownerArguments) != parameters[index]
                    || signature != null && SubstOwnerTvs(FunctionSignatureIdentity.SignatureType(parameter), ownerArguments) != signature[index])
                { exact = false; break; }
            }
            if (exact && method.Ret.OrDefault() is { } ret && SubstOwnerTvs(ret, ownerArguments) == result) yield return method;
        }
    }

    static void NormalizeOwnedSourceSlots(SourceDefinition owner, IReadOnlyDictionary<string, SourceDefinition> definitions)
    {
        if (!owner.Declaration.Methods.HasValue) return;
        foreach (var application in ReachableSourceInterfaces(owner, definitions))
        {
            if (!definitions.TryGetValue(application.Name, out var iface) || iface.Kind != "interface") continue;
            var arguments = EffectiveArgs(application, iface.Arity);
            if (arguments == null) continue;
            foreach (var slot in iface.Methods)
            {
                if (slot.Static.OrDefault() || !slot.Abstract.OrDefault() || !slot.Name.HasValue || !slot.Params.HasValue) continue;
                var parameters = slot.Params.Value.Select(parameter => SubstOwnerTvs(parameter.Type.OrDefault(), arguments)).ToArray();
                var result = SubstOwnerTvs(slot.Ret.OrDefault(), arguments);
                if (parameters.Any(type => type == null) || result == null) continue;
                var signature = slot.Params.Value.Select(parameter => SubstOwnerTvs(FunctionSignatureIdentity.SignatureType(parameter), arguments)).ToArray();
                var selected = ExactSourceMethods(owner.Methods, slot.Name.Value, slot.TypeParams.OrEmpty().Length,
                    parameters, result, owner.OwnArguments, signature)
                    .Where(method => !method.Static.OrDefault() && method.Vis.OrDefault() is null or "public").ToArray();
                if (selected.Length == 1) selected[0].Virtual = true;
            }
        }
    }

    static IReadOnlyList<BirFunctionDeclaration> AddSourceBridges(SourceDefinition owner,
        IReadOnlyDictionary<string, SourceDefinition> definitions)
    {
        if (!owner.Declaration.Methods.HasValue) owner.Declaration.Methods = Array.Empty<BirFunctionDeclaration>();
        var appended = new List<BirFunctionDeclaration>();
        foreach (var application in ReachableSourceInterfaces(owner, definitions))
        {
            if (!definitions.TryGetValue(application.Name, out var iface) || iface.Kind != "interface") continue;
            var arguments = EffectiveArgs(application, iface.Arity);
            if (arguments == null) continue;
            foreach (var slot in iface.Methods.ToArray())
            {
                if (slot.Static.OrDefault() || !slot.Abstract.OrDefault() || !slot.Name.HasValue || !slot.Params.HasValue) continue;
                var parameters = slot.Params.Value.Select(parameter => SubstOwnerTvs(parameter.Type.OrDefault(), arguments)).ToArray();
                var result = SubstOwnerTvs(slot.Ret.OrDefault(), arguments);
                if (parameters.Any(type => type == null) || result == null) continue;
                var own = ExactSourceMethods(owner.Methods, slot.Name.Value, slot.TypeParams.OrEmpty().Length,
                    parameters, result, owner.OwnArguments)
                    .Where(method => !method.Static.OrDefault() && method.Vis.OrDefault() is null or "public").ToArray();
                if (own.Length != 0 || owner.Declaration is BirClassDefinition concrete && concrete.Abstract.OrDefault()) continue;
                var target = FindSelectedSourceBaseMethod(owner, definitions, iface.Name, slot.Name.Value,
                    slot.TypeParams.OrEmpty().Length, parameters, result, slot.DeclarationId.OrDefault());
                if (target == null) continue;
                var bridge = BuildSourceBridge(application, arguments, slot, parameters, result, target.Value);
                var identity = target.Value.Method.DeclarationId.OrDefault()
                    ?? throw new InvalidOperationException("Selected inherited implementation has no declaration identity");
                bridge.DeclarationId = DeclarationIdentityBinding.InheritedProjectionId(owner.Name, new[] { identity });
                bridge.DeclarationSourceName = bridge.Name;
                bridge.Generated = true;
                owner.Declaration.Methods = owner.Methods.Append(bridge).ToArray();
                appended.Add(bridge);
            }
        }
        return appended;
    }

    static SourceMethodMatch? FindSelectedSourceBaseMethod(SourceDefinition owner,
        IReadOnlyDictionary<string, SourceDefinition> definitions, string interfaceName, string name, int arity,
        TypeNode[] parameters, TypeNode result, string slotIdentity)
    {
        if (owner.Declaration is not BirClassDefinition { InheritedClassMethods.HasValue: true } declaration) return null;
        var selected = declaration.InheritedClassMethods.Value.Where(fact => fact.Member.OrDefault() == name
            && fact.Params.HasValue && fact.Params.Value.Length == parameters.Length
            && (slotIdentity != null ? fact.Overrides.OrEmpty().Any(edge => edge.DeclarationId.OrDefault() == slotIdentity)
                : fact.Params.Value.Select(parameter => parameter.Type.OrDefault()).SequenceEqual(parameters))
            && fact.Ret.OrDefault() == result && fact.InheritedImplementation.HasValue
            && fact.InheritedImplementation.Value.TypeParams.OrEmpty().Length == arity && fact.Overrides.HasValue
            && fact.Overrides.Value.Any(edge => edge.Owner.OrDefault() is TypeNode.Fqn type && type.Name == interfaceName
                && edge.Member.OrDefault() == name && edge.Kind.OrDefault() == "method")).ToArray();
        if (selected.Length != 1) return null;
        var target = selected[0].InheritedImplementation.Value;
        var selectedOwner = (target.Owner.OrDefault() as TypeNode.Fqn)?.Name;
        var selectedMember = target.Member.OrDefault();
        var current = owner.Base == null ? null : (TypeNode.Fqn)SubstOwnerTvs(owner.Base, owner.OwnArguments);
        var seen = new HashSet<TypeNode.Fqn>();
        while (current != null && seen.Add(current) && definitions.TryGetValue(current.Name, out var definition))
        {
            var arguments = EffectiveArgs(current, definition.Arity);
            if (arguments == null) return null;
            if (current.Name == selectedOwner)
            {
                var matches = ExactSourceMethods(definition.Methods, selectedMember, arity, parameters, result, arguments)
                    .Where(method => !method.Static.OrDefault() && !method.Abstract.OrDefault()
                        && method.Vis.OrDefault() is null or "public"
                        && KotlinOverrideSlotBridge.SameSourceMethodTypeParameterShape(
                            KotlinOverrideSlotBridge.SemanticMethodTypeParameters(method),
                            KotlinOverrideSlotBridge.SemanticMethodTypeParameters(target), arguments, arguments)).ToArray();
                if (matches.Length != 1 || matches[0].Virtual.OrDefault() || matches[0].Override.OrDefault()) return null;
                return new SourceMethodMatch(current, matches[0]);
            }
            current = definition.Base == null ? null : (TypeNode.Fqn)SubstOwnerTvs(definition.Base, arguments);
        }
        return null;
    }

    static BirFunctionDeclaration BuildSourceBridge(TypeNode.Fqn application, TypeNode[] ownerArguments,
        BirFunctionDeclaration slot, TypeNode[] parameters, TypeNode result, SourceMethodMatch target)
    {
        var bridgeParameters = parameters.Select((type, index) => {
            var source = slot.Params.Value[index];
            var parameter = new BirParameter { Name = source.Name.HasValue ? source.Name.Value : "p" + index,
                Type = BirGraphValue.CopyType(type) };
            if (source.NullableGeneric.HasValue) parameter.NullableGeneric = BirGraphValue.CopyType(SubstOwnerTvs(source.NullableGeneric.Value, ownerArguments));
            return parameter;
        }).ToArray();
        var arity = slot.TypeParams.OrEmpty().Length;
        var suspend = slot.Mods.OrDefault()?.Suspend.OrDefault() == true;
        var call = new BirCallInstance { OwnerType = BirGraphValue.CopyType(target.ConstructedOwner), Virtual = false, ClrOwnerResolved = true,
            Recv = new BirThis(), Method = target.Method.Name,
            Sig = target.Method.Params.Value.Select(parameter => BirGraphValue.CopyType(parameter.Type.OrDefault())).ToArray(),
            Sty = BirGraphValue.CopyType(result), DynRet = BirGraphValue.CopyType(result), Ret = BirGraphValue.CopyType(result),
            Args = bridgeParameters.Select(parameter => (IBirExpression)new BirLocal { Name = parameter.Name }).ToArray() };
        if (target.Method.DeclarationId.HasValue) call.DeclarationId = target.Method.DeclarationId;
        if (suspend) call.SuspendCall = true;
        if (arity > 0) call.TypeArgs = Enumerable.Range(0, arity).Select(index => (TypeNode)new TypeNode.Tv("method", index)).ToArray();
        var bridge = new BirFunctionDeclaration { Name = slot.Name, Static = false, Override = false, Virtual = true,
            Abstract = false, Vis = "public", Params = bridgeParameters, Ret = BirGraphValue.CopyType(result),
            Body = result is TypeNode.Fqn { Name: "kotlin.Unit" }
                ? new IBirStatement[] { new BirExpressionStatement { Expr = call } }
                : new IBirStatement[] { new BirReturn { Value = call } },
            Attrs = Array.Empty<BirAttribute>(), Overrides = new[] { new BirOverride {
                Owner = BirGraphValue.CopyType(application), Member = slot.Name, Kind = "method", Arity = parameters.Length,
                DeclarationId = slot.DeclarationId.HasValue ? slot.DeclarationId : OptionalField<string>.Null,
            } } };
        if (suspend)
        {
            bridge.Mods = new BirModifiers { Suspend = true };
            bridge.SuspendRet = BirGraphValue.CopyType(result);
            bridge.SuspendResult = BirGraphValue.CopyType(SubstOwnerTvs(slot.SuspendResult.OrDefault()
                ?? throw new InvalidOperationException("inherited suspend bridge has no logical slot result"), ownerArguments));
        }
        if (slot.NullableGenericRet.HasValue) bridge.NullableGenericRet = BirGraphValue.CopyType(SubstOwnerTvs(slot.NullableGenericRet.Value, ownerArguments));
        if (slot.TypeParams.HasValue)
            bridge.TypeParams = KotlinOverrideSlotBridge.SubstituteOwnerTypeParameterConstraints(slot.TypeParams.Value, ownerArguments);
        if (!suspend)
        {
            bridge.PhysicalSlotBridge = true;
            var descriptor = new BirMethodImpl { Owner = BirGraphValue.CopyType(application), Member = slot.Name, Arity = arity,
                Params = slot.Params.Value.Select(parameter => SubstOwnerTvs(FunctionSignatureIdentity.SignatureType(parameter), ownerArguments)).ToArray(),
                Ret = BirGraphValue.CopyType(result) };
            if (bridge.TypeParams.HasValue && bridge.TypeParams.Value.Length > 0)
                descriptor.TypeParams = bridge.TypeParams.Value.Select(parameter => (BirTypeParameter)parameter.DeepClone()).ToArray();
            bridge.ClrInterfaceImpls = new[] { descriptor };
            DeclarationSignatureIdentity.RecordSelectedImplementationSlot(bridge, slot);
        }
        return MaterializedExecutable.NormalizeSource(bridge);
    }
}
