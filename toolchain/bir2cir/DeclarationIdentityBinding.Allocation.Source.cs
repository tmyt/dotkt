using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class DeclarationIdentityBinding
{
    // A scalar allocation row, not a second declaration identity index. Current
    // declarations feed the existing collision/name/semantic-carrier authority.
    sealed record NameAllocationRow(string Owner, string Package, string Id, string Name,
        string SourceName, string Sig, string ExplicitName, bool Generated, bool IndependentName,
        int MethodArity, int ParamCount, bool IsStatic);

    internal static IReadOnlyDictionary<string, string> AllocatePhysicalNames(
        IReadOnlyList<BirFileDeclaration> physicalProjectionRoots,
        out IReadOnlySet<string> semanticCarrierIds)
    {
        var rows = new List<NameAllocationRow>();
        void Methods(IEnumerable<BirFunctionDeclaration> methods, string owner, string package,
            bool generatedOwner, string ownerKind)
        {
            foreach (var method in methods)
                if (method.Name.OrDefault() is string name)
                    rows.Add(new(owner, package, method.DeclarationId.OrDefault(), name,
                        method.DeclarationSourceName.OrDefault() ?? name,
                        PhysicalSignature(method.TypeParams.OrEmpty().Length,
                            method.Params.OrEmpty().Select(parameter => parameter.Type.OrDefault()), method.Ret.OrDefault()),
                        method.ExplicitClrName.OrDefault(), generatedOwner || method.Generated.OrDefault(),
                        !method.Abstract.OrDefault() && (method.IndependentAliasSlot.OrDefault() || ownerKind == "interface"
                            || !method.Virtual.OrDefault() && !method.Override.OrDefault()
                                && method.Overrides.OrEmpty().Length == 0),
                        method.TypeParams.OrEmpty().Length, method.Params.OrEmpty().Length, method.Static.OrDefault()));
        }
        foreach (var root in physicalProjectionRoots)
        {
            var facade = root.FileClass.OrDefault() ?? "<file>";
            var separator = facade.LastIndexOf('.');
            Methods(root.Methods.OrEmpty(), facade, separator < 0 ? "" : facade[..separator], false, null);
            foreach (var owner in root.Types.OrEmpty())
                Methods(owner.Methods.OrEmpty(), owner.Name.OrDefault() ?? facade + "/<anonymous>", null,
                    owner.Generated.OrDefault(), owner.Kind.OrDefault());
        }
        return AllocatePhysicalNamesCore(rows, out semanticCarrierIds);
    }

    static IReadOnlyDictionary<string, string> AllocatePhysicalNamesCore(
        IReadOnlyList<NameAllocationRow> allDeclarations, out IReadOnlySet<string> semanticCarrierIds)
    {
        var declarations = allDeclarations.Where(declaration => declaration.Id != null).ToArray();
        var carrierIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in declarations.GroupBy(d => (d.Owner, d.Name, d.Sig)))
        {
            var ids = group.Select(d => d.Id).Distinct(StringComparer.Ordinal).ToArray();
            if (ids.Length > 1)
                foreach (var id in ids) carrierIds.Add(id);
        }
        // An identity-selected call is validated against the selected declaration, never structurally rebound. Keep
        // the Kotlin signature for every same-source-name family whose members have the same callable scalar shape,
        // even when their final CLR parameter types remain distinct. The reflected spelling alone cannot validate
        // Kotlin/CLR projections such as List<T> -> IReadOnlyList<T>, and this is precisely the family in which a
        // shape-only fallback could otherwise select a sibling overload.
        foreach (var group in declarations.GroupBy(declaration => (
                     declaration.Owner,
                     declaration.SourceName,
                     declaration.MethodArity,
                     declaration.ParamCount,
                     declaration.IsStatic)))
        {
            var ids = group.Select(declaration => declaration.Id).Distinct(StringComparer.Ordinal).ToArray();
            if (ids.Length > 1)
                foreach (var id in ids) carrierIds.Add(id);
        }

        foreach (var declaration in allDeclarations.Where(declaration => declaration.ExplicitName != null))
        {
            ValidateExplicitName(declaration.ExplicitName, declaration.Owner, declaration.SourceName);
            if (declaration.Id == null || !declaration.IndependentName)
                throw new InvalidOperationException(
                    $"bir2cir: @ClrName on '{declaration.Owner}.{declaration.SourceName}' cannot be applied " +
                    "independently because the declaration has no independent physical-name allocation (for example, " +
                    "an open/override family or a local implementation artifact); complete slot-wide explicit-name " +
                    "propagation is not supported");
        }

        var explicitByAllocationId = declarations
            .Where(declaration => declaration.ExplicitName != null)
            .GroupBy(declaration => AllocationIdentity(declaration.Id), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group =>
            {
                var names = group.Select(declaration => declaration.ExplicitName)
                    .Distinct(StringComparer.Ordinal).ToArray();
                if (names.Length != 1)
                    throw new InvalidOperationException(
                        $"bir2cir: declaration identity '{group.Key}' carries conflicting explicit CLR names: " +
                        string.Join(", ", names.Select(name => $"'{name}'")));
                return names[0];
            }, StringComparer.Ordinal);

        string AllocatedName(NameAllocationRow declaration)
        {
            if (declaration.ExplicitName != null) return declaration.ExplicitName;
            var allocationId = AllocationIdentity(declaration.Id);
            // A physical-only signature/existential copy of the source declaration shares an explicit source name
            // only when it retained the source spelling. Compiler-owned bridges/wrappers with their own structured
            // name remain in that separate generated naming domain.
            if (explicitByAllocationId.TryGetValue(allocationId, out var explicitName)
                && (declaration.Id == allocationId || declaration.Name == declaration.SourceName))
                return explicitName;
            return declaration.Name;
        }

        var candidatesById = declarations
            .Select(declaration => (declaration.Id, Name: AllocatedName(declaration)))
            .GroupBy(candidate => candidate.Id, StringComparer.Ordinal);
        var physicalById = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var candidates in candidatesById)
        {
            var names = candidates.Select(x => x.Name).Distinct(StringComparer.Ordinal).ToArray();
            if (names.Length != 1)
                throw new InvalidOperationException(
                    $"bir2cir: declaration identity '{candidates.Key}' maps to multiple physical names: "
                    + string.Join(", ", names.OrderBy(x => x, StringComparer.Ordinal)));
            physicalById.Add(candidates.Key, names[0]);
        }
        // A non-owner-dependent existential slot is the same CLR contract as its source MethodDef, not a second
        // independently named implementation. Propagate an explicit source name to that slot; an unresolved collision
        // is rejected below instead of receiving an automatic declaration-id hash.
        foreach (var slot in declarations.Where(declaration =>
                     declaration.Id.Contains(ExistentialSlotMarker, StringComparison.Ordinal)
                     && declaration.Name == declaration.SourceName))
        {
            var sourceId = AllocationIdentity(slot.Id);
            if (!physicalById.TryGetValue(sourceId, out var sourcePhysical)
                || !physicalById.TryGetValue(slot.Id, out var slotPhysical))
                continue;
            if (sourcePhysical != slot.SourceName && slotPhysical == slot.SourceName)
                physicalById[slot.Id] = sourcePhysical;
        }
        // CLR MethodDef collisions are owner-local, but dll2klib merges file facades back into one Kotlin package.
        // Preserve semantic signatures for a same-package/source-name pair that shares a physical signature across
        // different facades as well; otherwise the reverse projection can collapse Map and MutableMap to one type
        // before FIR has a chance to select either declaration.
        foreach (var group in declarations
            .Where(declaration => declaration.Package != null)
            .GroupBy(declaration => (declaration.Package, declaration.SourceName, declaration.Sig)))
        {
            var owners = group.Select(declaration => declaration.Owner).Distinct(StringComparer.Ordinal).ToArray();
            var ids = group.Select(declaration => declaration.Id).Distinct(StringComparer.Ordinal).ToArray();
            if (owners.Length < 2 || ids.Length < 2) continue;
            foreach (var id in ids) carrierIds.Add(id);
        }

        var finalDeclarations = allDeclarations.Select(declaration =>
        {
            var finalName = declaration.Id != null
                ? physicalById.TryGetValue(declaration.Id, out var allocated)
                    ? allocated
                    : throw new InvalidOperationException(
                        $"bir2cir: declaration identity '{declaration.Id}' has no physical allocation")
                : declaration.Name;
            return (Declaration: declaration, FinalName: finalName);
        });
        var collisionDiagnostics = new List<string>();
        foreach (var collision in finalDeclarations
            .GroupBy(candidate => (candidate.Declaration.Owner, candidate.FinalName, candidate.Declaration.Sig)))
        {
            // Shared synthesized types are projected into each per-file collision root and hoisted/deduplicated later.
            // Count that repeated generated declaration once, while retaining every independently authored method.
            var collisionCandidates = collision.Where(candidate => !candidate.Declaration.Generated)
                .Concat(collision.Where(candidate => candidate.Declaration.Generated).Take(1))
                .ToArray();
            if (collisionCandidates.Length < 2) continue;
            var parts = collision.Key.Sig.Split('|', 2);
            var arity = parts[0];
            var parameters = parts.Length == 2 ? parts[1] : "";
            var declarationsText = string.Join(", ", collisionCandidates.Select(candidate =>
                candidate.Declaration.Generated
                    ? $"compiler-generated '{candidate.Declaration.Name}'"
                    : $"Kotlin declaration '{candidate.Declaration.SourceName}'" +
                      (candidate.Declaration.Id == null ? " (open/override family)" : "")));
            collisionDiagnostics.Add(
                $"CLR physical signature collision at " +
                $"'{collision.Key.Owner}.{collision.Key.FinalName}' (generic arity {arity}; parameters [{parameters}]): " +
                $"{declarationsText}. Assign a distinct @ClrName to an independently allocatable user declaration; " +
                "declaration order and automatic hash suffixes are not used");
        }
        if (collisionDiagnostics.Count != 0)
            throw new InvalidOperationException(
                "bir2cir: unresolved CLR physical signature collisions:" + Environment.NewLine +
                string.Join(Environment.NewLine, collisionDiagnostics));
        semanticCarrierIds = carrierIds;
        return physicalById;
    }
}
