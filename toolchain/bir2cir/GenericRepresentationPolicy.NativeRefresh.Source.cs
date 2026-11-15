using System.Text.Json.Nodes;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

sealed partial class GenericRepresentationPolicy
{
    sealed record NativePhysicalOccurrence(BirFileDeclaration File, BirGraphValue Owner, BirGraphValue Declaration,
        int FileIndex, int TypeIndex, int Index, bool Cell, int TypeCount, int StorageCount,
        string FileClass, string OwnerName, string OwnerKind,
        (string Owner, string Name, bool Static, string Receiver) Key);
    NativePhysicalOccurrence[] _refreshedNativeLocations;
    BirFileDeclaration[] _nativeRefreshRoots;
    sealed record NativeRefreshTypeVector(IBirTypeDeclaration[] Original, IBirTypeDeclaration[] Continuing);
    NativeRefreshTypeVector[] _nativeRefreshTypeVectors;
    readonly Dictionary<BirGraphValue, BirGraphValue> _nativeStorageMaterializations = new(ReferenceEqualityComparer.Instance);

    // Only the producer of a replacement storage record can establish this
    // edge. It does not select a new origin or grant source carrier eligibility.
    internal void RegisterNativeStorageMaterialization(BirGraphValue origin, BirGraphValue physical)
    {
        if (!_sourceNativeStorage.Contains(origin)) return;
        if (!_sourceAwaitingDom || _pendingNativeOrigins == null || _pendingNativePhysicalBindings == null
            || !_pendingNativeOrigins.TryGetValue(origin, out var key)
            || !_pendingNativePhysicalBindings.Contains(key))
            throw new InvalidOperationException("Native storage materialization has no pending selected source origin");
        if (physical == null || _nativeStorageMaterializations.ContainsKey(origin)
            || _nativeStorageMaterializations.Values.Any(value => ReferenceEquals(value, physical)))
            throw new InvalidOperationException("Native source storage has more than one materialization association");
        _nativeStorageMaterializations.Add(origin, physical);
    }

    void ValidateOriginalNativeAdmission(NativePhysicalOccurrence occurrence)
    {
        if (!_pendingNativePhysicalBindings.Contains(occurrence.Key)) return;
        if (_pendingNativeOrigins.TryGetValue(occurrence.Declaration, out var originalKey)
            && originalKey == occurrence.Key) return;
        foreach (var (origin, physical) in _nativeStorageMaterializations)
            if (ReferenceEquals(physical, occurrence.Declaration)
                && _pendingNativeOrigins.TryGetValue(origin, out var key) && key == occurrence.Key) return;
        throw new InvalidOperationException("Native physical admission replaced a selected original source occurrence");
    }

