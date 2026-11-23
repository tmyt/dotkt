using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

// Build the reflection value before Kotlin classifier/projection information is
// erased. A variable leaf belongs to the caller's Kotlin type witness, never
// to the CLR storage type of the generic parameter.
static class ReflectedKotlinType
{
    // Nominal declaration tokens in the CLR stdlib. Multiple Kotlin declarations can have
    // the same @ClrTypeAlias; this association must be selected before physical erasure.
    static readonly Dictionary<string, TypeNode.Fqn> CollectionLiterals = new[] {
        "Iterable", "MutableIterable", "Collection", "MutableCollection", "List", "MutableList",
        "Set", "MutableSet", "Map", "MutableMap", "ArrayList", "HashSet",
    }.ToDictionary(name => "kotlin.collections." + name,
        name => new TypeNode.Fqn("DotKt.Runtime.CompilerServices." + name + "Literal"), StringComparer.Ordinal);
    static TypeNode CollectionLiteral(TypeNode type) => type switch {
        TypeNode.Nullable n => CollectionLiteral(n.Of),
        TypeNode.Oblivious o => CollectionLiteral(o.Of),
        TypeNode.Fqn f => CollectionLiterals.GetValueOrDefault(SourceName(f)),
        _ => null,
    };
    static readonly Dictionary<string, string> LiteralNames = CollectionLiterals.ToDictionary(
        binding => binding.Value.Name, binding => binding.Key, StringComparer.Ordinal);
    internal static string CollectionLiteralName(string token) => LiteralNames.GetValueOrDefault(token);
    internal static string SourceName(TypeNode.Fqn f) => f.Classifier is { } logical
        ? string.Join(".", new[] { logical.PackageName }.Concat(logical.OwnerPath).Append(logical.Name).Where(part => part.Length != 0))
        : f.Name;

    static readonly TypeNode String = new TypeNode.Fqn("kotlin.String"), Boolean = new TypeNode.Fqn("kotlin.Boolean"),
        Classifier = new TypeNode.Nullable(new TypeNode.Fqn("kotlin.reflect.KClassifier")),
        KType = new TypeNode.Fqn("kotlin.reflect.KType"), Projection = new TypeNode.Fqn("kotlin.reflect.KTypeProjection");
    internal static BirFileDeclaration Apply(BirFileDeclaration root, SourceHelperBinder helpers)
    {
        BirGraphValue Lower(BirGraphValue node)
        {
            node.VisitChildSlots(slot => {
                var child = slot.Value;
                var lowered = Lower(child);
                if (!ReferenceEquals(child, lowered)) slot.Replace(lowered);
            });
            if (node is BirClassReference { Type.Value: TypeNode.Fn { Reflective: true, Suspend: false } } literal)
                literal.Type = ReflectiveCallableClassifier();
            if (node is BirClassReference reference && reference.ReifiedLiteral.HasValue)
            {
                var reified = reference.ReifiedLiteral.Value;
                reference.ReifiedLiteral = default;
                var target = reference.Type.Value;
                if (CollectionLiteral(target) is TypeNode token) reference.Type = token;
                else if (reified && KotlinTypeWitness.Variable(target) is { } variable)
                {
                    // A native generic argument need not identify the Kotlin collection declaration.
                    // Forward the caller's reflection witness; keep the old runtime token for other types.
                    var native = new TypeNode.Fqn("DotKt.Runtime.CompilerServices.StarProjectionType");
                    return helpers.Bind(new BirCallStatic {
                        Owner = new TypeNode.Fqn("DotKt.Runtime.CompilerServices.ReflectionRuntimeKt"),
                        Method = "reifiedKClass", Sig = new TypeNode[] { native, KType }, Ret = native,
                        Args = new IBirExpression[] { reference, new BirReflectedType { Operand = variable } },
                    });
                }
                else if (reified) reference.ReifiedLiteral = true;
            }
            return node switch {
                BirReflectedParameter parameter => (BirGraphValue)Declared(parameter, helpers, value => value),
                BirReflectedType reflected => (BirGraphValue)Value(reflected.Operand.Value, helpers,
                    projections: Projections(reflected, helpers, value => value)),
                _ => node,
            };
        }
        return (BirFileDeclaration)Lower(root);
    }

