using DotKt.Bir;
using DotKt.Bir.Lowering;

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
        void Validate(TypeNode type, string file)
        {
            var field = new BirFieldDeclaration { Name = "pointer", Type = type };
            var root = new BirFileDeclaration { Fields = new[] { field } };
            ValidatePointerVocabulary(root, file);
            if (!ReferenceEquals(root.Fields.Value[0], field) || !ReferenceEquals(field.Type.Value, type))
                throw new Exception("Pointer validation rewrote its owning source declarations");
        }
        var expected = new TypeNode.FnPtr(0, 0, 2, methodVariable, [typeVariable, methodVariable]);
        if (Lower(callback) != expected) throw new Exception("Function pointer lost its exact generic frame");
        var nullableConstructor = Named("System.Nullable`1", methodVariable);
        if (CanonicalPhysicalSlotType(nullableConstructor) != new TypeNode.Nullable(methodVariable))
            throw new Exception("Native Nullable constructor and physical nullable signature disagree");
        var source = new BirVariable { Name = "pointer", Type = callback };
        FunctionValueRepresentation.Apply(source, _ => false);
        if (source.Type.Value != callback || FunctionValueRepresentation.ContainsOrdinaryFunction(callback)
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
        var rectangularPointers = TypeNode.Array.General(expected, 2);
        if (Lower(rectangularPointers) != rectangularPointers || Lower(Lower(rectangularPointers)) != rectangularPointers)
            throw new Exception("Repeated physical lowering changed a native array rank");
        var unit = Named("kotlin.Unit");
        var unitValue = Named(FunctionPointerVocabulary.Value, unit);
        if (Lower(Named(FunctionPointerVocabulary.Pointer, convention, new TypeNode.Fn(false, unitValue, [unitValue])))
                is not TypeNode.FnPtr { Ret: TypeNode.Fqn { Name: "kotlin.Unit" }, Params: [TypeNode.Fqn { Name: "kotlin.Unit" }] }
            || Lower(Named(FunctionPointerVocabulary.Pointer, convention, new TypeNode.Fn(false, unit, [])))
                is not TypeNode.FnPtr { Ret: TypeNode.Fqn { Name: "void" } })
            throw new Exception("Native Unit value and void return were conflated");
        var modifiedUnit = Named(FunctionPointerVocabulary.Optional, Named("Marker"), unit);
        if (Lower(Named(FunctionPointerVocabulary.Pointer, convention, new TypeNode.Fn(false, modifiedUnit, [modifiedUnit])))
                is not TypeNode.FnPtr {
                    Ret: TypeNode.Mod { Of: TypeNode.Fqn { Name: "void" } },
                    Params: [TypeNode.Mod { Of: TypeNode.Fqn { Name: "kotlin.Unit" } }],
                })
            throw new Exception("A signature modifier changed the Unit value/return position");
        foreach (var invalid in new[] {
            Named(FunctionPointerVocabulary.Pointer, convention, new TypeNode.Star()),
            Named(FunctionPointerVocabulary.Pointer, convention,
                new TypeNode.Fn(false, new TypeNode.Nullable(methodVariable), [methodVariable])),
            Named("System.Collections.Generic.List", callback),
        })
        {
            try { Validate(invalid, "function-pointer-selftest"); }
            catch (InvalidDataException) { continue; }
            throw new Exception("Inexact or generic-argument function-pointer signature was accepted");
        }
        Validate(new TypeNode.Array(callback), "function-pointer-array-selftest");
        foreach (var invalid in new[] {
            expected with { CallingConvention = 5 },
            expected with { GenericArity = 1 },
            expected with { RequiredParams = 1 },
            expected with { Ret = new TypeNode.Mod(false, Named("Opt"), new TypeNode.Mod(true, Named("Req"), methodVariable)) },
        })
        {
            try { ValidateNativePointer(invalid); }
            catch (InvalidDataException) { continue; }
            throw new Exception("Unsupported native function-pointer signature escaped its diagnostic boundary");
        }
    }
}
