using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

// Author empty-stack evaluation sites on the owning physical graph. Operands are
// selected by their declared node roles, never by JSON keys or apparent shape.
static partial class TypedPhysicalOperandNormalization
{
    static int nextTemporary;

    internal static void Apply(BirGraphValue root, ValueTypeOracle isValue,
        Func<BirGraphValue, IReadOnlyDictionary<string, TypeNode>, TypeNode> typeOf,
        ReferenceMetadataIndex references)
    {
        var owner = references.ResolveRefType("System.Type");
        var getter = ClrMemberResolution.MemberRefOf(owner.GetProperty("IsValueType").GetMethod,
            MemberRefNode.Kinds.Method, owner, null);
        new Normalizer(isValue ?? (_ => false), typeOf, BirMemberReference.FromPhysical(getter))
            .Walk(root, new Dictionary<string, TypeNode>(StringComparer.Ordinal));
    }

    readonly record struct Operand(Func<IBirExpression> Read, Action<IBirExpression> Write,
        bool Location = false, TypeNode Element = null, bool Receiver = false);
    sealed record Sequence(Operand[] Operands, bool BeginsEmpty, bool SpillWholeStream = false);
    readonly record struct StatementList(OptionalField<IBirStatement[]> Value,
        Action<OptionalField<IBirStatement[]>> Write, bool Body = true);

    static Operand Slot(Func<OptionalField<IBirExpression>> read, Action<OptionalField<IBirExpression>> write)
        => new(() => read().OrDefault(), value => write(OptionalField<IBirExpression>.Of(value)));
    static Operand[] Slots(IBirExpression[] values) => values == null ? [] : values.Select((_, index) =>
        new Operand(() => values[index], value => values[index] = value)).ToArray();
    static IBirExpression Copy(IBirExpression value)
        => (IBirExpression)((BirGraphValue)value).Rewrite(node => node);
    static string Temporary(string role) => "dotkt$" + role + Interlocked.Increment(ref nextTemporary);

