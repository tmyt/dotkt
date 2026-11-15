using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class ReferenceNullableStrip
{
    internal static void SourceSelfTest()
    {
        static void Need(bool value, string message)
        { if (!value) throw new InvalidOperationException("Owning nullable strip: " + message); }
        var text = new TypeNode.Fqn("kotlin.String");
        var nullableText = new TypeNode.Nullable(text);
        var integer = new TypeNode.Fqn("kotlin.Int");
        var nullableInteger = new TypeNode.Nullable(integer);
        var unit = new TypeNode.Fqn("kotlin.Unit");
        var segment = new TypeNode.Fqn("System.ArraySegment`1", new TypeNode[] { nullableText });
        ValueTypeOracle values = type => type.Name is "kotlin.Int" or "System.ArraySegment`1";
        var function = new TypeNode.Fn(true, nullableText, new TypeNode[] {
            new TypeNode.Array(nullableText), new TypeNode.ByRef(nullableText), new TypeNode.Nullable(segment) },
            nullableText, Ctx: new TypeNode[] { nullableText });
        var parameter = new BirParameter { Name = "value", Type = function,
            KotlinType = function, SuspendFnTypePre = function, NullableFlags = new[] { 2 } };
        var referenceNull = new BirConstant { Type = nullableText, Value = new BirLiteral.NullLiteral() };
        var physicalNull = new BirConstant { Type = nullableText, Value = OptionalField<BirLiteral>.Null };
        var unitNull = new BirConstant { Type = new TypeNode.Nullable(unit), Value = new BirLiteral.NullLiteral() };
        var valueNull = new BirConstant { Type = nullableInteger, Value = new BirLiteral.NullLiteral() };
        var absent = new BirConstant { Type = nullableText };
        var field = new BirFieldDeclaration { Name = "value", Type = new TypeNode.Array(new TypeNode.Nullable(segment)),
            KotlinType = function };
        var descriptor = new BirMethodImpl { Owner = new TypeNode.Fqn("Contract", new TypeNode[] { nullableText }),
            Member = "read", Params = new TypeNode[] { nullableText }, Ret = nullableText };
        var method = new BirFunctionDeclaration { Name = "run", Params = new[] { parameter }, Ret = new TypeNode.Nullable(unit),
            // These fields are structured wire types despite their frozen frame role.
            SuspendSourceParams = new TypeNode[] { nullableText }, SuspendSourceRet = nullableText,
            RetKotlinType = function, MethodTypeParameterBoundsPre = new SourceBoundsSnapshot(new Dictionary<int, TypeNode[]> {
                [0] = new TypeNode[] { nullableText } }),
            ClrInterfaceImpls = new[] { descriptor }, InlineBir = new byte[] { 0x80 },
            Body = new IBirExpression[] { referenceNull, physicalNull, unitNull, valueNull, absent }.Select(value => (IBirStatement)new BirExpressionStatement {
                Expr = OptionalField<IBirExpression>.Of(value) }).ToArray() };
        var sourceBounds = method.MethodTypeParameterBoundsPre.Value;
        var body = method.Body.Value; var parameters = method.Params.Value;
        var opaque = method.InlineBir.Value;
        var file = new BirFileDeclaration { Fields = new[] { field }, Methods = new[] { method } };
        Apply(file, values);
        Need(parameter.Type.Value is TypeNode.Fn { Ret: TypeNode.Fqn { Name: "kotlin.String" },
                Recv: TypeNode.Fqn { Name: "kotlin.String" }, Ctx: [TypeNode.Fqn { Name: "kotlin.String" }],
                Params: [TypeNode.Array { Elem: TypeNode.Fqn { Name: "kotlin.String" } },
                    TypeNode.ByRef { Of: TypeNode.Fqn { Name: "kotlin.String" } },
                    TypeNode.Nullable { Of: TypeNode.Fqn { Name: "System.ArraySegment`1", Args: [TypeNode.Fqn { Name: "kotlin.String" }] } }] }
            && field.Type.Value is TypeNode.Array { Elem: TypeNode.Nullable { Of: TypeNode.Fqn {
                Name: "System.ArraySegment`1", Args: [TypeNode.Fqn { Name: "kotlin.String" }] } } },
            "function/array/byref recursion or constructed value nullability changed");
        Need(referenceNull.Type.Value is TypeNode.Fqn { Name: "kotlin.Nothing" }
            && physicalNull.Type.Value is TypeNode.Fqn { Name: "kotlin.Nothing" } && physicalNull.Value.IsNull
            && unitNull.Type.Value is TypeNode.Fqn { Name: "kotlin.Nothing" }
            && valueNull.Type.Value == nullableInteger && absent.Type.Value == text && !absent.Value.IsPresent
            && method.Ret.Value == new TypeNode.Nullable(unit), "explicit null, absent value, value-null or nullable Unit convention changed");
        Need(descriptor.Params.Value[0] == text && descriptor.Ret.Value == text
            && descriptor.Owner.Value == new TypeNode.Fqn("Contract", new TypeNode[] { text })
            && method.SuspendSourceParams.Value[0] == text && method.SuspendSourceRet.Value == text,
            "a blanket foreign/frozen-frame skip lost structured wire types");
        Need(ReferenceEquals(parameter.KotlinType.Value, function) && ReferenceEquals(parameter.SuspendFnTypePre.Value, function)
            && ReferenceEquals(method.RetKotlinType.Value, function) && ReferenceEquals(field.KotlinType.Value, function)
            && ReferenceEquals(method.MethodTypeParameterBoundsPre.Value, sourceBounds)
            && ReferenceEquals(method.InlineBir.Value, opaque) && parameter.NullableFlags.Value.Single() == 2
            && ReferenceEquals(method.Body.Value, body) && ReferenceEquals(method.Params.Value, parameters)
            && ReferenceEquals(method.Params.Value[0], parameter) && ReferenceEquals(method.ClrInterfaceImpls.Value[0], descriptor),
            "encoded source facts, bytes, flags or owning records changed");
        Console.WriteLine("[owning nullable strip] self-test OK (null presence, Unit/value/function shapes, structured foreign/frozen fields and opaque source facts)");
    }
}
