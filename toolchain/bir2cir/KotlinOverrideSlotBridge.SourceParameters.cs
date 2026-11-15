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
        sealed record OwnerRow(int File, int Position, IBirTypeDeclaration Owner, MethodRow[] Methods)
        {
            internal readonly List<BirFunctionDeclaration> Materialized = new();
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
            foreach (var method in appended) bindings.GetSource(method);
            row.Materialized.AddRange(appended);
            return true;
        }

        static IEnumerable<BirFunctionDeclaration> CurrentMethods(OwnerRow row)
            => row.Methods.Select(method => method.Method).Concat(row.Materialized);

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

        // The final typed slot consumer has used the snapshot. Release it at
        // that clock; no later physical pass consumes source parameter rows.
        internal void Complete()
        {
            ValidateSourceRows();
            consumed = true;
            files = null;
            owners = null;
        }

        static InvalidOperationException Mismatch()
            => new("Source override parameter snapshot no longer owns its exact declarations");
    }
}