    // This is a physical admission at the complete pre-FBound clock. It must
    // not add source origins or reconsider source existential eligibility.
    internal void RefreshPhysicalNativeLocations(IReadOnlyList<BirFileDeclaration> roots)
    {
        if (!_sourceAwaitingDom || _pendingNativePhysicalBindings == null)
            throw new InvalidOperationException("Physical native refresh requires the pending source frontier");
        if (_refreshedNativeLocations != null)
            throw new InvalidOperationException("Physical native locations were already refreshed");
        var fields = new Dictionary<(string Owner, string Name, bool Static, string Receiver), NativePhysicalOccurrence>();
        var owned = new HashSet<BirGraphValue>(ReferenceEqualityComparer.Instance);
        var selected = new HashSet<BirGraphValue>(ReferenceEqualityComparer.Instance);
        var occurrences = new List<NativePhysicalOccurrence>();
        void Select(NativePhysicalOccurrence occurrence)
        {
            // Otherwise stamping a same-key replacement would manufacture the
            // marker that the original-origin frontier is required to check.
            ValidateOriginalNativeAdmission(occurrence);
            if (!selected.Add(occurrence.Declaration)) return;
            switch (occurrence.Declaration)
            {
                case BirFieldDeclaration field: field._nativeLocation = true; break;
                case BirRefTypeDeclaration cell: cell._nativeLocation = true; break;
            }
            _nativeFieldNames.Add(occurrence.Key);
            occurrences.Add(occurrence);
        }
        void Fields(BirFileDeclaration file, BirGraphValue owner, string name, string kind,
            BirFieldDeclaration[] declarations, int fileIndex, int typeIndex)
        {
            for (var index = 0; index < declarations.Length; index++)
            {
                var field = declarations[index];
                if (name == null || !field.Name.HasValue || !field.Type.HasValue) continue;
                if (!owned.Add(field)) throw new InvalidOperationException("Native physical storage has duplicate owning occurrences");
                var key = FieldKey(name, field.Name.Value, field.Static.OrDefault(), field.CompanionReceiver.OrDefault());
                var occurrence = new NativePhysicalOccurrence(file, owner, field, fileIndex, typeIndex, index, false,
                    file.Types.OrEmpty().Length, declarations.Length, file.FileClass.OrDefault(), name, kind, key);
                fields.Add(key, occurrence);
                if (field._nativeLocation.OrDefault() || field.Attrs.OrEmpty().Any(attribute =>
                    attribute.Attr.OrDefault() is TypeNode.Fqn { Name: "kotlin.clr.ClrField" })) Select(occurrence);
            }
        }
        for (var fileIndex = 0; fileIndex < roots.Count; fileIndex++)
        {
            var file = roots[fileIndex];
            Fields(file, file, file.FileClass.OrDefault(), null, file.Fields.OrEmpty(), fileIndex, -1);
            var cells = file.RefTypes.OrEmpty();
            for (var index = 0; index < cells.Length; index++)
            {
                var cell = cells[index];
                if (!cell.Name.HasValue) continue;
                if (!owned.Add(cell)) throw new InvalidOperationException("Native capture storage has duplicate owning occurrences");
                var key = FieldKey(cell.Name.Value, "v", false, (TypeNode)null);
                var occurrence = new NativePhysicalOccurrence(file, file, cell, fileIndex, -1, index, true,
                    file.Types.OrEmpty().Length, cells.Length, file.FileClass.OrDefault(), file.FileClass.OrDefault(), null, key);
                fields.Add(key, occurrence);
                if (cell._nativeLocation.OrDefault()) Select(occurrence);
            }
            var types = file.Types.OrEmpty();
            for (var index = 0; index < types.Length; index++)
                Fields(file, (BirGraphValue)types[index], types[index].Name.OrDefault(), types[index].Kind.OrDefault(),
                    types[index].Fields.OrEmpty(), fileIndex, index);
        }
        void Walk(BirGraphValue node)
        {
            if (node is IBirExecutablePayload) return;
            var (parameters, arguments) = NativeRefreshArguments(node);
            if (node is BirGraphNode operation && NativeRefreshCall(operation.Tag)
                && parameters != null && arguments != null && parameters.Length == arguments.Length)
                for (var index = 0; index < parameters.Length; index++)
                {
                    if (!NativeRefreshAddress(parameters[index])) continue;
                    var expression = arguments[index] is BirByRefOf address ? address.Inner.OrDefault() : arguments[index];
                    var (owner, name, receiver) = expression switch {
                        BirFieldGet field => (field.OwnerType.OrDefault() as TypeNode.Fqn, field.Name.OrDefault(), (TypeNode)null),
                        BirStaticFieldGet field => (field.OwnerType.OrDefault() as TypeNode.Fqn, field.Name.OrDefault(), field.CompanionReceiver.OrDefault()),
                        _ => (null, null, null),
                    };
                    if (expression is not BirGraphNode location || owner == null || name == null
                        || !NativeRefreshLocation(location.Tag, out var isStatic)) continue;
                    var key = FieldKey(owner.Name, name, isStatic, receiver);
                    if (fields.TryGetValue(key, out var occurrence)) Select(occurrence);
                    else if (_localOwners.Contains(owner.Name)
                        || _references?.HasReferencedFieldLocation(owner, name, isStatic) != true) continue;
                    switch (expression)
                    {
                        case BirFieldGet field: field._nativeFieldAddress = true; field._nativeLocation = true; break;
                        case BirStaticFieldGet field: field._nativeFieldAddress = true; field._nativeLocation = true; break;
                    }
                }
            node.VisitOwnChildSlots((field, role, slot) => {
                if (field != "attrs" && !NullableRepresentationViews.Frozen(role)) Walk(slot.Value);
            });
        }
        foreach (var root in roots) Walk(root);
        _nativeRefreshRoots = roots.ToArray();
        _nativeRefreshTypeVectors = roots.Select(file => {
            var original = file.Types.OrEmpty().ToArray();
            return new NativeRefreshTypeVector(original, original);
        }).ToArray();
        _refreshedNativeLocations = occurrences.ToArray();
    }

