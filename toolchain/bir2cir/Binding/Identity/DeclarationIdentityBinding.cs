using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

// #395 — frontend-selected Kotlin callable identity -> one physical CLR MethodDef.
//
// Kotlin can distinguish declarations whose receiver/parameter types collapse to the same CLR type. The selected
// IrFunction's stable pre-CLR fingerprint rides BIR as `declarationId`; this pass runs only after
// BirTypeLowering has exposed the real CLI collision set. It allocates a stable physical name once and rewrites
// declarations and every local use by that same authoritative key. No signature/name resolver is allowed to
// rediscover the Kotlin declaration after erasure.
static partial class DeclarationIdentityBinding
{
    internal const string Key = "declarationId";
    internal const string ExplicitNameKey = "explicitClrName";
    internal const string SemanticSignatureKey = "declarationSemanticSignature";
    internal const string ReferencedIntrinsicKey = "declarationReferencedIntrinsic";
    internal const string ReferencedFactoryKey = "declarationReferencedFactory";
    internal const string PhysicalOnlySuffix = "|clr-physical-only";
    internal const string ExistentialSlotMarker = PhysicalOnlySuffix + "|existential-slot:";
    internal const string IndependentAliasSlotKey = "independentAliasSlot";

    internal static string PhysicalOnlyId(string declarationId, string role) =>
        declarationId + PhysicalOnlySuffix + "|" + role;

    internal static string InheritedProjectionId(string owner, IEnumerable<string> sourceIds)
    {
        var identities = sourceIds.Distinct(StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal).ToArray();
        if (identities.Length == 0)
            throw new InvalidOperationException("Inherited physical projection has no selected declaration identity");
        return PhysicalOnlyId(string.Join("|", identities), "inherited-slot:" + owner);
    }

    // A metadata self-build cannot read its own future reference DLL. The source
    // index merges trusted local @ClrTypeAlias facts into the referenced aliases,
    // so reference/runtime collision projection uses the same physical names.
    internal static void AddCollisionAlias(Dictionary<string, string> result, string ownerName, string alias)
    {
        if (string.IsNullOrEmpty(alias))
            throw new InvalidOperationException($"bir2cir: malformed @ClrTypeAlias on '{ownerName}'");
        if (result.TryGetValue(ownerName, out var prior) && prior != alias)
            throw new InvalidOperationException($"bir2cir: conflicting @ClrTypeAlias for '{ownerName}': '{prior}' and '{alias}'");
        result[ownerName] = alias;
    }


