using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

sealed partial class InlineExpansion
{
    // A construction's value operands execute here; its synthetic declaration
    // does not. This is the capture-rewrite boundary, not the TV-frame policy.
    static void WithoutSyntheticDeclarationSlots(BirGraphValue node, Action<BirGraphSlot> visit)
    {
        if (node == null) return;
        BirGraphValue synthetic = node switch {
            BirNewClosure value => value.SynthClass.OrDefault(), BirNewSam value => value.SynthClass.OrDefault(), _ => null,
        };
        node.VisitChildSlots(slot => { if (!ReferenceEquals(slot.Value, synthetic)) visit(slot); });
    }

    internal static HashSet<string> InlineLambdaParamNames(BirInlineLambda lambda) =>
        lambda.Params.OrEmpty().Where(value => value.Name.HasValue).Select(value => value.Name.Value).ToHashSet(StringComparer.Ordinal);

    internal static void CollectDeclaredLocals(BirGraphValue node, HashSet<string> names)
    {
        if (node == null || node is BirInlineLambda or BirNewClosure or BirNewDelegate or BirNewSuspendLambda or BirNewSam) return;
        var name = node switch {
            BirVariable value => value.Name.OrDefault(), BirForArray value => value.Var.OrDefault(),
            BirForIn value => value.Var.OrDefault(), BirRepeat value => value.Var.OrDefault(),
            BirCatch value => value.Var.OrDefault(), _ => null,
        };
        if (name != null) names.Add(name);
        node.VisitChildren(child => CollectDeclaredLocals(child, names));
    }

    internal static HashSet<string> SuspendLambdaInnerScope(BirNewSuspendLambda lambda)
    {
        var names = lambda.Params.OrEmpty().Where(value => value.Name.HasValue)
            .Select(value => value.Name.Value).ToHashSet(StringComparer.Ordinal);
        CollectDeclaredLocals(BodyGraph(lambda.Body), names);
        var captures = lambda.Captures.OrEmpty();
        if (captures.Any(value => value.Outer.OrDefault()) && !captures.Any(value => value.Name.OrDefault() == "__self")) names.Add("__self");
        return names;
    }

    internal static string SuspendCaptureHazard(BirGraphValue node, bool refuseOuter)
    {
        if (node == null) return null;
        if (node is BirNewSuspendLambda lambda)
        {
            var inner = SuspendLambdaInnerScope(lambda);
            foreach (var capture in lambda.Captures.OrEmpty())
                if (capture.Name.HasValue && (refuseOuter && capture.Outer.OrDefault() || inner.Contains(capture.Name.Value)))
                    return capture.Name.Value;
        }
        string result = null;
        WithoutSyntheticDeclarationSlots(node, slot => result ??= SuspendCaptureHazard(slot.Value, refuseOuter));
        return result;
    }

    internal static bool HasThisOutsideSynthClass(BirGraphValue node)
    {
        if (node is BirThis) return true;
        var result = false;
        WithoutSyntheticDeclarationSlots(node, slot => result |= HasThisOutsideSynthClass(slot.Value));
        return result;
    }

    internal static bool HasPayloadFrameThis(BirGraphValue node)
    {
        if (node == null || node is BirNewSuspendLambda) return false;
        if (node is BirThis) return true;
        var result = false;
        WithoutSyntheticDeclarationSlots(node, slot => result |= HasPayloadFrameThis(slot.Value));
        return result;
    }

    internal static bool HasPayloadDispatchSuspendCapture(BirGraphValue node)
    {
        if (node is BirNewSuspendLambda lambda)
            return lambda.CapValues.OrEmpty().Any(value => HasPayloadFrameThis((BirGraphValue)value));
        var result = false;
        WithoutSyntheticDeclarationSlots(node, slot => result |= HasPayloadDispatchSuspendCapture(slot.Value));
        return result;
    }

    static bool HasStrayLocal(BirGraphValue node, HashSet<string> allowed)
    {
        if (node is BirLocal local) return local.Name.HasValue && !allowed.Contains(local.Name.Value);
        var result = false;
        WithoutSyntheticDeclarationSlots(node, slot => result |= HasStrayLocal(slot.Value, allowed));
        return result;
    }

