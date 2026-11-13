using System.Text.Json.Nodes;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

sealed partial class GenericRepresentationPolicy
{
    readonly record struct SourceOwner(string Name, bool HasKind, bool Generated,
        BirTypeParameter[] Parameters, BirAttribute[] Attributes, BirFieldDeclaration[] Fields);

    static SourceOwner Owner(IBirTypeDeclaration declaration) => declaration switch {
        BirClassDefinition value => new(value.Name.OrDefault(), value.Kind.HasValue, value.Generated.OrDefault(),
            value.TypeParams.OrEmpty(), value.Attrs.OrEmpty(), value.Fields.OrEmpty()),
        BirInterfaceDefinition value => new(value.Name.OrDefault(), value.Kind.HasValue, value.Generated.OrDefault(),
            value.TypeParams.OrEmpty(), value.Attrs.OrEmpty(), value.Fields.OrEmpty()),
        BirEnumDefinition value => new(value.Name.OrDefault(), value.Kind.HasValue, value.Generated.OrDefault(),
            value.TypeParams.OrEmpty(), value.Attrs.OrEmpty(), value.Fields.OrEmpty()),
        _ => throw new InvalidOperationException("Unaccounted generic source owner: " + declaration.GetType().Name),
    };

    internal static GenericRepresentationPolicy CreateSource(IReadOnlyList<BirFileDeclaration> roots,
        IReadOnlyDictionary<string, string> aliases, ReferenceMetadataIndex references)
    {
        var policy = new GenericRepresentationPolicy(aliases, references, sourceOwned: true);
        foreach (var root in roots)
        {
            foreach (var declaration in root.Types.OrEmpty())
            {
                var owner = Owner(declaration);
                if (owner.Name == null || !owner.HasKind) continue;
                policy._localOwners.Add(owner.Name);
                var alias = owner.Attributes.Any(attribute => attribute.Attr.OrDefault() is TypeNode.Fqn { Name: "kotlin.clr.ClrTypeAlias" });
                if (FBoundStarProjectionErasure.IsSourceGeneric(owner.Name, owner.Generated, alias, owner.Parameters.Length))
                    policy._existentialOwners.Add(owner.Name);
            }
            foreach (var cell in root.RefTypes.OrEmpty())
                if (cell.Name.HasValue) policy._localOwners.Add(cell.Name.Value);
        }
        policy.RecordSourceNativeFields(roots);
        return policy;
    }

    void RecordSourceNativeFields(IReadOnlyList<BirFileDeclaration> roots)
    {
        var fields = new Dictionary<(string Owner, string Name, bool Static, string Receiver), BirGraphValue>();
        void Fields(string owner, IEnumerable<BirFieldDeclaration> declarations)
        {
            foreach (var field in declarations)
            {
                if (owner == null || !field.Name.HasValue || !field.Type.HasValue) continue;
                var key = FieldKey(owner, field.Name.Value, field.Static.OrDefault(), field.CompanionReceiver.OrDefault());
                fields.Add(key, field);
                if (field.Attrs.OrEmpty().Any(attribute => attribute.Attr.OrDefault() is TypeNode.Fqn { Name: "kotlin.clr.ClrField" }))
                {
                    field._nativeLocation = true;
                    _nativeFieldNames.Add(key);
                }
            }
        }
        foreach (var root in roots)
        {
            Fields(root.FileClass.OrDefault(), root.Fields.OrEmpty());
            foreach (var cell in root.RefTypes.OrEmpty())
                if (cell.Name.HasValue) fields.Add(FieldKey(cell.Name.Value, "v", false, (TypeNode)null), cell);
            foreach (var declaration in root.Types.OrEmpty())
            {
                var owner = Owner(declaration);
                Fields(owner.Name, owner.Fields);
            }
        }
        void Walk(BirGraphValue node)
        {
            if (node is IBirExecutablePayload) return;
            var (parameters, arguments) = Arguments(node);
            if (parameters != null && arguments != null && parameters.Length == arguments.Length)
                for (var index = 0; index < parameters.Length; index++)
                {
                    if (SignatureValueTypes.Of(parameters[index]) is not TypeNode.ByRef) continue;
                    var expression = arguments[index] is BirByRefOf address ? address.Inner.OrDefault() : arguments[index];
                    var (owner, name, isStatic, receiver) = expression switch {
                        BirFieldGet value => (value.OwnerType.OrDefault() as TypeNode.Fqn, value.Name.OrDefault(), false, (TypeNode)null),
                        BirStaticFieldGet value => (value.OwnerType.OrDefault() as TypeNode.Fqn, value.Name.OrDefault(), true, value.CompanionReceiver.OrDefault()),
                        _ => (null, null, false, null),
                    };
                    if (owner == null || name == null) continue;
                    var key = FieldKey(owner.Name, name, isStatic, receiver);
                    if (fields.TryGetValue(key, out var declaration))
                    {
                        switch (declaration)
                        {
                            case BirFieldDeclaration field: field._nativeLocation = true; break;
                            case BirRefTypeDeclaration cell: cell._nativeLocation = true; break;
                            default: throw new InvalidOperationException("Unaccounted native source storage declaration");
                        }
                        _nativeFieldNames.Add(key);
                    }
                    else if (_localOwners.Contains(owner.Name)
                        || _references?.HasReferencedFieldLocation(owner, name, isStatic) != true) continue;
                    switch (expression)
                    {
                        case BirFieldGet value: value._nativeFieldAddress = true; value._nativeLocation = true; break;
                        case BirStaticFieldGet value: value._nativeFieldAddress = true; value._nativeLocation = true; break;
                    }
                }
            node.VisitChildren(Walk);
        }
        foreach (var root in roots) Walk(root);
    }

