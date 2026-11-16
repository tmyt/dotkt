using System;
using System.Linq;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class StringCharSequenceBridge
{
    internal static void CallSignatureSelfTest()
    {
        var shape = new TypeNode[] { new TypeNode.Tv("method", 0), new TypeNode.Fqn(CharSeq) };
        var value = new BirLocal { Name = "value" };
        var text = new BirConstant { Type = new TypeNode.Fqn("System.String"), Value = new BirLiteral.TextLiteral("text") };
        var witness = new BirLocal { Name = "witness" };
        var owner = new TypeNode.Fqn("Owner", new TypeNode[] { new TypeNode.Tv("type", 5) });
        var methodArguments = new TypeNode[] { new TypeNode.Tv("method", 4) };
        var call = new BirCallInstance { Method = "ordinaryGeneric", ShapeTypes = shape, OwnerType = owner,
            TypeArgs = methodArguments, DeclarationId = "selected-declaration",
            Recv = new BirLocal { Name = "receiver" }, Args = new IBirExpression[] { value, text, witness } };
        var physical = new BirCallStatic { Method = "physical", Sig = new TypeNode[] { new TypeNode.Fqn("System.String") },
            ShapeTypes = new TypeNode[] { new TypeNode.Fqn(CharSeq) }, Args = new IBirExpression[] {
                new BirConstant { Type = new TypeNode.Fqn("System.String"), Value = new BirLiteral.TextLiteral("text") },
            } };
        var physicalBefore = physical.DeepClone();
        var method = new BirFunctionDeclaration { Name = "caller", Ret = new TypeNode.Fqn("object"),
            Params = new[] { new BirParameter { Name = "value", Type = new TypeNode.Tv("method", 4) },
                new BirParameter { Name = "witness", Type = new TypeNode.Fqn("object") },
                new BirParameter { Name = "receiver", Type = owner } },
            TypeParams = Enumerable.Range(0, 5).Select(index => new BirTypeParameter { Name = "M" + index }).ToArray(),
            Body = new IBirStatement[] { new BirExpressionStatement { Expr = call }, new BirExpressionStatement { Expr = physical } },
        };
        var file = new BirFileDeclaration { Types = new IBirTypeDeclaration[] { new BirClassDefinition {
            Name = "Caller", Kind = "class",
            TypeParams = Enumerable.Range(0, 6).Select(index => new BirTypeParameter { Name = "T" + index }).ToArray(),
            Methods = new[] { method },
        } } };
        Apply(file, null, null);
        if (call.Sig.IsPresent || !ReferenceEquals(call.ShapeTypes.Value, shape)
            || call.ShapeTypes.Value is not [TypeNode.Tv { Scope: "method", I: 0 }, TypeNode.Fqn { Name: CharSeq }]
            || !ReferenceEquals(call.OwnerType.Value, owner)
            || call.OwnerType.Value is not TypeNode.Fqn { Name: "Owner", Args: [TypeNode.Tv { Scope: "type", I: 5 }] }
            || !ReferenceEquals(call.TypeArgs.Value, methodArguments)
            || call.TypeArgs.Value is not [TypeNode.Tv { Scope: "method", I: 4 }]
            || call.DeclarationId.OrDefault() != "selected-declaration" || call.Args.Value.Length != 3
            || !ReferenceEquals(call.Args.Value[0], value) || !ReferenceEquals(call.Args.Value[2], witness)
            || call.Args.Value[1] is not BirNew { Type.Value: TypeNode.Fqn { Name: Adapter }, Args.Value: [var adapted] }
            || !ReferenceEquals(adapted, text) || !physical.StructurallyEquals(physicalBefore))
            throw new InvalidOperationException("Call bridge changed source arity, witnesses, selected identity, or generic frames");
        Console.WriteLine("[call representation] self-test OK (source shape, physical precedence, witness, identity, generic frames)");
    }
}
