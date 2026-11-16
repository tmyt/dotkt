using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class GenericArrayStorageLowering
{
    internal static void SourceTypeOperationSelfTest()
    {
        static void Require(bool value, string message)
        { if (!value) throw new InvalidOperationException("Owning array type operation: " + message); }
        var refs = ReferenceMetadataIndex.Build(new[] { typeof(object).Assembly.Location });
        var objectType = new TypeNode.Fqn("System.Object");
        var sourceDeclaration = new BirFunctionDeclaration { Name = "retained", Params = Array.Empty<BirParameter>(),
            Ret = objectType, Body = Array.Empty<IBirStatement>() };
        var operand = new BirValueBlock { Stmts = new IBirStatement[] { new BirLocalFunction { Id = "retained", Decl = sourceDeclaration } },
            Result = new BirLocal { Name = "input", Sty = objectType } };
        var source = new BirCast { Type = new TypeNode.Array(new TypeNode.Star()), E = operand };
        var file = new BirFileDeclaration { Methods = new[] { new BirFunctionDeclaration { Name = "owner",
            Params = new[] { new BirParameter { Name = "$arrayCast$0", Type = objectType } },
            Body = new IBirStatement[] { new BirExpressionStatement { Expr = source } } } } };
        var names = new TypedGenericArrayStorageLowering.FreshNames(file);
        bool Interface(TypeNode type) => type is TypeNode.Fqn named && refs.IsInterfaceType(named);
        var result = (BirValueBlock)LowerTypeOperation(source, source.Type.Value, null, null, refs,
            type => type, Interface, names, (_, _) => null);
        Require(result.Type.Value is TypeNode.Fqn { Name: "System.Array" }
            && result.Stmts.Value is [BirVariable capture, BirIf _, BirIf _]
            && capture.Name.Value == "$arrayCast$1" && capture.Init.Value is BirCast actualCast
            && ReferenceEquals(actualCast.E.Value, operand), "unsafe cast lost exact names, null refusal, or operand ownership");
        var actualDeclarations = 0;
        var nativeCalls = 0;
        result.Visit(node => {
            if (ReferenceEquals(node, sourceDeclaration)) actualDeclarations++;
            if (node is BirNativeInstance call) { nativeCalls++; Require(call.MemberRef.HasValue, "runtime type call lost its exact MemberRef"); }
        });
        Require(actualDeclarations == 1 && nativeCalls > 0, "a nested registered declaration was cloned or runtime checks were omitted");

        var nullableTest = new BirIsInstance { Type = new TypeNode.Nullable(new TypeNode.Array(new TypeNode.Star())),
            E = new BirLocal { Name = "nullableInput", Sty = objectType } };
        var nullableResult = (BirValueBlock)LowerTypeOperation(nullableTest, nullableTest.Type.Value, null, null, refs,
            type => type, Interface, names, (_, _) => null);
        Require(nullableResult.Stmts.Value[0] is BirVariable input && ReferenceEquals(input.Init.Value, nullableTest.E.Value)
            && nullableResult.Stmts.Value[1] is BirVariable { Init.Value: BirInstanceOrNull }
            && nullableResult.Result.Value is BirConditional { Then.Value: BirConstant { Value.Value: BirLiteral.BooleanLiteral { Value: true } } },
            "nullable classifier did not preserve null before checking the nonnull allocation component");

        var methodVariable = new TypeNode.Tv("method", 0);
        var witness = new BirIsInstance { Type = new TypeNode.Array(methodVariable), E = new BirLocal { Name = "input" } };
        Require(ReferenceEquals(witness, LowerTypeOperation(witness, witness.Type.Value, null, null, refs,
            type => type, Interface, names, (_, _) => null)), "reified/witness-owned type variable cast was consumed early");
        var exception = new TypeNode.Fqn("System.Exception");
        var disposable = new TypeNode.Fqn("System.IDisposable");
        var frame = new SourceTypeOperationFrame(new[] { new BirTypeParameter { Name = "T",
            Constraints = new TypeNode[] { disposable, exception } } }, null, null);
        var boundedCast = new BirCast { Type = new TypeNode.Array(methodVariable), E = new BirLocal { Name = "bounded" } };
        var bounded = LowerTypeOperation(boundedCast, boundedCast.Type.Value, null, frame, refs,
            type => type, Interface, names, (_, _) => null);
        var classifiers = new List<TypeNode>();
        bounded.Visit(node => { if (node is BirClassReference value) classifiers.Add(value.Type.Value); });
        Require(classifiers.Contains(exception) && !classifiers.Contains(disposable),
            "generic array erasure selected an extra intersection interface instead of its representative class bound");
        Console.WriteLine("[owning array type operations] self-test OK (single operand, native exact refs, null-safe classifier, witness boundary, declaration-owned representative bound)");
    }
}
