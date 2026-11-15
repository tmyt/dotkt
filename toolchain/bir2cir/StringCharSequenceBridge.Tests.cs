using System;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class StringCharSequenceBridge
{
    internal static void SelfTest()
    {
        TypeNode T(string name) => new TypeNode.Fqn(name);
        BirLocal Local(string name) => new() { Name = name };
        BirConstant Null(TypeNode type) => new() { Type = type, Value = new BirLiteral.NullLiteral() };
        BirExpressionStatement Statement(IBirExpression value) => new() { Expr = OptionalField<IBirExpression>.Of(value) };
        void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("CharSequence boundary selftest: " + message);
        }
        bool Adapted(IBirExpression value) => value is BirNew { Type.Value: TypeNode.Fqn { Name: Adapter } };

        var receiver = new BirCallInstance { OwnerType = T(CharSeq), Method = "get",
            Recv = Local("text"), Sig = Array.Empty<TypeNode>(), Args = Array.Empty<IBirExpression>() };
        var custom = Local("custom");
        var customReceiver = new BirCallInstance { OwnerType = T(CharSeq), Method = "get",
            Recv = custom, Sig = Array.Empty<TypeNode>(), Args = Array.Empty<IBirExpression>() };
        var nullBranch = Null(new TypeNode.Nullable(T(CharSeq)));
        var nullableJoin = new BirConditional { Type = new TypeNode.Nullable(T(CharSeq)),
            Cond = new BirConstant { Type = T("kotlin.Boolean"), Value = new BirLiteral.BooleanLiteral(true) },
            Then = Local("nullable"), Else = nullBranch };
        var boxed = Local("boxed");
        var restore = new BirReturn { Value = new BirCast { Type = T(CharSeq), E = boxed } };
        var method = new BirFunctionDeclaration { Name = "boundaries",
            Params = new[] { new BirParameter { Name = "text", Type = T("kotlin.String") },
                new BirParameter { Name = "custom", Type = T(CharSeq) },
                new BirParameter { Name = "nullable", Type = new TypeNode.Nullable(T("kotlin.String")) },
                new BirParameter { Name = "boxed", Type = T("object") } },
            Ret = T("object"), Body = new IBirStatement[] {
                Statement(receiver), Statement(customReceiver), new BirReturn { Value = nullableJoin }, restore,
            } };
        Apply(new BirFileDeclaration { Methods = new[] { method }, Types = Array.Empty<IBirTypeDeclaration>() }, null, null);
        Require(Adapted(receiver.Recv.Value), "resolved interface receiver must adapt String");
        Require(ReferenceEquals(customReceiver.Recv.Value, custom), "custom receiver identity must remain unchanged");
        var nullableCore = nullableJoin.Then.Value is BirValueBlock nullableBlock ? nullableBlock.Result.Value : nullableJoin.Then.Value;
        Require(nullableCore is BirConditional { Then.Value: BirConstant { Value.Value: BirLiteral.NullLiteral },
            Else.Value: BirNew { Type.Value: TypeNode.Fqn { Name: Adapter } } }
            && ReferenceEquals(nullableJoin.Else.Value, nullBranch), "nullable join must preserve null and adapt its non-null String branch");
        Require(restore.Value.Value is BirValueBlock { Stmts.Value: [BirVariable saved], Result.Value: BirConditional {
                Then.Value: BirNew { Type.Value: TypeNode.Fqn { Name: Adapter } },
                Else.Value: BirConditional { Else.Value: BirCast } } }
            && ReferenceEquals(saved.Init.Value, boxed), "marker-free opaque consumption must evaluate once and retain the existing/null view");

        var member = new BirCallInstance { OwnerType = T(CharSeq),
            Method = "length", Prop = "get", Args = Array.Empty<IBirExpression>(),
            Recv = new BirCallInstance { OwnerType = T("ImportedSlot"), Method = "read",
                Args = Array.Empty<IBirExpression>(), Ret = T(CharSeq) } };
        var stringMember = CharSeqStringLowering.Apply(member, null);
        Require(stringMember is BirPropertyGet { Recv.Value: BirCallStatic {
                Method.Value: "toString", Args.Value: [BirCallInstance { Method.Value: "read" }] } },
            "a String member must normalize an imported generic result before native consumption");
        member.Recv = new BirConstant { Type = T("kotlin.String"), Value = new BirLiteral.TextLiteral("ok") };
        Require(CharSeqStringLowering.Apply(member, null) is BirPropertyGet,
            "a proven String receiver must retain its direct native member lowering");

        // The materialized capability consumes the actual attached method,
        // not a second whole-file source pass. Only returns may be adapted.
        var untouchedArgument = Local("text");
        var unrelated = new BirCallStatic { Method = "unrelated", Sig = new[] { T(CharSeq) },
            Args = new IBirExpression[] { untouchedArgument } };
        var units = "a\ud800b\udfff";
        var literal = new BirConstant { Type = T("kotlin.String"), Value = new BirLiteral.TextLiteral(units) };
        var block = new BirValueBlock { Stmts = new IBirStatement[] {
            new BirVariable { Name = "incoming", Type = T("kotlin.String"), Init = Local("text") },
            new BirVariable { Name = "inner", Type = T("kotlin.String"), Init = literal },
        }, Result = Local("inner") };
        var returned = new BirReturn { Value = block };
        var lateMethod = new BirFunctionDeclaration {
            Name = "materialized", Params = new[] { new BirParameter { Name = "text", Type = T("kotlin.String") } },
            Ret = T(CharSeq), Body = new IBirStatement[] {
                Statement(unrelated), returned,
            },
        };
        var late = new BirFileDeclaration { Methods = new[] { lateMethod } };
        var statements = lateMethod.Body.Value;
        var blockStatements = block.Stmts.Value;
        var emitted = _adapterEmitted;
        _adapterEmitted = false;
        try
        {
            var appended = ApplyMaterializedReturns(late, lateMethod);
            Require(ReferenceEquals(unrelated.Args.Value[0], untouchedArgument),
                "materialized return entry must not replay call-argument bridging");
            Require(returned.Value.Value is BirNew { Args.Value: [var original] } && ReferenceEquals(original, block)
                && ReferenceEquals(block.Stmts.Value, blockStatements) && ReferenceEquals(lateMethod.Body.Value, statements)
                && ReferenceEquals(late.Methods.Value[0], lateMethod)
                && appended is [var adapter] && ReferenceEquals(late.Types.Value[0], adapter) && adapter.Name.Value == Adapter,
                "late return must retain owning valueBlock scope and publish the actual shared adapter");
            Require(ReferenceEquals(((BirVariable)block.Stmts.Value[1]).Init.Value, literal)
                && literal.Value.Value is BirLiteral.TextLiteral { Value: var text } && text == units
                && text[1] == '\ud800' && text[3] == '\udfff',
                "materialized return changed UTF-16 units or duplicated the literal occurrence");
        }
        finally { _adapterEmitted = emitted; }
        Console.WriteLine("[selftest] String/CharSequence receiver and materialized return boundaries: OK");
    }
}
