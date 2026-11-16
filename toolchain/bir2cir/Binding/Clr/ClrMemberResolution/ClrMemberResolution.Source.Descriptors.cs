using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class ClrMemberResolution
{
    readonly record struct PlainDescriptorPlan(TypeNode[] Signature, bool ConsumeShapes);

    // Existing declaration vectors are evidence; expression argument types are
    // not. Absence/null is different from an explicitly empty argument vector.
    static PlainDescriptorPlan PlanPlainDescriptor(bool construction, TypeNode[] signature,
        TypeNode[] shapes, TypeNode[] arguments, bool emptyArguments, TypeNode function)
    {
        if (construction)
            // DelegateParams includes the real leading extension receiver;
            // Fn.Params alone is not the target method's complete signature.
            return signature == null && function is TypeNode.Fn fn
                ? new(fn.DelegateParams.Select(BirGraphValue.CopyType).ToArray(), false) : default;
        if (signature != null) return new(null, true);
        if (shapes != null) return new(shapes.Select(BirGraphValue.CopyType).ToArray(), true);
        if (arguments != null) return new(arguments.Select(BirGraphValue.CopyType).ToArray(), false);
        return emptyArguments ? new(Array.Empty<TypeNode>(), false) : default;
    }

    internal static void EnsurePlainCallDescriptors(BirGraphValue root)
    {
        void Walk(BirGraphValue node)
        {
            if (node is IBirExecutablePayload) return;
            node.VisitOwnChildSlots((_, role, slot) => {
                if (role != BirGenericFrameRole.FrozenSourceFact) Walk(slot.Value);
            });
            switch (node)
            {
                case BirNewDelegate call:
                    var direct = PlanPlainDescriptor(true, call.Sig.OrDefault(), null, null, false, call.FuncType.OrDefault());
                    if (direct.Signature != null) call.Sig = direct.Signature;
                    break;
                case BirNewBoundDelegate call:
                    var bound = PlanPlainDescriptor(true, call.Sig.OrDefault(), null, null, false, call.FuncType.OrDefault());
                    if (bound.Signature != null) call.Sig = bound.Signature;
                    break;
                case BirCallStatic call:
                    var stat = PlanPlainDescriptor(false, call.Sig.OrDefault(), call.ShapeTypes.OrDefault(),
                        call.ArgTypes.OrDefault(), call.Args.HasValue && call.Args.Value.Length == 0, null);
                    if (stat.Signature != null) call.Sig = stat.Signature;
                    if (stat.ConsumeShapes) call.ShapeTypes = default;
                    break;
                case BirCallInstance call:
                    var instance = PlanPlainDescriptor(false, call.Sig.OrDefault(), call.ShapeTypes.OrDefault(),
                        call.ArgTypes.OrDefault(), call.Args.HasValue && call.Args.Value.Length == 0, null);
                    if (instance.Signature != null) call.Sig = instance.Signature;
                    if (instance.ConsumeShapes) call.ShapeTypes = default;
                    break;
                case BirConstrainedCall call:
                    var constrained = PlanPlainDescriptor(false, call.Sig.OrDefault(), call.ShapeTypes.OrDefault(),
                        call.ArgTypes.OrDefault(), call.Args.HasValue && call.Args.Value.Length == 0, null);
                    if (constrained.Signature != null) call.Sig = constrained.Signature;
                    if (constrained.ConsumeShapes) call.ShapeTypes = default;
                    break;
            }
        }
        Walk(root);
    }
}
