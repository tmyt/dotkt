using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

sealed partial class TypedModuleInventory
{
    internal static void SelfTest()
    {
        static TypeNode.Fqn Fqn(string name) => new(name);
        static BirFunctionDeclaration Generic(string id, params BirParameter[] parameters) => new() {
            Name = "sameName", DeclarationId = id,
            TypeParams = new[] { new BirTypeParameter { Name = "T" } }, Params = parameters,
        };
        static void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Typed module inventory: " + message);
        }

        var arguments = new TypeNode[] { new TypeNode.Tv("method", 0) };
        var path = new[] { "Outer" };
        var sourceParameter = new BirParameter { Name = "value", Type = new TypeNode.Fqn("Box", arguments) {
            Classifier = new TypeNode.SourceClassifier("demo", path, "Box"),
        } };
        var generic = Generic("file-generic", sourceParameter);
        var nested = new BirClassDefinition {
            Name = "Nested", Kind = "class", NestedIn = "Container", SemanticOwner = "Container",
            Base = new TypeNode.Fqn("Base", new TypeNode[] { Fqn("Argument") }),
            Interfaces = new TypeNode[] { Fqn("First"), new TypeNode.Nullable(Fqn("Wrapped")), Fqn("First") },
            Methods = new[] { Generic("nested-generic", new BirParameter()), new BirFunctionDeclaration { Name = "member-only" } },
        };
        var files = new[] {
            new BirFileDeclaration {
                FileClass = "demo.File", Methods = new[] {
                    generic, new BirFunctionDeclaration { Name = "top" }, new BirFunctionDeclaration { Name = "" },
                    new BirFunctionDeclaration { Name = "nongeneric", DeclarationId = "not-generic" },
                    new BirFunctionDeclaration { Name = "anonymous-generic", TypeParams = new[] { new BirTypeParameter { Name = "T" } } },
                    new BirFunctionDeclaration(),
                },
                Types = new IBirTypeDeclaration[] {
                    nested,
                    new BirInterfaceDefinition { Name = "Contract", Kind = "interface", Methods = new[] { Generic("interface-generic") } },
                    new BirEnumDefinition { Name = "Enum", Kind = "enum", Methods = new[] { Generic("enum-generic") } },
                    new BirClassDefinition { Name = "Replaced", Base = Fqn("OldBase") },
                    new BirClassDefinition { Name = "", Interfaces = new TypeNode[] { Fqn("EmptyNameSuper") } },
                },
                RefTypes = new[] { new BirRefTypeDeclaration { Name = "ReferencedOnly" } },
            },
            new BirFileDeclaration { FileClass = "demo.Other", Methods = new[] { new BirFunctionDeclaration { Name = "top" } },
                Types = new IBirTypeDeclaration[] { new BirClassDefinition { Name = "Replaced" } } },
            new BirFileDeclaration { FileClass = "" },
        };
        var before = files.Select(file => file.DeepClone()).ToArray();
        var inventory = new TypedModuleInventory(files);
        Assert(inventory.LocalTopLevelFunctions.SetEquals(new[] { "sameName", "top", "", "nongeneric", "anonymous-generic" }),
            "file-only names or empty-name presence changed");
        Assert(inventory.EmittedTypeFqns.SetEquals(new[] { "demo.File", "demo.Other", "Nested", "Contract", "Enum", "Replaced", "" }),
            "file facade, flattened nested declaration or referenced-only ownership changed");
        Assert(inventory.DirectSuperNames["Nested"].SequenceEqual(new[] { "Base", "First", "First" })
            && inventory.DirectSuperNames["Replaced"].Count == 0
            && inventory.DirectSuperNames[""].SequenceEqual(new[] { "EmptyNameSuper" })
            && !inventory.DirectSuperNames.ContainsKey("demo.File"), "direct FQN edges, order or last declaration changed");
        Assert(!inventory.HasUserCharSeqImplementation, "unrelated interfaces retained CharSequence");
        Assert(inventory.CalleeTypeParameters.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(new[] {
            "file-generic", "nested-generic", "interface-generic", "enum-generic",
        }), "selected declaration identity/generic eligibility changed");
        Assert(inventory.CalleeTypeParameters["nested-generic"] is [null]
            && inventory.CalleeTypeParameters["interface-generic"].Length == 0,
            "absent parameter type or empty parameter vector changed");
        for (var index = 0; index < files.Length; index++)
            Assert(files[index].StructurallyEquals(before[index]), "collection mutated an owning graph");
        Assert(ReferenceEquals(files[0].Methods.Value[0], generic) && ReferenceEquals(files[0].Types.Value[0], nested),
            "collection replaced actual declaration occurrences");

