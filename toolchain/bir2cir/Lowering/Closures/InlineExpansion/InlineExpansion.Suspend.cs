using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

sealed partial class InlineExpansion
{
    InlineMaterialization MaterializeSuspendCarrier(BirInlineLambda carrier, TypeNode.Fn function,
        BirValueBlock work, List<IBirStatement> prefixStatements)
    {
        var identity = Pass.NextSpliceIdentity();
        var invoke = CarrierInvokeBody(work, function.Ret);
        if (HasNonAppLocalDelegate(invoke)) return InlineMaterialization.Refused("MSC:non-applocal-delegate");
        var parameters = carrier.Params.OrEmpty();
        var captures = new List<BirCaptureDescriptor>();
        var values = new List<IBirExpression>();
        var anyValue = false;
        var innerScope = InlineLambdaParamNames(carrier);
        CollectDeclaredLocals(invoke, innerScope);
        var storageNames = new HashSet<string>(innerScope, StringComparer.Ordinal);
        string FreshStorage(string stem)
        {
            var candidate = stem;
            for (var suffix = 1; !storageNames.Add(candidate); suffix++) candidate = stem + "$" + suffix;
            return candidate;
        }
        var renames = new Dictionary<string, IBirExpression>(StringComparer.Ordinal);
        var ordinaryNames = new HashSet<string>(StringComparer.Ordinal);
        var outerCaptured = false;
        var sourceStorage = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var capture in carrier.Captures.OrEmpty())
        {
            if (!capture.Name.HasValue || !capture.Type.HasValue) return InlineMaterialization.Refused("MSC:capture-no-name-type");
            var name = capture.Name.Value;
            var type = capture.Type.Value;
            var outer = capture.Outer.OrDefault();
            if (outer)
            {
                if (outerCaptured) return InlineMaterialization.Refused("MSC:duplicate-outer-capture");
                outerCaptured = true;
            }
            else if (!ordinaryNames.Add(name)) return InlineMaterialization.Refused("MSC:duplicate-capture");
            var storage = outer ? FreshStorage("cap$__outer") : storageNames.Add(name) ? name : FreshStorage("cap$" + name);
            if (!outer) sourceStorage.Add(name, storage);
            var descriptor = new BirCaptureDescriptor { Name = storage, Type = type };
            if (!outer && capture.SharedCellType.HasValue)
            {
                descriptor.Type = capture.SharedCellType;
                invoke = (BirValueBlock)BoxVarInBody(invoke, name, capture.SharedCellType.Value, type);
                RequestBox(capture, storage, capture.SharedCellType.Value, type, false);
            }
            if (outer) descriptor.Outer = true;
            captures.Add(descriptor);
            if (!outer && storage != name) renames.Add(name, new BirLocal { Name = storage });
            if (capture.Value.HasValue)
            {
                values.Add(Copy(capture.Value.Value));
                anyValue = true;
            }
            else if (outer || storage != name)
            {
                values.Add(outer ? new BirThis() : new BirLocal { Name = name });
                anyValue = true;
            }
            else values.Add(null);
        }
        if (renames.Count != 0) invoke = (BirValueBlock)RewriteLocalRefs(invoke, renames);