    // Phase-3 representation passes may synthesize additional MethodDefs after source declaration allocation. Check
    // the actual final emission unit as a whole so a generated bridge can neither duplicate another generated bridge
    // nor silently collide with an explicitly named user declaration. This is validation only: no late renaming is
    // allowed because every call/MethodImpl/property association has already been physically bound.
    internal static void ValidateFinalPhysicalNames(IEnumerable<BirFileDeclaration> roots)
    {
        var rootList = roots.ToArray();
        var declarations = new List<(string Owner, string Name, string Sig,
            bool GeneratedOwner, bool GeneratedMethod)>();
        var properties = new List<(string Owner, string Name, string Sig)>();
        void Collect(string owner, bool generated, BirConstructor[] constructors,
            BirFunctionDeclaration[] methods, BirPropertyDeclaration[] rows)
        {
            foreach (var constructor in constructors)
                declarations.Add((owner, ".ctor", PhysicalSignature(0,
                    constructor.Params.OrEmpty().Select(parameter => parameter.Type.OrDefault()), null), generated, false));
            foreach (var method in methods)
                if (method.Name.HasValue)
                    declarations.Add((owner, method.Name.Value, PhysicalSignature(method.TypeParams.OrEmpty().Length,
                        method.Params.OrEmpty().Select(parameter => parameter.Type.OrDefault()), method.Ret.OrDefault()),
                        generated, method.Generated.OrDefault()));
            foreach (var property in rows)
                if (property.Name.HasValue)
                {
                    var indexes = property.GetSig.HasValue ? property.GetSig.Value
                        : property.SetSig.HasValue ? property.SetSig.Value.Take(Math.Max(0, property.SetSig.Value.Length - 1))
                        : throw new InvalidOperationException("bir2cir: final PropertyDef has no accessor signature");
                    properties.Add((owner, property.Name.Value, PhysicalPropertySignature(property.Type.OrDefault(), indexes)));
                }
        }
        foreach (var root in rootList)
            Collect(root.FileClass.OrDefault() ?? "<file>", false, [], root.Methods.OrEmpty(), root.Properties.OrEmpty());
        var definedOwners = rootList.Where(root => root.Methods.OrEmpty().Length != 0 || root.Fields.OrEmpty().Length != 0)
            .Select(root => root.FileClass.OrDefault() ?? "<file>").ToHashSet(StringComparer.Ordinal);
        foreach (var root in rootList)
            foreach (var declaration in root.Types.OrEmpty())
            {
                var shape = declaration switch {
                    BirClassDefinition type => (type.Name, type.Generated, type.Ctors, type.Methods, type.Properties),
                    BirInterfaceDefinition type => (type.Name, type.Generated, type.Ctors, type.Methods, type.Properties),
                    BirEnumDefinition type => (type.Name, type.Generated, type.Ctors, type.Methods, type.Properties),
                    _ => throw new InvalidOperationException("Unaccounted final physical declaration " + declaration.GetType().Name),
                };
                var owner = shape.Name.OrDefault() ?? "<anonymous>";
                if (definedOwners.Add(owner))
                    Collect(owner, shape.Generated.OrDefault(), shape.Ctors.OrEmpty(), shape.Methods.OrEmpty(), shape.Properties.OrEmpty());
            }
        CheckFinalPhysicalNames(declarations, properties);
    }

    static void CheckFinalPhysicalNames(
        List<(string Owner, string Name, string Sig, bool GeneratedOwner, bool GeneratedMethod)> declarations,
        List<(string Owner, string Name, string Sig)> properties)
    {
        var collisions = declarations
            .GroupBy(declaration => (declaration.Owner, declaration.Name, declaration.Sig))
            .Select(group => new
            {
                group.Key,
                Candidates = group.ToArray(),
            })
            .Where(group => group.Candidates.Length > 1)
            .Select(group =>
            {
                var parts = group.Key.Sig.Split('|', 2);
                var arity = parts[0];
                var parameters = parts.Length == 2 ? parts[1] : "";
                var kinds = string.Join(", ", group.Candidates.Select(declaration =>
                    declaration.GeneratedOwner || declaration.GeneratedMethod
                        ? "compiler-generated declaration"
                        : "user declaration"));
                return $"CLR physical signature collision at '{group.Key.Owner}.{group.Key.Name}' " +
                       $"(generic arity {arity}; parameters [{parameters}]): {kinds}";
            })
            .ToArray();
        if (collisions.Length != 0)
            throw new InvalidOperationException(
                "bir2cir: final CIR contains duplicate MethodDef signatures; late collision repair is forbidden:" +
                Environment.NewLine + string.Join(Environment.NewLine, collisions));

        // Equal file facades are separate CIR roots until ilemit merges them. Validate the Property table with the
        // same physical-owner model as methods so a duplicate row cannot hide merely by living in a sibling root.
        var propertyCollisions = properties
            .GroupBy(property => (property.Owner, property.Name, property.Sig))
            .Where(group => group.Count() > 1)
            .Select(group =>
                $"CLR physical PropertyDef collision at '{group.Key.Owner}.{group.Key.Name}' " +
                $"(signature [{group.Key.Sig}])")
            .ToArray();
        if (propertyCollisions.Length != 0)
            throw new InvalidOperationException(
                "bir2cir: final CIR contains duplicate PropertyDef signatures; late collision repair is forbidden:" +
                Environment.NewLine + string.Join(Environment.NewLine, propertyCollisions));
    }


