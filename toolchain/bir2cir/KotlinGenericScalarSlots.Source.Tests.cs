using System;
using System.Collections.Generic;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class KotlinGenericScalarSlots
{
    internal static void SourceScalarSelfTest()
    {
        var source = new TypeNode.Tv("method", 0);
        var physical = new TypeNode.Tv("method", 1);
        var ownerPhysical = new TypeNode.Tv("type", 1);
        var integer = new TypeNode.Fqn("kotlin.Int");
        BirFunctionDeclaration Method(string id, TypeNode variable) => new() {
            Name = id, DeclarationId = id, Ret = variable,
            TypeParams = new[] { new BirTypeParameter { Name = "T" }, new BirTypeParameter { Name = "Storage" } },
            Params = new[] { new BirParameter { Name = "value", Type = variable } },
        };
        var method = Method("scalar", physical);
        method.NullableRepresentationFrame = new NullableRepresentationFrame(1, Array.Empty<int>(), storageIndices: new[] { 0 });
        var ownerMethod = Method("ownerScalar", ownerPhysical);
        var suspend = Method("suspend", source);
        suspend.Mods = new BirModifiers { Suspend = true };
        var native = Method("native", source);
        native._physicalDeclarationSignature = true;
        var intrinsic = Method("intrinsic", source);
        intrinsic.Attrs = new[] { new BirAttribute { Attr = new TypeNode.Fqn("kotlin.clr.ClrIntrinsic") } };
        var overridesIntrinsic = Method("overridesIntrinsic", source);
        overridesIntrinsic.Overrides = new[] { new BirOverride { Owner = new TypeNode.Fqn("ScalarOwner"), DeclarationId = "intrinsic" } };
        var query = Method("contains", source);
        query.Overrides = new[] { new BirOverride { Owner = new TypeNode.Fqn("kotlin.collections.Collection"), Member = "contains", Kind = "method", Arity = 1 } };
        var alias = Method("alias", source);
        var property = new BirPropertyDeclaration { Name = "value", Type = ownerPhysical };
        var inherited = new BirInheritedClassMethod { Member = "inherited", Params = new[] { new BirParameter { Name = "value", Type = ownerPhysical } }, Ret = ownerPhysical };
        var call = new BirCallStatic { Method = "scalar", DeclarationId = "scalar", Sig = new TypeNode[] { source }, Sty = integer };
        var constrained = new BirConstrainedCall { Method = "scalar", DeclarationId = "scalar", Sig = new TypeNode[] { source }, Sty = integer };
        var root = new BirFileDeclaration { FileClass = "ScalarCalls", Methods = new[] { new BirFunctionDeclaration {
                Name = "use", Params = Array.Empty<BirParameter>(), Ret = new TypeNode.Fqn("kotlin.Unit"),
                Body = new IBirStatement[] { new BirExpressionStatement { Expr = call }, new BirExpressionStatement { Expr = constrained } },
            } }, Types = new IBirTypeDeclaration[] {
                new BirClassDefinition {
                    Name = "ScalarOwner", Kind = "class", TypeParams = new[] { new BirTypeParameter { Name = "T" }, new BirTypeParameter { Name = "Storage" } },
                    KotlinSupertypesPre = SourceTypeFacts.ReadFrozenText("""{"bounds":{},"nullableFrame":{"sourceArity":1,"nullable":[],"storage":[0],"nullableStorage":[],"order":[0,1]}}"""),
                    Methods = new[] { method, ownerMethod, suspend, native, intrinsic, overridesIntrinsic, query },
                    Properties = new[] { property }, InheritedClassMethods = new[] { inherited },
                },
                new BirClassDefinition { Name = "NativeAlias", Kind = "class", Methods = new[] { alias } },
            } };
        var aliases = new Dictionary<string, string> {
            ["NativeAlias"] = "System.Native", ["kotlin.collections.Collection"] = "System.Collections.Generic.IReadOnlyCollection",
        };
        var refs = ReferenceMetadataIndex.Build(Array.Empty<string>());
        var policy = GenericRepresentationPolicy.CreateSource(new[] { root }, aliases, refs);
        var projection = ApplyAll(new[] { root }, policy, refs);
        if (!ReferenceEquals(projection.Policy, policy)
            || method.Params.Value[0].Type.Value is not TypeNode.Fqn { Name: "object" }
            || method.Params.Value[0].KotlinType.Value != source || method.RetKotlinType.Value != source
            || ownerMethod.Params.Value[0].KotlinType.Value != new TypeNode.Tv("type", 0)
            || ownerMethod.RetKotlinType.Value != new TypeNode.Tv("type", 0)
            || property.KotlinType.Value != ownerPhysical || property.Type.Value is not TypeNode.Fqn { Name: "object" }
            || inherited.Params.Value[0].KotlinType.Value != new TypeNode.Tv("type", 0)
            || inherited.RetKotlinType.Value != new TypeNode.Tv("type", 0))
            throw new InvalidOperationException("Scalar projection lost source/physical frame ownership or raw property source identity");
        if (suspend.Params.Value[0].Type.Value is not TypeNode.Fqn { Name: "object" } || suspend.Ret.Value != source
            || suspend.RetKotlinType.IsPresent || native.Params.Value[0].Type.Value != source
            || intrinsic.Params.Value[0].Type.Value != source || overridesIntrinsic.Params.Value[0].Type.Value != source
            || alias.Params.Value[0].Type.Value != source || query.Params.Value[0].Type.Value != source
            || call.Sig.Value[0] is not TypeNode.Fqn { Name: "object" } || call.Ret.Value != integer
            || constrained.Sig.Value[0] is not TypeNode.Fqn { Name: "object" } || constrained.Ret.Value != integer)
            throw new InvalidOperationException("Scalar projection changed native/query/suspend boundaries or the caller-facing result");
        // The late DOM callback must update the maps carried from this source pass.
        var late = Method("late", source).ToDom();
        projection.ProjectMethod(late);
        if (!projection.Parameters.ContainsKey("late") || !projection.Returns.Contains("late")
            || !projection.Parameters.ContainsKey("scalar") || !projection.Returns.Contains("scalar"))
            throw new InvalidOperationException("Late scalar projection replaced the source projection authority");
        if (projection.Result(new BirCallStatic { DeclarationId = "scalar", Sty = source, DynRet = physical, Ret = integer }, refs) != source
            || projection.Result(new BirCallInstance { DeclarationId = "late", Sty = OptionalField<TypeNode>.Null, DynRet = physical }, refs) != physical
            || projection.Result(new BirCallStatic { DeclarationId = "scalar", Ret = source }, refs) != null
            || projection.Result(new BirCallStatic { DeclarationId = "scalar", Sty = integer, DynRet = source }, refs) != null
            || projection.Result(new BirCallStatic { DeclarationId = "scalar", Sty = new TypeNode.Nullable(source) }, refs) != null
            || projection.Result(new BirCallStatic { Method = "scalar", Sty = source }, refs) != null
            || projection.Result(new BirCallStatic { DeclarationId = "not-recorded", Sty = source }, refs) != null
            || projection.Result(new BirConstrainedCall { DeclarationId = "scalar", Sty = source }, refs) != null)
            throw new InvalidOperationException("Owning scalar results lost exact identity, live projection authority, bare-variable or Sty/DynRet precedence");
        Console.WriteLine("[source generic scalar slots] self-test OK (method/property frames, native/query/suspend, call result, one late authority)");
    }
}
