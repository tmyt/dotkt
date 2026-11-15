using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class SuspendColdLowering
{
    internal sealed partial class SourceFunGen
    {
        static bool IsSuspendCoroutineCall(BirGraphValue node) => IsSuspendCoroutineCallSource(node);

        IBirExpression EmitSuspendCoroutineCall(BirGraphValue source, List<IBirStatement> output)
        {
            var call = (BirCallStatic)source;
            var method = call.Method.OrDefault();
            var wrapper = method == "suspendCoroutine";
            var argument = call.Args.OrEmpty().FirstOrDefault();
            var (body, parameter, captures, closure) = ResolveBlockLambda(argument);
            var blockType = TypedCallEvalLowering.StaticTypeOf(argument as BirGraphValue) as TypeNode.Fn
                ?? (argument as BirGraphValue)?.FunctionTypeStamp as TypeNode.Fn
                ?? call.Sig.OrDefault()?.FirstOrDefault() as TypeNode.Fn;
            if (body == null && (blockType == null || blockType.Suspend || blockType.DelegateParams.Length != 1))
                throw new InvalidOperationException($"malformed {method} block in '{(_ownerClass ?? _fileClass)}.{_name}': "
                    + "expected a non-suspend function argument with one delegate parameter");
            var result = TypedCallEvalLowering.StaticTypeOf(source) ?? AnyTn;
            var resultType = IsUnitTn(result) ? AnyTn : result;
            if (body == null)
            {
                var local = CallEvalLowering.FreshBindingId();
                output.Add(LocalVar(new BirVariable { Name = local, Type = blockType }, Rewrite(argument, output, blockType)));
                argument = Local(local);
            }
            var state = ++_state;
            var resume = NextLabel();
            RegisterResume(state, resume);
            var resultField = "__aw$" + state;
            FieldStorage(resultField, resultType, RoleAwaited, lives: true, across: method);
            IBirExpression tail;
            if (wrapper)
            {
                var safe = "__safe$" + state;
                FieldStorage(safe, ContAnyTn, RoleMachinery, lives: true, across: null);
                output.Add(SetField(safe, new BirCallStatic {
                    Owner = new TypeNode.Fqn(ThrowOnFailureOwner), Method = "newSafeContinuation",
                    Sig = new TypeNode[] { ContAny() }, Args = new IBirExpression[] { CallCast(ContAny(), new BirThis()) }, Ret = ContAny(),
                }));
                IBirExpression Continuation() => SmSelfField(safe, ContAnyTn);
                if (body != null)
                    foreach (var statement in body)
                        EmitStmt((IBirStatement)SubstBlock((BirGraphValue)statement, captures, parameter, Continuation, closure), output);
                else output.Add(new BirExpressionStatement { Expr = InvokeStoredBlock(argument, blockType, Continuation()) });
                tail = new BirCallStatic { Owner = new TypeNode.Fqn(ThrowOnFailureOwner), Method = "safeGetOrThrow",
                    Sig = new TypeNode[] { ContAny() }, Args = new IBirExpression[] { SmSelfField(safe, ContAnyTn) }, Ret = AnyTn };
            }
            else
            {
                // The unintercepted block can synchronously re-enter this SM.
                // Arm its label before executing the block, unlike SafeContinuation.
                output.Add(SetField(StateLabelField, IntConst(state)));
                if (body != null)
                {
                    tail = Suspended();
                    for (var index = 0; index < body.Length; index++)
                    {
                        if (index == body.Length - 1 && body[index] is BirReturn returned)
                        {
                            tail = returned.Value.OrDefault() ?? NullConst(AnyTn);
                            returned.Value = default;
                        }
                        else EmitStmt((IBirStatement)SubstBlock((BirGraphValue)body[index], captures, parameter, StateMachineSelf, closure), output);
                    }
                    tail = (IBirExpression)SubstBlock((BirGraphValue)tail, captures, parameter, StateMachineSelf, closure);
                }
                else tail = InvokeStoredBlock(argument, blockType, StateMachineSelf());
            }
            if (body != null)
            {
                // The constructed literal is consumed; copied body/capture
                // occurrences were reported at their independent birth above.
                _module.Transitions.RetireDiscardedSubtree(call.Args.Value[0] as BirGraphValue);
                call.Args.Value[0] = null;
            }
            if (wrapper) output.Add(SetField(StateLabelField, IntConst(state)));
            output.Add(new BirSetLocal { Name = ResumeResultParameter, Value = SourceOperandField(Rewrite(tail, output, AnyTn)) });
            EmitSuspensionMerge(output, resume);
            output.Add(new BirExpressionStatement { Expr = ThrowOnFailure() });
            output.Add(SetField(resultField, IsAnyTn(resultType) ? Local(ResumeResultParameter)
                : CallCast(resultType, Local(ResumeResultParameter))));
            return FieldOf(resultField, resultType);
        }

        static BirDelegateInvoke InvokeStoredBlock(IBirExpression block, TypeNode.Fn type, IBirExpression continuation) => new() {
            FuncType = type, Recv = SourceOperandField(block),
            Args = new IBirExpression[] { CallCast(type.DelegateParams[0], continuation) },
        };

        (IBirStatement[] Body, string Parameter, Dictionary<string, IBirExpression> Captures, string Closure)
            ResolveBlockLambda(IBirExpression argument)
        {
            var captures = new Dictionary<string, IBirExpression>(StringComparer.Ordinal);
            BirFunctionDeclaration invoke;
            IBirTypeDeclaration declaration = null;
            BirNewClosure construction = null;
            if (argument is BirNewClosure closure)
            {
                construction = closure;
                if (closure.ClosureType.OrDefault() is not TypeNode.Fqn type || !_closures.TryGetValue(type.Name, out declaration))
                    return (null, null, null, null);
                invoke = declaration.Methods.OrEmpty().FirstOrDefault(method => method.Name.OrDefault() == "invoke");
                if (invoke != null) _module.ConsumedIntrinsicClosures.Add(declaration);
                var fields = declaration.Fields.OrEmpty();
                var values = closure.Captures.OrEmpty();
                for (var index = 0; index < fields.Length && index < values.Length; index++)
                    if (fields[index].Name.HasValue) captures[fields[index].Name.Value] = values[index];
            }
            else if (argument is BirNewDelegate method && method.Method.HasValue
                && _lambdaMethods.TryGetValue(method.Method.Value, out invoke)) { }
            else return (null, null, null, null);
            if (invoke == null) return (null, null, null, null);
            // Each copied executable occurrence is born through the shared
            // declaration/proof observer before any in-place instantiation.
            var body = invoke.Body.OrEmpty().Select(statement =>
                (IBirStatement)_module.Transitions.CloneIndependent((BirGraphValue)statement)).ToArray();
            if (declaration != null) InstantiateClosureBody(body, declaration, construction);
            return (body, invoke.Params.OrEmpty().FirstOrDefault()?.Name.OrDefault(), captures, declaration?.Name.OrDefault());
        }

        static void InstantiateClosureBody(IBirStatement[] body, IBirTypeDeclaration declaration, BirNewClosure construction)
        {
            var arity = declaration.TypeParams.OrEmpty().Length;
            if (arity == 0) return;
            if (!construction.TypeArgs.HasValue || construction.TypeArgs.Value.Length != arity)
                throw new InvalidOperationException($"generic intrinsic closure '{declaration.Name.OrDefault()}' has {arity} type parameters "
                    + $"but its construction supplies {(construction.TypeArgs.HasValue ? construction.TypeArgs.Value.Length : 0)} arguments");
            foreach (var statement in body)
                TypedExecutableFrames.SubstituteInPlace((BirGraphValue)statement, Array.Empty<TypeNode>(), 0, construction.TypeArgs.Value);
        }

        BirGraphValue SubstBlock(BirGraphValue node, IReadOnlyDictionary<string, IBirExpression> captures,
            string parameter, Func<IBirExpression> continuation, string closure)
        {
            if (node == null || node is IBirExecutablePayload) return node;
            if (node is BirFieldGet field && closure != null
                && field.OwnerType.OrDefault() is TypeNode.Fqn owner && owner.Name == closure
                && field.Name.HasValue && captures.TryGetValue(field.Name.Value, out var value))
            {
                var replacement = _module.Transitions.CloneIndependent((BirGraphValue)value);
                _module.Transitions.RetireDiscardedSubtree(field);
                return replacement;
            }
            if (node is BirLocal local && parameter != null && local.Name.OrDefault() == parameter)
                return (BirGraphValue)continuation();
            if (node is BirFunctionDeclaration or BirConstructor or BirClosureDefinition or IBirTypeDeclaration) return node;
            if (IsSourceOtherFrame(node))
                node.VisitCurrentFrameSlots(slot => slot.Replace(SubstBlock(slot.Value, captures, parameter, continuation, closure)));
            else node.VisitOwnChildSlots((_, role, slot) => {
                if (!SourceOperandForeign(role) && role != BirGenericFrameRole.SyntheticDeclaration)
                    slot.Replace(SubstBlock(slot.Value, captures, parameter, continuation, closure));
            });
            return node;
        }

        BirFieldGet SmSelfField(string name, TypeNode type) => new() {
            OwnerType = _smTypeInst, Recv = StateMachineSelf(), Name = name, Ret = type,
        };
    }
}
