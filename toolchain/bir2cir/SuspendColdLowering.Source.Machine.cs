using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class SuspendColdLowering
{
    internal sealed partial class SourceFunGen
    {
        BirClassDefinition SmType(IBirStatement[] invokeBody) => BuildMachine(invokeBody, lambda: false);
        BirClassDefinition SmTypeLambda(IBirStatement[] invokeBody) => BuildMachine(invokeBody, lambda: true);

        BirClassDefinition BuildMachine(IBirStatement[] invokeBody, bool lambda)
        {
            var fields = _fieldDecls.Select(field => new BirFieldDeclaration {
                Name = field.name, Type = BirGraphValue.CopyType(field.type), Vis = "internal",
                Outer = IsExactOuterStorage(field.name) ? OptionalField<bool>.Of(true) : default,
            }).ToArray();
            var parameters = new List<BirParameter>();
            var assignments = new List<IBirStatement>();
            void Capture(string name, TypeNode type, bool outer)
            {
                var parameter = new BirParameter { Name = name, Type = BirGraphValue.CopyType(type) };
                var value = Local(name);
                if (outer) { parameter.Outer = true; value.Outer = true; }
                parameters.Add(parameter); assignments.Add(SetField(name, value));
            }
            if (lambda)
            {
                foreach (var (name, captureType) in _captures) Capture(name, captureType, IsExactOuterStorage(name));
            }
            else
            {
                if (_isMember) Capture(ThisField, _selfType, true);
                foreach (var parameter in _params) Capture(parameter.Name, parameter.Type, false);
            }
            parameters.Add(new BirParameter { Name = CompletionParameter, Type = ContAny() });
            var invoke = new BirFunctionDeclaration {
                Name = "invokeSuspend", Static = false, Override = _baseIsLocal, Virtual = false, Abstract = false,
                ObjectOverride = false, Vis = "public", Params = new[] { new BirParameter { Name = ResumeResultParameter, Type = AnyTn } },
                Ret = AnyTn, Body = invokeBody, Attrs = Array.Empty<BirAttribute>(),
            };
            if (!_baseIsLocal)
            {
                invoke.PendingOverrideOwner = new TypeNode.Fqn(BaseContinuationImplFqn);
                invoke.PendingOverrideReturn = AnyTn;
            }
            var methods = new List<BirFunctionDeclaration> { invoke };
            if (lambda) methods.AddRange(CreateMethods());
            methods.AddRange(_awaitResumeMethods);
            var baseArguments = new List<IBirExpression>();
            var baseSignature = new List<TypeNode>();
            if (lambda) { baseArguments.Add(IntConst(_arity)); baseSignature.Add(IntTn); }
            baseArguments.Add(Local(CompletionParameter)); baseSignature.Add(NullableContAny());
            var type = new BirClassDefinition {
                Name = _smType, Kind = "class", Generated = true, Abstract = false, Vis = "public",
                Base = new TypeNode.Fqn(lambda ? _restrictedBase ? RestrictedSuspendLambdaFqn : SuspendLambdaFqn : ContinuationImplFqn),
                Interfaces = Array.Empty<TypeNode>(), Fields = fields,
                Ctors = new[] { new BirConstructor { Params = parameters.ToArray(), BaseArgs = baseArguments.ToArray(),
                    DelegationSig = baseSignature.ToArray(), ThisArgs = OptionalField<IBirExpression[]>.Null,
                    Vis = "public", Body = assignments.ToArray() } },
                Methods = methods.ToArray(), Properties = Array.Empty<BirPropertyDeclaration>(), Attrs = Array.Empty<BirAttribute>(),
            };
            if (!lambda) type.SemanticOwner = _ownerClass ?? _fileClass;
            if (_smAllTps.Count != 0)
                type.TypeParams = _ownerTypeParamDecls.Concat(_methodTypeParamDecls)
                    .Select(parameter => (BirTypeParameter)parameter.DeepClone()).ToArray();
            if (_ownerTypeParams.Count != 0) type.OuterTypeParamCount = _ownerTypeParams.Count;
            RebindMethodTypeVariablesToSm(type, _ownerTypeParams.Count);
            if (type.TypeParams.HasValue) OwnerConstrainedMethodLowering.PreserveDispatchBounds(type, type.TypeParams.Value);
            return type;
        }

        IEnumerable<BirFunctionDeclaration> CreateMethods()
        {
            if (_arity == 0)
            {
                yield return CreateMethod(new[] { new BirParameter { Name = CompletionParameter, Type = ContStar() } },
                    new IBirStatement[] { Ret(NewSm()) });
                yield break;
            }
            var body = new List<IBirStatement> {
                new BirVariable { Name = StateMachineLocal, Type = _smTypeInst, Init = NewSm() },
            };
            var parameters = new List<BirParameter>();
            if (_arity >= 2)
            {
                parameters.Add(new BirParameter { Name = "args", Type = new TypeNode.Array(new TypeNode.Nullable(AnyTn)) });
                for (var index = 0; index < _params.Length; index++)
                    Store(_params[index], new BirArrayGet { Elem = AnyTn, Array = Local("args"), Index = IntConst(index) });
            }
            else
            {
                parameters.Add(new BirParameter { Name = "value", Type = AnyTn });
                Store(_params[0], Local("value"));
            }
            parameters.Add(new BirParameter { Name = CompletionParameter, Type = ContStar() });
            body.Add(Ret(Local(StateMachineLocal)));
            yield return CreateMethod(parameters.ToArray(), body.ToArray());

            void Store(SourceParameter parameter, IBirExpression value)
            {
                var parameterType = RequiredParamType(parameter);
                if (!IsAnyTn(parameterType)) value = new BirCast { Type = BirGraphValue.CopyType(parameterType), E = OptionalField<IBirExpression>.Of(value) };
                body.Add(new BirSetField { OwnerType = _smTypeInst, Recv = Local(StateMachineLocal), Name = parameter.Name,
                    Value = OptionalField<IBirExpression>.Of(value) });
            }
        }

        BirNew NewSm()
        {
            var arguments = new List<IBirExpression>();
            var types = new List<TypeNode>();
            foreach (var (name, type) in _captures)
            { arguments.Add(FieldOf(name, type)); types.Add(BirGraphValue.CopyType(type)); }
            arguments.Add(new BirCast { Type = ContAny(), E = Local(CompletionParameter) }); types.Add(ContAny());
            return new BirNew { Type = _smTypeInst, ArgTypes = types.ToArray(), Args = arguments.ToArray() };
        }

        BirFunctionDeclaration CreateMethod(BirParameter[] parameters, IBirStatement[] body)
        {
            var method = new BirFunctionDeclaration { Name = "create", Static = false, Override = _baseIsLocal,
                Virtual = false, Abstract = false, ObjectOverride = false, Vis = "public", Params = parameters,
                Ret = ContUnit(), Body = body, Attrs = Array.Empty<BirAttribute>() };
            if (!_baseIsLocal)
            {
                method.PendingOverrideOwner = new TypeNode.Fqn(BaseContinuationImplFqn);
                method.PendingOverrideReturn = ContUnit();
            }
            return method;
        }

        // This is the original lexical move, not generic callee substitution.
        // In particular a donor lambda declaration keeps its separate frame.
        static void RebindMethodTypeVariablesToSm(BirGraphValue node, int ownerArity)
        {
            if (node == null || node is IBirExecutablePayload) return;
            TypeNode Rebind(TypeNode type) => TypedExecutableFrames.RewriteType(type,
                variable => variable.Scope == "method" ? new TypeNode.Tv("type", ownerArity + variable.I) : null);
            if (node is BirNewSuspendLambda { _suspendDeclarationTypeArgs.HasValue: true } lambda)
            {
                if (lambda.TypeArgs.HasValue) lambda.TypeArgs = lambda.TypeArgs.Value.Select(Rebind).ToArray();
                foreach (var value in lambda.CapValues.OrEmpty()) if (value != null) RebindMethodTypeVariablesToSm((BirGraphValue)value, ownerArity);
                if (lambda.FuncType.HasValue) lambda.FuncType = Rebind(lambda.FuncType.Value);
                if (lambda.Sty.HasValue) lambda.Sty = Rebind(lambda.Sty.Value);
                return;
            }
            node.RewriteOwnTypeFieldsInPlace((field, _, type) => field is "sig" or "resolvedMemberParams"
                || field == "argTypes" && node is not BirNew ? type : Rebind(type));
            node.VisitOwnChildSlots((_, _, slot) => RebindMethodTypeVariablesToSm(slot.Value, ownerArity));
        }
    }
}
