using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class CollectionIdentityRecord
{
    internal static void SourceSelfTest()
    {
        var variable = new TypeNode.Tv("type", 0);
        var text = new TypeNode.Fqn("kotlin.String");
        var list = new TypeNode.Fqn("kotlin.collections.List", new TypeNode[] { text });
        var nested = new TypeNode.Fqn("Owner", new TypeNode[] { list });
        var mutable = new TypeNode.Fqn("kotlin.collections.MutableList", new TypeNode[] { text });
        var frame = new NullableRepresentationFrame(1, Array.Empty<int>());
        var original = SourceTypeFacts.Empty.Merge(new TypeNode.Fqn("Earlier"), null,
            new Dictionary<int, TypeNode[]> { [0] = new TypeNode[] { new TypeNode.Fqn("EarlierBound") } }).MergeNullableFrame(frame);
        var method = new BirFunctionDeclaration { Name = "untouched", Ret = nested };
        var owner = new BirClassDefinition { Name = "Owner", Kind = "class", Base = nested,
            Interfaces = new TypeNode[] { mutable, new TypeNode.Fqn("Other", new TypeNode[] { new TypeNode.Oblivious(list) }) },
            TypeParams = new[] { new BirTypeParameter { Name = "T", Constraints = new TypeNode[] { nested, variable } } },
            KotlinSupertypesPre = original, Methods = new[] { method } };
        var plain = new BirInterfaceDefinition { Name = "Plain", Kind = "interface", Base = list,
            Interfaces = new TypeNode[] { new TypeNode.Array(list) } };
        var file = new BirFileDeclaration { Types = new IBirTypeDeclaration[] { owner, plain } };
        var types = file.Types.Value;
        var edges = owner.Interfaces.Value;
        RecordTypeEdges(file);
        var facts = owner.KotlinSupertypesPre.Value;
        if (!ReferenceEquals(file.Types.Value, types) || !ReferenceEquals(owner.Interfaces.Value, edges)
            || !ReferenceEquals(owner.Methods.Value[0], method) || method.CollIdentityRet.IsPresent
            || facts.Base != new TypeNode.Fqn("Earlier") || facts.Bounds[0][0] != new TypeNode.Fqn("EarlierBound")
            || !ReferenceEquals(facts.NullableFrame, frame) || facts.Interfaces.Count != 2
            || ReferenceEquals(facts.Interfaces[0], mutable) || plain.KotlinSupertypesPre.IsPresent)
            throw new InvalidOperationException("Typed collection edges lost first-writer source facts, frame, ownership or position policy");
        var once = facts.FrozenText;
        RecordTypeEdges(file);
        if (owner.KotlinSupertypesPre.Value.FrozenText != once)
            throw new InvalidOperationException("Typed collection edge recording duplicated source facts");
        Console.WriteLine("[typed collection type edges] self-test OK (nested policy, source merge, frames, ownership and member clock)");
    }
}
