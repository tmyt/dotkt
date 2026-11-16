using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class CompanionExtensionBinding
{
    sealed class ExtensionGroup
    {
        internal readonly List<BirFunctionDeclaration> Functions = new();
        internal readonly List<BirFunctionDeclaration> Accessors = new();
        internal readonly List<BirFieldDeclaration> Fields = new();
    }
    sealed class PropertyParts
    {
        internal string SourceName;
        internal BirFunctionDeclaration Getter, Setter;
        internal BirFieldDeclaration Field;
    }

    public static LocalIndex Apply(IReadOnlyList<BirFileDeclaration> roots, ReferenceMetadataIndex refs)
    {
        var bindings = new Dictionary<string, Binding>(StringComparer.Ordinal);
        var localTypes = roots.SelectMany(root => root.Types.OrEmpty()).Where(type => type.Name.HasValue)
            .ToDictionary(type => type.Name.Value, StringComparer.Ordinal);
        foreach (var root in roots)
        {
            var owner = root.FileClass.OrDefault()
                ?? throw new InvalidOperationException("BIR file has no fileClass while binding companion extensions");
            var groups = new Dictionary<string, ExtensionGroup>(StringComparer.Ordinal);
            ExtensionGroup Group(TypeNode receiver)
            {
                var key = TypeNode.ToJson(receiver);
                if (!groups.TryGetValue(key, out var group)) groups[key] = group = new ExtensionGroup();
                return group;
            }
            var retained = new List<BirFunctionDeclaration>();
            foreach (var method in root.Methods.OrEmpty())
            {
                if (method.CompanionReceiver.HasValue && method.CompanionMemberKind.OrDefault() == "function"
                    && method.Mods.OrDefault()?.Suspend.OrDefault() != true)
                    Group(method.CompanionReceiver.Value).Functions.Add(method);
                else if (method.CompanionReceiver.HasValue && method.CompanionMemberKind.OrDefault() is "get" or "set"
                    && !method.Params.OrEmpty().Any(parameter => parameter.Mods.OrDefault()?.Context.OrDefault() == true))
                    Group(method.CompanionReceiver.Value).Accessors.Add(method);
                else { BindDeclaration(owner, method, bindings); retained.Add(method); }
            }
            if (root.Methods.HasValue) root.Methods = retained.ToArray();
            foreach (var field in root.Fields.OrEmpty())
                if (field.CompanionReceiver.HasValue && field.CompanionMemberKind.OrDefault() == "field")
                    Group(field.CompanionReceiver.Value).Fields.Add(field);
                else BindDeclaration(owner, field, bindings);
            foreach (var (receiver, group) in groups) MaterializeCSharp14Members(root, owner, receiver, group, bindings, localTypes, refs);
        }
        foreach (var root in roots) RewriteUses(root, bindings, refs);
        return new LocalIndex(bindings);
    }

    static void BindDeclaration(string owner, BirGraphValue declaration, Dictionary<string, Binding> bindings)
    {
        var (receiver, name, kind) = declaration switch {
            BirFunctionDeclaration value => (value.CompanionReceiver, value.CompanionSourceName, value.CompanionMemberKind),
            BirFieldDeclaration value => (value.CompanionReceiver, value.CompanionSourceName, value.CompanionMemberKind),
            _ => throw new InvalidOperationException("Unaccounted companion extension declaration"),
        };
        if (!receiver.HasValue) return;
        var sourceName = name.OrDefault() ?? throw new InvalidOperationException("companion extension declaration has no source name");
        var memberKind = kind.OrDefault();
        ValidateKind(memberKind);
        var encodedReceiver = TypeNode.ToJson(receiver.Value);
        var physicalRoot = PhysicalRoot(encodedReceiver, sourceName);
        var physicalName = memberKind is "get" or "set" ? KotlinPropertyAccessors.PhysicalName(physicalRoot, memberKind) : physicalRoot;
        var key = Key(owner, encodedReceiver, memberKind, sourceName);
        if (bindings.TryGetValue(key, out var prior) && prior.PhysicalName != physicalName)
            throw new InvalidOperationException($"inconsistent companion-extension physical identity for '{owner}.{sourceName}'");
        if (prior == null) bindings.Add(key, new Binding(physicalName, owner));
        if (declaration is BirFunctionDeclaration method) method.Name = physicalName;
        else ((BirFieldDeclaration)declaration).Name = physicalName;
    }

    static void MaterializeCSharp14Members(BirFileDeclaration root, string semanticOwner, string receiver,
        ExtensionGroup declarations, Dictionary<string, Binding> bindings,
        IReadOnlyDictionary<string, IBirTypeDeclaration> localTypes, ReferenceMetadataIndex refs)
    {
        if (declarations.Accessors.Count != 0)
        {
            var moved = declarations.Accessors.Select(accessor => accessor.PropertyAssociation.OrDefault()
                ?? throw new InvalidOperationException("companion-extension accessor has no property association")).ToHashSet(StringComparer.Ordinal);
            if (!root.Properties.HasValue) throw new InvalidOperationException("BIR file has no property declarations");
            root.Properties = root.Properties.Value.Where(property => !property.PropertyAssociation.HasValue
                || !moved.Contains(property.PropertyAssociation.Value)).ToArray();
        }
        var classifier = ReceiverClassifier(TypeNode.Parse(receiver))
            ?? throw new InvalidOperationException("companion extension receiver is not a classifier type: " + receiver);
        var physicalClassifier = classifier;
        if (!localTypes.ContainsKey(classifier) && !refs.Aliases.ContainsKey(classifier)
            && refs.TryExactPhysicalTypeName(classifier, refs.OwnerArity(classifier), out var exact))
            physicalClassifier = exact ?? throw new InvalidOperationException($"ambiguous CLR metadata identity for companion-extension receiver '{classifier}'");
        var graphKey = semanticOwner + "\u001f" + classifier;
        var containerName = semanticOwner + "$extensions$" + StableId(graphKey).ToLowerInvariant();
        var groupSimple = "<G>$" + StableId("group\u001f" + graphKey);
        var markerSimple = "<M>$" + StableId("marker\u001f" + graphKey);
        var groupName = containerName + "." + groupSimple;
        var markerName = groupName + "." + markerSimple;
        var parameters = ReceiverTypeParameters(classifier, localTypes, refs);
        var frame = (localTypes.TryGetValue(classifier, out var definition)
                ? NullableRepresentationViews.TypeFacts((BirGraphValue)definition)?.NullableFrame
                : refs.NullableTypeFrames.GetValueOrDefault(classifier))
            ?? new NullableRepresentationFrame(parameters.Length, Array.Empty<int>());
        var physicalParameters = PhysicalReceiverTypeParameters(parameters);
        var arity = parameters.Length;
        var arguments = Enumerable.Range(0, arity).Select(index => (TypeNode)new TypeNode.Tv("type", index)).ToArray();
        if (refs.Aliases.TryGetValue(classifier, out var alias))
        {
            arguments = refs.NullableTypeFrames.GetValueOrDefault(classifier)?.OrdinaryArguments(arguments) ?? arguments;
            physicalClassifier = refs.ResolveNetType(alias, arguments.Length)?.FullName
                ?? throw new InvalidOperationException("Companion receiver alias has no exact CLR declaration");
        }
        var implementations = new List<BirFunctionDeclaration>();
        var signatures = new List<BirFunctionDeclaration>();
        var properties = new List<BirPropertyDeclaration>();
        foreach (var declaration in declarations.Functions)
        {
            var name = declaration.CompanionSourceName.OrDefault()
                ?? throw new InvalidOperationException("companion extension declaration has no source name");
            var coreName = arity == 0 ? name : CoreName("function", name);
            AddBinding(bindings, Key(semanticOwner, receiver, "function", name), new Binding(coreName, containerName), semanticOwner, name);
            var signature = Signature(declaration, name, markerSimple, specialName: false);
            signatures.Add(signature);
            declaration.Name = coreName;
            RemoveCompanionFacts(declaration);
            implementations.Add(declaration);
            if (arity != 0) implementations.Add(WrapperImplementation(declaration, name, coreName, containerName, parameters, frame));
        }
        var partsByName = new Dictionary<string, PropertyParts>(StringComparer.Ordinal);
        foreach (var accessor in declarations.Accessors)
        {
            var name = accessor.CompanionSourceName.OrDefault()
                ?? throw new InvalidOperationException("companion extension accessor has no source name");
            if (!partsByName.TryGetValue(name, out var parts)) partsByName[name] = parts = new PropertyParts { SourceName = name };
            if (accessor.CompanionMemberKind.OrDefault() == "get")
            {
                if (parts.Getter != null) throw new InvalidOperationException($"duplicate companion-extension getter '{semanticOwner}.{name}'");
                parts.Getter = accessor;
            }
            else
            {
                if (parts.Setter != null) throw new InvalidOperationException($"duplicate companion-extension setter '{semanticOwner}.{name}'");
                parts.Setter = accessor;
            }
        }
        foreach (var field in declarations.Fields)
        {
            var name = field.CompanionSourceName.OrDefault()
                ?? throw new InvalidOperationException("companion extension field has no source name");
            if (partsByName.ContainsKey(name)) throw new InvalidOperationException($"duplicate companion-extension property '{semanticOwner}.{name}'");
            partsByName[name] = new PropertyParts { SourceName = name, Field = field };
        }
        foreach (var parts in partsByName.Values)
        {
            if (parts.Field != null) MaterializeFieldBackedProperty(parts, semanticOwner, receiver);
            if (parts.Getter == null) throw new InvalidOperationException($"companion-extension property '{semanticOwner}.{parts.SourceName}' has no getter");
            var valueType = TypeNode.ToJson(parts.Getter.Ret.Value);
            var getterName = parts.Getter.ExplicitClrName.OrDefault() ?? KotlinPropertyAccessors.PhysicalName(parts.SourceName, "get");
            var setterName = parts.Setter?.ExplicitClrName.OrDefault() ?? KotlinPropertyAccessors.PhysicalName(parts.SourceName, "set");
            var getCore = arity == 0 ? getterName : CoreName("get", parts.SourceName);
            var setCore = arity == 0 ? setterName : CoreName("set", parts.SourceName);
            AddBinding(bindings, Key(semanticOwner, receiver, "get", parts.SourceName), new Binding(getCore, containerName, valueType), semanticOwner, parts.SourceName);
            PrepareImplementationAccessor(parts.Getter, getCore, implementations);
            if (arity != 0) implementations.Add(WrapperImplementation(parts.Getter, getterName, getCore, containerName, parameters, frame));
            BirFunctionDeclaration setterSignature = null;
            if (parts.Setter != null)
            {
                AddBinding(bindings, Key(semanticOwner, receiver, "set", parts.SourceName), new Binding(setCore, containerName, valueType), semanticOwner, parts.SourceName);
                PrepareImplementationAccessor(parts.Setter, setCore, implementations);
                if (arity != 0) implementations.Add(WrapperImplementation(parts.Setter, setterName, setCore, containerName, parameters, frame));
                setterSignature = Signature(parts.Setter, setterName, markerSimple, specialName: true);
            }
            var getterSignature = Signature(parts.Getter, getterName, markerSimple, specialName: true);
            signatures.Add(getterSignature);
            if (setterSignature != null) signatures.Add(setterSignature);
            var property = new BirPropertyDeclaration {
                Name = parts.SourceName, Type = BirGraphValue.CopyType(parts.Getter.Ret.Value), Get = getterName,
                GetSig = AccessorSignature(getterSignature), GetRet = BirGraphValue.CopyType(getterSignature.Ret.Value),
                GetMethodArity = getterSignature.TypeParams.OrEmpty().Length,
                Set = setterSignature == null ? OptionalField<string>.Null : setterName,
                Attrs = new[] { ExtensionMarker(markerSimple) },
            };
            if (setterSignature != null) {
                property.SetSig = AccessorSignature(setterSignature); property.SetRet = BirGraphValue.CopyType(setterSignature.Ret.Value);
                property.SetMethodArity = setterSignature.TypeParams.OrEmpty().Length;
            }
            properties.Add(property);
        }
        var container = Shell(containerName, abstractType: true);
        container.BeforeFieldInit = true; container.Methods = implementations.ToArray(); container.Attrs = new[] { ExtensionAttribute() };
        var group = Shell(groupName, abstractType: false);
        group.Abstract = default; group.NestedIn = containerName; group.SpecialName = true;
        group.Methods = signatures.ToArray(); group.Properties = properties.ToArray(); group.Attrs = new[] { ExtensionAttribute() };
        if (arity != 0) group.TypeParams = physicalParameters.Select(parameter => (BirTypeParameter)parameter.DeepClone()).ToArray();
        var marker = Shell(markerName, abstractType: true);
        marker.NestedIn = groupName; marker.SpecialName = true; marker.Properties = default;
        marker.Methods = new[] { new BirFunctionDeclaration {
            Name = "<Extension>$", Static = true, Override = false, Virtual = false, Abstract = false,
            SpecialName = true, Generated = true, _physicalDeclarationSignature = true, Vis = "public",
            Params = new[] { new BirParameter { Name = "", Type = arity == 0 ? TypeNode.Parse(receiver) : new TypeNode.Fqn(physicalClassifier, arguments) } },
            Ret = new TypeNode.Fqn("void"), Body = Array.Empty<IBirStatement>(), Attrs = Array.Empty<BirAttribute>(),
        } };
        if (arity != 0) marker.CapturedTypeParams = physicalParameters.Select(parameter => (BirTypeParameter)parameter.DeepClone()).ToArray();
        root.Types = root.Types.OrEmpty().Concat(new IBirTypeDeclaration[] { container, group, marker }).ToArray();
    }

    static BirClassDefinition Shell(string name, bool abstractType) => new() {
        Name = name, Kind = "class", Vis = "public", Abstract = abstractType, Final = true, Generated = true,
        Interfaces = Array.Empty<TypeNode>(), Fields = Array.Empty<BirFieldDeclaration>(), Methods = Array.Empty<BirFunctionDeclaration>(),
        Properties = Array.Empty<BirPropertyDeclaration>(), Ctors = Array.Empty<BirConstructor>(), Attrs = Array.Empty<BirAttribute>(),
    };

    static void AddBinding(Dictionary<string, Binding> bindings, string key, Binding binding, string owner, string name)
    {
        if (bindings.TryGetValue(key, out var prior) && prior != binding)
            throw new InvalidOperationException($"inconsistent companion-extension physical identity for '{owner}.{name}'");
        bindings[key] = binding;
    }
}
