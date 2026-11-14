using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

// Consume the frontend-selected inherited implementation, never rediscover it
// from ancestor bodies, CLR metadata or an allocated physical member spelling.
static partial class InheritedDefaultFakeOverrideElision
{
    public static void Apply(BirFileDeclaration root, Action<BirFunctionDeclaration> retireActual)
    {
        // Selection is per file and last declaration wins, including a later
        // non-interface shadow. Current owning type placement is flat.
        var local = new Dictionary<string, IBirTypeDeclaration>(StringComparer.Ordinal);
        foreach (var declaration in root.Types.OrEmpty())
            if (declaration.Name.HasValue) local[declaration.Name.Value] = declaration;
        foreach (var declaration in local.Values)
        {
            if (declaration.Kind.OrDefault() != "interface" || declaration is not BirInterfaceDefinition type
                || !type.Methods.HasValue) continue;
            var methods = type.Methods.Value;
            var removedAccessors = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            HashSet<BirFunctionDeclaration> removed = null;
            List<BirInheritedDefaultMethod> added = null;
            for (var index = methods.Length - 1; index >= 0; index--)
            {
                var method = methods[index];
                if (!method.FakeOverride.OrDefault() || !method.InheritedImplementation.HasValue) continue;
                if (KotlinPropertyAccessors.TryIdentity(method, out _, out var role) && method.PropertyAssociation.HasValue)
                {
                    if (!removedAccessors.TryGetValue(method.PropertyAssociation.Value, out var roles))
                        removedAccessors[method.PropertyAssociation.Value] = roles = new(StringComparer.Ordinal);
                    roles.Add(role);
                }
                else if (method.Name.HasValue && method.Params.HasValue && method.Ret.HasValue)
                {
                    (added ??= new()).Add(new BirInheritedDefaultMethod {
                        Member = method.Name,
                        Params = method.Params.Value.Select(parameter => BirGraphValue.CopyType(parameter.Type.OrDefault())).ToArray(),
                        Ret = BirGraphValue.CopyType(method.Ret.Value),
                        Implementation = (BirInheritedImplementation)method.InheritedImplementation.Value.DeepClone(),
                    });
                }
                retireActual(method);
                (removed ??= new(ReferenceEqualityComparer.Instance)).Add(method);
            }
            if (removed != null) type.Methods = methods.Where(method => !removed.Contains(method)).ToArray();
            // Preserve the pre-existing rows, then append in the original
            // reverse-method traversal order. Do not copy extra declaration
            // facts which the old row producer did not own.
            if (added != null) type.InheritedDefaultMethods = type.InheritedDefaultMethods.OrEmpty().Concat(added).ToArray();
            if (removedAccessors.Count == 0 || !type.Properties.HasValue) continue;
            var properties = type.Properties.Value;
            HashSet<BirPropertyDeclaration> removedProperties = null;
            foreach (var property in properties)
            {
                if (!property.PropertyAssociation.HasValue || !property.KotlinAccessors.HasValue
                    || !removedAccessors.TryGetValue(property.PropertyAssociation.Value, out var roles)) continue;
                var retained = property.KotlinAccessors.Value.Where(role => !roles.Contains(role)).ToArray();
                if (retained.Length != property.KotlinAccessors.Value.Length) property.KotlinAccessors = retained;
                if (retained.Length == 0) (removedProperties ??= new(ReferenceEqualityComparer.Instance)).Add(property);
            }
            if (removedProperties != null)
                type.Properties = properties.Where(property => !removedProperties.Contains(property)).ToArray();
        }
    }
}
