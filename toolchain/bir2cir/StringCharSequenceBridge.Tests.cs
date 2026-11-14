using System;
using System.Text.Json.Nodes;
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
        var nullableCore = nullableJoin.Then.Value is BirValueBlock block ? block.Result.Value : nullableJoin.Then.Value;
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

        // The remaining DOM capability consumes a newly materialized method,
        // not a second whole-file source pass. Only its returns may be adapted.
        var late = new BirFileDeclaration { Methods = new[] { new BirFunctionDeclaration {
            Name = "materialized", Params = new[] { new BirParameter { Name = "text", Type = T("kotlin.String") } },
            Ret = T(CharSeq), Body = new IBirStatement[] {
                Statement(new BirCallStatic { Method = "unrelated", Sig = new[] { T(CharSeq) }, Args = new IBirExpression[] { Local("text") } }),
                new BirReturn { Value = new BirValueBlock { Stmts = new IBirStatement[] {
                    new BirVariable { Name = "inner", Type = T("kotlin.String"), Init = Local("text") },
                }, Result = Local("inner") } },
            },
        } } }.ToDom();
        var lateMethod = (JsonObject)late["methods"][0];
        var emitted = _adapterEmitted;
        _adapterEmitted = false;
        try
        {
            ApplyMaterializedReturns(lateMethod);
            Require(Str(lateMethod["body"][0]["expr"]["args"][0]["k"]) == "local",
                "materialized return entry must not replay call-argument bridging");
            Require(Str(lateMethod["body"][1]["value"]["k"]) == "new"
                && Str(lateMethod["body"][1]["value"]["args"][0]["k"]) == "valueBlock"
                && Str(late["types"][0]["name"]) == Adapter, "late return must retain valueBlock scope and publish the shared adapter");
        }
        finally { _adapterEmitted = emitted; }
        Console.WriteLine("[selftest] String/CharSequence receiver and materialized return boundaries: OK");
    }
}
