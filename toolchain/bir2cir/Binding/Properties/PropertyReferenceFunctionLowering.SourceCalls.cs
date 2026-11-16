using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class PropertyReferenceFunctionLowering
{
    readonly record struct ArgumentSlots(IBirExpression[] Args, TypeNode[] Parameters,
        TypeNode Owner = null, TypeNode[] MethodArguments = null);

    // Read only the declaration vector owned by this operation. A resolved
    // MemberRef or the runtime types of arguments must not replace that source
    // slot contract. Missing vectors leave argument traversal untargeted.
    static ArgumentSlots Arguments(BirGraphValue node) => node switch {
        BirCallStatic value => new(value.Args.OrDefault(),
            SelectParameters(value.ResolvedMemberParams.OrDefault(), value.ShapeTypes.OrDefault(), value.ArgTypes.OrDefault(), value.Sig.OrDefault()),
            value.OwnerType.OrDefault() ?? value.Owner.OrDefault() ?? value.CalleeOwner.OrDefault(), value.TypeArgs.OrDefault()),
        BirCallInstance value => new(value.Args.OrDefault(),
            SelectParameters(value.ResolvedMemberParams.OrDefault(), value.ShapeTypes.OrDefault(), value.ArgTypes.OrDefault(), value.Sig.OrDefault()),
            value.OwnerType.OrDefault(), value.TypeArgs.OrDefault()),
        BirCallLocal value => new(value.Args.OrDefault(), value.Sig.OrDefault(), null, value.TypeArgs.OrDefault()),
        BirConstrainedCall value => new(value.Args.OrDefault(),
            SelectParameters(value.ResolvedMemberParams.OrDefault(), null, value.ArgTypes.OrDefault(), value.Sig.OrDefault()),
            null, value.TypeArgs.OrDefault()),
        BirNativeStatic value => new(value.Args.OrDefault(),
            SelectParameters(value.ResolvedMemberParams.OrDefault(), null, value.ArgTypes.OrDefault(), value.Sig.OrDefault()),
            value.Type.OrDefault(), value.TypeArgs.OrDefault()),
        BirNativeInstance value => new(value.Args.OrDefault(),
            SelectParameters(value.ResolvedMemberParams.OrDefault(), null, value.ArgTypes.OrDefault(), value.Sig.OrDefault()),
            value.Type.OrDefault(), value.TypeArgs.OrDefault()),
        BirGenericStatic value => new(value.Args.OrDefault(), value.ResolvedMemberParams.OrDefault(),
            value.Type.OrDefault(), value.TypeArgs.OrDefault()),
        BirGenericInstance value => new(value.Args.OrDefault(), value.ResolvedMemberParams.OrDefault(),
            value.Type.OrDefault(), value.TypeArgs.OrDefault()),
        BirNew value => new(value.Args.OrDefault(),
            SelectParameters(value.ResolvedMemberParams.OrDefault(), null, value.ArgTypes.OrDefault(), null), value.Type.OrDefault()),
        BirNewClr value => new(value.Args.OrDefault(),
            SelectParameters(value.ResolvedMemberParams.OrDefault(), null, value.ArgTypes.OrDefault(), null), value.Type.OrDefault()),
        BirDelegateInvoke value when value.FuncType.OrDefault() is TypeNode.Fn function
            => new(value.Args.OrDefault(), function.DelegateParams),
        _ => default,
    };
}
