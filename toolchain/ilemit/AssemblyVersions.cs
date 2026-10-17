using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

// Preserve the project-selected physical version facts after the SDK compiler has resolved them.
// No declaration signatures or bodies are imported from the provider assembly.
sealed record AssemblyVersions(Version Identity, IReadOnlyList<(string TypeName, byte[] Blob)> Attributes)
{
    public static AssemblyVersions FromPE(string path)
    {
        using var pe = new PEReader(File.OpenRead(path));
        var metadata = pe.GetMetadataReader();
        var assembly = metadata.GetAssemblyDefinition();
        var attributes = new List<(string, byte[])>();
        foreach (var handle in assembly.GetCustomAttributes())
        {
            var attribute = metadata.GetCustomAttribute(handle);
            if (attribute.Constructor.Kind != HandleKind.MemberReference) continue;
            var ctor = metadata.GetMemberReference((MemberReferenceHandle)attribute.Constructor);
            if (ctor.Parent.Kind != HandleKind.TypeReference) continue;
            var type = metadata.GetTypeReference((TypeReferenceHandle)ctor.Parent);
            var name = metadata.GetString(type.Name);
            if (metadata.GetString(type.Namespace) != "System.Reflection" ||
                name is not ("AssemblyFileVersionAttribute" or "AssemblyInformationalVersionAttribute")) continue;
            // Both standard attributes have exactly one string constructor and no token-bearing blob values.
            attributes.Add(("System.Reflection." + name, metadata.GetBlobBytes(attribute.Value)));
        }
        return new AssemblyVersions(assembly.Version, attributes);
    }
}
