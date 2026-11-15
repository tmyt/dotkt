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
            ImmutableArray<KType> arguments, TypeNode[]? physicalArguments)
        {
            var reader = current.Reader;
            var currentDefinition = reader.GetTypeDefinition(current.Handle);
            var visitKey = current.DefinitionPath + ":" + MetadataTokens.GetRowNumber(current.Handle) + ":" +
                string.Join(",", physicalArguments?.Select(TypeNode.ToJson) ?? []);
            if (!visited.Add(visitKey)) return;
            IEnumerable<EntityHandle> edges = currentDefinition.GetInterfaceImplementations()
                .Select(handle => reader.GetInterfaceImplementation(handle).Interface);
            if (!currentDefinition.BaseType.IsNil) edges = edges.Append(currentDefinition.BaseType);
            foreach (var edge in edges)
            {
                if (!_publicTypeCatalog.TryResolveDefinition(reader, edge, out var target, current.DefinitionPath)) continue;
                var sourceType = SubstituteTypeParameters(decoder.DecodeEntity(edge, frame, platform: false), arguments);
                var physicalType = (TypeNode.Fqn)NativeSignatureReader.Type(reader, edge, physicalArguments);
                var targetDefinition = target.Reader.GetTypeDefinition(target.Handle);
                var targetDecoder = DecoderFor(target.Reader, names, signatures, target.DefinitionPath);
                var targetFrame = new GenericContext(target.Handle, default, targetDefinition.GetGenericParameters()
                    .ToDictionary(handle => handle, handle => target.Reader.GetGenericParameter(handle).Index));
                if (IsCoreComparable(target) && selected.Add(TypeNode.ToJson(physicalType)))
                {
                    var declaration = targetDefinition.GetMethods().Single(handle => {
                        var method = target.Reader.GetMethodDefinition(handle);
                        return target.Reader.GetString(method.Name) == "CompareTo" &&
                            (method.Attributes & MethodAttributes.Static) == 0 && method.GetGenericParameters().Count == 0;
                    });
                    var view = InheritedFunction(new InheritedDefaultImplementation(target.Reader, declaration,
                        "CompareTo", sourceType.Argument.Select(argument => argument.Type).ToImmutableArray(),
                        targetDecoder, null, 0, null, "", false, 0, false), "compareTo", 2, names,
                        argumentSignatures: decoder);
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
                Visit(target, targetDecoder, targetFrame, targetDecoder.DeclarationArguments(target.Handle, sourceType), physicalType.Args);
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
