using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class GenericDowncastRealignment
{
    internal static void SourceSelfTest()
    {
        static void Need(bool condition, string message)
        { if (!condition) throw new InvalidOperationException("Source downcast realignment: " + message); }
        static OptionalField<IBirExpression> E(IBirExpression value) => OptionalField<IBirExpression>.Of(value);
        var text = new TypeNode.Fqn("kotlin.String"); var any = new TypeNode.Fqn("object");
        var variable = new TypeNode.Tv("type", 0);
        TypeNode.Fqn Face(TypeNode argument) => new("RealignFace", new[] { argument });
        TypeNode.Fqn Derived(TypeNode argument) => new("RealignDerived", new[] { argument });
        var definition = new BirClassDefinition { Name = "RealignDerived", Kind = "class",
            TypeParams = new[] { new BirTypeParameter { Name = "T" } }, Interfaces = new TypeNode[] { Face(variable) } };
        var operand = new BirNativeInstance { Type = Face(text), Ret = Face(any), Sty = Face(new TypeNode.Fqn("kotlin.Int")) };
        var cast = new BirCast { Type = Derived(text), E = E(operand) };
        var local = new BirVariable { Name = "item", Type = Derived(text), Init = E(cast) };
        var receiver = new BirLocal { Name = "item", Sty = Derived(text) };
        var call = new BirCallInstance { OwnerType = Derived(text), Method = "read", Recv = E(receiver) };
        var shadow = new BirCallInstance { OwnerType = Derived(text), Method = "read", Recv = E(new BirLocal { Name = "item" }) };
        var method = new BirFunctionDeclaration { Name = "use", Params = Array.Empty<BirParameter>(),
            Body = new IBirStatement[] { local, new BirExpressionStatement { Expr = E(call) } }, InlineBir = new byte[] { 0x80 } };
        var sibling = new BirFunctionDeclaration { Name = "sibling", Params = new[] {
            new BirParameter { Name = "item", Type = Derived(text) } }, Body = new IBirStatement[] {
                new BirExpressionStatement { Expr = E(shadow) } } };
        var root = new BirFileDeclaration { Types = new IBirTypeDeclaration[] { definition }, Methods = new[] { method, sibling } };
        var snapshot = Collect(new[] { root });
        var capturedEdge = snapshot["RealignDerived"].Interfaces[0];
        // The caller passes this SAME earlier hierarchy, not a later recapture.
        definition.Interfaces = new TypeNode[] { Face(text) };
        Apply(root, snapshot);
        Need(cast.Type.Value == Derived(any) && local.Type.Value == Derived(any) && call.OwnerType.Value == Derived(any)
            && shadow.OwnerType.Value == Derived(text) && snapshot["RealignDerived"].Interfaces[0] == capturedEdge,
            "ret/type priority, sequential local continuation, method scope or frozen hierarchy changed");
        Need(ReferenceEquals(cast.E.Value, operand) && ReferenceEquals(call.Recv.Value, receiver)
            && ReferenceEquals(root.Methods.Value[0], method) && method.InlineBir.Value.SequenceEqual(new byte[] { 0x80 }),
            "owning operands/declarations or opaque payload changed");
        var incomplete = new Def { Arity = 2, Interfaces = new[] { Face(variable) } };
        Need(Realign(new TypeNode.Fqn("Incomplete", new TypeNode[] { text, text }), Face(any),
                new Dictionary<string, Def> { ["Incomplete"] = incomplete }) == null,
            "an incomplete generic equation invented an argument");
        var once = root.DeepClone(); Apply(root, snapshot);
        Need(root.StructurallyEquals(once), "completed downcast alignment was applied twice");
        Console.WriteLine("[source downcast realignment] self-test OK (same hierarchy, ret-before-type, sequential scopes, exact operands and incomplete equations)");
    }
}
