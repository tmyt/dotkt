using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class BackingFieldRename
{
    // Report exact continuing occurrences, not names to be reselected by a
    // native-location ledger after its original eligibility was captured.
    internal sealed record Rename(BirGraphValue Owner, BirFieldDeclaration Field, string OldName, string NewName);

    internal static IReadOnlyList<Rename> ApplyAll(IReadOnlyList<BirFileDeclaration> roots)
    {
        var renames = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        var staticRenames = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        var bases = new Dictionary<string, string>(StringComparer.Ordinal);
        var changed = new List<Rename>();
        foreach (var root in roots)
        {
            RenameDeclarations(root, root.FileClass.OrDefault(), null, root.Fields.OrEmpty(), root.Properties.OrEmpty());
            // Final placement has put nested CLR TypeDefs in this same vector.
            // Do not discover another declaration inside a saved executable fact.
            foreach (var type in root.Types.OrEmpty())
                RenameDeclarations((BirGraphValue)type, type.Name.OrDefault(), type.Base.OrDefault(),
                    type.Fields.OrEmpty(), type.Properties.OrEmpty());
        }
        foreach (var root in roots) RewriteUses(root);
        return changed;

        void RenameDeclarations(BirGraphValue declaration, string rawName, TypeNode baseType,
            IReadOnlyList<BirFieldDeclaration> fields, IReadOnlyList<BirPropertyDeclaration> properties)
        {
            if (rawName == null) return;
            var owner = ReferenceMetadataIndex.BareOwnerFqn(rawName);
            if (baseType is TypeNode.Fqn parent) bases[owner] = ReferenceMetadataIndex.BareOwnerFqn(parent.Name);
            var propertyNames = new HashSet<string>(properties.Where(IsReceiverless)
                .Select(property => property.Name.OrDefault()).Where(name => name != null), StringComparer.Ordinal);
            var declared = new HashSet<string>(fields.Select(field => field.Name.OrDefault())
                .Where(name => name != null), StringComparer.Ordinal);
            var instanceNames = new HashSet<string>(fields.Where(field => !field.Static.OrDefault())
                .Select(field => field.Name.OrDefault()).Where(name => name != null), StringComparer.Ordinal);
            Dictionary<string, string> map = null, staticMap = null;
            foreach (var field in fields)
            {
                if (field.Name.OrDefault() is not string name) continue;
                var isStatic = field.Static.OrDefault();
                var selected = isStatic
                    ? staticMap ??= new Dictionary<string, string>(StringComparer.Ordinal)
                    : map ??= new Dictionary<string, string>(StringComparer.Ordinal);
                // Ordinary fields are lookup barriers too. A static singleton
                // beside same-named instance storage is a different member.
                selected[name] = name;
                if (!propertyNames.Contains(name) || isStatic && instanceNames.Contains(name)) continue;
                var physical = Mangle(name);
                if (declared.Contains(physical))
                    throw new InvalidOperationException(
                        $"bir2cir: '{owner}' already declares a field named '{physical}'; cannot rename the backing field of property '{name}'");
                field.Name = physical;
                field.Attrs = field.Attrs.OrEmpty().Append(new BirAttribute {
                    Attr = new TypeNode.Fqn(CompilerGeneratedAttr), AttrExternal = true,
                    ArgTypes = Array.Empty<TypeNode>(), Args = Array.Empty<IBirAttributeValue>(),
                }).ToArray();
                selected[name] = physical;
                changed.Add(new(declaration, field, name, physical));
            }
            // Bare physical owner keys may span several facade/type records.
            // Keep all earlier declarations and let each later name win.
            Merge(renames, owner, map);
            Merge(staticRenames, owner, staticMap);
        }

        void RewriteUses(BirGraphValue node)
        {
            if (node is IBirExecutablePayload) return;
            string Renamed(TypeNode owner, string name, bool isStatic) =>
                owner is TypeNode.Fqn fqn && name != null
                    ? Resolve(ReferenceMetadataIndex.BareOwnerFqn(fqn.Name), name,
                        isStatic ? staticRenames : renames, bases) : null;
            switch (node)
            {
                case BirFieldGet field:
                    if (Renamed(field.OwnerType.OrDefault(), field.Name.OrDefault(), false) is { } read) field.Name = read;
                    break;
                case BirSetField field:
                    if (Renamed(field.OwnerType.OrDefault(), field.Name.OrDefault(), false) is { } write) field.Name = write;
                    break;
                case BirFieldSetExpression field:
                    if (Renamed(field.OwnerType.OrDefault(), field.Name.OrDefault(), false) is { } expression) field.Name = expression;
                    break;
                case BirStaticFieldGet field:
                    if (Renamed(field.OwnerType.OrDefault(), field.Name.OrDefault(), true) is { } staticRead) field.Name = staticRead;
                    break;
                case BirStaticFieldSet field:
                    if (Renamed(field.OwnerType.OrDefault(), field.Name.OrDefault(), true) is { } staticWrite) field.Name = staticWrite;
                    break;
            }
            // There is deliberately no lateinitGet case: its distinct storage
            // fact was not part of the field-addressing owner being migrated.
            node.VisitOwnChildSlots((_, role, slot) => {
                if (role != BirGenericFrameRole.FrozenSourceFact) RewriteUses(slot.Value);
            });
        }
    }

    // Getter descriptors take precedence, including an empty vector; only
    // absence/null permits the setter's final value slot to prove ownership.
    static bool IsReceiverless(BirPropertyDeclaration property) =>
        property.GetSig.HasValue ? property.GetSig.Value.Length == 0
        : property.SetSig.HasValue && property.SetSig.Value.Length == 1;
}
