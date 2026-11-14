using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class ExistentialCaptureAlignment
{
    internal static void SourceSelfTest()
    {
        static void Require(bool value, string message)
        { if (!value) throw new InvalidOperationException("Owning capture alignment: " + message); }
        var refs = ReferenceMetadataIndex.Build(Array.Empty<string>());
        var unit = new TypeNode.Fqn("kotlin.Unit");
        var variable = new TypeNode.Tv("type", 0);
        var logical = new TypeNode.Fqn("Logical", new TypeNode[] { new TypeNode.Fqn("kotlin.String") });
        var carrier = new TypeNode.Fqn("LogicalView");
        var owners = new Dictionary<string, string>(StringComparer.Ordinal) { ["Logical"] = "LogicalView" };
        BirExpressionStatement Statement(IBirExpression expression) => new() { Expr = OptionalField<IBirExpression>.Of(expression) };
        BirFunctionDeclaration Method(string name, params IBirStatement[] body) => new() {
            Name = name, Params = Array.Empty<BirParameter>(), Ret = unit, Body = body,
        };
        BirClassDefinition Closure(string name, TypeNode type) => new() {
            Name = name, Kind = "class", Generated = true,
            Fields = new[] { new BirFieldDeclaration { Name = "value", Type = type } },
            Ctors = new[] { new BirConstructor { Params = new[] { new BirParameter { Name = "value", Type = type } }, Body = Array.Empty<IBirStatement>() } },
            Methods = Array.Empty<BirFunctionDeclaration>(),
        };
        BirNewClosure Construction(string name, IBirExpression value) => new() {
            ClosureType = new TypeNode.Fqn(name), Captures = new[] { value },
        };

        var first = Closure("First", logical);
        var second = Closure("Second", logical);
        var field = new BirFieldGet { Name = "value", OwnerType = new TypeNode.Fqn("First"), Recv = new BirThis(),
            Sty = logical, MemberType = logical };
        var secondUse = Construction("Second", field);
        first.Methods = new[] { Method("nested", Statement(secondUse)) };
        var delivered = new BirCast { Type = carrier, E = new BirConstant { Type = logical } };
        var firstUse = Construction("First", delivered);
        var source = new BirClassDefinition { Name = "Source", Kind = "class", Methods = new[] { Method("construct", Statement(firstUse)) } };
        // Second enters the queue first. First's storage change must requeue it.
        var file = new BirFileDeclaration { FileClass = "Captures", Types = new IBirTypeDeclaration[] { first, second, source } };
        var notices = new List<string>();
        var fields = first.Fields.Value;
        var parameters = first.Ctors.Value[0].Params.Value;
        ApplyAll(new[] { file }, owners, refs, recordStorage: (owner, _, _, _, _) => notices.Add(owner));
        Require(notices.SequenceEqual(new[] { "First", "Second" })
            && first.Fields.Value[0].Type.Value == carrier && second.Fields.Value[0].Type.Value == carrier
            && first.Ctors.Value[0].Params.Value[0].Type.Value == carrier
            && ReferenceEquals(fields, first.Fields.Value) && ReferenceEquals(parameters, first.Ctors.Value[0].Params.Value)
            && ReferenceEquals(firstUse.Captures.Value[0], delivered) && ReferenceEquals(secondUse.Captures.Value[0], field)
            && field.Sty.Value == carrier && field.MemberType.Value == carrier,
            "FIFO dependencies, exact continuing storage, field reads or construction operands changed");

        var ignored = Closure("Raw", variable);
        ignored.TypeParams = new[] { new BirTypeParameter { Name = "T" } };
        var raw = Closure("Raw", variable);
        raw.TypeParams = new[] { new BirTypeParameter { Name = "T" } };
        var frame = new NullableRepresentationFrame(1, Array.Empty<int>());
        raw.KotlinSupertypesPre = SourceTypeFacts.Empty.MergeNullableFrame(frame);
        raw.Fields.Value[0].KotlinType = OptionalField<TypeNode>.Null;
        var preserved = new TypeNode.Tv("type", 0);
        raw.Ctors.Value[0].Params.Value[0].KotlinType = preserved;
        var local = new BirLocal { Name = "physical", Sty = logical };
        var block = new BirValueBlock { Type = logical, Sty = logical, Stmts = new IBirStatement[] {
            new BirVariable { Name = "physical", Type = new TypeNode.Fqn("object"), Init = new BirConstant { Type = new TypeNode.Fqn("object") } },
        }, Result = local };
        var branch = new BirConditional { Type = logical,
            Cond = new BirConstant { Type = new TypeNode.Fqn("kotlin.Boolean"), Value = new BirLiteral.BooleanLiteral(true) },
            Then = block, Else = new BirThrowExpression() };
        var rawUse = Construction("Raw", branch);
        var bindingsFile = new BirFileDeclaration { FileClass = "RawCaptures", Types = new IBirTypeDeclaration[] { ignored },
            Methods = new[] { Method("use", Statement(rawUse)) } };
        var selectedFile = new BirFileDeclaration { Types = new IBirTypeDeclaration[] { raw } };
        var index = new NullableTvErasureCallRealign.DeclIndex();
        index.Slots["Raw"] = new(StringComparer.Ordinal) { ["value"] = variable };
        index.Ctors["Raw"] = new() { [1] = new TypeNode[] { variable } };
        ApplyAll(new[] { bindingsFile, selectedFile }, owners, refs, recordStorage: (owner, name, position, count, storage) =>
            NullableTvErasureCallRealign.RebindGeneratedCapture(index, owner, name, position, count, storage));
        Require(ignored.Fields.Value[0].Type.Value == variable && raw.Fields.Value[0].Type.Value == new TypeNode.Fqn("object")
            && raw.Fields.Value[0].KotlinType.Value == variable && ReferenceEquals(raw.Ctors.Value[0].Params.Value[0].KotlinType.Value, preserved)
            && ReferenceEquals(raw.KotlinSupertypesPre.Value.NullableFrame, frame)
            && rawUse.Captures.Value[0] is BirCast cast && ReferenceEquals(cast.E.Value, branch)
            && index.Slots["Raw"]["value"] == new TypeNode.Fqn("object")
            && index.Ctors["Raw"][1][0] == new TypeNode.Fqn("object"),
            "last-writer selection, stale wrapper types, first-writer source facts or shared capture index were lost");

        var explicitValue = new BirCast { Type = carrier, E = new BirConstant { Type = logical } };
        var explicitCapture = new BirCaptureDescriptor { Name = "item", Type = logical };
        var implicitCapture = new BirCaptureDescriptor { Name = "raw", Type = variable, KotlinType = OptionalField<TypeNode>.Null };
        var nested = new BirNewSuspendLambda { Captures = new[] { new BirCaptureDescriptor { Name = "item", Type = logical } },
            CapValues = OptionalField<IBirExpression[]>.Null, Params = Array.Empty<BirLambdaParameter>(), Body = Array.Empty<IBirStatement>() };
        var suspend = new BirNewSuspendLambda { Captures = new[] { explicitCapture, implicitCapture },
            CapValues = new IBirExpression[] { explicitValue, null },
            Params = new[] { new BirLambdaParameter { Name = "item", Type = carrier } },
            TypeParams = new[] { new BirTypeParameter { Name = "T" } }, Body = new IBirStatement[] { Statement(nested) } };
        var suspendFile = new BirFileDeclaration { Methods = new[] { new BirFunctionDeclaration { Name = "suspendOwner", Ret = unit,
            Params = new[] { new BirParameter { Name = "raw", Type = new TypeNode.Fqn("object") } },
            Body = new IBirStatement[] { Statement(suspend) } } } };
        ApplyAll(new[] { suspendFile }, owners, refs);
        Require(explicitCapture.Type.Value == carrier && implicitCapture.Type.Value == new TypeNode.Fqn("object")
            && implicitCapture.KotlinType.Value == variable && ReferenceEquals(suspend.CapValues.Value[0], explicitValue)
            && suspend.CapValues.Value[1] == null && nested.CapValues.IsNull
            && nested.Captures.Value[0].Type.Value == carrier,
            "explicit/implicit suspend capture values, null presence or independent nested lexical frame changed");
        Console.WriteLine("[owning capture alignment] self-test OK (FIFO, current membership, shared index, physical wrappers, suspend frames and identity)");
    }
}
