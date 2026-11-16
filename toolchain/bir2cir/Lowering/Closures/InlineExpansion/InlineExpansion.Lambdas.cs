using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

sealed partial class InlineExpansion
{
    internal BirGraphValue SpliceLambdaInvokes(BirGraphValue node,
        Dictionary<string, BirInlineLambda> lambdas, BirInlineLambda host = null)
    {
        if (node == null || node is BirNewSuspendLambda) return node;
        if (node is BirInlineLambda current) host = current;
        node.VisitChildSlots(slot => slot.Replace(SpliceLambdaInvokes(slot.Value, lambdas, host)));
        if (node is BirCallInstance call && call.Method.OrDefault() == "invoke"
            && call.Recv.OrDefault() is BirLocal { Name.HasValue: true } local
            && lambdas.TryGetValue(local.Name.Value, out var lambda))
        {
            var renames = host == null ? null : PropagateSplicedCaptures(lambda, host);
            return BuildLambdaSplice(lambda, call.Args.OrEmpty(), renames, host == null);
        }
        return node;
    }

    Dictionary<string, IBirExpression> PropagateSplicedCaptures(BirInlineLambda lambda, BirInlineLambda host)
    {
        Dictionary<string, IBirExpression> renames = null;
        HashSet<string> hostScope = null;
        foreach (var capture in lambda.Captures.OrEmpty())
        {
            if (!capture.Name.HasValue || !capture.Type.HasValue) continue;
            var name = capture.Name.Value;
            var outer = capture.Outer.OrDefault();
            if (outer ? !(HasThisOutsideSynthClass(BodyGraph(lambda.Body))
                    || HasThisOutsideSynthClass((BirGraphValue)lambda.Result.OrDefault()))
                : !CarrierStillReferences(lambda, name)) continue;
            var existing = host.Captures.OrEmpty().FirstOrDefault(value =>
                outer ? value.Outer.OrDefault() : value.Name.OrDefault() == name);
            if (existing != null)
            {
                if (!outer && !TypeNodeDataEquality.Equal(existing.Type.OrDefault(), capture.Type.Value))
                    throw new NotSupportedException($"inline splice: spliced carrier capture '{name}' type-conflicts with the host carrier's same-named capture");
                if (capture.SharedCellType.HasValue)
                {
                    if (existing.SharedCellType.HasValue
                        && !TypeNodeDataEquality.Equal(existing.SharedCellType.Value, capture.SharedCellType.Value))
                        throw new InvalidOperationException($"inline capture '{name}' has conflicting shared locations");
                    existing.SharedCellType = capture.SharedCellType;
                    existing.SharedCellTypeParams = capture.SharedCellTypeParams.HasValue
                        ? capture.SharedCellTypeParams.Value.Select(value => (BirTypeParameter)value.DeepClone()).ToArray()
                        : capture.SharedCellTypeParams;
                }
                continue;
            }
            if (!outer)
            {
                if (hostScope == null)
                {
                    hostScope = InlineLambdaParamNames(host);
                    CollectDeclaredLocals(BodyGraph(host.Body), hostScope);
                }
                if (hostScope.Contains(name))
                {
                    if (HasSetLocalIn(BodyGraph(lambda.Body), name) || HasSetLocalIn((BirGraphValue)lambda.Result.OrDefault(), name))
                        throw new NotSupportedException($"inline splice: a spliced carrier capture '{name}' that is WRITTEN name-collides with a host binding — alpha-converting a mutable capture is unsound because the ref-cell box keys the enclosing scope by name");
                    var fresh = "__inlhyg" + Pass.NextSpliceIdentity() + "$" + name;
                    var forwarded = (BirInlineCapture)capture.DeepClone();
                    forwarded.Name = fresh;
                    forwarded.Value = OptionalField<IBirExpression>.Of(Copy(capture.Value.OrDefault()) ?? new BirLocal { Name = name });
                    host.Captures = host.Captures.OrEmpty().Append(forwarded).ToArray();
                    (renames ??= new(StringComparer.Ordinal))[name] = new BirLocal { Name = fresh };
                    continue;
                }
            }
            host.Captures = host.Captures.OrEmpty().Append((BirInlineCapture)capture.DeepClone()).ToArray();
        }
        return renames;
    }

    internal BirValueBlock BuildLambdaSplice(BirInlineLambda lambda, IReadOnlyList<IBirExpression> arguments,
        Dictionary<string, IBirExpression> hostRename = null, bool topLevelSplice = true)
    {
        var prefix = "__inll" + Pass.NextSpliceIdentity() + "$";
        var work = new BirValueBlock {
            Stmts = lambda.Body.OrEmpty().Select(value => (IBirStatement)((BirGraphValue)value).DeepClone()).ToArray(),
            Result = lambda.Result.HasValue ? OptionalField<IBirExpression>.Of(Copy(lambda.Result.Value)) : default,
        };
        LexicalDeclarationIds.Freshen(work);
        if (SuspendCaptureHazard(work, false) is string hazard)
            throw new NotSupportedException($"inline splice: a carrier newSuspendLambda capture '{hazard}' collides with the SM's own inner scope");
        FreshenLabelsJoint(work);
        PrefixLocalsJoint(prefix, work);
        var statements = new List<IBirStatement>();
        var substitutions = new Dictionary<string, IBirExpression>(StringComparer.Ordinal);
        var boundNames = new HashSet<string>(StringComparer.Ordinal);
        var parameters = lambda.Params.OrEmpty();
        for (var index = 0; index < parameters.Length; index++)
        {
            var parameter = parameters[index];
            var name = parameter.Name.OrDefault();
            var temporary = prefix + name;
            if (!boundNames.Add(temporary))
            {
                temporary += "$" + (index + 1);
                while (!boundNames.Add(temporary)) temporary += "$";
            }
            statements.Add(new BirVariable {
                Name = temporary, Type = parameter.Type, _callEvalTemporary = true,
                Init = OptionalField<IBirExpression>.Of(index < arguments.Count
                    ? Copy(arguments[index]) ?? UnitConst() : UnitConst()),
            });
            substitutions[name] = new BirLocal { Name = temporary, Sty = parameter.Type };
        }
        if (hostRename != null) foreach (var pair in hostRename) substitutions[pair.Key] = pair.Value;
        work = (BirValueBlock)RewriteLocalRefs(work, substitutions);
        if (topLevelSplice)
        {
            var values = lambda.Captures.OrEmpty().Where(value => value.Name.HasValue && value.Value.HasValue)
                .ToDictionary(value => value.Name.Value, value => value.Value.Value, StringComparer.Ordinal);
            if (values.Count != 0) work = (BirValueBlock)RewriteLocalRefs(work, values);
        }
        statements.AddRange(work.Stmts.OrEmpty());
        var result = work.Result.OrDefault();
        FlattenCarrierResult(statements, ref result);
        return new BirValueBlock { Stmts = statements.ToArray(), Result = OptionalField<IBirExpression>.Of(result ?? UnitConst()) };
    }

    // This temporary is detached and exists only to apply joint operations to a
    // statement vector, never to wrap or replace a continuing declaration body.
    static BirValueBlock BodyGraph(OptionalField<IBirStatement[]> body) => body.HasValue ? new BirValueBlock { Stmts = body } : null;

    static void FlattenCarrierResult(List<IBirStatement> statements, ref IBirExpression result)
    {
        while (result is BirValueBlock block)
        {
            statements.AddRange(block.Stmts.OrEmpty());
            result = block.Result.OrDefault();
        }
    }
}
