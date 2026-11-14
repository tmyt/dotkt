using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class ValueSlotNullableWrite
{
    internal static void SelfTestSource()
    {
        var integer = new TypeNode.Fqn("kotlin.Int");
        var text = new TypeNode.Fqn("kotlin.String");
        var nullable = new TypeNode.Nullable(integer);
        bool IsValue(TypeNode.Fqn type) => type.Name == integer.Name;
        var refs = ReferenceMetadataIndex.Build(new[] { typeof(object).Assembly.Location });
        TypeNode Owner(TypeNode argument) => new TypeNode.Fqn("System.Threading.ThreadLocal", new[] { argument });
        var source = new BirLocal { Name = "source" };
        var unwrap = new BirPropertySet { Type = Owner(integer), Name = "Value", Value = source };
        var wrapValue = new BirLocal { Name = "bare" };
        var wrap = new BirPropertySet { Type = new TypeNode.Oblivious(Owner(nullable)), Name = "Value", Value = wrapValue };
        var read = new BirPropertyGet { Type = Owner(nullable), Name = "Value", Ret = integer, Sty = integer };
        var readNullStamp = new BirPropertyGet { Type = Owner(nullable), Name = "Value", Ret = integer,
            Sty = OptionalField<TypeNode>.Null };
        var opaque = DefaultBirCarrier.Create(0, new BirIsInstance { Type = new TypeNode.Nullable(text),
            E = new BirConstant { Type = text, Value = new BirLiteral.TextLiteral("opaque") } });
        var bytes = ((BirOpaqueBytes)opaque.Args.Value[2]).Bytes.Value.ToArray();
        var parameter = new BirParameter { Name = "argument", Type = integer, Attrs = new[] { opaque } };
        var method = new BirFunctionDeclaration { Name = "slots", Params = new[] { parameter }, Ret = new TypeNode.Fqn("kotlin.Unit"),
            Body = new IBirStatement[] {
                new BirVariable { Name = "source", Type = nullable },
                new BirExpressionStatement { Expr = unwrap },
                new BirVariable { Name = "bare", Type = integer },
                new BirExpressionStatement { Expr = wrap },
                new BirExpressionStatement { Expr = read }, new BirExpressionStatement { Expr = readNullStamp },
            } };
        var file = new BirFileDeclaration { Methods = new[] { method } };
        Apply(file, refs, IsValue);
        if (!ReferenceEquals(file.Methods.Value[0], method) || !ReferenceEquals(method.Params.Value[0], parameter)
            || unwrap.Value.Value is not BirNullableValue { Elem.Value: var extracted } || extracted != integer
            || wrap.Value.Value is not BirNullableWrap { Elem.Value: var wrapped } || wrapped != integer
            || read.Ret.Value != nullable || read.Sty.Value != nullable
            || readNullStamp.Ret.Value != nullable || !readNullStamp.Sty.IsNull)
            throw new InvalidOperationException("Typed platform slots lost lexical types, declared storage or occurrence/presence identity");

        var numbers = new TypeNode.Fqn("Numbers");
        var iterableInteger = new TypeNode.Fqn("kotlin.collections.Iterable", new[] { integer });
        var numberDeclaration = new BirClassDefinition { Name = numbers.Name, Kind = "class", Interfaces = new TypeNode[] { iterableInteger } };
        var operand = new BirLocal { Name = "numbers", Sty = numbers };
        var unchanged = new BirLocal { Name = "unrelated", Sty = numbers };
        var call = new BirCallStatic { Method = "consume", Sig = new TypeNode[] {
                new TypeNode.Fqn("Box", new TypeNode[] { nullable }),
                new TypeNode.Fqn("kotlin.collections.Iterable", new TypeNode[] { nullable }),
                new TypeNode.Fqn("kotlin.collections.List", new TypeNode[] { nullable }),
            }, Args = new IBirExpression[] { unchanged, operand, new BirLocal { Name = "list", Sty = numbers } } };
        var iterableFile = new BirFileDeclaration { Types = new IBirTypeDeclaration[] { numberDeclaration },
            Methods = new[] { new BirFunctionDeclaration { Name = "iterables", Ret = new TypeNode.Fqn("kotlin.Unit"),
                Body = new IBirStatement[] { new BirExpressionStatement { Expr = call } } } } };
        var definitions = new Dictionary<string, SupertypeGraph.Def>(StringComparer.Ordinal);
        ValueElementIterableCoercion.Apply(iterableFile, IsValue, definitions, refs);
        if (!ReferenceEquals(call.Args.Value[0], unchanged) || call.Args.Value[2] is not BirLocal
            || call.Args.Value[1] is not BirGenericStatic { Method.Value: "Cast" } adapter
            || !ReferenceEquals(adapter.Args.Value[0], operand)
            || adapter.TypeArgs.Value[0] != new TypeNode.Fqn("object")
            || adapter.ResolvedMemberParams.Value[0] != new TypeNode.Fqn("System.Collections.IEnumerable")
            || !definitions.ContainsKey(numbers.Name))
            throw new InvalidOperationException("Typed iterable coercion lost selected-slot position or the exact boxing adapter");
        ValueElementIterableCoercion.Apply(iterableFile, IsValue, definitions, refs);
        if (!ReferenceEquals(call.Args.Value[1], adapter))
            throw new InvalidOperationException("Typed iterable coercion wrapped an already selected adapter twice");

        var valueArray = new TypeNode.Array(nullable);
        var constructions = new IBirExpression[] {
            new BirNewArray { Elem = integer, Elems = Array.Empty<IBirExpression>() },
            new BirNewArraySized { Elem = integer, Size = new BirConstant { Type = integer, Value = new BirLiteral.IntegerLiteral(0) } },
            new BirNewArrayInit { Elem = integer, Size = new BirConstant { Type = integer, Value = new BirLiteral.IntegerLiteral(0) } },
        };
        var nullableTest = new BirIsInstance { Type = new TypeNode.Nullable(text), E = new BirLocal { Name = "item" } };
        var plainTest = new BirIsInstance { Type = text, E = new BirLocal { Name = "item" } };
        var locals = constructions.Select((construction, index) => new BirVariable {
            Name = "array" + index, Type = valueArray, Init = OptionalField<IBirExpression>.Of(construction),
        }).ToArray();
        var arrayFile = new BirFileDeclaration { Methods = new[] { new BirFunctionDeclaration {
            Name = "arrays", Ret = new TypeNode.Fqn("kotlin.Unit"), Params = new[] { parameter },
            Body = locals.Cast<IBirStatement>().Concat(new IBirStatement[] {
                new BirExpressionStatement { Expr = nullableTest }, new BirExpressionStatement { Expr = plainTest },
            }).ToArray(),
        } } };
        ArrayNullableElemCanonicalization.Apply(arrayFile, IsValue);
        NullableIsInstMatch.Apply(arrayFile);
        if (((BirNewArray)constructions[0]).Elem.Value != new TypeNode.Fqn("object")
            || ((BirNewArraySized)constructions[1]).Elem.Value != new TypeNode.Fqn("object")
            || ((BirNewArrayInit)constructions[2]).Elem.Value != new TypeNode.Fqn("object")
            || !nullableTest.NullMatches.OrDefault() || plainTest.NullMatches.IsPresent
            || !bytes.AsSpan().SequenceEqual(((BirOpaqueBytes)opaque.Args.Value[2]).Bytes.Value))
            throw new InvalidOperationException("Typed nullable array/isInst lowering missed an owning operand or entered an opaque default");
        Console.WriteLine("[typed nullable prefix] self-test OK (platform slots, iterable targets, array elements, nullable isInst, identities and opaque defaults)");
    }
}
