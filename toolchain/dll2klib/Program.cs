using System.Collections.Immutable;
using System.IO.Compression;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using DotKt.Klib.Metadata;
using Google.Protobuf;
using KType = DotKt.Klib.Metadata.Type;

internal static class Program
{
    public static int Main(string[] args)
    {
        try
        {
            if (args.Length != 2)
            {
                Console.Error.WriteLine("usage: dll2klib <reference.dll> <output.klib>");
                return 2;
            }
            Convert(Path.GetFullPath(args[0]), Path.GetFullPath(args[1]));
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"dll2klib: {ex.Message}");
            return 1;
        }
    }

    private static void Convert(string input, string output)
    {
        using var file = File.OpenRead(input);
        using var pe = new PEReader(file, PEStreamOptions.PrefetchMetadata);
        if (!pe.HasMetadata || pe.PEHeaders.CorHeader is null)
            throw new InvalidDataException($"not a managed PE: {input}");

        var md = pe.GetMetadataReader();
        var assemblyName = md.IsAssembly ? md.GetString(md.GetAssemblyDefinition().Name) : Path.GetFileNameWithoutExtension(input);
        var moduleName = $"clr.{assemblyName}.{md.GetGuid(md.GetModuleDefinition().Mvid):N}";
        var fragments = new AssemblyScanner(md).Scan();

        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var temp = output + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                Write(zip, "default/manifest", Manifest(moduleName));
                var header = new Header { ModuleName = moduleName };
                header.PackageFragmentName.Add(fragments.Select(x => x.PackageName));
                Write(zip, "default/linkdata/module", header.ToByteArray());
                foreach (var fragment in fragments)
                {
                    var dir = "default/linkdata/package_" + fragment.PackageName;
                    var shortName = fragment.PackageName.Split('.').LastOrDefault() ?? "";
                    Write(zip, $"{dir}/0_{shortName}.knm", fragment.Message.ToByteArray());
                }
            }
            File.Move(temp, output, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
        Console.WriteLine($"{Path.GetFileName(input)} -> {Path.GetFileName(output)}: {fragments.Sum(x => x.Message.Class.Count)} public class(es)");
    }

    private static byte[] Manifest(string moduleName) => System.Text.Encoding.UTF8.GetBytes(
        "abi_version=2.4.0\n" +
        "compiler_version=2.4.0\n" +
        "ir_signature_versions=1,2\n" +
        "metadata_version=2.4.0\n" +
        $"unique_name={moduleName}\n");

    private static void Write(ZipArchive zip, string name, byte[] bytes)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Fastest);
        entry.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
        using var output = entry.Open();
        output.Write(bytes);
    }
}

internal sealed record Fragment(string PackageName, PackageFragment Message);

internal sealed class AssemblyScanner
{
    private readonly MetadataReader _md;

    public AssemblyScanner(MetadataReader md) => _md = md;

