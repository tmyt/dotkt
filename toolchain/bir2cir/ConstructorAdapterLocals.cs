using DotKt.Bir.Lowering;
using DotKt.Cir;

// Constructor parameters are free reads in a saved adapter, not names to replace
// inside a nested declaration's own region. These operations retain that lexical
// distinction through substitution and alpha-conversion on owning occurrences.
static class ConstructorAdapterLocals
{
    sealed record Region(IBirStatement[] Statements, string[] Parameters, string Binder = null,
        Action<string> RenameBinder = null, Func<IBirExpression> Result = null, Action<IBirExpression> SetResult = null,
        IBirExpression[] Results = null);

    static Region Body(BirGraphValue node) => node switch {
        BirConstructorAdapterPayload value => new(value.Statements.Value, Array.Empty<string>(), Results: value.Arguments.Value),
        BirBlock value => new(value.Body.OrEmpty(), Array.Empty<string>()),
        BirValueBlock value => new(value.Stmts.OrEmpty(), Array.Empty<string>(), Result: () => value.Result.OrDefault(),
            SetResult: result => value.Result = OptionalField<IBirExpression>.Of(result)),
        BirFunctionDeclaration value => new(value.Body.OrEmpty(), value.Params.OrEmpty().Select(parameter => parameter.Name.OrDefault()).ToArray()),
        BirConstructor value => new(value.Body.OrEmpty(), value.Params.OrEmpty().Select(parameter => parameter.Name.OrDefault()).ToArray()),
        BirClosureDefinition value => new(value.Body.OrEmpty(), value.Params.OrEmpty().Select(parameter => parameter.Name.OrDefault()).ToArray()),
        BirNewSuspendLambda value => new(value.Body.OrEmpty(), value.Params.OrEmpty().Select(parameter => parameter.Name.OrDefault()).ToArray()),
        BirInlineLambda value => new(value.Body.OrEmpty(), value.Params.OrEmpty().Select(parameter => parameter.Name.OrDefault()).ToArray(),
            Result: () => value.Result.OrDefault(), SetResult: result => value.Result = OptionalField<IBirExpression>.Of(result)),
        BirCatch value => new(value.Body.OrEmpty(), Array.Empty<string>(), value.Var.OrDefault(), name => value.Var = name),
        BirForRange value => new(value.Body.OrEmpty(), Array.Empty<string>(), value.Var.OrDefault(), name => value.Var = name),
        BirForArray value => new(value.Body.OrEmpty(), Array.Empty<string>(), value.Var.OrDefault(), name => value.Var = name),
        BirForIn value => new(value.Body.OrEmpty(), Array.Empty<string>(), value.Var.OrDefault(), name => value.Var = name),
        BirFor value => new(value.Body.OrEmpty(), Array.Empty<string>(), value.Var.OrDefault(), name => value.Var = name),
        BirForEach value => new(value.Body.OrEmpty(), Array.Empty<string>(), value.Var.OrDefault(), name => value.Var = name),
        BirRepeat value => new(value.Body.OrEmpty(), Array.Empty<string>(), value.Var.OrDefault(), name => value.Var = name),
        _ => null,
    };

    internal static BirGraphValue Substitute(BirGraphValue source, IReadOnlyDictionary<string, IBirExpression> replacements)
        => Rewrite(source.DeepClone(), replacements, null);

    internal static void Freshen(BirConstructorAdapterPayload root, Func<string, string> freshName)
        => Rewrite(root, new Dictionary<string, IBirExpression>(StringComparer.Ordinal), freshName);

    static BirGraphValue Rewrite(BirGraphValue node, IReadOnlyDictionary<string, IBirExpression> replacements,
        Func<string, string> freshName)
    {
        if (node is BirLocal read && read.Name.HasValue && replacements.TryGetValue(read.Name.Value, out var replacement))
            return ((BirGraphValue)replacement).DeepClone();
        if (node is BirSetLocal write && write.Name.HasValue
            && replacements.TryGetValue(write.Name.Value, out var target) && target is BirLocal local)
            write.Name = local.Name;
        var region = Body(node);
        var handled = new HashSet<BirGraphValue>(ReferenceEqualityComparer.Instance);
        if (region != null)
        {
            foreach (var statement in region.Statements) handled.Add((BirGraphValue)statement);
            if (region.Result?.Invoke() is { } result) handled.Add((BirGraphValue)result);
            foreach (var argument in region.Results ?? Array.Empty<IBirExpression>()) handled.Add((BirGraphValue)argument);
        }
        node.VisitChildSlots(slot => {
            if (!handled.Contains(slot.Value)) slot.Replace(Rewrite(slot.Value, replacements, freshName));
        });
        if (region == null) return node;

        var nested = new Dictionary<string, IBirExpression>(replacements, StringComparer.Ordinal);
        foreach (var parameter in region.Parameters) if (parameter != null) nested.Remove(parameter);
        if (region.Binder != null)
        {
            nested.Remove(region.Binder);
            if (freshName != null)
            {
                var fresh = freshName(region.Binder);
                region.RenameBinder(fresh);
                nested[region.Binder] = new BirLocal { Name = fresh };
            }
        }
        RewriteStatements(region.Statements, nested, freshName);
        if (region.Result?.Invoke() is { } value)
            region.SetResult((IBirExpression)Rewrite((BirGraphValue)value, nested, freshName));
        if (region.Results != null)
            for (var index = 0; index < region.Results.Length; index++)
                region.Results[index] = (IBirExpression)Rewrite((BirGraphValue)region.Results[index], nested, freshName);
        return node;
    }

    static void RewriteStatements(IBirStatement[] statements, Dictionary<string, IBirExpression> replacements,
        Func<string, string> freshName)
    {
        for (var index = 0; index < statements.Length; index++)
        {
            var statement = (BirGraphValue)statements[index];
            statements[index] = (IBirStatement)Rewrite(statement, replacements, freshName);
            if (statement is not BirVariable variable || !variable.Name.HasValue) continue;
            // The initializer reads the enclosing frame. The declaration shadows
            // that frame only for the statements/result that follow it.
            var old = variable.Name.Value;
            replacements.Remove(old);
            if (freshName != null)
            {
                var fresh = freshName(old);
                variable.Name = fresh;
                replacements[old] = new BirLocal { Name = fresh };
            }
        }
    }
}
