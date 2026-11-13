using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

sealed partial class ValueRepresentationLowering
{
    static BirGraphValue Graph(IBirExpression expression) => (BirGraphValue)expression;
    static IBirExpression CloneValue(IBirExpression expression) => (IBirExpression)Graph(expression).DeepClone();
    static OptionalField<IBirExpression> Value(IBirExpression expression) => OptionalField<IBirExpression>.Of(expression);
    static BirConstant ShapeValue(string shape) => new() { Type = new TypeNode.Fqn("kotlin.String"), Value = new BirLiteral.TextLiteral(shape) };
    internal IBirExpression PredicateValue(TypeNode type)
    {
        var (binding, shape) = Classifier(type);
        return !referenceBuild && binding != null ? HelperValue(binding.ClassifierFactory,
            new IBirExpression[] { ShapeValue(shape) }, new[] { new TypeNode.Fqn("kotlin.String") }, KotlinRuntimeTypeWitness.PredicateType) : null;
    }
    BirCallStatic HelperValue(string target, IReadOnlyList<IBirExpression> arguments, IReadOnlyList<TypeNode> parameters, TypeNode result)
    {
        var split = target.LastIndexOf('.');
        var owner = target[..split];
        var name = target[(split + 1)..];
        return new BirCallStatic { Owner = new TypeNode.Fqn(owner), Method = name,
            DeclarationId = HelperIdentity(owner, name, parameters), Sig = parameters.Select(BirGraphValue.CopyType).ToArray(),
            Args = arguments.Select(CloneValue).ToArray(), Ret = BirGraphValue.CopyType(result), Sty = BirGraphValue.CopyType(result) };
    }

    readonly record struct OperationLexicalContext(BirGraphValue Type, BirGraphValue Callable)
    {
        internal OperationLexicalContext Enter(BirGraphValue node) => this with {
            Type = node is IBirTypeDeclaration type && type.Kind.OrDefault() is "class" or "interface" or "enum" or "struct" ? node : Type,
            Callable = node switch {
                BirFunctionDeclaration value when value.Params.HasValue && value.Body.HasValue => node,
                BirConstructor value when value.Params.HasValue && value.Body.HasValue => node,
                BirClosureDefinition value when value.Params.HasValue && value.Body.HasValue => node,
                _ => Callable,
            },
        };
        internal BirGraphValue Declaration(string scope) => scope == "method" ? Callable : Type;
        internal NullableRepresentationFrame Frame(string scope) => scope == "method"
            ? (Callable as BirFunctionDeclaration)?.NullableRepresentationFrame.OrDefault()
            : Type == null ? null : NullableRepresentationViews.TypeFacts(Type)?.NullableFrame;
        internal IReadOnlyDictionary<int, TypeNode[]> Bounds(string scope) => scope == "method" ? Callable switch {
            BirFunctionDeclaration value => value.MethodTypeParameterBoundsPre.OrDefault()?.Bounds,
            BirClosureDefinition value => value.MethodTypeParameterBoundsPre.OrDefault()?.Bounds,
            _ => null,
        } : Type == null ? null : NullableRepresentationViews.TypeFacts(Type)?.Bounds;
        internal BirTypeParameter[] Parameters(string scope) => Declaration(scope) switch {
            IBirTypeDeclaration value => value.TypeParams.OrEmpty(), BirFunctionDeclaration value => value.TypeParams.OrEmpty(),
            BirClosureDefinition value => value.TypeParams.OrEmpty(), _ => Array.Empty<BirTypeParameter>(),
        };
    }

    sealed record SemanticBoundFrame(NullableRepresentationFrame Frame,
        IReadOnlyDictionary<int, TypeNode[]> Bounds, IReadOnlyList<TypeNode[]> Constraints);

    TypeNode SemanticBound(OperationLexicalContext context, TypeNode.Tv variable, HashSet<TypeNode.Tv> visited)
        => ResolveSemanticBound(variable, scope => context.Declaration(scope) == null ? null
            : new SemanticBoundFrame(context.Frame(scope), context.Bounds(scope),
                context.Parameters(scope).Select(parameter => parameter.Constraints.OrDefault()).ToArray()), visited);

