using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class RefBodySquash
{
    internal static void Squash(BirFileDeclaration file, Action<BirGraphValue> retireDiscardedSubtree)
    {
        ArgumentNullException.ThrowIfNull(retireDiscardedSubtree);
        SquashMethods(file.Methods.OrEmpty());
        foreach (var type in file.Types.OrEmpty())
        {
            SquashMethods(type.Methods.OrEmpty());
            foreach (var constructor in type.Ctors.OrEmpty())
                if (constructor.Body.HasValue)
                {
                    Retire(constructor.Body.Value);
                    constructor.Body = SourceThrowStubBody();
                }
        }
        void SquashMethods(IEnumerable<BirFunctionDeclaration> methods)
        {
            foreach (var method in methods)
                if (!method.Abstract.OrDefault() && method.Body.HasValue)
                {
                    Retire(method.Body.Value);
                    method.Body = SourceThrowStubBody();
                }
        }
        void Retire(IBirStatement[] body)
        {
            // Retire each exact owned forest while still attached. The retained
            // method/ctor and all prologue, signature and metadata slots survive.
            foreach (var statement in body) retireDiscardedSubtree((BirGraphValue)statement);
        }
    }
    static IBirStatement[] SourceThrowStubBody() => new IBirStatement[] {
        new BirThrow { Value = new BirNewClr { Type = new TypeNode.Fqn("System.NotImplementedException"),
            ArgTypes = Array.Empty<TypeNode>(), Args = Array.Empty<IBirExpression>() } },
    };
}