    sealed class Normalizer(ValueTypeOracle isValue,
        Func<BirGraphValue, IReadOnlyDictionary<string, TypeNode>, TypeNode> typeOf,
        BirMemberReference isValueType)
    {
        // A list gets one private environment, with declarations becoming visible
        // only after their initializers. Sibling blocks never share mutable locals.
        internal void Walk(BirGraphValue node, IReadOnlyDictionary<string, TypeNode> outer)
        {
            if (node is IBirExecutablePayload) return;
            var scope = FrameScope(node, outer);
            var lists = Lists(node);
            if (lists.Length == 0) node.VisitChildSlots(child => Walk(child.Value, scope));
            else
            {
                var statements = new HashSet<BirGraphValue>(ReferenceEqualityComparer.Instance);
                foreach (var list in lists)
                    foreach (var statement in list.Value.OrEmpty()) statements.Add((BirGraphValue)statement);
                node.VisitChildSlots(child =>
                {
                    if (statements.Contains(child.Value)) return;
                    var childScope = node is BirValueBlock block && ReferenceEquals(child.Value, block.Result.OrDefault())
                        ? BlockScope(block, scope) : scope;
                    Walk(child.Value, childScope);
                });
            }
            foreach (var list in lists)
            {
                if (!list.Value.HasValue) continue;
                var bodyScope = list.Body ? BodyScope(node, scope) : scope;
                WalkList(list.Value.Value, bodyScope);
                list.Write(HoistList(list.Value.Value, bodyScope));
            }

            if (node is BirConstructor constructor)
            {
                NormalizeDelegation(constructor, constructor.BaseArgs, scope);
                NormalizeDelegation(constructor, constructor.ThisArgs, scope);
            }
            switch (node)
            {
                case BirWhile value: AtSite(Slot(() => value.Cond, v => value.Cond = v), scope); break;
                case BirBranchIf value: AtSite(Slot(() => value.Cond, v => value.Cond = v), scope); break;
                case BirBranch value: AtSite(Slot(() => value.Cond, v => value.Cond = v), scope); break;
                case BirConditional value:
                    AtSite(Slot(() => value.Cond, v => value.Cond = v), scope);
                    AtSite(Slot(() => value.Then, v => value.Then = v), scope);
                    AtSite(Slot(() => value.Else, v => value.Else = v), scope);
                    break;
            }
        }

        void WalkList(IBirStatement[] values, IReadOnlyDictionary<string, TypeNode> outer)
        {
            var scope = new Dictionary<string, TypeNode>(outer, StringComparer.Ordinal);
            foreach (var value in values)
            {
                Walk((BirGraphValue)value, scope);
                Declare(value, scope);
            }
        }

        IBirStatement[] HoistList(IBirStatement[] values, IReadOnlyDictionary<string, TypeNode> outer)
        {
            var result = new List<IBirStatement>();
            var scope = new Dictionary<string, TypeNode>(outer, StringComparer.Ordinal);
            foreach (var statement in values)
            {
                var prefix = new List<IBirStatement>();
                switch (statement)
                {
                    case BirVariable value: Child(Slot(() => value.Init, v => value.Init = v), prefix, scope); break;
                    case BirSetLocal value: Child(Slot(() => value.Value, v => value.Value = v), prefix, scope); break;
                    case BirReturn value: Child(Slot(() => value.Value, v => value.Value = v), prefix, scope); break;
                    case BirThrow value: Child(Slot(() => value.Value, v => value.Value = v), prefix, scope); break;
                    case BirExpressionStatement value: Child(Slot(() => value.Expr, v => value.Expr = v), prefix, scope); break;
                    case BirSetField value: Ordered(Plan(value, true), prefix, scope); break;
                    case BirForArray value: Child(Slot(() => value.Array, v => value.Array = v), prefix, scope); break;
                    case BirForRange value: Child(Slot(() => value.Range, v => value.Range = v), prefix, scope); break;
                    case BirForEach value: Child(Slot(() => value.Src, v => value.Src = v), prefix, scope); break;
                    case BirRepeat value: Child(Slot(() => value.Count, v => value.Count = v), prefix, scope); break;
                }
                result.AddRange(prefix);
                result.Add(statement);
                Declare(statement, scope);
            }
            return result.ToArray();
        }

        static void Declare(IBirStatement statement, IDictionary<string, TypeNode> scope)
        {
            if (statement is BirVariable { Name.HasValue: true, Type.HasValue: true } variable)
                scope[variable.Name.Value] = variable.Type.Value;
        }

        static IReadOnlyDictionary<string, TypeNode> FrameScope(BirGraphValue node,
            IReadOnlyDictionary<string, TypeNode> outer)
        {
            var parameters = node switch
            {
                BirFunctionDeclaration value => value.Params.OrEmpty().Select(p => (p.Name, p.Type)),
                BirConstructor value => value.Params.OrEmpty().Select(p => (p.Name, p.Type)),
                BirClosureDefinition value => value.Params.OrEmpty().Select(p => (p.Name, p.Type)),
                BirNewSuspendLambda value => value.Params.OrEmpty().Select(p => (p.Name, p.Type)),
                BirInlineLambda value => value.Params.OrEmpty().Select(p => (p.Name, p.Type)),
                _ => null,
            };
            if (parameters == null) return outer;
            var scope = new Dictionary<string, TypeNode>(outer, StringComparer.Ordinal);
            foreach (var (name, type) in parameters)
                if (name.HasValue && type.HasValue) scope[name.Value] = type.Value;
            return scope;
        }

        static IReadOnlyDictionary<string, TypeNode> BodyScope(BirGraphValue node,
            IReadOnlyDictionary<string, TypeNode> outer)
        {
            var scope = outer;
            if (node is BirConstructor { PreStmts.HasValue: true } constructor)
            {
                var prefix = new Dictionary<string, TypeNode>(outer, StringComparer.Ordinal);
                foreach (var statement in constructor.PreStmts.Value) Declare(statement, prefix);
                scope = prefix;
            }
            var (name, type) = node switch
            {
                BirCatch value => (value.Var.OrDefault(), value.ExcType.OrDefault()),
                BirForArray value => (value.Var.OrDefault(), value.Elem.OrDefault()),
                BirForEach value => (value.Var.OrDefault(), value.Elem.OrDefault()),
                BirForRange value => (value.Var.OrDefault(), new TypeNode.Fqn("System.Int32")),
                BirRepeat value => (value.Var.OrDefault(), new TypeNode.Fqn("System.Int32")),
                _ => ((string)null, (TypeNode)null),
            };
            if (name == null || type == null) return scope;
            var body = new Dictionary<string, TypeNode>(scope, StringComparer.Ordinal) { [name] = type };
            return body;
        }

        static IReadOnlyDictionary<string, TypeNode> BlockScope(BirValueBlock block,
            IReadOnlyDictionary<string, TypeNode> outer)
        {
            var scope = new Dictionary<string, TypeNode>(outer, StringComparer.Ordinal);
            foreach (var statement in block.Stmts.OrEmpty()) Declare(statement, scope);
            return scope;
        }

        static StatementList[] Lists(BirGraphValue node) => node switch
        {
            BirFunctionDeclaration value => [new(value.Body, body => value.Body = body)],
            BirConstructor value => [new(value.Body, body => value.Body = body), new(value.PreStmts, body => value.PreStmts = body, false)],
            BirClosureDefinition value => [new(value.Body, body => value.Body = body)],
            BirNewSuspendLambda value => [new(value.Body, body => value.Body = body)],
            BirInlineLambda value => [new(value.Body, body => value.Body = body)],
            BirValueBlock value => [new(value.Stmts, body => value.Stmts = body, false)],
            BirBlock value => [new(value.Body, body => value.Body = body)],
            BirCatch value => [new(value.Body, body => value.Body = body)],
            BirTry value => [new(value.Body, body => value.Body = body), new(value.Finally, body => value.Finally = body, false)],
            BirBranch value => [new(value.Body, body => value.Body = body)],
            BirWhile value => [new(value.Body, body => value.Body = body)],
            BirFor value => [new(value.Body, body => value.Body = body)],
            BirForArray value => [new(value.Body, body => value.Body = body)],
            BirForIn value => [new(value.Body, body => value.Body = body)],
            BirForRange value => [new(value.Body, body => value.Body = body)],
            BirForEach value => [new(value.Body, body => value.Body = body)],
            BirRepeat value => [new(value.Body, body => value.Body = body)],
            _ => [],
        };

        void NormalizeDelegation(BirConstructor constructor, OptionalField<IBirExpression[]> arguments,
            IReadOnlyDictionary<string, TypeNode> outer)
        {
            if (!arguments.HasValue) return;
            var signature = constructor.BaseCtorRef.OrDefault()?.ParameterTypes.OrDefault()
                ?? constructor.DelegationSig.OrDefault();
            var operands = Slots(arguments.Value).Select((slot, index) => signature != null
                && index < signature.Length && signature[index] is TypeNode.ByRef reference
                    ? slot with { Location = true, Element = reference.Of } : slot).ToArray();
            var prefix = new List<IBirStatement>();
            Ordered(new(operands, false), prefix, BodyScope(constructor, outer));
            if (prefix.Count != 0) constructor.PreStmts = constructor.PreStmts.OrEmpty().Concat(prefix).ToArray();
        }

        void Child(Operand slot, List<IBirStatement> prefix, IReadOnlyDictionary<string, TypeNode> scope)
        {
            if (slot.Read() is { } value) slot.Write(Hoist(value, prefix, true, scope));
        }

        void AtSite(Operand slot, IReadOnlyDictionary<string, TypeNode> scope)
        {
            if (slot.Read() is not { } value) return;
            var prefix = new List<IBirStatement>();
            var result = Hoist(value, prefix, true, scope);
            slot.Write(prefix.Count == 0 ? result : new BirValueBlock { Stmts = prefix.ToArray(),
                Result = OptionalField<IBirExpression>.Of(result) });
        }

        IBirExpression Hoist(IBirExpression expression, List<IBirStatement> prefix, bool atEmpty,
            IReadOnlyDictionary<string, TypeNode> scope)
        {
            if (expression == null) return null;
            var node = (BirGraphValue)expression;
            if (!atEmpty && RunsStackBoundary(node))
                return Spill(Hoist(expression, prefix, true, scope), prefix, scope);
            switch (node)
            {
                case BirValueBlock block:
                    AtSite(Slot(() => block.Result, v => block.Result = v), BlockScope(block, scope));
                    return expression;
                case BirConditional conditional:
                    AtSite(Slot(() => conditional.Cond, v => conditional.Cond = v), scope);
                    AtSite(Slot(() => conditional.Then, v => conditional.Then = v), scope);
                    AtSite(Slot(() => conditional.Else, v => conditional.Else = v), scope);
                    return expression;
            }
            if (Plan(node, atEmpty) is { } plan) Ordered(plan, prefix, scope);
            else foreach (var child in EagerChildren(node))
                child.Replace((BirGraphValue)Hoist((IBirExpression)child.Value, prefix, atEmpty, scope));
            return expression;
        }

        Sequence Named(BirGraphValue node, bool atEmpty, bool firstIsReceiver, params Operand[] operands)
        {
            var present = operands.Where(operand => operand.Read() != null).ToArray();
            if (firstIsReceiver && operands.Length > 0 && operands[0].Read() != null
                && CallEvalLowering.ReceiverNeedsAddress(node, isValue))
                present[0] = present[0] with { Location = true, Element = ReceiverType(node), Receiver = true };
            return new(present, atEmpty && node is not BirEnumParse
                && (node is not BirByRefStore store || store.Ptr.HasValue));
        }

        Sequence Call(BirGraphValue node, bool atEmpty, Operand? receiver, IBirExpression[] arguments,
            TypeNode[] signature)
        {
            var operands = new List<Operand>();
            if (receiver is { } recv && recv.Read() != null)
                operands.Add(recv with { Location = CallEvalLowering.ReceiverNeedsAddress(node, isValue),
                    Element = ReceiverType(node), Receiver = true });
            var args = Slots(arguments);
            for (var index = 0; index < args.Length; index++)
                operands.Add(signature != null && index < signature.Length && signature[index] is TypeNode.ByRef reference
                    ? args[index] with { Location = true, Element = reference.Of } : args[index]);
            return new(operands.ToArray(), atEmpty);
        }

        Sequence Plan(BirGraphValue node, bool atEmpty) => node switch
        {
            BirBinaryOperation v => Named(v, atEmpty, false, Slot(() => v.Lhs, x => v.Lhs = x), Slot(() => v.Rhs, x => v.Rhs = x)),
            BirArrayGet v => Named(v, atEmpty, false, Slot(() => v.Array, x => v.Array = x), Slot(() => v.Index, x => v.Index = x)),
            BirArraySet v => Named(v, atEmpty, false, Slot(() => v.Array, x => v.Array = x), Slot(() => v.Index, x => v.Index = x), Slot(() => v.Value, x => v.Value = x)),
            BirObjectEquals v => Named(v, atEmpty, false, Slot(() => v.Lhs, x => v.Lhs = x), Slot(() => v.Rhs, x => v.Rhs = x)),
            BirObjectMethod v => Named(v, atEmpty, true, Slot(() => v.Recv, x => v.Recv = x), Slot(() => v.Arg, x => v.Arg = x)),
            BirFieldSetExpression v => Named(v, atEmpty, true, Slot(() => v.Recv, x => v.Recv = x), Slot(() => v.Value, x => v.Value = x)),
            BirSetField v => Named(v, atEmpty, true, Slot(() => v.Recv, x => v.Recv = x), Slot(() => v.Value, x => v.Value = x)),
            BirPropertySet v => Named(v, atEmpty, true, Slot(() => v.Recv, x => v.Recv = x), Slot(() => v.Value, x => v.Value = x)),
            BirEventAdd v => Named(v, atEmpty, true, Slot(() => v.Recv, x => v.Recv = x), Slot(() => v.Handler, x => v.Handler = x)),
            BirEventRemove v => Named(v, atEmpty, true, Slot(() => v.Recv, x => v.Recv = x), Slot(() => v.Handler, x => v.Handler = x)),
            BirStackGet v => Named(v, atEmpty, false, Slot(() => v.Index, x => v.Index = x), Slot(() => v.Len, x => v.Len = x), Slot(() => v.Ptr, x => v.Ptr = x)),
            BirStackSet v => Named(v, atEmpty, false, Slot(() => v.Index, x => v.Index = x), Slot(() => v.Len, x => v.Len = x), Slot(() => v.Ptr, x => v.Ptr = x), Slot(() => v.Value, x => v.Value = x)),
            BirStackAsSpan v => Named(v, atEmpty, false, Slot(() => v.Ptr, x => v.Ptr = x), Slot(() => v.Len, x => v.Len = x)),
            BirByRefStore v => Named(v, atEmpty, false, Slot(() => v.Ptr, x => v.Ptr = x), Slot(() => v.Value, x => v.Value = x)),
            BirNewArrayInit v => Named(v, atEmpty, false, Slot(() => v.Size, x => v.Size = x), Slot(() => v.Init, x => v.Init = x)),
            BirEnumParse v => Named(v, atEmpty, false, Slot(() => v.Arg, x => v.Arg = x)),
            BirConcat v => new(Slots(v.Parts.OrDefault()), false),
            BirNewClosure v => new(Slots(v.Captures.OrDefault()), atEmpty),
            BirNewSam v => new(Slots(v.Captures.OrDefault()), atEmpty),
            BirNewArray v => new(Slots(v.Elems.OrDefault()), false, true),
            BirNewList v => new(Slots(v.Elems.OrDefault()), false, true),
            BirSpreadConcat v => new(v.Parts.OrEmpty().Where(part => part.E.HasValue)
                .Select(part => Slot(() => part.E, x => part.E = x)).ToArray(), false, true),
            BirCallStatic v => Call(v, atEmpty, null, v.Args.OrDefault(), v.ArgTypes.OrDefault() ?? v.ShapeTypes.OrDefault() ?? v.Sig.OrDefault() ?? v.MemberRef.OrDefault()?.ParameterTypes.OrDefault()),
            BirCallInstance v => Call(v, atEmpty, Slot(() => v.Recv, x => v.Recv = x), v.Args.OrDefault(), v.ArgTypes.OrDefault() ?? v.ShapeTypes.OrDefault() ?? v.Sig.OrDefault() ?? v.MemberRef.OrDefault()?.ParameterTypes.OrDefault()),
            BirCallLocal v => Call(v, atEmpty, null, v.Args.OrDefault(), v.Sig.OrDefault()),
            BirNew v => Call(v, atEmpty, null, v.Args.OrDefault(), v.ArgTypes.OrDefault() ?? v.MemberRef.OrDefault()?.ParameterTypes.OrDefault()),
            BirNewClr v => Call(v, atEmpty, null, v.Args.OrDefault(), v.ArgTypes.OrDefault() ?? v.MemberRef.OrDefault()?.ParameterTypes.OrDefault()),
            BirConstrainedCall v => Call(v, atEmpty, Slot(() => v.Recv, x => v.Recv = x), v.Args.OrDefault(), v.ArgTypes.OrDefault() ?? v.Sig.OrDefault() ?? v.MemberRef.OrDefault()?.ParameterTypes.OrDefault()),
            BirGenericStatic v => Call(v, atEmpty, null, v.Args.OrDefault(), v.MemberRef.OrDefault()?.ParameterTypes.OrDefault()),
            BirGenericInstance v => Call(v, atEmpty, Slot(() => v.Recv, x => v.Recv = x), v.Args.OrDefault(), v.MemberRef.OrDefault()?.ParameterTypes.OrDefault()),
            BirNativeStatic v => Call(v, atEmpty, Slot(() => v.Recv, x => v.Recv = x), v.Args.OrDefault(), v.ArgTypes.OrDefault() ?? v.Sig.OrDefault() ?? v.MemberRef.OrDefault()?.ParameterTypes.OrDefault()),
            BirNativeInstance v => Call(v, atEmpty, Slot(() => v.Recv, x => v.Recv = x), v.Args.OrDefault(), v.ArgTypes.OrDefault() ?? v.Sig.OrDefault() ?? v.MemberRef.OrDefault()?.ParameterTypes.OrDefault()),
            BirDelegateInvoke v => Call(v, atEmpty, Slot(() => v.Recv, x => v.Recv = x), v.Args.OrDefault(), v.InvokeRef.OrDefault()?.ParameterTypes.OrDefault()),
            BirFieldGet v => Call(v, atEmpty, Slot(() => v.Recv, x => v.Recv = x), null, v.Sig.OrDefault() ?? v.MemberRef.OrDefault()?.ParameterTypes.OrDefault()),
            BirPropertyGet v => Call(v, atEmpty, Slot(() => v.Recv, x => v.Recv = x), null, v.Sig.OrDefault() ?? v.MemberRef.OrDefault()?.ParameterTypes.OrDefault()),
            BirNewBoundDelegate v => Call(v, atEmpty, Slot(() => v.Recv, x => v.Recv = x), null, v.ArgTypes.OrDefault() ?? v.Sig.OrDefault() ?? v.MemberRef.OrDefault()?.ParameterTypes.OrDefault() ?? v.InvokeRef.OrDefault()?.ParameterTypes.OrDefault()),
            BirBoundClrDelegate v => Call(v, atEmpty, Slot(() => v.Recv, x => v.Recv = x), null, v.Sig.OrDefault() ?? v.MemberRef.OrDefault()?.ParameterTypes.OrDefault()),
            BirLateinitGet v => Call(v, atEmpty, Slot(() => v.Recv, x => v.Recv = x), null, null),
            BirLateinitIsInitialized v => Call(v, atEmpty, Slot(() => v.Recv, x => v.Recv = x), null, null),
            BirEventGet v => Call(v, atEmpty, Slot(() => v.Recv, x => v.Recv = x), null, null),
            BirEventRaise v => Call(v, atEmpty, Slot(() => v.Recv, x => v.Recv = x), v.Args.OrDefault(), null),
            _ => null,
        };

        static TypeNode ReceiverType(BirGraphValue node) => node switch
        {
            BirConstrainedCall value => value.RecvType.OrDefault(),
            BirCallInstance value => value.OwnerType.OrDefault(),
            BirFieldGet value => value.OwnerType.OrDefault() ?? value.Type.OrDefault(),
            BirFieldSetExpression value => value.OwnerType.OrDefault() ?? value.Type.OrDefault(),
            BirSetField value => value.OwnerType.OrDefault() ?? value.Type.OrDefault(),
            BirNativeInstance value => value.Type.OrDefault(),
            BirNativeStatic value => value.Type.OrDefault(),
            BirGenericInstance value => value.Type.OrDefault(),
            BirPropertyGet value => value.Type.OrDefault(),
            BirPropertySet value => value.Type.OrDefault(),
            BirEventAdd value => value.Type.OrDefault(),
            BirEventRemove value => value.Type.OrDefault(),
            BirNewBoundDelegate value => value.OwnerType.OrDefault(),
            BirBoundClrDelegate value => value.Type.OrDefault(),
            BirLateinitGet value => value.OwnerType.OrDefault(),
            BirLateinitIsInitialized value => value.OwnerType.OrDefault(),
            BirEventGet value => value.Type.OrDefault(),
            BirEventRaise value => value.Type.OrDefault(),
            _ => null,
        };

        void Ordered(Sequence sequence, List<IBirStatement> prefix, IReadOnlyDictionary<string, TypeNode> scope)
        {
            var lastHoist = -1;
            var operands = sequence.Operands;
            for (var index = 0; index < operands.Length; index++)
                if (WillHoist((BirGraphValue)operands[index].Read(), index == 0 && sequence.BeginsEmpty)) lastHoist = index;
            for (var index = 0; index < operands.Length; index++)
            {
                var slot = operands[index];
                var value = slot.Read();
                if (slot.Location && lastHoist >= index)
                    value = Address(value, slot.Element, prefix, scope, slot.Receiver);
                var resolved = Hoist(value, prefix, index == 0 && sequence.BeginsEmpty, scope);
                if (!slot.Location && lastHoist >= 0 && (sequence.SpillWholeStream || index < lastHoist))
                    resolved = Spill(resolved, prefix, scope);
                slot.Write(resolved);
            }
        }

        IBirExpression Address(IBirExpression location, TypeNode declaredType, List<IBirStatement> prefix,
            IReadOnlyDictionary<string, TypeNode> scope, bool receiver)
        {
            var resolved = Hoist(location, prefix, true, scope);
            var type = typeOf((BirGraphValue)resolved, scope) ?? declaredType
                ?? throw new InvalidOperationException("bir2cir: an ordered address operand has no declared element type");
            if (!CallEvalLowering.IsLvalueFormer((BirGraphValue)resolved) && type is not TypeNode.ByRef
                || receiver && type is TypeNode.Fqn concrete && !isValue(concrete))
                resolved = Spill(resolved, prefix, scope);
            var reference = type is TypeNode.ByRef ? type : new TypeNode.ByRef(type);
            IBirExpression address = new BirByRefOf { Inner = OptionalField<IBirExpression>.Of(Copy(resolved)) };
            if (receiver && type is TypeNode.Tv)
            {
                var snapshot = Temporary("receiver");
                prefix.Add(new BirVariable { Name = snapshot, Type = type });
                address = new BirConditional
                {
                    Type = reference,
                    Cond = new BirNativeInstance
                    {
                        Type = new TypeNode.Fqn("System.Type"), Method = "get_IsValueType",
                        Recv = new BirClassReference { Type = type }, Args = Array.Empty<IBirExpression>(),
                        ArgTypes = Array.Empty<TypeNode>(), Ret = new TypeNode.Fqn("System.Boolean"),
                        Dispatch = "callvirt", MemberRef = isValueType,
                    },
                    Then = OptionalField<IBirExpression>.Of(address),
                    Else = new BirValueBlock
                    {
                        Stmts = new IBirStatement[] { new BirSetLocal { Name = snapshot,
                            Value = OptionalField<IBirExpression>.Of(Copy(resolved)) } },
                        Result = new BirByRefOf { Inner = new BirLocal { Name = snapshot } },
                    },
                };
            }
            var name = Temporary("address");
            prefix.Add(new BirVariable { Name = name, Type = reference, Init = OptionalField<IBirExpression>.Of(address) });
            return new BirByRefLoad { Local = name, Elem = reference is TypeNode.ByRef byref ? byref.Of : type };
        }

        IBirExpression Spill(IBirExpression expression, List<IBirStatement> prefix,
            IReadOnlyDictionary<string, TypeNode> scope)
        {
            // Q4: only literals and `this` may stay behind while a later operand
            // hoists. Even a plain local must be snapshotted: the hoisted operand
            // may write it before the original operand slot is consumed.
            if (expression is BirConstant or BirThis) return expression;
            var node = (BirGraphValue)expression;
            var type = typeOf(node, scope) ?? throw new NotSupportedException(
                $"bir2cir: physical operand normalization: the `{(node as BirGraphNode)?.Tag ?? "?"}` operand moved across a stack boundary "
                + "carries no static type, so the temporary that holds its value across the hoist would be untyped. "
                + "An earlier lowering dropped the operand's type, or its node kind needs an arm in bir-common/NodeType.cs.");
            var name = Temporary("hoist");
            prefix.Add(new BirVariable { Name = name, Type = type, Init = OptionalField<IBirExpression>.Of(expression) });
            return new BirLocal { Name = name, Sty = type };
        }

        bool WillHoist(BirGraphValue node, bool atEmpty)
        {
            if (node == null) return false;
            if (!atEmpty && RunsStackBoundary(node)) return true;
            if (node is BirValueBlock or BirConditional) return false;
            if (Plan(node, atEmpty) is { } plan)
            {
                for (var index = 0; index < plan.Operands.Length; index++)
                    if (WillHoist((BirGraphValue)plan.Operands[index].Read(), index == 0 && plan.BeginsEmpty)) return true;
                return false;
            }
            return EagerChildren(node).Any(child => WillHoist(child.Value, atEmpty));
        }

        // Generated frame roles exclude deferred bodies while retaining caller-side
        // captures. Statement arrays are separate evaluation sites, not operands.
        static BirGraphSlot[] EagerChildren(BirGraphValue node)
        {
            var lists = Lists(node);
            var statements = lists.Length == 0 ? null : lists.SelectMany(list => list.Value.OrEmpty())
                .Cast<BirGraphValue>().ToHashSet(ReferenceEqualityComparer.Instance);
            List<BirGraphSlot> children = null;
            node.VisitCurrentFrameSlots(child =>
            {
                if (child.Value is IBirExpression && statements?.Contains(child.Value) != true)
                    (children ??= new()).Add(child);
            });
            return children?.ToArray() ?? [];
        }

        static bool RunsStackBoundary(BirGraphValue node)
        {
            if (node is IBirExecutablePayload) return false;
            if (node is BirTry or BirReturn or BirReturnExpression or BirBreak or BirContinue or BirGoto) return true;
            var boundary = false;
            node.VisitCurrentFrameSlots(child => { if (!boundary) boundary = RunsStackBoundary(child.Value); });
            return boundary;
        }
    }
}
