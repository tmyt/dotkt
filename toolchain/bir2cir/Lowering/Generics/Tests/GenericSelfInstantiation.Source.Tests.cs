using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class GenericSelfInstantiation
{
    internal static void SourceSelfTest()
    {
        void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Owning generic self: " + message);
        }
        TypeNode Bare() => new TypeNode.Fqn("Outer.Inner");
        BirExpressionStatement Expr(IBirExpression value) => new() { Expr = OptionalField<IBirExpression>.Of(value) };
        var receiver = new BirThis();
        var call = new BirCallInstance { OwnerType = Bare(), Recv = receiver, Args = Array.Empty<IBirExpression>() };
        var field = new BirFieldGet { OwnerType = Bare(), Recv = new BirThis(), Name = "value" };
        var storeValue = new BirLocal { Name = "input" };
        var store = new BirSetField { OwnerType = Bare(), Recv = new BirThis(), Name = "value", Value = storeValue };
        var constructed = new TypeNode.Fqn("Outer.Inner", new TypeNode[] { new TypeNode.Tv("type", 0), new TypeNode.Tv("type", 1) });
        var already = new BirCallInstance { OwnerType = constructed, Recv = new BirThis() };
        var empty = new TypeNode.Fqn("Outer.Inner", Array.Empty<TypeNode>());
        var emptyCall = new BirCallInstance { OwnerType = empty, Recv = new BirThis() };
        var foreign = new BirCallInstance { OwnerType = new TypeNode.Fqn("Other"), Recv = new BirThis() };
        var staticRead = new BirStaticFieldGet { OwnerType = Bare(), Name = "staticValue" };
        var exprStore = new BirFieldSetExpression { OwnerType = Bare(), Recv = new BirThis(), Value = new BirLocal { Name = "input" } };
        var late = new BirLateinitGet { OwnerType = Bare(), Recv = new BirThis() };
        var method = new BirFunctionDeclaration { Name = "use", Ret = new TypeNode.Fqn("kotlin.Unit"),
            Params = new[] { new BirParameter { Name = "input", Type = new TypeNode.Tv("type", 1) } },
            Body = new IBirStatement[] { Expr(call), Expr(field), store, Expr(already), Expr(emptyCall),
                Expr(foreign), Expr(staticRead), Expr(exprStore), Expr(late) } };
        var ctorStore = new BirSetField { OwnerType = Bare(), Recv = new BirThis(), Value = new BirLocal { Name = "initial" } };
        var owner = new BirClassDefinition { Name = "Outer.Inner", Kind = "class", NestedIn = "Outer",
            CapturedTypeParams = new[] { new BirTypeParameter { Name = "OuterT" } },
            TypeParams = new[] { new BirTypeParameter { Name = "InnerU" } }, Base = Bare(), Interfaces = new[] { Bare() },
            Methods = new[] { method }, Ctors = new[] { new BirConstructor {
                Params = new[] { new BirParameter { Name = "initial", Type = new TypeNode.Tv("type", 1) } },
                Body = new IBirStatement[] { ctorStore },
            } } };
        var ordinaryCall = new BirCallInstance { OwnerType = new TypeNode.Fqn("Plain"), Recv = new BirThis() };
        var plain = new BirClassDefinition { Name = "Plain", Kind = "class", Methods = new[] {
            new BirFunctionDeclaration { Name = "use", Body = new IBirStatement[] { Expr(ordinaryCall) } },
        } };
        var file = new BirFileDeclaration { Types = new IBirTypeDeclaration[] { owner, plain } };
        var types = file.Types.Value; var methods = owner.Methods.Value; var body = method.Body.Value;
        ApplyAll(new[] { file });
        Require(call.OwnerType.Value.Equals(constructed) && field.OwnerType.Value.Equals(constructed)
            && store.OwnerType.Value.Equals(constructed) && ctorStore.OwnerType.Value.Equals(constructed),
            "bare self did not include captured prefix followed by declared CLR slots");
        Require(ReferenceEquals(file.Types.Value, types) && ReferenceEquals(owner.Methods.Value, methods)
            && ReferenceEquals(method.Body.Value, body) && ReferenceEquals(call.Recv.Value, receiver)
            && ReferenceEquals(store.Value.Value, storeValue) && ReferenceEquals(already.OwnerType.Value, constructed)
            && ReferenceEquals(emptyCall.OwnerType.Value, empty) && foreign.OwnerType.Value.Equals(new TypeNode.Fqn("Other"))
            && staticRead.OwnerType.Value.Equals(Bare()) && exprStore.OwnerType.Value.Equals(Bare())
            && late.OwnerType.Value.Equals(Bare()) && owner.Base.Value.Equals(Bare()) && owner.Interfaces.Value[0].Equals(Bare())
            && ordinaryCall.OwnerType.Value.Equals(new TypeNode.Fqn("Plain")),
            "self closure changed declaration/operand ownership, another shape or an already constructed token");
        var callArgs = ((TypeNode.Fqn)call.OwnerType.Value).Args;
        Require(!ReferenceEquals(callArgs, ((TypeNode.Fqn)field.OwnerType.Value).Args), "separate owner tokens share a mutable argument vector");
        var stableOwner = call.OwnerType.Value;
        ApplyAll(new[] { file });
        Require(ReferenceEquals(stableOwner, call.OwnerType.Value), "self construction was reapplied");
        Console.WriteLine("[owning generic self] self-test OK (placed frames, exact shapes, independent tokens and continuing operands)");
    }
}
