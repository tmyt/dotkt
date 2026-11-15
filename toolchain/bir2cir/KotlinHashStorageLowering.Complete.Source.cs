using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class KotlinHashStorageLowering
{
    // Close generated native signatures at their final value-representation
    // clock. Presence, not the marker's boolean value, requests completion.
    internal static void Complete(BirGraphValue root)
    {
        void Walk(BirGraphValue node)
        {
            if (node == null || node is IBirExecutablePayload) return;
            node.VisitOwnChildSlots((_, role, slot) => {
                if (role != BirGenericFrameRole.FrozenSourceFact) Walk(slot.Value);
            });
            if (node is BirNewClr construction && construction.KotlinHashStorageSignature.IsPresent)
            {
                construction.KotlinHashStorageSignature = default;
                var key = ((TypeNode.Fqn)construction.Type.Value).Args[0];
                var comparer = new TypeNode.Fqn("DotKt.Runtime.CompilerServices.NativeHashComparer", new[] { BirGraphValue.CopyType(key) });
                var call = new BirCallStatic {
                    Owner = new TypeNode.Fqn("DotKt.Runtime.CompilerServices.KotlinHashComparerKt"),
                    Method = "kotlinHashComparer", TypeArgs = new[] { BirGraphValue.CopyType(key) }, Sig = Array.Empty<TypeNode>(),
                    Ret = BirGraphValue.CopyType(comparer), Args = Array.Empty<IBirExpression>(),
                };
                construction.ArgTypes = construction.ArgTypes.Value.Append(comparer).ToArray();
                construction.Args = construction.Args.Value.Append<IBirExpression>(call).ToArray();
            }
            else if (node is BirNativeInstance call && call.KotlinHashStorageSignature.IsPresent)
            {
                call.KotlinHashStorageSignature = default;
                call.ArgTypes = ((TypeNode.Fqn)call.Type.Value).Args.Select(BirGraphValue.CopyType).ToArray();
            }
        }
        Walk(root);
    }
}
