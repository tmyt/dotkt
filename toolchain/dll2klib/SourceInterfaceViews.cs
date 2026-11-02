using System.Reflection;
using System.Reflection.Metadata;
using DotKt.Bir;

// A binding declaration states a Kotlin protocol view of an existing native interface. It does not add CLR
// interfaces or methods. Keep the native declaration's own vocabulary and transport the extra source view separately.
internal sealed class SourceInterfaceViews
{
    private sealed record Binding(string Source, string DeclarationPath);
    private readonly Dictionary<(string Path, string Native), Binding> _views = new();

    public static SourceInterfaceViews Discover(IEnumerable<ReferenceMetadataSnapshot> references)
    {
        var inputs = references.ToArray();
        var native = inputs.SelectMany(input => input.Reader.TypeDefinitions.Select(handle =>
            (Input: input, Handle: handle, Definition: input.Reader.GetTypeDefinition(handle))))
            .Where(row => IsTopLevelProtocol(row.Definition))
            .GroupBy(row => Name(row.Input.Reader, row.Definition), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var declarations = new List<(string Native, Binding Binding)>();
        foreach (var input in inputs)
        {
            var attributes = new MetadataAttributes(input.Reader);
            foreach (var handle in input.Reader.TypeDefinitions)
            {
                var definition = input.Reader.GetTypeDefinition(handle);
                if (!IsTopLevelProtocol(definition)) continue;
                if (attributes.Find(handle, "kotlin.clr.ClrTypeAlias")?.StringValue is not { } target) continue;
                // Ref declarations retain their declared source name. A representation carrier, when present,
                // authoritatively supplies a different semantic classifier instead.
                var semantic = attributes.CarrierType(handle, MetadataAttributes.DotKtNs + "KotlinTypeAttribute");
                if (semantic is not null && semantic is not TypeNode.Fqn { Args: null or { Length: 0 } }) continue;
                var source = (semantic as TypeNode.Fqn)?.Name ?? Name(input.Reader, definition);
                if (source != target)
                    declarations.Add((target, new Binding(source, input.Path)));
            }
        }
        var result = new SourceInterfaceViews();
        foreach (var group in declarations.GroupBy(row => row.Native, StringComparer.Ordinal))
        {
            var bindings = group.Distinct().ToArray();
            // An alias string alone cannot select between independent same-FQN definitions or competing source
            // protocols. Do not guess an identity. Generic/native-arity-changing views need richer binding facts.
            if (bindings.Length != 1 || !native.TryGetValue(group.Key, out var definitions) || definitions.Length != 1)
                continue;
            result._views[(definitions[0].Input.Path, group.Key)] = bindings[0].Binding;
        }
        return result;
    }

    public string? View(string path, string native) => _views.GetValueOrDefault((path, native))?.Source;

    public IEnumerable<string> Dependencies(string path) => _views
        .Where(view => view.Key.Path == path).Select(view => view.Value.DeclarationPath).Distinct(StringComparer.Ordinal);

    private static bool IsTopLevelProtocol(TypeDefinition definition) =>
        (definition.Attributes & TypeAttributes.VisibilityMask) == TypeAttributes.Public &&
        (definition.Attributes & TypeAttributes.Interface) != 0 &&
        definition.GetDeclaringType().IsNil && definition.GetGenericParameters().Count == 0;

    private static string Name(MetadataReader reader, TypeDefinition definition) =>
        string.Join('.', new[] { reader.GetString(definition.Namespace), reader.GetString(definition.Name) }
            .Where(part => part.Length != 0));
}
