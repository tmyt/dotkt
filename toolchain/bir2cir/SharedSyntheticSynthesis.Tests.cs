using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class SharedSyntheticSynthesis
{
    internal static void SelfTest()
    {
        var refs = ReferenceMetadataIndex.Build(new[] { typeof(object).Assembly.Location });
        var integer = new TypeNode.Fqn("kotlin.Int");
        var obj = new TypeNode.Fqn("object");
        var frame = new NullableRepresentationFrame(1, new[] { 0 });
        var facts = SourceTypeFacts.Empty.MergeNullableFrame(frame);
        var registry = new BirRefTypeDeclaration {
            Name = "ErasedCell", Elem = obj, _nativeLocation = true, KotlinSupertypesPre = facts,
            TypeParams = new[] {
                new BirTypeParameter { Name = "SourceT", Constraints = new TypeNode[] {
                    new TypeNode.Fqn("Bound", new TypeNode[] { new TypeNode.Tv("type", 0), new TypeNode.Tv("type", 1) }),
                } },
                new BirTypeParameter { Name = "NullableT" },
            },
        };
        var hint = new BirRefTypeDeclaration {
            Name = "HintOnlyCell", Elem = new TypeNode.Fqn("System.Span`1", new TypeNode[] { new TypeNode.Fqn("System.Int32") }),
            TypeParams = Array.Empty<BirTypeParameter>(),
        };
        var construction = new BirNew {
            Type = new TypeNode.Fqn("ErasedCell", new TypeNode[] { integer, new TypeNode.Nullable(integer) }),
            Args = new IBirExpression[] { new BirConstant { Type = obj, Value = new BirLiteral.NullLiteral() } },
            ArgTypes = new TypeNode[] { obj },
        };
        var parameter = new BirParameter { Name = "hint", Type = integer, SharedCellType = new TypeNode.Fqn("HintOnlyCell") };
        var method = new BirFunctionDeclaration {
            Name = "use", Params = new[] { parameter }, _syntheticTypeArgs = OptionalField<TypeNode[]>.Null,
            Ret = new TypeNode.Fqn("kotlin.Unit"), Body = new IBirStatement[] {
                new BirExpressionStatement { Expr = construction },
                // The old reference trigger includes a literal exactly equal to
                // the identity, not only a structured type node's name.
                new BirExpressionStatement { Expr = new BirConstant {
                    Type = new TypeNode.Fqn("kotlin.String"), Value = new BirLiteral.TextLiteral(CharSeq),
                } },
            },
        };
        var original = new BirClassDefinition { Name = "Original", Kind = "class" };
        var file = new BirFileDeclaration {
            FileClass = "Cells", Types = new IBirTypeDeclaration[] { original }, Methods = new[] { method },
            RefTypes = new[] { registry, hint },
        };
        var storage = new List<CellStorage>();
        Apply(file, refs, storage);
        Require(ReferenceEquals(file.Methods.Value[0], method) && ReferenceEquals(file.Types.Value[0], original)
            && file.Types.Value.Length == 3 && !file.RefTypes.IsPresent,
            "source declarations were replaced/reordered or registry/hint ownership survived");
        var cell = (BirClassDefinition)file.Types.Value[1];
        Require(cell.Name.Value == "ErasedCell" && cell.TypeParams.Value.Length == 2
            && cell.TypeParams.Value.Select(value => value.Name.Value).SequenceEqual(new[] { "T0", "T1" })
            && cell.TypeParams.Value[0].Constraints.Value[0] is TypeNode.Fqn { Args: [TypeNode.Tv { Scope: "type", I: 0 }, TypeNode.Tv { Scope: "type", I: 1 }] }
            && !cell.TypeParams.Value[1].Constraints.IsPresent
            && ReferenceEquals(cell.KotlinSupertypesPre.Value, facts),
            "erased element lost its complete captured generic frame or source frame facts");
        Require(cell.Fields.Value[0]._nativeLocation.Value && cell.Fields.Value[0].Type.Value.Equals(obj)
            && cell.Ctors.Value[0].Params.Value[0].Type.Value.Equals(obj)
            && cell.Ctors.Value[0].BaseArgs.IsNull && cell.Ctors.Value[0].ThisArgs.IsNull
            && cell.Ctors.Value[0].Body.Value[0] is BirSetField { OwnerType.Value: TypeNode.Fqn {
                Args: [TypeNode.Tv { Scope: "type", I: 0 }, TypeNode.Tv { Scope: "type", I: 1 }],
            } }
            && construction.MemberSignature.Value is [TypeNode.Fqn { Name: "object" }]
            && construction.Type.Value is TypeNode.Fqn { Args: [TypeNode.Fqn { Name: "kotlin.Int" }, TypeNode.Nullable] }
            && storage.SequenceEqual(new[] { new CellStorage("ErasedCell", "v", 0) }),
            "native location, exact open constructor signature or storage handoff changed");
        Require(registry.TypeParams.Value[0].Name.Value == "SourceT" && !parameter.SharedCellType.IsPresent
            && !method._syntheticTypeArgs.IsPresent,
            "synthesis mutated registry descriptors or retained transient shared-frame facts");
        var charSequence = (BirInterfaceDefinition)file.Types.Value[2];
        Require(charSequence.Name.Value == CharSeq && charSequence.Generated.Value && charSequence.Base.IsNull
            && !charSequence.Interfaces.IsPresent && !charSequence.TypeParams.IsPresent
            && charSequence.KotlinType.Value is TypeNode.Fqn { Name: "kotlin.CharSequence" }
            && charSequence.Methods.Value.Select(value => value.Name.Value).SequenceEqual(new[] { "length", "get", "subSequence" })
            && charSequence.Methods.Value.All(value => value.Abstract.Value && value.Virtual.Value && value.Body.Value.Length == 0)
            && charSequence.Methods.Value[0].PropertyAssociation.Value == "charsequence-length"
            && charSequence.Properties.Value[0].KotlinAccessors.Value.SequenceEqual(new[] { "get" }),
            "CharSequence builder changed the fixed declaration/association or absent-field contract");

        var transient = new BirInlineCapture { SharedCellType = obj,
            SharedCellTypeParams = new[] { new BirTypeParameter { Name = "T" } } };
        DropSyntheticTypeArgs(transient);
        Require(!transient.SharedCellType.IsPresent && !transient.SharedCellTypeParams.IsPresent,
            "capture parameter hint was not consumed");
        var explicitNull = new BirFileDeclaration { Types = OptionalField<IBirTypeDeclaration[]>.Null,
            RefTypes = OptionalField<BirRefTypeDeclaration[]>.Null };
        Apply(explicitNull, refs);
        Require(explicitNull.Types.Value.Length == 0 && explicitNull.RefTypes.IsNull,
            "null non-registry fields changed their existing presence behavior");
        Console.WriteLine("[typed shared synthetics] self-test OK (append-only cells, complete frames, native storage, open constructors and CharSequence)");
    }

    static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException("Typed shared synthetics: " + message);
    }
}
