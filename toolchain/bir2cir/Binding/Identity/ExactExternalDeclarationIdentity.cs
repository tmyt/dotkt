using System.Linq;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

// A declaration edge or MethodImpl owner is CLR metadata, not a Kotlin lookup token. Keep every referenced
// base/interface and descriptor owner on the exact TypeDef identity selected by bir2cir. In particular, a semantic
// @ClrTypeAlias edge (`ICollection` with one structured argument) and a descriptor owner (`ICollection`1`) must not
// reach ilemit as two spellings of the same interface: walking both would emit the same MethodImpl twice. Nested
// external identities also retain their `Outer`N+Inner`M ownership instead of asking ilemit to reconstruct it from a
// flattened source name.
static partial class ExactExternalDeclarationIdentity
{
    internal static void Apply(BirFileDeclaration root, ReferenceMetadataIndex refs)
    {
        TypeNode ExactSource(TypeNode current)
        {
            if (current is not TypeNode.Fqn type) return current;
            var exact = refs.ExactReflectedOwner(type.Name, type.Args?.Length ?? 0);
            return exact == type.Name ? current : new TypeNode.Fqn(exact,
                type.Args?.Select(BirGraphValue.CopyType).ToArray());
        }
        // Type placement has already flattened the owning type vector. These
        // current edges and attached MethodImpls are physical declaration facts,
        // not arbitrary executable owner hints or frozen Kotlin source metadata.
        foreach (var type in root.Types.OrEmpty())
        {
            if (type.Base.HasValue) type.Base = ExactSource(type.Base.Value);
            if (type.Interfaces.HasValue)
                for (var index = 0; index < type.Interfaces.Value.Length; index++)
                    type.Interfaces.Value[index] = ExactSource(type.Interfaces.Value[index]);
            foreach (var method in type.Methods.OrEmpty())
                foreach (var descriptor in method.ClrInterfaceImpls.OrEmpty().Concat(method.ClrBaseImpls.OrEmpty()))
                    if (descriptor.Owner.HasValue) descriptor.Owner = ExactSource(descriptor.Owner.Value);
        }
    }
}
