using System;
using System.Collections.Generic;
using System.Linq;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

sealed partial class InlineExpansion
{
    internal void Apply()
    {
        if (Pass.RequireCompleteDefaults) DefaultArgSplice.AssertPlaceholdersPlanned(Pass.File, false);
        Expand(Pass.File, InlineFrame.Empty);
        while (Pass.PendingHelpers.Count != 0)
        {
            var helper = Pass.PendingHelpers[0];
            Pass.PendingHelpers.RemoveAt(0);
            Expand(helper, InlineFrame.Empty with { MethodParameters = helper.TypeParams.OrDefault() });
            if (helper.Name.HasValue) Pass.AppLocalMethods.Add(helper.Name.Value);
            if (!Pass.File.Methods.HasValue)
                throw new InvalidOperationException("bir2cir: InlineSplice re-hoisted a carried delegate target but the file root has no `methods` array");
            Pass.File.Methods = Pass.File.Methods.Value.Append(helper).ToArray();
        }
        FinishCaptureBoxing();
        NormalizeImplicitNullableUnwrap(Pass.File);
        WidenCovariantConstruction(Pass.File);
        RetypeReceiverToConcrete(Pass.File);
        AssertNoUnsplicedInline(Pass.File);
        if (Pass.RequireCompleteDefaults) DefaultArgSplice.AssertNoPlaceholder(Pass.File, null);
    }

    internal BirGraphValue Expand(BirGraphValue source, InlineFrame frame)
    {
        var current = source;
        Visit(new BirGraphSlot(() => current, replacement => current = replacement), frame);
        return current;
    }

    internal void Visit(BirGraphSlot occurrence, InlineFrame enclosing)
    {
        var node = occurrence.Value;
        if (node == null || node is IBirExecutablePayload) return;
        var previous = Frame;
        var frame = enclosing with { DiagnosticContext = DefaultArgSplice.DeclarationContext(node, enclosing.DiagnosticContext) };
        var parameters = Parameters(node);
        var representation = node is BirFunctionDeclaration method ? method.NullableRepresentationFrame.OrDefault()
            : NullableRepresentationViews.TypeFacts(node)?.NullableFrame;
        if (parameters != null && representation != null) Pass.RepresentationFrames[parameters] = representation;
        if (node is BirFunctionDeclaration or BirConstructor or BirClosureDefinition or BirFieldDeclaration)
            frame = frame with { BodyOwner = node };
        if (node is BirFunctionDeclaration currentMethod)
            frame = frame with { MethodParameters = currentMethod.TypeParams.OrDefault(),
                MethodRepresentation = currentMethod.NullableRepresentationFrame.OrDefault() };
        Frame = frame;
        try
        {
            node.VisitChildSlots(slot => {
                var childFrame = frame;
                if (node is IBirTypeDeclaration owner)
                    childFrame = frame with { OwnerParameters = owner.TypeParams.OrDefault(),
                        OwnerRepresentation = NullableRepresentationViews.TypeFacts((BirGraphValue)owner)?.NullableFrame,
                        MethodParameters = null, MethodRepresentation = null };
                else if (node is BirFileDeclaration)
                    childFrame = frame with { OwnerParameters = null, OwnerRepresentation = null,
                        MethodParameters = null, MethodRepresentation = null };
                if (slot.Value is BirFunctionDeclaration childMethod)
                    childFrame = childFrame with { MethodParameters = childMethod.TypeParams.OrDefault(),
                        MethodRepresentation = childMethod.NullableRepresentationFrame.OrDefault() };
                else if (slot.Value is BirConstructor)
                    childFrame = childFrame with { MethodParameters = null, MethodRepresentation = null };
                if (node is BirLocalFunction && slot.Value is BirFunctionDeclaration local)
                    childFrame = frame with { MethodParameters = local.TypeParams.OrDefault(),
                        MethodRepresentation = local.NullableRepresentationFrame.OrDefault() };
                if (node is BirNewClosure closure && ReferenceEquals(slot.Value, closure.SynthClass.OrDefault())
                    && ClosureSynthesis.HasPreboundFrame(slot.Value)
                    || node is BirNewSam sam && ReferenceEquals(slot.Value, sam.SynthClass.OrDefault())
                        && ClosureSynthesis.HasPreboundFrame(slot.Value))
                    childFrame = frame with { OwnerParameters = Parameters(slot.Value),
                        OwnerRepresentation = NullableRepresentationViews.TypeFacts(slot.Value)?.NullableFrame,
                        MethodParameters = null, MethodRepresentation = null };
                if (node is BirNewSuspendLambda { TypeFrame.HasValue: true } suspend && suspend.TypeFrame.Value == "dense"
                    && IsSuspendDeclarationChild(suspend, slot.Value))
                    childFrame = frame with { BodyOwner = suspend, OwnerParameters = null, OwnerRepresentation = null,
                        MethodParameters = suspend.TypeParams.OrDefault(), MethodRepresentation = null };
                Visit(slot, childFrame);
            });
            Frame = frame;
            if (node is BirCallInline { Pc.IsPresent: true } call)
            {
                var replacement = ExpandInline(call);
                occurrence.Replace((BirGraphValue)replacement);
                Visit(occurrence, frame with { Depth = frame.Depth + 1 });
            }
        }
        finally { Frame = previous; }
    }

