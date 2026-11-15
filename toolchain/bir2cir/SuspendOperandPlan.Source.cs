using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class SuspendColdLowering
{
    // A lexical local's declared type and the authored evaluation role carried
    // through a later materialized binding.
    internal readonly record struct LocalDecl(TypeNode Type, string Role);

    internal readonly record struct SourceEvalSlot(BirGraphSlot Slot, bool PreserveLocation = false)
    {
        internal IBirExpression Value => (IBirExpression)Slot.Value;
    }

    // The ordered operands and their actual owning slots are one description.
    // Generated traversal order is not an expression's evaluation-order policy.
    internal readonly record struct SourceEvalOrder(IReadOnlyList<SourceEvalSlot> Slots, bool HasReceiver)
    {
        internal int ArgumentStart => HasReceiver ? 1 : 0;
        internal IBirExpression[] Operands => Slots.Select(slot => slot.Value).ToArray();
    }

    static SourceEvalSlot SourceOperand(Func<IBirExpression> read, Action<IBirExpression> write,
        bool location = false) => new(new BirGraphSlot(() => (BirGraphValue)read(),
            value => write((IBirExpression)value)), location);

    static OptionalField<IBirExpression> SourceOperandField(IBirExpression value) => value == null
        ? OptionalField<IBirExpression>.Null : OptionalField<IBirExpression>.Of(value);

    static SourceEvalOrder SourceOperands(bool receiver, params SourceEvalSlot[] slots) => new(slots, receiver);

    static SourceEvalOrder SourceOperandList(IBirExpression[] values, bool receiver = false,
        SourceEvalSlot? receiverSlot = null)
    {
        var slots = new List<SourceEvalSlot>();
        if (receiverSlot is { } first) slots.Add(first);
        for (var index = 0; index < values.Length; index++)
        {
            var at = index;
            slots.Add(SourceOperand(() => values[at], value => values[at] = value));
        }
        return new(slots, receiver);
    }

    internal static SourceEvalOrder? EvalOrderOf(IBirExpression expression, ValueTypeOracle isValue)
        => SourceEvalOrderOf((BirGraphValue)expression, isValue);

    static SourceEvalOrder? SourceEvalOrderOf(BirGraphValue node, ValueTypeOracle isValue)
    {
        var address = CallEvalLowering.ReceiverNeedsAddress(node, isValue);
        switch (node)
        {
            case BirBinaryOperation value:
                return SourceOperands(false,
                    SourceOperand(() => value.Lhs.OrDefault(), x => value.Lhs = SourceOperandField(x)),
                    SourceOperand(() => value.Rhs.OrDefault(), x => value.Rhs = SourceOperandField(x)));
            case BirObjectEquals value:
                return SourceOperands(false,
                    SourceOperand(() => value.Lhs.OrDefault(), x => value.Lhs = SourceOperandField(x)),
                    SourceOperand(() => value.Rhs.OrDefault(), x => value.Rhs = SourceOperandField(x)));
            case BirConcat value: return SourceOperandList(value.Parts.OrEmpty());
            case BirCallStatic value:
                if (value.SuspendCall.OrDefault() && IsAwaitMarkerSource(value)) return null;
                return SourceOperandList(value.Args.OrEmpty());
            case BirCallInstance value:
                if (value.SuspendCall.OrDefault() && IsAwaitMarkerSource(value)) return null;
                var instanceReceiver = value.SuspendCall.OrDefault() || value.Recv.HasValue;
                return SourceOperandList(value.Args.OrEmpty(), instanceReceiver, instanceReceiver
                    ? SourceOperand(() => value.Recv.OrDefault(), x => value.Recv = SourceOperandField(x), address) : null);
            case BirNativeStatic value:
                if (value.SuspendCall.OrDefault() && IsAwaitMarkerSource(value)) return null;
                var staticReceiver = !value.SuspendCall.OrDefault() && value.Recv.HasValue;
                return SourceOperandList(value.Args.OrEmpty(), staticReceiver, staticReceiver
                    ? SourceOperand(() => value.Recv.OrDefault(), x => value.Recv = SourceOperandField(x), address) : null);
            case BirNativeInstance value:
                if (value.SuspendCall.OrDefault() && IsAwaitMarkerSource(value)) return null;
                var nativeReceiver = value.SuspendCall.OrDefault() || value.Recv.HasValue;
                return SourceOperandList(value.Args.OrEmpty(), nativeReceiver, nativeReceiver
                    ? SourceOperand(() => value.Recv.OrDefault(), x => value.Recv = SourceOperandField(x), address) : null);
            case BirGenericStatic value:
                if (value.SuspendCall.OrDefault() && IsAwaitMarkerSource(value)) return null;
                return SourceOperandList(value.Args.OrEmpty());
            case BirGenericInstance value:
                if (value.SuspendCall.OrDefault() && IsAwaitMarkerSource(value)) return null;
                var genericReceiver = value.SuspendCall.OrDefault() || value.Recv.HasValue;
                return SourceOperandList(value.Args.OrEmpty(), genericReceiver, genericReceiver
                    ? SourceOperand(() => value.Recv.OrDefault(), x => value.Recv = SourceOperandField(x), address) : null);
            case BirNew value: return SourceOperandList(value.Args.OrEmpty());
            case BirNewClr value: return SourceOperandList(value.Args.OrEmpty());
            case BirArrayGet value:
                return SourceOperands(false,
                    SourceOperand(() => value.Array.OrDefault(), x => value.Array = SourceOperandField(x)),
                    SourceOperand(() => value.Index.OrDefault(), x => value.Index = SourceOperandField(x)));
            case BirArraySet value:
                return SourceOperands(false,
                    SourceOperand(() => value.Array.OrDefault(), x => value.Array = SourceOperandField(x)),
                    SourceOperand(() => value.Index.OrDefault(), x => value.Index = SourceOperandField(x)),
                    SourceOperand(() => value.Value.OrDefault(), x => value.Value = SourceOperandField(x)));
            case BirSetField value:
                return SourceReceiverThen(() => value.Recv.OrDefault(),
                    x => value.Recv = SourceOperandField(x), address,
                    SourceOperand(() => value.Value.OrDefault(), x => value.Value = SourceOperandField(x)));
            case BirFieldSetExpression value:
                return SourceReceiverThen(() => value.Recv.OrDefault(),
                    x => value.Recv = SourceOperandField(x), address,
                    SourceOperand(() => value.Value.OrDefault(), x => value.Value = SourceOperandField(x)));
            case BirPropertySet value:
                return SourceReceiverThen(() => value.Recv.OrDefault(),
                    x => value.Recv = SourceOperandField(x), address,
                    SourceOperand(() => value.Value.OrDefault(), x => value.Value = SourceOperandField(x)));
            case BirDelegateInvoke value:
                return SourceOperandList(value.Args.OrEmpty(), true,
                    SourceOperand(() => value.Recv.OrDefault(), x => value.Recv = SourceOperandField(x), address));
            case BirObjectMethod value:
                var receiver = SourceOperand(() => value.Recv.OrDefault(), x => value.Recv = SourceOperandField(x));
                return value.Arg.HasValue ? SourceOperands(true, receiver,
                    SourceOperand(() => value.Arg.OrDefault(), x => value.Arg = SourceOperandField(x)))
                    : SourceOperands(true, receiver);
            case BirConstrainedCall value:
                return SourceOperandList(value.Args.OrEmpty(), true,
                    SourceOperand(() => value.Recv.OrDefault(), x => value.Recv = SourceOperandField(x), address));
            case BirNewArray value: return SourceOperandList(value.Elems.OrEmpty());
            case BirNewList value: return SourceOperandList(value.Elems.OrEmpty());
            case BirNewArraySized value:
                return SourceOperands(false, SourceOperand(() => value.Size.OrDefault(), x => value.Size = SourceOperandField(x)));
            case BirNewArrayInit value:
                return SourceOperands(false,
                    SourceOperand(() => value.Size.OrDefault(), x => value.Size = SourceOperandField(x)),
                    SourceOperand(() => value.Init.OrDefault(), x => value.Init = SourceOperandField(x)));
            case BirSpreadConcat value:
                return new SourceEvalOrder(value.Parts.OrEmpty().Select(part =>
                    SourceOperand(() => part.E.OrDefault(), x => part.E = SourceOperandField(x))).ToArray(), false);
            default: return null;
        }
    }

    static SourceEvalOrder SourceReceiverThen(Func<IBirExpression> receiver, Action<IBirExpression> write,
        bool address, SourceEvalSlot value)
    {
        return receiver() == null ? SourceOperands(false, value) : SourceOperands(true,
            SourceOperand(receiver, write, address), value);
    }

    // Extend node-local type facts with the current declared-local/field index;
    // do not select methods or infer declaration facts from physical names.
    internal sealed class SourceExprTyper
    {
        readonly IReadOnlyDictionary<string, TypeNode> methodReturns;
        readonly IReadOnlyDictionary<string, TypeNode> fieldTypes;
        internal SourceExprTyper(IReadOnlyDictionary<string, TypeNode> methodReturns,
            IReadOnlyDictionary<string, TypeNode> fieldTypes)
        {
            this.methodReturns = methodReturns;
            this.fieldTypes = fieldTypes;
        }

        internal TypeNode Of(IBirExpression expression, IReadOnlyDictionary<string, LocalDecl> locals)
        {
            if (expression is not BirGraphNode node) return null;
            if (node is BirLocal local && local.Name.HasValue && locals.TryGetValue(local.Name.Value, out var declaration))
                return declaration.Type;
            var type = BirNodeType.Of(node, child => Of((IBirExpression)child, locals),
                name => BirTypeLowering.PrimArrayElem.TryGetValue(name, out var element) ? element : null);
            if (type != null) return type;
            switch (node)
            {
                case BirCallStatic call:
                    var owner = SourceOperandOwner(call.Owner.OrDefault());
                    var name = call.Method.OrDefault();
                    if (methodReturns.TryGetValue((owner == null ? "#" : owner + "#") + name, out var result)) return result;
                    if (methodReturns.TryGetValue("#" + name, out result)) return result;
                    break;
                case BirCallInstance call:
                    if (SourceOperandOwner(call.OwnerType.OrDefault()) is { } instanceOwner
                        && methodReturns.TryGetValue(instanceOwner + "#" + call.Method.OrDefault(), out var instanceResult)) return instanceResult;
                    break;
                case BirFieldGet field:
                    return Field(field.OwnerType.OrDefault(), field.Name.OrDefault());
                case BirStaticFieldGet field:
                    return Field(field.OwnerType.OrDefault(), field.Name.OrDefault());
                case BirLateinitGet field:
                    return Field(field.OwnerType.OrDefault(), field.Name.OrDefault());
            }
            return null;

            TypeNode Field(TypeNode owner, string name)
            {
                if (SourceOperandOwner(owner) is { } fieldOwner && fieldTypes.TryGetValue(fieldOwner + "#" + name, out var result)) return result;
                return fieldTypes.TryGetValue("#" + name, out result) ? result : null;
            }
        }
    }

    static string SourceOperandOwner(TypeNode type) => type is TypeNode.Fqn named ? named.Name : null;

    internal static void PlanSuspensionBearingOperands(IReadOnlyList<BirFileDeclaration> roots,
        SourceExprTyper typer, ValueTypeOracle isValue, Func<BirFunctionDeclaration, bool, bool> checkBody,
        Action<BirGraphValue> retireDiscardedSubtree)
    {
        ArgumentNullException.ThrowIfNull(checkBody);
        ArgumentNullException.ThrowIfNull(retireDiscardedSubtree);
        var materialized = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in roots)
            PlanSourceNode(file, typer, isValue, materialized,
                new Dictionary<string, LocalDecl>(StringComparer.Ordinal), file.FileClass.OrDefault() ?? "?", retireDiscardedSubtree);
        AssertSourceOperandsPlanned(roots, materialized, isValue, checkBody);
    }

    static bool SourceOperandFrame(BirGraphValue node) => node is BirFunctionDeclaration or BirConstructor
        or BirClosureDefinition or BirInlineLambda || IsSourceOtherFrame(node) || IsSourceInlineLoop(node);

    static BirGraphValue PlanSourceNode(BirGraphValue node, SourceExprTyper typer, ValueTypeOracle isValue,
        HashSet<string> materialized, Dictionary<string, LocalDecl> locals, string where,
        Action<BirGraphValue> retireDiscardedSubtree)
    {
        if (node == null || node is IBirExecutablePayload) return node;
        // Construction operands run in the caller's frame. In particular a
        // capValue may read a same-named caller local, not the lambda parameter.
        HashSet<BirGraphValue> callerOperands = null;
        if (IsSourceOtherFrame(node))
        {
            callerOperands = new(ReferenceEqualityComparer.Instance);
            node.VisitCurrentFrameSlots(slot => {
                slot.Replace(PlanSourceNode(slot.Value, typer, isValue, materialized, locals, where, retireDiscardedSubtree));
                callerOperands.Add(slot.Value);
            });
        }
        var scope = SourceOperandFrame(node)
            ? node is BirFunctionDeclaration or BirConstructor or BirClosureDefinition
                ? new Dictionary<string, LocalDecl>(StringComparer.Ordinal)
                : new Dictionary<string, LocalDecl>(locals, StringComparer.Ordinal)
            : locals;
        if (node is BirNewSuspendLambda lambda)
            foreach (var capture in lambda.Captures.OrEmpty())
                if (capture.Name.HasValue)
                    scope[capture.Name.Value] = new(capture.Type.OrDefault() ?? throw new NotSupportedException(
                        $"bir2cir: suspend-lambda lowering: the capture `{capture.Name.Value}` carries no type — an earlier lowering dropped it."), null);
        node.VisitSourceParameters((name, type) => scope[name] = new(type, null));
        var site = node is BirFunctionDeclaration method ? method.Name.OrDefault() ?? where : where;
        DeclareSourceOperandLocals(node, scope);
        node.VisitSourceChildren((_, role, slot) => {
            if (!SourceOperandForeign(role) && callerOperands?.Contains(slot.Value) != true)
                slot.Replace(PlanSourceNode(slot.Value, typer, isValue, materialized, scope, site, retireDiscardedSubtree));
        }, region => {
            for (var index = 0; index < region.Statements.Length; index++)
                region.Statements[index] = (IBirStatement)PlanSourceNode((BirGraphValue)region.Statements[index],
                    typer, isValue, materialized, scope, site, retireDiscardedSubtree);
            if (region.Result is { } result) result.Replace(PlanSourceNode(result.Value, typer, isValue, materialized, scope, site, retireDiscardedSubtree));
        });
        return PlanSourceOperandsOf(node, typer, isValue, materialized, scope, site, retireDiscardedSubtree);
    }

    static bool SourceOperandForeign(BirGenericFrameRole role) => role is BirGenericFrameRole.ReferencedDeclaration
        or BirGenericFrameRole.ResolvedDeclaration or BirGenericFrameRole.OverrideDeclaration
        or BirGenericFrameRole.FrozenSourceFact or BirGenericFrameRole.StorageSourceType;

    static void DeclareSourceOperandLocals(BirGraphValue node, Dictionary<string, LocalDecl> scope)
    {
        void Add(string name, TypeNode type, string role = null)
        { if (name != null && type != null) scope[name] = new(type, role); }
        switch (node)
        {
            case BirVariable variable: Add(variable.Name.OrDefault(), variable.Type.OrDefault(), variable.Role.OrDefault()); break;
            case BirTry statement:
                foreach (var clause in statement.Catches.OrEmpty()) Add(clause.Var.OrDefault(), clause.ExcType.OrDefault());
                break;
            case BirForArray loop: Add(loop.Var.OrDefault(), loop.Elem.OrDefault()); break;
            case BirForRange loop: Add(loop.Var.OrDefault(), loop.Elem.OrDefault()); break;
            case BirForEach loop: Add(loop.Var.OrDefault(), loop.Elem.OrDefault()); break;
            case BirForIn loop: Add(loop.Var.OrDefault(), loop.Elem.OrDefault()); break;
        }
    }

    static int LastSourceSuspending(IReadOnlyList<IBirExpression> operands)
    {
        var last = -1;
        for (var index = 0; index < operands.Count; index++)
            if (operands[index] is BirGraphValue value && HasLoopBorneSuspensionSource(value)) last = index;
        return last;
    }

    static bool SourceOperandSuspendCall(BirGraphValue node) => node switch {
        BirCallStatic value => value.SuspendCall.OrDefault(), BirCallInstance value => value.SuspendCall.OrDefault(),
        BirNativeStatic value => value.SuspendCall.OrDefault(), BirNativeInstance value => value.SuspendCall.OrDefault(),
        BirGenericStatic value => value.SuspendCall.OrDefault(), BirGenericInstance value => value.SuspendCall.OrDefault(),
        BirConstrainedCall value => value.SuspendCall.OrDefault(), _ => false,
    };

    static bool SourceOperandStable(IBirExpression expression) => ValueStability.IsReReadable(
        (expression as BirGraphNode)?.Tag,
        expression is BirEnumValue { Underlying.HasValue: true, PhysicalValue.HasValue: true });

    static BirGraphValue PlanSourceOperandsOf(BirGraphValue node, SourceExprTyper typer, ValueTypeOracle isValue,
        HashSet<string> materialized, Dictionary<string, LocalDecl> locals, string where,
        Action<BirGraphValue> retireDiscardedSubtree)
    {
        if (SourceEvalOrderOf(node, isValue) is not { } order) return node;
        var operands = order.Operands;
        var last = LastSourceSuspending(operands);
        if (last < 0) return node;
        var terminal = -1;
        for (var index = 0; index < operands.Length && terminal < 0; index++)
            if (operands[index] != null && NodeType.IsNothing(typer.Of(operands[index], locals))) terminal = index;
        if (terminal >= 0 && last <= terminal) terminal = -1;
        var count = terminal >= 0 ? terminal : operands.Length;
        for (var index = 0; index < count; index++) if (operands[index] == null) return node;
        var force = Enumerable.Range(0, count).Select(index => index < last
            || index == last && SourceOperandSuspendCall(node)).ToArray();
        if (terminal < 0 && !force.Any(value => value)) return node;
        var originalType = node is IBirExpression expression ? typer.Of(expression, locals) : null;
        if (terminal >= 0)
            for (var index = terminal + 1; index < operands.Length; index++)
                if (operands[index] is BirGraphValue discarded) retireDiscardedSubtree(discarded);
        var role = SourceOperandRole(node);
        var bindings = new BirCallBinding[count];
        for (var index = 0; index < count; index++)
        {
            var operand = operands[index];
            var id = CallEvalLowering.FreshBindingId();
            var stable = SourceOperandStable(operand);
            var type = typer.Of(operand, locals);
            if (type == null && force[index] && !stable)
                throw new NotSupportedException($"bir2cir: suspend-lowering: in `{where}`, the `{(operand as BirGraphNode)?.Tag ?? "?"}` "
                    + $"{(order.HasReceiver && index == 0 ? "receiver" : "argument " + index)} of an expression is evaluated "
                    + "before a suspending operand and carries no static type, so its suspension-spanning local would be untyped.");
            var inherited = operand is BirLocal local && local.Name.HasValue && locals.TryGetValue(local.Name.Value, out var declaration)
                ? declaration.Role : null;
            var binding = new BirCallBinding {
                Id = id, Phase = order.HasReceiver && index == 0 ? "recv" : "arg",
                Kind = order.Slots[index].PreserveLocation ? "address" : "value",
                Stable = stable, Role = inherited ?? role, Expr = SourceOperandField(operand),
            };
            var read = new BirBindingReference { Id = id };
            if (type != null) { binding.Type = type; read.Sty = type; }
            bindings[index] = binding;
            if (terminal < 0) order.Slots[index].Slot.Replace(read);
        }
        var reader = terminal >= 0 ? (BirGraphValue)operands[terminal] : node;
        var (statements, replacements) = TypedCallEvalLowering.MaterialiseOwned(bindings,
            new[] { reader }, role, force, isValue);
        var lowered = TypedCallEvalLowering.SubstituteOwned(reader, replacements);
        foreach (var statement in statements)
            if (statement is BirVariable { Name.HasValue: true } variable) materialized.Add(variable.Name.Value);
        if (terminal < 0 && statements.Length == 0) return lowered;
        if (node is BirSetField)
        {
            IBirStatement lastStatement = terminal >= 0
                ? new BirExpressionStatement { Expr = SourceOperandField((IBirExpression)lowered) } : (IBirStatement)lowered;
            return new BirBlock { Body = statements.Append(lastStatement).ToArray() };
        }
        var block = new BirValueBlock { Stmts = statements, Result = SourceOperandField((IBirExpression)lowered) };
        if (originalType != null) block.Type = originalType;
        return block;
    }

    static string SourceOperandRole(BirGraphValue node) => node switch {
        BirSetField or BirFieldSetExpression or BirPropertySet => "operand of a member write",
        BirArrayGet or BirArraySet => "operand of an array access",
        BirConcat => "operand of a string concatenation",
        BirBinaryOperation operation when operation.Op.HasValue => $"operand of the `{operation.Op.Value}` operator",
        BirNew value when SourceOperandOwner(value.Type.OrDefault()) is { } name => $"operand of the construction of `{name}`",
        BirNewClr value when SourceOperandOwner(value.Type.OrDefault()) is { } name => $"operand of the construction of `{name}`",
        _ => SourceOperandMethod(node) is { } method ? $"operand of the call to `{method}`" : "operand of an expression",
    };

    static string SourceOperandMethod(BirGraphValue node) => node switch {
        BirCallStatic value => value.Method.OrDefault(), BirCallInstance value => value.Method.OrDefault(),
        BirNativeStatic value => value.Method.OrDefault(), BirNativeInstance value => value.Method.OrDefault(),
        BirGenericStatic value => value.Method.OrDefault(), BirGenericInstance value => value.Method.OrDefault(),
        BirConstrainedCall value => value.Method.OrDefault(), BirObjectMethod value => value.Method.OrDefault(), _ => null,
    };

    static void AssertSourceOperandsPlanned(IReadOnlyList<BirFileDeclaration> roots, HashSet<string> materialized,
        ValueTypeOracle isValue, Func<BirFunctionDeclaration, bool, bool> checkBody)
    {
        foreach (var file in roots)
        {
            CheckMembers(file.Methods.OrEmpty(), Array.Empty<BirConstructor>(), file.Fields.OrEmpty(), true);
            foreach (var owner in file.Types.OrEmpty())
                CheckMembers(owner.Methods.OrEmpty(), owner.Ctors.OrEmpty(), owner.Fields.OrEmpty(), false);
        }

        void CheckMembers(BirFunctionDeclaration[] methods, BirConstructor[] constructors,
            BirFieldDeclaration[] fields, bool topLevel)
        {
            foreach (var method in methods)
                if (checkBody(method, topLevel)) foreach (var statement in method.Body.OrEmpty()) Check((BirGraphValue)statement);
            foreach (var constructor in constructors)
            {
                foreach (var statement in constructor.Body.OrEmpty()) Check((BirGraphValue)statement);
                foreach (var statement in constructor.PreStmts.OrEmpty()) Check((BirGraphValue)statement);
                foreach (var argument in constructor.ThisArgs.OrEmpty()) Check((BirGraphValue)argument);
                foreach (var argument in constructor.BaseArgs.OrEmpty()) Check((BirGraphValue)argument);
            }
            foreach (var field in fields) if (field.Static.OrDefault()) Check(field.Init.OrDefault() as BirGraphValue);
        }
        void Check(BirGraphValue node)
        {
            if (node == null || node is IBirExecutablePayload) return;
            if (SourceEvalOrderOf(node, isValue) is { } order)
            {
                var operands = order.Operands;
                var last = LastSourceSuspending(operands);
                if (last >= 0 && SourceOperandSuspendCall(node))
                    throw new InvalidOperationException("A suspending source call retained a suspending operand after Stage0");
                for (var index = 0; index < last; index++)
                    if (operands[index] != null && !(order.Slots[index].PreserveLocation
                        ? TypedCallEvalLowering.IsPinnedLocation(operands[index], Settled, isValue) : Settled(operands[index])))
                        throw new InvalidOperationException("An operand before a suspension was not materialized by Stage0");
            }
            node.VisitSourceChildren((_, role, slot) => { if (!SourceOperandForeign(role)) Check(slot.Value); }, region => {
                foreach (var statement in region.Statements) Check((BirGraphValue)statement);
                if (region.Result is { } result) Check(result.Value);
            });
        }
        bool Settled(IBirExpression expression) => SourceOperandStable(expression)
            || expression is BirLocal { Name.HasValue: true } local && materialized.Contains(local.Name.Value);
    }
}
