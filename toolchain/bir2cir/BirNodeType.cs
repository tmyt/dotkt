using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

// Node-local types for owning BIR. Index-dependent answers remain the caller's
// responsibility; missing facts never become an invented object slot.
static class BirNodeType
{
    static readonly TypeNode Integer = new TypeNode.Fqn("kotlin.Int");
    static readonly TypeNode Boolean = new TypeNode.Fqn("kotlin.Boolean");
    static readonly TypeNode Text = new TypeNode.Fqn("kotlin.String");
    static readonly TypeNode Nothing = new TypeNode.Fqn("kotlin.Nothing");

    internal static TypeNode Of(BirGraphNode node, Func<BirGraphNode, TypeNode> recurse = null,
        Func<string, string> primitiveArrayElement = null)
    {
        if (node == null) return null;
        recurse ??= value => Of(value, null, primitiveArrayElement);
        if (node.ResultTypeStamp is { } stamp) return stamp;
        TypeNode Value(IBirExpression expression) => recurse(expression as BirGraphNode);
        TypeNode Array(TypeNode element) => element == null ? null : new TypeNode.Array(element);
        TypeNode Nullable(TypeNode element) => element == null ? null : new TypeNode.Nullable(element);
        return node switch {
            BirConstant value => value.Type.OrDefault(),
            BirCast value => value.Type.OrDefault(),
            BirSamConversion value => value.Type.OrDefault(),
            BirNew value => value.Type.OrDefault(),
            BirNewClr value => value.Type.OrDefault(),
            BirVariable value => value.Type.OrDefault(),
            BirEnumValue value => value.Type.OrDefault(),
            BirEnumBits value => value.Type.OrDefault(),
            BirEnumParse value => value.Type.OrDefault(),
            BirDefaultValue value => value.Type.OrDefault(),
            BirNullableValue value => value.Elem.OrDefault(),
            BirSafeValueCast value => value.Elem.OrDefault(),
            BirNullableWrap value => Nullable(value.Elem.OrDefault()),
            BirNullableNull value => Nullable(value.Elem.OrDefault()),
            BirConditional value => value.Type.OrDefault() ?? Branch(Value(value.Then.OrDefault()), value.Else.OrDefault(), Value),
            BirCallEvaluation value => value.Type.OrDefault() ?? Value(value.Expr.OrDefault()),
            BirValueBlock value => value.Type.OrDefault() ?? Block(value, recurse, primitiveArrayElement),
            BirStackGet value => value.Elem.OrDefault(),
            BirByRefLoad value => value.Elem.OrDefault(),
            BirArrayGet value => value.Elem.OrDefault() ?? NodeType.ElementOf(Value(value.Array.OrDefault()), primitiveArrayElement),
            BirConvert value => value.To.OrDefault(),
            BirBinaryOperation value => value.Op.OrDefault() is "===" or "==" or "!=" or "<" or ">" or "<=" or ">=" or "&&" or "||"
                ? Boolean : Value(value.Lhs.OrDefault()) ?? Value(value.Rhs.OrDefault()),
            BirUnaryOperation value => value.Op.OrDefault() == "!" ? Boolean : Value(value.E.OrDefault()),
            BirObjectEquals or BirIsInstance or BirInstanceOrNull or BirNullableHasValue => Boolean,
            BirThrowExpression or BirReturnExpression => Nothing,
            BirArrayLength or BirEnumOrdinal => Integer,
            BirConcat or BirEnumName => Text,
            BirNewArray value => Array(value.Elem.OrDefault()),
            BirNewArrayInit value => Array(value.Elem.OrDefault()),
            BirNewArraySized value => Array(value.Elem.OrDefault()),
            BirSpreadConcat value => Array(value.Elem.OrDefault()),
            BirEnumValues value => Array(value.Type.OrDefault()),
            BirClassReference or BirRuntimeClass => new TypeNode.Fqn("System.Type"),
            BirStackAsSpan value => value.Elem.OrDefault() is { } element
                ? new TypeNode.Fqn("System.Span", new[] { element }) : null,
            BirStackAllocation => new TypeNode.Fqn("dotkt$stackptr"),
            BirNewClosure value => value.FuncType.OrDefault() ?? ClosureHead(value.ClosureType.OrDefault()),
            BirNewDelegate value => value.FuncType.OrDefault(),
            BirNewSam value => value.SamType.OrDefault(),
            BirNewSuspendLambda value => value.FuncType.OrDefault(),
            BirNewBoundDelegate value => value.FuncType.OrDefault(),
            BirBoundClrDelegate value => value.FuncType.OrDefault(),
            BirClrStaticDelegate value => value.FuncType.OrDefault(),
            BirDelegateInvoke value => (value.FuncType.OrDefault() as TypeNode.Fn)?.Ret,
            BirObjectMethod value => value.Method.OrDefault() switch {
                "toString" or "ToString" => Text, "hashCode" or "GetHashCode" => Integer,
                "equals" or "Equals" => Boolean, _ => null,
            },
            _ => null,
        };
    }

