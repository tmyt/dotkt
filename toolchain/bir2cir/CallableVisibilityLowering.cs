using System.Text.Json.Nodes;
using System.Text.Json;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

// Capture the Kotlin declaration before any representation pass can replace it with a wider CLR entry point.
// Field visibility is explicit in BIR, including both source properties and private backing storage.
static class CallableVisibilityLowering
{
    internal const string Carrier = "DotKt.Runtime.CompilerServices.KotlinCallableVisibilityAttribute";
    const string PublishedApi = "kotlin.PublishedApi";

    public static BirFileDeclaration[] ApplyAll(IReadOnlyList<BirFileDeclaration> roots, ReferenceMetadataIndex refs)
    {
        // `overrides` is the frontend's complete declaration closure, including exact getter/setter identities.
        // A selected public CLR slot requires a public override even when its Kotlin declaration stays internal.
        // Snapshot the already-promoted slots so this decision is independent of source/file traversal order;
        // neither matching member names nor repeating the base declaration's annotation establishes this edge.
        var promoted = MapRoots(roots, method => method with {
            Attrs = Preserve(method.Vis, method.Attrs), Vis = Promote(method.Vis, method.Attrs),
        }, constructor => constructor with {
            Attrs = Preserve(constructor.Vis, constructor.Attrs), Vis = Promote(constructor.Vis, constructor.Attrs),
        }, field => field with { Attrs = Preserve(field.Vis, field.Attrs), Vis = Promote(field.Vis, field.Attrs) });
        var methods = promoted.SelectMany(root => new BirGraphValue[] { root }
                .Concat(root.Types.OrEmpty().Cast<BirGraphValue>())).SelectMany(owner => owner switch {
                    BirFileDeclaration value => value.Methods.OrEmpty(),
                    BirClassDefinition value => value.Methods.OrEmpty(),
                    BirInterfaceDefinition value => value.Methods.OrEmpty(),
                    BirEnumDefinition value => value.Methods.OrEmpty(),
                    _ => throw new InvalidOperationException("Unaccounted visibility owner: " + owner.GetType().Name),
                });
        var publicSlots = methods.Where(method => (method.Vis.OrDefault() ?? "public") == "public")
            .Select(method => method.DeclarationId.OrDefault())
            .Where(id => id != null).ToHashSet(StringComparer.Ordinal);
        return MapRoots(promoted, method => method.Overrides.OrEmpty().Any(edge =>
                edge.DeclarationId.OrDefault() is string id && (publicSlots.Contains(id) || refs.IsPublicVirtualDeclaration(id)))
            ? method with { Vis = "public" } : method, constructor => constructor, field => field);
    }

    static OptionalField<BirAttribute[]> Preserve(OptionalField<string> visibility, OptionalField<BirAttribute[]> attributes)
    {
        var source = visibility.OrDefault() ?? "public";
        if (source == "public") return attributes;
        var text = new TypeNode.Fqn("System.String");
        return attributes.OrEmpty().Append(new BirAttribute {
            Attr = new TypeNode.Fqn(Carrier),
            ArgTypes = new TypeNode[] { text, new TypeNode.Array(new TypeNode.Fqn("System.Byte")) },
            Args = new IBirAttributeValue[] {
                new BirConstant { Type = text, Value = new BirLiteral.TextLiteral(BirCarrier.JsonV1) },
                new BirOpaqueBytes { Bytes = JsonSerializer.SerializeToUtf8Bytes(new { visibility = source }) },
            },
        }).ToArray();
    }

    static OptionalField<string> Promote(OptionalField<string> visibility, OptionalField<BirAttribute[]> attributes) =>
        visibility.OrDefault() == "internal" && attributes.OrEmpty().Any(attribute =>
            attribute.Attr.OrDefault() is TypeNode.Fqn { Name: PublishedApi }) ? "public" : visibility;

    static BirFileDeclaration[] MapRoots(IReadOnlyList<BirFileDeclaration> roots,
        Func<BirFunctionDeclaration, BirFunctionDeclaration> method,
        Func<BirConstructor, BirConstructor> constructor, Func<BirFieldDeclaration, BirFieldDeclaration> field)
    {
        static OptionalField<T[]> Map<T>(OptionalField<T[]> values, Func<T, T> rewrite) =>
            values.HasValue ? values.Value.Select(rewrite).ToArray() : values;
        IBirTypeDeclaration Owner(IBirTypeDeclaration declaration) => declaration switch {
            BirClassDefinition owner => owner with { Methods = Map(owner.Methods, method), Ctors = Map(owner.Ctors, constructor), Fields = Map(owner.Fields, field) },
            BirInterfaceDefinition owner => owner with { Methods = Map(owner.Methods, method), Ctors = Map(owner.Ctors, constructor), Fields = Map(owner.Fields, field) },
            BirEnumDefinition owner => owner with { Methods = Map(owner.Methods, method), Ctors = Map(owner.Ctors, constructor), Fields = Map(owner.Fields, field) },
            _ => throw new InvalidOperationException("Unaccounted visibility owner: " + declaration.GetType().Name),
        };
        return roots.Select(root => root with {
            Methods = Map(root.Methods, method), Fields = Map(root.Fields, field), Types = Map(root.Types, Owner),
        }).ToArray();
    }

