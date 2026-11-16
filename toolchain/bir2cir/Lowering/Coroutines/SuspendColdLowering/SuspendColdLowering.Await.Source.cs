using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class SuspendColdLowering
{
    internal sealed partial class SourceFunGen
    {
        static bool LowersToStatements(BirGraphValue node) => SourceOperandLowering(node, false);
        static bool LowersToSuspension(BirGraphValue node) => SourceOperandLowering(node, true);
        static bool SourceOperandLowering(BirGraphValue node, bool suspensionOnly)
        {
            if (node == null || node is IBirExecutablePayload or BirNewSuspendLambda) return false;
            if (TrySourceSuspension(node, out _) || IsSuspendCoroutineCallSource(node)) return true;
            if (!suspensionOnly && node is BirGoto or BirBranchIf or BirReturn or BirReturnExpression or BirThrow or BirThrowExpression)
                return true;
            var found = false;
            node.VisitOwnChildSlots((_, role, slot) => {
                if (!found && role != BirGenericFrameRole.SyntheticDeclaration && !SourceOperandForeign(role))
                    found = SourceOperandLowering(slot.Value, suspensionOnly);
            });
            return found;
        }

        IBirExpression EmitAwaitPoint(BirGraphValue source, List<IBirStatement> output)
        {
            var member = source is BirCallInstance;
            var methodArguments = source.ApplicationTypeArguments.OrEmpty().Where(type => type != null).ToArray();
            var arguments = source switch {
                BirCallInstance call => call.Args.OrEmpty(), BirCallStatic call => call.Args.OrEmpty(),
                _ => throw new InvalidOperationException("An await marker has no source call descriptor"),
            };
            var captureIndex = member ? 0 : 1;
            var captureArgument = arguments.Length > captureIndex ? arguments[captureIndex] : null;
            var configured = captureArgument != null && captureArgument is not BirConstant { Value.Value: BirLiteral.BooleanLiteral { Value: true } };
            var receiverType = source is BirCallInstance instance ? instance.OwnerType.OrDefault() : ReceiverParamType((BirCallStatic)source);
            receiverType = SubstituteMethodTypeParameters(receiverType, methodArguments);
            var awaitableType = receiverType as TypeNode.Fqn;
            var plan = awaitableType == null ? null : _refs.ResolveAwaitable(awaitableType.Name, awaitableType.Args?.Length ?? 0);
            if (plan == null)
                throw new NotSupportedException($"await: '{awaitableType?.Name ?? "<unknown>"}' is not a resolvable .NET awaitable "
                    + "(no conforming GetAwaiter found in the referenced metadata)");
            if (configured && !plan.SupportsConfigureAwait)
                throw new NotSupportedException($"await(captureContext = …) is unsupported for '{awaitableType.Name}': {plan.ConfigureAwaitGap}");
            var declaredResult = source switch {
                BirCallInstance call => call.AwaitResult.OrDefault(), BirCallStatic call => call.AwaitResult.OrDefault(), _ => null,
            };
            var resultType = SubstituteTypeParameters(SubstituteMethodTypeParameters(declaredResult ?? UnitTn, methodArguments),
                awaitableType.Args ?? Array.Empty<TypeNode>());
            var hasResult = resultType is not TypeNode.Fqn { Name: "kotlin.Unit" };
            var awaiterType = CloseAwaitTemplate(configured ? plan.ConfiguredAwaiterTemplate : plan.AwaiterTemplate,
                awaitableType, methodArguments);
            var receiver = source is BirCallInstance callInstance ? callInstance.Recv.OrDefault() : arguments.FirstOrDefault();
            var awaitable = Rewrite(receiver, output, awaitableType);
            IBirExpression captureValue = null;
            if (configured)
            {
                // One query of the same operand decides both ordering and
                // whether its receiver must survive a resume rather than a local.
                if (LowersToStatements((BirGraphValue)captureArgument))
                {
                    var bound = "__awaitable$" + ++_awaitTarget;
                    if (LowersToSuspension((BirGraphValue)captureArgument))
                    {
                        FieldStorage(bound, awaitableType, RoleMachinery, lives: true, across: "await");
                        output.Add(SetField(bound, awaitable));
                        awaitable = FieldOf(bound, awaitableType);
                    }
                    else
                    {
                        output.Add(new BirVariable { Name = bound, Type = awaitableType, Init = SourceOperandField(awaitable) });
                        awaitable = new BirLocal { Name = bound, Sty = awaitableType };
                    }
                }
                captureValue = Rewrite(captureArgument, output, BoolTn);
            }
            var state = ++_state;
            var after = NextLabel();
            RegisterResume(state, after);
            var awaiterField = "__awaiter$" + state;
            FieldStorage(awaiterField, awaiterType, RoleMachinery, lives: true, across: "await");
            output.Add(SetField(awaiterField, BuildGetAwaiter(plan, awaitableType, awaiterType, awaitable,
                captureValue, resultType, methodArguments)));
            output.Add(BrIf(new BirPropertyGet { Type = awaiterType, Name = "IsCompleted", Static = false,
                Recv = FieldOf(awaiterField, awaiterType), Ret = BoolTn }, true, after));
            output.Add(SetField(StateLabelField, IntConst(state)));
            var callbackName = "$awaitOnDone$" + state;
            _awaitResumeMethods.Add(AwaitResumeMethod(callbackName));
            output.Add(new BirExpressionStatement { Expr = new BirNativeInstance {
                Type = awaiterType, Method = "OnCompleted", Recv = FieldOf(awaiterField, awaiterType),
                ArgTypes = new TypeNode[] { new TypeNode.Fqn(ActionFqn) }, Ret = VoidTn,
                Args = new IBirExpression[] { new BirNewBoundDelegate {
                    FuncType = new TypeNode.Fn(false, VoidTn, Array.Empty<TypeNode>()),
                    OwnerType = _smTypeInst, CalleeOwner = _smTypeInst, Method = callbackName, Virtual = false, Recv = new BirThis(),
                } },
            } });
            if (_needSuspendGuard) output.Add(SetField(SuspendingField, BoolConst(true)));
            output.Add(Ret(Suspended()));
            output.Add(Label(after));
            var result = new BirNativeInstance { Type = awaiterType, Method = "GetResult", Recv = FieldOf(awaiterField, awaiterType),
                ArgTypes = Array.Empty<TypeNode>(), Args = Array.Empty<IBirExpression>(), Ret = hasResult ? resultType : VoidTn };
            if (hasResult)
            {
                var valueField = "__awval$" + state;
                FieldStorage(valueField, resultType, RoleAwaited, lives: true, across: "await");
                output.Add(SetField(valueField, result));
                return FieldOf(valueField, resultType);
            }
            output.Add(new BirExpressionStatement { Expr = result });
            return NullConst(UnitTn);
        }

        static TypeNode ReceiverParamType(BirCallStatic source) => source.ShapeTypes.OrDefault()?.FirstOrDefault()
            ?? source.ArgTypes.OrDefault()?.FirstOrDefault() ?? source.Sig.OrDefault()?.FirstOrDefault();

        static TypeNode CloseAwaitTemplate(TypeNode template, TypeNode.Fqn awaitableType, TypeNode[] methodTypeArgs) =>
            SubstituteTypeParameters(SubstituteMethodTypeParameters(template, methodTypeArgs), awaitableType?.Args ?? Array.Empty<TypeNode>());
        static TypeNode SubstituteMethodTypeParameters(TypeNode type, TypeNode[] arguments) => SubstituteParameters(type, "method", arguments);
        static TypeNode SubstituteTypeParameters(TypeNode type, TypeNode[] arguments) => SubstituteParameters(type, "type", arguments);
        static TypeNode SubstituteParameters(TypeNode type, string scope, TypeNode[] arguments) =>
            TypedExecutableFrames.RewriteType(type, variable => variable.Scope == scope && variable.I >= 0 && variable.I < arguments.Length
                ? arguments[variable.I] : null);

        IBirExpression BuildGetAwaiter(AwaitPlan plan, TypeNode awaitableType, TypeNode awaiterType,
            IBirExpression task, IBirExpression captureValue, TypeNode resultTok, TypeNode[] methodTypeArgs)
        {
            if (captureValue != null)
            {
                var configuredType = CloseAwaitTemplate(plan.ConfiguredAwaitableTemplate, awaitableType as TypeNode.Fqn, methodTypeArgs);
                var configured = new BirNativeInstance { Type = awaitableType, Method = "ConfigureAwait", Recv = SourceOperandField(task),
                    ArgTypes = new TypeNode[] { BoolTn }, Args = new[] { captureValue }, Ret = configuredType };
                if (!plan.ConfiguredGetAwaiterExtension)
                    return new BirNativeInstance { Type = configuredType, Method = "GetAwaiter", Recv = configured,
                        ArgTypes = Array.Empty<TypeNode>(), Args = Array.Empty<IBirExpression>(), Ret = awaiterType };
                var owner = new TypeNode.Fqn(plan.ConfiguredGetAwaiterExtOwner);
                if (!plan.ConfiguredGetAwaiterExtGeneric)
                    return new BirNativeStatic { Type = owner, Method = "GetAwaiter", ArgTypes = new[] { configuredType },
                        Args = new IBirExpression[] { configured }, Ret = awaiterType };
                return new BirGenericStatic { Type = owner, Method = "GetAwaiter",
                    TypeArgs = plan.ConfiguredGetAwaiterExtTypeArgs.Select(type => CloseAwaitTemplate(type, awaitableType as TypeNode.Fqn, methodTypeArgs)).ToArray(),
                    ResolvedMemberParams = new[] { plan.ConfiguredGetAwaiterExtOpenRecv }, Args = new IBirExpression[] { configured }, Ret = awaiterType };
            }
            if (plan.GetAwaiterExtension)
            {
                var owner = new TypeNode.Fqn(plan.GetAwaiterExtOwner);
                if (plan.GetAwaiterExtGeneric)
                {
                    var open = awaitableType is TypeNode.Fqn named ? new TypeNode.Fqn(named.Name,
                        named.Args?.Select((_, index) => (TypeNode)new TypeNode.Tv("method", index)).ToArray()) : awaitableType;
                    return new BirGenericStatic { Type = owner, Method = "GetAwaiter", TypeArgs = CopyCallTypes(methodTypeArgs),
                        ResolvedMemberParams = new[] { open }, Args = new[] { task }, Ret = awaiterType };
                }
                return new BirNativeStatic { Type = owner, Method = "GetAwaiter", ArgTypes = new[] { awaitableType },
                    Args = new[] { task }, Ret = awaiterType };
            }
            return new BirNativeInstance { Type = awaitableType, Method = "GetAwaiter", Recv = SourceOperandField(task),
                ArgTypes = Array.Empty<TypeNode>(), Args = Array.Empty<IBirExpression>(), Ret = awaiterType };
        }

        BirFunctionDeclaration AwaitResumeMethod(string name)
        {
            var resume = new BirCallInstance { OwnerType = ContAny(), Virtual = true,
                Recv = new BirCallInstance { OwnerType = new TypeNode.Fqn(ContinuationImplFqn), Virtual = true, Recv = new BirThis(),
                    Method = "intercepted", Sig = Array.Empty<TypeNode>(), Ret = ContAny(), Args = Array.Empty<IBirExpression>() },
                Method = "resumeWith", Sig = new TypeNode[] { new TypeNode.Fqn("kotlin.Result", new TypeNode[] { AnyTn }) }, Ret = VoidTn,
                Args = new[] { CompanionFactoryCall("kotlin.Result", "success", new TypeNode[] { AnyTn },
                    new TypeNode[] { new TypeNode.Tv("method", 0) }, new IBirExpression[] { NullConst(AnyTn) },
                    new TypeNode.Fqn("kotlin.Result", new TypeNode[] { AnyTn })) },
            };
            return new BirFunctionDeclaration { Name = name, Static = false, Override = false, Virtual = false, Abstract = false,
                ObjectOverride = false, Vis = "public", Params = Array.Empty<BirParameter>(), Ret = VoidTn,
                Body = new IBirStatement[] { new BirExpressionStatement { Expr = resume } }, Attrs = Array.Empty<BirAttribute>() };
        }
    }
}
