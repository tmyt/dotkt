using System.Reflection;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class ForeignStarProjectionBinding
{
    internal sealed record SourceMaterialization(BirFileDeclaration File,
        IReadOnlyList<BirClassDefinition> AppendedTypes);

    // The initial owner resets usage/adapter numbering once for the module.
    // ApplyLate deliberately retains that usage and continues the same names.
    static void BeginBinding(IReadOnlyDictionary<string, string> owners,
        Dictionary<string, TypeNode.Fqn> hints, bool resetUsage)
    {
        _bindProjectedHelpers = !resetUsage;
        if (resetUsage) { UsedRuntimeFallback = false; _nextDelegateAdapter = 0; }
        _localExistentialOwners = owners ?? new Dictionary<string, string>(StringComparer.Ordinal);
        _reservedNames = new HashSet<string>(StringComparer.Ordinal);
        _closedViewHints = hints;
        _dependentLocalTypes = new Dictionary<string, TypeNode>(StringComparer.Ordinal);
        _nextTemp = 0;
    }

    internal static IReadOnlyList<SourceMaterialization> ApplyAll(IReadOnlyList<BirFileDeclaration> roots,
        IReadOnlyDictionary<string, string> localExistentialOwners, ReferenceMetadataIndex refs,
        IReadOnlyDictionary<string, string> localClrAliases, Func<TypeNode, bool, TypeNode> projectValueType,
        Func<TypeNode,TypeNode> materialMethodArgument, Action<IReadOnlyList<BirClassDefinition>> projectMaterialized)
    {
        _localClrAliases = localClrAliases ?? new Dictionary<string, string>(StringComparer.Ordinal);
        ContinueWithProjection(projectValueType, materialMethodArgument);
        return BindSourceRoots(roots, localExistentialOwners, refs, resetUsage: true,
            admitMaterialized: null, projectMaterialized);
    }

    internal static IReadOnlyList<SourceMaterialization> ApplyLate(IReadOnlyList<BirFileDeclaration> roots,
        IReadOnlyDictionary<string, string> localExistentialOwners, ReferenceMetadataIndex refs,
        Action<BirFileDeclaration, IReadOnlyList<BirClassDefinition>> admitMaterialized,
        Action<IReadOnlyList<BirClassDefinition>> projectMaterialized)
        => BindSourceRoots(roots, localExistentialOwners, refs, resetUsage: false,
            admitMaterialized ?? throw new ArgumentNullException(nameof(admitMaterialized)), projectMaterialized);

    static IReadOnlyList<SourceMaterialization> BindSourceRoots(IReadOnlyList<BirFileDeclaration> roots,
        IReadOnlyDictionary<string, string> localExistentialOwners, ReferenceMetadataIndex refs, bool resetUsage,
        Action<BirFileDeclaration, IReadOnlyList<BirClassDefinition>> admitMaterialized,
        Action<IReadOnlyList<BirClassDefinition>> projectMaterialized)
    {
        BeginBinding(localExistentialOwners, CollectClosedViewHints(roots, refs), resetUsage);
        void Names(BirGraphValue node)
        {
            if (node is IBirExecutablePayload) return;
            node.VisitNameFields(name => _reservedNames.Add(name), includeVariableNames: false);
            node.VisitChildren(Names);
        }
        foreach (var root in roots) Names(root);
        var result = new List<SourceMaterialization>();
        foreach (var file in roots)
        {
            var synthesized = new HashSet<BirClosureDefinition>(ReferenceEqualityComparer.Instance);
            Walk(new BirGraphSlot(() => file, _ => throw new InvalidOperationException("A file is not an expression")));
            result.Add(MaterializeSourceAdapters(file, synthesized, refs, admitMaterialized, projectMaterialized));

            void Walk(BirGraphSlot slot)
            {
                var node = slot.Value;
                if (node is null or IBirExecutablePayload) return;
                if (node is BirLocal local && local.Name.HasValue
                    && _dependentLocalTypes.TryGetValue(local.Name.Value, out var localType))
                    local.Sty = BirGraphValue.CopyType(localType);
                node.VisitChildSlots(Walk);
                if (node is BirVariable variable && variable.Name.HasValue && variable.Init.HasValue
                    && BirNodeType.Of(variable.Init.Value as BirGraphNode) is { } initial
                    && IsForeignStarType(initial, refs))
                {
                    variable.Type = BirGraphValue.CopyType(initial);
                    _dependentLocalTypes[variable.Name.Value] = initial;
                }
                var replacement = (_bindProjectedHelpers ? RewriteSourceStaticHelper(node, refs) : null)
                    ?? RewriteSourceClassifier(node, refs)
                    ?? RewriteSourceCall(node, refs, synthesized);
                if (replacement == null) return;
                UsedRuntimeFallback = true;
                slot.Replace((BirGraphValue)replacement);
            }

        }
        return result;
    }

    static SourceMaterialization MaterializeSourceAdapters(BirFileDeclaration file,
        HashSet<BirClosureDefinition> synthesized, ReferenceMetadataIndex refs,
        Action<BirFileDeclaration, IReadOnlyList<BirClassDefinition>> admitMaterialized,
        Action<IReadOnlyList<BirClassDefinition>> projectMaterialized)
    {
        var occurrences = new List<ClosureSynthesis.MaterializedOccurrence>();
        Find(new BirGraphSlot(() => file, _ => throw new InvalidOperationException("A file is not an expression")), default);
        var materialized = ClosureSynthesis.ApplyMaterialized(file, occurrences, refs);
        // Report the exact already-attached records before their narrow
        // projection can require declaration/proof bindings. Initial entry's
        // null admission retains its original report-after-project order.
        admitMaterialized?.Invoke(file, materialized.AppendedTypes);
        projectMaterialized(materialized.AppendedTypes);
        foreach (var type in materialized.AppendedTypes) SharedSyntheticSynthesis.DropSyntheticTypeArgs(type);
        return new(file, materialized.AppendedTypes);

        void Find(BirGraphSlot slot, ClosureSynthesis.CaptureContext context)
        {
            var node = slot.Value;
            if (node is null or IBirExecutablePayload) return;
            context = ClosureSynthesis.ContextFor(node, context);
            if (node is BirNewClosure { SynthClass.HasValue: true } closure
                && synthesized.Contains(closure.SynthClass.Value))
            {
                occurrences.Add(new(slot, context));
                return;
            }
            node.VisitChildSlots(child => Find(child, context));
        }
    }

    // Selection facts do not own executable operands. Rendering consumes this
    // exact reflection selector and its declaration-frame keys.
    sealed record ForeignMember(string OpenType, TypeNode DeclaringView, int Token,
        string RuntimeName, string[] RuntimeParameterKeys, TypeNode DeclarationReturn,
        bool ReturnsVoid, MethodInfo Method, bool IsField);

    static ForeignMember SelectForeignMember(TypeNode.Fqn owner, string sourceName, string propertyAccess,
        string kind, int methodArity, TypeNode[] signature, ReferenceMetadataIndex refs)
    {
        if (refs.TryForeignStarMethod(owner, sourceName, propertyAccess, methodArity, signature,
            out var open, out var view, out var token, out var name, out var keys,
            out var ret, out var isVoid, out var method))
            return new(open, view, token, name, keys, ret, isVoid, method, false);
        if (kind is "clrPropGet" or "clrPropSet"
            && refs.TryForeignStarField(owner, sourceName, out open, out view, out token, out ret))
            return new(open, view, token, sourceName, null, ret, kind == "clrPropSet", null, true);
        throw new NotSupportedException($"bir2cir: cannot bind exact foreign star member `{owner.Name}.{sourceName}`/"
            + $"{signature.Length}<{methodArity}>");
    }

    static bool SelectForeignOwner(TypeNode.Fqn authored, TypeNode receiverType, ReferenceMetadataIndex refs,
        out TypeNode.Fqn owner, out TypeNode nominalReceiver)
    {
        nominalReceiver = null;
        if (TryForeignStarOwner(authored, refs, out owner, out _)) return true;
        if (!TryForeignStarOwner(receiverType, refs, out owner, out _)) return false;
        // An explicitly selected base construction is a nominal recovery, not
        // a proof that the receiver has the same existential generic closure.
        if (!ContainsExistential(authored) && !AliasVarianceRepresentation.RequiresErasure(owner, refs)
            && refs.ResolveForeignProjectionType(authored.Name, authored.Args) is { } nominal
            && nominal != refs.ResolveForeignProjectionType(owner.Name, owner.Args)
            && nominal.GetGenericArguments().Length == (authored.Args?.Length ?? 0))
        {
            nominalReceiver = authored;
            return false;
        }
        return true;
    }

    static void ValidateForeignMethod(TypeNode.Fqn owner, string name, TypeNode[] signature,
        TypeNode result, ReferenceMetadataIndex refs)
    {
        if (signature.Any(t => t is TypeNode.ByRef))
            throw new NotSupportedException($"bir2cir: foreign star member `{owner.Name}.{name}` has ref/out parameters; "
                + "the object[] reflection ABI cannot preserve managed-reference aliasing");
        if (result is TypeNode.ByRef)
            throw new NotSupportedException($"bir2cir: foreign star member `{owner.Name}.{name}` has a ref return; "
                + "the object reflection ABI cannot preserve lvalue identity");
        if (signature.Any(t => ContainsByRefLike(t, refs)) || ContainsByRefLike(result, refs))
            throw new NotSupportedException($"bir2cir: foreign star member `{owner.Name}.{name}` contains a byref-like parameter or result; "
                + "the object reflection ABI cannot box that signature");
    }

    sealed record ForeignResult(TypeNode AdapterType, TypeNode.Fn Target, bool Adapt, TypeNode Projected);
    static ForeignResult PlanForeignResult(TypeNode declaration, TypeNode semantic, TypeNode.Fqn owner,
        ReferenceMetadataIndex refs, IReadOnlyList<TypeNode> methodArguments = null,
        bool field = false)
    {
        var resultOwnerArguments = AliasVarianceRepresentation.ResultOwnerArguments(owner, refs);
        var adapter = SubstituteProjectedOwnerSlots(semantic, owner.Args);
        var target = UnwrapFunction(adapter);
        var adapt = UnwrapFunction(declaration) is { } declaredFunction && (field || !declaredFunction.Suspend)
            && DependsOnProjectedOwnerSlot(declaration, resultOwnerArguments) && target != null;
        var projected = field && adapt ? null : ProjectResult(declaration, resultOwnerArguments, refs, methodArguments);
        // The native view's open argument selects a member, not a cast for a
        // Kotlin value. Keep a distinct caller value slot if supplied;
        // otherwise let the consumer impose its own physical type.
        if (declaration is TypeNode.Tv { Scope: "type" } && projected is TypeNode.Tv &&
            (semantic is TypeNode.Tv || IsObjectish(semantic)) && AliasVarianceRepresentation.RequiresErasure(owner, refs))
            projected = semantic == projected ? AnyN : semantic;
        return new(adapter, target, adapt, projected);
    }

    sealed record ForeignClassifier(TypeNode.Fqn Owner, bool Nullable, string OpenType, string Method, TypeNode Result);
    static ForeignClassifier PlanForeignClassifier(string kind, TypeNode target, ReferenceMetadataIndex refs)
    {
        if (!TryForeignStarOwner(target, refs, out var owner, out var nullable)) return null;
        if (refs.IsByRefLikeFqn(owner))
            throw new NotSupportedException($"bir2cir: foreign byref-like generic star projection `{owner.Name}<*>` has no boxable CLR existential representation");
        var open = OpenType(owner, refs)
            ?? throw new NotSupportedException($"bir2cir: cannot resolve foreign star classifier `{owner.Name}`/{owner.Args.Length}");
        return new(owner, nullable, open,
            kind switch { "isInst" => "starProjectionIsInstance", "isInstRef" => "starProjectionSafeCast", _ => "starProjectionCast" },
            kind == "isInst" ? Bool : kind == "cast" ? Any : AnyN);
    }

    static IBirExpression RewriteSourceClassifier(BirGraphValue node, ReferenceMetadataIndex refs)
    {
        var (kind, target, operand) = node switch {
            BirIsInstance value => ("isInst", value.Type.OrDefault(), value.E.OrDefault()),
            BirInstanceOrNull value => ("isInstRef", value.Type.OrDefault(), value.E.OrDefault()),
            BirCast value => ("cast", value.Type.OrDefault(), value.E.OrDefault()),
            _ => (null, (TypeNode)null, (IBirExpression)null),
        };
        if (operand == null || PlanForeignClassifier(kind, target, refs) is not { } plan) return null;
        var (owner, nullable, open, method, result) = plan;
        var hint = SourceClosedViewHint(operand, owner, new TypeNode.Fqn(open), refs, preferExplicit: true);
        IBirExpression Invoke(IBirExpression value) => SourceCall(method, new[] { AnyN, Type, TypeN }, result,
            value, SourceClassRef(new TypeNode.Fqn(open)), hint);
        if (!nullable || kind == "isInstRef") return Invoke(operand);
        var temp = FreshTemp();
        return SourceBlock(new[] { SourceVariable(temp, AnyN, operand) }, new BirConditional {
            Cond = Expr(SourceBinary("===", SourceLocal(temp), SourceConstant(AnyN, new BirLiteral.NullLiteral()))),
            Then = Expr(kind == "isInst" ? SourceConstant(Bool, new BirLiteral.BooleanLiteral(true))
                : SourceConstant(AnyN, new BirLiteral.NullLiteral())),
            Else = Expr(Invoke(SourceLocal(temp))),
        });
    }

    sealed record SourceCallInput(string Kind, string Name, string Property, bool Static, TypeNode Owner,
        IBirExpression Receiver, Action<IBirExpression> SetReceiver, TypeNode[] Signature,
        TypeNode[] MethodArguments, IBirExpression[] Arguments, IBirExpression SetValue, TypeNode Result);

    static SourceCallInput ReadSourceCall(BirGraphValue node) => node switch {
        BirCallInstance value => new(value.Tag, value.Method.OrDefault(), value.Prop.OrDefault(), value.Static.OrDefault(),
            value.OwnerType.OrDefault(), value.Recv.OrDefault(), receiver => value.Recv = Expr(receiver),
            value.Sig.OrDefault() ?? value.ArgTypes.OrDefault() ?? value.ResolvedMemberParams.OrDefault(),
            value.TypeArgs.OrEmpty(), value.Args.OrEmpty(), null, value.Ret.OrDefault() ?? BirNodeType.Of(value)),
        BirNativeInstance value => new(value.Tag, value.Method.OrDefault(), value.Prop.OrDefault(), false,
            value.Type.OrDefault(), value.Recv.OrDefault(), receiver => value.Recv = Expr(receiver),
            value._foreignDeclarationSignature.OrDefault()?.Types.ToArray() ?? value.Sig.OrDefault()
                ?? value.ArgTypes.OrDefault() ?? value.ResolvedMemberParams.OrDefault(),
            value.TypeArgs.OrEmpty(), value.Args.OrEmpty(), null, value.Ret.OrDefault() ?? BirNodeType.Of(value)),
        BirGenericInstance value => new(value.Tag, value.Method.OrDefault(), null, false,
            value.Type.OrDefault(), value.Recv.OrDefault(), receiver => value.Recv = Expr(receiver),
            value._foreignDeclarationSignature.OrDefault()?.Types.ToArray() ?? value.ResolvedMemberParams.OrDefault(),
            value.TypeArgs.OrEmpty(), value.Args.OrEmpty(), null, value.Ret.OrDefault() ?? BirNodeType.Of(value)),
        BirConstrainedCall value => new(value.Tag, value.Method.OrDefault(), value.Prop.OrDefault(), false,
            value.Iface.OrDefault(), value.Recv.OrDefault(), receiver => value.Recv = Expr(receiver),
            value._foreignDeclarationSignature.OrDefault()?.Types.ToArray() ?? value.Sig.OrDefault()
                ?? value.ArgTypes.OrDefault() ?? value.ResolvedMemberParams.OrDefault(),
            value.TypeArgs.OrEmpty(), value.Args.OrEmpty(), null, value.Ret.OrDefault() ?? BirNodeType.Of(value)),
        BirPropertyGet value => new(value.Tag, value.Name.OrDefault(), "get", value.Static.OrDefault(),
            value.Type.OrDefault(), value.Recv.OrDefault(), receiver => value.Recv = Expr(receiver),
            value._foreignDeclarationSignature.OrDefault()?.Types.ToArray() ?? value.Sig.OrDefault()
                ?? value.ResolvedMemberParams.OrDefault(),
            Array.Empty<TypeNode>(), Array.Empty<IBirExpression>(), null, value.Ret.OrDefault() ?? BirNodeType.Of(value)),
        BirPropertySet value => new(value.Tag, value.Name.OrDefault(), "set", value.Static.OrDefault(),
            value.Type.OrDefault(), value.Recv.OrDefault(), receiver => value.Recv = Expr(receiver),
            value._foreignDeclarationSignature.OrDefault()?.Types.ToArray() ?? value.Sig.OrDefault()
                ?? value.ResolvedMemberParams.OrDefault(),
            Array.Empty<TypeNode>(), Array.Empty<IBirExpression>(), value.Value.OrDefault(), BirNodeType.Of(value)),
        _ => null,
    };

    static IBirExpression RewriteSourceCall(BirGraphValue node, ReferenceMetadataIndex refs,
        HashSet<BirClosureDefinition> synthesized)
    {
        var input = ReadSourceCall(node);
        if (input == null || input.Static || input.Receiver == null || input.Owner is not TypeNode.Fqn authored) return null;
        var receiver = input.Receiver;
        if (!SelectForeignOwner(authored, BirNodeType.Of(receiver as BirGraphNode), refs, out var owner, out var nominal))
        {
            if (nominal != null) input.SetReceiver(SourceCast(nominal, receiver));
            return null;
        }
        if (refs.IsByRefLikeFqn(owner))
            throw new NotSupportedException($"bir2cir: foreign byref-like generic star projection `{owner.Name}<*>` has no boxable CLR existential representation");
        var valueReceiver = refs.IsValueType(owner);
        var signature = input.Signature ?? (input.Kind == "clrPropSet" && input.SetValue != null
            ? new[] { BirNodeType.Of(input.SetValue as BirGraphNode) } : Array.Empty<TypeNode>());
        if (signature.Any(t => t == null))
            throw new NotSupportedException($"bir2cir: foreign star call `{owner.Name}.{input.Name}` has an incomplete signature");
        var selected = SelectForeignMember(owner, input.Name, input.Property, input.Kind,
            input.MethodArguments.Length, signature, refs);
        var hint = SourceClosedViewHint(receiver, owner, selected.DeclaringView, refs);
        if (selected.IsField)
        {
            if (input.Kind == "clrPropSet")
                return valueReceiver ? SourceValueReceiverFieldSet(receiver, selected, hint, input.SetValue)
                    : SourceCall("starProjectionSetField", new[] { Any, Type, TypeN, Int, String, AnyN },
                        new TypeNode.Fqn("kotlin.Unit"), receiver, SourceClassRef(new TypeNode.Fqn(selected.OpenType)),
                        hint, SourceInteger(selected.Token), SourceText(input.Name), input.SetValue);
            var call = SourceCall("starProjectionGetField", new[] { Any, Type, TypeN, Int, String }, AnyN,
                receiver, SourceClassRef(new TypeNode.Fqn(selected.OpenType)), hint, SourceInteger(selected.Token), SourceText(input.Name));
            var fieldResult = PlanForeignResult(selected.DeclarationReturn, input.Result, owner, refs, field: true);
            if (fieldResult.Adapt) return SourceDelegateResult(call, fieldResult.AdapterType, fieldResult.Target, synthesized);
            return IsObjectish(fieldResult.Projected) ? call : SourceCast(fieldResult.Projected, call);
        }
        ValidateForeignMethod(owner, input.Name, signature, selected.DeclarationReturn, refs);
        var arguments = input.SetValue == null ? input.Arguments : input.Arguments.Append(input.SetValue).ToArray();
        IBirExpression Invoke(string method, TypeNode result, IBirExpression runtimeReceiver, IBirExpression runtimeArguments) =>
            SourceCall(method, new TypeNode[] { Any, Type, TypeN, Int, String, Int, new TypeNode.Array(String),
                new TypeNode.Array(Type), new TypeNode.Array(AnyN) }, result,
                runtimeReceiver, SourceClassRef(new TypeNode.Fqn(selected.OpenType)),
                (IBirExpression)((BirGraphValue)hint).DeepClone(), SourceInteger(selected.Token), SourceText(selected.RuntimeName),
                SourceInteger(input.MethodArguments.Length), SourceArray(String, selected.RuntimeParameterKeys.Select(SourceText)),
                SourceArray(Type, input.MethodArguments.Select((type, i) => SourceClassRef(
                    ClrMemberResolution.RequiresMaterialMethodArgument(selected.Method, i) ? _materialMethodArgument(type) : type))), runtimeArguments);
        var resultPlan = PlanForeignResult(selected.DeclarationReturn, input.Result, owner, refs, input.MethodArguments);
        var (adapterType, target, adapt, projected) = resultPlan;
        if (valueReceiver)
        {
            var invoke = SourceValueReceiverInvocation(receiver, arguments, signature, selected.ReturnsVoid,
                adapt ? AnyN : projected, Invoke);
            return adapt ? SourceDelegateResult(invoke, adapterType, target, synthesized) : invoke;
        }
        IBirExpression Fallback(IBirExpression recv, IEnumerable<IBirExpression> args) =>
            Invoke(selected.ReturnsVoid ? "starProjectionInvokeUnit" : "starProjectionInvoke",
                selected.ReturnsVoid ? new TypeNode.Fqn("kotlin.Unit") : AnyN, recv, SourceArray(AnyN, args));
        if (input.MethodArguments.Length == 0 && !adapt
            && ExactMutableViewDispatch.TrySourceView(owner, refs, type => _projectValueType(type, false),
                out var nativeView, out var family))
        {
            var directOwner = ExactMutableViewDispatch.CloseDeclaring((TypeNode.Fqn)selected.DeclaringView, nativeView.Args);
            return SourceExactViewCall(receiver, arguments, nativeView, family,
                selected.ReturnsVoid ? new TypeNode.Fqn("kotlin.Unit") : projected,
                (recv, args) => ClrMemberResolution.ExactInstanceCall(null, refs, selected.Method, directOwner, recv, args),
                (recv, args) => {
                    var fallback = Fallback(recv, args);
                    return selected.ReturnsVoid || IsObjectish(projected) ? fallback : SourceCast(projected, fallback);
                });
        }
        var runtime = Fallback(receiver, arguments);
        if (selected.ReturnsVoid) return runtime;
        if (adapt) return SourceDelegateResult(runtime, adapterType, target, synthesized);
        return IsObjectish(projected) ? runtime : SourceCast(projected, runtime);
    }

    static IBirExpression SourceClosedViewHint(IBirExpression receiver, TypeNode.Fqn owner, TypeNode declaringView,
        ReferenceMetadataIndex refs, bool preferExplicit = false)
        => ClosedViewHintType(receiver is BirLocal local ? local.Name.OrDefault() : null,
            owner, declaringView, refs, preferExplicit) is { } hint ? SourceClassRef(hint)
            : SourceConstant(TypeN, new BirLiteral.NullLiteral());

    internal static IBirExpression TypeClassifierCheck(TypeNode.Fqn owner, IBirExpression runtimeType,
        ReferenceMetadataIndex refs) => OpenType(owner, refs) is string open
        ? SourceCall("starProjectionHasView", new[] { Type, Type }, Bool,
            runtimeType, SourceClassRef(new TypeNode.Fqn(open))) : null;

    static IBirExpression SourceExactViewCall(IBirExpression receiver, IBirExpression[] arguments,
        TypeNode.Fqn view, int family, TypeNode result,
        Func<BirLocal, IReadOnlyList<IBirExpression>, IBirExpression> direct,
        Func<BirLocal, IReadOnlyList<IBirExpression>, IBirExpression> fallback)
        => ExactMutableViewDispatch.Wrap(receiver, view, family, result, arguments, direct, fallback);

    static OptionalField<IBirExpression> Expr(IBirExpression value) => OptionalField<IBirExpression>.Of(value);
    static BirLocal SourceLocal(string name) => new() { Name = name };
    static BirConstant SourceConstant(TypeNode type, BirLiteral value) => new() { Type = BirGraphValue.CopyType(type), Value = value };
    static BirConstant SourceInteger(int value) => SourceConstant(Int, new BirLiteral.IntegerLiteral(value));
    static BirConstant SourceText(string value) => SourceConstant(String, new BirLiteral.TextLiteral(value));
    static BirClassReference SourceClassRef(TypeNode type) => new() { Type = BirGraphValue.CopyType(type) };
    static BirNewArray SourceArray(TypeNode element, IEnumerable<IBirExpression> values)
        => new() { Elem = BirGraphValue.CopyType(element), Elems = values.ToArray() };
    static BirCast SourceCast(TypeNode type, IBirExpression value) => new() { Type = BirGraphValue.CopyType(type), E = Expr(value) };
    static BirBinaryOperation SourceBinary(string op, IBirExpression left, IBirExpression right)
        => new() { Op = op, Lhs = Expr(left), Rhs = Expr(right) };
    static BirVariable SourceVariable(string name, TypeNode type, IBirExpression value)
        => new() { Name = name, Type = BirGraphValue.CopyType(type), Init = Expr(value) };
    static BirExpressionStatement SourceStatement(IBirExpression value) => new() { Expr = Expr(value) };
    static BirValueBlock SourceBlock(IEnumerable<IBirStatement> statements, IBirExpression result)
        => new() { Stmts = statements.ToArray(), Result = Expr(result) };
    static BirCallStatic SourceCall(string method, IReadOnlyList<TypeNode> signature, TypeNode result,
        params IBirExpression[] arguments) => new() {
            Owner = new TypeNode.Fqn(RuntimeOwner), Method = method,
            Sig = signature.Select(BirGraphValue.CopyType).ToArray(), Ret = BirGraphValue.CopyType(result), Args = arguments,
        };

    static IBirExpression SourceDelegateResult(IBirExpression value, TypeNode semanticResult, TypeNode.Fn target,
        HashSet<BirClosureDefinition> synthesized)
    {
        if (semanticResult is not (TypeNode.Nullable or TypeNode.Oblivious))
            return SourceDelegateAdapter(value, target, synthesized);
        var temp = FreshTemp();
        return SourceBlock(new[] { SourceVariable(temp, AnyN, value) }, new BirConditional {
            Cond = Expr(SourceBinary("===", SourceLocal(temp), SourceConstant(AnyN, new BirLiteral.NullLiteral()))),
            Then = Expr(SourceConstant(semanticResult, new BirLiteral.NullLiteral())),
            Else = Expr(SourceDelegateAdapter(SourceLocal(temp), target, synthesized)),
        });
    }

    static BirNewClosure SourceDelegateAdapter(IBirExpression value, TypeNode.Fn target,
        HashSet<BirClosureDefinition> synthesized)
    {
        var name = FreshDelegateAdapterName();
        var owner = new TypeNode.Fqn(name);
        var parameters = target.DelegateParams.Select((type, index) => new BirLambdaParameter {
            Name = "p" + index, Type = BirGraphValue.CopyType(type),
        }).ToArray();
        var read = new BirFieldGet { OwnerType = owner, Sty = BirGraphValue.CopyType(AnyN),
            Recv = Expr(new BirThis()), Name = "source" };
        var invoke = SourceCall("starProjectionInvokeDelegate", new TypeNode[] { AnyN, new TypeNode.Array(AnyN) }, AnyN,
            read, SourceArray(AnyN, parameters.Select(parameter => (IBirExpression)SourceLocal(parameter.Name.Value))));
        var body = target.Ret is TypeNode.Fqn { Args: null, Name: "kotlin.Unit" or "void" }
            ? new IBirStatement[] { SourceStatement(invoke), new BirReturn() }
            : new IBirStatement[] { new BirReturn { Value = Expr(IsObjectish(target.Ret) ? invoke : SourceCast(target.Ret, invoke)) } };
        var arguments = new List<TypeNode.Tv>();
        CollectTypeVariables(target, arguments);
        var declaration = new BirClosureDefinition { Name = name,
            Fields = new[] { new BirCaptureDescriptor { Name = "source", Type = BirGraphValue.CopyType(AnyN) } },
            Params = parameters, Ret = BirGraphValue.CopyType(target.Ret), Body = body,
        };
        var closure = new BirNewClosure { ClosureType = BirGraphValue.CopyType(owner), Captures = new[] { value },
            Method = "invoke", FuncType = BirGraphValue.CopyType(target), SynthClass = declaration };
        if (arguments.Count != 0)
        {
            declaration.TypeParams = arguments.Select((_, index) => new BirTypeParameter { Name = "T" + index }).ToArray();
            closure.TypeArgs = arguments.Select(BirGraphValue.CopyType).ToArray();
        }
        synthesized.Add(declaration);
        return closure;
    }

    static IBirExpression SourceValueReceiverFieldSet(IBirExpression receiver, ForeignMember selected,
        IBirExpression hint, IBirExpression value)
    {
        var pointer = FreshTemp();
        var boxed = FreshTemp();
        var unit = new TypeNode.Fqn("kotlin.Unit");
        return SourceBlock(new IBirStatement[] {
            SourceVariable(pointer, new TypeNode.ByRef(Any), new BirByRefOf { Inner = Expr(receiver) }),
            SourceVariable(boxed, Any, SourceCall("starProjectionCloneValue", new[] { Any }, Any,
                new BirByRefLoad { Local = pointer, Elem = BirGraphValue.CopyType(Any) })),
            SourceStatement(SourceCall("starProjectionSetField", new[] { Any, Type, TypeN, Int, String, AnyN }, unit,
                SourceLocal(boxed), SourceClassRef(new TypeNode.Fqn(selected.OpenType)), hint,
                SourceInteger(selected.Token), SourceText(selected.RuntimeName), value)),
            SourceStatement(new BirByRefStore { Local = pointer, Elem = BirGraphValue.CopyType(Any), Value = Expr(SourceLocal(boxed)) }),
        }, SourceConstant(unit, new BirLiteral.NullLiteral()));
    }

    static IBirExpression SourceValueReceiverInvocation(IBirExpression receiver, IBirExpression[] arguments,
        TypeNode[] signature, bool returnsVoid, TypeNode projectedResult,
        Func<string, TypeNode, IBirExpression, IBirExpression, IBirExpression> invoke)
    {
        if (arguments.Length != signature.Length)
            throw new NotSupportedException("bir2cir: foreign star value-receiver call lost its positional argument vector");
        if (signature.Any(t => t is TypeNode.ByRef))
            throw new InvalidOperationException("bir2cir: foreign star ref/out signature reached value-receiver lowering");
        var statements = new List<IBirStatement>();
        var pointer = FreshTemp();
        statements.Add(SourceVariable(pointer, new TypeNode.ByRef(Any), new BirByRefOf { Inner = Expr(receiver) }));
        var boxed = FreshTemp();
        statements.Add(SourceVariable(boxed, Any, SourceCall("starProjectionCloneValue", new[] { Any }, Any,
            new BirByRefLoad { Local = pointer, Elem = BirGraphValue.CopyType(Any) })));
        var elements = new List<IBirExpression>();
        foreach (var argument in arguments)
        {
            var name = FreshTemp();
            statements.Add(SourceVariable(name, AnyN, argument));
            elements.Add(SourceLocal(name));
        }
        var array = FreshTemp();
        statements.Add(SourceVariable(array, new TypeNode.Array(AnyN), SourceArray(AnyN, elements)));
        var outcome = FreshTemp();
        statements.Add(SourceVariable(outcome, InvocationOutcome,
            invoke("starProjectionInvokeCaptured", InvocationOutcome, SourceLocal(boxed), SourceLocal(array))));
        statements.Add(SourceStatement(new BirByRefStore { Local = pointer, Elem = BirGraphValue.CopyType(Any), Value = Expr(SourceLocal(boxed)) }));
        IBirExpression result;
        if (returnsVoid)
        {
            statements.Add(SourceStatement(SourceCall("starProjectionInvocationUnit", new[] { InvocationOutcome },
                new TypeNode.Fqn("void"), SourceLocal(outcome))));
            result = new BirStaticFieldGet { OwnerType = new TypeNode.Fqn("kotlin.Unit"), Name = "INSTANCE" };
        }
        else
        {
            result = SourceCall("starProjectionInvocationValue", new[] { InvocationOutcome }, AnyN, SourceLocal(outcome));
            if (!IsObjectish(projectedResult)) result = SourceCast(projectedResult, result);
        }
        return SourceBlock(statements, result);
    }
}
