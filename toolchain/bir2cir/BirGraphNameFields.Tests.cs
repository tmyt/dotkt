using System.Text;
using DotKt.Bir;
using DotKt.Bir.Lowering;

static class BirGraphNameFieldsTests
{
    internal static void SelfTest()
    {
        var nested = new TypeNode.Fqn("ParameterType") {
            Classifier = new TypeNode.SourceClassifier("ignoredPackage", new[] { "ignoredOwner" }, "SourceClassifier"),
        };
        var signature = new TypeNode.Fn(false, new TypeNode.Fqn("ReturnType"), new TypeNode[] {
            new TypeNode.ByRef(new TypeNode.Array(new TypeNode.Ptr(new TypeNode.Oblivious(
                new TypeNode.Nullable(new TypeNode.Projection("out", nested)))))),
            new TypeNode.Mod(true, new TypeNode.Fqn("ModifierType"),
                new TypeNode.FnPtr(0, 0, 1, new TypeNode.Fqn("PointerReturn"), new TypeNode[] { new TypeNode.Fqn("PointerParameter") })),
        }, Recv: new TypeNode.Fqn("ReceiverType"), Ctx: new TypeNode[] { new TypeNode.Fqn("ContextType") });
        var parameter = new BirParameter {
            Name = "parameter", Type = signature, KotlinType = new TypeNode.Fqn("FrozenParameterType"),
        };
        var root = new BirFunctionDeclaration {
            Name = "method", Params = new[] { parameter },
            Ret = new TypeNode.Tv("method", 0),
            RetSuspendFnTypePre = new TypeNode.Fqn("FrozenReturnType"),
            Body = new IBirStatement[] { new BirTry {
                Catches = new[] { new BirCatch { Var = "caught", ExcType = new TypeNode.Fqn("ExceptionType") } },
            } },
            Attrs = new[] { new BirAttribute { Attr = new TypeNode.Fqn("AttributeType"), Args = new IBirAttributeValue[] {
                new BirOpaqueBytes { Bytes = Encoding.UTF8.GetBytes("{\"name\":\"OpaqueName\"}") },
                new BirAttributeLiteral { Value = new BirLiteral.TextLiteral("{\"var\":\"LiteralName\"}") },
            } } },
        };
        var before = root.DeepClone();
        var names = new List<string>();
        root.Visit(node => node.VisitNameFields(names.Add));
        var expected = new[] { "method", "parameter", "ReturnType", "ParameterType", "SourceClassifier",
            "ModifierType", "PointerReturn", "PointerParameter", "ReceiverType", "ContextType", "caught", "ExceptionType", "AttributeType" };
        if (names.Count != expected.Length || !names.Order(StringComparer.Ordinal).SequenceEqual(expected.Order(StringComparer.Ordinal)))
            throw new InvalidOperationException("Structural name visitor lost a structured type/name/var field or entered a frozen string");
        if (!root.StructurallyEquals(before) || !ReferenceEquals(parameter, root.Params.Value[0]))
            throw new InvalidOperationException("Structural name visitor changed graph ownership or facts");

        var absent = new BirLocal();
        absent.VisitNameFields(_ => throw new InvalidOperationException("Absent name was visited"));
        var explicitNull = new BirLocal { Name = DotKt.Cir.OptionalField<string>.Null };
        explicitNull.VisitNameFields(_ => throw new InvalidOperationException("Null name was visited"));
        Console.WriteLine("[structural name fields] self-test OK (graph names, structured types/classifiers, frozen/opaque exclusions and identity)");
    }
}
