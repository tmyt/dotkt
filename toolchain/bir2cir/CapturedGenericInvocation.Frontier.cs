using System.Text.Json.Nodes;
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
        internal JsonObject Dom;
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
        internal readonly int MethodCount;
        internal BirGraphValue Source;
        internal OptionalField<BirFunctionDeclaration[]> SourceMethods;
        internal BirFunctionDeclaration[] SourceOccurrences;
        internal JsonObject Dom;
        internal JsonArray DomMethods;
        internal BirFunctionDeclaration[] Methods;
        internal IBirTypeDeclaration Type;
        internal ContainerBinding(BirGraphValue node, int position, string name)
        {
            Source = node; Position = position; Name = name;
            Kind = node is IBirTypeDeclaration type ? type.Kind.OrDefault() : null;
            SourceMethods = CurrentMethods(node);
            SourceOccurrences = SourceMethods.OrEmpty().ToArray(); MethodCount = SourceOccurrences.Length;
        }
    }

    internal sealed class RootBinding
    {
        internal readonly ContainerBinding File;
        internal readonly ContainerBinding[] Types;
        internal BirFileDeclaration Source;
        internal OptionalField<IBirTypeDeclaration[]> SourceTypes;
        internal IBirTypeDeclaration[] SourceOccurrences;
        internal JsonArray DomTypes;
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
        internal bool Projected;
        internal PendingDeclarations(RootBinding[] roots, bool flatten) { Roots = roots; FlattenMissingOwners = flatten; }
    }

    // Capture admission and IDs before metadata preparation consumes the raw
    // keys. The dictionary itself crosses the frontier; it is not rebuilt from
    // pending metadata, attributes, names or current declaration membership.
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

    // This is an address transition of the same captured rows, not another ID,
    // signature or metadata admission pass. Validate every original vector,
    // including ID-less methods, before replacing any source anchor with DOM.
    internal static void BindProjectedDeclarations(PendingDeclarations pending, SourceDeclarationProjection projection)
    {
        if (pending.Roots == null || pending.Projected) throw FrontierMismatch();
        foreach (var root in pending.Roots)
        {
            if (root.Source == null || !ReferenceEquals(root.File.Source, root.Source)
                || root.Source.FileClass.OrDefault() != root.File.Name
                || !SameVector(root.Source.Types, root.SourceTypes, root.SourceOccurrences)) throw FrontierMismatch();
            var dom = projection.Get(root.Source);
            var types = dom["types"] as JsonArray;
            if (Str(dom["fileClass"]) != root.File.Name || (types?.Count ?? 0) != root.Types.Length
                || (types != null) != root.SourceTypes.HasValue) throw FrontierMismatch();
            Validate(root.File, dom);
            foreach (var type in root.Types)
            {
                var source = root.SourceOccurrences[type.Position];
                var projected = projection.Get((BirGraphValue)source);
                if (!ReferenceEquals(type.Source, source) || source.Name.OrDefault() != type.Name || source.Kind.OrDefault() != type.Kind
                    || !ReferenceEquals(types[type.Position], projected)
                    || Str(projected["name"]) != type.Name || Str(projected["kind"]) != type.Kind) throw FrontierMismatch();
                Validate(type, projected);
            }
        }
        foreach (var bucket in pending.Declarations.Values)
            foreach (var binding in bucket)
            {
                var method = binding.Source;
                var dom = projection.Get(method);
                if (!ReferenceEquals(binding.Container.SourceOccurrences[binding.Position], method)
                    || method.Name.OrDefault() != binding.Name || method.Static.OrDefault() != binding.Static
                    || Str(dom["name"]) != binding.Name || Bool(dom["static"]) != binding.Static) throw FrontierMismatch();
            }

        // All rows passed. Fill only observed addresses and release the typed
        // staging forest; the original dictionaries and bucket rows stay live.
        foreach (var bucket in pending.Declarations.Values)
            foreach (var binding in bucket) { binding.Dom = projection.Get(binding.Source); binding.Source = null; }
        foreach (var root in pending.Roots)
        {
            Bind(root.File);
            foreach (var type in root.Types) Bind(type);
            root.DomTypes = root.File.Dom["types"] as JsonArray;
            root.Source = null; root.SourceTypes = default; root.SourceOccurrences = null;
        }
        pending.Projected = true;

        void Validate(ContainerBinding owner, JsonObject dom)
        {
            var methods = dom["methods"] as JsonArray;
            if (!SameVector(CurrentMethods(owner.Source), owner.SourceMethods, owner.SourceOccurrences)
                || (methods?.Count ?? 0) != owner.MethodCount || (methods != null) != owner.SourceMethods.HasValue)
                throw FrontierMismatch();
            for (var index = 0; index < owner.SourceOccurrences.Length; index++)
                if (!ReferenceEquals(methods[index], projection.Get(owner.SourceOccurrences[index]))) throw FrontierMismatch();
        }
        void Bind(ContainerBinding owner)
        {
            owner.Dom = projection.Get(owner.Source); owner.DomMethods = owner.Dom["methods"] as JsonArray;
            owner.Source = null; owner.SourceMethods = default; owner.SourceOccurrences = null;
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

    internal static PreparedInvocation BindPhysicalDeclarations(PendingDeclarations pending,
        IReadOnlyList<JsonNode> domRoots, IReadOnlyList<BirFileDeclaration> typedRoots)
    {
        if (pending.Roots == null || !pending.Projected || pending.Roots.Length != domRoots.Count || domRoots.Count != typedRoots.Count) throw FrontierMismatch();
        // Validate every original owning vector before transitioning any anchor.
        for (var index = 0; index < pending.Roots.Length; index++)
        {
            var original = pending.Roots[index]; var root = typedRoots[index];
            if (!ReferenceEquals(domRoots[index], original.File.Dom)
                || !ReferenceEquals(original.File.Dom["types"], original.DomTypes)
                || (original.DomTypes?.Count ?? 0) != original.Types.Length
                || root.Types.OrEmpty().Length != original.Types.Length
                || Str(original.File.Dom["fileClass"]) != original.File.Name || root.FileClass.OrDefault() != original.File.Name)
                throw FrontierMismatch();
            Validate(original.File, root.Methods.OrEmpty());
            foreach (var type in original.Types)
            {
                var actual = root.Types.Value[type.Position];
                if (!ReferenceEquals(original.DomTypes[type.Position], type.Dom)
                    || Str(type.Dom["name"]) != type.Name || Str(type.Dom["kind"]) != type.Kind
                    || actual.Name.OrDefault() != type.Name || actual.Kind.OrDefault() != type.Kind) throw FrontierMismatch();
                Validate(type, actual.Methods.OrEmpty());
            }
        }
        foreach (var bucket in pending.Declarations.Values)
            foreach (var binding in bucket)
                if (!ReferenceEquals(binding.Container.DomMethods[binding.Position], binding.Dom)
                    || Str(binding.Dom["name"]) != binding.Name || Bool(binding.Dom["static"]) != binding.Static
                    || binding.Container.Methods[binding.Position].Name.OrDefault() != binding.Name
                    || binding.Container.Methods[binding.Position].Static.OrDefault() != binding.Static) throw FrontierMismatch();

        foreach (var bucket in pending.Declarations.Values)
            foreach (var binding in bucket) { binding.Method = binding.Container.Methods[binding.Position]; binding.Dom = null; }
        for (var index = 0; index < pending.Roots.Length; index++)
        {
            var original = pending.Roots[index];
            foreach (var type in original.Types) type.Type = typedRoots[index].Types.Value[type.Position];
            Release(original.File);
            foreach (var type in original.Types) Release(type);
            original.DomTypes = null;
        }
        pending.Roots = null;
        return new PreparedInvocation(pending.Declarations, pending.Types, pending.FlattenMissingOwners,
            TypedPhysicalValueCoercion.ResultTypeOracle(typedRoots));

        static void Validate(ContainerBinding original, BirFunctionDeclaration[] methods)
        {
            if (!ReferenceEquals(original.Dom["methods"], original.DomMethods)
                || (original.DomMethods?.Count ?? 0) != original.MethodCount || methods.Length != original.MethodCount)
                throw FrontierMismatch();
            original.Methods = methods;
        }
        static void Release(ContainerBinding binding) { binding.Dom = null; binding.DomMethods = null; binding.Methods = null; }
    }

    static InvalidOperationException FrontierMismatch() => new("Captured declaration membership changed across its owning frontier");
    static bool Bool(JsonNode value) => value is JsonValue scalar && scalar.TryGetValue<bool>(out var result) && result;
    static string Str(JsonNode node) => node is JsonValue value && value.TryGetValue<string>(out var result) ? result : null;
}
