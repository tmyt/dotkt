using System;
using System.Linq;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class CharCodeInvokeLowering
{
    internal static void SelfTestSource()
    {
        var character = new TypeNode.Fqn("kotlin.Char");
        var integer = new TypeNode.Fqn("kotlin.Int") { Classifier = new("kotlin", Array.Empty<string>(), "Int") };
        var characterRead = new BirLocal { Name = "character", Sty = character };
        BirCallStatic Code() => new() {
            Owner = OptionalField<TypeNode>.Null, Method = "code", Prop = "get",
            Sig = new TypeNode[] { character }, Ret = integer,
            Args = new IBirExpression[] { characterRead },
        };
        var code = Code();
        var absentOwner = Code(); absentOwner.Owner = default;
        var nullOwnerType = Code(); nullOwnerType.OwnerType = OptionalField<TypeNode>.Null;
        var memberOwner = Code(); memberOwner.Owner = new TypeNode.Fqn("UserCharOperations");
        var differentGetter = Code(); differentGetter.Prop = "set";
        var otherExtension = Code(); otherExtension.Sig = new TypeNode[] { integer };
        var statements = new[] { code, absentOwner, nullOwnerType, memberOwner, differentGetter, otherExtension }
            .Select(value => new BirExpressionStatement { Expr = value }).ToArray();
        var file = File(statements);
        var method = file.Methods.Value[0];
        var methods = file.Methods.Value;
        var body = method.Body.Value;
        if (!ReferenceEquals(Apply(file), file) || !ReferenceEquals(file.Methods.Value, methods)
            || !ReferenceEquals(file.Methods.Value[0], method) || !ReferenceEquals(method.Body.Value, body)
            || statements[0].Expr.Value is not BirConvert converted
            || !ReferenceEquals(converted.To.Value, integer) || !ReferenceEquals(converted.E.Value, characterRead)
            || !ReferenceEquals(statements[1].Expr.Value, absentOwner)
            || !ReferenceEquals(statements[2].Expr.Value, nullOwnerType)
            || !ReferenceEquals(statements[3].Expr.Value, memberOwner)
            || !ReferenceEquals(statements[4].Expr.Value, differentGetter)
            || !ReferenceEquals(statements[5].Expr.Value, otherExtension))
            throw new InvalidOperationException("Char.code changed its getter/source-slot/presence contract or declaration ownership");

        var variable = new TypeNode.Tv("method", 0);
        var genericArray = new TypeNode.Array(new TypeNode.Nullable(variable));
        var ownerArguments = new TypeNode[] { integer, variable, genericArray };
        var receiver = new BirLocal { Name = "function" };
        var arguments = new IBirExpression[] { Code(), new BirLocal { Name = "value", Sty = variable } };
        var invoke = new BirCallInstance {
            OwnerType = new TypeNode.Fqn("kotlin.Function2", ownerArguments), Method = "invoke",
            Recv = receiver, Args = arguments, Sig = new TypeNode[] { integer, variable }, Ret = genericArray,
        };
        var invocation = new BirExpressionStatement { Expr = invoke };
        var text = new TypeNode.Fqn("kotlin.String");
        var reflective = new BirExpressionStatement { Expr = new BirCallInstance {
            OwnerType = new TypeNode.Fqn("kotlin.reflect.KFunction0", new TypeNode[] { text }), Method = "invoke",
            Recv = new BirLocal { Name = "reflective" }, Args = Array.Empty<IBirExpression>(), Sig = Array.Empty<TypeNode>(), Ret = text,
        } };
        var ordinary = new BirCallInstance { OwnerType = new TypeNode.Fqn("UserInvoker"), Method = "invoke",
            Recv = new BirLocal { Name = "ordinary" }, Args = Array.Empty<IBirExpression>() };
        var ordinaryStatement = new BirExpressionStatement { Expr = ordinary };
        var invokeFile = File(invocation, reflective, ordinaryStatement);
        var invokeMethod = invokeFile.Methods.Value[0];
        invokeMethod.TypeParams = new[] { new BirTypeParameter { Name = "T" } };
        invokeMethod.Params = invokeMethod.Params.Value.Concat(new[] {
            new BirParameter { Name = "function", Type = invoke.OwnerType.Value },
            new BirParameter { Name = "value", Type = variable },
            new BirParameter { Name = "reflective", Type = new TypeNode.Fqn("kotlin.reflect.KFunction0", new TypeNode[] { text }) },
            new BirParameter { Name = "ordinary", Type = new TypeNode.Fqn("UserInvoker") },
        }).ToArray();
        Apply(invokeFile);
        if (invocation.Expr.Value is not BirDelegateInvoke delegated || !ReferenceEquals(delegated.Recv.Value, receiver)
            || !ReferenceEquals(delegated.Args.Value, arguments) || delegated.Args.Value[0] is not BirConvert
            || delegated.FuncType.Value is not TypeNode.Fn { Suspend: false, Params.Length: 2 } function
            || !TypeNodeDataEquality.Equal(function.Params[0], integer)
            || !TypeNodeDataEquality.Equal(function.Params[1], variable)
            || !TypeNodeDataEquality.Equal(function.Ret, genericArray)
            || !ownerArguments.SequenceEqual(new TypeNode[] { integer, variable, genericArray })
            || ReferenceEquals(function.Params, ownerArguments)
            || reflective.Expr.Value is not BirDelegateInvoke { Args.Value.Length: 0,
                FuncType.Value: TypeNode.Fn { Suspend: false, Params.Length: 0, Ret: TypeNode.Fqn { Name: "kotlin.String" } } }
            || !ReferenceEquals(ordinaryStatement.Expr.Value, ordinary))
            throw new InvalidOperationException("Function invoke changed argument order, bottom-up rewriting, generic frame or ordinary member dispatch");

        var opaqueCall = Code();
        var payload = new BirInlinePayload { Body = new IBirStatement[] { new BirExpressionStatement { Expr = opaqueCall } } };
        if (!ReferenceEquals(Walk(payload), payload)
            || !ReferenceEquals(((BirExpressionStatement)payload.Body.Value[0]).Expr.Value, opaqueCall))
            throw new InvalidOperationException("Char/invoke lowering traversed an opaque executable payload");
        Console.WriteLine("[typed char code/invoke] self-test OK (presence, bottom-up operands, exact function arguments, ownership and opaque payloads)");
    }

    static BirFileDeclaration File(params IBirStatement[] statements) => new() {
        FileClass = "CharCodeInvokeProbe", Methods = new[] { new BirFunctionDeclaration {
            Name = "probe", Static = true, DeclarationId = "char-code-invoke-probe",
            Params = new[] { new BirParameter { Name = "character", Type = new TypeNode.Fqn("kotlin.Char") } },
            Ret = new TypeNode.Fqn("kotlin.Unit"), Body = statements,
        } },
    };
}