    // A later representation producer may append actual physical declarations.
    // This reports structure only: it neither reselects native storage nor
    // changes source origins, eligibility, fields or the captured type prefix.
    internal void AdmitMaterializedNativeTypes(BirFileDeclaration file, IReadOnlyList<IBirTypeDeclaration> appended)
    {
        if (!_sourceAwaitingDom || _refreshedNativeLocations == null || _nativeRefreshRoots == null
            || _nativeRefreshTypeVectors == null || appended == null)
            throw new InvalidOperationException("Native materialized types have no pending physical refresh");
        var index = Array.FindIndex(_nativeRefreshRoots, root => ReferenceEquals(root, file));
        if (index < 0 || Array.FindLastIndex(_nativeRefreshRoots, root => ReferenceEquals(root, file)) != index)
            throw new InvalidOperationException("Native materialized types changed their owning file");
        var prior = _nativeRefreshTypeVectors[index];
        var continuing = prior.Continuing.Concat(appended).ToArray();
        if (continuing.Any(type => type == null)
            || !file.Types.OrEmpty().SequenceEqual(continuing, ReferenceEqualityComparer.Instance)
            || new HashSet<IBirTypeDeclaration>(continuing, ReferenceEqualityComparer.Instance).Count != continuing.Length)
            throw new InvalidOperationException("Native materialized types do not match their exact appended occurrences");
        _nativeRefreshTypeVectors[index] = prior with { Continuing = continuing };
    }

    internal void RetireMaterializedNativeTypes(BirFileDeclaration file, IReadOnlyList<IBirTypeDeclaration> removed)
    {
        if (!_sourceAwaitingDom || _nativeRefreshRoots == null || _nativeRefreshTypeVectors == null)
            throw new InvalidOperationException("Native type retirement has no pending physical refresh");
        var index = Array.FindIndex(_nativeRefreshRoots, root => ReferenceEquals(root, file));
        if (index < 0) throw new InvalidOperationException("Native type retirement changed its owning file");
        var prior = _nativeRefreshTypeVectors[index];
        if (!file.Types.OrEmpty().SequenceEqual(prior.Continuing, ReferenceEqualityComparer.Instance)
            || removed.Distinct(ReferenceEqualityComparer.Instance).Count() != removed.Count
            || removed.Any(type => prior.Continuing.Count(actual => ReferenceEquals(actual, type)) != 1))
            throw new InvalidOperationException("Native type retirement changed its exact owning vector");
        _nativeRefreshTypeVectors[index] = prior with { Continuing = prior.Continuing.Where(type =>
            !removed.Any(actual => ReferenceEquals(actual, type))).ToArray() };
    }

