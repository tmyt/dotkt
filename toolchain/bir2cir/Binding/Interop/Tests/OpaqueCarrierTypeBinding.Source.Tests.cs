using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class OpaqueCarrierTypeBinding
{
    internal static void SourceSelfTest()
    {
        static void Reject(Action action)
        {
            try { action(); } catch (InvalidOperationException) { return; }
            throw new InvalidOperationException("Opaque source binding accepted conflicting/cyclic placement facts");
        }
        var refs = ReferenceMetadataIndex.Build(new[] { typeof(object).Assembly.Location });
        var text = new TypeNode.Fqn("kotlin.String");
        var semantic = new TypeNode.Fqn("generatedLeaf", new TypeNode[] { text, text });
        var expected = semantic with { Name = "Package.Outer`1+generatedLeaf`1" };
        var outer = new BirClassDefinition { Name = "Package.Outer", Kind = "class",
            TypeParams = new[] { new BirTypeParameter { Name = "O" } } };
        var leaf = new BirClassDefinition { Name = semantic.Name, Kind = "class", NestedIn = outer.Name,
            TypeParams = new[] { new BirTypeParameter { Name = "I" } },
            CapturedTypeParams = new[] { new BirTypeParameter { Name = "Captured" } } };
        var same = leaf with { };
        var sourceFacts = SourceTypeFacts.Empty.Merge(new Dictionary<int, TypeNode[]> { [0] = new[] { (TypeNode)semantic } });
        leaf.KotlinSupertypesPre = sourceFacts;
        var bounds = new SourceBoundsSnapshot(new Dictionary<int, TypeNode[]> { [0] = new[] { (TypeNode)semantic } });
        var methodImpl = new BirMethodImpl { Owner = semantic, MethodTypeParameterBoundsPre = bounds,
            TypeParams = new[] { new BirTypeParameter { Name = "M" } } };
        var method = new BirFunctionDeclaration { Name = "metadata", Ret = semantic,
            ClrInterfaceImpls = new[] { methodImpl }, NullableGenericRet = semantic,
            TypeParams = new[] { new BirTypeParameter { Name = "M" } },
            RetKotlinType = semantic, MethodTypeParameterBoundsPre = bounds,
            Params = new[] { new BirParameter { Name = "value", Type = semantic, NullableGeneric = semantic } } };
        var file = new BirFileDeclaration { FileClass = "Package.File", Types = new IBirTypeDeclaration[] { outer, leaf },
            Methods = new[] { method } };
        var duplicateFile = new BirFileDeclaration { Types = new IBirTypeDeclaration[] { same } };
        var signatures = new SourceDeclarationSignatures();
        method.DeclarationId = "metadata";
        file = signatures.Capture(new[] { file })[0];
        leaf = (BirClassDefinition)file.Types.Value[1];
        method = file.Methods.Value[0];
        var actualMethods = file.Methods.Value; var actualParameters = method.Params.Value;
        ApplyAll(new[] { file, duplicateFile }, refs, signatures);
        Require(ReferenceEquals(file.Methods.Value, actualMethods) && ReferenceEquals(actualMethods[0], method)
            && ReferenceEquals(method.Params.Value, actualParameters) && ReferenceEquals(method.ClrInterfaceImpls.Value[0], methodImpl)
            && TypeNodeDataEquality.Equal(method.NullableGenericRet.Value, expected)
            && TypeNodeDataEquality.Equal(method.Params.Value[0].NullableGeneric.Value, expected)
            && TypeNodeDataEquality.Equal(methodImpl.MethodTypeParameterBoundsPre.Value.Bounds[0][0], expected)
            && TypeNodeDataEquality.Equal(leaf.KotlinSupertypesPre.Value.Bounds[0][0], expected)
            && TypeNodeDataEquality.Equal(method.Ret.Value, semantic) && TypeNodeDataEquality.Equal(method.RetKotlinType.Value, semantic)
            && TypeNodeDataEquality.Equal(methodImpl.Owner.Value, semantic)
            && signatures.TryGetValue("metadata", out var selectedSignature)
            && TypeNodeDataEquality.Equal(selectedSignature.Parameters[0], expected),
            "own arity/explicit owner, exact records, selected metadata or preserved ordinary source facts changed");
        Require(TypeNodeDataEquality.Equal(sourceFacts.Bounds[0][0], semantic)
            && TypeNodeDataEquality.Equal(bounds.Bounds[0][0], semantic), "frozen metadata input was mutated");
        duplicateFile.Types = new IBirTypeDeclaration[] { same with { NestedIn = "Package.Other" } };
        Reject(() => ApplyAll(new[] { file, duplicateFile }, refs));
        duplicateFile.Types = new IBirTypeDeclaration[] { same with { TypeParams = Array.Empty<BirTypeParameter>() } };
        Reject(() => ApplyAll(new[] { file, duplicateFile }, refs));
        var cycle = new BirFileDeclaration { Types = new IBirTypeDeclaration[] {
            new BirClassDefinition { Name = "A", NestedIn = "B" }, new BirClassDefinition { Name = "B", NestedIn = "A" },
        } };
        Reject(() => ApplyAll(new[] { cycle }, refs));
        var facade = PlanPhysicalNames(new[] { new PhysicalDeclaration("Facade.Nested", "Facade", 1) },
            new HashSet<string> { "Facade" });
        Require(facade["Facade.Nested"] == "Facade+Nested`1", "file facade identity was treated as a nested generic declaration");
        var noNested = new BirFileDeclaration { Methods = new[] { new BirFunctionDeclaration {
            Attrs = new[] { DefaultBirCarrier.Create(1, new BirValueBlock {
                Stmts = new IBirStatement[] { new BirSourcePoint { Span = new BirSourceSpan { F = "default.kt", L = 1 } } },
                Result = new BirClassReference { Type = semantic },
            }) },
        } } };
        ApplyAll(new[] { noNested }, refs);
        Require(DefaultBirCarrier.Decode(noNested.Methods.Value[0].Attrs.Value[0]) is BirValueBlock {
            Stmts.Value: [BirSourcePoint { Span.IsPresent: false }], Result.Value: BirClassReference { Type.Value: var untouched }
        } && TypeNodeDataEquality.Equal(untouched, semantic), "empty placement map lost outgoing default span cleanup or rewrote an ordinary classifier");
        Console.WriteLine("[opaque source owner] self-test OK (current placement, duplicate/cycle rules, own arity, exact records, metadata and empty-map defaults)");
    }
}
