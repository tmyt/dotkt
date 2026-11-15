using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

sealed partial class ValueRepresentationLowering
{
    // These are continuing declarations, not reconstructed source headers. The
    // callback observes the producer's exact selection before it removes it.
    internal void SynthesizeSlots(IReadOnlyList<BirFileDeclaration> roots, ValueTypeOracle isValue,
        Action<IBirTypeDeclaration, IReadOnlyList<BirFunctionDeclaration>, IReadOnlyList<BirPropertyDeclaration>> retireMembers,
        Action<IBirTypeDeclaration, IReadOnlyList<BirFunctionDeclaration>> appendedMethods)
    {
        if (members.Count == 0) return;
        if (domBound) throw new InvalidOperationException("Source nominal slots used after declaration handoff");
        var selection = KotlinOverrideSlotBridge.CreateSourceSelection(roots);
        var definitions = selection.Definitions;
        var actual = selection.Declarations;
        var completed = new HashSet<string>(StringComparer.Ordinal);
        var active = new HashSet<string>(StringComparer.Ordinal);
        var bridges = new Dictionary<(string Owner, string Slot), BirFunctionDeclaration>();
        static bool Selects(IEnumerable<BirOverride> facts, string identity)
            => facts.Any(fact => fact.DeclarationId.OrDefault() == identity);
        void Apply(SupertypeGraph.Def cls)
        {
            if (completed.Contains(cls.Name) || cls.Kind != "class" || aliases.ContainsKey(cls.Name)) return;
            if (!active.Add(cls.Name)) throw new InvalidOperationException("Cyclic nominal-slot base graph");
            if (cls.Base != null && definitions.TryGetValue(cls.Base.Name, out var parent)) Apply(parent);
            var owner = actual[cls.Name];
            var reachable = SupertypeGraph.Reachable(cls, definitions, refs).Select(edge => edge.spec.Name)
                .ToHashSet(StringComparer.Ordinal);
            var inheritedFacts = owner is BirClassDefinition type
                ? type.InheritedClassMethods.OrEmpty().Select(fact => fact.Overrides.OrEmpty())
                    .Concat(type.InheritedDefaultAccessors.OrEmpty().Select(fact => fact.Overrides.OrEmpty()))
                    .Concat(type.InheritedDefaultMethods.OrEmpty().Select(fact => fact.Overrides.OrEmpty()))
                : Enumerable.Empty<BirOverride[]>();
            foreach (var overrides in owner.Methods.OrEmpty().Select(method => method.Overrides.OrEmpty()).Concat(inheritedFacts))
            foreach (var marker in overrides)
                if (marker.DeclarationId.HasValue && members.TryGetValue(marker.DeclarationId.Value, out var selected))
                    reachable.Add(selected.Owner);
            var sourceMembers = members.Where(member => reachable.Contains(member.Value.Owner)
                && aliases.ContainsKey(member.Value.Owner)).ToArray();
            var inheritedImplementations = sourceMembers.Length == 0 ? null
                : KotlinOverrideSlotBridge.SelectedInheritedImplementationsSource(owner, selection, refs, isValue,
                    (facts, _) => sourceMembers.Any(source => Selects(facts, source.Key)));
            foreach (var (sourceId, source) in sourceMembers)
            {
                var split = source.Slot.LastIndexOf('.');
                var slotOwner = source.Slot[..split];
                var slotMember = source.Slot[(split + 1)..];
                var own = owner.Methods.OrEmpty().Where(method => !method.Static.OrDefault()
                    && Selects(method.Overrides.OrEmpty(), sourceId)).ToArray();
                if (own.Length > 1) throw new InvalidOperationException("Ambiguous selected nominal-slot implementation");
                string inheritedName = null;
                var inheritedAbstract = false;
                TypeNode.Fqn inheritedOwner = null;
                foreach (var (baseType, _) in SupertypeGraph.Reachable(cls, definitions, refs).Where(edge => !edge.isInterface))
                {
                    if (bridges.TryGetValue((baseType.Name, source.Slot), out var local))
                    {
                        inheritedName = local.Name.OrDefault(); inheritedAbstract = local.Abstract.OrDefault();
                        inheritedOwner = baseType;
                    }
                    else if (refs?.ValueSlotBridge(baseType.Name, slotOwner, slotMember) is { } external)
                    {
                        inheritedName = external.Name; inheritedAbstract = external.IsAbstract;
                        inheritedOwner = baseType;
                    }
                    if (inheritedOwner != null) break;
                }
                var declaration = source.Method;
                var parameters = declaration.Parameters;
                var arguments = parameters.Select((parameter, index) => (IBirExpression)new BirLocal {
                    Name = "p" + index, Sty = TypeFact(parameter) }).ToArray();
                var target = own.SingleOrDefault();
                var selectedBodies = target == null && (inheritedOwner == null || inheritedAbstract)
                    ? inheritedImplementations?.Where(selected => refs?.UsesObjectValueSlots(selected.Owner.Name) != true
                        && Selects(selected.Overrides, sourceId)).ToArray() : null;
                if (selectedBodies?.Length > 1) throw new InvalidOperationException("Ambiguous selected inherited value implementation");
                var hasSelectedBody = selectedBodies?.Length == 1;
                if (target != null && target.Overrides.HasValue
                    && target.Overrides.Value.Select(marker => marker.DeclarationId.OrDefault()).Where(id => id != null).All(members.ContainsKey))
                    target.IndependentAliasSlot = true;
                var abstractSlot = target != null ? target.Abstract.OrDefault()
                    : hasSelectedBody ? false : inheritedOwner != null ? inheritedAbstract : declaration.Abstract;
                IBirExpression forwarded = null;
                if (!abstractSlot && !hasSelectedBody)
                {
                    if (target != null || inheritedOwner != null)
                    {
                        var call = new BirCallInstance {
                            OwnerType = target != null ? new TypeNode.Fqn(cls.Name, cls.Arity == 0 ? null
                                : Enumerable.Range(0, cls.Arity).Select(index => (TypeNode)new TypeNode.Tv("type", index)).ToArray())
                                : BirGraphValue.CopyType(inheritedOwner),
                            Method = target != null ? target.ExplicitClrName.HasValue ? target.ExplicitClrName : target.Name
                                : OptionalField<string>.Of(inheritedName),
                            Recv = new BirThis(), Args = arguments, Sig = parameters.Select(BirGraphValue.CopyType).ToArray(),
                            Ret = TypeFact(declaration.Return), Virtual = target != null, ClrOwnerResolved = true,
                        };
                        if (target == null) call.Super = true;
                        else if (target.DeclarationId.HasValue) call.DeclarationId = target.DeclarationId;
                        forwarded = call;
                    }
                    else forwarded = DefaultCallValue(source.Owner, declaration, new BirThis(), arguments);
                }
                var name = "dotkt$value$" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(source.Slot))).ToLowerInvariant()[..16];
                var bridge = new BirFunctionDeclaration {
                    Name = name, Static = false, Override = inheritedOwner != null, Virtual = true,
                    Abstract = abstractSlot, ObjectOverride = false, Vis = "protected", Generated = true,
                    Params = parameters.Select((parameter, index) => new BirParameter {
                        Name = "p" + index, Type = TypeFact(parameter) }).ToArray(),
                    Ret = TypeFact(declaration.Return),
                    Body = abstractSlot || hasSelectedBody ? Array.Empty<IBirStatement>()
                        : new IBirStatement[] { new BirReturn { Value = Value(forwarded) } },
                    Attrs = Array.Empty<BirAttribute>(), ClrInterfaceImpls = new[] { new BirMethodImpl {
                        Owner = new TypeNode.Fqn(slotOwner), Member = slotMember, Arity = 0,
                        Params = parameters.Select(BirGraphValue.CopyType).ToArray(), Ret = TypeFact(declaration.Return) } },
                };
                if (hasSelectedBody)
                {
                    var selected = selectedBodies[0];
                    var body = KotlinOverrideSlotBridge.BuildBridgeSource(owner, selected,
                        parameters, declaration.Return, name, isValue, refs, selected.Owner,
                        virtualCall: !selected.IsDefault, callMember: selected.Method.ExplicitClrName.OrDefault() ?? selected.Method.Name.OrDefault());
                    bridge.Params = body.Params;
                    bridge.Body = body.Body;
                }
                MaterializedExecutable.NormalizeSource(bridge);
                owner.Methods = owner.Methods.OrEmpty().Append(bridge).ToArray();
                appendedMethods?.Invoke(owner, new[] { bridge });
                bridges[(cls.Name, source.Slot)] = bridge;
                if (!owner.Interfaces.OrEmpty().Any(edge => edge is TypeNode.Fqn named && named.Name == slotOwner))
                    owner.Interfaces = owner.Interfaces.OrEmpty().Append(new TypeNode.Fqn(slotOwner)).ToArray();
            }
            var placeholders = owner.Methods.OrEmpty().Where(method => method.Abstract.OrDefault()
                && SelectedAdapter(method.DeclarationId.OrDefault(), method.Overrides.OrEmpty().Select(marker => marker.DeclarationId.OrDefault())) != null
                && method.Overrides.OrEmpty().Select(marker => marker.DeclarationId.OrDefault()).Where(id => id != null).All(members.ContainsKey)).ToArray();
            var associations = placeholders.Select(method => method.PropertyAssociation.OrDefault())
                .Where(value => value != null).ToHashSet(StringComparer.Ordinal);
            var properties = owner.Properties.OrEmpty().Where(property => associations.Contains(property.PropertyAssociation.OrDefault())).ToArray();
            if (placeholders.Length != 0 || properties.Length != 0)
            {
                retireMembers?.Invoke(owner, placeholders, properties);
                owner.Methods = owner.Methods.OrEmpty().Where(method => !placeholders.Contains(method, ReferenceEqualityComparer.Instance)).ToArray();
                if (properties.Length != 0)
                    owner.Properties = owner.Properties.Value.Where(property => !properties.Contains(property, ReferenceEqualityComparer.Instance)).ToArray();
            }
            active.Remove(cls.Name);
            completed.Add(cls.Name);
        }
        foreach (var cls in definitions.Values) Apply(cls);
    }

    static OptionalField<TypeNode> TypeFact(TypeNode type) => type == null
        ? OptionalField<TypeNode>.Null : OptionalField<TypeNode>.Of(BirGraphValue.CopyType(type));
}
