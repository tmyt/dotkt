using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static class LateinitInitializationLoweringTests
{
    internal static void SelfTest()
    {
        var logical = new TypeNode.Tv("type", 0);
        var owner = new TypeNode.Fqn("State", new TypeNode[] { logical });
        var field = new BirFieldDeclaration { Name = "value", Type = logical, LateinitDeclarationId = "getter-id", Vis = "public" };
        var receiver = new BirCallStatic { OwnerType = new TypeNode.Fqn("Factory"), Method = "get", Ret = owner, Args = Array.Empty<IBirExpression>() };
        var getter = new BirFunctionDeclaration { Name = "value", DeclarationId = "getter-id", Params = Array.Empty<BirParameter>(), Ret = logical,
            Body = new IBirStatement[] { new BirReturn { Value = new BirLateinitGet {
                Name = "value", OwnerType = owner, Recv = receiver, Sty = logical, LateinitSourceName = "sourceValue",
            } } } };
        var set = new BirSetField { Name = "value", OwnerType = owner, Recv = new BirThis(),
            Value = new BirConstant { Type = new TypeNode.Fqn("kotlin.Int"), Value = new BirLiteral.IntegerLiteral(0) } };
        var query = new BirLateinitIsInitialized { LateinitDeclarationId = "getter-id", OwnerType = owner, Recv = new BirThis() };
        var method = new BirFunctionDeclaration { Name = "use", Params = Array.Empty<BirParameter>(), Ret = new TypeNode.Fqn("kotlin.Boolean"),
            Body = new IBirStatement[] { set, new BirReturn { Value = query } } };
        var collision = new BirFunctionDeclaration { Name = "dotkt<lateinitInitialized>0" };
        var type = new BirClassDefinition { Kind = "class", Name = "State", TypeParams = new[] { new BirTypeParameter { Name = "T" } },
            Fields = new[] { field }, Methods = new[] { getter, method, collision } };
        var root = new BirFileDeclaration { FileClass = "LateinitTests", Types = new IBirTypeDeclaration[] { type } };
        LateinitInitializationLowering.ApplyAll(new[] { root }, null);
        var read = (BirCast)((BirReturn)getter.Body.Value[0]).Value.Value;
        var checkedRead = (BirLateinitGet)read.E.Value;
        if (field.Type.Value is not TypeNode.Fqn { Name: "object" } || field.Vis.Value != "private" || field.LateinitDeclarationId.IsPresent
            || getter.Ret.Value != logical || !getter.Lateinit.Value || getter.Attrs.Value.Length != 1
            || read.Type.Value != logical || checkedRead.LateinitSourceName.Value != "sourceValue"
            || checkedRead.Value.Value is not BirFieldGet raw || !ReferenceEquals(raw.Recv.Value, receiver)
            || set.Value.Value is not BirCast { Type.Value: TypeNode.Fqn { Name: "object" }, E.Value: BirConstant { Value.Value: BirLiteral.IntegerLiteral { Value: 0 } } })
            throw new InvalidOperationException("Typed lateinit storage changed accessor types, default-value boxing, or receiver evaluation");
        var call = (BirCallInstance)((BirReturn)method.Body.Value[1]).Value.Value;
        if (call.Method.Value != "dotkt<lateinitInitialized>1" || call.Virtual.Value || call.MemberVisibility.Value != "private"
            || call.OwnerType.Value is not TypeNode.Fqn { Args: { Length: 1 } } || type.Methods.Value.Length != 4
            || type.Methods.Value[3].Vis.Value != "private" || !type.Methods.Value[3].Generated.Value)
            throw new InvalidOperationException("Typed lateinit query lost its exact private target or open owner frame");
        var staticField = new BirFieldDeclaration { Name = "top", Static = true, Type = new TypeNode.Fqn("kotlin.String"), LateinitDeclarationId = "top-id" };
        var staticQuery = new BirLateinitIsInitialized { LateinitDeclarationId = "top-id" };
        var staticMethod = new BirFunctionDeclaration { Name = "topState", Params = Array.Empty<BirParameter>(), Body = new IBirStatement[] { new BirReturn { Value = staticQuery } } };
        var top = new BirFileDeclaration { FileClass = "Top", Fields = new[] { staticField }, Methods = new[] { staticMethod } };
        LateinitInitializationLowering.ApplyAll(new[] { top }, null);
        if (((BirReturn)staticMethod.Body.Value[0]).Value.Value is not BirCallStatic { OwnerType.Value: TypeNode.Fqn { Name: "Top", Args: null } }
            || top.Methods.Value.Length != 2 || !top.Methods.Value[1].Static.Value)
            throw new InvalidOperationException("Typed lateinit file storage lost its eagerly authored static query helper");
    }
}
