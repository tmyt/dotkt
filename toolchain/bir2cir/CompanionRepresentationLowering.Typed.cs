using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class CompanionRepresentationLowering
{
    // Member substitution consumes physical singleton/member operands. No
    // semantic companion value may survive into that capability boundary.
    internal static void AssertNoCompanionValues(BirGraphValue root)
    {
        root.Visit(node => {
            if (node is BirCompanionValue value)
                throw new InvalidOperationException("companionValue survived CLR companion use binding: "
                    + "owner=" + (value.OwnerType.OrDefault() as TypeNode.Fqn)?.Name
                    + " semantic=" + (value.CompanionType.OrDefault() as TypeNode.Fqn)?.Name);
        });
    }

    public static LocalIndex Apply(IReadOnlyList<BirFileDeclaration> roots)
    {
        var types = roots.SelectMany(root => root.Types.OrEmpty()).ToArray();
        var byName = types.ToDictionary(type => type.Name.Value, StringComparer.Ordinal);
        var associations = new List<Association>();
        var unplaced = types.OfType<BirClassDefinition>().Where(type => type.KotlinCompanion.HasValue
                && type.Name.OrDefault() != type.KotlinCompanion.Value.Owner.OrDefault())
            .ToDictionary(type => type.Name.Value, StringComparer.Ordinal);
        var placing = new HashSet<string>(StringComparer.Ordinal);
        IBirTypeDeclaration ResolveOwner(string name)
        {
            if (unplaced.TryGetValue(name, out var companion)) Place(name, companion);
            return byName.GetValueOrDefault(name);
        }
        void Place(string semanticName, BirClassDefinition companion)
        {
            if (!placing.Add(semanticName)) throw new InvalidOperationException($"cyclic Kotlin companion ownership for '{semanticName}'");
            var fact = companion.KotlinCompanion.Value;
            var ownerName = Required(fact.Owner.OrDefault(), "owner");
            var sourceName = Required(fact.Name.OrDefault(), "name");
            var visibility = Required(fact.Visibility.OrDefault(), "visibility");
            var suffixStart = semanticName.LastIndexOf(".<companion:", StringComparison.Ordinal);
            if (suffixStart < 0) throw new InvalidOperationException($"semantic companion '{semanticName}' has no declaration owner identity");
            var declarationOwnerName = semanticName[..suffixStart];
            if (!byName.TryGetValue(declarationOwnerName, out var owner))
                throw new InvalidOperationException($"semantic companion '{ownerName}.{sourceName}' has no owner declaration");
            var physicalOwner = PhysicalMetadataName(owner, ResolveOwner);
            var arity = owner.CapturedTypeParams.OrEmpty().Length + owner.TypeParams.OrEmpty().Length;
            var physicalName = arity > 0 ? physicalOwner.Replace('+', '$') + HoistedMarker + sourceName
                : declarationOwnerName + ".$" + sourceName;
            if (!byName.TryAdd(physicalName, companion))
                throw new InvalidOperationException($"reserved physical companion type identity '{physicalName}' is already declared");
            var association = new Association(declarationOwnerName, ownerName, semanticName, sourceName, visibility,
                physicalName, physicalOwner, arity);
            if (owner is BirClassDefinition ownerClass) ownerClass.KotlinCompanion = default;
            Materialize(owner, companion, association);
            associations.Add(association);
            unplaced.Remove(semanticName);
            placing.Remove(semanticName);
        }
        foreach (var name in unplaced.Keys.ToArray()) ResolveOwner(name);
        var moved = associations.Select(association => association.SemanticName).ToHashSet(StringComparer.Ordinal);
        foreach (var type in types)
            if (type.SemanticOwner.HasValue && moved.Contains(type.SemanticOwner.Value)
                && type.Vis.OrDefault() is "private" or "protected")
            {
                RoundtripMetadata.PreserveTypeVisibility(type);
                type.Vis = type.Vis.Value == "private" ? "internal" : "protectedInternal";
            }
        foreach (var root in roots) RewriteUses(root, associations);
        return new LocalIndex(associations);
    }

    static string Required(string value, string key) => value
        ?? throw new InvalidOperationException($"malformed companion fact: '{key}' is required");

    static string PhysicalMetadataName(IBirTypeDeclaration type, Func<string, IBirTypeDeclaration> resolveOwner)
    {
        var name = type.Name.OrDefault() ?? throw new InvalidOperationException("companion owner has no physical name");
        var parentName = type.NestedIn.OrDefault() ?? type.SemanticOwner.OrDefault();
        if (parentName == null) return name;
        var parent = resolveOwner(parentName);
        return (parent == null ? parentName : PhysicalMetadataName(parent, resolveOwner))
            + "+" + name[(name.LastIndexOf('.') + 1)..];
    }

    static void Materialize(IBirTypeDeclaration owner, BirClassDefinition companion, Association association)
    {
        companion.Name = association.PhysicalName;
        companion.NestedIn = association.Hoisted ? default : OptionalField<string>.Of(association.OwnerName);
        companion.KotlinCompanion = default;
        companion.Vis = "public";
        companion.Generated = true;
        if (!companion.Mods.HasValue) companion.Mods = new BirModifiers();
        companion.Mods.Value.Object = true;
        companion.Fields = new[] { new BirFieldDeclaration {
            Name = "$INSTANCE", Type = new TypeNode.Fqn(association.PhysicalName), Static = true,
            ReadOnly = true, InitOnly = true, Init = new BirNew {
                Type = new TypeNode.Fqn(association.PhysicalName), ArgTypes = Array.Empty<TypeNode>(), Args = Array.Empty<IBirExpression>(),
            },
        } }.Concat(companion.Fields.OrEmpty()).ToArray();
        companion.CompanionCarrier = new BirCompanionCarrier {
            Kind = association.Hoisted ? "sidecar" : "nested", Owner = association.KotlinOwnerName,
            Name = association.SourceName, Visibility = association.Visibility,
            PhysicalOwner = association.PhysicalOwner, PhysicalOwnerArity = association.PhysicalOwnerArity,
        };
        if (owner.Kind.OrDefault() == "enum") return;
        if (owner.Fields.OrEmpty().Any(field => field.Name.OrDefault() == association.SourceName))
            throw new InvalidOperationException($"companion value accessor '{association.OwnerName}.{association.SourceName}' collides with an existing field");
        owner.Fields = owner.Fields.OrEmpty().Append(new BirFieldDeclaration {
            Name = association.SourceName, Type = new TypeNode.Fqn(association.PhysicalName), Static = true,
            ReadOnly = true, InitOnly = true, Vis = association.Visibility,
            Init = SingletonLoad(association.PhysicalName),
        }).ToArray();
    }

    static BirStaticFieldGet SingletonLoad(string name) => new() {
        OwnerType = new TypeNode.Fqn(name), Name = "$INSTANCE", Sty = new TypeNode.Fqn(name),
    };

    static void RewriteUses(BirGraphValue root, IReadOnlyList<Association> associations)
    {
        BirGraphValue Walk(BirGraphValue node)
        {
            if (node is IBirExecutablePayload) return node;
            foreach (var association in associations)
            {
                if (node is BirCompanionValue companionValue && companionValue.CompanionType.OrDefault() is TypeNode.Fqn named
                    && named.Name == association.SemanticName) return SingletonLoad(association.PhysicalName);
                var (owner, receiver) = node switch {
                    BirCallInstance value => (value.OwnerType.OrDefault(), value.Recv.OrDefault()),
                    BirFieldGet value => (value.OwnerType.OrDefault(), value.Recv.OrDefault()),
                    BirSetField value => (value.OwnerType.OrDefault(), value.Recv.OrDefault()),
                    BirFieldSetExpression value => (value.OwnerType.OrDefault(), value.Recv.OrDefault()),
                    BirLateinitGet value => (value.OwnerType.OrDefault(), value.Recv.OrDefault()),
                    BirNewBoundDelegate value => (value.OwnerType.OrDefault(), value.Recv.OrDefault()),
                    _ => (null, null),
                };
                if (owner is TypeNode.Fqn selected && IsCompanionMemberOwner(selected.Name, association)
                    && receiver is BirCompanionValue receiverValue
                    && receiverValue.CompanionType.OrDefault() is TypeNode.Fqn source
                    && source.Name == association.SemanticName)
                {
                    var physical = new TypeNode.Fqn(association.PhysicalName);
                    switch (node) {
                        case BirCallInstance value: value.OwnerType = physical; break;
                        case BirFieldGet value: value.OwnerType = physical; break;
                        case BirSetField value: value.OwnerType = physical; break;
                        case BirFieldSetExpression value: value.OwnerType = physical; break;
                        case BirLateinitGet value: value.OwnerType = physical; break;
                        case BirNewBoundDelegate value: value.OwnerType = physical; value.CalleeOwner = new TypeNode.Fqn(association.PhysicalName); break;
                    }
                }
                string Owner(string current) => current == association.SemanticName ? association.PhysicalName : current;
                switch (node) {
                    case IBirTypeDeclaration value when value.SemanticOwner.HasValue: value.SemanticOwner = Owner(value.SemanticOwner.Value); break;
                    case BirFunctionDeclaration value when value.SemanticOwner.HasValue: value.SemanticOwner = Owner(value.SemanticOwner.Value); break;
                    case BirClosureDefinition value when value.SemanticOwner.HasValue: value.SemanticOwner = Owner(value.SemanticOwner.Value); break;
                    case BirCallInline value when value.SemanticOwner.HasValue: value.SemanticOwner = Owner(value.SemanticOwner.Value); break;
                    case BirCallEvaluation value when value.SemanticOwner.HasValue: value.SemanticOwner = Owner(value.SemanticOwner.Value); break;
                }
                node.RewriteTypeFieldsInPlace(type => RewriteCompanionType(type, association));
            }
            node.VisitChildSlots(slot => slot.Replace(Walk(slot.Value)));
            return node;
        }
        Walk(root);
    }

    // Structural recursion over TypeNode data; the only representation decision
    // is the explicit semantic-to-physical association allocated above.
    static TypeNode RewriteCompanionType(TypeNode type, Association association) => MapCompanionTypes(type,
        named => named.Name == association.SemanticName ? new TypeNode.Fqn(association.PhysicalName) : named);

    static TypeNode MapCompanionTypes(TypeNode type, Func<TypeNode.Fqn, TypeNode> rewrite) => type switch {
        TypeNode.Fqn named => rewrite(named with { Args = named.Args?.Select(argument => MapCompanionTypes(argument, rewrite)).ToArray() }),
        TypeNode.Nullable value => value with { Of = MapCompanionTypes(value.Of, rewrite) },
        TypeNode.Oblivious value => value with { Of = MapCompanionTypes(value.Of, rewrite) },
        TypeNode.Projection value => value with { Of = MapCompanionTypes(value.Of, rewrite) },
        TypeNode.Array value => value with { Elem = MapCompanionTypes(value.Elem, rewrite) },
        TypeNode.ByRef value => value with { Of = MapCompanionTypes(value.Of, rewrite) },
        TypeNode.Ptr value => value with { Of = MapCompanionTypes(value.Of, rewrite) },
        TypeNode.Mod value => value with { M = MapCompanionTypes(value.M, rewrite), Of = MapCompanionTypes(value.Of, rewrite) },
        TypeNode.Fn value => value with {
            Ret = MapCompanionTypes(value.Ret, rewrite), Params = value.Params.Select(parameter => MapCompanionTypes(parameter, rewrite)).ToArray(),
            Recv = value.Recv == null ? null : MapCompanionTypes(value.Recv, rewrite),
            Ctx = value.Ctx?.Select(context => MapCompanionTypes(context, rewrite)).ToArray(),
        },
        TypeNode.FnPtr value => value with { Ret = MapCompanionTypes(value.Ret, rewrite),
            Params = value.Params.Select(parameter => MapCompanionTypes(parameter, rewrite)).ToArray() },
        _ => type,
    };
}
