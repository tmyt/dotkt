using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class SuspendColdLowering
{
    internal sealed partial class SourceFunGen
    {
        const string RoleLocal = "local variable", RoleParam = "parameter", RoleCapture = "captured variable",
            RoleMachinery = "state-machine slot", RoleAwaited = "awaited value", RoleCondResult = "conditional-result temporary", RoleReturn = "result";

        internal SourceBuildResult Build()
        {
            var methods = new List<BirFunctionDeclaration>();
            var types = new List<IBirTypeDeclaration>();
            CheckSuspendAbi();
            if (_memberAbstract)
            {
                methods.Add(ColdEntryAbstract());
                if (WantsBridge) methods.Add(BuildBridge());
                return Finish();
            }
            if (_stubReason is { } reason)
            {
                if (_isLambda)
                {
                    Console.Error.WriteLine($"bir2cir: WARNING suspend-lowering: suspend lambda '{_smType}' is not segmentable "
                        + $"({reason}) — emitting a call-time-throw SuspendLambda state machine. The lambda COMPILES; invoking it throws NotSupportedException.");
                    foreach (var (name, type) in _captures) FieldStorage(name, type, RoleCapture, true, null);
                    foreach (var parameter in _params) FieldStorage(parameter.Name, parameter.Type, RoleParam, true, null);
                    var stub = new List<IBirStatement> { new BirExpressionStatement { Expr = ThrowOnFailure() } };
                    stub.AddRange(UnsupportedThrowBody($"{_smType}: {reason} — this suspend lambda is not supported by bir2cir's cold-lowering."));
                    types.Add(SmTypeLambda(stub.ToArray()));
                    return Finish();
                }
                Console.Error.WriteLine($"bir2cir: WARNING suspend-lowering: '{(_ownerClass ?? _fileClass)}.{_name}' is not segmentable "
                    + $"({reason}) — emitting a call-time-throw cold entry. The suspend fun COMPILES; invoking it throws NotSupportedException.");
                methods.Add(ColdEntryStub(reason));
                AddNamedWrappers();
                return Finish();
            }

            // The transition owns the one move (or registered independent birth
            // for a retained runtime original), before any body transformation.
            var body = _module.AcquireBody(_input);
            if (IsUnitTn(_resultType))
                for (var index = 0; index < body.Length; index++) body[index] = (IBirStatement)MaterializeUnitReturns((BirGraphValue)body[index]);
            var hasSuspension = body.Any(statement => HasSuspensionSource((BirGraphValue)statement));
            if (hasSuspension)
            {
                _label = MaxLabelId(body) + 1000;
                body = FlattenSuspendingLoops(body);
                TypedEscapingFinally.ApplyOwned(TypedProtectedRegions.Statements(body), IsUnitTn(_resultType) ? AnyTn : _resultType,
                    _refs, physicalMembers: false);
                _label = Math.Max(_label, MaxLabelId(body) + 1000);
                body = HoistSuspendingCatches(body);
            }
            _needSuspendGuard = hasSuspension && HasSuspendingFinally(body);
            if (!_isLambda && !hasSuspension)
            {
                methods.Add(ColdEntryDirect(body)); AddNamedWrappers(); return Finish();
            }
            if (!hasSuspension) _label = MaxLabelId(body) + 1000;
            var mutableBody = body.ToList();
            TypedPhysicalValueCoercion.AllocateNativeLocals(body.Cast<BirGraphValue>(),
                _params.Select(parameter => parameter.Declaration).ToArray(), mutableBody);
            body = mutableBody.ToArray();
            var live = SuspendLiveness.Analyze(body);
            FieldStorage(StateLabelField, IntTn, RoleMachinery, true, null);
            if (_needSuspendGuard) FieldStorage(SuspendingField, BoolTn, RoleMachinery, true, null);
            if (_isMember) FieldStorage(ThisField, _selfType, RoleMachinery, true, null);
            if (_isLambda) foreach (var (name, type) in _captures) FieldStorage(name, type, RoleCapture, true, null);
            foreach (var parameter in _params) FieldStorage(parameter.Name, RequiredParamType(parameter), RoleParam, true, null);
            foreach (var (name, type, role) in live.DeclaredVars)
            {
                if (name != null) _localTypes[name] = type;
                FieldStorage(name, type, role ?? RoleLocal, live.LivesAcrossSuspension(name), live.FirstSuspensionAcross(name), role != null);
            }
            var lowered = new List<IBirStatement>();
            foreach (var statement in body) EmitStmt(statement, lowered);
            if (IsUnitTn(_resultType)) lowered.Add(Ret(UnitValue()));
            var invoke = new List<IBirStatement>();
            if (_needSuspendGuard) invoke.Add(SetField(SuspendingField, BoolConst(false)));
            foreach (var (state, label) in _dispatch) invoke.Add(BrIf(BinEq(FieldOf(StateLabelField, IntTn), IntConst(state)), true, label));
            invoke.Add(new BirExpressionStatement { Expr = ThrowOnFailure() });
            invoke.AddRange(lowered);
            AssertLocalsResolved(invoke.ToArray());
            if (_isLambda) types.Add(SmTypeLambda(invoke.ToArray()));
            else { types.Add(SmType(invoke.ToArray())); methods.Add(ColdEntrySm()); AddNamedWrappers(); }
            return Finish();

            void AddNamedWrappers()
            {
                if (_name == "main" && _ownerClass == null) methods.Add(DrainMain());
                if (WantsBridge) methods.Add(BuildBridge());
            }
            SourceBuildResult Finish() => new(methods, types, NeedsRootContinuation, _methodProjections);
        }

        string DiagOwner => _isLambda ? _smType : (_ownerClass ?? _fileClass) + "." + _name;
        string PositionPrefix
        {
            get
            {
                var position = _m?.Pos.OrDefault();
                return FieldLegality.PosPrefix(position?.F.OrDefault(), position?.L.HasValue == true ? position.L.Value : null);
            }
        }
        bool IsByRefLikeFqn(TypeNode.Fqn type) => _refs?.IsByRefLikeFqn(type) == true;
        void FieldStorage(string name, TypeNode type, string role, bool lives, string across, bool roleNamesIt = false)
        {
            if (name == null || !lives) return;
            var why = FieldLegality.Classify(type, IsByRefLikeFqn, out var offending);
            if (why != FieldRejection.None)
                throw new NotSupportedException(FieldLegality.SuspendMessage(PositionPrefix, DiagOwner, role,
                    roleNamesIt ? null : name, type, offending, why, across));
            if (_fields.Add(name)) _fieldDecls.Add((name, type ?? throw new NotSupportedException(
                $"bir2cir: {PositionPrefix}suspend-lowering: in `{DiagOwner}`, the {role} `{name}` lives across a suspension but carries no static type, "
                + "so its state-machine field would be untyped — an earlier lowering dropped the type.")));
        }
        void CheckSuspendAbi()
        {
            void Check(string role, string name, TypeNode type)
            {
                var why = FieldLegality.Classify(type, IsByRefLikeFqn, out var offending);
                if (why != FieldRejection.None) throw new NotSupportedException(
                    FieldLegality.SuspendAbiMessage(PositionPrefix, DiagOwner, role, name, type, offending, why));
            }
            foreach (var parameter in _params) Check(RoleParam, parameter.Name, RequiredParamType(parameter));
            if (_captures != null) foreach (var (name, type) in _captures) Check(RoleCapture, name, type);
            Check(RoleReturn, "<result>", _resultType);
        }
        TypeNode VarType(BirVariable variable) => variable.Type.OrDefault() ?? throw new NotSupportedException(
            $"bir2cir: {PositionPrefix}suspend-lowering: in `{DiagOwner}`, the local `{variable.Name.OrDefault()}` is declared with no type, "
            + "so the slot that holds it across a suspension would be untyped — an earlier lowering dropped the type.");
        TypeNode RequiredParamType(SourceParameter parameter) => parameter.Type ?? throw new NotSupportedException(
            $"bir2cir: {PositionPrefix}suspend-lowering: in `{DiagOwner}`, parameter `{parameter.Name}` carries no static type — an earlier lowering dropped it.");
        BirVariable LocalVar(BirVariable variable, IBirExpression value)
        {
            variable.Type = BirGraphValue.CopyType(VarType(variable)); variable.Init = OptionalField<IBirExpression>.Of(value);
            variable.Role = default; variable.Outer = default; variable.JoinNullBranch = default; variable.SuspendFnTypePre = default;
            if (!variable._callEvalTemporary.OrDefault()) variable._callEvalTemporary = default;
            return variable;
        }

        void EmitStmt(IBirStatement statement, List<IBirStatement> output)
        {
            switch (statement)
            {
                case BirVariable variable:
                {
                    var type = VarType(variable);
                    var value = variable.Init.HasValue ? Rewrite(variable.Init.Value, output, type) : DefaultOf(type);
                    if (_fields.Contains(variable.Name.OrDefault()))
                    {
                        var store = SetField(variable.Name.Value, value);
                        if (variable._callEvalTemporary.OrDefault()) store._callEvalTemporary = true;
                        output.Add(store);
                    }
                    else output.Add(LocalVar(variable, value));
                    return;
                }
                case BirSetLocal store:
                {
                    var value = Rewrite(store.Value.OrDefault(), output, RequiredSlotType(store.Name.OrDefault()));
                    if (_fields.Contains(store.Name.OrDefault())) output.Add(SetField(store.Name.Value, value));
                    else { store.Value = OptionalField<IBirExpression>.Of(value); output.Add(store); }
                    return;
                }
                case BirReturn result:
                    result.Value = result.Value.HasValue ? OptionalField<IBirExpression>.Of(Rewrite(result.Value.Value, output,
                        IsUnitTn(_resultType) ? UnitTn : _resultType)) : UnitValue(); output.Add(result); return;
                case BirExpressionStatement expression:
                    expression.Expr = OptionalField<IBirExpression>.Of(Rewrite(expression.Expr.OrDefault(), output, UnitTn)); output.Add(expression); return;
                case BirBlock block: foreach (var child in block.Body.OrEmpty()) EmitStmt(child, output); return;
                case BirLabel or BirGoto: output.Add(statement); return;
                case BirBranchIf branch:
                    branch.Cond = OptionalField<IBirExpression>.Of(Rewrite(branch.Cond.OrDefault(), output, BoolTn)); output.Add(branch); return;
                case BirTry region: EmitTry(region, output); return;
                default: output.Add((IBirStatement)RewriteNode((BirGraphValue)statement, output)); return;
            }
        }

        void EmitTry(BirTry region, List<IBirStatement> output)
        {
            if (!HasOwnSuspensionSource(region.Body.OrEmpty())) { output.Add((BirTry)RewriteNoSpill(region)); return; }
            var entry = NextLabel(); output.Add(Label(entry));
            output.Add(new BirExpressionStatement { Expr = NullConst(AnyTn) });
            var inner = new List<(int state, int label)>(); _tryStack.Push((inner, entry));
            var body = new List<IBirStatement>();
            foreach (var statement in region.Body.OrEmpty()) EmitStmt(statement, body);
            _tryStack.Pop();
            var dispatch = inner.Select(route => (IBirStatement)BrIf(BinEq(FieldOf(StateLabelField, IntTn), IntConst(route.state)), true, route.label)).ToList();
            dispatch.AddRange(body); region.Body = dispatch.ToArray();
            foreach (var handler in region.Catches.OrEmpty())
            {
                var statements = new List<IBirStatement>();
                foreach (var statement in handler.Body.OrEmpty()) EmitStmt(statement, statements);
                handler.Body = statements.ToArray();
            }
            if (region.Finally.HasValue)
            {
                var statements = new List<IBirStatement>();
                foreach (var statement in region.Finally.Value) EmitStmt(statement, statements);
                if (_needSuspendGuard)
                {
                    var skip = NextLabel(); statements.Insert(0, BrIf(FieldOf(SuspendingField, BoolTn), true, skip)); statements.Add(Label(skip));
                }
                region.Finally = statements.ToArray();
            }
            region.Catches = region.Catches.OrEmpty(); region.SuspendFnTypePre = default;
            output.Add(region);
        }

        IBirExpression RewriteNoSpill(IBirExpression expression) => (IBirExpression)RewriteNoSpill((BirGraphValue)expression);
        BirGraphValue RewriteNoSpill(BirGraphValue node)
        {
            if (node == null || node is IBirExecutablePayload) return node;
            if (node is BirThis self && _stateMachineSelf.Contains(self)) return self;
            if (IsSuspendedRead(node)) return Suspended();
            if (node is BirLocal local && _fields.Contains(local.Name.OrDefault())) return FieldOf(local.Name.Value, RequiredFieldType(local.Name.Value));
            if (node is BirSetLocal store && _fields.Contains(store.Name.OrDefault())) return SetField(store.Name.Value, RewriteNoSpill(store.Value.OrDefault()));
            if (node is BirVariable variable)
            {
                var value = variable.Init.HasValue ? RewriteNoSpill(variable.Init.Value) : DefaultOf(VarType(variable));
                return _fields.Contains(variable.Name.OrDefault()) ? SetField(variable.Name.Value, value) : LocalVar(variable, value);
            }
            if (node is BirThis) return _isMember ? FieldOf(ThisField, _selfType) : (BirGraphValue)CapturedOuterField() ?? node;
            if (node is BirNewSuspendLambda lambda) return RewriteSuspendLambdaNew(lambda);
            if (IsContextRead(node)) return CoroutineContextValue(true);
            node.VisitChildSlots(slot => slot.Replace(RewriteNoSpill(slot.Value)));
            return node;
        }

        IBirExpression Rewrite(IBirExpression expression, List<IBirStatement> output, TypeNode expectedType = null) =>
            (IBirExpression)RewriteNode((BirGraphValue)expression, output, expectedType);
        BirGraphValue RewriteNode(BirGraphValue node, List<IBirStatement> output, TypeNode expectedType = null)
        {
            if (node == null || node is IBirExecutablePayload) return node;
            if (node is BirThis self && _stateMachineSelf.Contains(self)) return self;
            if (IsSuspendedRead(node)) return Suspended();
            if (node is BirLocal local && _fields.Contains(local.Name.OrDefault())) return FieldOf(local.Name.Value, RequiredFieldType(local.Name.Value));
            if (node is BirSetLocal store && _fields.Contains(store.Name.OrDefault()))
                return SetField(store.Name.Value, Rewrite(store.Value.OrDefault(), output, RequiredFieldType(store.Name.Value)));
            if (node is BirVariable variable)
            {
                var value = variable.Init.HasValue ? Rewrite(variable.Init.Value, output, VarType(variable)) : DefaultOf(VarType(variable));
                return _fields.Contains(variable.Name.OrDefault()) ? SetField(variable.Name.Value, value) : LocalVar(variable, value);
            }
            if (node is BirThis) return _isMember ? FieldOf(ThisField, _selfType) : (BirGraphValue)CapturedOuterField() ?? node;
            if (node is BirNewSuspendLambda lambda) return RewriteSuspendLambdaNew(lambda);
            if (IsContextRead(node)) return CoroutineContextValue(true);
            if (IsSuspendCoroutineCallSource(node)) return (BirGraphValue)EmitSuspendCoroutineCall(node, output);
            if (node is BirValueBlock block && (HasOwnSuspensionSource(block) || EscapesExpression(block)))
            {
                foreach (var statement in block.Stmts.OrEmpty()) EmitStmt(statement, output);
                return (BirGraphValue)(block.Result.HasValue ? Rewrite(block.Result.Value, output, expectedType) : MissingValuePlaceholder(expectedType));
            }
            if (IsAwaitMarkerSource(node)) return (BirGraphValue)EmitAwaitPoint(node, output);
            if (node is BirCallStatic or BirCallInstance or BirNativeStatic or BirNativeInstance or BirGenericStatic or BirGenericInstance
                && TrySourceSuspension(node, out _)) return (BirGraphValue)EmitSuspensionPoint(node, output);
            if (node is BirConditional conditional && (HasOwnSuspensionSource(conditional) || EscapesExpression(conditional)))
                return (BirGraphValue)EmitCondValue(conditional, output);
            var slots = new List<(string Field, BirGraphSlot Slot)>();
            node.VisitOwnChildSlots((field, _, slot) => slots.Add((field, slot)));
            foreach (var (_, slot) in slots.OrderBy(item => SuspendLiveness.OperandRank(item.Field)))
                slot.Replace(RewriteNode(slot.Value, output));
            return node;
        }

        IBirExpression EmitCondValue(BirConditional conditional, List<IBirStatement> output)
        {
            var type = FrameType((BirGraphValue)conditional) ?? throw new NotSupportedException(
                $"bir2cir: suspend-lowering: a conditional expression in `{(_ownerClass ?? _fileClass)}.{_name}` carries no result type of its own "
                + $"and neither branch yields one (`then` = {Kind((BirGraphValue)conditional.Then.OrDefault())}, `else` = {Kind((BirGraphValue)conditional.Else.OrDefault())}), "
                + "so the temporary that carries its value through the lowering would be untyped. An earlier lowering dropped a branch's type, or a branch's node kind needs an arm in bir-common/NodeType.cs.");
            var spans = HasOwnSuspensionSource(conditional); var name = "__cond$" + ++_condCounter;
            if (spans) FieldStorage(name, type, RoleCondResult, true, SuspendedCalleeInSource(conditional));
            else output.Add(new BirVariable { Name = name, Type = type, Init = DefaultOf(type) });
            var otherwise = NextLabel(); var end = NextLabel();
            output.Add(BrIf(Rewrite(conditional.Cond.OrDefault(), output, BoolTn), false, otherwise));
            EmitCondBranch(conditional.Then.OrDefault(), output, name, type); output.Add(Goto(end)); output.Add(Label(otherwise));
            EmitCondBranch(conditional.Else.OrDefault(), output, name, type); output.Add(Label(end));
            return spans ? FieldOf(name, type) : Local(name);
        }

        void EmitCondBranch(IBirExpression branch, List<IBirStatement> output, string name, TypeNode type)
        {
            IBirExpression value;
            if (branch is BirValueBlock block)
            {
                foreach (var statement in block.Stmts.OrEmpty()) EmitStmt(statement, output);
                value = block.Result.HasValue ? Rewrite(block.Result.Value, output, type) : NullConst(type);
            }
            else value = Rewrite(branch, output, type);
            switch (value)
            {
                case BirThrowExpression thrown: output.Add(new BirThrow { Value = thrown.Value }); return;
                case BirReturnExpression result: output.Add(new BirReturn { Value = result.Value.HasValue ? result.Value : UnitValue() }); return;
            }
            output.Add(_fields.Contains(name) ? SetField(name, value) : new BirSetLocal { Name = name, Value = OptionalField<IBirExpression>.Of(value) });
        }

        static bool EscapesExpression(BirGraphValue node)
        {
            if (node == null || node is IBirExecutablePayload || IsSourceOtherFrame(node) || IsSourceInlineLoop(node)) return false;
            if (node is BirReturn or BirReturnExpression or BirThrow or BirThrowExpression or BirGoto or BirBreak or BirContinue) return true;
            var found = false; node.VisitChildren(child => { if (!found) found = EscapesExpression(child); }); return found;
        }

        static bool IsSuspendedRead(BirGraphValue node) => node is BirCallStatic call
            && KotlinPropertyAccessors.IsCall(call, "COROUTINE_SUSPENDED", "get") && call.Args.OrEmpty().Length == 0;
        static bool IsContextRead(BirGraphValue node) => node is BirCallStatic call && IsCoroutineContextRead("callStatic",
            call.PropertyName.OrDefault(), call.PropertyAccessor.OrDefault(), call.Method.OrDefault(), call.Prop.OrDefault(),
            call.Args.HasValue ? call.Args.Value.Length : null);
        TypeNode FrameType(IBirExpression expression) => FrameType((BirGraphValue)expression);
        TypeNode FrameType(BirGraphValue node)
        {
            if (node is BirLocal { Name.HasValue: true } local && _localTypes.TryGetValue(local.Name.Value, out var type) && type != null) return type;
            return BirNodeType.Of(node as BirGraphNode, child => FrameType((BirGraphValue)child),
                name => BirTypeLowering.PrimArrayElem.TryGetValue(name, out var element) ? element : null);
        }
        static string Kind(BirGraphValue node) => (node as BirGraphNode)?.Tag ?? "<none>";
        TypeNode FieldType(string name)
        {
            foreach (var field in _fieldDecls) if (field.name == name) return field.type;
            return _localTypes.GetValueOrDefault(name ?? "");
        }
        TypeNode RequiredFieldType(string name) => FieldType(name) ?? throw new NotSupportedException(
            $"bir2cir: suspend-lowering: state-machine slot `{name}` in `{DiagOwner}` carries no static type.");
        TypeNode RequiredSlotType(string name) => FieldType(name) ?? throw new NotSupportedException(
            $"bir2cir: suspend-lowering: local slot `{name}` in `{DiagOwner}` carries no static type.");
        IBirExpression CapturedOuterField()
        {
            if (!_isLambda) return null;
            if (_capturedOuterName != null && _fields.Contains(_capturedOuterName)) return FieldOf(_capturedOuterName, RequiredFieldType(_capturedOuterName));
            throw new NotSupportedException($"bir2cir: suspend-lowering: lambda `{DiagOwner}` reads its enclosing receiver but carries no outer:true capture descriptor");
        }
        BirNewSuspendLambda RewriteSuspendLambdaNew(BirNewSuspendLambda lambda)
        {
            var values = lambda.CapValues.OrEmpty(); var captures = lambda.Captures.OrEmpty();
            var rewritten = new IBirExpression[captures.Length];
            for (var index = 0; index < captures.Length; index++)
                rewritten[index] = index < values.Length && values[index] is { } value ? RewriteNoSpill(value)
                    : CaptureValueInSm(captures[index].Name.OrDefault(), captures[index].Outer.OrDefault());
            lambda.CapValues = rewritten;
            return lambda;
        }
        IBirExpression CaptureValueInSm(string name, bool outer)
        {
            if (outer)
            {
                if (_isMember) return FieldOf(ThisField, _selfType);
                if (_extensionReceiverName != null && _fields.Contains(_extensionReceiverName)) return FieldOf(_extensionReceiverName, RequiredFieldType(_extensionReceiverName));
                if (_isLambda && _capturedOuterName != null && _fields.Contains(_capturedOuterName)) return FieldOf(_capturedOuterName, RequiredFieldType(_capturedOuterName));
                return new BirThis();
            }
            return _fields.Contains(name) ? FieldOf(name, RequiredFieldType(name)) : Local(name);
        }
    }
}
