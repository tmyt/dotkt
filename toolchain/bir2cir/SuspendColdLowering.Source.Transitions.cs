using DotKt.Bir;
using DotKt.Bir.Lowering;

static partial class SuspendColdLowering
{
    // The producer reports real graph operations at their owning clock. The
    // pipeline supplies the declaration/proof continuation, not this lowerer.
    internal abstract class SourceDeclarationTransitions
    {
        internal abstract BirGraphValue CloneIndependent(BirGraphValue source);
        internal abstract void RetireDiscardedSubtree(BirGraphValue source);
        internal abstract void RetireMethod(BirFileDeclaration file, IBirTypeDeclaration owner,
            BirFunctionDeclaration method);
        internal abstract void PublishMethods(BirFileDeclaration file, IBirTypeDeclaration owner,
            IReadOnlyList<BirFunctionDeclaration> appended);
        internal abstract void PublishTypes(BirFileDeclaration file, IReadOnlyList<IBirTypeDeclaration> appended);
        internal abstract void RetireTypes(BirFileDeclaration file, IReadOnlyList<IBirTypeDeclaration> removed);
    }
}