    public static void Preserve(JsonObject declaration)
    {
        var visibility = declaration["vis"]?.GetValue<string>() ?? "public";
        if (visibility != "public") RoundtripMetadata.PreserveCallableVisibility(declaration, visibility);
    }

    static void Promote(JsonObject declaration)
    {
        if (declaration["vis"]?.GetValue<string>() == "internal" &&
            declaration["attrs"] is JsonArray attrs && attrs.OfType<JsonObject>().Any(attribute =>
                TypeJson.OwnerName(attribute["attr"]) == PublishedApi))
            declaration["vis"] = "public";
    }

    // A representation-created source entry point must retain declaration visibility independently of its physical
    // access. In particular, a suspend Task bridge is public even when the Kotlin declaration is not.
    public static void Copy(JsonObject source, JsonObject target, string visibility = null)
    {
        if (source["attrs"] is JsonArray attributes)
            foreach (var attribute in attributes.OfType<JsonObject>())
            {
                var name = TypeJson.OwnerName(attribute["attr"]);
                if (name != PublishedApi && (name != Carrier || visibility != null)) continue;
                if (target["attrs"] is not JsonArray) target["attrs"] = new JsonArray();
                ((JsonArray)target["attrs"]).Add(attribute.DeepClone());
            }
        if (visibility != null && visibility != "public")
            RoundtripMetadata.PreserveCallableVisibility(target, visibility);
        Promote(target);
    }

    internal static void SelfTest()
    {
        var published = new BirAttribute { Attr = new TypeNode.Fqn(PublishedApi) };
        var source = new[] {
            new BirFileDeclaration { FileClass = "Base", Methods = new[] {
                new BirFunctionDeclaration { Name = "helper", DeclarationId = "published", Vis = "internal", Attrs = new[] { published } },
                new BirFunctionDeclaration { Name = "ordinary", DeclarationId = "ordinary" },
            }, Fields = new[] {
                new BirFieldDeclaration { Name = "privateField", Vis = "private" },
                new BirFieldDeclaration { Name = "publicField", Vis = "public", Attrs = OptionalField<BirAttribute[]>.Null },
            } },
            new BirFileDeclaration { FileClass = "Derived", Types = new IBirTypeDeclaration[] {
                new BirInterfaceDefinition { Name = "Contract", Methods = new[] {
                    new BirFunctionDeclaration { Name = "member", DeclarationId = "middle", Vis = "internal",
                        Overrides = new[] { new BirOverride { DeclarationId = "published" } } },
                } },
                new BirClassDefinition { Name = "Implementation", Methods = new[] {
                    new BirFunctionDeclaration { Name = "member", DeclarationId = "leaf", Vis = "internal",
                        Overrides = new[] { new BirOverride { DeclarationId = "middle" }, new BirOverride { DeclarationId = "published" } } },
                    new BirFunctionDeclaration { Name = "helper", DeclarationId = "same-name", Vis = "internal" },
                }, Ctors = new[] { new BirConstructor { Vis = "internal", Attrs = new[] { published } } } },
            } },
        };
        var before = source.Select(root => root.ToDom()).ToArray();
        var refs = ReferenceMetadataIndex.Build(Array.Empty<string>());
        var result = ApplyAll(source, refs);
        var reordered = ApplyAll(source.Reverse().ToArray(), refs).Reverse().ToArray();
        for (var index = 0; index < source.Length; index++)
            if (!JsonNode.DeepEquals(before[index], source[index].ToDom())
                || !JsonNode.DeepEquals(result[index].ToDom(), reordered[index].ToDom()))
                throw new InvalidOperationException("Visibility promotion depends on traversal order or mutates source inputs");
        var helper = result[0].Methods.Value[0];
        var contract = (BirInterfaceDefinition)result[1].Types.Value[0];
        var implementation = (BirClassDefinition)result[1].Types.Value[1];
        if (helper.Vis.OrDefault() != "public" || contract.Methods.Value[0].Vis.OrDefault() != "public"
            || implementation.Methods.Value[0].Vis.OrDefault() != "public"
            || implementation.Methods.Value[1].Vis.OrDefault() != "internal"
            || implementation.Ctors.Value[0].Vis.OrDefault() != "public"
            || result[0].Methods.Value[1].Attrs.IsPresent || !result[0].Fields.Value[1].Attrs.IsNull)
            throw new InvalidOperationException("Visibility promotion lost selected slots, source access or absent/null attributes");
        var old = new JsonObject();
        RoundtripMetadata.PreserveCallableVisibility(old, "internal");
        var payload = ((BirOpaqueBytes)helper.Attrs.Value.Single(attribute =>
            attribute.Attr.OrDefault() is TypeNode.Fqn { Name: Carrier }).Args.Value[1]).Bytes.Value;
        if (!payload.SequenceEqual(Convert.FromBase64String(old["attrs"][0]["args"][1]["bytes"].GetValue<string>()))
            || helper.Attrs.Value.Length != 2 || result[0].Fields.Value[0].Attrs.Value.Length != 1)
            throw new InvalidOperationException("Typed visibility metadata lost original access or its exact payload");
        Console.WriteLine("[callable visibility] self-test OK (typed source access, promoted slots, fields/ctors, traversal independence)");
    }
}