    public IReadOnlyList<Fragment> Scan()
    {
        var visible = _md.TypeDefinitions
            .Select(h => (Handle: h, Definition: _md.GetTypeDefinition(h)))
            .Where(x => IsPublicTopLevel(x.Definition))
            .Where(x => _md.GetString(x.Definition.Name) != "<Module>")
            .GroupBy(x => _md.GetString(x.Definition.Namespace), StringComparer.Ordinal);

        var result = new List<Fragment>();
        foreach (var package in visible.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            var names = new NameTable();
            var fragment = new PackageFragment {
                Package = new Package(),
                IsEmpty = false,
                FqName = package.Key,
            };
            fragment.Package.PackageFqName = names.Package(package.Key);
            var signatures = new SignatureDecoder(_md, names);

            foreach (var (handle, def) in package.OrderBy(x => _md.GetString(x.Definition.Name), StringComparer.Ordinal))
            {
                try
                {
                    var klass = ReadClass(handle, def, names, signatures);
                    fragment.Class.Add(klass);
                    fragment.ClassName.Add(klass.FqName);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"dll2klib: warning: skipped {FullName(def)}: {ex.Message}");
                }
            }
            fragment.Strings = names.Strings;
            fragment.QualifiedNames = names.QualifiedNames;
            if (fragment.Class.Count != 0) result.Add(new Fragment(package.Key, fragment));
        }
        return result;
    }

    private Class ReadClass(TypeDefinitionHandle handle, TypeDefinition def, NameTable names, SignatureDecoder signatures)
    {
        var metadataName = _md.GetString(def.Name);
        var kotlinName = StripArity(metadataName);
        var isInterface = (def.Attributes & TypeAttributes.Interface) != 0;
        var isEnum = IsSystemType(def.BaseType, "System", "Enum");
        var isAnnotation = IsSystemType(def.BaseType, "System", "Attribute");
        var kind = isInterface ? 1 : isEnum ? 2 : isAnnotation ? 4 : 0;
        var modality = kind == 1 || (def.Attributes & TypeAttributes.Abstract) != 0 ? 2
            : (def.Attributes & TypeAttributes.Sealed) == 0 ? 1 : 0;
        var result = new Class {
            FqName = names.Class(FullName(def, kotlinName)),
            Flags = Flags.Declaration(modality, kind, hasEnumEntries: isEnum),
        };

        var typeParameterIds = new Dictionary<GenericParameterHandle, int>();
        foreach (var gpHandle in def.GetGenericParameters())
        {
            var gp = _md.GetGenericParameter(gpHandle);
            var id = gp.Index;
            typeParameterIds[gpHandle] = id;
            result.TypeParameter.Add(new TypeParameter {
                Id = id,
                Name = names.String(_md.GetString(gp.Name)),
                Variance = (gp.Attributes & GenericParameterAttributes.VarianceMask) switch {
                    GenericParameterAttributes.Covariant => TypeParameter.Types.Variance.Out,
                    GenericParameterAttributes.Contravariant => TypeParameter.Types.Variance.In,
                    _ => TypeParameter.Types.Variance.Inv,
                },
            });
        }

        var typeContext = new GenericContext(handle, default, typeParameterIds);
        if (isEnum)
        {
            var enumBase = new KType { ClassName = names.Class("kotlin.Enum") };
            var self = new KType { ClassName = result.FqName };
            foreach (var tp in result.TypeParameter)
                self.Argument.Add(new KType.Types.Argument {
                    Projection = KType.Types.Argument.Types.Projection.Inv,
                    Type = new KType { TypeParameter = tp.Id },
                });
            enumBase.Argument.Add(new KType.Types.Argument {
                Projection = KType.Types.Argument.Types.Projection.Inv,
                Type = self,
            });
            result.Supertype.Add(enumBase);
        }
        else
        {
            if (!def.BaseType.IsNil &&
                !IsSystemType(def.BaseType, "System", "Object") &&
                !IsSystemType(def.BaseType, "System", "ValueType") &&
                !IsSystemType(def.BaseType, "System", "Attribute"))
                result.Supertype.Add(signatures.DecodeEntity(def.BaseType, typeContext, platform: false));
            foreach (var implHandle in def.GetInterfaceImplementations())
            {
                var impl = _md.GetInterfaceImplementation(implHandle);
                result.Supertype.Add(signatures.DecodeEntity(impl.Interface, typeContext, platform: false));
            }
            if (result.Supertype.Count == 0)
                result.Supertype.Add(new KType { ClassName = names.Class("kotlin.Any") });
        }

        foreach (var methodHandle in def.GetMethods())
        {
            var method = _md.GetMethodDefinition(methodHandle);
            if (!IsPublicOrProtected(method.Attributes)) continue;
            var name = _md.GetString(method.Name);
            var context = new GenericContext(handle, methodHandle, typeParameterIds);
            var sig = method.DecodeSignature(signatures, context);
            if (name == ".ctor")
            {
                result.Constructor.Add(new Constructor {
                    Flags = Flags.Visibility(method.Attributes),
                    ValueParameter = { Parameters(method, sig.ParameterTypes, names) },
                });
            }
            else if ((method.Attributes & MethodAttributes.SpecialName) == 0 && !name.StartsWith('<'))
            {
                var modalityForMethod = (method.Attributes & MethodAttributes.Abstract) != 0 ? 2
                    : (method.Attributes & MethodAttributes.Virtual) != 0 && (method.Attributes & MethodAttributes.Final) == 0 ? 1 : 0;
                var function = new Function {
                    Name = names.String(name),
                    Flags = Flags.Callable(method.Attributes, modalityForMethod),
                    ReturnType = sig.ReturnType,
                    ValueParameter = { Parameters(method, sig.ParameterTypes, names) },
                };
                foreach (var gpHandle in method.GetGenericParameters())
                {
                    var gp = _md.GetGenericParameter(gpHandle);
                    var parameter = new TypeParameter {
                        Id = 10000 + gp.Index,
                        Name = names.String(_md.GetString(gp.Name)),
                        Variance = TypeParameter.Types.Variance.Inv,
                    };
                    foreach (var constraintHandle in gp.GetConstraints())
                    {
                        var constraint = _md.GetGenericParameterConstraint(constraintHandle);
                        parameter.UpperBound.Add(signatures.DecodeEntity(constraint.Type, context, platform: false));
                    }
                    function.TypeParameter.Add(parameter);
                }
                result.Function.Add(function);
            }
        }

        var propertyNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var propertyHandle in def.GetProperties())
        {
            var property = _md.GetPropertyDefinition(propertyHandle);
            var accessors = property.GetAccessors();
            var getter = accessors.Getter.IsNil ? default(MethodDefinition?) : _md.GetMethodDefinition(accessors.Getter);
            var setter = accessors.Setter.IsNil ? default(MethodDefinition?) : _md.GetMethodDefinition(accessors.Setter);
            if (getter is not { } getMethod && setter is not { } setMethod) continue;
            var representative = getter ?? setter!.Value;
            if (!IsPublicOrProtected(representative.Attributes)) continue;
            var context = new GenericContext(handle, accessors.Getter.IsNil ? accessors.Setter : accessors.Getter, typeParameterIds);
            var signature = property.DecodeSignature(signatures, context);
            if (signature.ParameterTypes.Length != 0) continue; // indexed properties are projected as operators later
            var name = _md.GetString(property.Name);
            var canWrite = setter is { } sm && IsPublicOrProtected(sm.Attributes);
            var isStatic = (representative.Attributes & MethodAttributes.Static) != 0;
            result.Property.Add(new Property {
                Name = names.String(name),
                ReturnType = signature.ReturnType,
                Flags = Flags.Property(representative.Attributes, canWrite, isStatic),
                SetterValueParameter = canWrite
                    ? new ValueParameter { Name = names.String("value"), Type = signature.ReturnType.Clone() }
                    : null,
            });
            propertyNames.Add(name);
        }

        foreach (var fieldHandle in def.GetFields())
        {
            var field = _md.GetFieldDefinition(fieldHandle);
            if (!IsPublicOrProtected(field.Attributes)) continue;
            var name = _md.GetString(field.Name);
            if (name.StartsWith('<') || propertyNames.Contains(name)) continue;
            if (isEnum && (field.Attributes & FieldAttributes.Literal) != 0 &&
                (field.Attributes & FieldAttributes.Static) != 0)
            {
                result.EnumEntry.Add(new EnumEntry { Name = names.String(name) });
                continue;
            }
            var fieldType = field.DecodeSignature(signatures, typeContext);
            var canWrite = (field.Attributes & (FieldAttributes.InitOnly | FieldAttributes.Literal)) == 0;
            result.Property.Add(new Property {
                Name = names.String(name),
                ReturnType = fieldType,
                Flags = Flags.Property(field.Attributes, canWrite),
                SetterValueParameter = canWrite
                    ? new ValueParameter { Name = names.String("value"), Type = fieldType.Clone() }
                    : null,
            });
        }
        return result;
    }

    private IEnumerable<ValueParameter> Parameters(MethodDefinition method, ImmutableArray<KType> types, NameTable names)
    {
        var rows = method.GetParameters().Select(h => _md.GetParameter(h))
            .Where(p => p.SequenceNumber > 0).ToDictionary(p => p.SequenceNumber);
        for (var i = 0; i < types.Length; i++)
        {
            rows.TryGetValue(i + 1, out var row);
            var name = row.Name.IsNil ? $"arg{i}" : _md.GetString(row.Name);
            yield return new ValueParameter { Name = names.String(string.IsNullOrEmpty(name) ? $"arg{i}" : name), Type = types[i] };
        }
    }

    private string FullName(TypeDefinition def, string? simpleName = null)
    {
        var ns = _md.GetString(def.Namespace);
        var name = simpleName ?? _md.GetString(def.Name);
        return string.IsNullOrEmpty(ns) ? name : ns + "." + name;
    }

    private static bool IsPublicTopLevel(TypeDefinition def) =>
        (def.Attributes & TypeAttributes.VisibilityMask) == TypeAttributes.Public;
    private static bool IsPublicOrProtected(MethodAttributes attrs) =>
        (attrs & MethodAttributes.MemberAccessMask) is MethodAttributes.Public or MethodAttributes.Family or MethodAttributes.FamORAssem;
    private static bool IsPublicOrProtected(FieldAttributes attrs) =>
        (attrs & FieldAttributes.FieldAccessMask) is FieldAttributes.Public or FieldAttributes.Family or FieldAttributes.FamORAssem;
    private bool IsSystemType(EntityHandle handle, string ns, string name)
    {
        if (handle.IsNil) return false;
        return handle.Kind switch {
            HandleKind.TypeReference => IsReference(_md.GetTypeReference((TypeReferenceHandle)handle)),
            HandleKind.TypeDefinition => IsDefinition(_md.GetTypeDefinition((TypeDefinitionHandle)handle)),
            _ => false,
        };
        bool IsReference(TypeReference t) => _md.GetString(t.Namespace) == ns && _md.GetString(t.Name) == name;
        bool IsDefinition(TypeDefinition t) => _md.GetString(t.Namespace) == ns && _md.GetString(t.Name) == name;
    }
    private static string StripArity(string name) => name.Contains('`') ? name[..name.IndexOf('`')] : name;
}

