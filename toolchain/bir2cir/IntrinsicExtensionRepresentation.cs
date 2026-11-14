using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

// At a trusted intrinsic boundary, the selected CLR declaration owns its signature.
// Stored generic values cross back to ordinary Kotlin value slots explicitly.
static partial class IntrinsicExtensionRepresentation
{
    internal static void SelfTest()
    {
        var ordinary = new TypeNode.Tv("method", 2);
        var storage = new TypeNode.Tv("method", 5);
        var argument = new BirCallStatic { Method = "EvaluateOnce" };
        var call = new BirNativeInstance { Method = "Exchange", ArgTypes = new TypeNode[] { ordinary },
            Args = new IBirExpression[] { argument }, Ret = ordinary };
        var adapted = Adapt(call, new TypeNode[] { storage }, storage);
        if (adapted is not BirCast { E.Value: BirNativeInstance same } cast
            || cast.Type.Value != ordinary || !ReferenceEquals(same, call)
            || call.Args.Value[0] is not BirCast { E.Value: BirCallStatic original } value
            || value.Type.Value != storage || original.Method.Value != "EvaluateOnce"
            || ReferenceEquals(original, argument)
            || call.ArgTypes.Value[0] != storage || call.Ret.Value != storage)
            throw new InvalidOperationException("Intrinsic extension lost its ordinary/storage signature boundary");
        var local = new BirLocal { Name = "x" };
        var unchanged = new BirNativeInstance { Args = new IBirExpression[] { local },
            ArgTypes = new TypeNode[] { ordinary }, Ret = new TypeNode.Fqn("kotlin.Unit") };
        if (!ReferenceEquals(Adapt(unchanged, new TypeNode[] { ordinary }, new TypeNode.Fqn("System.Void")), unchanged)
            || !ReferenceEquals(unchanged.Args.Value[0], local))
            throw new InvalidOperationException("Intrinsic extension converted an unchanged slot or a void result");
        Console.WriteLine("[intrinsic extension representation] self-test OK (storage arguments, ordinary result, single evaluation, unchanged slots)");
    }
}