    public static void SelfTest()
    {
        var inherited = new BirFunctionDeclaration {
            Name = "inherited", Virtual = true, Generated = true, DeclarationSourceName = "inherited",
            DeclarationId = InheritedProjectionId("InheritedOwner", new[] { "selected-base", "selected-base" }),
            Params = Array.Empty<BirParameter>(), Ret = new TypeNode.Fqn("void"),
            TypeParams = new[] { new BirTypeParameter { Name = "T" }, new BirTypeParameter { Name = "S" } },
            MethodTypeParameterBoundsPre = new SourceBoundsSnapshot(new Dictionary<int, TypeNode[]> { [0] = Array.Empty<TypeNode>() }),
            NullableRepresentationFrame = new NullableRepresentationFrame(1, Array.Empty<int>(), storageIndices: new[] { 0 }),
        };
        var inheritedRoot = new BirFileDeclaration { FileClass = "InheritedOwner", Methods = new[] { inherited } };
        var inheritedNames = AllocatePhysicalNames(new[] { inheritedRoot }, out var inheritedCarriers);
        var noticed = 0;
        ApplyLocal(new[] { inheritedRoot }, inheritedNames, inheritedCarriers,
            new SourceDeclarationSignatures(), new Dictionary<string, int[]>(), null, (file, owner, method, id) => {
                if (!ReferenceEquals(file, inheritedRoot) || owner != null || !ReferenceEquals(method, inherited)
                    || inherited.DeclarationId.Value != id)
                    throw new InvalidOperationException("Facade identity consumption lost its actual source occurrence");
                noticed++;
            });
        if (noticed != 1 || inherited.DeclarationId.IsPresent || inherited.NullableRepresentationFrame.IsPresent
            || inherited.MethodTypeParameterBoundsPre.IsPresent || !inherited.Generated.Value || inherited.Name.Value != "inherited")
            throw new InvalidOperationException("Inherited physical frame published an authored Kotlin declaration");
        LocalAllocationSourceSelfTest();
        SelfTestFinalPhysicalNames();
        SelfTestSourceBinding();
    }

    static void SelfTestFinalPhysicalNames()
    {
        var integer = new TypeNode.Fqn("System.Int32");
        var method = new BirFunctionDeclaration { Name = "probe", Ret = integer,
            Params = new[] { new BirParameter { Name = "value", Type = integer } } };
        var property = new BirPropertyDeclaration { Name = "Item", Type = integer, GetSig = new TypeNode[] { integer } };
        string Failure(Action action)
        {
            try { action(); return null; }
            catch (InvalidOperationException error) { return error.Message; }
        }
        void Check(BirFileDeclaration[] roots, string expected = null)
        {
            var typed = Failure(() => ValidateFinalPhysicalNames(roots));
            if (expected == null ? typed != null : typed?.Contains(expected) != true)
                throw new InvalidOperationException("Owning physical declaration validation changed a final signature table");
        }
        Check(new[] { new BirFileDeclaration { FileClass = "Facade", Methods = new[] { method },
            Properties = new[] { property } } });
        Check(new[] { new BirFileDeclaration { FileClass = "Facade", Methods = new[] { method } },
            new BirFileDeclaration { FileClass = "Facade", Methods = new[] { method } } }, "duplicate MethodDef");
        Check(new[] { new BirFileDeclaration { FileClass = "Facade", Properties = new[] { property } },
            new BirFileDeclaration { FileClass = "Facade", Properties = new[] { property } } }, "duplicate PropertyDef");
        Check(new[] { new BirFileDeclaration { Types = new IBirTypeDeclaration[] {
            new BirClassDefinition { Name = "Generated", Generated = true, Methods = new[] { method } },
            new BirClassDefinition { Name = "Generated", Generated = true, Methods = new[] { method } },
        } } });
        var first = method with { Params = new[] { new BirParameter { Name = "value", Type = new TypeNode.Tv("method", 0) } },
            TypeParams = new[] { new BirTypeParameter { Name = "T" }, new BirTypeParameter { Name = "S" } } };
        var second = first with { Params = new[] { new BirParameter { Name = "value", Type = new TypeNode.Tv("method", 1) } } };
        Check(new[] { new BirFileDeclaration { FileClass = "Facade", Methods = new[] { first, second } } });
        Console.WriteLine("[typed final declarations] self-test OK (facades, generated owners, exact generics, properties)");
    }


