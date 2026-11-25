using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using DotKt.Klib.Metadata;
using KType = DotKt.Klib.Metadata.Type;

internal sealed partial class AssemblyScanner
{
    private sealed record OverrideBase(MetadataReader Reader, TypeDefinitionHandle Owner, string? Path,
        SignatureDecoder Decoder, RawSignatureTypeProvider Physical, ImmutableArray<KType> Arguments,
        ILookup<string, MethodDefinitionHandle> Virtuals);
    private readonly ConcurrentDictionary<(MetadataReader Reader, TypeDefinitionHandle Owner), ILookup<string, MethodDefinitionHandle>> _virtualIndices = new();

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
            var methods = _virtualIndices.GetOrAdd((reader, parent.Handle), static key =>
                key.Reader.GetTypeDefinition(key.Owner).GetMethods()
                    .Where(h => (key.Reader.GetMethodDefinition(h).Attributes & MethodAttributes.Virtual) != 0)
                    .ToLookup(h => key.Reader.GetString(key.Reader.GetMethodDefinition(h).Name), StringComparer.Ordinal));
            result.Add(new(reader, parent.Handle, path, decoder, raw, arguments, methods));
        }
        return result;
    }

    private void ReconcileOverrideNullability(Function function, MethodDefinitionHandle body, GenericContext context,
        NameTable names, SignatureDecoder signatures, List<OverrideBase> bases)
    {
        var visited = new HashSet<(int, MethodDefinitionHandle)>();
        Visit(-1, body, context);
        void Visit(int level, MethodDefinitionHandle handle, GenericContext sourceContext)
        {
            var reader = level < 0 ? _md : bases[level].Reader;
            var path = level < 0 ? _definitionPath : bases[level].Path;
            var method = reader.GetMethodDefinition(handle);
            // A covariant return (or an explicitly selected hidden slot) uses a class MethodImpl,
            // independent of the body's NewSlot flag. Its declaration, not the body's return type, identifies it.
            foreach (var implHandle in reader.GetTypeDefinition(method.GetDeclaringType()).GetMethodImplementations())
            {
                var impl = reader.GetMethodImplementation(implHandle);
                if (impl.MethodBody != handle) continue;
                var target = impl.MethodDeclaration;
                if (target.Kind == HandleKind.MethodDefinition)
                {
                    var decl = (MethodDefinitionHandle)target;
                    var owner = reader.GetMethodDefinition(decl).GetDeclaringType();
                    for (int i = level + 1; i < bases.Count; i++)
                        if (bases[i].Reader == reader && bases[i].Owner == owner) Apply(i, decl);
                }
                else if (target.Kind == HandleKind.MemberReference)
                {
                    var member = reader.GetMemberReference((MemberReferenceHandle)target);
                    if (!_publicTypeCatalog.TryResolveDefinition(reader, member.Parent, out var owner, path)) continue;
                    for (int i = level + 1; i < bases.Count; i++)
                    {
                        var parent = bases[i];
                        if (parent.Reader.GetGuid(parent.Reader.GetModuleDefinition().Mvid) !=
                            owner.Reader.GetGuid(owner.Reader.GetModuleDefinition().Mvid) || parent.Owner != owner.Handle) continue;
                        var expected = member.DecodeProjectedMethodSignature(SlotIdentity(path), sourceContext);
                        foreach (var decl in parent.Virtuals[reader.GetString(member.Name)])
                        {
                            var definition = parent.Reader.GetMethodDefinition(decl);
                            if (SameSignature(expected, definition.DecodeProjectedSignature(SlotIdentity(parent.Path),
                                    InheritedContext(parent.Reader, decl, definition)))) Apply(i, decl);
                        }
                    }
                }
            }
            if ((method.Attributes & MethodAttributes.NewSlot) != 0) return;
            var actual = method.DecodeProjectedSignature(level < 0 ? SlotIdentity(path) : bases[level].Physical, sourceContext);
            for (int i = level + 1; i < bases.Count; i++)
            {
                var parent = bases[i];
                foreach (var decl in parent.Virtuals[reader.GetString(method.Name)])
                {
                    var candidate = parent.Reader.GetMethodDefinition(decl);
                    if (!IsPublicOrProtected(candidate.Attributes)) continue;
                    if (!SameSignature(actual, candidate.DecodeProjectedSignature(parent.Physical,
                        InheritedContext(parent.Reader, decl, candidate)))) continue;
                    Apply(i, decl);
                    return;
                }
            }
        }
        void Apply(int i, MethodDefinitionHandle decl)
        {
            var parent = bases[i];
            if (!visited.Add((i, decl)) || parent.Decoder.Attributes.IsDotKtAssembly) return;
            var declaration = parent.Reader.GetMethodDefinition(decl);
            var ctx = InheritedContext(parent.Reader, decl, declaration);
            var surface = declaration.DecodeProjectedSignature(parent.Decoder, ctx);
            var n = 0;
            foreach (var inherited in InheritedParameters(parent.Reader, decl, declaration, surface.ParameterTypes, names, parent.Decoder))
            {
                var parameter = function.ValueParameter[n++];
                var closed = SubstituteTypeParameters(inherited.Type, parent.Arguments);
                parameter.Type = Reconcile(parameter.Type, closed, signatures);
                if (parameter.VarargElementType != null) parameter.VarargElementType = signatures.ArrayElement(parameter.Type);
            }
            Visit(i, decl, ctx);
        }
    }

    private static bool SameSignature(MethodSignature<string> a, MethodSignature<string> b) =>
        a.Header == b.Header && a.GenericParameterCount == b.GenericParameterCount &&
        a.RequiredParameterCount == b.RequiredParameterCount && a.ReturnType == b.ReturnType &&
        a.ParameterTypes.SequenceEqual(b.ParameterTypes);

    // Kotlin override parameters are invariant. Preserve the return contract and flex only conflicting
    // reference occurrences. A declaration's NRT bytes are interpreted before substituting its owner arguments.
    private static KType Reconcile(KType actual, KType inherited, SignatureDecoder decoder)
    {
        if (actual.ClassName != inherited.ClassName || actual.HasClassName != inherited.HasClassName ||
            actual.TypeParameter != inherited.TypeParameter || actual.HasTypeParameter != inherited.HasTypeParameter ||
            actual.Argument.Count != inherited.Argument.Count) return actual;
        for (int i = 0; i < actual.Argument.Count; ++i)
            if (actual.Argument[i].Type is { } arg && inherited.Argument[i].Type is { } parent)
                actual.Argument[i].Type = Reconcile(arg, parent, decoder);
        if (actual.FlexibleUpperBound != null) return decoder.AsPlatform(actual);
        return inherited.FlexibleUpperBound == null && !decoder.IsValueType(actual) && actual.Nullable != inherited.Nullable
            ? decoder.AsPlatform(actual) : actual;
    }
}
