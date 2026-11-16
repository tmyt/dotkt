using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

// This is the source identity selection, not CharSequence's later optional string representation.
// Declaration names and frozen source facts stay Kotlin; all structured use-site references use the bridge.
// Run before module implementer detection, CharSeqStringLowering and synthetic
// interface materialization so all three consume the same selected identity.
static class TypedCharSequenceIdentity
{
    internal static void ApplyAll(IReadOnlyList<BirFileDeclaration> roots)
    {
        TypeNode Changed(TypeNode type) => type != null && !TypeNodeDataEquality.Equal(type, RewriteType(type))
            ? BirGraphValue.CopyType(type) : null;
        void Walk(BirGraphValue node)
        {
            if (node is IBirExecutablePayload) return;
            if (node is IBirTypeDeclaration owner && owner.Kind.OrDefault() is "class" or "interface" or "struct" or "enum")
            {
                var baseType = Changed(owner.Base.OrDefault());
                var interfaces = owner.Interfaces.OrEmpty().Select(Changed).Where(type => type != null).ToArray();
                if (baseType != null || interfaces.Length != 0)
                    NullableRepresentationViews.SetTypeFacts(node,
                        (NullableRepresentationViews.TypeFacts(node) ?? SourceTypeFacts.Empty).Merge(baseType, interfaces, null));
            }
            if (node is BirCallInstance call && call.OwnerType.OrDefault() is TypeNode.Fqn { Name: "kotlin.CharSequence" })
                call.DeclarationId = default;
            node.RewriteTypeFieldsInPlace(RewriteType);
            node.VisitChildren(Walk);
        }
        foreach (var root in roots) Walk(root);
    }

    static TypeNode RewriteType(TypeNode type) => type switch {
        TypeNode.Fqn value => value with {
            Name = value.Name == "kotlin.CharSequence" ? SharedSyntheticSynthesis.CharSeq : value.Name,
            Args = value.Args?.Select(RewriteType).ToArray(),
        },
        TypeNode.Projection value => value with { Of = RewriteType(value.Of) },
        TypeNode.Fn value => value with {
            Ret = RewriteType(value.Ret), Params = value.Params.Select(RewriteType).ToArray(),
            Recv = value.Recv == null ? null : RewriteType(value.Recv), Ctx = value.Ctx?.Select(RewriteType).ToArray(),
        },
        TypeNode.Nullable value => value with { Of = RewriteType(value.Of) },
        TypeNode.Oblivious value => value with { Of = RewriteType(value.Of) },
        TypeNode.Array value => value with { Elem = RewriteType(value.Elem) },
        TypeNode.ByRef value => value with { Of = RewriteType(value.Of) },
        TypeNode.Ptr value => value with { Of = RewriteType(value.Of) },
        TypeNode.FnPtr value => value with { Ret = RewriteType(value.Ret), Params = value.Params.Select(RewriteType).ToArray() },
        TypeNode.Mod value => value with { M = RewriteType(value.M), Of = RewriteType(value.Of) },
        TypeNode.Tv or TypeNode.Star => type,
        _ => throw new InvalidOperationException("Unaccounted CharSequence type reference: " + type.GetType().Name),
    };
}
