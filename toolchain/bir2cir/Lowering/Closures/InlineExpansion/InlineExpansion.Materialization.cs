using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

sealed partial class InlineExpansion
{
    internal InlineMaterialization MaterializeCarrier(BirInlineLambda carrier, TypeNode functionType,
        List<IBirStatement> prefixStatements, string semanticOwner)
    {
        if (functionType is not TypeNode.Fn function) return InlineMaterialization.Refused("MC:funcType-not-fn");
        var work = new BirValueBlock {
            Stmts = carrier.Body.OrEmpty().Select(value => (IBirStatement)((BirGraphValue)value).DeepClone()).ToArray(),
            Result = carrier.Result.HasValue ? OptionalField<IBirExpression>.Of(Copy(carrier.Result.Value)) : default,
        };
        LexicalDeclarationIds.Freshen(work);
        if (HasNodeNonClosure(work, "return") || HasNodeNonClosure(work, "returnExpr"))
            return InlineMaterialization.Refused("MC:return-in-body");
        if (function.Suspend) return MaterializeSuspendCarrier(carrier, function, work, prefixStatements);

        var identity = Pass.NextSpliceIdentity();
        var className = "dotkt$inlmat" + identity + "$Closure";
        var invoke = CarrierInvokeBody(work, function.Ret);
        if (HasUnmaterializableNested(invoke)) return InlineMaterialization.Refused("MC:unmaterializable-nested");
        var fields = new List<BirCaptureDescriptor>();
        var values = new List<IBirExpression>();
        var captureNames = new HashSet<string>(StringComparer.Ordinal);
        string outerName = null;
        foreach (var capture in carrier.Captures.OrEmpty())
        {
            if (!capture.Name.HasValue || !capture.Type.HasValue) return InlineMaterialization.Refused("MC:capture-no-name-type");
            var name = capture.Name.Value;
            fields.Add(new BirCaptureDescriptor { Name = name, Type = capture.Type });
            if (capture.Outer.OrDefault())
            {
                outerName = name;
                values.Add(new BirThis());
            }
            else
            {
                captureNames.Add(name);
                values.Add(Copy(capture.Value.OrDefault()) ?? new BirLocal { Name = name });
            }
        }
        if (outerName == null && HasThisOutsideSynthClass(invoke)) return InlineMaterialization.Refused("MC:this-outside-synthclass");
        var boxed = new HashSet<string>(StringComparer.Ordinal);
        CollectWrittenCaptures(invoke, captureNames, boxed);
        var declaredCells = new Dictionary<string, TypeNode>(StringComparer.Ordinal);
        var declaredCaptures = new Dictionary<string, BirInlineCapture>(StringComparer.Ordinal);
        foreach (var capture in carrier.Captures.OrEmpty())
            if (capture.Name.HasValue && captureNames.Contains(capture.Name.Value) && capture.SharedCellType.HasValue)
            {
                declaredCells.Add(capture.Name.Value, capture.SharedCellType.Value);
                declaredCaptures.Add(capture.Name.Value, capture);
                boxed.Add(capture.Name.Value);
            }
        foreach (var field in fields)
            if (boxed.Contains(field.Name.Value) && !declaredCells.ContainsKey(field.Name.Value) && CaptureTypeHasTv(field.Type.Value))
                return InlineMaterialization.Refused("MC:boxed-cap-has-tv");
        var plain = new HashSet<string>(captureNames.Where(name => !boxed.Contains(name)), StringComparer.Ordinal);
        invoke = (BirValueBlock)RewriteCapturesToFields(invoke, plain, outerName, className);
        if (boxed.Count != 0)
        {
            var cells = new Dictionary<string, TypeNode>(StringComparer.Ordinal);
            var elements = new Dictionary<string, TypeNode>(StringComparer.Ordinal);
            foreach (var field in fields)
            {
                var name = field.Name.Value;
                if (!boxed.Contains(name)) continue;
                var element = field.Type.Value;
                var supplied = declaredCells.TryGetValue(name, out var declaredCell);
                var cell = supplied ? declaredCell : new TypeNode.Fqn(RefCellNameFor(element));
                cells[name] = cell;
                elements[name] = element;
                field.Type = cell;
                RequestBox(supplied ? declaredCaptures[name] : null, name, cell, element, !supplied);
            }
            invoke = (BirValueBlock)RewriteBoxedCaptureInCarrier(invoke, boxed, cells, elements, className);
        }
        var allowed = InlineLambdaParamNames(carrier);
        CollectDeclaredLocals(invoke, allowed);
        if (HasStrayLocal(invoke, allowed)) return InlineMaterialization.Refused("MC:stray-local");

        var parameters = carrier.Params.OrEmpty().Select(value => (BirLambdaParameter)value.DeepClone()).ToArray();
        var returnType = function.Ret ?? new TypeNode.Fqn("kotlin.Unit");
        var keys = new SortedSet<(string scope, int i)>();
        CollectTvKeys(invoke, keys);
        foreach (var parameter in parameters) CollectTvKeys(parameter, keys);
        CollectTvKeys(returnType, keys); CollectTvKeys(function, keys);
        foreach (var field in fields) CollectTvKeys(field, keys);
        var constraints = CapturedConstraints(carrier, keys);
        var remap = new Dictionary<(string, int), int>();
        var typeArguments = new List<TypeNode>();
        var typeParameters = new List<BirTypeParameter>();
        foreach (var key in keys)
        {
            var index = remap.Count;
            remap[key] = index;
            typeArguments.Add(new TypeNode.Tv(key.scope, key.i));
            typeParameters.Add(CaptureTypeParameter("T" + index, key, constraints));
        }
        if (remap.Count != 0)
        {
            // Bind nested declaration frames before their construction-side
            // arguments move into the enclosing materialized class frame.
            invoke = (BirValueBlock)TypedExecutableFrames.Prebind(invoke);
            RenumberTvs(invoke, remap, true);
            foreach (var parameter in parameters) RenumberTvs(parameter, remap, true);
            returnType = RemapCallerType(returnType, remap, true);
            foreach (var field in fields) RenumberTvs(field, remap, true);
            foreach (var parameter in typeParameters) RenumberTvs(parameter, remap, true);
        }
        var synthetic = new BirClosureDefinition {
            Name = className, SemanticOwner = OptionalField<string>.Of(semanticOwner),
            Fields = fields.ToArray(), Params = parameters, Ret = returnType, Body = invoke.Stmts.OrEmpty(),
            _syntheticFrameBound = true,
        };
        if (typeParameters.Count != 0) synthetic.TypeParams = typeParameters.ToArray();
        var representation = CapturedRepresentationFrame(keys);
        if (representation.RequiresMetadata) synthetic.KotlinSupertypesPre = SourceTypeFacts.Empty.MergeNullableFrame(representation);
        var closure = new BirNewClosure {
            ClosureType = new TypeNode.Fqn(className), Captures = values.ToArray(), Method = "invoke",
            FuncType = function, SynthClass = synthetic,
        };
        if (typeArguments.Count != 0) closure.TypeArgs = typeArguments.ToArray();
        var temporary = "__inlmat" + identity;
        prefixStatements.Add(new BirVariable { Name = temporary, Type = function, Init = OptionalField<IBirExpression>.Of(closure) });
        return InlineMaterialization.Success(temporary);
    }

