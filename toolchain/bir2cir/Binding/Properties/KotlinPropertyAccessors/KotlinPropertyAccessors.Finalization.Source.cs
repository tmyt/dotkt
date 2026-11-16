using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class KotlinPropertyAccessors
{
    // A PropertyDef has a metadata identity independent of its MethodDefs.
    // Allocate it only after exact method naming, keeping source association
    // in the metadata carrier rather than parsing an allocated physical name.
    // Pass-local scalar rows over exact property/accessor occurrences. They do
    // not select methods or admit a second declaration catalog.
    sealed record FinalAccessor(string Name, string Role, string Identity, string ExplicitName,
        Action<string, string, string> StampCarrier);
    sealed record FinalProperty(Func<string> Name, Action<string> Rename, string Association, string Signature,
        FinalAccessor[] Accessors);

    internal static void FinalizePhysicalProperties(IReadOnlyList<BirFileDeclaration> roots)
    {
        // Resolve associations in each actual file before combining file facades.
        foreach (var owners in roots.GroupBy(root => root.FileClass.OrDefault() ?? "<file>"))
            FinalizeOwners(owners.Cast<BirGraphValue>().ToArray());
        foreach (var root in roots) Walk(root, allocateOwner: false);

        void FinalizeOwners(IReadOnlyList<BirGraphValue> owners)
        {
            var rows = new List<FinalProperty>();
            var properties = new List<BirPropertyDeclaration>();
            foreach (var owner in owners)
            {
                var (methods, owned) = owner switch {
                    BirFileDeclaration file => (file.Methods.OrEmpty(), file.Properties.OrEmpty()),
                    IBirTypeDeclaration type => (type.Methods.OrEmpty(), type.Properties.OrEmpty()),
                    _ => (Array.Empty<BirFunctionDeclaration>(), Array.Empty<BirPropertyDeclaration>()),
                };
                foreach (var property in owned)
                {
                    // Earlier allocation retires actual unsupported source rows.
                    // As in the old finalizer, this refresh never detaches a row
                    // even if an accessor is method-generic.
                    if (property.KotlinAccessors.HasValue) AllocateProperty(property, methods, stripIdentity: false);
                    properties.Add(property);
                    if (property.PropertyAssociation.OrDefault() is not string association) continue;
                    rows.Add(new(() => property.Name.OrDefault(), name => property.Name = name,
                        association, FinalPropertySignature(property), methods
                            .Where(method => method.PropertyAssociation.OrDefault() == association)
                            .Select(method => new FinalAccessor(method.Name.OrDefault(), method.PropertyAccessor.OrDefault(),
                                method.DeclarationId.OrDefault(), method.ExplicitClrName.OrDefault(),
                                (name, role, key) => method.KotlinPropertyAccessorCarrier = new EventPropertySourceFacts {
                                    Name = name, Kind = role, Association = key,
                                })).ToArray()));
                }
            }
            AllocateFinalPropertyNames(rows);
            foreach (var property in properties) { property.KotlinAccessors = default; property.PropertyAssociation = default; }
        }

        void Walk(BirGraphValue node, bool allocateOwner = true)
        {
            if (node == null || node is IBirExecutablePayload) return;
            if (allocateOwner && node is BirFileDeclaration or IBirTypeDeclaration) FinalizeOwners(new[] { node });
            node.VisitOwnChildSlots((_, role, slot) => {
                if (role != BirGenericFrameRole.FrozenSourceFact) Walk(slot.Value);
            });
            node.ConsumeFinalPropertyFacts(HasIdentity);
        }
    }

    static string FinalPropertySignature(BirPropertyDeclaration property)
    {
        var type = property.Type.OrDefault() ?? throw new InvalidOperationException("Kotlin property record has no physical type");
        var parameters = property.GetSig.OrDefault() ?? (property.SetSig.HasValue
            ? property.SetSig.Value.Take(Math.Max(0, property.SetSig.Value.Length - 1)).ToArray()
            : throw new InvalidOperationException("Kotlin property record has no physical accessor signature"));
        return FinalPropertySignature(type, parameters);
    }

    static string FinalPropertySignature(TypeNode type, IEnumerable<TypeNode> parameters) =>
        DeclarationIdentityBinding.PhysicalPropertyTypeSignature(type) + "|" + string.Join(";", parameters.Select(parameter =>
            DeclarationIdentityBinding.PhysicalPropertyTypeSignature(parameter
                ?? throw new InvalidOperationException("Kotlin property record has an untyped physical index parameter"))));

    static bool HasPropertyDeclarationIdentity(FinalProperty property)
    {
        var ids = property.Accessors.Select(method => method.Identity).Where(id => id != null).Distinct(StringComparer.Ordinal).ToArray();
        if (ids.Length == 0) return false;
        // Preserve the exact getter/sole-setter representative rule (including
        // its uniqueness requirement); identities authorize, never name-mangle.
        var getter = property.Accessors.SingleOrDefault(method => method.Role == "get");
        _ = getter?.Identity ?? ids[0];
        return true;
    }

    static string ExplicitPropertyName(FinalProperty property)
    {
        // A getter controls the Property name even when the explicitly named
        // setter appears first. Setter-only projected shapes use their setter.
        var getter = property.Accessors.SingleOrDefault(method => method.Role == "get");
        if (getter != null) return getter.ExplicitName != null ? getter.Name : null;
        var setter = property.Accessors.SingleOrDefault(method => method.Role == "set");
        return setter?.ExplicitName != null ? setter.Name : null;
    }

    static void AllocateFinalPropertyNames(IReadOnlyList<FinalProperty> properties)
    {
        foreach (var group in properties.GroupBy(property =>
            (Name: property.Name() ?? throw new InvalidOperationException("property has no name"), property.Signature)))
        {
            var colliding = group.ToArray();
            if (colliding.Length < 2) continue;
            var allocation = colliding.Select(property => (Property: property,
                HasDeclarationId: HasPropertyDeclarationIdentity(property), ExplicitName: ExplicitPropertyName(property),
                IsBridge: property.Association.StartsWith("dotkt$bridge$property$", StringComparison.Ordinal))).ToArray();
            var unidentified = allocation.Where(candidate => !candidate.HasDeclarationId && !candidate.IsBridge).ToArray();
            if (unidentified.Length > 1)
                throw new InvalidOperationException($"Kotlin properties named '{group.Key.Name}' with physical signature '{group.Key.Signature}' "
                    + "collide without frontend declaration identity: " + string.Join(", ", unidentified.Select(candidate => candidate.Property.Association)));
            foreach (var candidate in allocation)
            {
                var property = candidate.Property;
                if (candidate.HasDeclarationId)
                    foreach (var method in property.Accessors)
                    {
                        var role = method.Role ?? throw new InvalidOperationException($"Kotlin property association '{property.Association}' has an accessor without a role");
                        // Match existing collision metadata replacement. The
                        // earlier shared AllocateProperty owns carrier merging.
                        method.StampCarrier(group.Key.Name, role, property.Association);
                    }
                if (candidate.ExplicitName != null) property.Rename(candidate.ExplicitName);
                else if (candidate.IsBridge) property.Rename(group.Key.Name + "$bridge$" + DeclarationIdentityBinding.StableSuffix(property.Association));
            }
            var remaining = allocation.GroupBy(candidate => candidate.Property.Name()
                    ?? throw new InvalidOperationException("property has no physical name")).Where(names => names.Count() > 1).ToArray();
            if (remaining.Length != 0)
                throw new InvalidOperationException($"bir2cir: CLR physical property signature collision for '{group.Key.Name}' "
                    + $"with signature '{group.Key.Signature}': " + string.Join(", ", remaining.Select(names => $"'{names.Key}'"))
                    + ". Assign distinct @get:ClrName values (and @set:ClrName values for colliding setters); automatic hash suffixes are not used");
        }
        var cross = properties.GroupBy(property => (Name: property.Name()
                ?? throw new InvalidOperationException("property has no final physical name"), property.Signature)).Where(group => group.Count() > 1).ToArray();
        if (cross.Length != 0)
            throw new InvalidOperationException("bir2cir: final CLR PropertyDef identities collide after explicit accessor naming: "
                + string.Join(", ", cross.Select(group => $"'{group.Key.Name}' with signature '{group.Key.Signature}'"))
                + ". Assign distinct @get:ClrName values; automatic hash suffixes are not used");
    }
}
