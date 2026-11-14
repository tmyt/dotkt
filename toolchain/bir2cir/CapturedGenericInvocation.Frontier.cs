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
        internal JsonObject Dom;
        internal BirFunctionDeclaration Method;
        internal string Owner => Container.Name;
        internal MethodBinding(ContainerBinding container, int position, JsonObject method)
        {
            Container = container; Position = position; Dom = method;
            Name = Str(method["name"]); Static = Bool(method["static"]);
        }
    }

    internal sealed class ContainerBinding
    {
        internal readonly string Name, Kind;
        internal readonly int Position;
        internal readonly int MethodCount;
        internal JsonObject Dom;
        internal JsonArray DomMethods;
        internal BirFunctionDeclaration[] Methods;
        internal IBirTypeDeclaration Type;
        internal ContainerBinding(JsonObject node, int position, string name)
        {
            Dom = node; Position = position; Name = name; Kind = Str(node["kind"]);
            DomMethods = node["methods"] as JsonArray; MethodCount = DomMethods?.Count ?? 0;
        }
    }

    internal sealed class RootBinding
    {
        internal readonly ContainerBinding File;
        internal readonly ContainerBinding[] Types;
        internal JsonArray DomTypes;
        internal RootBinding(JsonObject root)
        {
            File = new(root, -1, Str(root["fileClass"]));
            DomTypes = root["types"] as JsonArray;
            Types = DomTypes?.Select((type, index) => new ContainerBinding(type.AsObject(), index, Str(type["name"]))).ToArray() ?? [];
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

    // Capture admission and IDs before metadata preparation consumes the raw
    // keys. The dictionary itself crosses the frontier; it is not rebuilt from
    // pending metadata, attributes, names or current declaration membership.
    internal static PendingDeclarations PrepareLocalDeclarations(IEnumerable<JsonNode> roots, bool flattenMissingOwners)
    {
        var pending = new PendingDeclarations(roots.OfType<JsonObject>().Select(root => new RootBinding(root)).ToArray(), flattenMissingOwners);
        foreach (var root in pending.Roots)
        {
            Capture(root.File);
            foreach (var type in root.Types) { pending.Types[type.Name] = type; Capture(type); }
        }
        return pending;

        void Capture(ContainerBinding owner)
        {
            if (owner.DomMethods == null) return;
            for (var index = 0; index < owner.DomMethods.Count; index++)
                if (owner.DomMethods[index] is JsonObject method && Str(method[DeclarationIdentityBinding.Key]) is string id)
                {
                    if (!pending.Declarations.TryGetValue(id, out var bucket)) pending.Declarations[id] = bucket = [];
                    bucket.Add(new MethodBinding(owner, index, method));
                }
        }
    }

    internal static PreparedInvocation BindPhysicalDeclarations(PendingDeclarations pending,
        IReadOnlyList<JsonNode> domRoots, IReadOnlyList<BirFileDeclaration> typedRoots)
    {
        if (pending.Roots == null || pending.Roots.Length != domRoots.Count || domRoots.Count != typedRoots.Count) throw FrontierMismatch();
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
