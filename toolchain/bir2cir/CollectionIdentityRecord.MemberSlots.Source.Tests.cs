using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class CollectionIdentityRecord
{
    internal static void MemberSlotsSourceSelfTest()
    {
        static void Check(bool condition, string message)
        { if (!condition) throw new InvalidOperationException("Owning collection member metadata: " + message); }
        var text = new TypeNode.Fqn("kotlin.String");
        var list = new TypeNode.Fqn("kotlin.collections.List", new TypeNode[] { text });
        var nested = new TypeNode.Fqn("Metadata.Box", new TypeNode[] { list });
        var prior = new TypeNode.Fqn("kotlin.collections.MutableList", new TypeNode[] { text });
        var overwritten = new BirParameter { Name = "overwritten", Type = nested, CollIdentity = prior };
        var nullCarrier = new BirParameter { Name = "null", Type = nested, CollIdentity = OptionalField<TypeNode>.Null };
        var directArray = new BirParameter { Name = "directArray", Type = new TypeNode.Array(list) };
        var nestedArray = new BirParameter { Name = "nestedArray", Type = new TypeNode.Array(nested) };
        var nullable = new BirParameter { Name = "nullable", Type = new TypeNode.Fqn("Metadata.Box", new TypeNode[] { new TypeNode.Nullable(list) }) };
        var oblivious = new BirParameter { Name = "oblivious", Type = new TypeNode.Fqn("Metadata.Box", new TypeNode[] { new TypeNode.Oblivious(list) }) };
        var mutable = new BirParameter { Name = "mutable", Type = new TypeNode.Fqn("Metadata.Box", new TypeNode[] { prior }), CollIdentity = OptionalField<TypeNode>.Null };
        var local = new BirFunctionDeclaration { Name = "local", Params = Array.Empty<BirParameter>(), Ret = nested, Body = Array.Empty<IBirStatement>() };
        var method = new BirFunctionDeclaration { Name = "read", Ret = nested, CollIdentityRet = prior,
            Params = new[] { overwritten, nullCarrier, directArray, nestedArray, nullable, oblivious, mutable },
            Body = new IBirStatement[] { new BirLocalFunction { Id = "local", Decl = local } } };
        var constructorParameter = new BirParameter { Name = "ctor", Type = nested };
        var field = new BirFieldDeclaration { Name = "field", Type = nested, _nativeLocation = true };
        var property = new BirPropertyDeclaration { Name = "property", Type = nested };
        var facts = SourceTypeFacts.Empty.Merge(nested, null, null);
        var owner = new BirClassDefinition { Name = "Metadata.Owner", Kind = "class", Base = nested, KotlinSupertypesPre = facts,
            Methods = Array.Empty<BirFunctionDeclaration>(), Ctors = new[] { new BirConstructor { Params = new[] { constructorParameter } } },
            Fields = new[] { field }, Properties = new[] { property } };
        var file = new BirFileDeclaration { Methods = new[] { method }, Types = new IBirTypeDeclaration[] { owner } };
        var parameters = method.Params.Value; var body = method.Body.Value;
        RecordMemberSlots(file);
        Check(TypeNodeDataEquality.Equal(method.CollIdentityRet.Value, nested) && TypeNodeDataEquality.Equal(overwritten.CollIdentity.Value, nested)
            && nullCarrier.CollIdentity.HasValue && nestedArray.CollIdentity.HasValue && oblivious.CollIdentity.HasValue,
            "exact collapsing readonly predicate did not overwrite current member carriers");
        Check(!directArray.CollIdentity.IsPresent && !nullable.CollIdentity.IsPresent && mutable.CollIdentity.IsNull
            && !local.CollIdentityRet.IsPresent, "root-V reset, mutable-only or declaration-domain predicate changed");
        Check(constructorParameter.CollIdentity.HasValue && field.CollIdentity.HasValue && property.CollIdentity.HasValue
            && ReferenceEquals(owner.KotlinSupertypesPre.Value, facts) && !owner.TypeParams.IsPresent
            && ReferenceEquals(method.Params.Value, parameters) && ReferenceEquals(method.Body.Value, body) && field._nativeLocation.Value,
            "member recording re-ran early edge capture or changed current declaration ownership");
        Check(!ReferenceEquals(((TypeNode.Fqn)overwritten.CollIdentity.Value).Args, nested.Args), "source member carrier shares mutable type vectors");
        Console.WriteLine("[owning collection member metadata] self-test OK (exact overwrite/reset predicate, current slots, edge clock and independent facts)");
    }
}
