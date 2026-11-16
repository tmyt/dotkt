using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class NullableTvErasureCallRealign
{
    internal static void SelfTestInheritedSource()
    {
        static void Need(bool value, string message)
        { if (!value) throw new InvalidOperationException("Nullable inherited source: " + message); }
        static OptionalField<IBirExpression> E(IBirExpression value) => OptionalField<IBirExpression>.Of(value);
        var refs = ReferenceMetadataIndex.Build(new[] { typeof(object).Assembly.Location });
        var text = new TypeNode.Fqn("kotlin.String");
        var erased = new TypeNode.Fqn("object");
        var ownerVariable = new TypeNode.Tv("type", 0);
        var sourceResult = new TypeNode.Array(new TypeNode.Nullable(ownerVariable));
        var physicalResult = new TypeNode.Array(erased);
        var semanticResult = new TypeNode.Array(new TypeNode.Nullable(text));
        var declaration = new BirFunctionDeclaration { Name = "array", DeclarationId = "inherited-array",
            Params = Array.Empty<BirParameter>(), Ret = sourceResult, Abstract = true, Virtual = true };
        var parent = new BirClassDefinition { Name = "ArrayBase", Kind = "class", Abstract = true,
            TypeParams = new[] { new BirTypeParameter { Name = "T" } }, Methods = new[] { declaration } };
        var derived = new BirClassDefinition { Name = "ArrayDerived", Kind = "class", Abstract = true,
            Base = new TypeNode.Fqn("ArrayBase", new[] { text }), Methods = Array.Empty<BirFunctionDeclaration>() };
        var receiver = new BirLocal { Name = "receiver", Sty = new TypeNode.Fqn("ArrayDerived") };
        var call = new BirCallInstance { OwnerType = new TypeNode.Fqn("ArrayDerived"), Method = "array",
            DeclarationId = "inherited-array", Recv = E(receiver), Args = Array.Empty<IBirExpression>(),
            Sig = Array.Empty<TypeNode>(), Ret = semanticResult, Sty = semanticResult };
        var variable = new BirVariable { Name = "values", Type = semanticResult, Init = E(call) };
        var method = new BirFunctionDeclaration { Name = "use", Params = new[] {
            new BirParameter { Name = "receiver", Type = new TypeNode.Fqn("ArrayDerived") } },
            Ret = new TypeNode.Fqn("kotlin.Unit"), Body = new IBirStatement[] { variable } };
        var file = new BirFileDeclaration { FileClass = "ArrayUses", Methods = new[] { method },
            Types = new IBirTypeDeclaration[] { parent, derived } };
        var index = CollectDeclaredMemberRets(new[] { file }, refs);
        var row = index.ByDeclarationOwner["ArrayBase"]["inherited-array"];
        var declarations = index.ByDeclarationOwner;
        var rows = index.ByOwner;
        var originalOrder = index.SourceApplicationOrder;
        // The canonical capture precedes declaration erasure. Continue that
        // SAME row after live declarations acquire their physical return.
        declaration.NullableGenericRet = sourceResult;
        declaration.Ret = physicalResult;
        ProjectDeclarationApplications(index);
        var frozenRowReturn = BirGraphValue.CopyType(row.Ret);
        var originalDeclaration = declaration.DeepClone();
        InheritedMemberOwnerBinding.ApplyAll(new[] { file }, refs);
        Need(call.OwnerType.Value == new TypeNode.Fqn("ArrayBase", new[] { text })
            && ReferenceEquals(call.Recv.Value, receiver), "actual inherited binding did not select the constructed base");
        // Earlier source flow may have left this ambient reader installed.
        // The new capability must explicitly enter the canonical physical mode.
        _sourceApplicationOrder = originalOrder;
        ApplyAfterInheritedOwnerBinding(file, index, _ => false, refs);
        Need(variable.Init.Value is BirCast projected && projected.Type.Value == semanticResult
            && ReferenceEquals(projected.E.Value, call) && call.Ret.Value == physicalResult && call.Sty.Value == physicalResult,
            "physical object[] result and checked string[] consumer view were not represented explicitly");
        Need(ReferenceEquals(call.Recv.Value, receiver) && ReferenceEquals(file.Methods.Value[0], method)
            && ReferenceEquals(parent.Methods.Value[0], declaration) && declaration.StructurallyEquals(originalDeclaration)
            && ReferenceEquals(index.ByDeclarationOwner, declarations) && ReferenceEquals(index.ByOwner, rows)
            && ReferenceEquals(index.ByDeclarationOwner["ArrayBase"]["inherited-array"], row)
            && row.Ret == frozenRowReturn && index.SourceApplicationOrder == null && _sourceApplicationOrder == null,
            "reflow recaptured declarations, changed a source record/index row, or restored source argument order");
        var once = file.DeepClone();
        ApplyAfterInheritedOwnerBinding(file, index, _ => false, refs);
        Need(file.StructurallyEquals(once) && variable.Init.Value is BirCast second && ReferenceEquals(second.E.Value, call),
            "inherited reflow repeated a completed conversion");

        var integer = new TypeNode.Fqn("kotlin.Int");
        bool IsValue(TypeNode.Fqn type) => type.Name == integer.Name;
        var nested = new BirFunctionDeclaration { Name = "nested", DeclarationId = "nested-coercion",
            Params = Array.Empty<BirParameter>(), Ret = integer, Body = new IBirStatement[] {
                new BirReturn { Value = E(new BirConstant { Type = integer, Value = new BirLiteral.IntegerLiteral(1) }) } } };
        var actual = new BirValueBlock { Type = erased, Stmts = new IBirStatement[] {
            new BirLocalFunction { Id = "nested-coercion", Decl = nested } },
            Result = E(new BirLocal { Name = "value", Sty = erased }) };
        var narrow = CastForErasedObjectSlot(actual, erased, integer, IsValue);
        Need(narrow is BirCast cast && cast.Type.Value == integer && ReferenceEquals(cast.E.Value, actual)
            && ReferenceEquals(((BirLocalFunction)actual.Stmts.Value[0]).Decl.Value, nested),
            "object narrowing cloned an actual operand or registered descendant");
        var value = new BirConstant { Type = integer, Value = new BirLiteral.IntegerLiteral(2) };
        Need(CastForErasedObjectSlot(value, integer, erased, IsValue) is BirCast boxed
            && boxed.Type.Value == erased && ReferenceEquals(boxed.E.Value, value), "value widening did not move its operand");
        Need(CastForErasedObjectSlot(actual, text, erased, IsValue) == null
            && CastForErasedObjectSlot(actual, erased, new TypeNode.ByRef(integer), IsValue) == null
            && CastForErasedObjectSlot(actual, new TypeNode.ByRef(integer), erased, IsValue) == null
            && CastForErasedObjectSlot(narrow, erased, integer, IsValue) == null
            && CastForErasedObjectSlot(actual, integer, integer, IsValue) == null,
            "reference/byref/equal slots acquired a conversion or a completed cast was wrapped twice");
        Console.WriteLine("[nullable inherited source] self-test OK (canonical physical index, exact inherited array projection, stable declarations, MOVE coercions and idempotence)");
    }
}
