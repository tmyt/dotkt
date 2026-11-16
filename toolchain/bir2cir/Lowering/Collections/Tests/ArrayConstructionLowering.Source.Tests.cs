using System;
using System.Linq;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class ArrayConstructionLowering
{
    internal static void SelfTestSource()
    {
        var integer = new TypeNode.Fqn("kotlin.Int");
        var text = new TypeNode.Fqn("kotlin.String");
        var intArray = new TypeNode.Array(integer);
        var textArray = new TypeNode.Array(text);
        var matrixType = new TypeNode.Oblivious(new TypeNode.Array(intArray));
        var sourceRead = Get("row");
        var bodyRead = Get("row");
        var afterRead = Get("row");
        // The source expression reads the outer row. The loop's row shadows it
        // only in the body; the original array type is known before rewriting.
        var array = new BirValueBlock {
            Stmts = new IBirStatement[] { new BirExpressionStatement { Expr = sourceRead } },
            Result = new BirLocal { Name = "matrix" },
        };
        var loopBody = new IBirStatement[] { new BirExpressionStatement { Expr = bodyRead } };
        var loop = new BirForArray { Var = "row", Array = array, Body = loopBody };
        var file = File(new[] {
            new BirParameter { Name = "row", Type = textArray },
            new BirParameter { Name = "matrix", Type = matrixType },
        }, loop, new BirExpressionStatement { Expr = afterRead });
        var method = file.Methods.Value[0];
        var methods = file.Methods.Value;
        var statements = method.Body.Value;
        if (!ReferenceEquals(Apply(file), file) || !ReferenceEquals(file.Methods.Value, methods)
            || !ReferenceEquals(file.Methods.Value[0], method) || !ReferenceEquals(method.Body.Value, statements)
            || !ReferenceEquals(loop.Body.Value, loopBody) || !ReferenceEquals(loop.Array.Value, array)
            || !TypeNodeDataEquality.Equal(loop.Elem.Value, intArray)
            || !TypeNodeDataEquality.Equal(sourceRead.Elem.Value, text)
            || !TypeNodeDataEquality.Equal(bodyRead.Elem.Value, integer)
            || !TypeNodeDataEquality.Equal(afterRead.Elem.Value, text) || !loop.Label.IsNull)
            throw new InvalidOperationException("Array loop changed source/body scope, nested element facts or owning declaration identity");

        // Lexical statement declarations apply to following siblings, not to a
        // different region or the outer function's next statement.
        var localWrite = new BirArraySet { Array = new BirLocal { Name = "row" }, Index = Number(0), Value = Number(7) };
        var scopedRead = Get("row");
        var outsideRead = Get("row");
        var local = new BirVariable { Name = "row", Type = new TypeNode.Fqn("kotlin.IntArray"),
            Init = new BirNew { Type = new TypeNode.Fqn("kotlin.IntArray"), Args = new IBirExpression[] { Number(2) },
                ArgTypes = new TypeNode[] { integer } } };
        var localBlock = new BirBlock { Body = new IBirStatement[] {
            local, new BirExpressionStatement { Expr = localWrite }, new BirExpressionStatement { Expr = scopedRead },
        } };
        Apply(File(new[] { new BirParameter { Name = "row", Type = textArray } },
            localBlock, new BirExpressionStatement { Expr = outsideRead }));
        if (local.Init.Value is not BirNewArraySized { Elem.Value: TypeNode.Fqn { Name: "kotlin.Int" } }
            || localWrite.Elem.Value is not TypeNode.Fqn { Name: "kotlin.Int" }
            || scopedRead.Elem.Value is not TypeNode.Fqn { Name: "kotlin.Int" }
            || outsideRead.Elem.Value is not TypeNode.Fqn { Name: "kotlin.String" })
            throw new InvalidOperationException("Array declaration scope leaked or lost an indexed write's element");
        var resultRead = Get("items");
        var valueBlock = new BirValueBlock {
            Stmts = new IBirStatement[] { new BirVariable { Name = "items", Type = intArray,
                Init = new BirNewArraySized { Elem = integer, Size = Number(1) } } },
            Result = resultRead,
        };
        Apply(File(Array.Empty<BirParameter>(), new BirExpressionStatement { Expr = valueBlock }));
        if (resultRead.Elem.Value is not TypeNode.Fqn { Name: "kotlin.Int" })
            throw new InvalidOperationException("Generated source region did not carry declarations into its result");

        // Preserve the explicit constructor initializer slot before source sty;
        // an initializer accepting Any is a valid argument to an Int-input slot.
        var declared = new TypeNode.Fn(false, integer, new TypeNode[] { integer });
        var wider = new TypeNode.Fn(false, integer, new TypeNode[] { new TypeNode.Fqn("kotlin.Any") });
        var size = new BirCallStatic { Owner = OptionalField<TypeNode>.Null, Method = "size", Args = Array.Empty<IBirExpression>() };
        var initializer = new BirLocal { Name = "initializer", Sty = wider };
        var construction = new BirExpressionStatement { Expr = new BirNew {
            Type = new TypeNode.Fqn("kotlin.IntArray"), Args = new IBirExpression[] { size, initializer },
            ArgTypes = new TypeNode[] { integer, declared },
        } };
        var existing = new BirNewArrayInit { Elem = integer, Size = Number(1), Init = initializer,
            FuncType = OptionalField<TypeNode>.Null };
        var nullFact = new BirNewArrayInit { Elem = integer, Size = Number(1), Init = new BirLocal { Name = "untyped" },
            FuncType = OptionalField<TypeNode>.Null };
        var absentFact = new BirNewArrayInit { Elem = integer, Size = Number(1), Init = new BirLocal { Name = "untyped" } };
        var initializerFunctionType = new BirNewDelegate { FuncType = declared,
            CalleeOwner = new TypeNode.Fqn("InitializerTargets"), Method = "initialize", Sig = new TypeNode[] { integer } };
        var fromFunctionFact = new BirNewArrayInit { Elem = integer, Size = Number(1), Init = initializerFunctionType };
        Apply(File(new[] { new BirParameter { Name = "initializer", Type = wider },
                new BirParameter { Name = "untyped", Type = declared } },
            construction, new BirExpressionStatement { Expr = existing },
            new BirExpressionStatement { Expr = nullFact }, new BirExpressionStatement { Expr = absentFact },
            new BirExpressionStatement { Expr = fromFunctionFact }));
        if (construction.Expr.Value is not BirNewArrayInit lowered || !ReferenceEquals(lowered.Size.Value, size)
            || !ReferenceEquals(lowered.Init.Value, initializer) || !TypeNodeDataEquality.Equal(lowered.FuncType.Value, declared)
            || !TypeNodeDataEquality.Equal(existing.FuncType.Value, wider)
            || !TypeNodeDataEquality.Equal(fromFunctionFact.FuncType.Value, declared)
            || !nullFact.FuncType.IsNull || absentFact.FuncType.IsPresent)
            throw new InvalidOperationException("Array initializer lost slot authority, one-time operands, or absent/null facts");

        var backing = new TypeNode.Fqn("kotlin.IntArray");
        var wrapped = new BirNew { Type = new TypeNode.Fqn("kotlin.UIntArray"),
            Args = new IBirExpression[] { new BirLocal { Name = "storage", Sty = backing } },
            ArgTypes = new TypeNode[] { backing } };
        var wrapStatement = new BirExpressionStatement { Expr = wrapped };
        var sized = new BirExpressionStatement { Expr = new BirNew { Type = new TypeNode.Fqn("kotlin.UIntArray"),
            Args = new IBirExpression[] { Number(3) }, ArgTypes = new TypeNode[] { integer } } };
        var nullableRead = Get("platformArray");
        var nullableElement = new TypeNode.Nullable(text);
        var preserved = Get("platformArray");
        preserved.Elem = nullableElement;
        Apply(File(new[] { new BirParameter { Name = "platformArray",
                Type = new TypeNode.Oblivious(new TypeNode.Nullable(new TypeNode.Array(nullableElement))) } },
            wrapStatement, sized, new BirExpressionStatement { Expr = nullableRead }, new BirExpressionStatement { Expr = preserved }));
        if (!ReferenceEquals(wrapStatement.Expr.Value, wrapped)
            || sized.Expr.Value is not BirNewArraySized { Elem.Value: TypeNode.Fqn { Name: "kotlin.UInt" } }
            || !TypeNodeDataEquality.Equal(nullableRead.Elem.Value, nullableElement)
            || !ReferenceEquals(preserved.Elem.Value, nullableElement)
            || !TypeNodeDataEquality.Equal(matrixType, new TypeNode.Oblivious(new TypeNode.Array(intArray))))
            throw new InvalidOperationException("Unsigned wrap/size distinction or existing/nullable element facts changed");
        Console.WriteLine("[typed array construction] self-test OK (loop timing, lexical slots, initializer authority, ownership and presence)");
    }

    static BirArrayGet Get(string name) => new() { Array = new BirLocal { Name = name }, Index = Number(0) };
    static BirConstant Number(long value) => new() { Type = new TypeNode.Fqn("kotlin.Int"), Value = new BirLiteral.IntegerLiteral(value) };
    static BirFileDeclaration File(BirParameter[] parameters, params IBirStatement[] body) => new() {
        FileClass = "ArrayConstructionProbe", Methods = new[] { new BirFunctionDeclaration {
            Name = "probe", Static = true, DeclarationId = "array-construction-probe", Params = parameters,
            Ret = new TypeNode.Fqn("kotlin.Unit"), Body = body,
        } },
    };
}
