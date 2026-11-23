using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class ReverseEnumeratorBridgeSynthesis
{
    const string NativeCursorName = "dotkt$EnumeratorOverNativeCursor";

    // Variant Kotlin enumerators can retain a CLR value-type element behind an object slot.
    // Preserve the cursor (including its position) when its exact CLR view already exists.
    internal static IBirExpression AdaptNativeCursor(IBirExpression input, TypeNode.Fqn target, Action demand)
    {
        demand();
        const string name = "dotkt$cursorValue";
        BirLocal Local() => new() { Name = name };
        var raw = new TypeNode.Fqn(IEnumerator);
        var obj = new TypeNode.Fqn("System.Object");
        var typed = BirGraphValue.CopyType(target);
        return new BirValueBlock {
            Stmts = new IBirStatement[] { new BirVariable { Name = name, Type = obj, Init = E(input) } },
            Type = typed, Result = new BirConditional {
                Type = typed, Cond = new BirObjectEquals { Lhs = Local(), Rhs = new BirConstant { Type = obj, Value = new BirLiteral.NullLiteral() } },
                Then = new BirConstant { Type = typed, Value = new BirLiteral.NullLiteral() },
                Else = new BirConditional {
                    Type = typed, Cond = new BirIsInstance { Type = typed, E = E(Local()) },
                    Then = new BirCast { Type = typed, E = E(Local()) },
                    Else = new BirNew { Type = Constructed(NativeCursorName, BirGraphValue.CopyType(target.Args[0])),
                        ArgTypes = new TypeNode[] { raw }, Args = new IBirExpression[] { new BirCast { Type = raw, E = E(Local()) } }, LocalCtorIndex = 0 },
                },
            },
        };
    }

    internal static BirClassDefinition NativeCursorType()
    {
        var raw = new TypeNode.Fqn(IEnumerator);
        var generic = Constructed(IEnumeratorT, Tv0);
        var obj = new TypeNode.Fqn("System.Object");
        var boolean = new TypeNode.Fqn("System.Boolean");
        var none = new TypeNode.Fqn("void");
        var disposable = new TypeNode.Fqn(IDisposable);
        TypeNode Self() => Constructed(NativeCursorName, Tv0);
        IBirExpression Cursor() => new BirFieldGet { OwnerType = Self(), Recv = E(new BirThis()), Name = "_source" };
        BirCallInstance Call(TypeNode owner, string name, TypeNode result) => new() {
            OwnerType = owner, Virtual = true, Method = name, Sig = Array.Empty<TypeNode>(), Ret = result,
            Recv = E(owner == raw ? Cursor() : new BirCast { Type = owner, E = E(Cursor()) }), Args = Array.Empty<IBirExpression>(),
        };
        BirFunctionDeclaration Slot(TypeNode owner, string name, string member, TypeNode result, IBirExpression value)
        {
            var method = SourceMethod(name, name == member ? "public" : "private", result,
                result == none ? [new BirExpressionStatement { Expr = E(value) }] : [SourceReturn(value)], result != none);
            method.Generated = true;
            if (member == "get_Current") method.SpecialName = true;
            method.ClrInterfaceImpls = new[] { SourceDescriptor(owner, member, result) };
            return method;
        }
        return new BirClassDefinition {
            Name = NativeCursorName, Kind = "class", Generated = true, Abstract = false, Final = true,
            BeforeFieldInit = true, Vis = "internal", TypeParams = new[] { new BirTypeParameter { Name = "T" } },
            Base = OptionalField<TypeNode>.Null, Interfaces = new TypeNode[] { generic, raw, disposable },
            Fields = new[] { new BirFieldDeclaration { Name = "_source", Type = raw, Vis = "private", InitOnly = true } },
            Ctors = new[] { new BirConstructor { Params = new[] { new BirParameter { Name = "source", Type = raw } }, Vis = "public",
                BaseArgs = OptionalField<IBirExpression[]>.Null, ThisArgs = OptionalField<IBirExpression[]>.Null,
                Body = new IBirStatement[] { new BirSetField { OwnerType = Self(), Recv = E(new BirThis()), Name = "_source",
                    Value = E(new BirLocal { Name = "source" }) } } } },
            Methods = new[] {
                Slot(raw, "MoveNext", "MoveNext", boolean, Call(raw, "MoveNext", boolean)),
                Slot(generic, "get_Current", "get_Current", Tv0, new BirCast { Type = Tv0, E = E(Call(raw, "get_Current", obj)) }),
                Slot(raw, NonGenericCurrentName, "get_Current", obj, Call(raw, "get_Current", obj)),
                Slot(raw, "Reset", "Reset", none, Call(raw, "Reset", none)),
                Slot(disposable, "Dispose", "Dispose", none, Call(disposable, "Dispose", none)),
            },
        };
    }
}