    TypeNode ResolveSemanticBound(TypeNode.Tv variable, Func<string, SemanticBoundFrame> query, HashSet<TypeNode.Tv> visited)
    {
        if (!visited.Add(variable) || query(variable.Scope) is not { } declaration) return null;
        var frame = declaration.Frame;
        var sourceIndex = frame?.PhysicalSlot(variable.I).SourceIndex ?? variable.I;
        var bounds = declaration.Bounds?.GetValueOrDefault(sourceIndex);
        bounds ??= declaration.Constraints.ElementAtOrDefault(variable.I);
        foreach (var bound in bounds ?? Array.Empty<TypeNode>())
        {
            if (HasClassifier(bound)) return bound;
            if (bound is TypeNode.Tv parent)
            {
                var physical = query(parent.Scope)?.Frame?.Variable(parent, NullableRepresentationFrame.Role.Ordinary) as TypeNode.Tv ?? parent;
                if (ResolveSemanticBound(physical, query, visited) is { } inherited) return inherited;
            }
        }
        return null;
    }

    internal void BindOperations(BirFileDeclaration root)
    {
        if (referenceBuild || bindings.Count == 0) return;
        if (domBound) throw new InvalidOperationException("Source value operations used after DOM declaration handoff");
        var suffix = 0;
        var names = new HashSet<string>(StringComparer.Ordinal);
        root.Visit(node => node.VisitNameFields(name => names.Add(name)));
        BirGraphValue Walk(BirGraphValue node, OperationLexicalContext lexical)
        {
            if (node is IBirExecutablePayload or BirAttribute or BirOverride) return node;
            var children = lexical.Enter(node);
            node.VisitChildSlots(slot => slot.Replace(Walk(slot.Value, children)));
            if (node is BirCallInstance call)
            {
                if (call.Super.OrDefault() && call.DeclarationId.HasValue
                    && members.TryGetValue(call.DeclarationId.Value, out var body) && !body.Method.Abstract)
                    return DefaultCallValue(body.Owner, body.Method, call.Recv.OrDefault(), call.Args.OrEmpty());
                if (!call.Super.OrDefault() && SelectedAdapter(call.DeclarationId.OrDefault(),
                        call.Overrides.OrEmpty().Select(marker => marker.DeclarationId.OrDefault())) is string target)
                {
                    var args = new[] { call.Recv.OrDefault() }.Concat(call.Args.OrEmpty()).ToArray();
                    var signature = new TypeNode[] { new TypeNode.Fqn("kotlin.Any") }.Concat(call.Sig.OrEmpty()).ToArray();
                    var ret = call.DynRet.OrDefault() ?? call.Ret.OrDefault() ?? call.Sty.OrDefault()
                        ?? throw new InvalidOperationException("Value member adapter has no selected return type");
                    return HelperValue(target, args, signature, ret);
                }
            }
            var (kind, source, operand, reified) = node switch {
                BirCast value => ("cast", value.Type.OrDefault(), value.E.OrDefault(), value.ReifiedTypeOperand.OrDefault()),
                BirIsInstance value => ("isInst", value.Type.OrDefault(), value.E.OrDefault(), true),
                BirInstanceOrNull value => ("isInstRef", value.Type.OrDefault(), value.E.OrDefault(), value.ReifiedTypeOperand.OrDefault()),
                _ => (null, null, null, false),
            };
            if (kind == null || operand == null) return node;
            var scalar = source is TypeNode.Nullable nullable ? nullable.Of : source;
            if (kind is "cast" or "isInstRef" && scalar is TypeNode.Tv variable
                && !KotlinTypeWitness.NeedsWitness(kind, reified)
                && SemanticBound(lexical, variable, new HashSet<TypeNode.Tv>()) is { } bound)
            {
                var guard = node.DeepClone();
                var guardType = bound is TypeNode.Nullable ? bound : new TypeNode.Nullable(bound);
                if (guard is BirCast cast) cast.Type = guardType;
                else ((BirInstanceOrNull)guard).Type = guardType;
                var expression = new BirCast { Type = new TypeNode.Nullable(new TypeNode.Fqn("kotlin.Any")), E = Value((IBirExpression)Walk(guard, lexical)) };
                if (node is BirCast outerCast) outerCast.E = expression;
                else ((BirInstanceOrNull)node).E = expression;
                return node;
            }
            var (binding, shape) = Classifier(source);
            if (binding == null) return node;
            string temp;
            do temp = "$valueClassifier$" + suffix++; while (!names.Add(temp));
            return ClassifiedOperation(kind, source, operand, binding, shape, temp);
        }
        Walk(root, default);
    }