internal static class Flags
{
    // metadata.proto: hasAnnotations(1), visibility(3), modality(2), then class kind/member kind.
    public static int Declaration(int modality, int kind, bool hasEnumEntries = false) =>
        6 | (modality << 4) | (kind << 6) | (hasEnumEntries ? 1 << 15 : 0);
    public static int Callable(MethodAttributes attrs, int modality) =>
        Visibility(attrs) | (modality << 4)
        // Kotlin 2.4 metadata Flags.IS_STATIC_FUNCTION. This is a frontend fact
        // present in ECMA-335 MethodAttributes, not a CLR call-shape decision.
        | ((attrs & MethodAttributes.Static) != 0 ? 1 << 18 : 0);
    public static int Visibility(MethodAttributes attrs) =>
        (attrs & MethodAttributes.MemberAccessMask) == MethodAttributes.Public ? 6 : 4; // PUBLIC=3, PROTECTED=2
    public static int Property(MethodAttributes attrs, bool canWrite, bool isStatic) =>
        Visibility(attrs) | (((attrs & MethodAttributes.Abstract) != 0 ? 2
            : (attrs & MethodAttributes.Virtual) != 0 && (attrs & MethodAttributes.Final) == 0 ? 1 : 0) << 4)
        | (canWrite ? 1 << 8 : 0) | 1 << 9 | (canWrite ? 1 << 10 : 0)
        | (isStatic ? 1 << 19 : 0);
    public static int Property(FieldAttributes attrs, bool canWrite) =>
        ((attrs & FieldAttributes.FieldAccessMask) == FieldAttributes.Public ? 6 : 4)
        | (canWrite ? 1 << 8 : 0) | 1 << 9 | (canWrite ? 1 << 10 : 0)
        | ((attrs & FieldAttributes.Static) != 0 ? 1 << 19 : 0);
}

