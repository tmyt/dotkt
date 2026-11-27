using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class SuspendColdLowering
{
    internal sealed partial class SourceFunGen
    {
        internal TypeNode ProjectedRootResult { get; private set; }
        internal bool UsesGenericTaskRoot { get; private set; }

        BirFunctionDeclaration BuildBridge(TypeNode view = null, string viewId = null, string viewName = null)
        {
            var isUnit = view == null && IsUnitTn(_resultType) && !_resultNullable && !_m.DotktValueReturn.OrDefault();
            var result = view ?? (IsUnitTn(_resultType) ? UnitTn : _taskResultType);
            var resultSlot = BirTypeLowering.AsReadonlyResultSlot(result);
            var taskType = new TypeNode.Fqn(_taskBcl, new[] { resultSlot });
            var taskReturn = isUnit ? new TypeNode.Fqn(_taskBcl) : taskType;
            var method = new BirFunctionDeclaration {
                Name = _name, Static = _memberAbstract ? false : !_isMember,
                Override = _memberOverride, Virtual = _memberAbstract || _memberVirtual,
                Abstract = _memberAbstract, ObjectOverride = false,
                SuspendBridge = true, SuspendResult = BirGraphValue.CopyType(_logicalSuspendResult),
                Vis = _physicalSlotBridge ? _physicalSlotVisibility ?? "private" : "public",
                Params = CloneNamedParameters(), Ret = taskReturn, Body = Array.Empty<IBirStatement>(),
                Attrs = Array.Empty<BirAttribute>(),
            };
            if (!_memberAbstract || view != null)
            {
                var tcs = new TypeNode.Fqn(_tcsBcl, new[] { resultSlot });
                bool text = StringCharSequenceBridge.CanRestoreMaterializedValue(view == null ? _resultType : AnyTn, result);
                if (text) ProjectedRootResult = result is TypeNode.Nullable nullable ? nullable.Of : result;
                else UsesGenericTaskRoot = true;
                var root = text ? new TypeNode.Fqn(TextRootFqn)
                    : new TypeNode.Fqn(RootContinuationFqn, new[] { resultSlot });
                var body = BridgePrelude(tcs, root);
                body.Add(new BirVariable { Name = BridgeResultLocal, Type = AnyTn, Init = Suspended() });
                body.Add(new BirTry {
                    Type = VoidTn,
                    Body = new IBirStatement[] { new BirSetLocal { Name = BridgeResultLocal, Value = OptionalField<IBirExpression>.Of(BridgeColdCall(view != null)) } },
                    Catches = new[] { new BirCatch { ExcType = new TypeNode.Fqn("kotlin.Throwable"), Var = BridgeExceptionLocal,
                        Body = new IBirStatement[] {
                            new BirExpressionStatement { Expr = RootResumeFailure() },
                            new BirSetLocal { Name = BridgeResultLocal, Value = Suspended() },
                        } } },
                });
                var skip = NextLabel();
                body.Add(BrIf(new BirBinaryOperation { Op = "===", Lhs = Local(BridgeResultLocal), Rhs = Suspended() }, true, skip));
                IBirExpression value = ProjectColdResult(Local(BridgeResultLocal), resultSlot);
                body.Add(new BirExpressionStatement { Expr = TcsCall(tcs, "TrySetResult", resultSlot, value) });
                body.Add(Label(skip));
                IBirExpression task = new BirPropertyGet { Type = tcs, Name = "Task", Static = false,
                    Recv = Local(TaskCompletionSourceLocal), Ret = taskType };
                if (isUnit) task = new BirCast { Type = taskReturn, E = OptionalField<IBirExpression>.Of(task) };
                body.Add(Ret(task));
                method.Body = body.ToArray();
            }
            if (view == null) FinishBridgeDeclaration(method, taskReturn);
            else
            {
                method.ProjectedTaskView = (ProjectedCallableEntries.EntryId(_declarationId), viewId);
                method.Generated = true; method.Override = false; method.Abstract = false; method.Virtual = false; method.Vis = "private";
                method.TypeParams = _methodTypeParamDecls.Select(p => (BirTypeParameter)p.DeepClone()).ToArray();
                method.Name = viewName;
            }
            return method;
        }

        // Build the selected completion view while suspend lowering still owns root creation,
        // failure binding and result materialization. Only projected slot bodies consume it.
        IEnumerable<BirFunctionDeclaration> TaskViews()
        {
            if (!_isMember || _declarationId == null || _typeParams.Count == 0 || _physicalSlotBridge || _m.Overrides.OrEmpty().Length == 0) yield break;
            var names = ((SourceFunctionInput.Named)_input).Owner.Methods.OrEmpty().Select(m => m.Name.OrDefault()).ToHashSet(StringComparer.Ordinal);
            var edges = _m.Overrides.OrEmpty().Where(o => o.DeclarationId.HasValue && o.Owner.OrDefault() is TypeNode.Fqn)
                .Select(o => (Id: o.DeclarationId.Value, Owner: (TypeNode.Fqn)o.Owner.Value)).Distinct().ToArray();
            int ordinal = 0;
            foreach (var id in edges.Select(e => e.Id).Distinct(StringComparer.Ordinal))
            {
                var local = _module.TaskSlot(id);
                TypeNode result; string declaringOwner;
                if (local != null)
                {
                    declaringOwner = local.Owner.Name.Value;
                    var storage = ResultStorageTypeSource(local.Method.SuspendRet.OrDefault());
                    if (storage == null || IsUnitTn(storage) && !local.Method.DotktValueReturn.OrDefault()) continue;
                    result = local.Method.SuspendTaskResult.OrDefault() ?? storage;
                }
                else if (_refs.TryDeclarationSignatureSlot(id, out var slot) && slot.IsInterface &&
                    slot.Return is TypeNode.Fqn { Args: [var element] } task &&
                    ReferenceMetadataReader.StripGenericArity(task.Name) == TaskBclFqn)
                {
                    declaringOwner = slot.Owner; result = element;
                }
                else continue;
                bool usesOwner = false;
                BirGraphNameFields.VisitTypeNodes(result, t => usesOwner |= t is TypeNode.Tv { Scope: "type" });
                var views = usesOwner ? _module.TaskSlotOwners(_ownerClass, declaringOwner, id, local != null, edges)
                    .Select(s => SupertypeGraph.SubstOwnerTvs(result, s.Args ?? [])).Distinct() : new[] { result };
                foreach (var view in views.Where(t => t != _taskResultType))
                {
                    string name;
                    do name = _name + "$taskView" + ordinal++; while (!names.Add(name));
                    yield return BuildBridge(view, id, name);
                }
            }
        }

        List<IBirStatement> BridgePrelude(TypeNode tcs, TypeNode root) => new() {
            new BirVariable { Name = TaskCompletionSourceLocal, Type = tcs,
                Init = new BirNewClr { Type = tcs, ArgTypes = Array.Empty<TypeNode>(), Args = Array.Empty<IBirExpression>() } },
            new BirVariable { Name = RootContinuationLocal, Type = root,
                Init = new BirNew { Type = root, ArgTypes = new[] { tcs }, Args = new IBirExpression[] { Local(TaskCompletionSourceLocal) } } },
        };

        void FinishBridgeDeclaration(BirFunctionDeclaration method, TypeNode.Fqn taskReturn)
        {
            if (_typeParams.Count > 0) method.TypeParams = _methodTypeParamDecls.Select(parameter => (BirTypeParameter)parameter.DeepClone()).ToArray();
            if (_generated) method.Generated = true;
            CarrySourceDeclaration(method);
            CarryKotlinDeclarationMetadata(method);
            CarryOverrideMarkers(method);
            CarryPhysicalSlotFacts(method, false);
            if (TaskReturnNullableFlags(taskReturn) is { } flags) method.RetNullableFlags = flags;
            if (_resultNullableGeneric != null) method.NullableGenericRet = BirGraphValue.CopyType(_resultNullableGeneric);
            if (_companionReceiver.HasValue) method.CompanionReceiver = BirGraphValue.CopyType(_companionReceiver.Value);
            if (_companionSourceName.HasValue) method.CompanionSourceName = _companionSourceName;
            if (_companionMemberKind.HasValue) method.CompanionMemberKind = _companionMemberKind;
            if (_declarationId != null)
            {
                method.DeclarationId = _declarationId;
                method.DeclarationSourceName = _declarationSourceName
                    ?? throw new InvalidOperationException("suspend declaration identity has no frontend source name");
            }
            if (_explicitClrName != null) method.ExplicitClrName = _explicitClrName;
            if (IsNothingTn(_resultType)) method.RetNothing = true;
            _methodProjections.Add(new(_m, method));
        }

        int[] TaskReturnNullableFlags(TypeNode.Fqn taskReturn)
        {
            if (taskReturn.Args == null) return null;
            var result = taskReturn.Args[0];
            if (_resultNullable && result is not TypeNode.Nullable) result = new TypeNode.Nullable(result);
            return NullableFlags.ComputeValues(new TypeNode.Fqn(taskReturn.Name, new[] { result }),
                _isValueFqn, NullableFlags.Convention.ClrSignature,
                type => BirTypeLowering.AnnotationArguments(type, _refs.Aliases, _isValueFqn,
                    _refs.PhysicalTypeNames, _localTypeFqns, nullableFrames: _refs.NullableTypeFrames));
        }

        IBirExpression BridgeColdCall(bool taskView = false)
        {
            var args = _params.Select(parameter => (IBirExpression)Local(parameter.Name))
                .Append(new BirCast { Type = ContAny(), E = Local(RootContinuationLocal) }).ToArray();
            var signature = _params.Select(parameter => BirGraphValue.CopyType(parameter.Type)).Append(ContAny()).ToArray();
            var typeArguments = _typeParams.Count > 0
                ? OptionalField<TypeNode[]>.Of(Enumerable.Range(0, _typeParams.Count).Select(index => (TypeNode)new TypeNode.Tv("method", index)).ToArray())
                : default;
            if (_isMember)
                return new BirCallInstance {
                    TaskBridgeColdInvocation = taskView,
                    OwnerType = _selfType, Virtual = false, ClrOwnerResolved = true, Recv = new BirThis(),
                    Method = _coldName, Sig = signature, Args = args, Ret = AnyTn, TypeArgs = typeArguments,
                    SelectedGenericCallable = ForwarderEntry(true),
                    DeclarationId = _declarationId == null ? default : OptionalField<string>.Of(_declarationId + "|cold"),
                };
            return new BirCallStatic {
                OwnerType = _staticMember && _ownerClass != null ? OptionalField<TypeNode>.Of(_selfType) : OptionalField<TypeNode>.Null,
                CalleeOwner = _staticMember && _ownerClass != null ? _selfType : new TypeNode.Fqn(_fileClass),
                Method = _coldName, Sig = signature, Args = args, Ret = AnyTn, TypeArgs = typeArguments,
                DeclarationId = _declarationId == null ? default : OptionalField<string>.Of(_declarationId + "|cold"),
            };
        }

        BirCallInstance RootResumeFailure() => new() {
            OwnerType = ContAny(), Virtual = true,
            Recv = new BirCast { Type = ContAny(), E = Local(RootContinuationLocal) },
            Method = "resumeWith", Sig = new TypeNode[] { new TypeNode.Fqn("kotlin.Result", new[] { AnyTn }) }, Ret = VoidTn,
            Args = new IBirExpression[] { CompanionFactoryCall("kotlin.Result", "failure", new TypeNode[] { AnyTn },
                new TypeNode[] { new TypeNode.Fqn("kotlin.Throwable") }, new IBirExpression[] { Local(BridgeExceptionLocal) },
                new TypeNode.Fqn("kotlin.Result", new[] { AnyTn })) },
        };

        BirNativeInstance TcsCall(TypeNode owner, string method, TypeNode argumentType, IBirExpression argument) => new() {
            Type = owner, Method = method, Recv = Local(TaskCompletionSourceLocal),
            ArgTypes = new[] { argumentType }, Args = new[] { argument }, Ret = BoolTn,
        };

        BirFunctionDeclaration DrainMain()
        {
            var tcs = new TypeNode.Fqn(_tcsBcl, new[] { UnitTn });
            var task = new TypeNode.Fqn(_taskBcl, new[] { UnitTn });
            var root = new TypeNode.Fqn(RootContinuationFqn, new[] { UnitTn });
            var plan = _refs.ResolveAwaitable(_taskBcl, 1) ?? throw new InvalidOperationException(
                $"suspend main drain: '{_taskBcl}<Unit>' has no conforming GetAwaiter in referenced metadata");
            var awaiter = CloseAwaitTemplate(plan.AwaiterTemplate, task, Array.Empty<TypeNode>());
            var body = BridgePrelude(tcs, root);
            body.Add(new BirVariable { Name = BridgeResultLocal, Type = AnyTn, Init = OptionalField<IBirExpression>.Of(BridgeColdCall()) });
            var skip = NextLabel();
            body.Add(BrIf(new BirBinaryOperation { Op = "===", Lhs = Local(BridgeResultLocal), Rhs = Suspended() }, false, skip));
            var taskValue = new BirPropertyGet { Type = tcs, Name = "Task", Static = false,
                Recv = Local(TaskCompletionSourceLocal), Ret = task };
            var getAwaiter = BuildGetAwaiter(plan, task, awaiter, taskValue, captureValue: null,
                resultTok: UnitTn, methodTypeArgs: Array.Empty<TypeNode>());
            body.Add(new BirExpressionStatement { Expr = new BirNativeInstance { Type = awaiter, Method = "GetResult",
                Recv = OptionalField<IBirExpression>.Of(getAwaiter), ArgTypes = Array.Empty<TypeNode>(), Args = Array.Empty<IBirExpression>(), Ret = UnitTn } });
            body.Add(Label(skip));
            return new BirFunctionDeclaration { Name = _name, EntryPoint = true, Static = true, Override = false, Virtual = false,
                DeclarationId = _m.DeclarationId, DeclarationSourceName = _m.DeclarationSourceName, ExplicitClrName = _m.ExplicitClrName,
                Abstract = false, ObjectOverride = false, Vis = "public", Params = CloneNamedParameters(),
                Ret = UnitTn, Body = body.ToArray(),
                Attrs = EntryPointApartment.ForDrain(_m, _refs) };
        }
    }
}
