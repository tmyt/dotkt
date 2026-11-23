using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class StringCharSequenceBridge
{
    internal sealed record SourceBridgeResult(IReadOnlyList<BirClassDefinition> AppendedTypes,
        IReadOnlyList<ClosureSynthesis.MaterializedOccurrence> DelegateAdapters);

    // A Kotlin CharSequence witness can be either a CLR string or an implementer of
    // the Kotlin interface. Neither is a valid CLR constraint on their union.
    internal static BirFileDeclaration ProjectGenericBounds(BirFileDeclaration file) =>
        (BirFileDeclaration)SourceConstraintErasure.Apply(file, ProjectBounds, includeEmptyBounds: false);

    static (TypeNode[] Retained, bool Changed) ProjectBounds(TypeNode[] bounds)
    {
        var physical = bounds.Where(bound => !IsGenericCharSequenceBound(bound)).ToArray();
        return (physical, physical.Length != bounds.Length);
    }

    internal static bool IsGenericCharSequenceBound(TypeNode bound) => bound switch {
        TypeNode.Fqn { Name: "kotlin.CharSequence" or CharSeq } => true,
        TypeNode.Nullable optional => IsGenericCharSequenceBound(optional.Of),
        _ => false,
    };

    internal static IReadOnlyList<BirClassDefinition> ApplyMaterializedReturns(
        BirFileDeclaration file, BirFunctionDeclaration method)
    {
        if (!IsCharSeqT(method.Ret.OrDefault()) || !method.Body.HasValue)
            return Array.Empty<BirClassDefinition>();
        _fired = false;
        var position = file.Pos.OrDefault();
        var context = new ClosureSynthesis.CaptureContext("<file>", position?.F.OrDefault(),
            position?.L.HasValue == true ? position.L.Value : null);
        WalkSourceBridge(new BirGraphSlot(() => method,
            _ => throw new InvalidOperationException("Return adaptation cannot replace its method")),
            new SourceBridgeEnv(context), new(), returnsOnly: true);
        MaterializedExecutable.NormalizeSource(method);
        return _fired ? EmitSourceAdapter(file) : Array.Empty<BirClassDefinition>();
    }

    internal static IReadOnlyList<BirClassDefinition> EmitPendingAdapter(BirFileDeclaration file) =>
        _fired && !_adapterEmitted ? EmitSourceAdapter(file) : Array.Empty<BirClassDefinition>();

    static IReadOnlyList<BirClassDefinition> EmitSourceAdapter(BirFileDeclaration file)
    {
        if (_adapterEmitted) return Array.Empty<BirClassDefinition>();
        var adapter = BuildAdapterDeclaration();
        file.Types = file.Types.OrEmpty().Append(adapter).ToArray();
        _adapterEmitted = true;
        return new[] { adapter };
    }

    internal delegate TypeNode[] ConstructorParameters(TypeNode.Fqn owner, string declaration, bool includeReferenceCapture);
    internal static ConstructorParameters ConstructorSlots(IReadOnlyList<BirFileDeclaration> files, ReferenceMetadataIndex refs)
    {
        // Read actual parameter slots after local String collapse; a use-site
        // signature still describes the original Kotlin declaration.
        var local = new Dictionary<(string, string), TypeNode[]>();
        var innerOrder = new InnerApplicationOrder(files, refs);
        foreach (var file in files)
            foreach (var type in file.Types.OrEmpty())
                foreach (var ctor in type.Ctors.OrEmpty())
                    if (ctor.DeclarationId.OrDefault() is string id && ctor.Params.OrDefault() is { } parameters
                        && parameters.Any(p => PotentialView(p.Type.OrDefault())))
                        local[(type.Name.Value, id)] = parameters.Select(p => p.Type.OrDefault()).ToArray();
        return (owner, id, includeReferenceCapture) => {
            if (owner == null) return null;
            if (id != null && local.TryGetValue((owner.Name, id), out var slots))
                return innerOrder.DeclarationArguments(owner) is { Length: > 0 } args
                    ? slots.Select(slot => SubstCallTvs(slot, args, Array.Empty<TypeNode>())).ToArray() : slots;
            // Reflection supplies only the CLR type of a generated capture,
            // not its Kotlin nullability. Preserve null when adapting it.
            return !includeReferenceCapture ? null : refs?.OwnerCtorParamTypes(owner.Name)?.Select(slot =>
                IsScalarCharSequence(slot) || IsSourceCharSeqT(slot) ? new TypeNode.Nullable(slot) : slot).ToArray();
        };
        static bool PotentialView(TypeNode type) => type switch {
            TypeNode.Tv { Scope: "type" } => true,
            TypeNode.Nullable nullable => PotentialView(nullable.Of),
            TypeNode.Oblivious platform => PotentialView(platform.Of),
            _ => IsScalarCharSequence(type) || IsStringTokT(type),
        };
    }

    // This entry owns the continuing source graph. Newly authored callback
    // ingredients are returned by their actual slots, not detached snapshots.
    internal static SourceBridgeResult Apply(BirFileDeclaration file, ConstructorParameters constructorSlots,
        CharSeqStringLowering.CharSeqRetLambdas retLambdas, CharSeqStringLowering.MemberSlots results = null)
    {
        _retLambdas = retLambdas;
        _fired = false;
        if (retLambdas != null) SourceRetypeLiftedRets(file, retLambdas);
        var materialized = new List<ClosureSynthesis.MaterializedOccurrence>();
        var position = file.Pos.OrDefault();
        var context = new ClosureSynthesis.CaptureContext("<file>", position?.F.OrDefault(),
            position?.L.HasValue == true ? position.L.Value : null);
        WalkSourceBridge(new BirGraphSlot(() => file, _ => throw new InvalidOperationException("A bridge cannot replace its file")),
            new SourceBridgeEnv(context, constructorSlots), materialized, results: results);
        IReadOnlyList<BirClassDefinition> appended = Array.Empty<BirClassDefinition>();
        if (!_adapterEmitted && (_fired || SourceUsesCharSequenceView(file))) appended = EmitSourceAdapter(file);
        return new SourceBridgeResult(appended, materialized);
    }

    sealed class SourceBridgeEnv
    {
        internal IReadOnlyDictionary<string, TypeNode> Vars { get; }
        internal TypeNode RetType { get; }
        internal (TypeNode.Fqn Self, TypeNode.Fqn Base) Owner { get; }
        internal ClosureSynthesis.CaptureContext Context { get; }
        internal ConstructorParameters ConstructorSlots { get; }
        internal SourceBridgeEnv(ClosureSynthesis.CaptureContext context, ConstructorParameters constructorSlots = null)
            : this(new Dictionary<string, TypeNode>(StringComparer.Ordinal), null, default, context, constructorSlots) { }
        SourceBridgeEnv(IReadOnlyDictionary<string, TypeNode> vars, TypeNode ret,
            (TypeNode.Fqn, TypeNode.Fqn) owner, ClosureSynthesis.CaptureContext context, ConstructorParameters constructorSlots)
        { Vars = vars; RetType = ret; Owner = owner; Context = context; ConstructorSlots = constructorSlots; }

        internal SourceBridgeEnv WithDecl(BirGraphValue node)
        {
            var context = ClosureSynthesis.ContextFor(node, Context);
            if (node is IBirTypeDeclaration type && type.Kind.OrDefault() is "class" or "interface" or "struct" or "enum")
                return new(Vars, RetType, (new TypeNode.Fqn(type.Name.Value), type.Base.OrDefault() as TypeNode.Fqn), context, ConstructorSlots);
            var parametersPresent = node switch {
                BirFunctionDeclaration value => value.Params.HasValue,
                BirConstructor value => value.Params.HasValue,
                BirClosureDefinition value => value.Params.HasValue,
                BirNewSuspendLambda value => value.Params.HasValue,
                BirInlineLambda value => value.Params.HasValue,
                BirInheritedClassMethod value => value.Params.HasValue,
                _ => false,
            };
            if (!parametersPresent) return new(Vars, RetType, Owner, context, ConstructorSlots);
            var vars = new Dictionary<string, TypeNode>(Vars, StringComparer.Ordinal);
            node.VisitSourceParameters((name, type) => vars[name] = type);
            var ret = SourceBridgeOwnType(node, "ret");
            return new(vars, ret ?? RetType, Owner, context, ConstructorSlots);
        }

        internal SourceBridgeEnv WithVar(string name, TypeNode type)
            => new(new Dictionary<string, TypeNode>(Vars, StringComparer.Ordinal) { [name] = type }, RetType, Owner, Context, ConstructorSlots);
    }

    static TypeNode SourceBridgeOwnType(BirGraphValue node, string field)
    {
        TypeNode result = null;
        node.VisitOwnTypeFields((name, type) => { if (name == field) result = type; });
        return result;
    }

    // Report a selected collapsed result to its enclosing evaluation block,
    // whose source stamp otherwise hides the physical result from its caller.
    static bool WalkSourceBridge(BirGraphSlot slot, SourceBridgeEnv env,
        List<ClosureSynthesis.MaterializedOccurrence> materialized, bool returnsOnly = false, CharSeqStringLowering.MemberSlots results = null)
    {
        var node = slot.Value;
        if (node == null || node is IBirExecutablePayload) return false;
        var childEnv = env.WithDecl(node);
        var collapsedResult = false;
        node.VisitSourceChildren(child => WalkSourceBridge(child, childEnv, materialized, returnsOnly, results), region => {
            // Preserve the original valueBlock-only snapshot: its result sees
            // all declared local types, but a variable's initializer does not
            // see itself. Other regions do not acquire this sibling scope.
            var resultEnv = childEnv;
            if (node is BirValueBlock)
                foreach (var statement in region.Statements)
                    if (statement is BirVariable { Name.HasValue: true, Type.HasValue: true } variable)
                        resultEnv = resultEnv.WithVar(variable.Name.Value, variable.Type.Value);
            var current = childEnv;
            for (var index = 0; index < region.Statements.Length; index++)
            {
                var at = index;
                WalkSourceBridge(new BirGraphSlot(() => (BirGraphValue)region.Statements[at],
                    value => region.Statements[at] = (IBirStatement)value), current, materialized, returnsOnly, results);
                if (region.Statements[index] is BirVariable { Name.HasValue: true, Type.HasValue: true } variable)
                    current = current.WithVar(variable.Name.Value, variable.Type.Value);
            }
            if (region.Result is { } result && WalkSourceBridge(result, resultEnv, materialized, returnsOnly, results)
                && node is BirValueBlock block)
                collapsedResult = CharSeqStringLowering.MemberSlots.BindResultBlock(block);
        });
        if (returnsOnly && node is not BirReturn) return false;
        if (node is BirCallStatic or BirCallInstance) collapsedResult = results?.Bind(node, env.Vars) == true;
        if (node is BirConstructor constructor) SourceWrapDelegationArgs(constructor, childEnv);
        var replacement = SourceBridgeTransform(node, env, materialized);
        if (!ReferenceEquals(replacement, node)) slot.Replace(replacement);
        return collapsedResult;
    }

    static BirGraphValue SourceBridgeTransform(BirGraphValue node, SourceBridgeEnv env,
        List<ClosureSynthesis.MaterializedOccurrence> materialized)
    {
        switch (node)
        {
            case BirNewSuspendLambda suspend when suspend.TypeParams.HasValue:
                // An inline default may have materialized a donor frame after the
                // original declarations were projected. These are physical helper slots.
                foreach (var parameter in suspend.TypeParams.Value)
                    if (parameter.Constraints.OrDefault() is { } bounds && ProjectBounds(bounds) is (var retained, true))
                        parameter.Constraints = retained;
                break;
            case BirArraySet set when IsScalarCharSequence(set.Elem.OrDefault()) && set.Value.HasValue:
                set.Value = OptionalField<IBirExpression>.Of(SourceCoerceCharSeqArg(set.Value.Value, env, set.Elem.Value));
                break;
            case BirNewArray array when IsScalarCharSequence(array.Elem.OrDefault()) && array.Elems.HasValue:
                for (var i = 0; i < array.Elems.Value.Length; i++)
                    array.Elems.Value[i] = SourceCoerceCharSeqArg(array.Elems.Value[i], env, array.Elem.Value);
                break;
            case BirCallStatic call:
                SourceWrapCallArgs(call, env, materialized); break;
            case BirCallInstance call:
                if (call.OwnerType.OrDefault() is TypeNode.Fqn { Name: CharSeq } && call.Recv.HasValue)
                    call.Recv = OptionalField<IBirExpression>.Of(SourceCoerceCharSeqArg(call.Recv.Value, env, call.OwnerType.Value));
                SourceWrapCallArgs(call, env, materialized); break;
            case BirConditional conditional:
                if (conditional.Type.OrDefault() is TypeNode.Fqn { Name: CharSeq }
                    or TypeNode.Nullable { Of: TypeNode.Fqn { Name: CharSeq } })
                    SourceCoerceCharSeqArg(conditional, env, conditional.Type.Value);
                break;
            case BirDelegateInvoke invoke:
                if (invoke.FuncType.OrDefault() is TypeNode.Fn function && invoke.Args.HasValue
                    && invoke.Args.Value.Length == function.DelegateParams.Length)
                    for (var index = 0; index < invoke.Args.Value.Length; index++)
                        if (IsCharSeqT(function.DelegateParams[index]) && invoke.Args.Value[index] is { } argument)
                            invoke.Args.Value[index] = SourceCoerceCharSeqArg(argument, env, function.DelegateParams[index]);
                break;
            case BirNewDelegate or BirNewClosure:
                SourceSyncDelegateFuncRet(node); break;
            case BirNew construction:
                SourceWrapNewCtorArgs(construction, env); break;
            case BirVariable variable when variable.Type.HasValue && IsCharSeqT(variable.Type.Value) && variable.Init.HasValue:
                variable.Init = OptionalField<IBirExpression>.Of(SourceCoerceCharSeqArg(variable.Init.Value, env, variable.Type.Value));
                break;
            case BirSetLocal store when store.Name.HasValue && store.Value.HasValue
                && env.Vars.TryGetValue(store.Name.Value, out var target) && IsCharSeqT(target):
                store.Value = OptionalField<IBirExpression>.Of(SourceCoerceCharSeqArg(store.Value.Value, env, target));
                break;
            case BirReturn statement when IsCharSeqT(env.RetType) && statement.Value.HasValue:
                statement.Value = OptionalField<IBirExpression>.Of(SourceCoerceCharSeqArg(statement.Value.Value, env, env.RetType));
                break;
            case BirCast cast:
                return SourceWrapCast(cast, env);
        }
        return node;
    }

    static void SourceWrapDelegationArgs(BirConstructor constructor, SourceBridgeEnv env)
    {
        if (!constructor.DelegationSig.HasValue) return;
        var arguments = constructor.ThisArgs.OrDefault() ?? constructor.BaseArgs.OrDefault();
        if (arguments == null) return;
        var owner = constructor.ThisArgs.HasValue ? env.Owner.Self : env.Owner.Base;
        var slots = env.ConstructorSlots?.Invoke(owner, constructor.DelegationDeclarationId.OrDefault(), includeReferenceCapture: false);
        // Local delegation uses the selected declaration after String collapse.
        // Otherwise keep the open signature, closing base arguments for super calls.
        var parameters = slots ?? constructor.DelegationSig.Value;
        var ownerArgs = owner?.Args ?? Array.Empty<TypeNode>();
        for (var index = 0; index < Math.Min(arguments.Length, parameters.Length); index++)
        {
            if (arguments[index] is not { } argument) continue;
            var type = slots == null ? SubstCallTvs(parameters[index], ownerArgs, Array.Empty<TypeNode>()) : parameters[index];
            if (IsCharSeqT(type))
                arguments[index] = SourceCoerceCharSeqArg(argument, env, type);
            else if (index < constructor.DelegationSig.Value.Length && IsCharSeqT(constructor.DelegationSig.Value[index])
                && (IsStringTokT(type) || type is TypeNode.Nullable nullable && IsStringTokT(nullable.Of)))
                arguments[index] = CharSeqStringLowering.SnapshotForStringSlot(argument, env.Vars, type is TypeNode.Nullable) ?? argument;
        }
    }

    static IBirExpression[] SourceBridgeArguments(BirGraphValue node) => node switch {
        BirCallStatic value => value.Args.OrDefault(), BirCallInstance value => value.Args.OrDefault(), _ => null,
    };

    static void SourceWrapCallArgs(BirGraphValue call, SourceBridgeEnv env,
        List<ClosureSynthesis.MaterializedOccurrence> materialized)
    {
        var arguments = SourceBridgeArguments(call);
        var signature = call switch {
            BirCallStatic value => value.Sig.OrDefault() ?? value.ShapeTypes.OrDefault(),
            BirCallInstance value => value.Sig.OrDefault() ?? value.ShapeTypes.OrDefault(), _ => null,
        };
        if (arguments == null || signature == null) return;
        for (var index = 0; index < Math.Min(signature.Length, arguments.Length); index++)
        {
            var open = signature[index];
            if (open == null || arguments[index] is not { } argument) continue;
            var target = SourceSpecializeCallType(open, call);
            if (IsCharSeqT(target))
            {
                arguments[index] = SourceCoerceCharSeqArg(argument, env, target);
                continue;
            }
            // Test the open return before specializing, just as the existing
            // call-boundary policy does; no wider generic target inference.
            if (UnwrapFn(open) is not { } openTarget || !IsCharSeqT(openTarget.Ret)
                || SourceSpecializeCallType(openTarget, call) is not TypeNode.Fn targetFunction
                || UnwrapFn(SourceExprType(argument, env)) is not { } source
                || source.Suspend || targetFunction.Suspend || !IsStringTokT(source.Ret)
                || !SameDelegateParams(source, targetFunction)) continue;
            arguments[index] = SourceBuildDelegateReturnAdapter(argument, source, targetFunction);
            var at = index;
            materialized.Add(new(new BirGraphSlot(() => (BirGraphValue)SourceBridgeArguments(call)[at],
                value => SourceBridgeArguments(call)[at] = (IBirExpression)value), env.Context));
        }
    }

    static TypeNode SourceSpecializeCallType(TypeNode type, BirGraphValue call)
    {
        var (owner, methodArgs) = call switch {
            BirCallStatic value => (value.OwnerType.OrDefault() ?? value.Owner.OrDefault(), value.TypeArgs.OrDefault()),
            BirCallInstance value => (value.OwnerType.OrDefault(), value.TypeArgs.OrDefault()),
            _ => ((TypeNode)null, (TypeNode[])null),
        };
        var validMethods = methodArgs != null && methodArgs.All(type => type != null) ? methodArgs : Array.Empty<TypeNode>();
        return SubstCallTvs(type, owner is TypeNode.Fqn { Args: { } arguments } ? arguments : Array.Empty<TypeNode>(), validMethods);
    }

    static TypeNode SourceExprType(IBirExpression expression, SourceBridgeEnv env)
        => expression is BirGraphValue value && value.FunctionTypeStamp is { } function
            ? function : BirGraphStaticType.Surface(expression, env.Vars);

    static BirNewClosure SourceBuildDelegateReturnAdapter(IBirExpression value, TypeNode.Fn source, TypeNode.Fn target)
    {
        _fired = true;
        var name = "dotkt$StringCharSequenceDelegateAdapter$" + System.Threading.Interlocked.Increment(ref _delegateAdapterCounter);
        var variables = new List<TypeNode.Tv>();
        CollectTvs(source, variables); CollectTvs(target, variables);
        var parameters = target.DelegateParams.Select((type, index) => new BirLambdaParameter {
            Name = "p" + index, Type = BirGraphValue.CopyType(type),
        }).ToArray();
        var invoke = new BirDelegateInvoke {
            FuncType = BirGraphValue.CopyType(source),
            Recv = new BirFieldGet { Sty = BirGraphValue.CopyType(source), OwnerType = new TypeNode.Fqn(name),
                Recv = new BirThis(), Name = "source" },
            Args = parameters.Select(parameter => (IBirExpression)new BirLocal { Name = parameter.Name }).ToArray(),
        };
        return new BirNewClosure {
            ClosureType = new TypeNode.Fqn(name), Captures = new[] { value }, Method = "invoke",
            FuncType = BirGraphValue.CopyType(target),
            TypeArgs = variables.Count == 0 ? default : OptionalField<TypeNode[]>.Of(variables.Cast<TypeNode>().ToArray()),
            SynthClass = new BirClosureDefinition {
                Name = name, Fields = new[] { new BirCaptureDescriptor { Name = "source", Type = BirGraphValue.CopyType(source) } },
                Params = parameters, Ret = BirGraphValue.CopyType(target.Ret),
                Body = new IBirStatement[] { new BirReturn { Value = SourceWrapAdapter(invoke) } },
                TypeParams = variables.Count == 0 ? default : OptionalField<BirTypeParameter[]>.Of(
                    variables.Select((_, index) => new BirTypeParameter { Name = "T" + index }).ToArray()),
            },
        };
    }

    // A nonnullable slot or an actual !! assertion permits peeling String?.
    // Nullable slots otherwise keep a conditional adapter/null result, with a
    // single spill for values which cannot be reread. Conditional value arms
    // are converted independently; an existing CharSequence view is unchanged.
    static IBirExpression SourceCoerceCharSeqArg(IBirExpression value, SourceBridgeEnv env, TypeNode target)
    {
        var source = BirGraphStaticType.Surface(value, env.Vars);
        if (IsScalarCharSequence(target) && source is (TypeNode.Tv or TypeNode.Nullable { Of: TypeNode.Tv }))
            return SourceRestoreErasedCharSequence(value, target);
        var conversion = SelectCharSequenceConversion(source,
            target is TypeNode.Fqn, SourceIsNotNullAssertion(value), value is BirConditional);
        if (conversion == CharSequenceConversion.String) return SourceWrapAdapter(value);
        if (conversion == CharSequenceConversion.StringBuilder)
            return SourceWrapAdapter(new BirCallStatic { Owner = new TypeNode.Fqn("kotlin.LibraryKt"), Method = "toString",
                Sig = new TypeNode[] { new TypeNode.Fqn("object") }, Args = new[] { value } });
        if (conversion == CharSequenceConversion.NullableString)
            return SourceWrapAdapterNullable(value, source);
        if (conversion == CharSequenceConversion.Branches && value is BirConditional conditional)
        {
            if (conditional.Then.HasValue) conditional.Then = OptionalField<IBirExpression>.Of(SourceCoerceCharSeqArg(conditional.Then.Value, env, target));
            if (conditional.Else.HasValue) conditional.Else = OptionalField<IBirExpression>.Of(SourceCoerceCharSeqArg(conditional.Else.Value, env, target));
        }
        return value;
    }

    static bool SourceIsNotNullAssertion(IBirExpression value)
    {
        if (value is not BirValueBlock block || block.Result.OrDefault() is not BirConditional conditional
            || conditional.Else.OrDefault() is not BirThrowExpression
            || conditional.Cond.OrDefault() is not BirUnaryOperation unary || unary.Op.OrDefault() != "!"
            || unary.E.OrDefault() is not BirObjectEquals equal
            || equal.Rhs.OrDefault() is not BirConstant rhs
            || !(rhs.Value.IsNull || rhs.Value.OrDefault() is BirLiteral.NullLiteral)) return false;
        var lhs = (equal.Lhs.OrDefault() as BirLocal)?.Name.OrDefault();
        var then = (conditional.Then.OrDefault() as BirLocal)?.Name.OrDefault();
        return lhs != null && lhs == then;
    }

    static bool SourceIsStaticString(IBirExpression value, SourceBridgeEnv env)
        => IsStringTokT(BirGraphStaticType.Surface(value, env.Vars));

    static void SourceWrapNewCtorArgs(BirNew node, SourceBridgeEnv env)
    {
        if (!node.Args.HasValue) return;
        var parameters = env.ConstructorSlots?.Invoke(node.Type.OrDefault() as TypeNode.Fqn, node.ConstructorDeclarationId.OrDefault(), includeReferenceCapture: true);
        if (parameters == null || parameters.Length != node.Args.Value.Length) return;
        for (var index = 0; index < parameters.Length; index++)
            if ((IsScalarCharSequence(parameters[index]) || IsSourceCharSeqT(parameters[index]))
                && node.Args.Value[index] is { } argument)
                node.Args.Value[index] = SourceCoerceCharSeqArg(argument, env, parameters[index]);
    }

    internal static bool RestoresClosureReturn(string owner, BirFunctionDeclaration method, CharSeqStringLowering.CharSeqRetLambdas returns)
        => returns.Closures.Contains(owner) && method.Name.OrDefault() == "invoke" && IsStringTokT(method.Ret.OrDefault());

    static void SourceRetypeLiftedRets(BirFileDeclaration file, CharSeqStringLowering.CharSeqRetLambdas returns)
    {
        foreach (var method in file.Methods.OrEmpty())
            if (returns.Statics.Contains(method.Name.OrDefault()) && IsStringTokT(method.Ret.OrDefault()))
                method.Ret = new TypeNode.Fqn(CharSeq);
        foreach (var type in file.Types.OrEmpty())
            if (returns.Closures.Contains(type.Name.OrDefault()))
                foreach (var method in type.Methods.OrEmpty())
                    if (RestoresClosureReturn(type.Name.Value, method, returns))
                        method.Ret = new TypeNode.Fqn(CharSeq);
    }

    static void SourceSyncDelegateFuncRet(BirGraphValue node)
    {
        if (_retLambdas == null) return;
        var target = node switch {
            BirNewDelegate value => _retLambdas.Statics.Contains(value.Method.OrDefault()),
            BirNewClosure value => value.ClosureType.OrDefault() is TypeNode.Fqn type && _retLambdas.Closures.Contains(type.Name),
            _ => false,
        };
        if (!target || node.FunctionTypeStamp is not TypeNode.Fn function || !IsStringTokT(function.Ret)) return;
        var converted = function with { Ret = new TypeNode.Fqn(CharSeq) };
        if (node is BirNewDelegate literal) literal.FuncType = converted;
        else ((BirNewClosure)node).FuncType = converted;
    }

    static BirGraphValue SourceWrapCast(BirCast cast, SourceBridgeEnv env)
    {
        var restored = cast.FunctionValueRestoration.IsPresent;
        cast.FunctionValueRestoration = default;
        if (!cast.E.HasValue || !IsScalarCharSequence(cast.Type.OrDefault())) return cast;
        if (restored || SourceExprType(cast.E.Value, env) is TypeNode.Fqn { Name: "object" or "System.Object" or "kotlin.Any" })
            return SourceRestoreErasedCharSequence(cast.E.Value, cast.Type.Value);
        return SourceIsStaticString(cast.E.Value, env) ? SourceWrapAdapter(cast.E.Value) : cast;
    }

    static BirValueBlock SourceRestoreErasedCharSequence(IBirExpression value, TypeNode target)
    {
        var name = "__csrestore$" + System.Threading.Interlocked.Increment(ref _counter);
        BirLocal Read() => new() { Name = name };
        BirCast Cast(string type) => new() { Type = new TypeNode.Fqn(type), E = Read() };
        BirConditional Branch(string type, IBirExpression converted, IBirExpression otherwise) => new() {
            Type = BirGraphValue.CopyType(target), Cond = new BirIsInstance { Type = new TypeNode.Fqn(type), E = Read() },
            Then = OptionalField<IBirExpression>.Of(converted), Else = OptionalField<IBirExpression>.Of(otherwise),
        };
        var unchanged = new BirCast { Type = BirGraphValue.CopyType(target), E = Read() };
        var builder = Branch("System.Text.StringBuilder", SourceWrapAdapter(new BirNativeInstance {
            Type = new TypeNode.Fqn("System.Text.StringBuilder"), Method = "ToString", Ret = new TypeNode.Fqn("System.String"),
            Recv = Cast("System.Text.StringBuilder"), Args = Array.Empty<IBirExpression>(),
        }), unchanged);
        return new BirValueBlock {
            Stmts = new IBirStatement[] { new BirVariable { Name = name, Type = new TypeNode.Fqn("object"), Init = OptionalField<IBirExpression>.Of(value) } },
            Result = Branch("kotlin.String", SourceWrapAdapter(Cast("kotlin.String")), builder),
        };
    }

    static BirNew SourceWrapAdapter(IBirExpression value)
    {
        _fired = true;
        return SourceAdapterConstruction(value);
    }

    static BirNew SourceAdapterConstruction(IBirExpression value) => new() {
        Type = new TypeNode.Fqn(Adapter), ArgTypes = new TypeNode[] { new TypeNode.Fqn("kotlin.String") }, Args = new[] { value },
    };

    internal static bool CanAdaptStringReturn(TypeNode source, TypeNode target) =>
        IsScalarCharSequence(target)
        && IsStringTokT(source switch {
            TypeNode.Nullable optional => optional.Of, TypeNode.Oblivious platform => platform.Of, _ => source,
        });

    internal static IBirExpression AdaptStringReturn(IBirExpression value, TypeNode source) =>
        source is TypeNode.Nullable or TypeNode.Oblivious ? SourceWrapAdapterNullable(value, source) : SourceWrapAdapter(value);

    static IBirExpression SourceWrapAdapterNullable(IBirExpression value, TypeNode source)
    {
        IBirExpression read = value;
        BirVariable temp = null;
        if (!ValueStability.IsReReadable(((BirGraphNode)value).Tag))
        {
            var name = "__cswrap$" + System.Threading.Interlocked.Increment(ref _counter);
            temp = new BirVariable { Name = name, Type = BirGraphValue.CopyType(source),
                Init = OptionalField<IBirExpression>.Of(value) };
            read = new BirLocal { Name = name };
        }
        IBirExpression CopyRead() => (IBirExpression)((BirGraphValue)read).DeepClone();
        BirConstant Null() => new() { Type = new TypeNode.Fqn(CharSeq), Value = new BirLiteral.NullLiteral() };
        var core = new BirConditional {
            Type = new TypeNode.Fqn(CharSeq), Cond = new BirBinaryOperation { Op = "===", Lhs = OptionalField<IBirExpression>.Of(CopyRead()), Rhs = Null() },
            Then = Null(), Else = SourceWrapAdapter(CopyRead()),
        };
        return temp == null ? core : new BirValueBlock { Stmts = new IBirStatement[] { temp }, Result = core };
    }

    static bool SourceUsesCharSequenceView(BirGraphValue node)
    {
        if (node is IBirExecutablePayload) return false;
        var found = false;
        node.VisitOwnTypeFields((_, type) => BirGraphNameFields.VisitTypeNodes(type, part => found |= IsCharSeqT(part)));
        node.VisitChildren(child => { if (!found) found = SourceUsesCharSequenceView(child); });
        return found;
    }

    // Whole-source and newly materialized-return paths publish this same
    // owning declaration. Its signatures and optional facts are fixed here;
    // later member/type binding still owns their physical resolution.
    static BirClassDefinition BuildAdapterDeclaration()
    {
        TypeNode T(string name) => new TypeNode.Fqn(name);
        BirLocal Local(string name) => new() { Name = name };
        BirCallInstance Value() => new() { OwnerType = T(Adapter), Virtual = false, Recv = new BirThis(), Method = "value", Prop = "get", Args = Array.Empty<IBirExpression>() };
        BirFunctionDeclaration Method(string name, TypeNode ret, BirParameter[] parameters, IBirExpression result,
            bool @override = false, bool @virtual = true, bool objectOverride = false) => new() {
            Name = name, Static = false, Override = @override, Virtual = @virtual, Abstract = false,
            ObjectOverride = objectOverride, Vis = "public", Params = parameters, Ret = ret,
            Body = new IBirStatement[] { new BirReturn { Value = OptionalField<IBirExpression>.Of(result) } },
        };
        var get = Method("get", T("kotlin.Char"), new[] { new BirParameter { Name = "index", Type = T("kotlin.Int") } },
            new BirNativeInstance { Type = T("System.String"), Method = "get_Chars", ArgTypes = new[] { T("System.Int32") },
                Ret = T("System.Char"), Recv = Value(), Args = new IBirExpression[] { Local("index") } });
        get.Mods = new BirModifiers { Operator = true }; get.Attrs = Array.Empty<BirAttribute>();
        var subsequence = Method("subSequence", T(CharSeq), new[] {
            new BirParameter { Name = "startIndex", Type = T("kotlin.Int") }, new BirParameter { Name = "endIndex", Type = T("kotlin.Int") },
        }, SourceAdapterConstruction(new BirNativeInstance { Type = T("System.String"), Method = "Substring",
            ArgTypes = new[] { T("System.Int32"), T("System.Int32") }, Ret = T("System.String"), Recv = Value(),
            Args = new IBirExpression[] { Local("startIndex"), new BirBinaryOperation { Op = "-", Lhs = Local("endIndex"), Rhs = Local("startIndex") } } }));
        subsequence.Attrs = Array.Empty<BirAttribute>();
        var value = Method("value", T("kotlin.String"), Array.Empty<BirParameter>(),
            new BirFieldGet { OwnerType = T(Adapter), Recv = new BirThis(), Name = "value" }, @virtual: false);
        value.PropertyName = "value"; value.PropertyAccessor = "get"; value.PropertyAssociation = "string-value";
        var length = Method("length", T("kotlin.Int"), Array.Empty<BirParameter>(), new BirPropertyGet {
            Type = T("System.String"), Name = "Length", Ret = T("System.Int32"), Static = false, Recv = Value(),
        }, @override: true);
        length.PropertyName = "length"; length.PropertyAccessor = "get"; length.PropertyAssociation = "string-length";
        return new BirClassDefinition {
            Name = Adapter, Kind = "class", Generated = true, Abstract = false, Vis = "public", Base = OptionalField<TypeNode>.Null,
            Interfaces = new[] { T(CharSeq) }, Fields = new[] { new BirFieldDeclaration { Name = "value", Type = T("kotlin.String"), Vis = "internal" } },
            Ctors = new[] { new BirConstructor { Params = new[] { new BirParameter { Name = "value", Type = T("kotlin.String") } },
                BaseArgs = OptionalField<IBirExpression[]>.Null, ThisArgs = OptionalField<IBirExpression[]>.Null, Vis = "public",
                Body = new IBirStatement[] { new BirSetField { OwnerType = T(Adapter), Recv = new BirThis(), Name = "value", Value = Local("value") } } } },
            Methods = new[] { get, subsequence, value, length, Method("ToString", T("kotlin.String"), Array.Empty<BirParameter>(),
                new BirFieldGet { OwnerType = T(Adapter), Recv = new BirThis(), Name = "value" }, @override: true, objectOverride: true) },
            Properties = new[] {
                new BirPropertyDeclaration { Name = "value", Type = T("kotlin.String"), KotlinAccessors = new[] { "get" }, PropertyAssociation = "string-value" },
                new BirPropertyDeclaration { Name = "length", Type = T("kotlin.Int"), KotlinAccessors = new[] { "get" }, PropertyAssociation = "string-length" },
            }, Attrs = Array.Empty<BirAttribute>(),
        };
    }
}
