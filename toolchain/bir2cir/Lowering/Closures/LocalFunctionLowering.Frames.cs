using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class LocalFunctionLowering
{
    static Binding PrepareGenericOwnerBinding(BirFunctionDeclaration method, string owner, IBirTypeDeclaration ownerType)
    {
        var ownerArity = ownerType?.TypeParams.OrEmpty().Length ?? 0;
        var ownerPositions = Enumerable.Repeat(-1, ownerArity).ToArray();
        if (ownerArity > 0 && method.Mods.OrDefault()?.Suspend.OrDefault() == true)
            method.LexicalOwnerTypeParamCount = ownerArity;
        if (method._syntheticTypeArgs.HasValue && method.TypeParams.HasValue
            && method._syntheticTypeArgs.Value.Length == method.TypeParams.Value.Length)
        {
            var origins = method._syntheticTypeArgs.Value;
            var declarations = method.TypeParams.Value;
            var keep = new List<int>();
            for (var index = 0; index < origins.Length; index++)
                if (origins[index] is TypeNode.Tv { Scope: "type" } variable && variable.I >= 0 && variable.I < ownerArity)
                    ownerPositions[variable.I] = index;
                else keep.Add(index);
            var physical = new Dictionary<int, TypeNode>();
            var next = 0;
            for (var index = 0; index < origins.Length; index++)
            {
                if (origins[index] is not TypeNode.Tv variable)
                    throw new InvalidOperationException($"local function '{method.SourceName.OrDefault() ?? method.Name.OrDefault()}' has a non-variable synthetic type origin");
                physical[index] = variable.Scope == "type" && variable.I >= 0 && variable.I < ownerArity
                    ? new TypeNode.Tv("type", variable.I) : new TypeNode.Tv("method", next++);
            }
            method.TypeParams = keep.Select(index => declarations[index]).ToArray();
            var prior = method.NullableRepresentationFrame.OrDefault() ?? new NullableRepresentationFrame(origins.Length, Array.Empty<int>());
            var retainedSources = Enumerable.Range(0, prior.SourceArity).Where(index => keep.Contains(prior.SourcePosition(index))).ToArray();
            var frame = prior.RetainSources(retainedSources);
            if (!keep.SequenceEqual(Enumerable.Range(0, prior.PhysicalArity)
                .Where(index => retainedSources.Contains(prior.PhysicalSlot(index).SourceIndex))))
                throw new InvalidOperationException("Local function capture split a source parameter's representation frame");
            if (method.NullableRepresentationFrame.IsPresent) method.NullableRepresentationFrame = frame;
            var ownerFrame = ownerType == null ? null : NullableRepresentationViews.TypeFacts((BirGraphValue)ownerType)?.NullableFrame;
            var sourceMap = new Dictionary<int, TypeNode>();
            for (var index = 0; index < prior.SourceArity; index++)
            {
                var variable = (TypeNode.Tv)physical[prior.SourcePosition(index)];
                sourceMap[index] = variable.Scope == "type" ? ownerFrame?.SemanticVariable(variable) ?? variable : frame.SemanticVariable(variable);
            }
            // Saved Kotlin types retain SOURCE positions, unlike structured CLR
            // slots. Rewrite their correspondence once, before physical remap.
            method.NullableGenericRet = RemapSource(method.NullableGenericRet, sourceMap);
            method.RetKotlinType = RemapSource(method.RetKotlinType, sourceMap);
            foreach (var parameter in method.Params.OrEmpty())
            {
                parameter.NullableGeneric = RemapSource(parameter.NullableGeneric, sourceMap);
                parameter.KotlinType = RemapSource(parameter.KotlinType, sourceMap);
            }
            RewriteCapturedTypeVariables(method, physical);
            method._syntheticTypeArgs = default;
            if (keep.Count == 0) method.TypeParams = default;
        }
        return new Binding(null, owner, ownerPositions, Array.Empty<int>());
    }

    static OptionalField<TypeNode> RemapSource(OptionalField<TypeNode> source, IReadOnlyDictionary<int, TypeNode> map) =>
        source.HasValue ? OptionalField<TypeNode>.Of(RewriteCapturedType(source.Value, map)) : source;

    static TypeNode RewriteCapturedType(TypeNode type, IReadOnlyDictionary<int, TypeNode> map) =>
        TypedExecutableFrames.RewriteType(type, variable => variable.Scope == "method" && map.TryGetValue(variable.I, out var physical) ? physical : null);

    static void RewriteCapturedTypeVariables(BirGraphValue node, IReadOnlyDictionary<int, TypeNode> map)
    {
        if (node == null || node is IBirExecutablePayload or BirLocalFunction) return;
        // Origins describe the enclosing frame, never this receiving method's
        // dense frame. All other structured member descriptors follow the old
        // local-function remap; signature vectors and frozen carriers do not.
        var method = node as BirFunctionDeclaration;
        var origins = method?._syntheticTypeArgs ?? default;
        if (method != null) method._syntheticTypeArgs = default;
        try
        {
            bool Includes(BirOwnershipRole role) => role is not (BirOwnershipRole.ForeignVector or BirOwnershipRole.FrozenFact);
            node.RewriteOwnershipInPlace((role, type) => Includes(role) ? RewriteCapturedType(type, map) : type,
                (role, child) => { if (Includes(role)) RewriteCapturedTypeVariables(child, map); });
        }
        finally { if (method != null) method._syntheticTypeArgs = origins; }
    }

    static int[] PrepareCaptureLocations(BirFunctionDeclaration declaration)
    {
        var slots = new List<int>();
        var locations = new Dictionary<string, TypeNode>(StringComparer.Ordinal);
        var parameters = declaration.Params.OrEmpty();
        for (var index = 0; index < parameters.Length; index++)
        {
            var parameter = parameters[index];
            if (!parameter.SharedCellType.HasValue || TypeNodeDataEquality.Equal(parameter.Type.OrDefault(), parameter.SharedCellType.Value)) continue;
            var element = BirGraphValue.CopyType(parameter.Type.OrDefault());
            locations.Add(parameter.Name.OrDefault(), element);
            parameter.Type = new TypeNode.ByRef(element);
            slots.Add(index);
        }
        if (slots.Count == 0) return Array.Empty<int>();
        BirGraphValue Rewrite(BirGraphValue node)
        {
            if (node == null || node is IBirExecutablePayload or BirLocalFunction) return node;
            if (node is BirLocal local && local.Name.HasValue && locations.TryGetValue(local.Name.Value, out var element))
                return new BirByRefLoad { Ptr = OptionalField<IBirExpression>.Of(new BirLocal { Name = local.Name }), Elem = BirGraphValue.CopyType(element) };
            BirGraphValue synthetic = node switch {
                BirNewClosure closure => closure.SynthClass.OrDefault(), BirNewSam sam => sam.SynthClass.OrDefault(), _ => null,
            };
            node.VisitChildSlots(slot => { if (!ReferenceEquals(slot.Value, synthetic)) slot.Replace(Rewrite(slot.Value)); });
            if (node is BirSetLocal write && write.Name.HasValue && locations.TryGetValue(write.Name.Value, out var written))
                return new BirExpressionStatement { Expr = OptionalField<IBirExpression>.Of(new BirByRefStore {
                    Ptr = OptionalField<IBirExpression>.Of(new BirLocal { Name = write.Name }), Elem = BirGraphValue.CopyType(written), Value = write.Value,
                }) };
            return node;
        }
        if (declaration.Body.HasValue)
            for (var index = 0; index < declaration.Body.Value.Length; index++)
                declaration.Body.Value[index] = (IBirStatement)Rewrite((BirGraphValue)declaration.Body.Value[index]);
        return slots.ToArray();
    }
}