    static (TypeNode[] Parameters, IBirExpression[] Arguments) NativeRefreshArguments(BirGraphValue node) => node switch {
        BirCallStatic value => (NativeRefreshParameters(value.ShapeTypes.OrDefault(), value.Sig.OrDefault(), value.MemberSignature.OrDefault(), value.ArgTypes.OrDefault()), value.Args.OrDefault()),
        BirCallInstance value => (NativeRefreshParameters(value.ShapeTypes.OrDefault(), value.Sig.OrDefault(), value.MemberSignature.OrDefault(), value.ArgTypes.OrDefault()), value.Args.OrDefault()),
        BirCallLocal value => (value.Sig.OrDefault(), value.Args.OrDefault()),
        BirNew value => (NativeRefreshParameters<TypeNode[]>(null, null, value.MemberSignature.OrDefault(), value.ArgTypes.OrDefault()), value.Args.OrDefault()),
        BirNewClr value => (NativeRefreshParameters<TypeNode[]>(null, null, value.MemberSignature.OrDefault(), value.ArgTypes.OrDefault()), value.Args.OrDefault()),
        BirConstrainedCall value => (NativeRefreshParameters<TypeNode[]>(null, value.Sig.OrDefault(), null, value.ArgTypes.OrDefault()), value.Args.OrDefault()),
        BirNativeStatic value => (NativeRefreshParameters<TypeNode[]>(null, value.Sig.OrDefault(), null, value.ArgTypes.OrDefault()), value.Args.OrDefault()),
        BirNativeInstance value => (NativeRefreshParameters<TypeNode[]>(null, value.Sig.OrDefault(), null, value.ArgTypes.OrDefault()), value.Args.OrDefault()),
        // Generic CLR calls carry resolvedMemberParams, not any of the four
        // vectors consumed by this clock. That descriptor is not a fallback.
        _ => (null, null),
    };