        // The inventory owns its parameter-type snapshots, not source vectors or
        // nested classifier arrays that a later declaration rewrite can change.
        var captured = (TypeNode.Fqn)inventory.CalleeTypeParameters["file-generic"][0];
        arguments[0] = Fqn("ChangedArgument");
        path[0] = "ChangedOwner";
        sourceParameter.Type = Fqn("ChangedSlot");
        generic.Params = Array.Empty<BirParameter>();
        Assert(captured.Args[0] == new TypeNode.Tv("method", 0) && captured.Classifier.OwnerPath[0] == "Outer",
            "parameter snapshots retained source-owned arrays");

        // CharSequence retention depends only on direct interface identity, not
        // a declaration's name/kind, inherited base, or an argument containing it.
        TypedModuleInventory CharSeq(params IBirTypeDeclaration[] types) =>
            new(new[] { new BirFileDeclaration { Types = types } });
        Assert(CharSeq(new BirClassDefinition { Interfaces = new TypeNode[] { Fqn(SharedSyntheticSynthesis.CharSeq) } })
            .HasUserCharSeqImplementation, "unnamed implementer was filtered");
        Assert(!CharSeq(new BirInterfaceDefinition { Name = SharedSyntheticSynthesis.CharSeq },
            new BirClassDefinition { Base = Fqn(SharedSyntheticSynthesis.CharSeq), Interfaces = new TypeNode[] {
                new TypeNode.Fqn("Other", new TypeNode[] { Fqn(SharedSyntheticSynthesis.CharSeq) }),
                new TypeNode.Nullable(Fqn(SharedSyntheticSynthesis.CharSeq)), Fqn("kotlin.CharSequence"),
            } }).HasUserCharSeqImplementation, "CharSequence detection inferred an indirect interface");

        // Absent, null and empty arrays all contribute no entries, and collecting
        // them must not fill or otherwise normalize their presence on the root.
        var sparse = new[] { new BirFileDeclaration(), new BirFileDeclaration {
            Methods = OptionalField<BirFunctionDeclaration[]>.Null, Types = OptionalField<IBirTypeDeclaration[]>.Null,
        }, new BirFileDeclaration { Methods = Array.Empty<BirFunctionDeclaration>(), Types = Array.Empty<IBirTypeDeclaration>() } };
        var sparseBefore = sparse.Select(file => file.DeepClone()).ToArray();
        var empty = new TypedModuleInventory(sparse);
        Assert(empty.LocalTopLevelFunctions.Count == 0 && empty.EmittedTypeFqns.Count == 0
            && empty.DirectSuperNames.Count == 0 && empty.CalleeTypeParameters.Count == 0, "sparse roots invented entries");
        for (var index = 0; index < sparse.Length; index++)
            Assert(sparse[index].StructurallyEquals(sparseBefore[index]), "array presence changed");

        var duplicate = Generic("duplicate", new BirParameter { Type = Fqn("Physical") });
        var repeated = Generic("duplicate", new BirParameter { Type = new TypeNode.Fqn("Physical") {
            Classifier = new TypeNode.SourceClassifier("demo", Array.Empty<string>(), "Physical"),
        } });
        var duplicates = new TypedModuleInventory(new[] { new BirFileDeclaration { Methods = new[] { duplicate, repeated } } });
        Assert(((TypeNode.Fqn)duplicates.CalleeTypeParameters["duplicate"][0]).Classifier != null,
            "equal physical signatures lost the last declaration's complete type data");
        repeated.Params = new[] { new BirParameter { Type = Fqn("Different") } };
        bool conflict = false;
        try { _ = new TypedModuleInventory(new[] { new BirFileDeclaration { Methods = new[] { duplicate, repeated } } }); }
        catch (InvalidOperationException error) when (error.Message == "Collection realignment has conflicting selected declaration signatures")
        { conflict = true; }
        Assert(conflict, "conflicting selected declaration signatures were accepted");
        Console.WriteLine("[typed module inventory] self-test OK (current owners, facade precedence, direct supers, CharSequence, exact callee IDs, isolated snapshots)");
    }
}
