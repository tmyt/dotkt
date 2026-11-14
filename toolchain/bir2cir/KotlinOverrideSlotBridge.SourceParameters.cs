using System.Text.Json.Nodes;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class KotlinOverrideSlotBridge
{
    // Snapshot only the parameter shapes, before inner-application projection.
    // The owning records themselves remain the exact continuing declarations.
    internal static PendingSourceParameters CaptureSourceParameters(IEnumerable<BirFileDeclaration> roots)
        => new(roots.ToArray());

    internal sealed class PendingSourceParameters
    {
        sealed record MethodRow(BirFunctionDeclaration Method, TypeNode[] Parameters);
        sealed record OwnerRow(int File, int Position, IBirTypeDeclaration Owner, MethodRow[] Methods);
        BirFileDeclaration[] files;
        OwnerRow[] owners;
        bool consumed;

        internal PendingSourceParameters(BirFileDeclaration[] roots)
        {
            files = roots;
            // This is the same last-declaration-wins hierarchy inventory as
            // SupertypeGraph.Collect, not a new overload or member selection.
            var selected = new Dictionary<string, OwnerRow>(StringComparer.Ordinal);
            for (var file = 0; file < roots.Length; file++)
            {
                var declarations = roots[file].Types.OrEmpty();
                for (var position = 0; position < declarations.Length; position++)
                {
                    var owner = declarations[position];
                    if (!owner.Name.HasValue) continue;
                    selected[owner.Name.Value] = new(file, position, owner,
                        owner.Methods.OrEmpty().Select(method => new MethodRow(method,
                            method.Params.OrEmpty().Select(parameter => BirGraphValue.CopyType(parameter.Type.OrDefault())).ToArray())).ToArray());
                }
            }
            owners = selected.Values.ToArray();
        }

        // The coordinator supplies the one actual projection. Associate by the
        // retained occurrence and ordinal, never by a renamed member or its body.
        internal IReadOnlyDictionary<JsonObject, TypeNode[]> BindDom(IReadOnlyList<JsonNode> roots)
        {
            if (consumed || roots.Count != files.Length) throw Mismatch();
            var result = new Dictionary<JsonObject, TypeNode[]>(ReferenceEqualityComparer.Instance);
            foreach (var row in owners)
            {
                var current = files[row.File].Types.OrEmpty();
                if (row.Position >= current.Length || !ReferenceEquals(current[row.Position], row.Owner)
                    || !row.Owner.Methods.OrEmpty().SequenceEqual(row.Methods.Select(method => method.Method),
                        ReferenceEqualityComparer.Instance)
                    || roots[row.File] is not JsonObject file
                    || (file["fileClass"] as JsonValue)?.GetValue<string>() != files[row.File].FileClass.OrDefault()
                    || file["types"] is not JsonArray types || row.Position >= types.Count
                    || types[row.Position] is not JsonObject owner
                    || (owner["name"] as JsonValue)?.GetValue<string>() != row.Owner.Name.OrDefault()) throw Mismatch();
                var methods = owner["methods"] as JsonArray;
                if ((methods?.Count ?? 0) != row.Methods.Length) throw Mismatch();
                for (var index = 0; index < row.Methods.Length; index++)
                {
                    var source = row.Methods[index];
                    if (methods[index] is not JsonObject method
                        || (method["name"] as JsonValue)?.GetValue<string>() != source.Method.Name.OrDefault()
                        || (method[DeclarationIdentityBinding.Key] as JsonValue)?.GetValue<string>() != source.Method.DeclarationId.OrDefault())
                        throw Mismatch();
                    result.Add(method, source.Parameters);
                }
            }
            consumed = true;
            files = null;
            owners = null;
            return result;
        }

        static InvalidOperationException Mismatch()
            => new("Source override parameter snapshot has no exact DOM continuation");
    }
}
