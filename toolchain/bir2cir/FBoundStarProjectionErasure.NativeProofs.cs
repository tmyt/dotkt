using System.Text.Json.Nodes;
using DotKt.Bir;

static partial class FBoundStarProjectionErasure
{
    // Initial demand belongs to the owning source graph. These remaining
    // queries consume the same bound plan while real late DOM owners mutate it.
    internal sealed partial class NativeProofPlan
    {
        // A physical slot/forwarder is authored from this exact method. A
        // separate private storage body must retain its independent proof rows.
        public void TransferIndependentMethodProofs(JsonObject source, JsonObject projection)
            => TransferIndependentMethodProofRows(Rows(source), bindings.RegisterPhysical(projection),
                Str(projection[DeclarationIdentityBinding.Key]));

        // Clone resolution here is the existing late declaration policy, not
        // permission to repair a missing captured frontier occurrence.
        public bool IsPhysicalConstraint(JsonArray constraints, TypeNode bound)
        {
            if (constraints.Parent is not JsonObject parameter || !ReferenceEquals(parameter["constraints"], constraints)
                || parameter.Parent is not JsonArray parameters || parameters.Parent is not JsonObject declaration
                || !ReferenceEquals(declaration["typeParams"], parameters)) return false;
            var index = parameters.IndexOf(parameter);
            IReadOnlyDictionary<int, List<TypeNode>> required;
            if (bindings.TryGetPhysical(declaration, out var binding) && rows.TryGetValue(binding, out var current))
                required = current;
            else if (IsTypeDefinition(declaration) && Str(declaration["name"]) is string owner)
                required = lateLocalTypes.TryGetValue(owner, out var physical) ? physical
                    : definitions.TryGetValue(owner, out var original) ? Rows(original)
                    : new Dictionary<int, List<TypeNode>>();
            else required = methods.TryGetValue(Str(declaration[DeclarationIdentityBinding.Key]) ?? "", out var method)
                ? Rows(method) : new Dictionary<int, List<TypeNode>>();
            return IsRequiredPhysicalConstraint(required, index, bound);
        }

        public void RegisterPhysicalLocalDefinitions(IReadOnlyDictionary<string, JsonObject> physicalDefinitions)
        {
            foreach (var (name, declaration) in physicalDefinitions)
                if (!definitions.ContainsKey(name))
                {
                    // Already-physical late declarations do not rerun source
                    // demand, projection or source carrier eligibility.
                    var binding = bindings.RegisterPhysical(declaration);
                    lateLocalTypes[name] = ReadRows(Constraints(binding), false,
                        Frame(binding), physicalDeclaration: true);
                }
        }

        public IEnumerable<TypeNode> Bounds(JsonObject declaration, int index) =>
            bindings.TryGetPhysical(declaration, out var binding) ? Bounds(binding, index) : Enumerable.Empty<TypeNode>();

        public bool ExactTypeArgument(string owner, int index) => TypeRows(owner).ContainsKey(index);

        public JsonObject LocalMethod(string identity) => identity != null && methods.TryGetValue(identity, out var binding)
            ? binding.Physical : null;

        public bool ExactMethodArgument(JsonObject declaration, string identity, int index) => declaration != null
            ? Rows(declaration).ContainsKey(index) : MethodRows(identity).ContainsKey(index);
    }

    static NativeProofPlan _nativeProofs;

}
