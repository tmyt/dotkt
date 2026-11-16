using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class UncheckedGenericCastReturnErasure
{
    static void SelfTestReferencedUses()
    {
        static void Require(bool condition, string reason)
        { if (!condition) throw new InvalidOperationException("Typed referenced unchecked return: " + reason); }
        var text = new TypeNode.Fqn("kotlin.String");
        var erased = new TypeNode.Fqn("object");
        var methodVariable = new TypeNode.Tv("method", 0);
        var ownerVariable = new TypeNode.Tv("type", 0);
        var metadata = new ReferenceDotKtMetadata();
        metadata.MemberBindings.Add(new("Producer", "read", 0, null, false, true,
            ReturnType: erased, MethodArity: 1, KotlinReturnType: methodVariable));
        metadata.MemberBindings.Add(new("Owner", "readOwner", 0, null, false, false,
            ReturnType: erased, KotlinReturnType: ownerVariable));
        metadata.MemberBindings.Add(new("Owner", "getItem", 0, null, false, false,
            KotlinReturnType: ownerVariable, ReturnTypeNode: erased, AccessorKind: "get", DeclarationId: "item-id"));
        var references = ReferenceMetadataIndex.DefaultDemandTestIndex(metadata);
        BirCallStatic Read() => new() { Method = "read", Owner = new TypeNode.Fqn("Producer"),
            TypeArgs = new TypeNode[] { text }, Args = Array.Empty<IBirExpression>(), Sty = text };
        var nested = Read();
        var outer = new BirCallStatic { Method = "consume", Owner = new TypeNode.Fqn("Other"),
            Args = new IBirExpression[] { nested }, Ret = erased };
        var ordinary = Read(); ordinary.Ret = OptionalField<TypeNode>.Null;
        var existing = Read(); existing.Ret = erased;
        var selectedOwner = Read(); selectedOwner.OwnerType = new TypeNode.Fqn("Other");
        selectedOwner.CalleeOwner = new TypeNode.Fqn("Producer");
        var unit = Read(); unit.Sty = new TypeNode.Fqn("kotlin.Unit");
        var owner = new TypeNode.Fqn("Owner", new TypeNode[] { text });
        var member = new BirCallInstance { Method = "readOwner", OwnerType = owner,
            Recv = new BirLocal { Name = "receiver", Sty = owner }, Args = Array.Empty<IBirExpression>(), Sty = text };
        var getter = new BirCallInstance { Method = "getItem", DeclarationId = "item-id", OwnerType = owner,
            PropertyName = "item", PropertyAccessor = "get", Args = Array.Empty<IBirExpression>(),
            Recv = new BirLocal { Name = "receiver", Sty = owner }, Sty = text, Ret = ownerVariable };
        var wrongGetter = (BirCallInstance)getter.DeepClone(); wrongGetter.DeclarationId = "other-id";
        IBirExpression[] expressions = { outer, ordinary, existing, selectedOwner, unit, member, getter, wrongGetter };
        var body = expressions.Select(expression => (IBirStatement)new BirExpressionStatement {
            Expr = OptionalField<IBirExpression>.Of(expression) }).ToArray();
        var method = new BirFunctionDeclaration { Name = "use", Body = body };
        var file = new BirFileDeclaration { Methods = new[] { method } };
        ApplyReferenced(file, references);
        Require(ReferenceEquals(file.Methods.Value[0], method) && ReferenceEquals(method.Body.Value, body)
            && ReferenceEquals(outer.Args.Value[0], nested) && nested.Ret.Value == text && ordinary.Ret.Value == text,
            "nested actual calls/null return did not receive the producing declaration's result");
        Require(existing.Ret.Value == erased && !selectedOwner.Ret.IsPresent && !unit.Ret.IsPresent,
            "an existing result, higher-priority owner or ignored Unit result was overwritten");
        Require(member.Ret.Value == text && getter.Ret.Value == text && wrongGetter.Ret.Value == ownerVariable
            && getter.Sty.Value == text && getter.OwnerType.Value == owner,
            "selected method/owner frame or exact getter identity changed");
        ApplyReferenced(file, references);
        Require(nested.Ret.Value == text && getter.Ret.Value == text && ReferenceEquals(method.Body.Value, body),
            "reapplying the referenced capability changed continuing occurrences");
    }
}
