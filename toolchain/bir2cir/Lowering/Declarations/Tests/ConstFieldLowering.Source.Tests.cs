using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class ConstFieldLowering
{
    internal static void SourceSelfTest()
    {
        static void Require(bool value, string message)
        { if (!value) throw new InvalidOperationException("Owning const fields: " + message); }
        var refs = ReferenceMetadataIndex.Build(new[] { typeof(object).Assembly.Location });
        foreach (var referenceBuild in new[] { false, true })
        {
            var semantic = new TypeNode.Fqn("kotlin.Int");
            var initial = new BirConstant { Type = semantic, Value = new BirLiteral.IntegerLiteral(17) };
            var field = new BirFieldDeclaration { Name = "value", Type = semantic, Const = true, Static = true, Init = initial };
            var plainInit = new BirConstant { Type = semantic, Value = new BirLiteral.IntegerLiteral(19) };
            var plain = new BirFieldDeclaration { Name = "plain", Type = semantic, Const = false, Init = plainInit };
            var read = new BirStaticFieldGet { OwnerType = new TypeNode.Fqn("ConstFile"), Name = "value" };
            var untouched = new BirStaticFieldGet { OwnerType = new TypeNode.Fqn("ConstFile"), Name = "plain" };
            var imported = new BirStaticFieldGet { OwnerType = new TypeNode.Fqn("System.Int32"), Name = "MaxValue" };
            var body = new IBirStatement[] { new BirExpressionStatement { Expr = read },
                new BirExpressionStatement { Expr = untouched }, new BirExpressionStatement { Expr = imported } };
            var method = new BirFunctionDeclaration { Name = "read", Params = Array.Empty<BirParameter>(), Body = body };
            var fields = new[] { field, plain };
            var methods = new[] { method };
            var root = new BirFileDeclaration { FileClass = "ConstFile", Fields = fields, Methods = methods };
            ApplyAll(new[] { root }, refs, referenceBuild);
            Require(!field.Init.IsPresent && !field.Const.IsPresent && field.Constant.Value == initial.Value.Value
                && field.Type.Value == new TypeNode.Fqn(referenceBuild ? "int" : "kotlin.Int")
                && (referenceBuild ? field.KotlinType.Value == semantic : !field.KotlinType.IsPresent),
                "literal declaration lost Constant/initializer lifetime or semantic-before-physical reference type");
            Require(((BirExpressionStatement)body[0]).Expr.Value is BirConstant folded
                && folded.Type.Value == semantic && folded.Value.Value == new BirLiteral.IntegerLiteral(17)
                && ((BirExpressionStatement)body[2]).Expr.Value is BirConstant external
                && external.Value.Value == new BirLiteral.IntegerLiteral(int.MaxValue),
                "local source type or referenced Constant-table value was not retained in folded reads");
            Require(ReferenceEquals(root.Fields.Value, fields) && ReferenceEquals(root.Methods.Value, methods)
                && ReferenceEquals(method.Body.Value, body) && ReferenceEquals(plain.Init.Value, plainInit)
                && !plain.Const.Value && !plain.Constant.IsPresent
                && ReferenceEquals(((BirExpressionStatement)body[1]).Expr.Value, untouched),
                "constant folding changed continuing declarations/body vectors or ordinary initializer/read identity");
        }
        ReferenceMetadataIndex.LiteralFieldValueSelfTest();
        Console.WriteLine("[owning const fields] self-test OK (literal storage, local/ref reads, semantic ref type, presence and identity)");
    }
}
