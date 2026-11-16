using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class MapVarianceRealign
{
    internal static void SelfTest()
    {
        TypeNode M(int index) => new TypeNode.Tv("method", index);
        var text = new TypeNode.Fqn("kotlin.String");
        var any = new TypeNode.Nullable(new TypeNode.Fqn("kotlin.Any"));
        var frames = new Dictionary<string, NullableRepresentationFrame>(StringComparer.Ordinal) {
            ["kotlin.collections.Map"] = new(2, Array.Empty<int>(), storageIndices: new[] { 0, 1 }),
            ["kotlin.collections.MutableMap"] = new(2, new[] { 1 }, storageIndices: new[] { 0, 1 }),
        };
        BirFunctionDeclaration Declaration(string id, TypeNode parameter) => new() {
            Name = "sameName", DeclarationId = id,
            TypeParams = Enumerable.Range(0, 4).Select(index => new BirTypeParameter { Name = "T" + index }).ToArray(),
            Params = new[] { new BirParameter { Name = "map", Type = parameter } },
        };
        var selected = new TypeNode.Fqn("kotlin.collections.Map", new[] { M(0), M(1), M(2), M(3) });
        var declarations = CollectCalleeTypeParams(new[] { new BirFileDeclaration { Methods = new[] {
            Declaration("unrelated", new TypeNode.Fqn("kotlin.collections.Map", new[] { M(3), M(2), M(1), M(0) })),
            Declaration("selected", selected),
        } } });
        BirCallStatic Call() => new() { Method = "sameName", DeclarationId = "selected",
            TypeArgs = new[] { any, M(0), any, M(2) }, Args = new IBirExpression[] { new BirLocal { Name = "map" } } };
        var env = new Dictionary<string, TypeNode>(StringComparer.Ordinal) {
            ["map"] = new TypeNode.Fqn("kotlin.collections.MutableMap", new[] { text, M(0), M(1), text, M(2) }),
        };
        void AssertCall(BirCallStatic call)
        {
            Realign(call.TypeArgs.Value, call.DeclarationId.Value, call.Args.Value, env, declarations, frames);
            if (!call.TypeArgs.Value.SequenceEqual(new[] { text, M(0), text, M(2) }))
                throw new InvalidOperationException("Collection realignment confused source or companion roles");
        }
        AssertCall(Call());
        frames["kotlin.collections.MutableMap"] = new(2, new[] { 1 }, new[] { 4, 0, 2, 3, 1 }, new[] { 0, 1 });
        env["map"] = new TypeNode.Fqn("kotlin.collections.MutableMap", new[] { M(2), text, M(1), text, M(0) });
        AssertCall(Call());
        declarations["selected"] = new[] { new TypeNode.Fqn("kotlin.collections.Map",
            new[] { new TypeNode.Tv("type", 0), M(1), M(2), M(3) }) };
        var ownerVariable = Call();
        Realign(ownerVariable.TypeArgs.Value, ownerVariable.DeclarationId.Value, ownerVariable.Args.Value, env, declarations, frames);
        if (ownerVariable.TypeArgs.Value[0] != any)
            throw new InvalidOperationException("Collection realignment rebound an owner variable as a method variable");

        var bound = new TypeNode.Fqn("kotlin.collections.MutableMap", new TypeNode[] { text, text });
        var temporary = new BirVariable { Name = "temporary", Type = new TypeNode.Fqn("kotlin.collections.MutableMap", new TypeNode[] { any, any }),
            Init = new BirLocal { Name = "source" } };
        var instance = new BirCallInstance { OwnerType = temporary.Type, Recv = new BirLocal { Name = "temporary" }, Method = "get" };
        var beforeDeclaration = new BirCallStatic { DeclarationId = "method", TypeArgs = new TypeNode[] { any },
            Args = new IBirExpression[] { new BirLocal { Name = "later", Sty = new TypeNode.Fqn("Ignored") } } };
        var later = new BirVariable { Name = "later", Type = bound };
        var cycleA = new BirVariable { Name = "cycleA", Type = any, Init = new BirLocal { Name = "cycleB" } };
        var cycleB = new BirVariable { Name = "cycleB", Type = any, Init = new BirLocal { Name = "cycleA" } };
        var body = new IBirStatement[] { new BirExpressionStatement { Expr = beforeDeclaration }, temporary,
            new BirExpressionStatement { Expr = instance }, later, cycleA, cycleB };
        var method = new BirFunctionDeclaration { Name = "probe", Body = body,
            Params = new[] { new BirParameter { Name = "source", Type = M(0) } },
            TypeParams = new[] { new BirTypeParameter { Name = "T", Constraints = new TypeNode[] { bound } } } };
        var source = new BirFileDeclaration { Methods = new[] { method } };
        var selectedParameters = new Dictionary<string, TypeNode[]> {
            ["method"] = new[] { new TypeNode.Fqn("kotlin.collections.Map", new[] { M(0), M(0) }) },
        };
        Apply(source, selectedParameters, null);
        if (!ReferenceEquals(method.Body.Value, body) || !ReferenceEquals(source.Methods.Value[0], method)
            || !TypeNodeDataEquality.Equal(temporary.Type.Value, bound)
            || !TypeNodeDataEquality.Equal(instance.OwnerType.Value, bound) || beforeDeclaration.TypeArgs.Value[0] != text
            || cycleA.Type.Value != any || cycleB.Type.Value != any)
            throw new InvalidOperationException("Owning map realignment lost precollection, alias roots or declaration identity");
        var opaque = new BirInlinePayload { Body = new IBirStatement[] { new BirVariable { Name = "hidden", Type = any } } };
        var visited = false;
        Walk(opaque, _ => visited = true);
        if (visited || ActualArgType(new BirLocal { Sty = bound }, env) != null
            || ActualArgType(new BirCallStatic { Ret = bound, Sty = any }, env) != bound)
            throw new InvalidOperationException("Map realignment inferred an argument from sty or opened an opaque carrier");
        var own = new BirPropertyGet { Type = text, Ret = bound, Sty = any };
        if (own.OwnTypeOrReturnStamp != text) throw new InvalidOperationException("Own type/return projection did not prefer type");
        own.Type = OptionalField<TypeNode>.Null;
        if (own.OwnTypeOrReturnStamp != bound || !own.Type.IsNull)
            throw new InvalidOperationException("Own type/return projection changed explicit null or its return fallback");
        own.Type = default; own.Ret = OptionalField<TypeNode>.Null;
        if (own.OwnTypeOrReturnStamp != null || own.Type.IsPresent || !own.Ret.IsNull)
            throw new InvalidOperationException("Own type/return projection used sty or changed missing field presence");
        var referenceFrame = new NullableRepresentationFrame(1, Array.Empty<int>());
        var currentFrame = new NullableRepresentationFrame(1, new[] { 0 });
        var referenced = new Dictionary<string, NullableRepresentationFrame> { ["Current"] = referenceFrame, ["Absent"] = referenceFrame };
        var current = new BirClassDefinition { Name = "Current", KotlinSupertypesPre = new SourceTypeFacts(
            new Dictionary<int, TypeNode[]>()).MergeNullableFrame(currentFrame) };
        var absent = new BirClassDefinition { Name = "Absent" };
        var currentFile = new BirFileDeclaration { Types = new IBirTypeDeclaration[] { current, absent } };
        var overlaid = CollectTypeFrames(currentFile, referenced);
        if (!ReferenceEquals(overlaid["Current"], currentFrame) || overlaid.ContainsKey("Absent")
            || !ReferenceEquals(referenced["Absent"], referenceFrame)
            || !ReferenceEquals(CollectTypeFrames(new BirFileDeclaration(), referenced)["Current"], referenceFrame))
            throw new InvalidOperationException("Map frame overlay lost explicit local shadowing or leaked into another file");
        Console.WriteLine("[typed map variance] self-test OK (exact identity, permuted roles, method scope, whole-body clock, aliases and ownership)");
    }
}
