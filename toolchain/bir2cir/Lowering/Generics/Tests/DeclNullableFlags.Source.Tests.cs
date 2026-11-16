using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class DeclNullableFlags
{
    internal static void SourceSelfTest()
    {
        static void Check(bool condition, string message)
        { if (!condition) throw new InvalidOperationException("Owning declaration NRT: " + message); }
        var text = new TypeNode.Fqn("kotlin.String"); var nullable = new TypeNode.Nullable(text);
        var unit = new TypeNode.Nullable(new TypeNode.Fqn("kotlin.Unit"));
        var function = new TypeNode.Fn(false, nullable, new TypeNode[] { nullable });
        var nullFlags = new BirParameter { Name = "nullFlags", Type = nullable, NullableFlags = OptionalField<int[]>.Null };
        var emptyFlags = new BirParameter { Name = "emptyFlags", Type = nullable, NullableFlags = Array.Empty<int>() };
        var annotated = new BirParameter { Name = "annotated", Type = nullable };
        var nullableUnit = new BirParameter { Name = "unit", Type = unit, KotlinType = OptionalField<TypeNode>.Null };
        var functionParameter = new BirParameter { Name = "function", Type = function };
        var generic = new BirParameter { Name = "generic", Type = function, NullableGeneric = function };
        var value = new BirParameter { Name = "value", Type = new TypeNode.Nullable(new TypeNode.Fqn("kotlin.Int")) };
        var collapsed = new BirParameter { Name = "collapsed", Type = new TypeNode.Fqn("Metadata.Collapsed", new TypeNode[] { nullable }) };
        var localParameter = new BirParameter { Name = "local", Type = nullable };
        var local = new BirFunctionDeclaration { Name = "local", Params = new[] { localParameter }, Ret = nullable, Body = Array.Empty<IBirStatement>() };
        var method = new BirFunctionDeclaration { Name = "read", Ret = nullable, RetNullableFlags = OptionalField<int[]>.Null,
            Params = new[] { nullFlags, emptyFlags, annotated, nullableUnit, functionParameter, generic, value, collapsed },
            Body = new IBirStatement[] { new BirLocalFunction { Id = "local", Decl = local } } };
        var ctorParameter = new BirParameter { Name = "ctor", Type = nullable };
        var field = new BirFieldDeclaration { Name = "field", Type = nullable, _nativeLocation = true };
        var property = new BirPropertyDeclaration { Name = "property", Type = new TypeNode.Nullable(function) };
        var owner = new BirClassDefinition { Name = "Metadata.Generated", Kind = "class", Generated = true,
            Methods = new[] { new BirFunctionDeclaration { Name = "generated", Ret = nullable, Params = Array.Empty<BirParameter>() } },
            Ctors = new[] { new BirConstructor { Params = new[] { ctorParameter } } }, Fields = new[] { field }, Properties = new[] { property } };
        var file = new BirFileDeclaration { Methods = new[] { method }, Types = new IBirTypeDeclaration[] { owner } };
        var methods = file.Methods.Value; var body = method.Body.Value; var parameters = method.Params.Value; var empty = emptyFlags.NullableFlags.Value;
        Apply(file, named => named.Name == "kotlin.Int", named => named.Name == "Metadata.Collapsed" ? Array.Empty<TypeNode>() : named.Args,
            named => named.Name == "Metadata.Collapsed");
        Check(nullFlags.NullableFlags.IsNull && method.RetNullableFlags.IsNull && ReferenceEquals(emptyFlags.NullableFlags.Value, empty)
            && annotated.NullableFlags.Value.SequenceEqual(new[] { 2 }), "ContainsKey-style explicit-null/empty flags were overwritten");
        Check(nullableUnit.KotlinType.HasValue && !nullableUnit.NullableFlags.IsPresent
            && functionParameter.KotlinType.HasValue && !functionParameter.NullableFlags.IsPresent
            && !generic.KotlinType.IsPresent && ReferenceEquals(generic.NullableGeneric.Value, function)
            && !value.NullableFlags.IsPresent && !value.KotlinType.IsPresent,
            "exact nullable Unit/function surface or earlier nullable carrier convention changed");
        Check(collapsed.KotlinType.HasValue && !collapsed.NullableFlags.IsPresent,
            "discarded alias arguments acquired nullable byte positions");
        Check(ctorParameter.NullableFlags.Value.SequenceEqual(new[] { 2 }) && field.NullableFlags.Value.SequenceEqual(new[] { 2 })
            && property.NullableFlags.Value.SequenceEqual(new[] { 2 }) && property.KotlinType.HasValue
            && owner.Methods.Value[0].RetNullableFlags.Value.SequenceEqual(new[] { 2 }), "current generated member/ctor inventory was incomplete");
        Check(!localParameter.NullableFlags.IsPresent && !local.RetNullableFlags.IsPresent
            && ReferenceEquals(file.Methods.Value, methods) && ReferenceEquals(method.Body.Value, body) && ReferenceEquals(method.Params.Value, parameters)
            && field._nativeLocation.Value, "NRT collection escaped its declaration domain or changed current ownership");
        Console.WriteLine("[owning declaration NRT] self-test OK (presence, source precedence, exact annotation arguments and current member inventory)");
    }
}
