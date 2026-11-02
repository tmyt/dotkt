using System.IO.Compression;
using DotKt.Klib.Metadata;

internal static class ExternalAwaitMetadataInspector
{
    internal static void Verify(string klibPath)
    {
        using var archive = ZipFile.OpenRead(klibPath);
        var fragment = archive.Entries.Where(entry => entry.FullName.EndsWith(".knm", StringComparison.Ordinal))
            .Select(entry => { using var input = entry.Open(); return PackageFragment.Parser.ParseFrom(input); })
            .Single(fragment => Name(fragment, fragment.Package.PackageFqName) == "ExternalAwaitable");
        var extension = fragment.Package.Function.Single(function => IsAwait(fragment, function));
        var parameter = extension.TypeParameter.Single().Id;
        Require(extension.ReceiverType is { HasClassName: true } receiver &&
            Name(fragment, receiver.ClassName) == "ExternalAwaitable.Operation" &&
            receiver.Argument.Single().Type.TypeParameter == parameter,
            "external await extension lost its receiver's method-owned generic frame");
        VerifyResult(fragment, extension, parameter);

        var owner = fragment.Class.Single(type => Name(fragment, type.FqName) == "ExternalAwaitable.MemberOperation");
        var member = owner.Function.Single(function => IsAwait(fragment, function));
        Require(member.TypeParameter.Count == 0 && member.ReceiverType is null,
            "instance await acquired an extension receiver or method type parameters");
        VerifyResult(fragment, member, owner.TypeParameter.Single().Id);
    }

    static bool IsAwait(PackageFragment fragment, Function function) =>
        fragment.Strings.String[function.Name] == "await";

    static void VerifyResult(PackageFragment fragment, Function function, int parameter)
    {
        Require((function.Flags & (1 << 13)) != 0 && function.FunctionAnnotation.Any(annotation =>
            Name(fragment, annotation.Id) == "kotlin.clr.ClrAwaitBridge"),
            "external await bridge lost its suspend contract");
        Require(function.ReturnType.HasTypeParameter && function.ReturnType.TypeParameter == parameter,
            "external GetResult substitution lost the caller's generic result");
        Require(function.ValueParameter.Count == 0,
            "awaitable without ConfigureAwait acquired a captureContext overload");
    }

    static string Name(PackageFragment fragment, int id)
    {
        var parts = new Stack<string>();
        while (id >= 0)
        {
            var item = fragment.QualifiedNames.QualifiedName[id];
            parts.Push(fragment.Strings.String[item.ShortName]);
            id = item.HasParentQualifiedName ? item.ParentQualifiedName : -1;
        }
        return string.Join(".", parts);
    }

    static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
