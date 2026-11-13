using System;
using System.Collections.Generic;
using System.Linq;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

sealed partial class InlineExpansion
{
    (BirInlinePayload Payload, bool SameModule, string Diagnostic) SelectPayload(string identity, string owner,
        string name, int parameters, int arity, TypeNode[] signature) => InlineSplice.SelectPayload(
            identity, owner, name, parameters, arity, signature, Pass.Bodies, Pass.References);

    BirGraphValue ForwardLambdaArgs(BirGraphValue node, Dictionary<string, BirInlineLambda> lambdas)
    {
        if (node == null) return null;
        if (node is BirCallInline inline && inline.Args.HasValue)
        {
            for (var index = 0; index < inline.Args.Value.Length; index++)
                if (inline.Args.Value[index] is BirLocal { Name.HasValue: true } local && lambdas.TryGetValue(local.Name.Value, out var lambda))
                    inline.Args.Value[index] = (BirInlineLambda)lambda.DeepClone();
        }
        else if (node is BirCallStatic or BirCallInstance)
        {
            var arguments = node is BirCallStatic staticCall ? staticCall.Args.OrDefault() : ((BirCallInstance)node).Args.OrDefault();
            if (arguments?.Any(argument => argument is BirLocal { Name.HasValue: true } local && lambdas.ContainsKey(local.Name.Value)) == true)
                node = TryForwardCall(node, lambdas);
        }
        node.VisitChildSlots(slot => slot.Replace(ForwardLambdaArgs(slot.Value, lambdas)));
        return node;
    }

    BirGraphValue TryForwardCall(BirGraphValue source, Dictionary<string, BirInlineLambda> lambdas)
    {
        var instance = source as BirCallInstance;
        var staticCall = source as BirCallStatic;
        var name = instance?.Method.OrDefault() ?? staticCall?.Method.OrDefault();
        var arguments = instance?.Args.OrDefault() ?? staticCall?.Args.OrDefault();
        if (name == null || arguments == null) return source;
        var signature = instance?.Sig.OrDefault() ?? staticCall?.Sig.OrDefault();
        var types = instance?.TypeArgs.OrDefault() ?? staticCall?.TypeArgs.OrDefault();
        var owner = instance == null ? (staticCall.Owner.OrDefault() as TypeNode.Fqn)?.Name : (instance.OwnerType.OrDefault() as TypeNode.Fqn)?.Name;
        if (instance != null && owner == null) return source;
        var selected = SelectPayload(null, owner, name, arguments.Length, types?.Length ?? 0, signature);
        if (selected.Payload == null) return source;
        var payload = selected.Payload;
        var ownerOut = instance != null ? payload.Owner.OrDefault() ?? owner : owner;
        var role = payload.Recv.OrDefault();
        var isStatic = payload.Static.OrDefault();
        var payloadBody = new BirValueBlock { Stmts = payload.Body.OrEmpty() };
        if (instance?.Recv.HasValue == true && role == "extensionParam" && isStatic && HasNode(payloadBody, "this")) return source;
        var receivers = new BirInlineReceivers();
        var start = 0;
        if (role == "extensionParam")
        {
            receivers.Extension = OptionalField<IBirExpression>.Of(Copy(arguments[0])); start = 1;
            if (!isStatic && instance?.Recv.HasValue == true) receivers.Dispatch = OptionalField<IBirExpression>.Of(Copy(instance.Recv.Value));
        }
        else if (role == "dispatch" && instance?.Recv.HasValue == true)
        {
            receivers.Dispatch = OptionalField<IBirExpression>.Of(Copy(instance.Recv.Value));
            var receiverType = instance.Recv.Value is BirCast cast ? cast.Type.OrDefault() as TypeNode.Fqn : null;
            receiverType ??= instance.OwnerType.OrDefault() as TypeNode.Fqn;
            if (receiverType != null && ResolveConstructedSuper(receiverType, ownerOut) is TypeNode.Fqn { Args.Length: > 0 } declaration)
            {
                receivers.DispatchTypeArgs = declaration.Args.Select(CopyType).ToArray();
                receivers.DispatchOwnerTypeArgs = declaration.Args.Select(CopyType).ToArray();
            }
        }
        var forwarded = new List<IBirExpression>();
        for (var index = start; index < arguments.Length; index++)
            forwarded.Add(arguments[index] is BirLocal { Name.HasValue: true } local && lambdas.TryGetValue(local.Name.Value, out var lambda)
                ? (BirInlineLambda)lambda.DeepClone() : Copy(arguments[index]));
        return new BirCallInline {
            Callee = new TypeNode.Fqn(name), Owner = OptionalField<TypeNode>.Of(ownerOut == null ? null : new TypeNode.Fqn(ownerOut)),
            Pc = arguments.Length, Ga = types?.Length ?? 0, ParamSig = (signature ?? Array.Empty<TypeNode>()).Select(CopyType).ToArray(),
            TypeArgs = (types ?? Array.Empty<TypeNode>()).Select(CopyType).ToArray(), Recvs = receivers, Args = forwarded.ToArray(),
            RetType = OptionalField<TypeNode>.Of(CopyType(instance?.Ret.OrDefault() ?? staticCall?.Ret.OrDefault())),
        };
    }

