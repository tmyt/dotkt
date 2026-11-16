using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;
using OwnerView = NullableRepresentationViews.Owner;
using MethodView = NullableRepresentationViews.Method;

static partial class NullableRepresentationMaterialization
{
    public static IReadOnlyDictionary<string, NullableRepresentationFrame> Apply(IReadOnlyList<BirFileDeclaration> roots,
        ValueTypeOracle isValue, ReferenceMetadataIndex references = null,
        Func<TypeNode.Fqn, bool, NullableRepresentationFrame, TypeNode> argumentHead = null,
        GenericRepresentationPolicy policy = null, IReadOnlyDictionary<string, NullableRepresentationFrame> declaredMethods = null)
    {
        var importedMethods = new Dictionary<string, NullableRepresentationFrame>(StringComparer.Ordinal);
        var importedTypes = new Dictionary<string, NullableRepresentationFrame>(references?.NullableTypeFrames
            ?? new Dictionary<string, NullableRepresentationFrame>(), StringComparer.Ordinal);
        void FindCalls(BirGraphValue node)
        {
            if (node is BirAttribute or IBirExecutablePayload) return;
            if (node is BirGraphNode && node.DeclarationIdentityFact.OrDefault() is string id && references != null)
            {
                if (references.NullableMethodFrame(id) is { } frame) importedMethods[id] = frame;
                if (references.TryDeclarationIdentity(id, out _, out var owner, out _, out _) && !importedTypes.ContainsKey(owner))
                    importedTypes[owner] = new(references.OwnerArity(owner), Array.Empty<int>());
            }
            node.VisitChildren(FindCalls);
        }
        foreach (var root in roots) FindCalls(root);
        var applications = new InnerApplicationFrames(roots, references);
        var demands = NullableRepresentationDemand.Collect(roots, importedTypes, importedMethods, policy, applications, declaredMethods);
        var localBindings = NullableRepresentationDemand.BindLocalFunctions(roots);
        var owners = new Dictionary<BirGraphValue, NullableRepresentationFrame>(ReferenceEqualityComparer.Instance);
        var methodFrames = new Dictionary<BirGraphValue, NullableRepresentationFrame>(ReferenceEqualityComparer.Instance);
        foreach (var owner in demands)
        {
            owners.Add(owner.Declaration, owner.Frame);
            foreach (var method in owner.Methods) methodFrames.Add(method.Declaration, method.Frame);
        }
        foreach (var owner in demands)
        {
            RequireCovered(owner.Body.Type, owners[owner.Declaration], owner.View.Name + " (type)");
            foreach (var method in owner.Methods)
            {
                RequireCovered(method.Body.Type, owners[owner.Declaration], owner.View.Name + "." + method.View.Name + " (type)");
                if (method.View.Body == null) RequireCovered(method.Body.Method, method.Frame, owner.View.Name + "." + method.View.Name + " (method)");
            }
        }
        var types = new Dictionary<string, NullableRepresentationFrame>(importedTypes, StringComparer.Ordinal);
        foreach (var owner in demands.Where(owner => owner.IsTypeDeclaration)) types[owner.View.Name] = owners[owner.Declaration];
        var methods = new Dictionary<string, NullableRepresentationFrame>(importedMethods, StringComparer.Ordinal);
        foreach (var method in demands.SelectMany(owner => owner.Methods))
            if (method.View.Id is string id) methods[id] = method.Frame;
        foreach (var root in roots) NullableGenericErasure.PreserveSourceFacts(root, isValue);
        var empty = new NullableRepresentationFrame(0, Array.Empty<int>());
        var applicationTypes = applications.Project(types);
        var dispatchTypes = applications.Project(types, retainPhysicalOrder: true);
        NullableRepresentationTypes Mapping(NullableRepresentationFrame owner, NullableRepresentationFrame method, string name = null) =>
            new(owner, method, applicationTypes, isValue, argumentHead, policy,
                name == null ? owner : dispatchTypes.GetValueOrDefault(name) ?? owner,
                name == null ? owner : applicationTypes.GetValueOrDefault(name) ?? owner, policy?.UsesNativeOwnerSlots(name) == true);
        var declarations = demands.SelectMany(owner => owner.Methods.Select(method => (owner, method)))
            .Where(pair => pair.method.View.Id != null).ToDictionary(pair => pair.method.View.Id,
                pair => Mapping(pair.owner.Frame, pair.method.Frame, pair.owner.View.Name), StringComparer.Ordinal);
        var locals = new Dictionary<BirGraphValue, NullableRepresentationTypes>(ReferenceEqualityComparer.Instance);
        foreach (var owner in demands)
            foreach (var method in owner.Methods.Where(method => method.IsLocal))
                locals.Add(method.Declaration, Mapping(owner.Frame, method.Frame, owner.Declaration is BirFileDeclaration ? null : owner.View.Name));
        foreach (var (id, frame) in importedMethods)
            if (!declarations.ContainsKey(id) && references.TryDeclarationIdentity(id, out _, out var owner, out _, out _))
                declarations[id] = Mapping(types.GetValueOrDefault(owner) ?? empty, frame, owner);
        var constructors = new Dictionary<BirGraphValue, NullableRepresentationTypes>(ReferenceEqualityComparer.Instance);
        foreach (var owner in demands)
            foreach (var constructor in owner.View.Constructors)
            {
                var target = constructor.ThisArgs.HasValue ? owner.Frame
                    : owner.View.Base is TypeNode.Fqn baseType ? types.GetValueOrDefault(baseType.Name)
                        ?? references?.KotlinConstructorOwnerFrames.GetValueOrDefault(baseType.Name) : null;
                constructors.Add(constructor, target == null ? null : Mapping(target, empty,
                    constructor.ThisArgs.HasValue ? owner.View.Name : (owner.View.Base as TypeNode.Fqn)?.Name));
            }
        NullableRepresentationTypes DeclarationMapping(BirGraphValue use)
        {
            if (use is BirInlineCapture { SharedCellTypeParams.HasValue: true, SharedCellType.HasValue: true, SharedCellType.Value: TypeNode.Fqn cell })
                return Mapping(types[cell.Name], empty);
            if (localBindings.TryGetValue(use, out var local)) return locals[local];
            if (constructors.TryGetValue(use, out var constructor)) return constructor;
            var ownerType = SelectedOwner(use);
            if (use.DeclarationIdentityFact.OrDefault() is string id && declarations.TryGetValue(id, out var selected))
                return ownerType is TypeNode.Fqn descriptor && types.TryGetValue(descriptor.Name, out var frame)
                    ? Mapping(frame, selected.MethodFrame, descriptor.Name) : selected;
            if (use.DeclarationIdentityFact.OrDefault() is string referencedId && references != null
                && references.TryDeclarationIdentity(referencedId, out _, out var referencedOwner, out _, out _))
                return Mapping(types.GetValueOrDefault(referencedOwner), references.NullableMethodFrame(referencedId), referencedOwner);
            if (use is BirNew && ownerType is TypeNode.Fqn constructed && references != null
                && references.KotlinConstructorOwnerFrames.TryGetValue(constructed.Name, out var constructionFrame))
                return Mapping(constructionFrame, empty, constructed.Name);
            return ownerType is TypeNode.Fqn owner && types.TryGetValue(owner.Name, out var ownerFrame)
                ? Mapping(ownerFrame, null, owner.Name) : null;
        }
        foreach (var owner in demands)
        {
            var view = owner.View;
            var frame = owners[owner.Declaration];
            var mapping = Mapping(frame, empty, view.Name);
            try
            {
                PreserveCompanionSourceBounds(view.Node, frame, owner: true);
                PreserveEdges(view, mapping);
                foreach (var field in view.Fields) PreserveValueFact(field, mapping, mapping.IsNativeField(field));
                foreach (var constructor in view.Constructors)
                    foreach (var parameter in constructor.Params.OrEmpty()) PreserveValueFact(parameter, mapping);
                foreach (var property in view.Properties)
                {
                    var association = property.PropertyAssociation.OrDefault();
                    var accessor = owner.Methods.FirstOrDefault(method => association != null
                        && method.View.Function?.PropertyAssociation.OrDefault() == association
                        && method.View.Function?.PropertyAccessor.OrDefault() == "get")
                        ?? owner.Methods.FirstOrDefault(method => association != null && method.View.Function?.PropertyAssociation.OrDefault() == association);
                    var propertyMapping = accessor == null ? mapping : Mapping(frame, methodFrames[accessor.Declaration], view.Name);
                    PreserveValueFact(property, propertyMapping);
                    Rewrite(property, propertyMapping, methods, DeclarationMapping);
                }
                Rewrite(view.Node, mapping, methods, DeclarationMapping, skipChild: child => child is
                    IBirTypeDeclaration or BirRefTypeDeclaration or BirFunctionDeclaration or BirPropertyDeclaration
                    or BirInheritedDefaultMethod or BirInheritedDefaultAccessor or BirInheritedClassMethod);
                foreach (var method in owner.Methods)
                {
                    var methodFrame = methodFrames[method.Declaration];
                    var methodMapping = Mapping(frame, methodFrame, view.Name);
                    if (methodFrame.RequiresMetadata && method.View.Function is { } function
                        && function.FakeOverride.OrDefault() && !function.DeclarationId.HasValue)
                    {
                        function.DeclarationId = DeclarationIdentityBinding.InheritedProjectionId(view.Name,
                            function.Overrides.OrEmpty().Select(edge => edge.DeclarationId.OrDefault()).Where(id => id != null));
                        function.DeclarationSourceName = function.Name;
                        function.Generated = true;
                    }
                    if (!method.View.Inherited) PreserveCompanionSourceBounds(method.Declaration, methodFrame, owner: false);
                    foreach (var parameter in method.View.ValueParameters) PreserveValueFact(parameter, methodMapping);
                    if (method.View.Return is { } result && result != methodMapping.Slot(result)) method.View.SetReturnFact(result);
                    if (method.View.Inherited)
                    {
                        PreserveCompanionSourceBounds(method.Implementation, methodFrame, owner: false);
                        RewriteInheritedSignature(method.View, methodMapping, methods, DeclarationMapping);
                        var implementation = DeclarationMapping(method.Implementation);
                        RewriteImplementation(method.Implementation,
                            implementation == null ? null : Mapping(implementation.OwnerFrame, methodFrame), methodMapping);
                        AppendParameters(method.Implementation, methodFrame, policy: policy);
                        method.Implementation.Arity = methodFrame.PhysicalArity;
                        continue;
                    }
                    var split = method.Body.Method.Except(methodFrame.NullableIndices).Any()
                        || method.Body.For("method", NullableRepresentationFrame.Role.Storage).Except(methodFrame.StorageIndices).Any()
                        || method.Body.For("method", NullableRepresentationFrame.Role.NullableStorage).Except(methodFrame.NullableStorageIndices).Any();
                    var entry = method.View.Function;
                    var source = split ? (BirFunctionDeclaration)entry.DeepClone() : null;
                    if (split) entry.Body = Array.Empty<IBirStatement>();
                    Rewrite(entry, methodMapping, methods, DeclarationMapping);
                    AppendParameters(entry, methodFrame, policy: policy, enclosingParameters: view.Parameters.OrDefault(), enclosingFrame: frame);
                    if (methodFrame.RequiresMetadata) entry.NullableRepresentationFrame = methodFrame;
                    if (split) NullableBodyDispatch.Build(view, source, entry, frame, methodFrame, method.Body, (helper, helperFrame) => {
                        PreserveCompanionSourceBounds(helper, helperFrame, owner: false);
                        Rewrite(helper, Mapping(frame, helperFrame, view.Name), methods, DeclarationMapping);
                        AppendParameters(helper, helperFrame, policy: policy, bodyOnlyStorage: true,
                            enclosingParameters: view.Parameters.OrDefault(), enclosingFrame: frame);
                        helper.NullableRepresentationFrame = helperFrame;
                    });
                }
                AppendParameters(view.Node, frame, "type", policy);
                if (owner.CapturedOwner != null && view.Node is BirClassDefinition captured)
                {
                    captured.OuterTypeParamCount = owners[owner.CapturedOwner.Declaration].PhysicalArity;
                    captured.OuterTypeParamOffset = default;
                }
                if (frame.RequiresMetadata) view.Facts = (view.Facts ?? SourceTypeFacts.Empty).MergeNullableFrame(frame);
            }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException)
            { throw new InvalidOperationException("Nullable owner " + view.Name + ": " + error.Message, error); }
        }
        return methods;
    }

    static TypeNode SelectedOwner(BirGraphValue node) => node switch {
        BirNew value => value.Type.OrDefault(),
        BirCallStatic value => value.OwnerType.OrDefault() ?? value.Owner.OrDefault(),
        BirCallInstance value => value.OwnerType.OrDefault(), BirFieldGet value => value.OwnerType.OrDefault(),
        BirStaticFieldGet value => value.OwnerType.OrDefault(), BirSetField value => value.OwnerType.OrDefault(),
        BirFieldSetExpression value => value.OwnerType.OrDefault(), BirStaticFieldSet value => value.OwnerType.OrDefault(),
        BirLateinitGet value => value.OwnerType.OrDefault(), BirNewBoundDelegate value => value.OwnerType.OrDefault(),
        BirCallInline value => value.Owner.OrDefault(),
        BirInheritedImplementation value => value.Owner.OrDefault(), _ => null,
    };

    static void RewriteInheritedSignature(MethodView method, NullableRepresentationTypes mapping,
        IReadOnlyDictionary<string, NullableRepresentationFrame> methods, Func<BirGraphValue, NullableRepresentationTypes> declarationMapping)
    {
        var parameters = method.SignatureParameters.Select(mapping.Slot).ToArray();
        switch (method.Node) {
            case BirInheritedDefaultMethod target: if (target.Params.HasValue) target.Params = parameters; break;
            case BirInheritedDefaultAccessor target: if (target.Params.HasValue) target.Params = parameters; break;
            case BirInheritedClassMethod target:
                foreach (var parameter in target.Params.OrEmpty()) Rewrite(parameter, mapping, methods, declarationMapping);
                break;
        }
        if (method.Return != null) method.Return = mapping.Slot(method.Return);
    }

    static void PreserveValueFact(BirGraphValue node, NullableRepresentationTypes mapping, bool native = false)
    {
        var source = node switch { BirParameter value => value.Type.OrDefault(), BirFieldDeclaration value => value.Type.OrDefault(),
            BirPropertyDeclaration value => value.Type.OrDefault(), _ => null };
        if (source == null || source == (native ? mapping.Argument(source) : mapping.Slot(source))) return;
        switch (node) {
            case BirParameter target: if (!target.NullableGeneric.HasValue) target.NullableGeneric = source; break;
            case BirFieldDeclaration target: if (!target.NullableGeneric.HasValue) target.NullableGeneric = source; break;
            case BirPropertyDeclaration target: if (!target.NullableGeneric.HasValue) target.NullableGeneric = source; break;
        }
    }

    static void PreserveCompanionSourceBounds(BirGraphValue node, NullableRepresentationFrame frame, bool owner)
    {
        if (!frame.RequiresMetadata) return;
        var parameters = NullableRepresentationViews.TypeParameters(node).Value;
        var bounds = Enumerable.Range(0, frame.SourceArity).ToDictionary(index => index, index => parameters[index].Constraints.OrEmpty());
        if (owner) NullableRepresentationViews.SetTypeFacts(node, (NullableRepresentationViews.TypeFacts(node) ?? SourceTypeFacts.Empty).Merge(bounds));
        else NullableRepresentationViews.SetBoundsFacts(node,
            NullableRepresentationViews.BoundsFacts(node) is { } saved ? saved.Merge(bounds) : new SourceBoundsSnapshot(bounds));
    }

    static void PreserveEdges(OwnerView owner, NullableRepresentationTypes mapping)
    {
        var bounds = owner.Parameters.OrEmpty().Select((parameter, index) => (parameter, index))
            .Where(pair => pair.parameter.Constraints.HasValue && pair.parameter.Constraints.Value.Any(type => mapping.Slot(type) != type))
            .ToDictionary(pair => pair.index, pair => pair.parameter.Constraints.Value);
        var baseType = owner.Base is { } edge && mapping.Slot(edge) != edge ? edge : null;
        var interfaces = owner.Interfaces.Where(edge => mapping.Slot(edge) != edge).ToArray();
        if (baseType != null || interfaces.Length != 0 || bounds.Count != 0)
            owner.Facts = (owner.Facts ?? SourceTypeFacts.Empty).Merge(baseType, interfaces, bounds);
    }
}