    void BindRefreshedNativeLocations(IReadOnlyList<JsonNode> roots, SourceDeclarationProjection declarations = null)
    {
        if (_refreshedNativeLocations == null) return;
        if (roots.Count != _nativeRefreshRoots.Length)
            throw new InvalidOperationException("Native physical admission changed module occurrences");
        for (var index = 0; index < roots.Count; index++)
            if (!_nativeRefreshRoots[index].Types.OrEmpty().SequenceEqual(
                    _nativeRefreshTypeVectors[index].Continuing, ReferenceEqualityComparer.Instance)
                || roots[index] is not JsonObject root
                || ((root["types"] as JsonArray)?.Count ?? 0) != _nativeRefreshTypeVectors[index].Continuing.Length)
                throw new InvalidOperationException("Native physical admission changed its reported type occurrences");
        var admitted = new HashSet<JsonObject>(ReferenceEqualityComparer.Instance);
        foreach (var occurrence in _refreshedNativeLocations)
        {
            ValidateOriginalNativeAdmission(occurrence);
            var typeVector = _nativeRefreshTypeVectors[occurrence.FileIndex];
            if (!ReferenceEquals(_nativeRefreshRoots[occurrence.FileIndex], occurrence.File)
                || occurrence.File.FileClass.OrDefault() != occurrence.FileClass
                || typeVector.Original.Length != occurrence.TypeCount
                || roots[occurrence.FileIndex] is not JsonObject file
                || Text(file["fileClass"]) != occurrence.FileClass
                || ((file["types"] as JsonArray)?.Count ?? 0) != typeVector.Continuing.Length)
                throw new InvalidOperationException("Native physical admission changed its owning file");
            var sourceOwner = occurrence.Owner;
            JsonObject owner;
            if (occurrence.TypeIndex < 0) owner = file;
            else
            {
                var position = Array.FindIndex(typeVector.Continuing, actual => ReferenceEquals(actual, occurrence.Owner));
                if (position >= 0)
                {
                    if (position >= occurrence.File.Types.OrEmpty().Length
                        || !ReferenceEquals(occurrence.File.Types.Value[position], occurrence.Owner))
                        throw new InvalidOperationException("Native physical admission lost its continuing owner");
                    owner = (file["types"] as JsonArray)?[position] as JsonObject;
                }
                else if (declarations != null && declarations.TryGet(occurrence.Owner, out var retired)
                    && declarations.IsRetired(occurrence.Owner, retired)) owner = retired;
                else throw new InvalidOperationException("Native physical admission has no reported owner retirement");
            }
            if (!ReferenceEquals(sourceOwner, occurrence.Owner) || owner == null
                || occurrence.TypeIndex >= 0 && !ReferenceEquals(typeVector.Original[occurrence.TypeIndex], occurrence.Owner)
                || occurrence.TypeIndex >= 0 && (Text(owner["name"]) != occurrence.OwnerName
                    || Text(owner["kind"]) != occurrence.OwnerKind
                    || ((IBirTypeDeclaration)sourceOwner).Name.OrDefault() != occurrence.OwnerName
                    || ((IBirTypeDeclaration)sourceOwner).Kind.OrDefault() != occurrence.OwnerKind))
                throw new InvalidOperationException("Native physical admission changed its declaring occurrence");
            JsonObject declaration;
            TypeNode type;
            (string Owner, string Name, bool Static, string Receiver) key;
            if (occurrence.Cell)
            {
                var cells = occurrence.File.RefTypes.OrEmpty();
                if (cells.Length != occurrence.StorageCount || !ReferenceEquals(cells[occurrence.Index], occurrence.Declaration)
                    || !cells[occurrence.Index]._nativeLocation.OrDefault()
                    || owner["refTypes"] is not JsonArray domCells || domCells.Count != cells.Length
                    || domCells[occurrence.Index] is not JsonObject cell)
                    throw new InvalidOperationException("Native physical capture admission changed its storage occurrence");
                declaration = cell;
                type = cells[occurrence.Index].Elem.OrDefault();
                key = FieldKey(Text(cell["name"]), "v", false, cell);
                if (FieldKey(cells[occurrence.Index].Name.OrDefault(), "v", false, (TypeNode)null) != occurrence.Key)
                    throw new InvalidOperationException("Native physical capture admission changed its selected role");
            }
            else
            {
                var fields = sourceOwner is BirFileDeclaration root ? root.Fields.OrEmpty() : ((IBirTypeDeclaration)sourceOwner).Fields.OrEmpty();
                if (fields.Length != occurrence.StorageCount || !ReferenceEquals(fields[occurrence.Index], occurrence.Declaration)
                    || !fields[occurrence.Index]._nativeLocation.OrDefault()
                    || owner["fields"] is not JsonArray domFields || domFields.Count != fields.Length
                    || domFields[occurrence.Index] is not JsonObject field)
                    throw new InvalidOperationException("Native physical field admission changed its storage occurrence");
                declaration = field;
                type = fields[occurrence.Index].Type.OrDefault();
                key = FieldKey(occurrence.OwnerName, Text(field["name"]), field["static"]?.GetValue<bool>() == true, field);
                var current = fields[occurrence.Index];
                if (FieldKey(occurrence.OwnerName, current.Name.OrDefault(), current.Static.OrDefault(), current.CompanionReceiver.OrDefault()) != occurrence.Key)
                    throw new InvalidOperationException("Native physical field admission changed its selected role");
            }
            if (key != occurrence.Key || declaration[NativeLocationKey]?.GetValue<bool>() != true
                || !TypeNodeDataEquality.Equal(type, TypeJson.Read(declaration[occurrence.Cell ? "elem" : "type"]))
                || !admitted.Add(declaration))
                throw new InvalidOperationException("Native physical admission does not match the continuing declaration");
        }
        foreach (var declaration in admitted) _nativeFields.Add(declaration);
        _refreshedNativeLocations = null;
        _nativeRefreshRoots = null;
        _nativeRefreshTypeVectors = null;
    }
}