    static BirValueBlock CarrierInvokeBody(BirValueBlock work, TypeNode returnType)
    {
        var statements = work.Stmts.OrEmpty().ToList();
        var result = work.Result.OrDefault();
        FlattenCarrierResult(statements, ref result);
        if (returnType == null || IsUnit(returnType))
        {
            if (result is BirGraphNode expression && expression is not BirConstant)
                statements.Add(new BirExpressionStatement { Expr = OptionalField<IBirExpression>.Of((IBirExpression)expression) });
            statements.Add(new BirReturn());
        }
        else statements.Add(new BirReturn { Value = OptionalField<IBirExpression>.Of(result ?? UnitConst()) });
        return new BirValueBlock { Stmts = statements.ToArray() };
    }

    static BirGraphValue RewriteCapturesToFields(BirGraphValue node, HashSet<string> names, string outer, string owner)
    {
        BirFieldGet Read(string name) => new() { OwnerType = new TypeNode.Fqn(owner), Recv = OptionalField<IBirExpression>.Of(new BirThis()), Name = name };
        if (node is BirLocal local && local.Name.HasValue && names.Contains(local.Name.Value)) return Read(local.Name.Value);
        if (outer != null && node is BirThis) return Read(outer);
        WithoutSyntheticDeclarationSlots(node, slot => slot.Replace(RewriteCapturesToFields(slot.Value, names, outer, owner)));
        return node;
    }

    static bool CaptureTypeHasTv(TypeNode type)
    {
        var keys = new SortedSet<(string scope, int i)>(); CollectTvKeys(type, keys); return keys.Count != 0;
    }