    static TypeNode ClosureHead(TypeNode type) => type is TypeNode.Fqn named ? new TypeNode.Fqn(named.Name) : null;

    static TypeNode Branch(TypeNode first, IBirExpression second, Func<IBirExpression, TypeNode> value) =>
        first != null && !NodeType.IsNothing(first) ? first : value(second) ?? first;

    static TypeNode Block(BirValueBlock block, Func<BirGraphNode, TypeNode> recurse, Func<string, string> primitiveArrayElement)
    {
        Dictionary<string, TypeNode> variables = null;
        foreach (var statement in block.Stmts.OrEmpty())
            if (statement is BirVariable variable && variable.Name.HasValue && variable.Type.HasValue)
                (variables ??= new(StringComparer.Ordinal))[variable.Name.Value] = variable.Type.Value;
        if (variables == null) return recurse(block.Result.OrDefault() as BirGraphNode);
        TypeNode InBlock(BirGraphNode node) => node is BirLocal local && local.Name.HasValue
            && variables.TryGetValue(local.Name.Value, out var type) ? type
            : Of(node, InBlock, primitiveArrayElement) ?? recurse(node);
        return InBlock(block.Result.OrDefault() as BirGraphNode);
    }

    internal static void SelfTest()
    {
        var frame = new TypeNode.Fqn("sample.Box", new TypeNode[] { new TypeNode.Tv("method", 3) });
        var leaf = new BirCast { Type = frame };
        var block = new BirValueBlock { Stmts = new IBirStatement[] {
            new BirVariable { Name = "captured", Type = frame },
        }, Result = new BirLocal { Name = "captured" } };
        var samples = new (BirGraphNode Node, TypeNode Expected)[] {
            (new BirCallInstance { Sty = frame, Ret = Integer, DynRet = Text }, frame),
            (new BirCallStatic { Ret = frame, DynRet = Text }, frame),
            (leaf, frame), (new BirConditional { Then = new BirThrowExpression(), Else = leaf }, frame),
            (new BirConditional { Then = leaf, Else = new BirThrowExpression() }, frame),
            (block, frame), (new BirCallEvaluation { Expr = block }, frame),
            (block with { Result = new BirValueBlock { Result = new BirLocal { Name = "captured" } } }, frame),
            (new BirArrayGet { Array = new BirNewArray { Elem = frame } }, frame),
            (new BirArrayGet { Elem = frame }, frame),
            (new BirBinaryOperation { Op = "+", Lhs = new BirLocal { Name = "unknown" }, Rhs = leaf }, frame),
            (new BirBinaryOperation { Op = "==", Lhs = leaf, Rhs = leaf }, Boolean),
            (new BirUnaryOperation { Op = "-", E = leaf }, frame),
            (new BirUnaryOperation { Op = "!", E = leaf }, Boolean),
            (new BirNullableWrap { Elem = frame }, new TypeNode.Nullable(frame)),
            (new BirNullableValue { Elem = frame }, frame),
            (new BirStackAsSpan { Elem = frame }, new TypeNode.Fqn("System.Span", new[] { frame })),
            (new BirNewClosure { ClosureType = frame }, new TypeNode.Fqn("sample.Box")),
            (new BirNewDelegate { FuncType = new TypeNode.Fn(false, frame, Array.Empty<TypeNode>()) },
                new TypeNode.Fn(false, frame, Array.Empty<TypeNode>())),
            (new BirDelegateInvoke { FuncType = new TypeNode.Fn(false, frame, Array.Empty<TypeNode>()) }, frame),
        };
        foreach (var (node, expected) in samples)
            if (!Equals(Of(node), expected) || !Equals(Of(node), NodeType.Of(node.ToDom())))
                throw new InvalidOperationException("Owning node-local type changed " + node.Tag);
        foreach (var family in typeof(BirGraphNode).Assembly.GetTypes().Where(type => !type.IsAbstract
            && type.IsSubclassOf(typeof(BirGraphNode))))
        {
            var node = (BirGraphNode)Activator.CreateInstance(family, nonPublic: true);
            if (!Equals(Of(node), NodeType.Of(node.ToDom())))
                throw new InvalidOperationException("Audit owning node-local type for " + node.Tag);
        }
        var read = new BirArrayGet { Array = new BirConstant { Type = new TypeNode.Fqn("kotlin.IntArray") } };
        if (Of(read) != null || Of(read, primitiveArrayElement: name => name == "kotlin.IntArray" ? "kotlin.Int" : null) != Integer)
            throw new InvalidOperationException("Owning node-local type invented a specialized-array mapping");
        Console.WriteLine("[owning node-local types] self-test OK (all current node families, stamps, block scope, branches, caller-owned array facts)");
    }
}
