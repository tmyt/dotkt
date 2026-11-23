using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class CharSeqStringLowering
{
    // Local declarations own their physical call slots. A foreign method with
    // the same source signature can still require the CharSequence interface.
    internal sealed class MemberSlots
    {
        readonly Dictionary<string, (BirFunctionDeclaration Method, bool CollapsedResult)> declarations = new(StringComparer.Ordinal);
        readonly Dictionary<(string Owner, string Name), TypeNode> fields = new();
        internal void CollectFields(string owner, BirFieldDeclaration[] declared)
        {
            foreach (var field in declared)
                if (field.Type.OrDefault() is { } slot && IsStringShape(slot) && field.Name.HasValue)
                    fields[(owner, field.Name.Value)] = slot;
        }
        internal TypeNode Field(TypeNode owner, string name) => owner is TypeNode.Fqn type
            && fields.TryGetValue((type.Name, name), out var slot) ? slot : null;
        internal void Collect(BirFunctionDeclaration method, bool collapsedResult)
        {
            if (method.DeclarationId.OrDefault() is string id)
                declarations[id] = (method, collapsedResult);
        }
        internal BirParameter[] Parameters(string id) =>
            id != null && declarations.TryGetValue(id, out var declaration) ? declaration.Method.Params.OrDefault() : null;
        internal bool Bind(BirGraphValue call, IReadOnlyDictionary<string, TypeNode> vars)
        {
            var (id, sig, args) = call switch {
                BirCallInstance c => (c.DeclarationId.OrDefault(), c.Sig.OrDefault() ?? c.ShapeTypes.OrDefault(), c.Args.OrDefault()),
                BirCallStatic c => (c.DeclarationId.OrDefault(), c.Sig.OrDefault() ?? c.ShapeTypes.OrDefault(), c.Args.OrDefault()),
                _ => default,
            };
            if (id == null || !declarations.TryGetValue(id, out var declaration)) return false;
            var parameters = declaration.Method.Params.OrEmpty();
            if (sig != null && args != null && parameters.Length == sig.Length)
                for (var i = 0; i < Math.Min(sig.Length, args.Length); i++)
                    if (MatchesCollapsedSlot(sig[i], parameters[i].Type.OrDefault()))
                    {
                        var scalar = IsScalarSequence(sig[i]);
                        sig[i] = LowerTokT(sig[i]);
                        if (scalar && args[i] is { } argument && SnapshotForStringSlot(argument, vars, sig[i] is TypeNode.Nullable) is { } value)
                            args[i] = value;
                    }
            if (declaration.CollapsedResult)
                call.RewriteOwnTypeFieldsInPlace((field, _, type) =>
                    (field is "sty" or "ret" or "dynRet" or "memberReturnType") && IsCharSeqT(type) ? LowerTokT(type) : type);
            return declaration.CollapsedResult;
        }
        internal static bool BindResultBlock(BirValueBlock block)
        {
            var surface = block.Type.OrDefault() ?? block.Sty.OrDefault();
            if (surface != null && !IsCharSeqT(surface) && !IsStringShape(surface)) return false;
            block.RewriteOwnTypeFieldsInPlace((field, _, type) =>
                (field is "type" or "sty") && IsCharSeqT(type) ? LowerTokT(type) : type);
            return true;
        }
    }
    static bool IsScalarSequence(TypeNode type) => type is TypeNode.Fqn { Name: CharSeq }
        or TypeNode.Nullable { Of: TypeNode.Fqn { Name: CharSeq } };

    static bool IsStringShape(TypeNode type) => type switch {
        TypeNode.Array a => IsStringShape(a.Elem),
        TypeNode.Nullable n => IsStringShape(n.Of),
        _ => IsStringTokT(type),
    };

    // Task<T> is invariant. A virtual suspend slot can be implemented in
    // a module with real CharSequence instances, even in a string-only app.
    static bool HasOpenSuspendResult(BirGraphValue node) =>
        node is BirFunctionDeclaration method && IsScalarSequence(method.SuspendRet.OrDefault())
        && (method.Abstract.OrDefault() || method.Virtual.OrDefault() || method.Override.OrDefault());

    internal static BirFileDeclaration Apply(BirFileDeclaration root, IReadOnlySet<string> localTopLevelFns,
        out CharSeqRetLambdas retLambdas, MemberSlots results = null)
        => (BirFileDeclaration)Apply((BirGraphValue)root, localTopLevelFns, out retLambdas, results);

    internal static BirGraphValue Apply(BirGraphValue root, IReadOnlySet<string> localTopLevelFns)
        => Apply(root, localTopLevelFns, out _);

    internal static BirGraphValue Apply(BirGraphValue root, IReadOnlySet<string> localTopLevelFns,
        out CharSeqRetLambdas retLambdas, MemberSlots results = null)
    {
        // These facts describe the original callable contract, before either a
        // declaration or a structured function-type return is collapsed.
        var exempt = new HashSet<string>(StringComparer.Ordinal);
        var statics = new HashSet<string>(StringComparer.Ordinal);
        var closures = new HashSet<string>(StringComparer.Ordinal);
        void Collect(BirGraphValue node)
        {
            if (node is IBirExecutablePayload) return;
            if (node is BirNewDelegate target && target.Method.HasValue)
            {
                if (FuncTypeHasCharSeqParam(target.FuncType.OrDefault())) exempt.Add(target.Method.Value);
                if (FuncTypeHasCharSeqRet(target.FuncType.OrDefault())) statics.Add(target.Method.Value);
            }
            if (node is BirNewClosure closure && FuncTypeHasCharSeqRet(closure.FuncType.OrDefault())
                && closure.ClosureType.OrDefault() is TypeNode.Fqn owner)
                closures.Add(owner.Name);
            // delegateInvoke has no method field in the current owning vocabulary.
            node.VisitChildren(Collect);
        }
        Collect(root);
        retLambdas = new CharSeqRetLambdas(statics, closures);
        var candidates = root is BirFileDeclaration file && results != null
            ? file.Types.OrEmpty().SelectMany(t => t.Methods.OrEmpty()
                .Select(m => (Owner: t.Name.OrDefault(), Method: m, SequenceResult: IsCharSeqT(m.Ret.OrDefault())))
                .Where(c => c.SequenceResult || c.Method.Params.OrEmpty().Any(p => IsCharSeqT(p.Type.OrDefault()))))
                .ToArray() : null;
        var lowered = WalkSource(root, new SourceEnv(), localTopLevelFns ?? new HashSet<string>(StringComparer.Ordinal), exempt);
        if (lowered is BirFileDeclaration unit && results != null) {
            results.CollectFields(unit.FileClass.OrDefault(), unit.Fields.OrEmpty());
            foreach (var type in unit.Types.OrEmpty()) results.CollectFields(type.Name.OrDefault(), type.Fields.OrEmpty());
        }
        if (candidates != null)
            foreach (var (owner, method, sequenceResult) in candidates)
                results.Collect(method, sequenceResult && IsStringShape(method.Ret.OrDefault())
                    && !StringCharSequenceBridge.RestoresClosureReturn(owner, method, retLambdas));
        return lowered;
    }

    sealed class SourceEnv
    {
        internal readonly Dictionary<string, TypeNode> Vars;
        internal readonly TypeNode RetCharSeq;
        internal SourceEnv() { Vars = new(StringComparer.Ordinal); RetCharSeq = null; }
        SourceEnv(Dictionary<string, TypeNode> vars, TypeNode ret) { Vars = vars; RetCharSeq = ret; }

        internal SourceEnv WithDecl(BirGraphValue node)
        {
            if (!HasSourceParameterArray(node)) return this;
            var vars = new Dictionary<string, TypeNode>(Vars, StringComparer.Ordinal);
            node.VisitSourceParameters((name, type) => vars[name] = IsCharSeqT(type) ? LowerTokT(type) : type);
            var returnType = OwnType(node, "ret");
            var ret = returnType == null ? RetCharSeq : IsCharSeqT(returnType) && !HasOpenSuspendResult(node) ? returnType : null;
            return new SourceEnv(vars, ret);
        }

        internal SourceEnv WithVar(string name, TypeNode type)
            => new(new Dictionary<string, TypeNode>(Vars, StringComparer.Ordinal) { [name] = type }, RetCharSeq);
    }

    // Presence matters: an absent/null params field did not introduce a new
    // return environment, whereas an authored empty vector did.
    static bool HasSourceParameterArray(BirGraphValue node) => node switch {
        BirFunctionDeclaration value => value.Params.HasValue,
        BirConstructor value => value.Params.HasValue,
        IBirTypeDeclaration value => value.Params.HasValue,
        BirInheritedDefaultAccessor value => value.Params.HasValue,
        BirInheritedDefaultMethod value => value.Params.HasValue,
        BirInheritedClassMethod value => value.Params.HasValue,
        BirClosureDefinition value => value.Params.HasValue,
        BirNewSuspendLambda value => value.Params.HasValue,
        BirInlineLambda value => value.Params.HasValue,
        BirMethodImpl value => value.Params.HasValue,
        _ => false,
    };

    // This is a direct owning-record name read, not declaration resolution.
    // The old exemption applied to any untagged named record, not just methods.
    static OptionalField<string> SourceRecordName(BirGraphValue node) => node switch {
        IBirTypeDeclaration value => value.Name,
        EventPropertySourceFacts value => value.Name,
        BirCompanionCarrier value => value.Name,
        BirAttributeNamedArgument value => value.Name,
        BirParameter value => value.Name,
        BirFunctionDeclaration value => value.Name,
        BirFieldDeclaration value => value.Name,
        BirRefTypeDeclaration value => value.Name,
        BirPropertyDeclaration value => value.Name,
        BirEnumEntryDeclaration value => value.Name,
        BirKotlinCompanion value => value.Name,
        BirRichEnumEntry value => value.Name,
        BirRichEnumDeclaration value => value.Name,
        BirClrEventBacking value => value.Name,
        BirClrEventForwarder value => value.Name,
        BirCaptureDescriptor value => value.Name,
        BirClosureDefinition value => value.Name,
        BirLambdaParameter value => value.Name,
        BirInlineCapture value => value.Name,
        BirBasicEnumCarrierEntry value => value.Name,
        BirTypeParameter value => value.Name,
        BirMemberReference value => value.Name,
        _ => default,
    };

    static TypeNode OwnType(BirGraphValue node, string field)
    {
        TypeNode found = null;
        node.VisitOwnTypeFields((name, type) => { if (name == field) found = type; });
        return found;
    }

    static BirGraphValue WalkSource(BirGraphValue node, SourceEnv env, IReadOnlySet<string> localFns,
        IReadOnlySet<string> exempt)
    {
        if (node is IBirExecutablePayload) return node;
        if (node is not BirGraphNode && SourceRecordName(node) is { HasValue: true } name && exempt.Contains(name.Value))
            return node;
        var childEnv = env.WithDecl(node);
        node.VisitSourceChildren(slot => slot.Replace(WalkSource(slot.Value, childEnv, localFns, exempt)), region => {
            // The original valueBlock rule snapshots all source var types for
            // its result before walking statements. Other paired regions (an
            // inlineLambda body/result) do not inherit that special rule.
            var resultEnv = childEnv;
            if (node is BirValueBlock)
                foreach (var statement in region.Statements)
                    if (statement is BirVariable { Name.HasValue: true, Type.HasValue: true } resultVariable)
                        resultEnv = resultEnv.WithVar(resultVariable.Name.Value,
                            IsCharSeqT(resultVariable.Type.Value) ? LowerTokT(resultVariable.Type.Value) : resultVariable.Type.Value);
            var current = childEnv;
            for (var index = 0; index < region.Statements.Length; index++)
            {
                var statement = (IBirStatement)WalkSource((BirGraphValue)region.Statements[index], current, localFns, exempt);
                region.Statements[index] = statement;
                if (statement is BirVariable { Name.HasValue: true, Type.HasValue: true } variable)
                    current = current.WithVar(variable.Name.Value,
                        IsCharSeqT(variable.Type.Value) ? LowerTokT(variable.Type.Value) : variable.Type.Value);
            }
            if (region.Result is { } result)
                result.Replace(WalkSource(result.Value, resultEnv, localFns, exempt));
        });
        // Structured TypeNodes were visited by the old DOM walk too. In
        // particular Fn/FnPtr own a ret field; lowering that return is distinct
        // from globally rewriting their argument/receiver classifiers. Encoded
        // frozen facts are deliberately absent from this generated field API.
        node.RewriteOwnTypeFieldsInPlace((_, _, type) => LowerFunctionReturns(type, exempt));
        return TransformSource(node, env, localFns);
    }

    static TypeNode LowerFunctionReturns(TypeNode type) => LowerFunctionReturns(type, null);

    static TypeNode LowerFunctionReturns(TypeNode type, IReadOnlySet<string> exempt)
    {
        // A structured Fqn is also an untagged named object in the original
        // walk. Preserve that same subtree exemption, not just graph records.
        if (type is TypeNode.Fqn named && exempt?.Contains(named.Name) == true) return type;
        TypeNode Walk(TypeNode value) => LowerFunctionReturns(value, exempt);
        TypeNode Return(TypeNode value)
        {
            var result = Walk(value);
            return IsCharSeqT(result) ? LowerTokT(result) : result;
        }
        return type switch {
        TypeNode.Fqn value => value with {
            Args = value.Args?.Select(Walk).ToArray(),
            Classifier = value.Classifier is { } source ? source with { OwnerPath = source.OwnerPath.ToArray() } : null,
        },
        TypeNode.Fn value => value with {
            Ret = Return(value.Ret), Params = value.Params.Select(Walk).ToArray(),
            Recv = value.Recv == null ? null : Walk(value.Recv), Ctx = value.Ctx?.Select(Walk).ToArray(),
        },
        TypeNode.FnPtr value => value with {
            Ret = Return(value.Ret), Params = value.Params.Select(Walk).ToArray(),
        },
        TypeNode.Projection value => value with { Of = Walk(value.Of) },
        TypeNode.Nullable value => value with { Of = Walk(value.Of) },
        TypeNode.Oblivious value => value with { Of = Walk(value.Of) },
        TypeNode.Array value => value with { Elem = Walk(value.Elem) },
        TypeNode.ByRef value => value with { Of = Walk(value.Of) },
        TypeNode.Ptr value => value with { Of = Walk(value.Of) },
        TypeNode.Mod value => value with { M = Walk(value.M), Of = Walk(value.Of) },
        TypeNode.Tv or TypeNode.Star => type,
        null => null,
        _ => throw new InvalidOperationException("Unaccounted CharSequence structural type: " + type.GetType().Name),
        };
    }

    static BirGraphValue TransformSource(BirGraphValue node, SourceEnv env, IReadOnlySet<string> localFns)
    {
        if (node is BirFieldGet or BirLateinitGet or BirStaticFieldGet
            || node is BirLocal local && (!local.Name.HasValue
                || !env.Vars.TryGetValue(local.Name.Value, out var declared) || IsStringShape(declared)))
            node.RewriteOwnTypeFieldsInPlace((field, _, type) => field == "sty"
                && (node is BirLocal ? IsCharSeqT(type) : IsScalarSequence(type)) ? LowerTokT(type) : type);

        if (node is BirCallInstance member && IsScalarSequence(member.OwnerType.OrDefault()))
        {
            var rewritten = RewriteMemberRead(member, env);
            if (rewritten != null) return rewritten;
        }

        if (node is not BirGraphNode)
        {
            var named = SourceRecordName(node).HasValue;
            var openSuspendResult = HasOpenSuspendResult(node);
            node.RewriteOwnTypeFieldsInPlace((field, _, type) =>
                (field == "ret" || field == "type" && named) && IsCharSeqT(type) ? LowerTokT(type) : type);
            if (node is BirFunctionDeclaration method && IsScalarSequence(method.SuspendRet.OrDefault())
                && IsStringDeclT(method.Ret.OrDefault()))
            {
                if (openSuspendResult && !method.SuspendTaskResult.IsPresent)
                    method.SuspendTaskResult = BirGraphValue.CopyType(method.SuspendRet.Value);
                method.SuspendRet = openSuspendResult ? new TypeNode.Fqn("object") : BirGraphValue.CopyType(method.Ret.Value);
            }
            node.VisitOwnChildSlots((field, _, slot) => {
                if (field != "params") return;
                switch (slot.Value)
                {
                    case BirParameter parameter when IsCharSeqT(parameter.Type.OrDefault()):
                        parameter.Type = LowerTokT(parameter.Type.Value); break;
                    case BirLambdaParameter lambdaParameter when IsCharSeqT(lambdaParameter.Type.OrDefault()):
                        lambdaParameter.Type = LowerTokT(lambdaParameter.Type.Value); break;
                }
            });
            return node;
        }
        switch (node)
        {
            case BirVariable variable when IsCharSeqT(variable.Type.OrDefault()):
                var slot = variable.Type.Value;
                variable.Type = LowerTokT(variable.Type.Value);
                if (variable.Init.HasValue && SnapshotAtSlot(variable.Init.Value, env, slot) is { } initializer)
                    variable.Init = ExpressionField(initializer);
                break;
            case BirCallStatic call:
                LowerLocalCall(call, env, localFns);
                break;
            case BirConditional conditional when IsScalarSequence(conditional.Type.OrDefault()):
                var nullableJoin = conditional.Type.Value is TypeNode.Nullable;
                conditional.Type = LowerTokT(conditional.Type.Value);
                if (conditional.Then.HasValue && CoerceOrNull(conditional.Then.Value, env, nullableJoin) is { } whenTrue)
                    conditional.Then = ExpressionField(whenTrue);
                if (conditional.Else.HasValue && CoerceOrNull(conditional.Else.Value, env, nullableJoin) is { } whenFalse)
                    conditional.Else = ExpressionField(whenFalse);
                break;
            case BirReturn statement when env.RetCharSeq != null && statement.Value.HasValue:
                if (SnapshotAtSlot(statement.Value.Value, env, env.RetCharSeq) is { } returned)
                    statement.Value = ExpressionField(returned);
                break;
            case BirCast cast when IsScalarSequence(cast.Type.OrDefault()) && cast.E.HasValue:
                return (BirGraphValue)(CoerceOrNull(cast.E.Value, env, cast.Type.Value is TypeNode.Nullable) ?? cast.E.Value);
            case BirCast cast when IsCharSeqT(cast.Type.OrDefault()):
                cast.Type = LowerTokT(cast.Type.Value);
                break;
        }
        return node;
    }

    static void LowerLocalCall(BirCallStatic call, SourceEnv env, IReadOnlySet<string> localFns)
    {
        if (call.Owner.OrDefault() is TypeNode.Fqn { Name: not null }
            || !call.Method.HasValue || !localFns.Contains(call.Method.Value) || !call.Sig.HasValue) return;
        var signature = call.Sig.Value;
        var arguments = call.Args.OrDefault();
        for (var index = 0; index < signature.Length; index++)
        {
            var type = signature[index];
            if (!IsCharSeqT(type)) continue;
            signature[index] = LowerTokT(type);
            if (arguments != null && index < arguments.Length
                && SnapshotAtSlot(arguments[index], env, type) is { } converted)
                arguments[index] = converted;
        }
        call.RewriteOwnTypeFieldsInPlace((field, _, type) =>
            (field is "sty" or "ret" or "dynRet" or "memberReturnType") && IsCharSeqT(type) ? LowerTokT(type) : type);
    }

    static BirGraphValue RewriteMemberRead(BirCallInstance call, SourceEnv env)
    {
        var receiver = call.Recv.OrDefault();
        if (receiver != null) receiver = CoerceOrNull(receiver, env, nullableTarget: false) ?? receiver;
        var arguments = call.Args.OrEmpty();
        switch (call.Method.OrDefault())
        {
            case "length" when call.Prop.OrDefault() == "get":
                return new BirPropertyGet { Type = SourceType("System.String"), Name = "Length",
                    Ret = SourceType("System.Int32"), Static = false, Recv = ExpressionField(receiver) };
            case "get":
                return new BirNativeInstance { Type = SourceType("System.String"), Method = "get_Chars",
                    ArgTypes = new[] { SourceType("System.Int32") }, Ret = SourceType("System.Char"),
                    Recv = ExpressionField(receiver), Args = new IBirExpression[] { arguments.Length > 0 ? arguments[0] : null } };
            case "subSequence" when arguments.Length >= 2:
                var id = Interlocked.Increment(ref _subSeqTmp);
                var receiverName = "$subSeqRecv$" + id;
                var startName = "$subSeqStart$" + id;
                return new BirValueBlock {
                    Stmts = new IBirStatement[] {
                        new BirVariable { Name = receiverName, Type = SourceType("System.String"), Init = ExpressionField(receiver) },
                        new BirVariable { Name = startName, Type = SourceType("System.Int32"), Init = ExpressionField(arguments[0]) },
                    },
                    Result = ExpressionField(new BirNativeInstance {
                        Type = SourceType("System.String"), Method = "Substring", Ret = SourceType("System.String"),
                        ArgTypes = new[] { SourceType("System.Int32"), SourceType("System.Int32") },
                        Recv = ExpressionField(ReadSource(receiverName)),
                        Args = new IBirExpression[] { ReadSource(startName), new BirBinaryOperation {
                            Op = "-", Lhs = ExpressionField(arguments[1]), Rhs = ExpressionField(ReadSource(startName)),
                        } },
                    }),
                };
            default:
                return null;
        }
    }

    static OptionalField<IBirExpression> ExpressionField(IBirExpression expression)
        => expression == null ? OptionalField<IBirExpression>.Null : OptionalField<IBirExpression>.Of(expression);
    static TypeNode SourceType(string name) => new TypeNode.Fqn(name);
    static BirLocal ReadSource(string name) => new() { Name = name };

    static IBirExpression CoerceOrNull(IBirExpression value, SourceEnv env, bool nullableTarget)
        => SnapshotForStringSlot(value, env.Vars, nullableTarget);

    static IBirExpression SnapshotAtSlot(IBirExpression value, SourceEnv env, TypeNode slot)
        => IsScalarSequence(slot) ? CoerceOrNull(value, env, slot is TypeNode.Nullable) : null;

    internal static void BindArrayElement(BirGraphValue node, IReadOnlyDictionary<string, TypeNode> vars)
    {
        var array = node switch {
            BirArraySet s => s.Array.OrDefault(), BirArrayGet g => g.Array.OrDefault(), BirForArray f => f.Array.OrDefault(), _ => null,
        };
        if (array == null || OwnType(node, "elem") is not { } logical || !IsCharSeqT(logical)
            || !MatchesCollapsedSlot(logical, ArrayConstructionLowering.ArrayElementOf(BirGraphStaticType.Surface(array, vars)))) return;
        if (node is BirArraySet set && IsScalarSequence(logical) && set.Value.HasValue
            && SnapshotForStringSlot(set.Value.Value, vars, logical is TypeNode.Nullable) is { } item)
            set.Value = ExpressionField(item);
        node.RewriteOwnTypeFieldsInPlace((field, _, type) => field == "elem" ? LowerTokT(type) : type);
    }

    internal static bool MatchesCollapsedSlot(TypeNode source, TypeNode target) => (source, target) switch {
        (TypeNode.Nullable s, _) => MatchesCollapsedSlot(s.Of, target),
        (_, TypeNode.Nullable t) => MatchesCollapsedSlot(source, t.Of),
        (TypeNode.Array s, TypeNode.Array t) => MatchesCollapsedSlot(s.Elem, t.Elem),
        _ => IsScalarSequence(source) && IsStringTokT(target),
    };

    // Array factories are already bound here. Select the component of a *fresh*
    // vector at its use site; an existing array must never be replaced or copied.
    internal static IBirExpression SelectFreshArray(IBirExpression value, TypeNode target, IReadOnlyDictionary<string, TypeNode> vars)
    {
        if (value == null || ArrayConstructionLowering.ArrayElementOf(target) is not { } element || !IsStringShape(element)) return value;
        var node = (BirGraphValue)value;
        if (node is BirValueBlock or BirConditional)
            node.RewriteOwnTypeFieldsInPlace((field, _, type) =>
                field is "type" or "sty" && MatchesCollapsedSlot(type, target) ? LowerTokT(type) : type);
        if (node is BirValueBlock block && block.Result.HasValue) {
            block.Result = ExpressionField(SelectFreshArray(block.Result.Value, target, vars)); return block;
        }
        if (node is BirConditional cond) {
            if (cond.Then.HasValue) cond.Then = ExpressionField(SelectFreshArray(cond.Then.Value, target, vars));
            if (cond.Else.HasValue) cond.Else = ExpressionField(SelectFreshArray(cond.Else.Value, target, vars));
            return cond;
        }
        if (node is not (BirNewArray or BirNewArrayInit or BirNewArraySized or BirSpreadConcat)
            || OwnType(node, "elem") is not { } logical || !MatchesCollapsedSlot(logical, element)) return value;
        node.RewriteOwnTypeFieldsInPlace((field, _, type) => field == "elem" ? LowerTokT(type) : type);
        IBirExpression Item(IBirExpression item) {
            if (IsScalarSequence(logical)) return SnapshotForStringSlot(item, vars, logical is TypeNode.Nullable) ?? item;
            return SelectFreshArray(item, element, vars);
        }
        if (node is BirNewArrayInit init && IsScalarSequence(logical)
            && (init.FuncType.OrDefault() ?? ((BirGraphValue)init.Init.Value).FunctionTypeStamp
                ?? BirGraphStaticType.Surface(init.Init.Value, vars)) is TypeNode.Fn signature)
            return SnapshotVector(element, init.Size.Value, init.Init.Value, signature, null, Item);
        if (node is BirNewArray literal && literal.Elems.OrDefault() is { } items)
            for (var i = 0; i < items.Length; i++) items[i] = Item(items[i]);
        if (node is BirSpreadConcat concat)
            foreach (var part in concat.Parts.OrEmpty())
                if (part.Spread.OrDefault() && part.E.HasValue) {
                    var spread = SelectFreshArray(part.E.Value, target, vars);
                    var source = BirGraphStaticType.Surface(spread, vars);
                    if (IsScalarSequence(logical) && ArrayConstructionLowering.ArrayElementOf(source) is { } component
                        && !IsStringShape(component))
                        spread = SnapshotVector(element, null, spread, source, component, Item);
                    part.E = ExpressionField(spread);
                }
                else if (part.E.HasValue) part.E = ExpressionField(Item(part.E.Value));
        return value;
    }

    // Kotlin requests a fresh vector here. Only its individual elements cross
    // the scalar boundary; both the source and the initializer are evaluated once.
    static BirValueBlock SnapshotVector(TypeNode element, IBirExpression count, IBirExpression input,
        TypeNode inputType, TypeNode spreadElement, Func<IBirExpression, IBirExpression> item)
    {
        var id = Interlocked.Increment(ref _nullableSnapshotTmp);
        var integer = SourceType("System.Int32");
        BirVariable Var(string name, TypeNode type, IBirExpression init) =>
            new() { Name = "$snapshotArray$" + id + name, Type = type, Init = ExpressionField(init) };
        BirLocal Read(BirVariable v) => new() { Name = v.Name, Sty = v.Type };
        var vector = new TypeNode.Array(element);
        var src = Var("input", inputType, input);
        var size = Var("size", integer, count ?? new BirArrayLength { Array = Read(src) });
        var result = Var("result", vector, new BirNewArraySized { Elem = element, Size = Read(size) });
        var ordinal = "$snapshotArrayIndex$" + id;
        BirLocal Index() => new() { Name = ordinal, Sty = integer };
        IBirExpression raw = spreadElement != null
            ? new BirArrayGet { Array = Read(src), Index = Index(), Elem = spreadElement }
            : new BirDelegateInvoke { Recv = Read(src), FuncType = inputType, Args = new IBirExpression[] { Index() } };
        var fill = new BirFor { Var = ordinal, From = new BirConstant { Type = integer, Value = new BirLiteral.IntegerLiteral(0) },
            To = Read(size), Cmp = "<", Step = 1, Body = new IBirStatement[] {
                new BirExpressionStatement { Expr = new BirArraySet { Array = Read(result), Index = Index(),
                    Elem = element, Value = ExpressionField(item(raw)) } } } };
        return new BirValueBlock { Type = vector, Stmts = count != null ? new IBirStatement[] { size, src, result, fill }
            : new IBirStatement[] { src, size, result, fill }, Result = Read(result) };
    }

    internal static IBirExpression SnapshotForStringSlot(IBirExpression value, IReadOnlyDictionary<string, TypeNode> vars, bool nullableTarget)
    {
        if (IsStaticString(value, vars)) return null;
        if (!nullableTarget) return Snapshot(value);
        if (value is BirConstant constant && (!constant.Value.HasValue || constant.Value.Value is BirLiteral.NullLiteral))
        {
            constant.Type = new TypeNode.Nullable(StringTn);
            return constant;
        }
        if (BirGraphStaticType.Surface(value, vars) is TypeNode.Nullable nullable && IsStringTokT(nullable.Of)) return null;
        return SnapshotNullable(value);
    }

    internal static BirCallStatic Snapshot(IBirExpression value) => new() {
        Owner = SourceType("kotlin.LibraryKt"), Method = "toString", Sig = new[] { SourceType("object") },
        Ret = StringTn, Args = new[] { value },
    };

    static IBirExpression SnapshotNullable(IBirExpression value)
    {
        IBirExpression read;
        BirVariable temporary = null;
        var graph = (BirGraphValue)value;
        var reReadable = graph is BirGraphNode tagged && ValueStability.IsReReadable(tagged.Tag,
            graph is BirEnumValue { Underlying.HasValue: true, PhysicalValue.HasValue: true });
        if (reReadable) read = value;
        else
        {
            var name = "__cssnapshot$" + Interlocked.Increment(ref _nullableSnapshotTmp);
            temporary = new BirVariable { Name = name, Type = SourceType("System.Object"), Init = ExpressionField(value) };
            read = ReadSource(name);
        }
        var nullableString = new TypeNode.Nullable(StringTn);
        var core = new BirConditional {
            Type = nullableString,
            Cond = ExpressionField(new BirBinaryOperation {
                Op = "===", Lhs = ExpressionField((IBirExpression)((BirGraphValue)read).DeepClone()),
                Rhs = ExpressionField(new BirConstant { Type = SourceType("System.Object"), Value = new BirLiteral.NullLiteral() }),
            }),
            Then = ExpressionField(new BirConstant { Type = nullableString, Value = new BirLiteral.NullLiteral() }),
            Else = ExpressionField(Snapshot((IBirExpression)((BirGraphValue)read).DeepClone())),
        };
        return temporary == null ? core : new BirValueBlock { Stmts = new IBirStatement[] { temporary }, Result = ExpressionField(core) };
    }

    // Positive String recognition is deliberately narrower than Surface:
    // a local uses its lexical declaration, and other values use ret/dynRet,
    // never a convenient sty that could describe an uncollapsed adapter.
    static bool IsStaticString(IBirExpression value, IReadOnlyDictionary<string, TypeNode> vars) => value switch {
        BirConstant constant => IsStringTokT(constant.Type.OrDefault()),
        BirLocal local => local.Name.HasValue && vars.TryGetValue(local.Name.Value, out var type) && IsStringTokT(type),
        BirCast cast => IsStringTokT(cast.Type.OrDefault()),
        BirConcat => true,
        BirThis => false,
        _ => IsStringTokT(OwnType((BirGraphValue)value, "ret") ?? OwnType((BirGraphValue)value, "dynRet")),
    };
}