    internal static IBirExpression[] Projections(BirReflectedType reflected, SourceHelperBinder helpers,
        Func<IBirExpression, IBirExpression> lower) => reflected.ArgumentShapes.OrDefault()?.Select(p =>
            MakeProjection(p.Variance.Value, p.Shape.HasValue ? lower(p.Shape.Value) : null, helpers)).ToArray();

    internal static IBirExpression Declared(BirReflectedParameter p, SourceHelperBinder helpers,
        Func<IBirExpression, IBirExpression> lower) => Parameter(((TypeNode.Fqn)p.Owner.Value).Name, p.DeclarationId.Value,
            p.Index.Value, new BirTypeParameter { Name = p.Name.Value, Variance = p.Variance.Value, Reified = p.Reified.Value },
            p.Nullable.Value, p.Bounds.Value.Select(lower).ToArray(), helpers);

    static IBirExpression MakeProjection(string variance, IBirExpression shape, SourceHelperBinder helpers)
    {
        var method = variance switch {
            "star" => "reflectedStar", "in" => "reflectedIn", "out" => "reflectedOut", "invariant" => "reflectedInvariant",
            _ => throw new InvalidOperationException("Invalid reflected projection " + variance),
        };
        return helpers.Bind(new BirCallStatic { Owner = new TypeNode.Fqn("DotKt.Runtime.CompilerServices.ReflectedTypeRuntimeKt"),
            Method = method, Sig = shape == null ? [] : new[] { KType }, Args = shape == null ? [] : new[] { shape }, Ret = Projection, Sty = Projection });
    }

    internal static IBirExpression Value(TypeNode source, SourceHelperBinder helpers,
        Func<TypeNode.Tv, bool, IBirExpression> variable = null, Func<TypeNode, TypeNode> selectClassifier = null,
        IBirExpression[] projections = null)
    {
        var nullable = source is TypeNode.Nullable;
        var core = source is TypeNode.Nullable n ? n.Of : source;
        if (core is TypeNode.Oblivious oblivious) return Value(oblivious.Of, helpers, variable, selectClassifier, projections);
        if (core is TypeNode.Tv tv)
        {
            if (variable != null) return variable(tv, nullable);
            var leaf = new BirReflectedType { Operand = tv };
            // T? is a Kotlin reflection modifier, not the nullable CLR storage slot.
            return nullable ? Call("reflectedWitnessType", [new TypeNode.Fqn("kotlin.Any"), Boolean],
                [leaf, new BirConstant { Type = Boolean, Value = new BirLiteral.BooleanLiteral(true) }], KType) : leaf;
        }
        var (name, classifier, arguments) = core switch {
            TypeNode.Fqn f => (SourceName(f), (TypeNode)(f with { Args = f.Args?.Select(_ => (TypeNode)new TypeNode.Star()).ToArray() }), f.Args ?? []),
            TypeNode.Array array => ("kotlin.Array", (TypeNode)(array with { Elem = new TypeNode.Star() }), new[] { array.Elem }),
            TypeNode.Fn fn => Function(fn),
            _ => throw new InvalidOperationException($"No Kotlin reflected classifier for {core}"),
        };
        return Call("reflectedType", [String, Classifier, Boolean, new TypeNode.Array(Projection)],
            [new BirConstant { Type = String, Value = new BirLiteral.TextLiteral(name) },
             new BirClassReference { Type = CollectionLiteral(core) ?? selectClassifier?.Invoke(classifier) ?? classifier }, new BirConstant { Type = Boolean, Value = new BirLiteral.BooleanLiteral(nullable) },
             new BirNewArray { Elem = Projection, Elems = projections ?? arguments.Select(Project).ToArray() }], KType);

        IBirExpression Project(TypeNode argument) => argument switch {
            TypeNode.Star => MakeProjection("star", null, helpers),
            TypeNode.Projection p => MakeProjection(p.Variance, Value(p.Of, helpers, variable, selectClassifier), helpers),
            _ => MakeProjection("invariant", Value(argument, helpers, variable, selectClassifier), helpers),
        };
        BirCallStatic Call(string method, TypeNode[] signature, IBirExpression[] values, TypeNode result) =>
            helpers.Bind(new BirCallStatic { Owner = new TypeNode.Fqn("DotKt.Runtime.CompilerServices.ReflectedTypeRuntimeKt"),
                Method = method, Sig = signature, Args = values, Ret = result, Sty = result });
    }