    // Only these current operation shapes carry both the old pass's selected
    // parameter-vector vocabulary and an argument vector. Other CLR bound
    // members/delegates have no such vector and were not address-use sites.
    static (TypeNode[] Parameters, IBirExpression[] Arguments) Arguments(BirGraphValue node) => node switch {
        BirCallStatic value => (value.ShapeTypes.OrDefault() ?? value.Sig.OrDefault() ?? value.MemberSignature.OrDefault() ?? value.ArgTypes.OrDefault(), value.Args.OrDefault()),
        BirCallInstance value => (value.ShapeTypes.OrDefault() ?? value.Sig.OrDefault() ?? value.MemberSignature.OrDefault() ?? value.ArgTypes.OrDefault(), value.Args.OrDefault()),
        BirCallLocal value => (value.Sig.OrDefault(), value.Args.OrDefault()),
        BirNew value => (value.MemberSignature.OrDefault() ?? value.ArgTypes.OrDefault(), value.Args.OrDefault()),
        BirNewClr value => (value.MemberSignature.OrDefault() ?? value.ArgTypes.OrDefault(), value.Args.OrDefault()),
        BirConstrainedCall value => (value.Sig.OrDefault() ?? value.ArgTypes.OrDefault(), value.Args.OrDefault()),
        BirNativeStatic value => (value.Sig.OrDefault() ?? value.ArgTypes.OrDefault(), value.Args.OrDefault()),
        BirNativeInstance value => (value.Sig.OrDefault() ?? value.ArgTypes.OrDefault(), value.Args.OrDefault()),
        _ => (null, null),
    };

    // Re-associate only the exact declarations carried across the one-way
    // frontier. Selection, source eligibility, and native flags already exist.
    internal void BindDomDeclarations(IReadOnlyList<JsonNode> roots)
    {
        if (!_sourceAwaitingDom) throw new InvalidOperationException("Source generic policy is already bound to its DOM continuation");
        var matched = new HashSet<(string Owner, string Name, bool Static, string Receiver)>();
        void BindOwners(JsonObject declaration)
        {
            if (declaration["kind"] != null && Text(declaration["name"]) is string owner)
                _localDeclarations[owner] = declaration;
            foreach (var type in (declaration["types"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                BindOwners(type);
            foreach (var cell in (declaration["refTypes"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                if (Text(cell["name"]) is string cellOwner) _localDeclarations[cellOwner] = cell;
        }
        void BindFields(JsonObject container, string owner)
        {
            foreach (var field in (container["fields"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                if (owner != null && Text(field["name"]) is string name && TypeJson.Read(field["type"]) != null)
                {
                    var key = FieldKey(owner, name, field["static"]?.GetValue<bool>() == true, field);
                    BindNative(key, field);
                }
            foreach (var cell in (container["refTypes"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                if (Text(cell["name"]) is string name)
                {
                    BindNative(FieldKey(name, "v", false, cell), cell);
                }
            foreach (var type in (container["types"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                BindFields(type, Text(type["name"]));
        }
        void BindNative((string Owner, string Name, bool Static, string Receiver) key, JsonObject declaration)
        {
            if (!_nativeFieldNames.Contains(key)) return;
            if (declaration[NativeLocationKey]?.GetValue<bool>() != true)
                throw new InvalidOperationException("Native storage selection was not carried across the owning frontier");
            _nativeFields.Add(declaration);
            matched.Add(key);
        }
        foreach (var root in roots.OfType<JsonObject>())
        {
            BindOwners(root);
            BindFields(root, Text(root["fileClass"]));
        }
        if (!matched.SetEquals(_nativeFieldNames))
            throw new InvalidOperationException("A selected native source declaration is absent from its DOM continuation");
        _sourceAwaitingDom = false;
    }
}