    internal static string PhysicalSignature(int arity, IEnumerable<TypeNode> parameters, TypeNode result) =>
        arity + "|" + string.Join(";", parameters.Select(type => PhysicalTypeSignature(type
            ?? throw new InvalidOperationException("bir2cir: declaration identity has an untyped physical parameter"))))
        + " -> " + (result == null ? "System.Void" : PhysicalTypeSignature(result));

    static string PhysicalPropertySignature(TypeNode type, IEnumerable<TypeNode> indexes) =>
        PhysicalPropertyTypeSignature(type ?? throw new InvalidOperationException("bir2cir: final PropertyDef has no physical type"))
        + "|" + string.Join(";", indexes.Select(parameter =>
            PhysicalPropertyTypeSignature(parameter
                ?? throw new InvalidOperationException(
                    "bir2cir: final PropertyDef has an untyped physical index parameter"))));

    // A declaration is allocated only after BirTypeLowering, so this key describes the actual CLI parameter types,
    // not their remaining JSON spellings. In particular, suspend-function values erase to the shorthand `object`
    // while an Any parameter normally arrives as `System.Object`; raw JSON comparison would miss that collision and
    // leave two MethodDefs with one CLR signature. Keep this nominal projection aligned with ilemit's MapType rules.
    internal static string PhysicalTypeSignature(TypeNode type) => type switch
    {
        TypeNode.Mod m => (m.Req ? "modreq:" : "modopt:") + PhysicalTypeSignature(m.M) + ":" + PhysicalTypeSignature(m.Of),
        TypeNode.Fqn { Name: "System.Nullable" or "System.Nullable`1", Args: [var value] } =>
            "System.Nullable[" + PhysicalTypeSignature(value) + "]",
        TypeNode.Fqn { Args: null } f => PhysicalTypeName(f.Name),
        TypeNode.Fqn f => PhysicalTypeName(f.Name) + "[" +
            string.Join(",", f.Args!.Select(PhysicalTypeSignature)) + "]",
        // Generic-parameter scope and index are part of an open CLR MethodDef signature (`!0` and `!1`, or `!0` and
        // `!!0`, are distinct). Constructing an owner with equal arguments does not merge its MethodDef rows.
        TypeNode.Tv tv => "gp:" + tv.Scope + ":" + tv.I,
        TypeNode.Fn fn => PhysicalDelegateSignature(fn),
        TypeNode.Nullable n => "System.Nullable[" + PhysicalTypeSignature(n.Of) + "]",
        TypeNode.Oblivious o => PhysicalTypeSignature(o.Of),
        TypeNode.Array a => "array:" + a.Rank + ":" + a.SzArray + ":" + PhysicalTypeSignature(a.Elem),
        TypeNode.ByRef b => "byref:" + PhysicalTypeSignature(b.Of),
        TypeNode.Ptr pointer => "ptr:" + PhysicalTypeSignature(pointer.Of),
        TypeNode.FnPtr pointer => "fnPtr:" + pointer.CallingConvention + ":" + pointer.GenericArity
            + ":" + pointer.RequiredParams + "[" + string.Join(";", pointer.Params.Select(PhysicalTypeSignature))
            + "]->" + PhysicalTypeSignature(pointer.Ret),
        TypeNode.Star => throw new InvalidOperationException(
            "bir2cir: unresolved star projection reached final CLR MethodDef signature allocation"),
        _ => throw new InvalidOperationException(
            $"bir2cir: unsupported type node '{type.GetType().Name}' reached final CLR MethodDef signature allocation"),
    };

