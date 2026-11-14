using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class KotlinPropertyAccessors
{
    // The readers describe existing declaration facts. Both owning representations
    // use this one association/signature/carrier decision, including late bridges.
    sealed record AccessorFacts(string Name, string Association, string Role, int Arity,
        string SourceAssociation, string Event, bool Inherited);
    sealed record AccessorPlan(int Index, string Role, bool StampCarrier);
    sealed record PropertyPlan(string Name, string Association, bool Representable, AccessorPlan[] Accessors);

    static PropertyPlan PlanProperty(string name, string association, IReadOnlyList<string> roles, IReadOnlyList<AccessorFacts> methods)
    {
        if (name == null) throw new InvalidOperationException("Kotlin property record has no source name");
        if (association == null) throw new InvalidOperationException($"Kotlin property record '{name}' has no accessor association");
        var resolved = new List<(int Index, string Role)>();
        foreach (var role in roles)
        {
            if (role is not ("get" or "set")) throw new InvalidOperationException($"invalid Kotlin property record accessor role '{role}'");
            var matches = Enumerable.Range(0, methods.Count).Where(index => methods[index].Association == association && methods[index].Role == role).ToArray();
            if (matches.Length != 1) throw new InvalidOperationException(
                $"Kotlin property record '{name}' association '{association}' resolves {matches.Length} '{role}' accessor declaration(s)");
            resolved.Add((matches[0], role));
        }
        var representable = resolved.All(item => methods[item.Index].Arity == 0);
        var bridgeSource = methods.Any(method => method.SourceAssociation == association);
        if (resolved.Count == 0) throw new InvalidOperationException($"Kotlin property record '{name}' has no accessor roles");
        foreach (var (index, _) in resolved)
            if (methods[index].Name == null) throw new InvalidOperationException("Kotlin property accessor has no allocated physical name");
        return new(name, association, representable, resolved.Select(item => new AccessorPlan(item.Index, item.Role,
            !representable || methods[item.Index].SourceAssociation != null || methods[item.Index].Event != null
                || methods[item.Index].Inherited || bridgeSource)).ToArray());
    }

    static AccessorFacts Facts(BirFunctionDeclaration method) => new(method.Name.OrDefault(), method.PropertyAssociation.OrDefault(),
        method.PropertyAccessor.OrDefault(), method.TypeParams.OrEmpty().Length,
        method.KotlinPropertyAccessorCarrier.OrDefault()?.SourceAssociation.OrDefault(),
        method.KotlinPropertyAccessorCarrier.OrDefault()?.Event.OrDefault(),
        method.KotlinPropertyAccessorCarrier.OrDefault()?.Inherited.OrDefault() == true);

    internal static bool TryIdentity(BirFunctionDeclaration declaration, out string sourceName, out string kind)
    {
        sourceName = declaration.PropertyName.OrDefault(); kind = declaration.PropertyAccessor.OrDefault();
        return HasIdentity(sourceName, kind);
    }

    internal static void PreserveCallIdentity(BirCallStatic call, string sourceName, string kind)
    {
        if (sourceName == null || kind is not ("get" or "set"))
            throw new InvalidOperationException("incomplete Kotlin property call identity");
        call.PropertyName = sourceName;
        call.PropertyAccessor = kind;
    }
    internal static void SetPhysicalName(BirFunctionDeclaration declaration, string physicalName)
    {
        if (!TryIdentity(declaration, out _, out _)) throw new InvalidOperationException("cannot allocate a physical accessor name without Kotlin identity");
        declaration.Name = physicalName;
    }

    public static void AllocateDeclarationsAndProperties(IReadOnlyList<BirFileDeclaration> roots)
    {
        void Walk(BirGraphValue node)
        {
            var (methods, properties) = node switch {
                BirFileDeclaration file => (file.Methods.OrEmpty(), file.Properties.OrEmpty()),
                IBirTypeDeclaration type => (type.Methods.OrEmpty(), type.Properties.OrEmpty()),
                _ => (Array.Empty<BirFunctionDeclaration>(), Array.Empty<BirPropertyDeclaration>()),
            };
            foreach (var method in methods)
                if (TryIdentity(method, out var sourceName, out var kind) && method.Name.OrDefault() == sourceName)
                    SetPhysicalName(method, PhysicalName(sourceName, kind));
            foreach (var property in properties) AllocateProperty(property, methods, stripIdentity: false);
            node.VisitChildren(Walk);
        }
        foreach (var root in roots) Walk(root);
    }

    static bool AllocateProperty(BirPropertyDeclaration property, IReadOnlyList<BirFunctionDeclaration> methods, bool stripIdentity)
    {
        if (!property.KotlinAccessors.HasValue) return true;
        var facts = methods.Select(Facts).ToArray();
        var plan = PlanProperty(property.Name.OrDefault(), property.PropertyAssociation.OrDefault(), property.KotlinAccessors.Value, facts);
        var get = false; var set = false;
        foreach (var item in plan.Accessors)
        {
            var method = methods[item.Index]; var fact = facts[item.Index];
            if (item.StampCarrier) method.KotlinPropertyAccessorCarrier = new EventPropertySourceFacts {
                Name = plan.Name, Kind = item.Role, Association = plan.Association,
                SourceAssociation = fact.SourceAssociation != null ? OptionalField<string>.Of(fact.SourceAssociation) : default,
                Event = fact.Event != null ? OptionalField<string>.Of(fact.Event) : default,
                Inherited = fact.Inherited ? OptionalField<bool>.Of(true) : default,
            };
            var result = BirGraphValue.CopyType(method.Ret.OrDefault() ?? throw new InvalidOperationException("Kotlin property accessor has no return type"));
            var signature = method.Params.OrEmpty().Select(FunctionSignatureIdentity.SignatureType).ToArray();
            if (item.Role == "get") {
                property.Get = fact.Name; property.GetSig = signature; property.GetRet = result; property.GetMethodArity = fact.Arity; get = true;
            } else {
                property.Set = fact.Name; property.SetSig = signature; property.SetRet = result; property.SetMethodArity = fact.Arity; set = true;
            }
        }
        if (!get) { property.Get = OptionalField<string>.Null; property.GetSig = default; property.GetRet = default; property.GetMethodArity = default; }
        if (!set) { property.Set = OptionalField<string>.Null; property.SetSig = default; property.SetRet = default; property.SetMethodArity = default; }
        if (stripIdentity) { property.KotlinAccessors = default; property.PropertyAssociation = default; }
        return plan.Representable;
    }
}
