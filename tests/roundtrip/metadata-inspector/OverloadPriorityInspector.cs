using System.IO.Compression;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using DotKt.Klib.Metadata;

internal static class OverloadPriorityInspector
{
    const string Package = "roundtrip.overloadpriority";
    const string Priority = "kotlin.internal.LowPriorityInOverloadResolution";
    static readonly HashSet<string> Overloads = new(StringComparer.Ordinal) {
        "pick", "genericPick", "extensionPick", "memberPick", "join", "plus", "memberExtension",
        "inheritedPick", "staticPick", "companionPick", "suspendedPick", "invoke" };

    public static void Verify(string dllPath, string klibPath)
    {
        using var input = File.OpenRead(dllPath);
        using var pe = new PEReader(input);
        var metadata = pe.GetMetadataReader();
        var carried = 0;
        foreach (var handle in metadata.MethodDefinitions)
        {
            var method = metadata.GetMethodDefinition(handle);
            if (!TypeName(metadata, method.GetDeclaringType()).StartsWith(Package + ".", StringComparison.Ordinal)) continue;
            var flags = FunctionFlags(metadata, method.GetCustomAttributes());
            if (metadata.GetString(method.Name).Contains("$dotkt_suspend", StringComparison.Ordinal))
                Require((flags & 8) == 0, "physical cold entry acquired source priority");
            if (Attributes(metadata, method.GetCustomAttributes(), "CompilerGeneratedAttribute").Length != 0)
                continue;
            var identities = Attributes(metadata, method.GetCustomAttributes(), "KotlinDeclarationIdentityAttribute");
            if (identities.Length == 0) continue;
            Require(identities.Length == 1, "duplicate declaration identity");
            using var identity = JsonPayload(metadata, identities[0]);
            var name = identity.RootElement.GetProperty("name").GetString()!;
            if (name is ".ctor" or ".cctor" || name.Contains('$')) continue;
            var parameters = identity.RootElement.TryGetProperty("signature", out var signature)
                ? signature.GetProperty("params").EnumerateArray().ToArray() : [];
            var isLow = name is "Alias" or "onlyLow" || Overloads.Contains(name) &&
                parameters.Length > 0 && SourceName(parameters[^1]) == "kotlin.String";
            var modifiers = name == "join" ? 1 : name == "plus" ? 2 : name == "invoke" ? 6 :
                name is "suspendedPick" or "onlyLow" or "sourceSuspended" ? 4 : 0;
            Require(flags == (modifiers | (isLow ? 8 : 0)), name + ": composed PE flags disagree with source");
            Require(!Attributes(metadata, method.GetCustomAttributes(), "LowPriorityInOverloadResolution").Any(),
                "raw priority annotation was not consumed");
            if (isLow) carried++;
        }
        Require(carried >= 16, "missing source-function priority carriers in PE");

        using var archive = ZipFile.OpenRead(klibPath);
        var fragments = archive.Entries.Where(entry => entry.FullName.EndsWith(".knm", StringComparison.Ordinal))
            .Select(entry => { using var stream = entry.Open(); return PackageFragment.Parser.ParseFrom(stream); })
            .Where(fragment => Name(fragment, fragment.Package.PackageFqName) == Package).ToArray();
        var projected = 0;
        var controls = 0;
        foreach (var fragment in fragments)
        {
            Require(fragment.Package.TypeAlias.Any(alias => fragment.Strings.String[alias.Name] == "Alias"),
                "alias was hidden instead of restoring function priority");
            foreach (var function in fragment.Package.Function.Concat(fragment.Class.SelectMany(klass => klass.Function)))
            {
                var name = fragment.Strings.String[function.Name];
                if (!Overloads.Contains(name) && name is not "Alias" and not "onlyLow" and not "control") continue;
                var isLow = name is "Alias" or "onlyLow" || Overloads.Contains(name) &&
                    function.ValueParameter.Count > 0 && function.ValueParameter[^1].Type is { HasClassName: true } last &&
                    Name(fragment, last.ClassName) == "kotlin.String";
                var annotations = function.FunctionAnnotation.Count(annotation => Name(fragment, annotation.Id) == Priority);
                Require(annotations == (isLow ? 1 : 0), name + ": KLIB source priority annotation mismatch");
                if (isLow)
                {
                    Require((function.Flags & 1) != 0, name + ": missing standard HAS_ANNOTATIONS");
                    projected++;
                }
                else controls++;
            }
        }
        Require(projected >= 16 && controls >= 10, "source/unannotated projection coverage disappeared");
    }

