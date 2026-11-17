using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class SuspendColdLowering
{
    internal static string SuspendedCalleeInSource(BirGraphValue node)
    {
        if (node == null || node is IBirExecutablePayload || IsSourceOtherFrame(node) || IsSourceInlineLoop(node)) return null;
        // This query consumes order only; location/address flags are immaterial.
        if (SourceEvalOrderOf(node, _ => false) is { } order)
            foreach (var operand in order.Operands)
                if (SuspendedCalleeInSource(operand as BirGraphValue) is { } selected) return selected;
        if (TrySourceSuspension(node, out var callee)) return callee;
        string result = null;
        node.VisitSourceChildren((_, role, slot) => {
            if (result == null && !SourceOperandForeign(role)) result = SuspendedCalleeInSource(slot.Value);
        }, region => {
            foreach (var statement in region.Statements)
                if (result == null) result = SuspendedCalleeInSource((BirGraphValue)statement);
            if (result == null && region.Result is { } value) result = SuspendedCalleeInSource(value.Value);
        });
        return result;
    }

    internal sealed partial class SourceFunGen
    {
        static BirCast CallCast(TypeNode type, IBirExpression value) => new() {
            Type = type, E = SourceOperandField(value),
        };

        IBirExpression EmitSuspensionPoint(BirGraphValue callNode, List<IBirStatement> output)
        {
            var method = SourceOperandMethod(callNode);
            var resultType = TypedCallEvalLowering.StaticTypeOf(callNode)
                ?? (_calleeRet.TryGetValue(method ?? "", out var declared) ? declared : null) ?? AnyTn;
            if (IsUnitTn(resultType)) resultType = AnyTn;
            var state = ++_state;
            var resume = NextLabel();
            RegisterResume(state, resume);
            var field = "__aw$" + state;
            FieldStorage(field, resultType, RoleAwaited, lives: true, across: method);
            output.Add(SetField(StateLabelField, IntConst(state)));
            output.Add(new BirSetLocal { Name = ResumeResultParameter, Value = SourceOperandField(ColdCall(callNode, output)) });
            EmitSuspensionMerge(output, resume);
            output.Add(new BirExpressionStatement { Expr = ThrowOnFailure() });
            output.Add(SetField(field, IsAnyTn(resultType) ? Local(ResumeResultParameter)
                : CallCast(resultType, Local(ResumeResultParameter))));
            return FieldOf(field, resultType);
        }

        void EmitSuspensionMerge(List<IBirStatement> output, int resume)
        {
            output.Add(BrIf(new BirBinaryOperation { Op = "===", Lhs = Local(ResumeResultParameter), Rhs = Suspended() }, false, resume));
            if (_needSuspendGuard) output.Add(SetField(SuspendingField, BoolConst(true)));
            output.Add(Ret(Suspended()));
            output.Add(Label(resume));
        }

        void RegisterResume(int state, int resume)
        {
            if (_tryStack.Count == 0) _dispatch.Add((state, resume));
            else
            {
                var target = resume;
                foreach (var frame in _tryStack)
                {
                    frame.inner.Add((state, target));
                    target = frame.tryEntry;
                }
                _dispatch.Add((state, target));
            }
        }

        IBirExpression ColdCall(BirGraphValue source, List<IBirStatement> output)
        {
            if (IsSuspendValueCallInScope(source)) return SuspendValueColdCall((BirCallInstance)source, output);
            var method = SourceOperandMethod(source) + "$dotkt_suspend";
            var identity = source.DeclarationIdentityFact.OrDefault() is { } sourceIdentity ? sourceIdentity + "|cold" : null;
            var order = EvalOrderOf((IBirExpression)source, _isValueFqn).Value;
            var rewritten = order.Operands.Select(operand => operand == null ? null : Rewrite(operand, output)).ToArray();
            var receiver = order.HasReceiver ? rewritten[0] : null;
            var arguments = rewritten.Skip(order.ArgumentStart).Append(CallCast(ContAny(), new BirThis())).ToArray();
            var typeArguments = SourceCallTypeArguments(source);
            IBirExpression result;
            switch (source)
            {
                case BirNativeStatic call:
                    CheckReferencedColdTarget(call.Type.OrDefault(), call.Method.OrDefault());
                    result = new BirNativeStatic { Type = ExplicitCallType(call.Type), Method = method,
                        Args = arguments, Ret = AnyTn, ArgTypes = AppendCompletion(call.ArgTypes.OrEmpty()) };
                    break;
                case BirNativeInstance call:
                    CheckReferencedColdTarget(call.Type.OrDefault(), call.Method.OrDefault());
                    result = new BirNativeInstance { Type = ExplicitCallType(call.Type), Method = method,
                        Recv = SourceOperandField(receiver), Args = arguments, Ret = AnyTn,
                        ArgTypes = AppendCompletion(call.ArgTypes.OrEmpty()) };
                    break;
                case BirGenericStatic call:
                    CheckReferencedColdTarget(call.Type.OrDefault(), call.Method.OrDefault());
                    result = new BirGenericStatic { Type = ExplicitCallType(call.Type), Method = method,
                        Args = arguments, Ret = AnyTn, TypeArgs = typeArguments,
                        ResolvedMemberParams = AppendCompletion(call.ResolvedMemberParams.OrEmpty()) };
                    break;
                case BirGenericInstance call:
                    CheckReferencedColdTarget(call.Type.OrDefault(), call.Method.OrDefault());
                    result = new BirGenericInstance { Type = ExplicitCallType(call.Type), Method = method,
                        Recv = SourceOperandField(receiver), Args = arguments, Ret = AnyTn, TypeArgs = typeArguments,
                        ResolvedMemberParams = AppendCompletion(call.ResolvedMemberParams.OrEmpty()) };
                    break;
                case BirCallInstance call:
                    var instance = new BirCallInstance { OwnerType = ExplicitCallType(call.OwnerType), Virtual = call.Virtual.OrDefault(),
                        Recv = SourceOperandField(receiver), Method = method, Args = arguments, Ret = AnyTn, TypeArgs = typeArguments };
                    if (call.ClrOwnerResolved.OrDefault()) instance.ClrOwnerResolved = true;
                    if (call.Super.HasValue)
                    {
                        instance.Super = call.Super;
                        if (call.MemberMethodTypeParams.HasValue)
                            instance.MemberMethodTypeParams = call.MemberMethodTypeParams.Value.Select(parameter =>
                                (BirTypeParameter)_module.Transitions.CloneIndependent(parameter)).ToArray();
                    }
                    if (call._exactExistentialBridgeOwnerCall.OrDefault()) instance._exactExistentialBridgeOwnerCall = true;
                    if (identity != null) instance.DeclarationId = identity;
                    var instanceSignature = AppendCompletion(SelectedColdSignature(call.CalleeParams, call.Sig, call.ArgTypes, call.ShapeTypes));
                    instance.Sig = instanceSignature;
                    if (call.CalleeParams.HasValue) instance.CalleeParams = CopyCallTypes(instanceSignature);
                    result = instance;
                    break;
                case BirCallStatic call:
                    var plain = new BirCallStatic { Owner = ExplicitCallType(call.Owner), Method = method,
                        Args = arguments, Ret = AnyTn, TypeArgs = typeArguments };
                    if (call.CalleeOwner.HasValue) plain.CalleeOwner = call.CalleeOwner;
                    if (identity != null) plain.DeclarationId = identity;
                    _module.ProjectCallSignature(call, plain, ContAny());
                    var staticSignature = AppendCompletion(SelectedColdSignature(call.CalleeParams, call.Sig, call.ArgTypes, call.ShapeTypes));
                    plain.Sig = staticSignature;
                    if (call.CalleeParams.HasValue) plain.CalleeParams = CopyCallTypes(staticSignature);
                    result = plain;
                    break;
                default: throw new InvalidOperationException("A named cold call has no supported source call descriptor");
            }
            return result;
        }

        void CheckReferencedColdTarget(TypeNode type, string method)
        {
            if (type is TypeNode.Fqn owner && method != null && !_refs.HasSuspendMemberInHierarchy(owner.Name, method))
                throw new NotSupportedException($"bir2cir: suspend-lowering: the referenced owner '{owner.Name}' exposes a suspend call target "
                    + $"'{method}' without the cold-entry ABI ([KotlinFunction(Suspend)] absent on the ref.dll).");
        }

        static OptionalField<TypeNode> ExplicitCallType(OptionalField<TypeNode> type) => type.HasValue
            ? OptionalField<TypeNode>.Of(BirGraphValue.CopyType(type.Value)) : OptionalField<TypeNode>.Null;
        static TypeNode[] CopyCallTypes(IEnumerable<TypeNode> types) => types.Select(BirGraphValue.CopyType).ToArray();
        static TypeNode[] AppendCompletion(IEnumerable<TypeNode> parameters) => CopyCallTypes(parameters).Append(ContAny()).ToArray();
        static TypeNode[] SelectedColdSignature(OptionalField<TypeNode[]> selected, OptionalField<TypeNode[]> signature,
            OptionalField<TypeNode[]> arguments, OptionalField<TypeNode[]> shape) => selected.OrDefault()
                ?? signature.OrDefault() ?? arguments.OrDefault() ?? shape.OrEmpty();
        static OptionalField<TypeNode[]> SourceCallTypeArguments(BirGraphValue source) => source.ApplicationTypeArguments.HasValue
            ? OptionalField<TypeNode[]>.Of(CopyCallTypes(source.ApplicationTypeArguments.Value)) : default;

        bool IsSuspendValueCallInScope(BirGraphValue source) => source is BirCallInstance {
            SuspendCall.Value: true, Method.Value: "invoke", Recv.HasValue: true } call
            && (IsSuspendFunctionType(TypedCallEvalLowering.StaticTypeOf((BirGraphValue)call.Recv.Value))
                || call.Recv.Value is BirLocal { Name.HasValue: true } local && IsSuspendFunctionType(FieldType(local.Name.Value)));

        IBirExpression SuspendValueColdCall(BirCallInstance call, List<IBirStatement> output)
        {
            var order = EvalOrderOf(call, _isValueFqn).Value;
            var values = order.Operands.Select(operand => operand == null ? null : Rewrite(operand, output)).ToArray();
            var receiver = values[0];
            var invokeArguments = values.Skip(order.ArgumentStart).ToArray();
            var completion = CallCast(ContAny(), new BirThis());
            if (invokeArguments.Length >= 2)
                return BindAuthoredCall(new BirCallStatic {
                    Owner = new TypeNode.Fqn(StartSuspendOwner), Method = "startSuspendUninterceptedOrReturnN",
                    Args = new IBirExpression[] { receiver, new BirNewArray { Elem = AnyTn, Elems = invokeArguments }, completion }, Ret = AnyTn,
                }, 1, new TypeNode[] { new TypeNode.Nullable(AnyTn), new TypeNode.Array(new TypeNode.Nullable(AnyTn)),
                    new TypeNode.Fqn(ContinuationFqn, new TypeNode[] { new TypeNode.Tv("method", 0) }) });
            var parameters = new List<TypeNode> { new TypeNode.Nullable(AnyTn) };
            if (invokeArguments.Length == 1) parameters.Add(new TypeNode.Tv("method", 0));
            parameters.Add(new TypeNode.Fqn(ContinuationFqn, new TypeNode[] { new TypeNode.Tv("method", invokeArguments.Length) }));
            return BindAuthoredCall(new BirCallStatic {
                Owner = new TypeNode.Fqn(StartSuspendOwner), Method = "startSuspendUninterceptedOrReturn",
                Args = new[] { receiver }.Concat(invokeArguments).Append(completion).ToArray(), Ret = AnyTn,
            }, invokeArguments.Length + 1, parameters.ToArray());
        }
    }
}