    Dictionary<(string scope, int i), List<TypeNode>> CapturedConstraints(BirInlineLambda carrier, SortedSet<(string scope, int i)> keys)
    {
        var constraints = SharedCaptureConstraints(carrier);
        foreach (var bounds in constraints.Values) foreach (var bound in bounds) CollectTvKeys(bound, keys);
        var visited = new HashSet<(string scope, int i)>();
        while (keys.Any(key => !visited.Contains(key)))
            foreach (var key in keys.Where(key => !visited.Contains(key)).ToArray())
            {
                visited.Add(key);
                var parameters = key.scope == "type" ? Frame.OwnerParameters : Frame.MethodParameters;
                var representation = CaptureSourceFrame(key.scope, parameters);
                if (parameters != null && representation != null)
                {
                    var slot = representation.PhysicalSlot(key.i);
                    keys.Add((key.scope, representation.SourcePosition(slot.SourceIndex)));
                }
                if (parameters == null || key.i < 0 || key.i >= parameters.Length || !parameters[key.i].Constraints.HasValue) continue;
                if (!constraints.TryGetValue(key, out var retained)) constraints[key] = retained = new();
                foreach (var bound in parameters[key.i].Constraints.Value)
                {
                    if (!retained.Any(previous => TypeNodeDataEquality.Equal(previous, bound))) retained.Add(bound);
                    CollectTvKeys(bound, keys);
                }
            }
        return constraints;
    }

    NullableRepresentationFrame CaptureSourceFrame(string scope, BirTypeParameter[] parameters)
    {
        var frame = scope == "type" ? Frame.OwnerRepresentation : Frame.MethodRepresentation;
        return frame ?? (parameters != null && Pass.RepresentationFrames.TryGetValue(parameters, out var retained) ? retained : null);
    }

    NullableRepresentationFrame CapturedRepresentationFrame(SortedSet<(string scope, int i)> keys)
    {
        var sources = new Dictionary<(string Scope, int Index), int>();
        var slots = new List<NullableRepresentationFrame.Slot>();
        foreach (var key in keys)
        {
            var parameters = key.scope == "type" ? Frame.OwnerParameters : Frame.MethodParameters;
            var frame = CaptureSourceFrame(key.scope, parameters);
            var slot = parameters != null && frame != null ? frame.PhysicalSlot(key.i)
                : new NullableRepresentationFrame.Slot(key.i, NullableRepresentationFrame.Role.Ordinary);
            var source = (key.scope, slot.SourceIndex);
            if (!sources.TryGetValue(source, out var index)) sources[source] = index = sources.Count;
            slots.Add(new NullableRepresentationFrame.Slot(index, slot.Representation));
        }
        int[] Indices(NullableRepresentationFrame.Role role) => slots.Where(slot => slot.Representation == role)
            .Select(slot => slot.SourceIndex).Distinct().OrderBy(index => index).ToArray();
        var canonical = new NullableRepresentationFrame(sources.Count, Indices(NullableRepresentationFrame.Role.Nullable),
            storageIndices: Indices(NullableRepresentationFrame.Role.Storage), nullableStorageIndices: Indices(NullableRepresentationFrame.Role.NullableStorage));
        return new NullableRepresentationFrame(sources.Count, canonical.NullableIndices,
            slots.Select(slot => canonical.Variable(new TypeNode.Tv("type", slot.SourceIndex), slot.Representation).I),
            canonical.StorageIndices, canonical.NullableStorageIndices);
    }

    static Dictionary<(string scope, int i), List<TypeNode>> SharedCaptureConstraints(BirInlineLambda carrier)
    {
        var result = new Dictionary<(string scope, int i), List<TypeNode>>();
        foreach (var capture in carrier.Captures.OrEmpty())
        {
            if (capture.SharedCellType.OrDefault() is not TypeNode.Fqn { Args: not null } cell) continue;
            if (!capture.SharedCellTypeParams.HasValue || capture.SharedCellTypeParams.Value.Length != cell.Args.Length)
                throw new InvalidOperationException("shared capture is missing its cell's generic declarations");
            for (var index = 0; index < cell.Args.Length; index++)
            {
                var declaration = capture.SharedCellTypeParams.Value[index];
                if (cell.Args[index] is not TypeNode.Tv variable || !declaration.Constraints.HasValue) continue;
                var instantiated = (BirTypeParameter)TypedExecutableFrames.Substitute((BirTypeParameter)declaration.DeepClone(), Array.Empty<TypeNode>(), 0, cell.Args);
                var key = (variable.Scope, variable.I);
                if (!result.TryGetValue(key, out var retained)) result[key] = retained = new();
                foreach (var bound in instantiated.Constraints.OrEmpty())
                    if (!retained.Any(previous => TypeNodeDataEquality.Equal(previous, bound))) retained.Add(bound);
            }
        }
        return result;
    }

    static BirTypeParameter CaptureTypeParameter(string name, (string scope, int i) key,
        Dictionary<(string scope, int i), List<TypeNode>> constraints)
    {
        var parameter = new BirTypeParameter { Name = name };
        if (constraints.TryGetValue(key, out var bounds)) parameter.Constraints = bounds.ToArray();
        return parameter;
    }
}
