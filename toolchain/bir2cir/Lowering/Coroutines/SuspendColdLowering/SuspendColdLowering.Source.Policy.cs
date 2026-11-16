using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class SuspendColdLowering
{
    internal static bool IsSourceOtherFrame(BirGraphValue node) =>
        node is BirNewClosure or BirNewDelegate or BirInlineLambda or BirNewSuspendLambda;
    internal static bool IsSourceInlineLoop(BirGraphValue node) => node is BirForEach or BirRepeat;

    internal static bool TrySourceSuspension(BirGraphValue node, out string callee)
    {
        var selected = node switch {
            BirCallStatic call => (call.SuspendCall.OrDefault(), call.Method.OrDefault()),
            BirCallInstance call => (call.SuspendCall.OrDefault(), call.Method.OrDefault()),
            BirCallLocal call => (call.SuspendCall.OrDefault(), (string)null),
            BirConstrainedCall call => (call.SuspendCall.OrDefault(), call.Method.OrDefault()),
            BirGenericStatic call => (call.SuspendCall.OrDefault(), call.Method.OrDefault()),
            BirGenericInstance call => (call.SuspendCall.OrDefault(), call.Method.OrDefault()),
            BirNativeStatic call => (call.SuspendCall.OrDefault(), call.Method.OrDefault()),
            BirNativeInstance call => (call.SuspendCall.OrDefault(), call.Method.OrDefault()),
            _ => (false, (string)null),
        };
        callee = selected.Item1 ? selected.Item2 ?? "a suspending call" : null;
        return selected.Item1;
    }

    internal static bool HasOwnSuspensionSource(BirGraphValue node) => SourceSuspensionIn(node, false, false);
    internal static bool HasLoopBorneSuspensionSource(BirGraphValue node) => SourceSuspensionIn(node, true, false);
    internal static bool HasSuspensionSource(BirGraphValue node) => SourceSuspensionIn(node, true, true);
    internal static bool HasOwnSuspensionSource(IEnumerable<IBirStatement> statements) =>
        statements.Any(statement => HasOwnSuspensionSource((BirGraphValue)statement));
    internal static bool HasLoopBorneSuspensionSource(IEnumerable<IBirStatement> statements) =>
        statements.Any(statement => HasLoopBorneSuspensionSource((BirGraphValue)statement));
    internal static bool HasSuspensionSource(IEnumerable<IBirStatement> statements) =>
        statements.Any(statement => HasSuspensionSource((BirGraphValue)statement));

    static bool SourceSuspensionIn(BirGraphValue node, bool inlineForEach, bool otherFrames)
    {
        if (node == null || node is IBirExecutablePayload) return false;
        if (!otherFrames && (IsSourceOtherFrame(node) || IsSourceInlineLoop(node)
            && !(inlineForEach && node is BirForEach))) return false;
        if (TrySourceSuspension(node, out _)) return true;
        var found = false;
        node.VisitChildren(child => { if (!found) found = SourceSuspensionIn(child, inlineForEach, otherFrames); });
        return found;
    }

    internal static bool IsAwaitMarkerSource(BirGraphValue node) => node switch {
        BirCallStatic call => call.SuspendCall.OrDefault() && call.ClrAwaitBridge.OrDefault(),
        BirCallInstance call => call.SuspendCall.OrDefault() && call.ClrAwaitBridge.OrDefault(),
        _ => false,
    };

    internal static bool ShouldCheckSuspendBodySource(BirFunctionDeclaration method, bool topLevel, bool appBuild) =>
        method.Mods.OrDefault()?.Suspend.OrDefault() != true || IsSourceColdCandidate(method, topLevel, appBuild);

    static bool IsSourceColdCandidate(BirFunctionDeclaration method, bool topLevel, bool appBuild) =>
        method.Mods.OrDefault()?.Suspend.OrDefault() == true
        && (!topLevel || method.Static.OrDefault())
        && (appBuild || method.Mods.OrDefault()?.Inline.OrDefault() != true);

    internal static bool IsSuspendCoroutineCallSource(BirGraphValue node)
    {
        if (node is not BirCallStatic call || !call.SuspendCall.OrDefault()
            || !call.Method.HasValue || !SuspendCoroutineIntrinsicOwners.TryGetValue(call.Method.Value, out var expected))
            return false;
        if (call.Owner.OrDefault() is TypeNode.Fqn owner && owner.Name != expected) return false;
        if (call.CalleeOwner.OrDefault() is TypeNode.Fqn calleeOwner && calleeOwner.Name != expected) return false;
        return call.Args.HasValue && call.Args.Value.Length == 1 && call.Args.Value[0] != null;
    }

    static string SuspensionRefusalReasonSource(IEnumerable<IBirStatement> statements, bool inHandler, int tryDepth)
    {
        foreach (var statement in statements)
            if (SuspensionRefusalReasonSource((BirGraphValue)statement, inHandler, tryDepth) is { } reason) return reason;
        return null;
    }

    static string SuspensionRefusalReasonSource(BirGraphValue node, bool inHandler, int tryDepth)
    {
        if (node == null || node is IBirExecutablePayload) return null;
        if (IsSuspendCoroutineCallSource(node) || node is BirNewSuspendLambda) return null;
        if (node is BirNewClosure or BirNewDelegate && !HasSuspensionSource(node)) return null;
        if (node is BirForRange range && HasOwnSuspensionSource(range.Body.OrEmpty()))
            return "suspension buried in an unsupported 'forRange' loop position";
        if ((IsSourceOtherFrame(node) || IsSourceInlineLoop(node))
            && !(node is BirForEach loop && HasOwnSuspensionSource(loop.Body.OrEmpty())))
            return $"suspension buried in an unsupported '{((BirGraphNode)node).Tag}' lambda/closure position";
        if (node is BirTry attempt)
        {
            var bodySuspends = HasLoopBorneSuspensionSource(attempt.Body.OrEmpty());
            if (SuspensionRefusalReasonSource(attempt.Body.OrEmpty(), inHandler,
                bodySuspends ? tryDepth + 1 : tryDepth) is { } bodyReason) return bodyReason;
            foreach (var handler in attempt.Catches.OrEmpty())
                if (SuspensionRefusalReasonSource(handler.Body.OrEmpty(), inHandler, tryDepth) is { } handlerReason)
                    return handlerReason;
            return SuspensionRefusalReasonSource(attempt.Finally.OrEmpty(), inHandler, tryDepth);
        }
        string result = null;
        node.VisitChildren(child => result ??= SuspensionRefusalReasonSource(child, inHandler, tryDepth));
        return result;
    }
}