internal sealed class NameTable
{
    private readonly Dictionary<string, int> _strings = new(StringComparer.Ordinal);
    private readonly Dictionary<(int Parent, int Short, QualifiedNameTable.Types.QualifiedName.Types.Kind Kind), int> _qualified = new();
    private readonly Dictionary<int, string> _classNames = new();
    public StringTable Strings { get; } = new();
    public QualifiedNameTable QualifiedNames { get; } = new();

    public int String(string value)
    {
        if (_strings.TryGetValue(value, out var id)) return id;
        id = Strings.String.Count;
        Strings.String.Add(value);
        _strings.Add(value, id);
        return id;
    }

    public int Package(string fqName)
    {
        var parent = -1;
        if (string.IsNullOrEmpty(fqName)) return parent;
        foreach (var part in fqName.Split('.'))
            parent = Qualified(parent, String(part), QualifiedNameTable.Types.QualifiedName.Types.Kind.Package);
        return parent;
    }

    public int Class(string fqName)
    {
        var dot = fqName.LastIndexOf('.');
        var package = dot < 0 ? "" : fqName[..dot];
        var simple = dot < 0 ? fqName : fqName[(dot + 1)..];
        var id = Qualified(Package(package), String(simple), QualifiedNameTable.Types.QualifiedName.Types.Kind.Class);
        _classNames[id] = fqName;
        return id;
    }

