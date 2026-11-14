using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class IntrinsicExtensionRepresentation
{
    internal static IBirExpression Adapt(BirNativeInstance call, TypeNode[] parameters, TypeNode result)
    {
        var arguments = call.Args.Value;
        var original = call.ArgTypes.Value;
        if (arguments.Length != parameters.Length || original.Length != parameters.Length)
            throw new InvalidOperationException("Intrinsic extension parameter vector does not match its CLR declaration");
        for (var index = 0; index < parameters.Length; index++)
            if (parameters[index] != original[index])
            {
                if (parameters[index] is TypeNode.ByRef || original[index] is TypeNode.ByRef)
                    throw new InvalidOperationException("Intrinsic extension cannot convert a managed-reference representation");
                arguments[index] = new BirCast { Type = BirGraphValue.CopyType(parameters[index]),
                    E = arguments[index] == null ? default : OptionalField<IBirExpression>.Of(
                        (IBirExpression)((BirGraphValue)arguments[index]).DeepClone()) };
            }
        call.ArgTypes = parameters.Select(BirGraphValue.CopyType).ToArray();
        var logical = call.Ret.OrDefault();
        call.Ret = BirGraphValue.CopyType(result);
        if (call.Sty.HasValue) call.Sty = BirGraphValue.CopyType(result);
        return logical != null && logical != result && result is not TypeNode.Fqn { Name: "System.Void" or "void" }
            ? new BirCast { Type = BirGraphValue.CopyType(logical), E = call } : call;
    }
}
