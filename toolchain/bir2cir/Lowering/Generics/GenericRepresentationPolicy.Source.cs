using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

sealed partial class GenericRepresentationPolicy
{
    readonly HashSet<BirGraphValue> _sourceNativeStorage = new(ReferenceEqualityComparer.Instance);
    HashSet<(string Owner, string Name, bool Static, string Receiver)> _pendingNativePhysicalBindings;
    Dictionary<BirGraphValue, (string Owner, string Name, bool Static, string Receiver)> _pendingNativeOrigins;
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
                    _sourceNativeStorage.Add(field);
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
                        _sourceNativeStorage.Add(declaration);
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

    // Placement changes a physical owner/name, not the source decision about
    // which storage needs a native slot. Follow the selected owning records;
    // neither carrier spelling nor a newly generated field can select an origin.
    internal void PrepareSourceLocations(IReadOnlyList<BirFileDeclaration> roots)
    {
        if (!_sourceContinuationPending) throw new InvalidOperationException("Source generic policy already completed its owning continuation");
        if (_pendingNativePhysicalBindings != null) throw new InvalidOperationException("Source native locations are already prepared for their owning continuation");
        var matched = new HashSet<BirGraphValue>(ReferenceEqualityComparer.Instance);
        var physical = new HashSet<(string Owner, string Name, bool Static, string Receiver)>();
        var origins = new Dictionary<BirGraphValue, (string Owner, string Name, bool Static, string Receiver)>(ReferenceEqualityComparer.Instance);
        void Fields(string owner, IEnumerable<BirFieldDeclaration> fields)
        {
            foreach (var field in fields)
            {
                if (!_sourceNativeStorage.Contains(field)) continue;
                if (owner == null || !field.Name.HasValue || !field._nativeLocation.OrDefault())
                    throw new InvalidOperationException("Selected native storage lost its physical declaration");
                var key = FieldKey(owner, field.Name.Value, field.Static.OrDefault(), field.CompanionReceiver.OrDefault());
                physical.Add(key);
                origins.Add(field, key);
                matched.Add(field);
            }
        }
        foreach (var root in roots)
        {
            Fields(root.FileClass.OrDefault(), root.Fields.OrEmpty());
            foreach (var type in root.Types.OrEmpty()) Fields(type.Name.OrDefault(), type.Fields.OrEmpty());
            foreach (var cell in root.RefTypes.OrEmpty())
            {
                if (!_sourceNativeStorage.Contains(cell)) continue;
                if (!cell.Name.HasValue || !cell._nativeLocation.OrDefault())
                    throw new InvalidOperationException("Selected native capture storage lost its physical declaration");
                var key = FieldKey(cell.Name.Value, "v", false, (TypeNode)null);
                physical.Add(key);
                origins.Add(cell, key);
                matched.Add(cell);
            }
        }
        if (!matched.SetEquals(_sourceNativeStorage))
            throw new InvalidOperationException("Placement discarded a selected native source declaration");
        // Some uses still carry their selected source owner until late generic-static binding.
        // Their native role survives placement independently of the physical declarations we must bind below.
        _nativeFieldNames.UnionWith(physical);
        _pendingNativePhysicalBindings = physical;
        _pendingNativeOrigins = origins;
    }

    // Complete custody on the SAME current/explicitly retired occurrences.
    // Native selection, physical refresh, materialization and renaming already
    // happened at their owning clocks. This is validation, never re-admission.
    internal void CompleteSourceContinuation(IReadOnlyList<BirFileDeclaration> roots,
        AliasDeclarationTransitions aliases, SourceDeclarationProjection declarations)
    {
        ArgumentNullException.ThrowIfNull(declarations);
        if (!_sourceContinuationPending)
            throw new InvalidOperationException("Source generic policy already completed its owning continuation");
        var expected = _pendingNativePhysicalBindings
            ?? throw new InvalidOperationException("Source native locations were not prepared for their continuation");
        if (_pendingNativeOrigins == null)
            throw new InvalidOperationException("Source native locations lost their selected origins");
        foreach (var root in roots) declarations.RequireOccurrence(root);
        aliases?.ValidateSourceContinuations(roots);
        if (aliases?.HasTransitions == true)
            foreach (var field in _sourceNativeStorage.OfType<BirFieldDeclaration>()) aliases.ValidateNativeField(field);
        var matched = new HashSet<(string Owner, string Name, bool Static, string Receiver)>();
        void BindNative((string Owner, string Name, bool Static, string Receiver) key, BirGraphValue actual)
        {
            if (!expected.Contains(key)) return;
            if (!_pendingNativeOrigins.Any(origin => origin.Value == key
                && ReferenceEquals(_nativeStorageMaterializations.TryGetValue(origin.Key, out var materialized)
                    ? materialized : origin.Key, actual)))
                throw new InvalidOperationException("Native source storage changed its exact continuing origin");
            declarations.RequireOccurrence(actual);
            var marked = actual switch {
                BirFieldDeclaration field => field._nativeLocation.OrDefault(),
                BirRefTypeDeclaration cell => cell._nativeLocation.OrDefault(),
                _ => false,
            };
            if (!marked) throw new InvalidOperationException("Native storage selection was not carried through its owning continuation");
            matched.Add(key);
        }
        void Fields(string owner, IEnumerable<BirFieldDeclaration> fields)
        {
            foreach (var field in fields)
                if (owner != null && field.Name.HasValue && field.Type.HasValue)
                    BindNative(FieldKey(owner, field.Name.Value, field.Static.OrDefault(), field.CompanionReceiver.OrDefault()), field);
        }
        foreach (var root in roots)
        {
            Fields(root.FileClass.OrDefault(), root.Fields.OrEmpty());
            foreach (var type in root.Types.OrEmpty()) Fields(type.Name.OrDefault(), type.Fields.OrEmpty());
            foreach (var cell in root.RefTypes.OrEmpty())
                if (cell.Name.HasValue) BindNative(FieldKey(cell.Name.Value, "v", false, (TypeNode)null), cell);
        }
        foreach (var owner in declarations.RetiredTypes)
        {
            var actual = (BirGraphValue)owner;
            declarations.RequireOccurrence(actual);
            if (!declarations.IsRetired(actual))
                throw new InvalidOperationException("Native owner is not inside its reported retired forest");
            Fields(owner.Name.OrDefault(), owner.Fields.OrEmpty());
        }
        if (aliases != null)
            foreach (var (_, field) in aliases.RetiredFields)
            {
                if (!_sourceNativeStorage.Contains(field)) continue;
                if (!_pendingNativeOrigins.TryGetValue(field, out var key)
                    || !expected.Contains(key) || matched.Contains(key) || !field._nativeLocation.OrDefault()
                    || FieldKey(key.Owner, field.Name.OrDefault(), field.Static.OrDefault(), field.CompanionReceiver.OrDefault()) != key)
                    throw new InvalidOperationException("Retired native storage does not match its selected source occurrence");
                declarations.RequireOccurrence(field);
                if (!declarations.IsRetired(field))
                    throw new InvalidOperationException("Native field is not inside its reported retired forest");
                BindNative(key, field);
            }
        if (!matched.SetEquals(expected))
            throw new InvalidOperationException("A selected native source declaration is absent from its owning continuation");
        CompleteRefreshedNativeLocations(roots, declarations);
        _nativeStorageMaterializations.Clear();
        _sourceNativeStorage.Clear();
        _pendingNativePhysicalBindings = null;
        _pendingNativeOrigins = null;
        _sourceContinuationPending = false;
    }
}