    // A PropertyDef signature retains the declaring type-parameter scope and index (`!0` vs `!1`). Unlike a
    // call-side MethodDef key, it must not collapse those slots merely because a closed owner could substitute the
    // same concrete type for both. The associated accessor MethodDefs already have independently allocated names,
    // and MethodSemantics binds the Property row to that exact declaration.
    internal static string PhysicalPropertyTypeSignature(TypeNode type) => PhysicalTypeSignature(type);

    static string PhysicalDelegateSignature(TypeNode.Fn fn)
    {
        var args = fn.DelegateParams.Select(PhysicalTypeSignature).ToList();
        var returnsVoid = PhysicalTypeSignature(fn.Ret) == "System.Void";
        var family = fn.Clr ?? throw new InvalidOperationException(
            "bir2cir: declaration identity physical signature contains an unresolved function type");
        if (!returnsVoid) args.Add(PhysicalTypeSignature(fn.Ret));
        return family switch
        {
            "System.Action" when returnsVoid && args.Count == 0 => "System.Action",
            "System.Action" when returnsVoid => "System.Action[" + string.Join(",", args) + "]",
            "System.Func" when !returnsVoid => "System.Func[" + string.Join(",", args) + "]",
            "DotKt.Runtime.CompilerServices.KAction" when returnsVoid =>
                "DotKt.Runtime.CompilerServices.KAction[" + string.Join(",", args) + "]",
            "DotKt.Runtime.CompilerServices.KFunc" when !returnsVoid =>
                "DotKt.Runtime.CompilerServices.KFunc[" + string.Join(",", args) + "]",
            _ => throw new InvalidOperationException(
                $"bir2cir: declaration identity physical signature has invalid delegate family '{family}'"),
        };
    }

    static string PhysicalTypeName(string name) => name switch
    {
        "void" => "System.Void",
        "int" or "kotlin.Int" => "System.Int32",
        "long" or "kotlin.Long" => "System.Int64",
        "short" or "kotlin.Short" => "System.Int16",
        "sbyte" or "kotlin.Byte" => "System.SByte",
        "double" or "kotlin.Double" => "System.Double",
        "float" or "kotlin.Float" => "System.Single",
        "bool" or "kotlin.Boolean" => "System.Boolean",
        "char" or "kotlin.Char" => "System.Char",
        "string" or "kotlin.String" => "System.String",
        "object" or "kotlin.Any" or "kotlin.Nothing" => "System.Object",
        "uint" or "kotlin.UInt" => "System.UInt32",
        "ulong" or "kotlin.ULong" => "System.UInt64",
        "byte" or "kotlin.UByte" => "System.Byte",
        "ushort" or "kotlin.UShort" => "System.UInt16",
        _ => name,
    };

    internal static string StableSuffix(string id)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(id));
        return Convert.ToHexString(digest.AsSpan(0, 8)).ToLowerInvariant();
    }

    static string AllocationIdentity(string id)
    {
        var marker = id.IndexOf(PhysicalOnlySuffix, StringComparison.Ordinal);
        return marker < 0 ? id : id[..marker];
    }


    static void ValidateExplicitName(string name, string owner, string sourceName)
    {
        if (name.Length == 0)
            throw new InvalidOperationException(
                $"bir2cir: malformed @ClrName on '{owner}.{sourceName}': the CLR method name must not be empty");
        if (name.IndexOf('\0') >= 0)
            throw new InvalidOperationException(
                $"bir2cir: malformed @ClrName on '{owner}.{sourceName}': the CLR method name must not contain NUL");
        if (name is ".ctor" or ".cctor")
            throw new InvalidOperationException(
                $"bir2cir: malformed @ClrName on '{owner}.{sourceName}': '{name}' is a reserved CLR constructor name");
        try
        {
            _ = new UTF8Encoding(false, true).GetByteCount(name);
        }
        catch (EncoderFallbackException)
        {
            throw new InvalidOperationException(
                $"bir2cir: malformed @ClrName on '{owner}.{sourceName}': the CLR method name is not valid Unicode");
        }
    }
}
