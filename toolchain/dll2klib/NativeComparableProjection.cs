using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using DotKt.Bir;
using DotKt.Klib.Metadata;
using KType = DotKt.Klib.Metadata.Type;

internal sealed partial class AssemblyScanner
{
    // A source convenience view, not an additional CLR slot or a new Kotlin
    // interface obligation. Its target is the selected constructed interface
    // declaration, including explicit implementations and non-self arguments.
    void AddNativeComparableViews(TypeDefinitionHandle owner, TypeDefinition definition,
        Class result, NameTable names, SignatureDecoder signatures, GenericContext context)
    {
        if (_attrs.IsDotKtAssembly || (definition.Attributes & TypeAttributes.Interface) != 0) return;
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var selected = new HashSet<string>(StringComparer.Ordinal);
        Visit(new ResolvedTypeDefinition(_md, owner, _definitionPath), signatures, context, [], null);

        void Visit(ResolvedTypeDefinition current, SignatureDecoder decoder, GenericContext frame,
            ImmutableArray<KType> arguments, TypeNode[]? physicalArguments, ImmutableArray<bool> publicArguments = default,
            ImmutableArray<SignatureDecoder> argumentDecoders = default)
        {
            var reader = current.Reader;
            var currentDefinition = reader.GetTypeDefinition(current.Handle);
            var visitKey = current.DefinitionPath + ":" + MetadataTokens.GetRowNumber(current.Handle) + ":" +
                string.Join(",", physicalArguments?.Select(TypeNode.ToJson) ?? []);
            if (!visited.Add(visitKey)) return;
            var edges = currentDefinition.GetInterfaceImplementations()
                .Select(handle => (Type: reader.GetInterfaceImplementation(handle).Interface, Slot: (EntityHandle)handle));
            if (!currentDefinition.BaseType.IsNil) edges = edges.Append((currentDefinition.BaseType, (EntityHandle)current.Handle));
            foreach (var (edge, slot) in edges)
            {
                // Operational carriers are not Kotlin inheritance paths.
                if (IsCurrentExistentialCarrier(reader, edge)) continue;
                if (!_publicTypeCatalog.TryResolveDefinition(reader, edge, out var target, current.DefinitionPath)) continue;
                // NRT describes this declaration-shaped edge. Read it before
                // inserting the inherited owner's argument trees.
                var decodedEdge = decoder.DecodeEntity(edge, frame, platform: false);
                if (decoder.IsCompilerOwnedSlotCarrier(decodedEdge)) continue;
                var nativeEdge = !decoder.Attributes.IsDotKtAssembly;
                var declaredEdge = ProjectInheritedType(reader, slot, decodedEdge, current.Handle, decoder,
                    preserveDecodedNullable: nativeEdge);
                var rawShapes = new Dictionary<KType, KType>(ReferenceEqualityComparer.Instance);
                if (nativeEdge) RecordShape(decodedEdge, declaredEdge);
                // Substituted types retain the decoder that read their actual
                // CLR occurrence, including custom value-type facts abroad.
                var edgeDecoders = declaredEdge.Argument.Select(argument =>
                    argument.Type.HasTypeParameter && argument.Type.TypeParameter >= 0 &&
                        !argumentDecoders.IsDefault && argument.Type.TypeParameter < argumentDecoders.Length
                        ? argumentDecoders[argument.Type.TypeParameter] : decoder).ToImmutableArray();
                // Native annotation T? instantiated at a value argument does
                // not construct Nullable<V>. A decoded structural T? does.
                var sourceType = SubstituteTypeParameters(declaredEdge, arguments,
                    nativeEdge ? (source, index) => argumentDecoders[index].IsValueType(arguments[index]) &&
                        !rawShapes[source].Nullable : null);
                var physicalType = (TypeNode.Fqn)NativeSignatureReader.Type(reader, edge, physicalArguments);
                var targetDefinition = target.Reader.GetTypeDefinition(target.Handle);
                var targetDecoder = DecoderFor(target.Reader, names, signatures, target.DefinitionPath);
                var targetFrame = new GenericContext(target.Handle, default, targetDefinition.GetGenericParameters()
                    .ToDictionary(handle => handle, handle => target.Reader.GetGenericParameter(handle).Index));
                var surface = _publicTypeCatalog.Surface(reader, edge, publicArguments);
                if (IsCoreComparable(target) && surface.IsPublic && selected.Add(TypeNode.ToJson(physicalType)))
                {
                    var declaration = targetDefinition.GetMethods().Single(handle => {
                        var method = target.Reader.GetMethodDefinition(handle);
                        return target.Reader.GetString(method.Name) == "CompareTo" &&
                            (method.Attributes & MethodAttributes.Static) == 0 && method.GetGenericParameters().Count == 0;
                    });
                    // Operator lookup first excludes non-operators. Standard
                    // low priority therefore preserves an existing named API.
                    var view = InheritedFunction(new InheritedDefaultImplementation(target.Reader, declaration,
                        "CompareTo", sourceType.Argument.Select(argument => argument.Type).ToImmutableArray(),
                        targetDecoder, null, 0, null, "", false, 0, false), "compareTo", 2 | 8, names,
                        argumentSignatures: edgeDecoders);
                    var binding = ClrExternalAnnotation(names, owner);
                    AddTarget("memberOwner", TypeNode.ToJson(physicalType));
                    AddTarget("memberName", target.Reader.GetString(target.Reader.GetMethodDefinition(declaration).Name));
                    view.FunctionAnnotation.Add(binding);
                    view.Flags |= 1; // Kotlin HAS_ANNOTATIONS makes the selected target visible to the frontend.
                    result.Function.Add(view);

                    void AddTarget(string name, string value) => binding.Argument.Add(new Annotation.Types.Argument {
                        NameId = names.String(name), Value = new Annotation.Types.Argument.Types.Value {
                            Type = Annotation.Types.Argument.Types.Value.Types.Type.String, StringValue = names.String(value),
                        },
                    });
                }
                Visit(target, targetDecoder, targetFrame, targetDecoder.DeclarationArguments(target.Handle, sourceType),
                    physicalType.Args, surface.TypeArguments, targetDecoder.DeclarationArguments(target.Handle, edgeDecoders));

                void RecordShape(KType raw, KType annotated)
                {
                    rawShapes.Add(annotated, raw);
                    if (raw.Argument.Count != annotated.Argument.Count)
                        throw new InvalidDataException("Native edge NRT changed its declared argument shape");
                    for (var i = 0; i < raw.Argument.Count; i++)
                        if (raw.Argument[i].Type is { } original && annotated.Argument[i].Type is { } projected)
                            RecordShape(original, projected);
                    if (annotated.FlexibleUpperBound is { } upper)
                        RecordShape(raw.FlexibleUpperBound ?? raw, upper);
                }
            }
        }

        bool IsCoreComparable(ResolvedTypeDefinition target)
        {
            var type = target.Reader.GetTypeDefinition(target.Handle);
            if (target.Reader.GetString(type.Namespace) != "System" || target.Reader.GetString(type.Name) != "IComparable`1" ||
                (type.Attributes & TypeAttributes.Interface) == 0) return false;
            // Resolve this assembly's Object root through the actual reference
            // universe; a same-named user interface is not the core contract.
            var root = new ResolvedTypeDefinition(_md, owner, _definitionPath);
            var seen = new HashSet<(MetadataReader, TypeDefinitionHandle)>();
            while (seen.Add((root.Reader, root.Handle)))
            {
                var row = root.Reader.GetTypeDefinition(root.Handle);
                if (row.BaseType.IsNil)
                    return root.Reader.GetString(row.Namespace) == "System" && root.Reader.GetString(row.Name) == "Object" &&
                        StringComparer.Ordinal.Equals(root.DefinitionPath, target.DefinitionPath);
                if (!_publicTypeCatalog.TryResolveDefinition(root.Reader, row.BaseType, out root, root.DefinitionPath)) return false;
            }
            return false;
        }
    }
}