    bool HasUnmaterializableNested(BirGraphValue node)
    {
        if (node is BirInlineLambda or BirNewSuspendLambda) return true;
        if (node is BirNewDelegate target && !IsAppLocalDelegate(target)) return true;
        var result = false;
        WithoutSyntheticDeclarationSlots(node, slot => result |= HasUnmaterializableNested(slot.Value));
        return result;
    }

    bool HasNonAppLocalDelegate(BirGraphValue node)
    {
        if (node is BirNewDelegate target && !IsAppLocalDelegate(target)) return true;
        var result = false;
        WithoutSyntheticDeclarationSlots(node, slot => result |= HasNonAppLocalDelegate(slot.Value));
        return result;
    }

    static void CollectWrittenCaptures(BirGraphValue node, HashSet<string> captures, HashSet<string> written)
    {
        if (node is BirSetLocal { Name.HasValue: true } write && captures.Contains(write.Name.Value)) written.Add(write.Name.Value);
        WithoutSyntheticDeclarationSlots(node, slot => CollectWrittenCaptures(slot.Value, captures, written));
    }

    static bool HasSetLocalIn(BirGraphValue node, string name)
    {
        if (node == null) return false;
        if (node is BirSetLocal local && local.Name.OrDefault() == name) return true;
        var result = false;
        node.VisitChildren(child => result |= HasSetLocalIn(child, name));
        return result;
    }

    internal static bool HasCaptureDescIn(BirGraphValue node, string name)
    {
        if (node == null) return false;
        if (node is BirInlineLambda lambda && lambda.Captures.OrEmpty().Any(value => value.Name.OrDefault() == name)
            || node is BirNewSuspendLambda suspend && suspend.Captures.OrEmpty().Any(value => value.Name.OrDefault() == name)) return true;
        var result = false;
        node.VisitChildren(child => result |= HasCaptureDescIn(child, name));
        return result;
    }

    static bool CarrierStillReferences(BirInlineLambda lambda, string name) =>
        HasLocalIn(BodyGraph(lambda.Body), new[] { name }) || HasLocalIn((BirGraphValue)lambda.Result.OrDefault(), new[] { name })
        || HasSetLocalIn(BodyGraph(lambda.Body), name) || HasSetLocalIn((BirGraphValue)lambda.Result.OrDefault(), name)
        || HasCaptureDescIn(BodyGraph(lambda.Body), name) || HasCaptureDescIn((BirGraphValue)lambda.Result.OrDefault(), name);

    internal static void PruneConsumedCarrierCaptures(BirGraphValue node, Dictionary<string, BirInlineLambda> lambdas)
    {
        if (node == null) return;
        WithoutSyntheticDeclarationSlots(node, slot => PruneConsumedCarrierCaptures(slot.Value, lambdas));
        if (node is not BirInlineLambda lambda || !lambda.Captures.HasValue) return;
        var parameters = InlineLambdaParamNames(lambda);
        lambda.Captures = lambda.Captures.Value.Where(capture => !capture.Name.HasValue
            || !lambdas.ContainsKey(capture.Name.Value) || parameters.Contains(capture.Name.Value)
            || capture.Outer.OrDefault() || CarrierStillReferences(lambda, capture.Name.Value)).ToArray();
    }

    static IEnumerable<BirNewSuspendLambda> FirstLevelNestedSuspendLambdas(BirGraphValue node)
    {
        if (node == null) yield break;
        if (node is BirNewSuspendLambda lambda) { yield return lambda; yield break; }
        var children = new List<BirGraphValue>();
        node.VisitChildren(children.Add);
        foreach (var child in children) foreach (var nested in FirstLevelNestedSuspendLambdas(child)) yield return nested;
    }

    static IEnumerable<BirNewSuspendLambda> CurrentFrameNestedSuspendLambdas(BirGraphValue node)
    {
        if (node == null) yield break;
        if (node is BirNewSuspendLambda lambda) { yield return lambda; yield break; }
        if (node is BirInlineLambda or BirNewClosure or BirNewSam or BirClosureDefinition) yield break;
        var children = new List<BirGraphValue>();
        node.VisitChildren(children.Add);
        foreach (var child in children) foreach (var nested in CurrentFrameNestedSuspendLambdas(child)) yield return nested;
    }
}
