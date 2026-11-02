using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

try
{
    var control = args[0] == "--control";
    if (control) args = args.Skip(1).ToArray();
    var expected = bool.Parse(args[0]);
    using var pe = new PEReader(File.OpenRead(args[1]));
    var metadata = pe.GetMetadataReader();
    var attributes = metadata.GetAssemblyDefinition().GetCustomAttributes()
        .Select(metadata.GetCustomAttribute).Where(attribute =>
        {
            if (attribute.Constructor.Kind != HandleKind.MemberReference) return false;
            var member = metadata.GetMemberReference((MemberReferenceHandle)attribute.Constructor);
            if (member.Parent.Kind != HandleKind.TypeReference) return false;
            var type = metadata.GetTypeReference((TypeReferenceHandle)member.Parent);
            return metadata.GetString(type.Namespace) == "System.Diagnostics"
                && metadata.GetString(type.Name) == "DebuggableAttribute";
        }).ToArray();
    if (attributes.Length != 1)
        throw new InvalidDataException($"Expected exactly one target-BCL DebuggableAttribute, found {attributes.Length}: {args[1]}");
    var constructor = metadata.GetMemberReference((MemberReferenceHandle)attributes[0].Constructor);
    var owner = metadata.GetTypeReference((TypeReferenceHandle)constructor.Parent);
    var assembly = metadata.GetAssemblyReference((AssemblyReferenceHandle)owner.ResolutionScope);
    if (metadata.GetString(assembly.Name) != "System.Runtime" || assembly.Version.Major != 10)
        throw new InvalidDataException("DebuggableAttribute did not use the net10.0 target reference");
    var signature = metadata.GetBlobReader(constructor.Signature);
    signature.ReadSignatureHeader();
    var parameters = signature.ReadCompressedInteger();
    var blob = metadata.GetBlobReader(attributes[0].Value);
    if (blob.ReadUInt16() != 1) throw new InvalidDataException("Invalid custom-attribute prolog");
    var modes = parameters switch
    {
        1 => blob.ReadInt32(),
        2 => (blob.ReadBoolean() ? 1 : 0) | (blob.ReadBoolean() ? 256 : 0),
        _ => throw new InvalidDataException("Unexpected DebuggableAttribute constructor"),
    };
    if (blob.ReadUInt16() != 0 || blob.RemainingBytes != 0)
        throw new InvalidDataException("Unexpected DebuggableAttribute payload");
    if (((modes & 256) != 0) != expected || (expected && (modes & 1) == 0))
        throw new InvalidDataException($"Wrong JIT policy: modes={modes}, expected disabled={expected}");
    if (!control && (modes & ~257) != 0)
        throw new InvalidDataException($"Unrequested EnC or symbol-store policy: modes={modes}");
    Console.WriteLine($"{args[1]}: modes={modes}; JIT optimization disabled={expected}");
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine(error.Message);
    return 1;
}
