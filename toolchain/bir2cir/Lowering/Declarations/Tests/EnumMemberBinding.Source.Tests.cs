using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class EnumMemberBinding
{
    internal static void SelfTestSource()
    {
        var integer = new TypeNode.Fqn("kotlin.Int");
        var text = new TypeNode.Fqn("kotlin.String");
        var enumType = new TypeNode.Fqn("LocalEnum");
        BirCallInstance Call(string owner, string method) => new() {
            OwnerType = new TypeNode.Fqn(owner), Method = method,
            Recv = new BirCallStatic { Method = "receiverEffect", Args = Array.Empty<IBirExpression>(), Ret = enumType },
            Args = method == "Equals" ? new IBirExpression[] {
                new BirCallStatic { Method = "argumentEffect", Args = Array.Empty<IBirExpression>(), Ret = enumType },
            } : Array.Empty<IBirExpression>(),
            AnySlot = true, Sty = method == "ToString" ? text : integer,
            Sig = Array.Empty<TypeNode>(), DeclarationId = "authored-selected-slot",
        };
        var calls = new[] { Call("LocalEnum", "ToString"), Call("LocalEnum", "GetHashCode"), Call("LocalEnum", "Equals") };
        var statements = calls.Select(call => new BirExpressionStatement { Expr = call }).ToArray();
        var generic = Call("kotlin.Enum", "ToString");
        var genericStatement = new BirExpressionStatement { Expr = generic };
        var nonVirtual = Call("kotlin.Enum", "ToString"); nonVirtual.Virtual = false;
        var super = Call("kotlin.Enum", "ToString"); super.Super = true;
        var ordinalReceiver = new BirLocal { Name = "generic", Sty = new TypeNode.Tv("method", 0) };
        var ordinal = Call("kotlin.Enum", "ordinal"); ordinal.Prop = "get"; ordinal.Recv = ordinalReceiver;
        var ordinalStatement = new BirExpressionStatement { Expr = ordinal };
        var name = Call("kotlin.Enum", "name"); name.Prop = "get";
        var rich = Call("RichEnum", "ToString");
        var body = statements.Cast<IBirStatement>().Concat(new IBirStatement[] {
            genericStatement, new BirExpressionStatement { Expr = nonVirtual }, new BirExpressionStatement { Expr = super },
            ordinalStatement, new BirExpressionStatement { Expr = name }, new BirExpressionStatement { Expr = rich },
        }).ToArray();
        var method = new BirFunctionDeclaration { Name = "use", Body = body };
        var file = new BirFileDeclaration { FileClass = "Consumer", Methods = new[] { method } };
        var basic = CollectBasicEnums(new[] { new BirFileDeclaration { Types = new IBirTypeDeclaration[] {
            new BirEnumDefinition { Name = "LocalEnum", Kind = "enum" }, new BirClassDefinition { Name = "RichEnum", Kind = "class", EnumRich = true },
        } } });
        Require(ReferenceEquals(Apply(file, basic), file) && ReferenceEquals(file.Methods.Value[0], method)
            && ReferenceEquals(method.Body.Value, body), "owning root, declaration and vector identity");
        for (var i = 0; i < statements.Length; i++)
        {
            Require(statements[i].Expr.Value is BirObjectMethod result && result.Method.OrDefault() == calls[i].Method.Value
                && ReferenceEquals(result.Recv.OrDefault(), calls[i].Recv.OrDefault()) && result.Sty.Equals(calls[i].Sty) && result.AnySlot.Value
                && (i == 2 ? ReferenceEquals(result.Arg.OrDefault(), calls[i].Args.Value[0]) : !result.Arg.IsPresent),
                "basic enum Object slots transfer operands and retain result/Any facts");
        }
        Require(genericStatement.Expr.Value is BirObjectMethod rendered && !rendered.AnySlot.IsPresent
            && ReferenceEquals(rendered.Recv.OrDefault(), generic.Recv.OrDefault()), "generic virtual enum ToString consumes its semantic slot");
        Require(nonVirtual.Method.Value == "ToString" && nonVirtual.AnySlot.Value && super.Super.Value && super.AnySlot.Value
            && name.Prop.Value == "get" && rich.OwnerType.Value == new TypeNode.Fqn("RichEnum"), "nonvirtual, super, name and rich declarations remain on their original binding route");
        Require(ordinalStatement.Expr.Value is BirCallStatic helper
            && helper.Owner.OrDefault() == new TypeNode.Fqn(RuntimeOwner) && helper.Method.OrDefault() == "kotlinEnumOrdinal"
            && helper.Ret.OrDefault() == integer && helper.Sig.Value.SequenceEqual(new TypeNode[] { new TypeNode.Fqn("kotlin.Any") })
            && helper.Args.Value.Length == 1 && ReferenceEquals(helper.Args.Value[0], ordinalReceiver)
            && !helper.Sty.IsPresent && !helper.DeclarationId.IsPresent, "ordinal uses the explicit runtime contract and caller-owned receiver");
        Console.WriteLine("[typed enum member slots] self-test OK (module enum identity, virtual/super intent, ordinal, operands and result facts)");

        static void Require(bool condition, string site)
        { if (!condition) throw new InvalidOperationException("Enum member source self-test failed: " + site); }
    }
}
