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
        sealed record MaterializedMethod(BirFunctionDeclaration Method, CurrentDeclarationBindings.DeclarationBinding Binding);
        sealed record OwnerRow(int File, int Position, IBirTypeDeclaration Owner, MethodRow[] Methods)
        {
            internal readonly List<MaterializedMethod> Materialized = new();
            internal readonly Dictionary<BirFunctionDeclaration, CurrentDeclarationBindings.DeclarationBinding> Retired
                = new(ReferenceEqualityComparer.Instance);
        }
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

        // The physical producer reports its exact append. These records do not
        // acquire source override eligibility or another parameter snapshot.
        // false means this actual owner was not selected by the original
        // last-declaration-wins inventory; do not match another owner by name.
        internal bool AdmitMaterializedMethods(IBirTypeDeclaration owner, IReadOnlyList<BirFunctionDeclaration> appended,
            CurrentDeclarationBindings bindings)
        {
            if (consumed) throw Mismatch();
            var row = owners.SingleOrDefault(row => ReferenceEquals(row.Owner, owner));
            if (row == null) return false;
            var current = files[row.File].Types.OrEmpty();
            if (row.Position >= current.Length || !ReferenceEquals(current[row.Position], owner)) throw Mismatch();
            var expected = CurrentMethods(row).Concat(appended).ToArray();
            if (expected.Distinct(ReferenceEqualityComparer.Instance).Count() != expected.Length
                || !owner.Methods.OrEmpty().SequenceEqual(expected, ReferenceEqualityComparer.Instance)) throw Mismatch();
            var continuations = appended.Select(method => new MaterializedMethod(method, bindings.GetSource(method))).ToArray();
            row.Materialized.AddRange(continuations);
            return true;
        }

        static IEnumerable<BirFunctionDeclaration> CurrentMethods(OwnerRow row)
            => row.Methods.Select(method => method.Method).Concat(row.Materialized.Select(method => method.Method))
                .Where(method => !row.Retired.ContainsKey(method));

        // Retirement changes current ownership, not the original snapshot's
        // membership or frozen signatures. Only the producer's exact removals
        // may disappear; their captured bindings cross the same frontier once.
        internal bool RetireMethods(IBirTypeDeclaration owner, IReadOnlyList<BirFunctionDeclaration> methods,
            CurrentDeclarationBindings bindings)
        {
            ValidateSourceRows();
            var row = owners.SingleOrDefault(row => ReferenceEquals(row.Owner, owner));
            if (row == null) return false;
            var current = new HashSet<BirFunctionDeclaration>(CurrentMethods(row), ReferenceEqualityComparer.Instance);
            var removed = new HashSet<BirFunctionDeclaration>(methods, ReferenceEqualityComparer.Instance);
            if (removed.Count != methods.Count || methods.Any(method => !current.Contains(method))) throw Mismatch();
            var continuations = methods.Select(method => (Method: method, Binding: bindings.GetCapturedSource(method))).ToArray();
            foreach (var entry in continuations) row.Retired.Add(entry.Method, entry.Binding);
            return true;
        }

        // The next owning passes read the same frozen source shapes before
        // projection. Producer-reported physical appends deliberately have no
        // entry; their current parameters remain the caller's physical input.
        internal IReadOnlyDictionary<BirFunctionDeclaration, TypeNode[]> ReadSourceParameters()
        {
            ValidateSourceRows();
            var result = new Dictionary<BirFunctionDeclaration, TypeNode[]>(ReferenceEqualityComparer.Instance);
            foreach (var row in owners)
                foreach (var method in row.Methods) result.Add(method.Method, method.Parameters);
            return result;
        }

        void ValidateSourceRows()
        {
            if (consumed) throw Mismatch();
            foreach (var row in owners)
            {
                var current = files[row.File].Types.OrEmpty();
                if (row.Position >= current.Length || !ReferenceEquals(current[row.Position], row.Owner)
                    || !row.Owner.Methods.OrEmpty().SequenceEqual(CurrentMethods(row), ReferenceEqualityComparer.Instance))
                    throw Mismatch();
            }
        }

        // The coordinator supplies the one actual projection. Associate by the
        // retained occurrence and ordinal, never by a renamed member or its body.
        internal IReadOnlyDictionary<JsonObject, TypeNode[]> BindDom(IReadOnlyList<JsonNode> roots)
        {
            if (consumed || roots.Count != files.Length) throw Mismatch();
            ValidateSourceRows();
            var result = new Dictionary<JsonObject, TypeNode[]>(ReferenceEqualityComparer.Instance);
            foreach (var row in owners)
            {
                if (roots[row.File] is not JsonObject file
                    || (file["fileClass"] as JsonValue)?.GetValue<string>() != files[row.File].FileClass.OrDefault()
                    || file["types"] is not JsonArray types || row.Position >= types.Count
                    || types[row.Position] is not JsonObject owner
                    || (owner["name"] as JsonValue)?.GetValue<string>() != row.Owner.Name.OrDefault()) throw Mismatch();
                var methods = owner["methods"] as JsonArray;
                if ((methods?.Count ?? 0) != CurrentMethods(row).Count()) throw Mismatch();
                var index = 0;
                foreach (var source in row.Methods)
                {
                    if (row.Retired.TryGetValue(source.Method, out var retired))
                    {
                        result.Add(retired.Physical, source.Parameters);
                        continue;
                    }
                    if (methods[index] is not JsonObject method
                        || (method["name"] as JsonValue)?.GetValue<string>() != source.Method.Name.OrDefault()
                        || (method[DeclarationIdentityBinding.Key] as JsonValue)?.GetValue<string>() != source.Method.DeclarationId.OrDefault())
                        throw Mismatch();
                    result.Add(method, source.Parameters);
                    index++;
                }
                foreach (var materialized in row.Materialized)
                    if (!row.Retired.ContainsKey(materialized.Method))
                    {
                        if (!ReferenceEquals(methods[index], materialized.Binding.Physical)) throw Mismatch();
                        index++;
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
