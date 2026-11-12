using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

// Immutable module facts at the source/physical boundary. Only declaration-owned
// methods and type attributes participate; expression IDs are uses, not definitions.
sealed class SourceModuleIndex
{
    internal IReadOnlySet<string> DeclarationIds { get; }
    internal IReadOnlyDictionary<string, string> CollisionAliases { get; }
    internal IReadOnlySet<string> ValueTypes { get; }

    internal SourceModuleIndex(IReadOnlyList<BirFileDeclaration> roots, IReadOnlyDictionary<string, string> referencedAliases)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var aliases = new Dictionary<string, string>(referencedAliases, StringComparer.Ordinal);
        var values = new HashSet<string>(StringComparer.Ordinal);
        void Methods(IEnumerable<BirFunctionDeclaration> methods)
        {
            foreach (var method in methods)
                if (method.DeclarationId.HasValue) ids.Add(method.DeclarationId.Value);
        }
        foreach (var root in roots)
        {
            Methods(root.Methods.OrEmpty());
            foreach (var declaration in root.Types.OrEmpty())
            {
                var (name, kind, methods, attributes) = declaration switch {
                    BirClassDefinition type => (type.Name, type.Kind, type.Methods, type.Attrs),
                    BirInterfaceDefinition type => (type.Name, type.Kind, type.Methods, type.Attrs),
                    BirEnumDefinition type => (type.Name, type.Kind, type.Methods, type.Attrs),
                    _ => throw new InvalidOperationException("Unaccounted source module owner: " + declaration.GetType().Name),
                };
                Methods(methods.OrEmpty());
                if (!name.HasValue) continue;
                if (kind.OrDefault() is "enum" or "struct") values.Add(name.Value);
                foreach (var attribute in attributes.OrEmpty())
                {
                    if (attribute.Attr.OrDefault() is not TypeNode.Fqn { Name: "kotlin.clr.ClrTypeAlias" }) continue;
                    var alias = attribute.Args.OrDefault() is { Length: 1 } arguments
                        && arguments[0] is BirConstant { Value.HasValue: true } constant
                        && constant.Value.Value is BirLiteral.TextLiteral text ? text.Value : null;
                    DeclarationIdentityBinding.AddCollisionAlias(aliases, name.Value, alias);
                }
            }
        }
        DeclarationIds = ids;
        CollisionAliases = aliases;
        ValueTypes = values;
    }

    internal static void SelfTest()
    {
        BirAttribute Alias(string target) => new() { Attr = new TypeNode.Fqn("kotlin.clr.ClrTypeAlias"),
            Args = new IBirAttributeValue[] { new BirConstant { Value = new BirLiteral.TextLiteral(target) } } };
        BirFunctionDeclaration Method(string id) => new() { DeclarationId = id, Body = new IBirStatement[] {
            new BirReturn { Value = new BirCallStatic { DeclarationId = "external-use-only" } },
        } };
        var roots = new[] { new BirFileDeclaration { FileClass = "File", Methods = new[] { Method("file-method") },
            Types = new IBirTypeDeclaration[] {
                new BirClassDefinition { Name = "Alias", Kind = "class", Attrs = new[] { Alias("System.Object") },
                    Methods = new[] { Method("class-method"), Method("class-method") },
                    Ctors = new[] { new BirConstructor { DeclarationId = "constructor-not-method" } } },
                new BirInterfaceDefinition { Name = "Contract", Kind = "interface", Methods = new[] { Method("interface-method") } },
                new BirEnumDefinition { Name = "NativeEnum", Kind = "enum", Methods = new[] { Method("enum-method") } },
                new BirClassDefinition { Name = "Value", Kind = "struct", NestedIn = "Alias" },
                new BirClassDefinition { Name = "RichEnum", Kind = "class", EnumRich = true },
            } }, new BirFileDeclaration { FileClass = "Other", Methods = new[] { Method("other-method") } } };
        var references = new Dictionary<string, string>(StringComparer.Ordinal) { ["External"] = "System.String", ["Alias"] = "System.Object" };
        var index = new SourceModuleIndex(roots, references);
        var dom = roots.Select(root => (System.Text.Json.Nodes.JsonNode)root.ToDom()).ToArray();
        var expectedAliases = DeclarationIdentityBinding.CollisionAliases(dom, references);
        if (!index.DeclarationIds.SetEquals(new[] { "file-method", "class-method", "interface-method", "enum-method", "other-method" })
            || !index.DeclarationIds.SetEquals(DeclarationIdentityBinding.CollectDeclarationIds(dom))
            || !index.ValueTypes.SetEquals(new[] { "NativeEnum", "Value" })
            || index.CollisionAliases.Count != expectedAliases.Count
            || expectedAliases.Any(pair => index.CollisionAliases.GetValueOrDefault(pair.Key) != pair.Value))
            throw new InvalidOperationException("Typed module index changed declaration ownership, aliases or physical value kinds");
        references["External"] = "changed";
        if (index.CollisionAliases["External"] != "System.String")
            throw new InvalidOperationException("Source module index retained mutable reference aliases");
        Console.WriteLine("[source module index] self-test OK (owner methods, exact IDs, alias merge, value kinds, nested classifier placement, isolation)");
    }
}