    public static void VerifyReference(string path)
    {
        using var input = File.OpenRead(path);
        using var pe = new PEReader(input);
        var metadata = pe.GetMetadataReader();
        var count = 0;
        foreach (var handle in metadata.MethodDefinitions)
        {
            var method = metadata.GetMethodDefinition(handle);
            if ((FunctionFlags(metadata, method.GetCustomAttributes()) & 8) == 0) continue;
            if (method.RelativeVirtualAddress != 0)
            {
                var il = pe.GetMethodBody(method.RelativeVirtualAddress).GetILBytes()!;
                // The emitter appends an unreachable return epilogue after the throw statement.
                // Require the complete reachable body to be the metadata-only stub, not its last byte.
                Require(il.Length >= 6 && il[0] == 0x73 && il[5] == 0x7a,
                    "reference priority method retained executable source body");
                var constructor = System.Reflection.Metadata.Ecma335.MetadataTokens.EntityHandle(
                    BitConverter.ToInt32(il, 1));
                Require(constructor.Kind == HandleKind.MemberReference, "invalid reference throw constructor");
                var member = metadata.GetMemberReference((MemberReferenceHandle)constructor);
                Require(metadata.GetString(member.Name) == ".ctor" && member.Parent.Kind == HandleKind.TypeReference,
                    "invalid reference throw constructor");
                var exception = metadata.GetTypeReference((TypeReferenceHandle)member.Parent);
                Require(metadata.GetString(exception.Namespace) == "System" &&
                    metadata.GetString(exception.Name) == "NotImplementedException", "reference body is not the throw stub");
            }
            count++;
        }
        Require(count > 0, "body stripping removed reference function priority metadata");
    }

    static int FunctionFlags(MetadataReader metadata, CustomAttributeHandleCollection handles)
    {
        var attributes = Attributes(metadata, handles, "KotlinFunctionAttribute");
        Require(attributes.Length <= 1, "duplicate composed function carrier");
        if (attributes.Length == 0) return 0;
        var blob = metadata.GetBlobReader(attributes[0].Value);
        Require(blob.ReadUInt16() == 1, "invalid function carrier prolog");
        var flags = blob.ReadInt32();
        Require(blob.ReadUInt16() == 0 && blob.RemainingBytes == 0, "invalid function carrier payload");
        return flags;
    }

    static CustomAttribute[] Attributes(MetadataReader metadata, CustomAttributeHandleCollection handles, string name) =>
        handles.Select(metadata.GetCustomAttribute).Where(attribute => AttributeName(metadata, attribute) == name).ToArray();

    static string AttributeName(MetadataReader metadata, CustomAttribute attribute)
    {
        if (attribute.Constructor.Kind == HandleKind.MethodDefinition)
            return metadata.GetString(metadata.GetTypeDefinition(metadata.GetMethodDefinition(
                (MethodDefinitionHandle)attribute.Constructor).GetDeclaringType()).Name);
        if (attribute.Constructor.Kind == HandleKind.MemberReference && metadata.GetMemberReference(
                (MemberReferenceHandle)attribute.Constructor).Parent is { Kind: HandleKind.TypeReference } type)
            return metadata.GetString(metadata.GetTypeReference((TypeReferenceHandle)type).Name);
        return "";
    }

    static JsonDocument JsonPayload(MetadataReader metadata, CustomAttribute attribute)
    {
        var blob = metadata.GetBlobReader(attribute.Value);
        Require(blob.ReadUInt16() == 1 && blob.ReadSerializedString() == "bir-json/1", "invalid identity carrier");
        var length = blob.ReadInt32();
        Require(length >= 0 && length <= blob.RemainingBytes - 2, "invalid identity payload length");
        var document = JsonDocument.Parse(blob.ReadBytes(length));
        Require(blob.ReadUInt16() == 0 && blob.RemainingBytes == 0, "invalid identity payload suffix");
        return document;
    }

    static string? SourceName(JsonElement type) => type.GetProperty("t").GetString() is "nullable" or "oblivious"
        ? SourceName(type.GetProperty("of")) : type.TryGetProperty("name", out var name) ? name.GetString() : null;

    static string TypeName(MetadataReader metadata, TypeDefinitionHandle handle)
    {
        var type = metadata.GetTypeDefinition(handle);
        return type.GetDeclaringType().IsNil ? metadata.GetString(type.Namespace) + "." + metadata.GetString(type.Name)
            : TypeName(metadata, type.GetDeclaringType()) + "." + metadata.GetString(type.Name);
    }

    static string Name(PackageFragment fragment, int id)
    {
        var parts = new Stack<string>();
        while (id >= 0)
        {
            var name = fragment.QualifiedNames.QualifiedName[id];
            parts.Push(fragment.Strings.String[name.ShortName]);
            id = name.ParentQualifiedName;
        }
        return string.Join('.', parts);
    }

    static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
