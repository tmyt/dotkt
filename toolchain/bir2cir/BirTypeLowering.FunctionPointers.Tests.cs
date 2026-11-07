using System.Text.Json.Nodes;
using DotKt.Bir;

static partial class BirTypeLowering
{
    internal static void SelfTestFunctionPointers()
    {
        TypeNode.Fqn Named(string name, params TypeNode[] args) => new(name, args.Length == 0 ? null : args);
        var typeVariable = new TypeNode.Tv("type", 0);
        var methodVariable = new TypeNode.Tv("method", 0);
        var convention = Named(FunctionPointerVocabulary.Conventions[0]);
        var callback = Named(FunctionPointerVocabulary.Pointer, convention,
            new TypeNode.Fn(false, methodVariable, [typeVariable, methodVariable]));
        TypeNode Lower(TypeNode type) => LowerPhysicalType(type, new Dictionary<string, string>(), _ => false, null, false);
        var expected = new TypeNode.FnPtr(0, 0, 2, methodVariable, [typeVariable, methodVariable]);
        if (Lower(callback) != expected) throw new Exception("Function pointer lost its exact generic frame");
        var source = TypeNode.Write(callback);
        FunctionValueRepresentation.Apply(source, _ => false);
        if (TypeJson.Read(source) != callback || FunctionValueRepresentation.ContainsOrdinaryFunction(source)
            || NullableGenericErasure.EraseArgument(callback, _ => false) != callback)
            throw new Exception("Native function-pointer signature was treated as a stored Kotlin function");
        if (SupertypeGraph.SubstOwnerTvs(expected, [Named("System.String")]) is not TypeNode.FnPtr {
                Params: [TypeNode.Fqn { Name: "System.String" }, TypeNode.Tv { Scope: "method", I: 0 }],
                Ret: TypeNode.Tv { Scope: "method", I: 0 } })
            throw new Exception("Function-pointer owner substitution changed the method frame");
        var nested = Named(FunctionPointerVocabulary.Pointer, Named(FunctionPointerVocabulary.Conventions[1]),
            new TypeNode.Fn(false, Named("kotlin.Unit"), [Named(FunctionPointerVocabulary.Arrays[2], callback)]));
        if (Lower(nested) is not TypeNode.FnPtr { CallingConvention: 1,
                Params: [TypeNode.Array { Rank: 2, SzArray: false, Elem: TypeNode.FnPtr }] })
            throw new Exception("Nested native function-pointer/array shape changed");
        foreach (var invalid in new[] {
            Named(FunctionPointerVocabulary.Pointer, convention, new TypeNode.Star()),
            Named("System.Collections.Generic.List", callback),
        })
        {
            try { ValidatePointerVocabulary(TypeNode.Write(invalid), "function-pointer-selftest"); }
            catch (InvalidDataException) { continue; }
            throw new Exception("Inexact or generic-argument function-pointer signature was accepted");
        }
        ValidatePointerVocabulary(TypeNode.Write(new TypeNode.Array(callback)), "function-pointer-array-selftest");
    }
}