    static BirTypeParameter[] Parameters(BirGraphValue node) => node switch {
        BirFunctionDeclaration method => method.TypeParams.OrDefault(),
        IBirTypeDeclaration type => type.TypeParams.OrDefault(),
        BirClosureDefinition closure => closure.TypeParams.OrDefault(),
        BirNewSuspendLambda suspend => suspend.TypeParams.OrDefault(), _ => null,
    };

    static bool IsSuspendDeclarationChild(BirNewSuspendLambda lambda, BirGraphValue child) =>
        lambda.Body.OrEmpty().Any(value => ReferenceEquals(value, child))
        || lambda.Params.OrEmpty().Any(value => ReferenceEquals(value, child))
        || lambda.Captures.OrEmpty().Any(value => ReferenceEquals(value, child))
        || lambda.TypeParams.OrEmpty().Any(value => ReferenceEquals(value, child));

    IBirExpression ExpandInline(BirCallInline call)
    {
        var owner = (call.Owner.OrDefault() as TypeNode.Fqn)?.Name;
        var callee = (call.Callee.OrDefault() as TypeNode.Fqn)?.Name;
        var name = callee?.Contains('.') == true ? callee[(callee.LastIndexOf('.') + 1)..] : callee;
        var pc = call.Pc.OrDefault(); var ga = call.Ga.OrDefault();
        void Fail(string reason) => FailInline(owner, name, pc, ga, reason);
        if (Frame.Depth > 32) Fail("inline splice depth > 32 (recursive-inline data corruption)");
        var selected = InlineSplice.ResolvePayload(call, Pass.Bodies, Pass.References);
        if (selected.Payload == null) Fail(selected.Diagnostic);
        var payload = MaterializedBirPayload.Normalize(selected.Payload);
        if (call.DeclarationId.HasValue || owner == null) owner = payload.Owner.OrDefault();
        if (payload.V.OrDefault() != 1) Fail("unsupported [KotlinInline] payload version");
        if (!payload.Body.HasValue) Fail("payload has no body");
        var parameters = payload.Params.OrEmpty();
        var typeArguments = call.TypeArgs.OrEmpty();
        if (typeArguments.Length < ga) Fail("fewer typeArgs than generic arity");
        var body = new BirValueBlock { Stmts = payload.Body.Value };
        if (!selected.SameModule)
        {
            if (RehoistPayloadDelegates(payload, body) is { } error) Fail(error);
        }
        else LexicalDeclarationIds.Freshen(body);
        var extension = payload.Recv.OrDefault() == "extensionParam";
        var dispatch = payload.Recv.OrDefault() == "dispatch";
        var isStatic = payload.Static.OrDefault();
        if (SuspendCaptureHazard(body, !(extension || dispatch)) is { } hazard)
            Fail($"payload newSuspendLambda capture '{hazard}' cannot be splice-rewritten: it captures an enclosing receiver or collides with the SM's own scope");
        var receivers = call.Recvs.OrDefault();
        var readsDispatch = HasPayloadFrameThis(body) || HasPayloadDispatchSuspendCapture(body);
        var coBind = extension && !isStatic && receivers?.Dispatch.HasValue == true && readsDispatch;
        if (extension && !coBind && readsDispatch)
            Fail(isStatic
                ? "a static extension inline payload retained an enclosing `this` after representation lowering — cannot co-bind"
                : "member-extension inline whose body reads the dispatch (enclosing-class) receiver but no recvs.dispatch was carried — cannot co-bind");
        var returnType = CopyType(payload.Ret.OrDefault());
        var clone = Pass.NextSpliceIdentity();
        var semanticOwner = call.SemanticOwner.OrDefault() ?? Pass.File.FileClass.OrDefault()
            ?? throw new InvalidOperationException("inline splice has no authored semantic use-site owner");
        body = (BirValueBlock)RenameSpliceSynthClasses(body, clone, semanticOwner);
        body = (BirValueBlock)TypedExecutableFrames.Prebind(body);
        var dispatchArguments = receivers?.DispatchTypeArgs.OrDefault();
        if (dispatchArguments?.Length == 0) dispatchArguments = null;
        body = (BirValueBlock)TypedExecutableFrames.Substitute(body, typeArguments, ga, dispatchArguments);
        returnType = SubstituteInlineType(returnType, typeArguments, ga, dispatchArguments);
        foreach (var parameter in parameters)
            if (parameter.Type.HasValue) parameter.Type = SubstituteInlineType(parameter.Type.Value, typeArguments, ga, dispatchArguments);
        PruneConcreteSynthClasses(body);
        body = (BirValueBlock)NormalizeConcretizedCasts(body);
        var prefix = "__inls" + Pass.NextSpliceIdentity() + "$";
        FreshenLabelsJoint(body);
        PrefixLocalsJoint(prefix, body);
        CallEvalLowering.FreshenPlanIds(body);
        var unit = IsUnit(returnType);
        var routed = RouteReturns(body.Stmts.Value, unit, returnType, prefix);
        body.Stmts = routed.Body;
        IBirExpression result = routed.Result;
        if (!unit && VtFqnOf(returnType) is { } foldedType && result != null)
            result = UnwrapNullableLocal(result, foldedType, CollectVarTypes(body)) ?? result;

        var statements = new List<IBirStatement>();
        var substitutions = new Dictionary<string, IBirExpression>(StringComparer.Ordinal);
        var lambdas = new Dictionary<string, BirInlineLambda>(StringComparer.Ordinal);
        var lambdaTypes = new Dictionary<string, TypeNode>(StringComparer.Ordinal);
        var arguments = call.Args.OrEmpty();
        var bound = new List<IBirExpression>();
        var pinned = new Dictionary<string, IBirExpression>(StringComparer.Ordinal);
        IBirExpression Bind(string parameter, IBirExpression value, TypeNode type, string description)
        {
            if (value is BirBindingReference) return Copy(value);
            var known = type ?? TypedCallEvalLowering.StaticTypeOf((BirGraphValue)value)
                ?? throw new NotSupportedException($"inline splice: cannot type the {description} of {owner}.{name} (a `{(value as BirGraphNode)?.Tag ?? "?"}` node), so the local that holds it would be untyped — the payload parameter carries no type and the node kind needs an arm in bir-common/NodeType.cs.");
            var local = prefix + parameter;
            statements.Add(new BirVariable { Name = local, Type = CopyType(known), Init = OptionalField<IBirExpression>.Of(Copy(value)) });
            return new BirLocal { Name = local, Sty = CopyType(known) };
        }
        IBirExpression Pin(string captured, IBirExpression value)
        {
            if (pinned.TryGetValue(captured, out var prior)) return Copy(prior);
            var type = ((BirGraphValue)value).ResultTypeStamp ?? TypedCallEvalLowering.StaticTypeOf((BirGraphValue)value)
                ?? throw new NotSupportedException($"inline splice: the payload of {owner}.{name} captures '{captured}', but the value bound to it carries no static type, so the local that pins it for the capture would be untyped.");
            var local = prefix + "cap$" + captured;
            statements.Add(new BirVariable { Name = local, Type = CopyType(type), Init = OptionalField<IBirExpression>.Of(Copy(value)) });
            var read = new BirLocal { Name = local, Sty = CopyType(type) };
            pinned[captured] = read;
            return Copy(read);
        }
        if (dispatch || coBind)
        {
            var receiver = receivers?.Dispatch.OrDefault();
            if (receiver == null) Fail("dispatch (member) inline but no recvs.dispatch carried");
            TypeNode thisType = dispatchArguments == null ? new TypeNode.Fqn(owner)
                : new TypeNode.Fqn(owner, receivers.DispatchOwnerTypeArgs.OrDefault()
                    ?? throw new InvalidOperationException("Inline dispatch has no owner application frame"));
            var receiverType = BirGraphStaticType.Surface(receiver, EmptyTypes);
            var core = receiverType;
            while (core is TypeNode.Nullable or TypeNode.Oblivious)
                core = core is TypeNode.Nullable nullable ? nullable.Of : ((TypeNode.Oblivious)core).Of;
            if (core is TypeNode.Tv) thisType = receiverType;
            statements.Add(new BirVariable { Name = prefix + "this", Type = CopyType(thisType), Init = OptionalField<IBirExpression>.Of(Copy(receiver)) });
            body = (BirValueBlock)RewriteThis(body, prefix + "this", thisType);
            result = (IBirExpression)RewriteThis((BirGraphValue)result, prefix + "this", thisType);
        }
        var defaultDispatch = dispatch || coBind ? new BirLocal { Name = prefix + "this" } : Copy(receivers?.Dispatch.OrDefault());
        for (var index = 0; index < parameters.Length; index++)
        {
            var parameter = parameters[index];
            var parameterName = parameter.Name.OrDefault();
            var type = parameter.Type.OrDefault();
            var argumentIndex = extension ? index - 1 : index;
            var argument = extension && index == 0 ? receivers?.Extension.OrDefault() : arguments.ElementAtOrDefault(argumentIndex);
            if (argument is BirInlineLambda lambda)
            {
                var local = prefix + parameterName;
                lambdas[local] = lambda;
                if (type != null) lambdaTypes[local] = type;
                substitutions[parameterName] = new BirLocal { Name = local };
                RecordBound(bound, index, new BirLocal { Name = local });
                continue;
            }
            var description = "param '" + parameterName + "'";
            if (argument == null)
            {
                if (extension && index == 0) Fail("extension receiver not carried");
                description = "default of parameter '" + parameterName + "'";
                var prepared = call.PreparedDefaults.OrDefault()?.ElementAtOrDefault(argumentIndex);
                if (prepared == null) Fail("missing prepared default for param " + parameterName);
                argument = DefaultArgSplice.InstantiatePreparedInlineDefault(prepared, semanticOwner, clone,
                    defaultDispatch, extension ? bound.ElementAtOrDefault(0) : null, bound);
                argument = (IBirExpression)SpliceLambdaInvokes((BirGraphValue)argument, lambdas);
                for (var earlier = 0; earlier < bound.Count; earlier++)
                {
                    if (bound[earlier] is not BirLocal { Name.HasValue: true } read
                        || !lambdas.TryGetValue(read.Name.Value, out var carrier)
                        || !HasLocalIn((BirGraphValue)argument, new[] { read.Name.Value })) continue;
                    var materialized = MaterializeCarrier(carrier, lambdaTypes.GetValueOrDefault(read.Name.Value), statements, semanticOwner);
                    if (!materialized.Succeeded) Fail($"lambda parameter used by a default could not be materialized [reason={materialized.RefusalReason}]");
                    var value = new BirLocal { Name = materialized.LocalName,
                        Sty = OptionalField<TypeNode>.Of(lambdaTypes.GetValueOrDefault(read.Name.Value)) };
                    RecordBound(bound, earlier, value);
                    substitutions[parameters[earlier].Name.OrDefault()] = value;
                    argument = (IBirExpression)RewriteLocalRefs((BirGraphValue)argument,
                        new Dictionary<string, IBirExpression>(StringComparer.Ordinal) { [read.Name.Value] = value });
                    lambdas.Remove(read.Name.Value);
                }
            }
            var binding = Bind(parameterName, argument, type, description);
            substitutions[parameterName] = binding;
            RecordBound(bound, index, binding);
        }
        foreach (var assigned in AssignedLocals(body, (BirGraphValue)result))
            if (substitutions.TryGetValue(assigned, out var value) && value is not BirLocal) substitutions[assigned] = Pin(assigned, value);
        body = (BirValueBlock)RewriteLocalRefs(body, substitutions, Pin);
        result = (IBirExpression)RewriteLocalRefs((BirGraphValue)result, substitutions, Pin);
        body = (BirValueBlock)SpliceLambdaInvokes(body, lambdas);
        result = (IBirExpression)SpliceLambdaInvokes((BirGraphValue)result, lambdas);
        body = (BirValueBlock)ForwardLambdaArgs(body, lambdas);
        result = (IBirExpression)ForwardLambdaArgs((BirGraphValue)result, lambdas);
        PruneConsumedCarrierCaptures(body, lambdas);
        PruneConsumedCarrierCaptures((BirGraphValue)result, lambdas);
        foreach (var (local, carrier) in lambdas.ToArray())
        {
            if (!HasLocalIn(body, new[] { local }) && !HasLocalIn((BirGraphValue)result, new[] { local })) continue;
            var materialized = MaterializeCarrier(carrier, lambdaTypes.GetValueOrDefault(local), statements, semanticOwner);
            if (!materialized.Succeeded)
                Fail($"lambda param '{local}' in a non-invoke position could not be materialized — non-local-return or unlisted-capture carrier [reason={materialized.RefusalReason}]");
            var rename = new Dictionary<string, IBirExpression>(StringComparer.Ordinal) { [local] = new BirLocal { Name = materialized.LocalName } };
            body = (BirValueBlock)RewriteLocalRefs(body, rename);
            result = (IBirExpression)RewriteLocalRefs((BirGraphValue)result, rename);
        }
        if (HasLocalIn(body, lambdas.Keys) || HasLocalIn((BirGraphValue)result, lambdas.Keys))
            Fail("lambda param aliased to a non-invoke position (not directly invoked) could not be materialized");
        if (lambdas.Keys.Any(local => HasCaptureDescIn(body, local) || HasCaptureDescIn((BirGraphValue)result, local)))
            Fail("lambda param survives only as a nested carrier's capture descriptor after its invoke sites were consumed and its captures rebound");
        statements.AddRange(body.Stmts.Value.Select(statement => (IBirStatement)((BirGraphValue)statement).DeepClone()));
        if (NodeType.IsNothing(returnType))
        {
            statements.Add(new BirExpressionStatement { Expr = OptionalField<IBirExpression>.Of(Copy(result)) });
            result = new BirThrowExpression { Value = new BirConstant { Type = new TypeNode.Nullable(new TypeNode.Fqn("kotlin.Nothing")), Value = new BirLiteral.NullLiteral() } };
        }
        return new BirValueBlock { Stmts = statements.ToArray(), Result = OptionalField<IBirExpression>.Of(result) };
    }

    static void RecordBound(List<IBirExpression> values, int index, IBirExpression value)
    {
        while (values.Count <= index) values.Add(null);
        values[index] = Copy(value);
    }

    static void FailInline(string owner, string name, int parameters, int arity, string reason) => throw new NotSupportedException(
        $"inline splice: cannot splice {owner}.{name} (pc={parameters} ga={arity}): {reason} — "
        + "under splice-all a callInline arrives for every inline+lambda call, so there is no fallback; fix the splice shape or the kotc gate.");
}