    // Capture the Kotlin shape before representation passes rewrite the caller's type arguments.
    // Class literals and variable leaves then flow through the same passes as a direct typeOf.
    internal static void CaptureCalls(BirFileDeclaration root, NullableWitnessDemand reflected, SourceHelperBinder helpers)
    {
        if (reflected == null) return;
        root.Visit(node => {
            if (node is not (BirCallStatic or BirCallInstance or BirConstrainedCall)
                || node.DeclarationIdentityFact.OrDefault() is not string id
                || reflected.Indices(id) is not { Length: > 0 } indices) return;
            var arguments = node.ApplicationTypeArguments.Value;
            var shapes = indices.Select(i => Value(arguments[i], helpers)).ToArray();
            switch (node) {
                case BirCallStatic call: call.ReflectedArguments = shapes; break;
                case BirCallInstance call: call.ReflectedArguments = shapes; break;
                case BirConstrainedCall call: call.ReflectedArguments = shapes; break;
            }
        });
    }

    internal static IBirExpression[] TakeCallShapes(BirGraphValue node)
    {
        switch (node) {
            case BirCallStatic call: var s = call.ReflectedArguments; call.ReflectedArguments = default; return s.OrDefault();
            case BirCallInstance call: var i = call.ReflectedArguments; call.ReflectedArguments = default; return i.OrDefault();
            case BirConstrainedCall call: var c = call.ReflectedArguments; call.ReflectedArguments = default; return c.OrDefault();
            default: return null;
        }
    }

    internal static IBirExpression Read(IBirExpression witness, bool nullable, SourceHelperBinder helpers) =>
        MarkNullable(KotlinRuntimeTypeWitness.ReflectedValue(witness), nullable, helpers);

    internal static IBirExpression MarkNullable(IBirExpression reflected, bool nullable, SourceHelperBinder helpers) =>
        helpers.Bind(new BirCallStatic { Owner = new TypeNode.Fqn("DotKt.Runtime.CompilerServices.ReflectedTypeRuntimeKt"),
            Method = "reflectedWitnessType", Sig = new TypeNode[] { new TypeNode.Fqn("kotlin.Any"), Boolean },
            Args = new[] { reflected, new BirConstant { Type = Boolean, Value = new BirLiteral.BooleanLiteral(nullable) } }, Ret = KType, Sty = KType });

    internal static IBirExpression Parameter(string owner, string id, int index, BirTypeParameter parameter,
        bool nullable, IBirExpression[] bounds, SourceHelperBinder helpers)
    {
        BirConstant Text(string value) => new() { Type = String, Value = new BirLiteral.TextLiteral(value) };
        return helpers.Bind(new BirCallStatic {
            Owner = new TypeNode.Fqn("DotKt.Runtime.CompilerServices.ReflectedTypeRuntimeKt"), Method = "reflectedParameter",
            Sig = new TypeNode[] { new TypeNode.Fqn("kotlin.Any"), String, new TypeNode.Fqn("kotlin.Int"), String, String, Boolean, Boolean, new TypeNode.Array(KType) },
            Args = new IBirExpression[] { new BirClassReference { Type = new TypeNode.Fqn(owner) }, Text(id),
                KotlinTypeWitness.ConstantValue(index), Text(parameter.Name.Value), Text(parameter.Variance.OrDefault() ?? ""),
                new BirConstant { Type = Boolean, Value = new BirLiteral.BooleanLiteral(parameter.Reified.OrDefault()) },
                new BirConstant { Type = Boolean, Value = new BirLiteral.BooleanLiteral(nullable) },
                new BirNewArray { Elem = KType, Elems = bounds } }, Ret = KType, Sty = KType,
        });
    }

    static (string, TypeNode, TypeNode[]) Function(TypeNode.Fn fn)
    {
        if (fn.Suspend)
            throw new InvalidOperationException("bir2cir: typeOf does not support suspend function types");
        var arguments = (fn.Ctx ?? []).Concat(fn.DelegateParams).Append(fn.Ret).ToArray();
        if (fn.Reflective == true)
            return ("kotlin.reflect.KFunction", ReflectiveCallableClassifier(), arguments);
        var name = "kotlin.Function" + (arguments.Length - 1);
        return (name, new TypeNode.Fn(false, new TypeNode.Star(),
            arguments.SkipLast(1).Select(_ => (TypeNode)new TypeNode.Star()).ToArray()), arguments);
    }

    static TypeNode ReflectiveCallableClassifier() => new TypeNode.Fqn("kotlin.reflect.KFunction", [new TypeNode.Star()]);
}
