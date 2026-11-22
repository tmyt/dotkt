using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class CapturedGenericInvocation
{
    internal sealed class MethodBinding
    {
        internal readonly ContainerBinding Container;
        internal readonly int Position;
        internal readonly string Name;
        internal readonly bool Static;
        internal BirFunctionDeclaration Source;
        internal BirFunctionDeclaration Method;
        internal string Owner => Container.Name;
        internal MethodBinding(ContainerBinding container, int position, BirFunctionDeclaration method)
        {
            Container = container; Position = position; Source = method;
            Name = method.Name.OrDefault(); Static = method.Static.OrDefault();
        }
    }

    internal sealed class ContainerBinding
    {
        internal readonly string Name, Kind;
        internal readonly int Position;
        internal BirGraphValue Source;
        internal OptionalField<BirFunctionDeclaration[]> SourceMethods;
        internal BirFunctionDeclaration[] SourceOccurrences;
        internal (string Name, bool Static)[] SourceHeaders;
        internal IBirTypeDeclaration Type;
        internal ContainerBinding(BirGraphValue node, int position, string name)
        {
            Source = node; Position = position; Name = name;
            Kind = node is IBirTypeDeclaration type ? type.Kind.OrDefault() : null;
            SourceMethods = CurrentMethods(node);
            SourceOccurrences = SourceMethods.OrEmpty().ToArray();
            SourceHeaders = SourceOccurrences.Select(method => (method.Name.OrDefault(), method.Static.OrDefault())).ToArray();
        }
    }

    internal sealed class RootBinding
    {
        internal readonly ContainerBinding File;
        internal readonly ContainerBinding[] Types;
        internal BirFileDeclaration Source;
        internal OptionalField<IBirTypeDeclaration[]> SourceTypes;
        internal IBirTypeDeclaration[] SourceOccurrences;
        internal RootBinding(BirFileDeclaration root)
        {
            Source = root; File = new(root, -1, root.FileClass.OrDefault());
            SourceTypes = root.Types; SourceOccurrences = root.Types.OrEmpty().ToArray();
            Types = SourceOccurrences.Select((type, index) => new ContainerBinding((BirGraphValue)type, index, type.Name.OrDefault())).ToArray();
        }
    }

    internal sealed class PendingDeclarations
    {
        internal RootBinding[] Roots;
        internal readonly Dictionary<string, List<MethodBinding>> Declarations = new(StringComparer.Ordinal);
        internal readonly Dictionary<string, ContainerBinding> Types = new(StringComparer.Ordinal);
        internal readonly bool FlattenMissingOwners;
        internal PendingDeclarations(RootBinding[] roots, bool flatten) { Roots = roots; FlattenMissingOwners = flatten; }
    }

    // Admission and IDs belong to this pre-metadata clock. Only these exact
    // rows continue; native resolution and pending metadata never grant another.
    internal static PendingDeclarations PrepareLocalDeclarations(IReadOnlyList<BirFileDeclaration> roots, bool flattenMissingOwners)
    {
        var pending = new PendingDeclarations(roots.Select(root => new RootBinding(root)).ToArray(), flattenMissingOwners);
        foreach (var root in pending.Roots)
        {
            Capture(root.File);
            foreach (var type in root.Types) { pending.Types[type.Name] = type; Capture(type); }
        }
        return pending;

        void Capture(ContainerBinding owner)
        {
            for (var index = 0; index < owner.SourceOccurrences.Length; index++)
                if (owner.SourceOccurrences[index] is { DeclarationId.HasValue: true } method)
                {
                    var id = method.DeclarationId.Value;
                    if (!pending.Declarations.TryGetValue(id, out var bucket)) pending.Declarations[id] = bucket = [];
                    bucket.Add(new MethodBinding(owner, index, method));
                }
        }
    }

    static OptionalField<BirFunctionDeclaration[]> CurrentMethods(BirGraphValue owner) => owner switch {
        BirFileDeclaration file => file.Methods,
        IBirTypeDeclaration type => type.Methods,
        _ => throw FrontierMismatch(),
    };

    static bool SameVector<T>(OptionalField<T[]> current, OptionalField<T[]> captured, T[] occurrences) where T : class
        => current.IsPresent == captured.IsPresent && current.IsNull == captured.IsNull
            && (!current.HasValue || ReferenceEquals(current.Value, captured.Value)
                && current.Value.SequenceEqual(occurrences, ReferenceEqualityComparer.Instance));

    // Native operations and declaration/base slots are now bound on these SAME
    // owning records. Validate the original complete vectors before releasing
    // any staged anchor; a metadata ID is not permission to replace a record.
    internal static PreparedInvocation BindPhysicalDeclarations(PendingDeclarations pending,
        IReadOnlyList<BirFileDeclaration> typedRoots, Func<TypeNode, TypeNode.Fqn> captureConstruction = null)
    {
        if (pending.Roots == null || pending.Roots.Length != typedRoots.Count) throw FrontierMismatch();
        var occurrences = new HashSet<BirGraphValue>(ReferenceEqualityComparer.Instance);
        for (var index = 0; index < pending.Roots.Length; index++)
        {
            var original = pending.Roots[index]; var root = typedRoots[index];
            if (!ReferenceEquals(original.Source, root) || !ReferenceEquals(original.File.Source, root)
                || root.FileClass.OrDefault() != original.File.Name
                || !SameVector(root.Types, original.SourceTypes, original.SourceOccurrences)) throw FrontierMismatch();
            Validate(original.File);
            foreach (var type in original.Types)
            {
                var actual = root.Types.Value[type.Position];
                if (!ReferenceEquals(type.Source, actual) || actual.Name.OrDefault() != type.Name
                    || actual.Kind.OrDefault() != type.Kind) throw FrontierMismatch();
                Validate(type);
            }
        }
        foreach (var bucket in pending.Declarations.Values)
            foreach (var binding in bucket)
                if (!ReferenceEquals(binding.Container.SourceOccurrences[binding.Position], binding.Source)
                    || binding.Source.Name.OrDefault() != binding.Name || binding.Source.Static.OrDefault() != binding.Static)
                    throw FrontierMismatch();

        // No mutation above: a refused completion leaves all original staging
        // intact. Keep the same dictionaries/bucket order and live declarations.
        foreach (var bucket in pending.Declarations.Values)
            foreach (var binding in bucket) { binding.Method = binding.Source; binding.Source = null; }
        foreach (var original in pending.Roots)
        {
            foreach (var type in original.Types) type.Type = (IBirTypeDeclaration)type.Source;
            Release(original.File);
            foreach (var type in original.Types) Release(type);
            original.Source = null; original.SourceTypes = default; original.SourceOccurrences = null;
        }
        pending.Roots = null;
        return new PreparedInvocation(pending.Declarations, pending.Types, pending.FlattenMissingOwners,
            TypedPhysicalValueCoercion.ResultTypeOracle(typedRoots), captureConstruction);

        void Validate(ContainerBinding original)
        {
            if (!occurrences.Add(original.Source)
                || !SameVector(CurrentMethods(original.Source), original.SourceMethods, original.SourceOccurrences)) throw FrontierMismatch();
            for (var index = 0; index < original.SourceOccurrences.Length; index++)
            {
                var actual = original.SourceOccurrences[index];
                if (!occurrences.Add(actual) || actual.Name.OrDefault() != original.SourceHeaders[index].Name
                    || actual.Static.OrDefault() != original.SourceHeaders[index].Static) throw FrontierMismatch();
            }
        }
        static void Release(ContainerBinding binding)
        {
            binding.Source = null; binding.SourceMethods = default;
            binding.SourceOccurrences = null; binding.SourceHeaders = null;
        }
    }

    static InvalidOperationException FrontierMismatch() => new("Captured declaration membership changed across its owning frontier");
}
