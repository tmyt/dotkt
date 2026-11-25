using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using DotKt.Klib.Metadata;
using KType = DotKt.Klib.Metadata.Type;

internal sealed partial class AssemblyScanner
{
    private sealed record OverrideBase(MetadataReader Reader, SignatureDecoder Decoder,
        RawSignatureTypeProvider Physical, ImmutableArray<KType> Arguments, ILookup<string, MethodDefinitionHandle> Virtuals);

    private RawSignatureTypeProvider SlotIdentity(string? path, ImmutableArray<string> arguments = default) =>
        new((md, entity) => _publicTypeCatalog.TryResolveDefinition(md, entity, out var def, path)
            ? def.Reader.GetGuid(def.Reader.GetModuleDefinition().Mvid) + ":" + MetadataTokens.GetRowNumber(def.Handle)
            : RawSignatureTypeProvider.ScopedReferenceIdentity(md, (TypeReferenceHandle)entity), arguments);

    private List<OverrideBase> OverrideBases(TypeDefinition definition, GenericContext context, NameTable names, SignatureDecoder signatures)
    {
        var result = new List<OverrideBase>();
        var reader = _md;
        var path = _definitionPath;
        var decoder = signatures;
        var raw = SlotIdentity(path);
        ImmutableArray<KType> arguments = [];
        var seen = new HashSet<(Guid, TypeDefinitionHandle)>();
        while (!definition.BaseType.IsNil && _publicTypeCatalog.TryResolveDefinition(reader, definition.BaseType, out var parent, path))
        {
            if (!seen.Add((parent.Reader.GetGuid(parent.Reader.GetModuleDefinition().Mvid), parent.Handle))) break;
            var physicalArgs = raw.BaseArguments(reader, definition.BaseType, context);
            var constructed = SubstituteTypeParameters(decoder.DecodeEntity(definition.BaseType, context, platform: false), arguments);
            reader = parent.Reader;
            path = parent.DefinitionPath;
            raw = SlotIdentity(path, physicalArgs);
            decoder = DecoderFor(reader, names, signatures, path);
            arguments = decoder.DeclarationArguments(parent.Handle, constructed);
            definition = reader.GetTypeDefinition(parent.Handle);
            context = new GenericContext(parent.Handle, default, definition.GetGenericParameters().ToDictionary(h => h, h => reader.GetGenericParameter(h).Index));
            var methods = definition.GetMethods().Where(h => (reader.GetMethodDefinition(h).Attributes & MethodAttributes.Virtual) != 0)
                .ToLookup(h => reader.GetString(reader.GetMethodDefinition(h).Name), StringComparer.Ordinal);
            result.Add(new(reader, decoder, raw, arguments, methods));
        }
        return result;
    }

    private void ReconcileOverrideNullability(Function function, MethodDefinition method, GenericContext context,
        NameTable names, SignatureDecoder signatures, List<OverrideBase> bases)
    {
        var actual = method.DecodeProjectedSignature(SlotIdentity(_definitionPath), context);
        var name = _md.GetString(method.Name);
        foreach (var parent in bases)
        {
            foreach (var handle in parent.Virtuals[name])
            {
                var declaration = parent.Reader.GetMethodDefinition(handle);
                var inheritedContext = InheritedContext(parent.Reader, handle, declaration);
                var slot = declaration.DecodeProjectedSignature(parent.Physical, inheritedContext);
                if (actual.Header != slot.Header || actual.GenericParameterCount != slot.GenericParameterCount ||
                    actual.RequiredParameterCount != slot.RequiredParameterCount || actual.ReturnType != slot.ReturnType ||
                    !actual.ParameterTypes.SequenceEqual(slot.ParameterTypes)) continue;
                var surface = declaration.DecodeProjectedSignature(parent.Decoder, inheritedContext);
                var closed = surface.ParameterTypes.Select(t => SubstituteTypeParameters(t, parent.Arguments)).ToImmutableArray();
                var inherited = InheritedParameters(parent.Reader, handle, declaration, closed, names, parent.Decoder,
                    substitutedDeclarationTypes: surface.ParameterTypes).ToArray();
                for (int i = 0; i < inherited.Length; ++i)
                {
                    function.ValueParameter[i].Type = Reconcile(function.ValueParameter[i].Type, inherited[i].Type, signatures);
                    if (function.ValueParameter[i].VarargElementType is { } element && inherited[i].VarargElementType is { } parentElement)
                        function.ValueParameter[i].VarargElementType = Reconcile(element, parentElement, signatures);
                }
                var ret = ProjectInheritedReturn(parent.Reader, handle, declaration,
                    SubstituteTypeParameters(surface.ReturnType, parent.Arguments), parent.Decoder);
                function.ReturnType = Reconcile(function.ReturnType, ret, signatures, isReturn: true);
                if ((declaration.Attributes & MethodAttributes.NewSlot) != 0) return;
                break;
            }
        }
    }

    // Parameters are invariant for Kotlin overrides; return types may strengthen nullability.
    // Flex only the conflicting occurrence, not every reference in the CLR declaration.
    private static KType Reconcile(KType actual, KType inherited, SignatureDecoder decoder, bool isReturn = false)
    {
        if (actual.ClassName != inherited.ClassName || actual.HasClassName != inherited.HasClassName ||
            actual.TypeParameter != inherited.TypeParameter || actual.HasTypeParameter != inherited.HasTypeParameter ||
            actual.Argument.Count != inherited.Argument.Count) return actual;
        for (int i = 0; i < actual.Argument.Count; ++i)
            if (actual.Argument[i].Type is { } arg && inherited.Argument[i].Type is { } parent)
                actual.Argument[i].Type = Reconcile(arg, parent, decoder);
        return actual.FlexibleUpperBound == null && inherited.FlexibleUpperBound == null &&
            actual.Nullable != inherited.Nullable && (!isReturn || actual.Nullable)
            ? decoder.AsPlatform(actual) : actual;
    }
}
