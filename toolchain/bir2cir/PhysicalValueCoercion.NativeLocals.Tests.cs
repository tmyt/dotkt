using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class PhysicalValueCoercion
{
    static void NativeLocalAllocationSelfTest()
    {
        var variable = new TypeNode.Tv("method", 0);
        var integer = new TypeNode.Fqn("kotlin.Int");
        var erased = new TypeNode.Fqn("object");
        (List<IBirStatement> Body, BirParameter[] Parameters, BirCallStatic Call, BirVariable Local) Fixture()
        {
            var parameters = new[] {
                new BirParameter { Name = "generic", Type = erased },
                new BirParameter { Name = "concrete", Type = erased },
                new BirParameter { Name = "reference", Type = new TypeNode.ByRef(variable) },
            };
            var local = new BirVariable { Name = "local", Type = erased, Init = new BirLocal { Name = "generic" } };
            var call = new BirCallStatic { Owner = new TypeNode.Fqn("Native"), Method = "Write",
                Sig = new TypeNode[] { new TypeNode.ByRef(variable), new TypeNode.ByRef(integer),
                    new TypeNode.ByRef(variable), new TypeNode.ByRef(variable) },
                Args = new IBirExpression[] { new BirByRefOf { Inner = new BirLocal { Name = "generic" } },
                    new BirLocal { Name = "concrete" }, new BirLocal { Name = "local" }, new BirLocal { Name = "reference" } } };
            return (new List<IBirStatement> { new BirVariable { Name = "$nativeArgument$0", Type = erased }, local,
                new BirExpressionStatement { Expr = call } }, parameters, call, local);
        }

        var early = Fixture();
        TypedPhysicalValueCoercion.AllocateNativeLocals(early.Body.Cast<BirGraphValue>(), early.Parameters,
            early.Body, genericParametersOnly: true);
        if (early.Body.Count != 4 || early.Body[0] is not BirVariable { Name.Value: "$nativeArgument$1", Type.Value: var earlyType,
                Init.Value: BirLocal { Name.Value: "generic" } } || earlyType != variable
            || ((BirLocal)((BirByRefOf)early.Call.Args.Value[0]).Inner.Value).Name.Value != "$nativeArgument$1"
            || ((BirLocal)early.Call.Args.Value[1]).Name.Value != "concrete"
            || ((BirLocal)early.Call.Args.Value[2]).Name.Value != "local"
            || ((BirLocal)early.Call.Args.Value[3]).Name.Value != "reference"
            || early.Local.Type.Value != erased || early.Parameters[0].Type.Value != erased)
            throw new InvalidOperationException("Early native storage allocation changed non-parameter/concrete/already-byref eligibility or incoming parameter identity");

        var full = Fixture();
        TypedPhysicalValueCoercion.AllocateNativeLocals(full.Body.Cast<BirGraphValue>(), full.Parameters, full.Body);
        if (full.Body.Count != 5
            || full.Body[0] is not BirVariable { Name.Value: "$nativeArgument$1", Type.Value: var first }
            || full.Body[1] is not BirVariable { Name.Value: "$nativeArgument$2", Type.Value: var second }
            || first != variable || second != integer || full.Local.Type.Value != variable
            || ((BirLocal)full.Call.Args.Value[1]).Name.Value != "$nativeArgument$2"
            || ((BirLocal)full.Call.Args.Value[3]).Name.Value != "reference"
            || full.Parameters[0].Type.Value != erased || full.Parameters[1].Type.Value != erased)
            throw new InvalidOperationException("Default native storage allocation changed local eligibility, prefix order, fresh names or published parameter slots");
    }
}