    public string? ClassName(int id) => _classNames.GetValueOrDefault(id);

    private int Qualified(int parent, int shortName, QualifiedNameTable.Types.QualifiedName.Types.Kind kind)
    {
        var key = (parent, shortName, kind);
        if (_qualified.TryGetValue(key, out var id)) return id;
        id = QualifiedNames.QualifiedName.Count;
        QualifiedNames.QualifiedName.Add(new QualifiedNameTable.Types.QualifiedName {
            ParentQualifiedName = parent,
            ShortName = shortName,
            Kind = kind,
        });
        _qualified.Add(key, id);
        return id;
    }
}

internal sealed record GenericContext(
    TypeDefinitionHandle Type,
    MethodDefinitionHandle Method,
    IReadOnlyDictionary<GenericParameterHandle, int> TypeParameterIds);

internal sealed class SignatureDecoder : ISignatureTypeProvider<KType, GenericContext>
{
    private readonly MetadataReader _md;
    private readonly NameTable _names;
    public SignatureDecoder(MetadataReader md, NameTable names) { _md = md; _names = names; }

    public KType GetArrayType(KType elementType, ArrayShape shape) => Array(elementType);
    public KType GetByReferenceType(KType elementType) => elementType;
    public KType GetFunctionPointerType(MethodSignature<KType> signature) => Any(nullable: true);
    public KType GetGenericInstantiation(KType genericType, ImmutableArray<KType> typeArguments)
    {
        if (genericType.HasClassName && _names.ClassName(genericType.ClassName) == "System.Nullable" &&
            typeArguments.Length == 1)
            return Nullable(typeArguments[0]);
        var copy = genericType.Clone();
        copy.Argument.Add(typeArguments.Select(t => new KType.Types.Argument {
            Projection = KType.Types.Argument.Types.Projection.Inv,
            Type = t,
        }));
        if (copy.FlexibleUpperBound is { } upper)
            upper.Argument.Add(typeArguments.Select(t => new KType.Types.Argument {
                Projection = KType.Types.Argument.Types.Projection.Inv,
                Type = t.Clone(),
            }));
        return copy;
    }
    public KType GetGenericMethodParameter(GenericContext genericContext, int index) => new() { TypeParameter = 10000 + index };
    public KType GetGenericTypeParameter(GenericContext genericContext, int index) => new() { TypeParameter = index };
    public KType GetModifiedType(KType modifier, KType unmodifiedType, bool isRequired) => unmodifiedType;
    public KType GetPinnedType(KType elementType) => elementType;
    public KType GetPointerType(KType elementType) => Any(nullable: true);
    public KType GetPrimitiveType(PrimitiveTypeCode code) => code switch {
        PrimitiveTypeCode.Void => Named("kotlin.Unit"),
        PrimitiveTypeCode.Boolean => Named("kotlin.Boolean"),
        PrimitiveTypeCode.Char => Named("kotlin.Char"),
        PrimitiveTypeCode.SByte => Named("kotlin.Byte"),
        PrimitiveTypeCode.Byte => Named("kotlin.UByte"),
        PrimitiveTypeCode.Int16 => Named("kotlin.Short"),
        PrimitiveTypeCode.UInt16 => Named("kotlin.UShort"),
        PrimitiveTypeCode.Int32 => Named("kotlin.Int"),
        PrimitiveTypeCode.UInt32 => Named("kotlin.UInt"),
        PrimitiveTypeCode.Int64 => Named("kotlin.Long"),
        PrimitiveTypeCode.UInt64 => Named("kotlin.ULong"),
        PrimitiveTypeCode.Single => Named("kotlin.Float"),
        PrimitiveTypeCode.Double => Named("kotlin.Double"),
        PrimitiveTypeCode.String => Platform("kotlin.String"),
        PrimitiveTypeCode.Object => Platform("kotlin.Any"),
        _ => Any(nullable: true),
    };
    public KType GetSZArrayType(KType elementType) => Array(elementType);
    public KType GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind)
    {
        var def = reader.GetTypeDefinition(handle);
        var name = FullName(reader.GetString(def.Namespace), StripArity(reader.GetString(def.Name)));
        return rawTypeKind == (byte)SignatureTypeKind.Class ? Platform(name) : Named(name);
    }
    public KType GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
    {
        var type = reader.GetTypeReference(handle);
        var full = FullName(reader.GetString(type.Namespace), StripArity(reader.GetString(type.Name)));
        return full switch {
            "System.String" => Platform("kotlin.String"),
            "System.Object" => Platform("kotlin.Any"),
            _ => rawTypeKind == (byte)SignatureTypeKind.Class ? Platform(full) : Named(full),
        };
    }
    public KType GetTypeFromSpecification(MetadataReader reader, GenericContext genericContext, TypeSpecificationHandle handle, byte rawTypeKind) =>
        reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);

    public KType DecodeEntity(EntityHandle handle, GenericContext context, bool platform) =>
        handle.Kind switch {
            HandleKind.TypeDefinition => FromDefinition((TypeDefinitionHandle)handle, platform),
            HandleKind.TypeReference => FromReference((TypeReferenceHandle)handle, platform),
            HandleKind.TypeSpecification => _md.GetTypeSpecification((TypeSpecificationHandle)handle).DecodeSignature(this, context),
            _ => Any(nullable: true),
        };

    private KType Named(string fqName, bool nullable = false) => new() { ClassName = _names.Class(fqName), Nullable = nullable };
    private KType Platform(string fqName)
    {
        var lower = Named(fqName);
        lower.FlexibleTypeCapabilitiesId = _names.String("dotkt.clr.PlatformType");
        lower.FlexibleUpperBound = Named(fqName, nullable: true);
        return lower;
    }
    private KType FromDefinition(TypeDefinitionHandle handle, bool platform)
    {
        var def = _md.GetTypeDefinition(handle);
        var name = FullName(_md.GetString(def.Namespace), StripArity(_md.GetString(def.Name)));
        return platform ? Platform(name) : Named(name);
    }
    private KType FromReference(TypeReferenceHandle handle, bool platform)
    {
        var type = _md.GetTypeReference(handle);
        var name = FullName(_md.GetString(type.Namespace), StripArity(_md.GetString(type.Name)));
        name = name switch {
            "System.String" => "kotlin.String",
            "System.Object" => "kotlin.Any",
            _ => name,
        };
        return platform ? Platform(name) : Named(name);
    }
    private static KType Nullable(KType type)
    {
        var result = type.Clone();
        result.ClearFlexibleTypeCapabilitiesId();
        result.FlexibleUpperBound = null;
        result.Nullable = true;
        return result;
    }
    private KType Any(bool nullable) => Named("kotlin.Any", nullable);
    private KType Array(KType element) {
        var t = Named("kotlin.Array");
        t.Argument.Add(new KType.Types.Argument { Projection = KType.Types.Argument.Types.Projection.Inv, Type = element });
        return t;
    }
    private static string FullName(string ns, string name) => string.IsNullOrEmpty(ns) ? name : ns + "." + name;
    private static string StripArity(string name) => name.Contains('`') ? name[..name.IndexOf('`')] : name;
}
