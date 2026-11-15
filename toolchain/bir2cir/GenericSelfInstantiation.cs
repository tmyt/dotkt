using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

// bir2cir — GenericSelfInstantiation (bundle-6 P5 BUG A part-2): a lifted GENERIC anon-object / closure class
// (`dotkt_obj*<T>`) emits its SELF instance accesses with the BARE type name as `ownerType`
// (`dotkt_obj144`, no type args) — so at runtime .NET throws InvalidOperationException "the method itself or
// the containing type is not fully instantiated": the emitted `this.get_nextState()` targets the OPEN generic
// type definition instead of the constructed self `dotkt_obj144<!0>`.
//
// A NORMAL generic class already emits its self-calls with the constructed token `Owner[gp:T]` (IndexingIterable
// etc. resolve fine); the lifted-anon-object lowering path is the one that leaves the self owner OPEN. Per the
// BIR-type-token contract (kotc emits the FQN IDENTITY, bir2cir/ilemit DERIVE the CLR resolution — the `[gp:T]`
// instantiation), deriving the constructed self here is exactly bir2cir's job.
//
// Rewrite, within any generic local type T (typeParams p1..pn): every callInstance/field/setField whose
// `ownerType` bare-equals T's own name (no existing `[`) -> `T[gp:p1,...,gp:pn]`. Codex-confirmed guardrails:
// only inside a generic declaration, only the executable instance-access shapes, never a double-instantiation,
// never a static `owner`/base/interface token. BIR types are top-level (lifted classes are extracted flat), so
// a per-type walk is lexically sound. Runs BEFORE BirTypeLowering (which then lowers `T[gp:...]` to the CLR
// constructed generic).
static partial class GenericSelfInstantiation
{
    internal static void ApplyAll(IReadOnlyList<BirFileDeclaration> roots)
    {
        foreach (var root in roots)
        foreach (var declaration in root.Types.OrEmpty())
        {
            if (!declaration.Name.HasValue) continue;
            // Placement has split the captured prefix from declared parameters. Both still participate in !N.
            var count = TypeParameterFrame.Count(declaration);
            if (count == 0) continue;
            var name = declaration.Name.Value;
            var inst = new TypeNode.Fqn(name,
                Enumerable.Range(0, count).Select(index => (TypeNode)new TypeNode.Tv("type", index)).ToArray());
            Walk((BirGraphValue)declaration, name, inst);
        }
    }

    static void Walk(BirGraphValue node, string self, TypeNode.Fqn inst)
    {
        if (node == null || node is IBirExecutablePayload) return;
        bool Bare(TypeNode type) => type is TypeNode.Fqn { Args: null } owner && owner.Name == self;
        // Only the three original instance access shapes; never static, base/interface, or an existing application.
        switch (node) {
            case BirCallInstance call when Bare(call.OwnerType.OrDefault()):
                call.OwnerType = BirGraphValue.CopyType(inst); break;
            case BirFieldGet field when Bare(field.OwnerType.OrDefault()):
                field.OwnerType = BirGraphValue.CopyType(inst); break;
            case BirSetField field when Bare(field.OwnerType.OrDefault()):
                field.OwnerType = BirGraphValue.CopyType(inst); break;
        }
        node.VisitChildren(child => Walk(child, self, inst));
    }
}
