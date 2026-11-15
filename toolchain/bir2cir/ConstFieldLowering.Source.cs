using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

// Kotlin const selects a CLR literal field and metadata Constant, not executable
// initializer storage. Local folded reads retain the declaration's Kotlin type.
static partial class ConstFieldLowering
{
    sealed record Literal(TypeNode Type, OptionalField<BirLiteral> Value);

    internal static void ApplyAll(IReadOnlyList<BirFileDeclaration> roots, ReferenceMetadataIndex refs, bool referenceBuild)
    {
        var local = new Dictionary<(string Owner, string Name), Literal>();
        foreach (var root in roots)
        {
            LowerDeclarations(root.Fields.OrEmpty(), root.FileClass.OrDefault());
            foreach (var type in root.Types.OrEmpty()) LowerDeclarations(type.Fields.OrEmpty(), type.Name.OrDefault());
        }
        foreach (var root in roots) Rewrite(root);

        void LowerDeclarations(IEnumerable<BirFieldDeclaration> declarations, string owner)
        {
            foreach (var field in declarations)
            {
                var declarationType = field.Type.OrDefault();
                if (!field.Const.OrDefault()) continue;
                var name = field.Name.OrDefault() ?? "<unnamed>";
                if (!field.Static.OrDefault()) throw new InvalidOperationException($"const field '{name}' is not static");
                if (field.Init.OrDefault() is not BirConstant init || !init.Value.IsPresent)
                    throw new InvalidOperationException($"const field '{name}' has no compile-time constant initializer");
                if (referenceBuild)
                {
                    var semantic = declarationType ?? throw new InvalidOperationException($"const field '{name}' has no declaration type");
                    field.KotlinType = BirGraphValue.CopyType(semantic);
                    field.Type = BirTypeLowering.LowerType(semantic, refBuild: false, force: true, typeArg: false);
                }
                field.Constant = init.Value;
                field.Init = default;
                field.Const = default;
                if (owner != null && field.Name.OrDefault() is string fieldName)
                    local.Add((owner, fieldName), new(declarationType ?? throw new InvalidOperationException(
                        $"const field '{owner}.{fieldName}' has no declaration type"), field.Constant));
            }
        }
        BirGraphValue Rewrite(BirGraphValue node)
        {
            if (node == null || node is IBirExecutablePayload) return node;
            node.VisitChildSlots(slot => slot.Replace(Rewrite(slot.Value)));
            if (node is not BirStaticFieldGet field || field.OwnerType.OrDefault() is not TypeNode.Fqn owner
                || field.Name.OrDefault() is not string name) return node;
            if (!local.TryGetValue((owner.Name, name), out var literal))
            {
                if (!refs.TryResolveLiteralFieldValue(owner, name, out var type, out var value)) return node;
                literal = new(type, value);
            }
            return new BirConstant { Type = BirGraphValue.CopyType(literal.Type), Value = literal.Value };
        }
    }
}
