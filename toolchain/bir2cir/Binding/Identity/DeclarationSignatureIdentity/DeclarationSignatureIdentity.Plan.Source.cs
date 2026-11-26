using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class DeclarationSignatureIdentity
{
    // One allocation decision serves the runtime-only shadow and the actual
    // declaration graph. Its values are signature facts, never value operands.
    internal sealed class SignatureAllocationPlan
    {
        internal IReadOnlyDictionary<string, TypeNode[]> Parameters { get; }
        internal SignatureAllocationPlan(IReadOnlyDictionary<string, TypeNode[]> parameters)
            => Parameters = parameters.ToDictionary(pair => pair.Key,
                pair => pair.Value.Select(BirGraphValue.CopyType).ToArray(), StringComparer.Ordinal);
    }

    sealed record SignatureParameter(TypeNode Type, TypeNode[] Bounds, TypeNode Nominal);
    sealed record SignatureDeclaration(string Owner, string Name, string Identity, bool Generated,
        int Arity, TypeNode Return, SignatureParameter[] Parameters, string[] Overrides,
        IReadOnlyList<(string Id, TypeNode.Fqn Owner)> Constructions = null);

    internal static SignatureAllocationPlan Plan(IReadOnlyList<BirFileDeclaration> roots,
        ReferenceMetadataIndex refs = null)
    {
        var rows = new List<SignatureDeclaration>();
        void Collect(string owner, BirFunctionDeclaration[] methods, BirConstructor[] constructors,
            IBirTypeDeclaration[] types)
        {
            foreach (var method in methods)
                rows.Add(new(owner, method.ExplicitClrName.OrDefault() ?? method.Name.OrDefault(),
                    method.DeclarationId.OrDefault(), method.Generated.OrDefault(), method.TypeParams.OrEmpty().Length,
                    method.Ret.OrDefault(), ReadParameters(method.Params.OrEmpty()), method.SignatureOverrideDeclarations.OrEmpty(),
                    method.ConstructedOverrideOwners));
            foreach (var constructor in constructors)
                rows.Add(new(owner, ".ctor", constructor.DeclarationId.OrDefault(), false, 0, null,
                    ReadParameters(constructor.Params.OrEmpty()), Array.Empty<string>()));
            foreach (var type in types)
                Collect(type.Name.OrDefault(), type.Methods.OrEmpty(), type.Ctors.OrEmpty(), Array.Empty<IBirTypeDeclaration>());
        }
        foreach (var root in roots)
            Collect(root.FileClass.OrDefault(), root.Methods.OrEmpty(), Array.Empty<BirConstructor>(), root.Types.OrEmpty());
        return new(PlanCore(rows, SupertypeGraph.CollectSource(roots), refs));
    }

    static SignatureParameter[] ReadParameters(BirParameter[] parameters) => parameters.Select(parameter =>
        new SignatureParameter(parameter.Type.OrDefault(), parameter.GenericParameterSignatureBounds.OrEmpty(),
            parameter.NominalParameterSignatureIdentity.OrDefault())).ToArray();

    static IReadOnlyDictionary<string, TypeNode[]> PlanCore(IReadOnlyList<SignatureDeclaration> methods,
        IReadOnlyDictionary<string, SupertypeGraph.Def> definitions, ReferenceMetadataIndex refs)
    {
        var sourceMethods = methods.Where(item => item.Identity != null).GroupBy(item => item.Identity, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        bool Authored(SignatureDeclaration method) => method.Identity != null && !method.Generated;
        bool Eligible(SignatureDeclaration method) => Authored(method)
            || method.Identity?.EndsWith("|cold", StringComparison.Ordinal) == true
                && sourceMethods.TryGetValue(method.Identity[..^5], out var source) && Authored(source);
        var result = new Dictionary<string, TypeNode[]>(StringComparer.Ordinal);
        static string PhysicalParameters(TypeNode[] parameters) => string.Join(";",
            parameters.Select(DeclarationIdentityBinding.PhysicalTypeSignature));
        foreach (var group in methods.GroupBy(item => (item.Owner, item.Name,
                     Signature: DeclarationIdentityBinding.PhysicalSignature(item.Arity,
                         item.Parameters.Select(parameter => parameter.Type), item.Return))))
        {
            var candidates = group.ToArray();
            if (candidates.Length < 2 || candidates.Any(item => !Eligible(item))) continue;
            var projected = candidates.Select(item => (Method: item, Parameters: Project(item.Parameters))).ToArray();
            if (projected.Select(item => PhysicalParameters(item.Parameters)).Distinct(StringComparer.Ordinal).Count() != candidates.Length)
                projected = candidates.Select(item => (Method: item, Parameters: Project(item.Parameters, nominal: true))).ToArray();
            if (projected.Select(item => PhysicalParameters(item.Parameters)).Distinct(StringComparer.Ordinal).Count() != candidates.Length) continue;
            foreach (var (method, parameters) in projected) result.Add(method.Identity, parameters);
        }
        var resolved = new HashSet<string>(StringComparer.Ordinal);
        void ResolveOverride(string id)
        {
            if (!resolved.Add(id) || !sourceMethods.TryGetValue(id, out var method)) return;
            foreach (var target in method.Overrides.OrderBy(value => value, StringComparer.Ordinal))
            {
                if (target == null) continue;
                ResolveOverride(target);
                string selectedOwnerName;
                if (result.TryGetValue(target, out var signature)) selectedOwnerName = sourceMethods[target].Owner;
                else if (refs?.TryDeclarationSignatureSlot(target, out var referenced) == true && referenced.SignatureAllocated)
                {
                    signature = referenced.Parameters;
                    foreach (var parameter in signature)
                        for (var type = parameter; type is TypeNode.Mod modifier; type = modifier.Of)
                            if (modifier.M is TypeNode.Fqn marker)
                                ReferencedModifiers.Add((modifier.Req, marker.Name, marker.Args?.Length ?? 0));
                    selectedOwnerName = referenced.Owner;
                }
                else continue;
                var owners = SupertypeGraph.SelectedOverrideOwners(definitions[method.Owner], definitions, refs, method.Constructions,
                    target, selectedOwnerName, sourceMethods.ContainsKey(target) ? null : target);
                var closed = owners.Select(owner => signature.Select(type => SupertypeGraph.SubstOwnerTvs(type, owner.Args ?? [])).ToArray())
                    .DistinctBy(PhysicalParameters).ToArray();
                // If a single authored body covers distinct signatures, the MethodImpl bridges
                // below retain each one; neither construction supplies the shared body signature.
                if (closed.Length == 1) result[id] = closed[0];
                break;
            }
        }
        foreach (var id in sourceMethods.Keys) ResolveOverride(id);
        return result;
    }

    static TypeNode[] Project(SignatureParameter[] parameters, bool nominal = false) => parameters.Select(parameter => {
        var type = parameter.Type;
        foreach (var bound in parameter.Bounds.Where(bound => bound is not TypeNode.Fqn { Name: "object" or "System.Object" })
                     .OrderBy(bound => TypeJson.Write(bound).ToJsonString(), StringComparer.Ordinal))
            type = new TypeNode.Mod(false, bound, type);
        if (nominal && parameter.Nominal != null) type = new TypeNode.Mod(false, parameter.Nominal, type);
        return type;
    }).ToArray();
}