        // Construction values are caller-frame edges, distinct from each nested
        // state machine's descriptor/body namespace. Promote only free defaults.
        foreach (var nested in CurrentFrameNestedSuspendLambdas(invoke))
        {
            if (!nested.Captures.HasValue) continue;
            var nestedCaptures = nested.Captures.Value;
            if (nested.CapValues.HasValue && nested.CapValues.Value.Length != nestedCaptures.Length)
                throw new NotSupportedException("inline splice: nested newSuspendLambda capValues must contain exactly one entry per capture");
            void SetValue(int index, string name)
            {
                if (!nested.CapValues.HasValue) nested.CapValues = new IBirExpression[nestedCaptures.Length];
                nested.CapValues.Value[index] = new BirLocal { Name = name };
            }
            for (var index = 0; index < nestedCaptures.Length; index++)
            {
                if (nested.CapValues.HasValue && nested.CapValues.Value[index] != null) continue;
                var capture = nestedCaptures[index];
                if (!capture.Name.HasValue || !capture.Type.HasValue) return InlineMaterialization.Refused("MSC:nested-capture-no-name-type");
                var name = capture.Name.Value;
                var type = capture.Type.Value;
                if (capture.Outer.OrDefault())
                {
                    var existingOuter = captures.FirstOrDefault(value => value.Outer.OrDefault());
                    if (existingOuter != null)
                    {
                        if (!TypeNodeDataEquality.Equal(existingOuter.Type.OrDefault(), type))
                            return InlineMaterialization.Refused("MSC:nested-outer-type-conflict");
                        SetValue(index, existingOuter.Name.Value);
                        continue;
                    }
                    var storage = FreshStorage("cap$__outer");
                    captures.Add(new BirCaptureDescriptor { Name = storage, Type = type, Outer = true });
                    values.Add(new BirThis());
                    anyValue = true;
                    SetValue(index, storage);
                    continue;
                }
                if (innerScope.Contains(name)) continue;
                var existingStorage = sourceStorage.TryGetValue(name, out var retained) ? retained : name;
                var existing = captures.FirstOrDefault(value => value.Name.OrDefault() == existingStorage);
                if (existing != null)
                {
                    if (!TypeNodeDataEquality.Equal(existing.Type.OrDefault(), type))
                        return InlineMaterialization.Refused("MSC:nested-capture-type-conflict");
                    if (existingStorage != name) SetValue(index, existingStorage);
                    continue;
                }
                var promoted = storageNames.Add(name) ? name : FreshStorage("cap$" + name);
                sourceStorage[name] = promoted;
                captures.Add(new BirCaptureDescriptor { Name = promoted, Type = type });
                if (promoted == name) values.Add(null);
                else
                {
                    values.Add(new BirLocal { Name = name });
                    anyValue = true;
                    SetValue(index, promoted);
                }
            }
        }
        var captureNames = captures.Where(value => value.Name.HasValue).Select(value => value.Name.Value).ToHashSet(StringComparer.Ordinal);
        var written = new HashSet<string>(StringComparer.Ordinal);
        CollectWrittenCaptures(invoke, captureNames, written);
        if (written.Count != 0) return InlineMaterialization.Refused("MSC:written-capture");

        var invokeParameters = parameters.Select(value => (BirLambdaParameter)value.DeepClone()).ToArray();
        var returnType = function.Ret ?? new TypeNode.Fqn("kotlin.Unit");
        var keys = new SortedSet<(string scope, int i)>();
        CollectTvKeys(invoke, keys);
        foreach (var parameter in invokeParameters) CollectTvKeys(parameter, keys);
        CollectTvKeys(returnType, keys);
        foreach (var capture in captures) CollectTvKeys(capture, keys);
        var constraints = CapturedConstraints(carrier, keys);
        var remap = new Dictionary<(string, int), int>();
        var typeArguments = new List<TypeNode>();
        var typeParameters = new List<BirTypeParameter>();
        foreach (var key in keys)
        {
            var index = remap.Count;
            remap[key] = index;
            typeArguments.Add(new TypeNode.Tv(key.scope, key.i));
            typeParameters.Add(CaptureTypeParameter("Tsm" + index, key, constraints));
        }
        foreach (var nested in FirstLevelNestedSuspendLambdas(invoke))
            if (nested.TypeParams.OrEmpty().Length != 0 && !nested.TypeArgs.HasValue)
                return InlineMaterialization.Refused("MSC:nested-sm-missing-typeargs");
        if (remap.Count != 0)
        {
            RenumberTvs(invoke, remap);
            foreach (var parameter in invokeParameters) RenumberTvs(parameter, remap);
            returnType = RemapCallerType(returnType, remap);
            foreach (var capture in captures) RenumberTvs(capture, remap);
            foreach (var parameter in typeParameters) RenumberTvs(parameter, remap);
        }
        var construction = new BirNewSuspendLambda {
            TypeFrame = "dense", Arity = parameters.Length, Captures = captures.ToArray(), Params = invokeParameters,
            SuspendRet = returnType, TypeParams = typeParameters.ToArray(), Body = invoke.Stmts.OrEmpty(), FuncType = function,
        };
        if (typeArguments.Count != 0) construction.TypeArgs = typeArguments.ToArray();
        if (anyValue) construction.CapValues = values.ToArray();
        var temporary = "__inlmat" + identity;
        prefixStatements.Add(new BirVariable { Name = temporary, Type = function, Init = OptionalField<IBirExpression>.Of(construction) });
        return InlineMaterialization.Success(temporary);
    }
}
