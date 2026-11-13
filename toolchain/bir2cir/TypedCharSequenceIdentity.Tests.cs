using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static class TypedCharSequenceIdentityTests
{
    internal static void SelfTest()
    {
        var source = new TypeNode.Fqn("kotlin.CharSequence") { Classifier = new("kotlin", Array.Empty<string>(), "CharSequence") };
        var physical = source with { Name = SharedSyntheticSynthesis.CharSeq };
        var baseArguments = new TypeNode[] { source };
        var sourceBase = new TypeNode.Fqn("Base", baseArguments);
        var prior = new TypeNode.Fqn("Contract", new TypeNode[] { new TypeNode.Fqn("Earlier") });
        var contract = new TypeNode.Fqn("Contract", new TypeNode[] { source });
        var independent = new TypeNode.Fqn("Other");
        var owner = new BirClassDefinition { Kind = "class", Name = "kotlin.CharSequence", Base = sourceBase,
            Interfaces = new TypeNode[] { contract, independent },
            KotlinSupertypesPre = SourceTypeFacts.Empty.Merge(null, new TypeNode[] { prior }, null) };
        var parameter = new BirParameter { Name = "value", Type = source, KotlinType = source };
        var call = new BirCallInstance { OwnerType = source, Method = "get", DeclarationId = "source-get", Ret = source,
            Recv = new BirLocal { Name = "value", Sty = source }, Args = Array.Empty<IBirExpression>() };
        var other = new BirCallInstance { OwnerType = independent, Method = "read", DeclarationId = "other-read", Args = Array.Empty<IBirExpression>() };
        var ownerless = new BirCallInstance { Method = "invoke", DeclarationId = "ownerless-invoke", Args = Array.Empty<IBirExpression>() };
        var nullOwner = new BirCallInstance { OwnerType = OptionalField<TypeNode>.Null, Method = "invoke",
            DeclarationId = "null-owner-invoke", Args = Array.Empty<IBirExpression>() };
        var nested = new TypeNode.Fn(false, new TypeNode.Array(source, 2, false), new TypeNode[] { new TypeNode.ByRef(source) },
            Recv: source, Ctx: new TypeNode[] { new TypeNode.Ptr(source) });
        var method = new BirFunctionDeclaration { Name = "use", Params = new[] { parameter }, Ret = nested,
            RetKotlinType = source, Body = new IBirStatement[] {
                new BirExpressionStatement { Expr = call }, new BirExpressionStatement { Expr = other },
                new BirExpressionStatement { Expr = ownerless }, new BirExpressionStatement { Expr = nullOwner },
            } };
        var methods = new[] { method }; owner.Methods = methods;
        var root = new BirFileDeclaration { FileClass = "CharSequenceIdentity", Types = new IBirTypeDeclaration[] { owner } };
        TypedCharSequenceIdentity.ApplyAll(new[] { root });
        var rewritten = (TypeNode.Fn)method.Ret.Value;
        if (owner.Name.Value != "kotlin.CharSequence" || !ReferenceEquals(owner.Methods.Value, methods)
            || call.DeclarationId.IsPresent || other.DeclarationId.Value != "other-read"
            || ownerless.OwnerType.IsPresent || !nullOwner.OwnerType.IsNull
            || ownerless.DeclarationId.Value != "ownerless-invoke" || nullOwner.DeclarationId.Value != "null-owner-invoke"
            || !TypeNodeDataEquality.Equal(call.OwnerType.Value, physical)
            || !TypeNodeDataEquality.Equal(parameter.Type.Value, physical)
            || !TypeNodeDataEquality.Equal(parameter.KotlinType.Value, source)
            || !TypeNodeDataEquality.Equal(method.RetKotlinType.Value, source)
            || rewritten.Ret is not TypeNode.Array { Rank: 2, SzArray: false, Elem: TypeNode.Fqn { Name: SharedSyntheticSynthesis.CharSeq } }
            || rewritten.Params[0] is not TypeNode.ByRef { Of: TypeNode.Fqn { Name: SharedSyntheticSynthesis.CharSeq } }
            || rewritten.Ctx is not [TypeNode.Ptr { Of: TypeNode.Fqn { Name: SharedSyntheticSynthesis.CharSeq } }])
            throw new InvalidOperationException("CharSequence identity rewrite changed declarations, frozen facts, or nested type metadata");
        var facts = owner.KotlinSupertypesPre.Value;
        if (!TypeNodeDataEquality.Equal(facts.Base, sourceBase) || facts.Interfaces.Count != 1
            || !TypeNodeDataEquality.Equal(facts.Interfaces[0], prior))
            throw new InvalidOperationException("CharSequence identity rewrite lost source edges or replaced earlier source facts");
        baseArguments[0] = independent;
        if (facts.Base is not TypeNode.Fqn { Args: [TypeNode.Fqn { Name: "kotlin.CharSequence" }] })
            throw new InvalidOperationException("Saved CharSequence source edge retained mutable input type storage");
        var stable = root.DeepClone();
        TypedCharSequenceIdentity.ApplyAll(new[] { root });
        if (!root.StructurallyEquals(stable)) throw new InvalidOperationException("CharSequence identity selection was applied twice");
    }
}