    TypeNode.Fqn ResolveConstructedSuper(TypeNode.Fqn start, string target)
    {
        var queue = new Queue<TypeNode.Fqn>(); queue.Enqueue(start);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (queue.TryDequeue(out var current))
        {
            if (!seen.Add(current.ToString())) continue;
            if (current.Name == target) return current;
            if (Pass.Dispatch == null || !Pass.Dispatch.TryGetValue(current.Name, out var shape)) continue;
            var arguments = shape.Arity == 0 ? Array.Empty<TypeNode>() : current.Args;
            if (arguments == null || arguments.Length != shape.Arity) continue;
            if (shape.Base != null) queue.Enqueue((TypeNode.Fqn)SupertypeGraph.SubstOwnerTvs(shape.Base, arguments));
            foreach (var face in shape.Interfaces) queue.Enqueue((TypeNode.Fqn)SupertypeGraph.SubstOwnerTvs(face, arguments));
        }
        return null;
    }

    internal static TypeNode CopyType(TypeNode type) => type == null ? null : BirGraphValue.CopyType(type);
    static TypeNode SubstituteInlineType(TypeNode type, TypeNode[] methodArguments, int arity, TypeNode[] ownerArguments) =>
        TypedExecutableFrames.SubstituteType(type, methodArguments, arity, ownerArguments, true);
    internal static IBirExpression Copy(IBirExpression value) => value == null ? null : (IBirExpression)((BirGraphValue)value).DeepClone();
    internal static T CopyGraph<T>(T value) where T : BirGraphValue => value == null ? null : (T)value.DeepClone();
    static readonly IReadOnlyDictionary<string, TypeNode> EmptyTypes = new Dictionary<string, TypeNode>(StringComparer.Ordinal);

    internal static bool IsUnit(TypeNode type) => type == null || type is TypeNode.Fqn { Name: "kotlin.Unit" or "void" };
    internal static BirConstant UnitConst() => new() { Type = new TypeNode.Fqn("kotlin.Unit"), Value = new BirLiteral.NullLiteral() };
    internal bool IsAppLocalDelegate(BirNewDelegate target) => target.Method.HasValue
        && (Pass.AppLocalMethods.Contains(target.Method.Value) || Pass.PendingHelpers.Any(method => method.Name.OrDefault() == target.Method.Value));
    internal static bool HasNode(BirGraphValue source, params string[] kinds)
    {
        if (source == null) return false;
        var found = false;
        source.Visit(node => { if (node is BirGraphNode expression && kinds.Contains(expression.Tag)) found = true; });
        return found;
    }
    internal static bool HasNodeNonClosure(BirGraphValue source, string kind)
    {
        if (source == null || source is BirNewClosure or BirNewDelegate or BirNewSam or BirNewSuspendLambda or BirLocalFunction) return false;
        if (source is BirGraphNode expression && expression.Tag == kind) return true;
        var found = false;
        source.VisitChildren(child => { if (!found && HasNodeNonClosure(child, kind)) found = true; });
        return found;
    }
    internal static bool HasLocalIn(BirGraphValue source, IEnumerable<string> names)
    {
        if (source == null) return false;
        var lookup = names as HashSet<string> ?? new HashSet<string>(names, StringComparer.Ordinal);
        var found = false;
        source.Visit(node => { if (node is BirLocal { Name.HasValue: true } local && lookup.Contains(local.Name.Value)) found = true; });
        return found;
    }
    internal static int MaxLabelId(BirGraphValue source)
    {
        var maximum = -1;
        source.Visit(node => {
            var id = node switch {
                BirLabel label => label.Id.OrDefault(-1), BirGoto branch => branch.Id.OrDefault(-1),
                BirBranchIf branch => branch.Id.OrDefault(-1), _ => -1,
            };
            maximum = Math.Max(maximum, id);
        });
        return maximum;
    }
    static void AssertNoUnsplicedInline(BirGraphValue root) => root.Visit(node => {
        if (node is BirCallInline call) throw new NotSupportedException(
            $"inline splice: a `callInline` (callee={(call.Callee.OrDefault() as TypeNode.Fqn)?.Name}) survived the pass un-spliced"
            + (call.Pc.IsPresent ? "" : " — it carries NO `pc`, so Rewrite silently skipped it")
            + "; ilemit cannot emit a callInline. Fix the kotc emission or the splice gate.");
    });
}