    BirCallStatic DefaultCallValue(string owner, LiveMethod source, IBirExpression receiver, IReadOnlyList<IBirExpression> arguments)
    {
        var plan = MemberCallSubstitution.PlanRule3Helper(refs, new TypeNode.Fqn(owner), source.Name, source.Id,
            true, receiver != null, arguments.Count, source.Parameters, Array.Empty<TypeNode>(), source.Return, source.Return,
            locals.Keys.ToHashSet(StringComparer.Ordinal), representations);
        return new BirCallStatic { Owner = plan.Owner, Method = plan.Method,
            DeclarationId = plan.DeclarationId == null ? default : OptionalField<string>.Of(plan.DeclarationId),
            TypeArgs = plan.TypeArgs == null ? default : OptionalField<TypeNode[]>.Of(plan.TypeArgs),
            Sig = plan.Signature == null ? default : OptionalField<TypeNode[]>.Of(plan.Signature),
            CalleeParams = plan.CalleeParams == null ? default : OptionalField<TypeNode[]>.Of(plan.CalleeParams),
            CalleeRet = plan.CalleeReturn == null ? default : OptionalField<TypeNode>.Of(plan.CalleeReturn),
            Ret = plan.Return == null ? default : OptionalField<TypeNode>.Of(plan.Return),
            ProjectedHelperReceiver = plan.ProjectedHelperReceiver is int projected ? OptionalField<int>.Of(projected) : default,
            Args = (receiver == null ? arguments : new[] { receiver }.Concat(arguments)).Select(CloneValue).ToArray() };
    }

    BirValueBlock ClassifiedOperation(string kind, TypeNode source, IBirExpression operand,
        ValueRepresentationBinding binding, string shape, string temp)
    {
        var any = new TypeNode.Nullable(new TypeNode.Fqn("kotlin.Any"));
        var boolean = new TypeNode.Fqn("kotlin.Boolean");
        var nullable = source is TypeNode.Nullable;
        var opaque = OpaqueScalar(source);
        var physical = ClassifierType(source);
        BirLocal Local() => new() { Name = temp, Sty = any };
        BirConstant Null() => new() { Type = any, Value = new BirLiteral.NullLiteral() };
        BirConstant Bool(bool value) => new() { Type = boolean, Value = new BirLiteral.BooleanLiteral(value) };
        BirObjectEquals IsNull() => new() { Lhs = Local(), Rhs = Null() };
        BirConditional Matches() => new() { Type = boolean, Cond = IsNull(), Then = Bool(nullable), Else = new BirConditional {
            Type = boolean, Cond = HelperValue(binding.Classifier, new IBirExpression[] { Local(), ShapeValue(shape) },
                new TypeNode[] { any, new TypeNode.Fqn("kotlin.String") }, boolean),
            Then = new BirIsInstance { Type = physical, E = Local() }, Else = Bool(false) } };
        BirIf ThrowWhen(IBirExpression test, string exception) => new() { Branches = new[] { new BirBranch {
            Cond = Value(test), Body = new IBirStatement[] { new BirThrow { Value = new BirNew {
                Type = new TypeNode.Fqn(exception), Args = Array.Empty<IBirExpression>(), ArgTypes = Array.Empty<TypeNode>() } } } } } };
        var statements = new List<IBirStatement> { new BirVariable { Name = temp, Type = any, Init = Value(CloneValue(operand)) } };
        IBirExpression result;
        if (kind == "cast")
        {
            if (!nullable) statements.Add(ThrowWhen(IsNull(), "kotlin.NullPointerException"));
            statements.Add(ThrowWhen(new BirUnaryOperation { Op = "!", E = Matches() }, "kotlin.ClassCastException"));
            result = new BirCast { Type = opaque ? any : source, E = Local() };
        }
        else if (kind == "isInst") result = Matches();
        else result = new BirConditional { Type = nullable ? source : new TypeNode.Nullable(source), Cond = Matches(),
            Then = new BirInstanceOrNull { Type = physical, E = Local() }, Else = Null() };
        return new BirValueBlock { Stmts = statements.ToArray(), Result = Value(result) };
    }
}
