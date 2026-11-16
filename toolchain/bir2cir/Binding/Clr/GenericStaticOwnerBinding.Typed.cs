using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class GenericStaticOwnerBinding
{
    public static void Materialize(IReadOnlyList<BirFileDeclaration> roots)
    {
        var original = roots.SelectMany(root => root.Types.OrEmpty()).ToArray();
        var names = original.Select(type => type.Name.OrDefault()).Where(name => name != null).ToHashSet(StringComparer.Ordinal);
        var nested = original.Where(type => type.SemanticOwner.HasValue && !type.Generated.OrDefault()
            && type is not BirClassDefinition { KotlinCompanion.HasValue: true } && !type.CompanionCarrier.HasValue
            && type.Mods.OrDefault()?.Inner.OrDefault() != true)
            .GroupBy(type => type.SemanticOwner.Value, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var carrierOwners = original.Where(type => TypeParameterFrame.Count(type) > 0
            && (type.Fields.OrEmpty().Any(field => field.Static.OrDefault() && field.KotlinStatic.OrDefault())
                || type.Methods.OrEmpty().Any(method => method.Static.OrDefault() && method.KotlinStatic.OrDefault())))
            .Select(type => type.Name.OrDefault()).Where(name => name != null).ToHashSet(StringComparer.Ordinal);
        var generated = new Dictionary<(string Owner, string Name), (BirFunctionDeclaration Method, BirFileDeclaration Container)>();
        foreach (var root in roots)
            foreach (var method in root.Methods.OrEmpty())
                if (method.Generated.OrDefault() && method.SemanticOwner.OrDefault() is string owner && carrierOwners.Contains(owner)
                    && method.Name.OrDefault() is string name && !generated.TryAdd((owner, name), (method, root)))
                    throw new InvalidOperationException($"generated method '{owner}.{name}' has multiple declarations");
        foreach (var root in roots)
        {
            var declarations = root.Types.OrEmpty();
            foreach (var owner in declarations)
            {
                var name = owner.Name.OrDefault();
                if (name == null || TypeParameterFrame.Count(owner) == 0) continue;
                var fields = owner.Fields.OrEmpty().Where(field => field.Static.OrDefault() && field.KotlinStatic.OrDefault()).ToArray();
                var methods = owner.Methods.OrEmpty().Where(method => method.Static.OrDefault() && method.KotlinStatic.OrDefault()).ToArray();
                var events = owner.ClrEvents.OrEmpty().Where(value => value.Static.OrDefault()).ToArray();
                var children = nested.GetValueOrDefault(name) ?? Array.Empty<IBirTypeDeclaration>();
                if (fields.Length == 0 && methods.Length == 0 && children.Length == 0) continue;
                var initializes = fields.Any(field => !field.Const.OrDefault() && field.Init.HasValue);
                var carrierName = name + Marker;
                if (!names.Add(carrierName)) throw new InvalidOperationException($"reserved generic-static carrier '{carrierName}' is already declared");
                var protectedNames = new List<string>();
                foreach (var child in children)
                {
                    child.NestedIn = carrierName;
                    if (child.Vis.OrDefault() == "private") child.Vis = "internal";
                    if (child.Vis.OrDefault() is "protected" or "protectedInternal")
                    {
                        var childName = child.Name.Value;
                        protectedNames.Add(childName[(childName.LastIndexOf('.') + 1)..]);
                        child.Vis = "public";
                    }
                }
                foreach (var implementation in roots.SelectMany(file => file.Types.OrEmpty()).OfType<BirClassDefinition>()
                    .Where(type => type.StaticSemanticOwner.OrDefault() == name))
                {
                    if (implementation.SemanticOwner.OrDefault() != name)
                        throw new InvalidOperationException($"Kotlin-static implementation type '{implementation.Name.OrDefault()}' has inconsistent semantic owner");
                    implementation.SemanticOwner = carrierName;
                    implementation.StaticSemanticOwner = default;
                }
                var helpers = CollectLiftedHelpers(fields.Cast<BirGraphValue>().Concat(methods), name, carrierName, methods, generated);
                var physicalMethods = methods.Concat(helpers).ToArray();
                foreach (var helper in helpers)
                {
                    helper.SemanticOwner = default;
                    var helperName = helper.Name.OrDefault() ?? throw new InvalidOperationException("generated static helper has no name");
                    var container = generated[(name, helperName)].Container;
                    container.Methods = Remove(container.Methods, new[] { helper });
                }
                foreach (var member in fields.Cast<BirGraphValue>().Concat(physicalMethods))
                    if (ContainsOwnerTypeVariable(member))
                        throw new InvalidOperationException($"static member on generic owner '{name}' captures an enclosing type parameter");
                var properties = owner.Properties.OrEmpty().Where(property => property.KotlinStatic.OrDefault()).ToArray();
                var physicalFields = fields.ToList();
                if (initializes) physicalFields.Add(StaticIntField(CarrierInitMarker, "internal", new BirConstant {
                    Type = new TypeNode.Fqn("kotlin.Int"), Value = new BirLiteral.IntegerLiteral(0),
                }));
                foreach (var field in fields) field.KotlinStatic = default;
                foreach (var method in physicalMethods) method.KotlinStatic = default;
                owner.Fields = Remove(owner.Fields, fields);
                owner.Methods = Remove(owner.Methods, methods);
                owner.Properties = Remove(owner.Properties, properties);
                owner.ClrEvents = Remove(owner.ClrEvents, events);
                if (initializes) owner.Fields = owner.Fields.OrEmpty().Append(StaticIntField(OwnerInitTrigger, "private", new BirStaticFieldGet {
                    OwnerType = new TypeNode.Fqn(carrierName), Name = CarrierInitMarker,
                })).ToArray();
                var carrier = new BirClassDefinition {
                    Name = carrierName, Kind = "class", Abstract = true, Final = true, Vis = "public", Generated = true,
                    Base = OptionalField<TypeNode>.Null, Interfaces = Array.Empty<TypeNode>(), Fields = physicalFields.ToArray(),
                    Ctors = Array.Empty<BirConstructor>(), Methods = physicalMethods, Properties = properties, ClrEvents = events,
                    Attrs = Array.Empty<BirAttribute>(), StaticCarrier = new BirStaticCarrier { Owner = name,
                        ProtectedNested = protectedNames.Count == 0 ? default : OptionalField<string[]>.Of(protectedNames.ToArray()) },
                };
                root.Types = root.Types.OrEmpty().Append((IBirTypeDeclaration)carrier).ToArray();
            }
        }
        foreach (var root in roots) StripMarkers(root);
    }

    public static void ApplyEventOwnersAll(IReadOnlyList<BirFileDeclaration> roots, ReferenceMetadataIndex refs)
    {
        var local = new Dictionary<string, string>(StringComparer.Ordinal);
        var genericOwners = new HashSet<string>(StringComparer.Ordinal);
        foreach (var type in roots.SelectMany(root => root.Types.OrEmpty()))
        {
            if (type.StaticCarrier.OrDefault()?.Owner.OrDefault() is string owner && type.Name.OrDefault() is string physical) local.Add(owner, physical);
            if (TypeParameterFrame.Count(type) > 0 && type.Name.OrDefault() is string name) genericOwners.Add(name);
        }
        void Walk(BirGraphValue node)
        {
            switch (node) {
                case BirEventGet value when value.Static.OrDefault():
                    if (SelectCarrier(value.Type.OrDefault(), value.Tag, local, genericOwners, refs) is string owner) value.Type = new TypeNode.Fqn(owner);
                    break;
                case BirEventRaise value when value.Static.OrDefault():
                    if (SelectCarrier(value.Type.OrDefault(), value.Tag, local, genericOwners, refs) is string raiser) value.Type = new TypeNode.Fqn(raiser);
                    break;
            }
            node.VisitChildren(Walk);
        }
        foreach (var root in roots) Walk(root);
    }

    static BirFieldDeclaration StaticIntField(string name, string visibility, IBirExpression initializer) => new() {
        Name = name, Type = new TypeNode.Fqn("kotlin.Int"), Static = true, ReadOnly = true, InitOnly = true, Vis = visibility,
        Init = OptionalField<IBirExpression>.Of(initializer),
    };

    static OptionalField<T[]> Remove<T>(OptionalField<T[]> values, IEnumerable<T> removed) where T : class
    {
        if (!values.HasValue) return values;
        var identities = new HashSet<T>(removed, ReferenceEqualityComparer.Instance);
        return OptionalField<T[]>.Of(values.Value.Where(value => !identities.Contains(value)).ToArray());
    }

    static BirFunctionDeclaration[] CollectLiftedHelpers(IEnumerable<BirGraphValue> roots, string semanticOwner, string physicalOwner,
        IReadOnlyList<BirFunctionDeclaration> movedMethods,
        IReadOnlyDictionary<(string Owner, string Name), (BirFunctionDeclaration Method, BirFileDeclaration Container)> generated)
    {
        var physicalNames = movedMethods.Select(method => method.Name.OrDefault()).Where(name => name != null).ToHashSet(StringComparer.Ordinal);
        var names = new HashSet<string>(StringComparer.Ordinal);
        var helpers = new List<BirFunctionDeclaration>();
        void Walk(BirGraphValue node)
        {
            RetargetSemanticOwner(node, semanticOwner, physicalOwner);
            if (node is BirNewDelegate { CalleeOwner.HasValue: true, CalleeOwner.Value: TypeNode.Fqn target } value
                && target.Name == semanticOwner && value.Method.OrDefault() is string name)
            {
                if (generated.TryGetValue((semanticOwner, name), out var helper))
                {
                    if (physicalNames.Contains(name)) throw new InvalidOperationException($"generated static helper '{semanticOwner}.{name}' collides with a moved source declaration");
                    physicalNames.Add(name);
                    if (names.Add(name)) { helpers.Add(helper.Method); Walk(helper.Method); }
                }
                else if (!physicalNames.Contains(name))
                    throw new InvalidOperationException($"static member on generic owner '{semanticOwner}' has an unresolved delegate target '{name}'");
                value.CalleeOwner = new TypeNode.Fqn(physicalOwner);
            }
            node.VisitChildren(Walk);
        }
        foreach (var root in roots) Walk(root);
        return helpers.ToArray();
    }

    static void RetargetSemanticOwner(BirGraphValue node, string source, string target)
    {
        switch (node) {
            case IBirTypeDeclaration type: if (type.SemanticOwner.OrDefault() == source) type.SemanticOwner = target; break;
            case BirFunctionDeclaration method: if (method.SemanticOwner.OrDefault() == source) method.SemanticOwner = target; break;
            case BirClosureDefinition closure: if (closure.SemanticOwner.OrDefault() == source) closure.SemanticOwner = target; break;
            case BirCallEvaluation evaluation: if (evaluation.SemanticOwner.OrDefault() == source) evaluation.SemanticOwner = target; break;
            case BirCallInline inline: if (inline.SemanticOwner.OrDefault() == source) inline.SemanticOwner = target; break;
        }
    }

    static void StripMarkers(BirGraphValue node)
    {
        switch (node) {
            case BirFieldDeclaration field: field.KotlinStatic = default; break;
            case BirFunctionDeclaration method: method.KotlinStatic = default; break;
            case BirPropertyDeclaration property: property.KotlinStatic = default; break;
            case BirClassDefinition type: type.StaticSemanticOwner = default; break;
        }
        node.VisitChildren(StripMarkers);
    }

    static bool ContainsOwnerTypeVariable(BirGraphValue node)
    {
        var found = false;
        // Only target-owner descriptors are exempt. A method's own parameter bounds,
        // shared-cell bounds, and every other structured child remain real uses.
        var ownerDescriptors = new HashSet<BirGraphValue>(ReferenceEqualityComparer.Instance);
        node.RewriteRepresentationParametersInPlace((role, parameters) => {
            if (role == BirRepresentationParameterRole.Owner)
                foreach (var parameter in parameters) ownerDescriptors.Add(parameter);
            return parameters;
        });
        node.RewriteRepresentationInPlace((_, _, type) => { found |= ContainsOwnerVariable(type); return type; },
            (_, _, child) => { if (!ownerDescriptors.Contains(child)) found |= ContainsOwnerTypeVariable(child); });
        return found;
    }

    static bool ContainsOwnerVariable(TypeNode type) => type switch {
        TypeNode.Tv { Scope: "type" } => true,
        TypeNode.Fqn value => value.Args?.Any(ContainsOwnerVariable) == true,
        TypeNode.Projection value => ContainsOwnerVariable(value.Of), TypeNode.Nullable value => ContainsOwnerVariable(value.Of),
        TypeNode.Oblivious value => ContainsOwnerVariable(value.Of), TypeNode.Array value => ContainsOwnerVariable(value.Elem),
        TypeNode.ByRef value => ContainsOwnerVariable(value.Of), TypeNode.Ptr value => ContainsOwnerVariable(value.Of),
        TypeNode.Mod value => ContainsOwnerVariable(value.M) || ContainsOwnerVariable(value.Of),
        TypeNode.Fn value => ContainsOwnerVariable(value.Ret) || value.Params.Any(ContainsOwnerVariable)
            || value.Recv != null && ContainsOwnerVariable(value.Recv) || value.Ctx?.Any(ContainsOwnerVariable) == true,
        TypeNode.FnPtr value => ContainsOwnerVariable(value.Ret) || value.Params.Any(ContainsOwnerVariable),
        _ => false,
    };
}
