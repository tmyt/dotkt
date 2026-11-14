using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class CharSeqStringLowering
{
    internal static BirFileDeclaration Apply(BirFileDeclaration root, IReadOnlySet<string> localTopLevelFns,
        out CharSeqRetLambdas retLambdas)
        => (BirFileDeclaration)Apply((BirGraphValue)root, localTopLevelFns, out retLambdas);

    internal static BirGraphValue Apply(BirGraphValue root, IReadOnlySet<string> localTopLevelFns)
        => Apply(root, localTopLevelFns, out _);

    internal static BirGraphValue Apply(BirGraphValue root, IReadOnlySet<string> localTopLevelFns,
        out CharSeqRetLambdas retLambdas)
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
        return WalkSource(root, new SourceEnv(), localTopLevelFns ?? new HashSet<string>(StringComparer.Ordinal), exempt);
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
            var ret = returnType == null ? RetCharSeq : IsCharSeqT(returnType) ? returnType : null;
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
                || !env.Vars.TryGetValue(local.Name.Value, out var declared) || IsStringDeclT(declared)))
            node.RewriteOwnTypeFieldsInPlace((field, _, type) => field == "sty" && IsCharSeqT(type) ? LowerTokT(type) : type);

        if (node is BirCallInstance member && IsCharSeqT(member.OwnerType.OrDefault()))
        {
            var rewritten = RewriteMemberRead(member, env);
            if (rewritten != null) return rewritten;
        }

        if (node is not BirGraphNode)
        {
            var named = SourceRecordName(node).HasValue;
            node.RewriteOwnTypeFieldsInPlace((field, _, type) =>
                (field == "ret" || field == "type" && named) && IsCharSeqT(type) ? LowerTokT(type) : type);
            LowerSourceConstraints(node);
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
            case BirNewSuspendLambda:
                LowerSourceConstraints(node);
                break;
            case BirVariable variable when IsCharSeqT(variable.Type.OrDefault()):
                var nullable = variable.Type.Value is TypeNode.Nullable;
                variable.Type = LowerTokT(variable.Type.Value);
                if (variable.Init.HasValue && CoerceOrNull(variable.Init.Value, env, nullable) is { } initializer)
                    variable.Init = ExpressionField(initializer);
                break;
            case BirCallStatic call:
                LowerLocalCall(call, env, localFns);
                break;
            case BirConditional conditional when conditional.Type.OrDefault() is TypeNode.Fqn { Name: CharSeq }
                or TypeNode.Nullable { Of: TypeNode.Fqn { Name: CharSeq } }:
                var nullableJoin = conditional.Type.Value is TypeNode.Nullable;
                conditional.Type = LowerTokT(conditional.Type.Value);
                if (conditional.Then.HasValue && CoerceOrNull(conditional.Then.Value, env, nullableJoin) is { } whenTrue)
                    conditional.Then = ExpressionField(whenTrue);
                if (conditional.Else.HasValue && CoerceOrNull(conditional.Else.Value, env, nullableJoin) is { } whenFalse)
                    conditional.Else = ExpressionField(whenFalse);
                break;
            case BirReturn statement when env.RetCharSeq != null && statement.Value.HasValue:
                if (CoerceOrNull(statement.Value.Value, env, env.RetCharSeq is TypeNode.Nullable) is { } returned)
                    statement.Value = ExpressionField(returned);
                break;
            case BirCast cast when IsCharSeqT(cast.Type.OrDefault()) && cast.E.HasValue:
                return (BirGraphValue)(CoerceOrNull(cast.E.Value, env, cast.Type.Value is TypeNode.Nullable) ?? cast.E.Value);
        }
        return node;
    }

    static void LowerSourceConstraints(BirGraphValue node)
        => node.VisitOwnChildSlots((field, _, slot) => {
            if (field != "typeParams" || slot.Value is not BirTypeParameter { Constraints.HasValue: true } parameter) return;
            var constraints = parameter.Constraints.Value;
            for (var index = 0; index < constraints.Length; index++)
                if (IsCharSeqT(constraints[index])) constraints[index] = LowerTokT(constraints[index]);
        });

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
                && CoerceOrNull(arguments[index], env, type is TypeNode.Nullable) is { } converted)
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
    {
        if (IsStaticString(value, env)) return null;
        if (!nullableTarget) return Snapshot(value);
        if (value is BirConstant constant && (!constant.Value.HasValue || constant.Value.Value is BirLiteral.NullLiteral))
        {
            constant.Type = new TypeNode.Nullable(StringTn);
            return constant;
        }
        if (BirGraphStaticType.Surface(value, env.Vars) is TypeNode.Nullable nullable && IsStringTokT(nullable.Of)) return null;
        return SnapshotNullable(value);
    }

    static BirCallStatic Snapshot(IBirExpression value) => new() {
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
    static bool IsStaticString(IBirExpression value, SourceEnv env) => value switch {
        BirConstant constant => IsStringTokT(constant.Type.OrDefault()),
        BirLocal local => local.Name.HasValue && env.Vars.TryGetValue(local.Name.Value, out var type) && IsStringTokT(type),
        BirCast cast => IsStringTokT(cast.Type.OrDefault()),
        BirConcat => true,
        BirThis => false,
        _ => IsStringTokT(OwnType((BirGraphValue)value, "ret") ?? OwnType((BirGraphValue)value, "dynRet")),
    };
}
