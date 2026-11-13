using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class NetInteropBinding
{
    internal static void TypedSelfTest()
    {
        var refs = ReferenceMetadataIndex.Build(new[] {
            typeof(object).Assembly.Location, typeof(System.Numerics.Vector3).Assembly.Location,
        }.Distinct().ToArray());
        var text = new TypeNode.Fqn("System.String");
        var integer = new TypeNode.Fqn("kotlin.Int");
        var builder = new TypeNode.Fqn("System.Text.StringBuilder");
        var vector = new TypeNode.Fqn("System.Numerics.Vector3");
        var floating = new TypeNode.Fqn("System.Single");
        var decimalType = new TypeNode.Fqn("System.Decimal");
        BirLocal Local(string name) => new() { Name = name };
        BirExpressionStatement Statement(IBirExpression value) => new() { Expr = OptionalField<IBirExpression>.Of(value) };
        var fieldReceiver = Local("vector");
        var indexReceiver = Local("text");
        var index = new BirConstant { Type = integer, Value = new BirLiteral.IntegerLiteral(0) };
        var memberReceiver = Local("builder");
        var wrappedBuilder = new TypeNode.Nullable(builder);
        var boundReceiver = Local("bound");
        var operatorReceiver = Local("left");
        var operatorArgument = Local("right");
        var assigned = new BirConstant { Type = floating, Value = new BirLiteral.RealLiteral(1.0) };
        var localReference = new BirNewBoundDelegate { OwnerType = builder, Method = "ToString", Sig = Array.Empty<TypeNode>(),
            Recv = Local("ordinary"), FuncType = new TypeNode.Fn(false, text, Array.Empty<TypeNode>()) };
        var opaqueCall = new BirCallStatic { OwnerType = new TypeNode.Fqn("System.Math"), Method = "Abs",
            Sig = new TypeNode[] { integer }, Args = new IBirExpression[] { index } };
        var opaque = new BirInlinePayload { V = 1, Fqn = "NativeProbeKt.carrier", FileClass = "NativeProbeKt", Static = true,
            Recv = "none", Params = Array.Empty<BirParameter>(), Ret = new TypeNode.Fqn("kotlin.Unit"),
            Body = new IBirStatement[] { Statement(opaqueCall) } };
        var call = new BirCallStatic { OwnerType = new TypeNode.Fqn("System.Activator"), Method = "CreateInstance",
            TypeArgs = new TypeNode[] { builder }, ShapeTypes = Array.Empty<TypeNode>(),
            Args = Array.Empty<IBirExpression>(), Ret = builder, Sty = builder };
        var method = new BirFunctionDeclaration { Name = "nativeProbe", DeclarationId = "native-probe",
            Ret = new TypeNode.Fqn("kotlin.Unit"), Params = Array.Empty<BirParameter>(),
            Body = new IBirStatement[] {
                Statement(call),
                Statement(new BirFieldGet { OwnerType = vector, Name = "X", Recv = fieldReceiver, Ret = floating, Sty = floating }),
                Statement(new BirCallInstance { OwnerType = new TypeNode.Fqn("System.String"), Method = "get", Prop = "index-get",
                    Recv = indexReceiver, Args = new IBirExpression[] { index }, Sig = new TypeNode[] { integer }, Ret = new TypeNode.Fqn("kotlin.Char") }),
                Statement(new BirCallInstance { OwnerType = wrappedBuilder, Method = "Length", Prop = "get", Recv = memberReceiver,
                    Args = Array.Empty<IBirExpression>(), Sig = Array.Empty<TypeNode>(), Ret = integer, Sty = integer, Super = false }),
                Statement(new BirNewBoundDelegate { OwnerType = builder, Method = "ToString", ArgTypes = Array.Empty<TypeNode>(),
                    Recv = boundReceiver, FuncType = new TypeNode.Fn(false, text, Array.Empty<TypeNode>()),
                    DeclarationId = "selected-native-member", Virtual = true }),
                Statement(localReference),
                Statement(new BirCallInstance { OwnerType = new TypeNode.Fqn("System.DayOfWeek"), Method = "ordinal", Prop = "get",
                    Recv = Local("day"), Args = Array.Empty<IBirExpression>(), Sig = Array.Empty<TypeNode>(), Ret = integer }),
                new BirSetField { OwnerType = vector, Name = "X", Recv = Local("destination"), Value = assigned },
                Statement(new BirCallInstance { OwnerType = decimalType, Method = "plus", Recv = operatorReceiver,
                    Args = new IBirExpression[] { operatorArgument }, ArgTypes = new TypeNode[] { decimalType }, Ret = decimalType }),
            },
        };
        var file = new BirFileDeclaration { FileClass = "NativeProbeKt", Methods = new[] { method },
            Types = Array.Empty<IBirTypeDeclaration>() };
        if (!ReferenceEquals(Apply(file, refs), file) || !ReferenceEquals(file.Methods.Value.Single(), method)
            || method.DeclarationId.Value != "native-probe")
            throw new InvalidOperationException("Native binding replaced a continuing declaration");
        IBirExpression At(int position) => ((BirExpressionStatement)method.Body.Value[position]).Expr.Value;
        if (At(0) is not BirGenericStatic { Method.Value: "CreateInstance", ResolvedMemberParams.Value.Length: 0 } generic
            || generic.Ret.OrDefault() != builder || generic.Sty.OrDefault() != builder || generic.TypeArgs.Value.Single() != builder
            || At(1) is not BirPropertyGet { Name.Value: "X", Static.Value: false } field
            || field.Ret.OrDefault() != floating || field.Sty.OrDefault() != floating || !ReferenceEquals(field.Recv.Value, fieldReceiver)
            || At(2) is not BirNativeInstance { Method.Value: "get_Chars" } indexed
            || !ReferenceEquals(indexed.Recv.Value, indexReceiver) || !ReferenceEquals(indexed.Args.Value.Single(), index)
            || indexed.ArgTypes.Value.Single() != integer || indexed.ResolvedMemberParams.Value.Single() != integer
            || At(3) is not BirPropertyGet { Name.Value: "Length", Super.Value: false } property
            || property.Type.OrDefault() != wrappedBuilder || !ReferenceEquals(property.Recv.Value, memberReceiver)
            || !property._foreignDeclarationSignature.HasValue || property._foreignDeclarationSignature.Value.Types.Count != 0
            || At(4) is not BirBoundClrDelegate { DeclarationId.Value: "selected-native-member", Virtual.Value: true, ArgTypes.Value.Length: 0 } reference
            || !ReferenceEquals(reference.Recv.Value, boundReceiver) || reference.TypeArgs.IsPresent
            || !ReferenceEquals(At(5), localReference)
            || At(6) is not BirEnumOrdinal { Values.Value.Length: 7 } ordinal || ordinal.Values.Value[0].Ordinal.Value != 0
            || At(7) is not BirPropertySet { Name.Value: "X", Static.Value: false } store || !ReferenceEquals(store.Value.Value, assigned)
            || At(8) is not BirNativeStatic { Method.Value: "op_Addition", ArgTypes.Value.Length: 2, Args.Value.Length: 2 } operation
            || !ReferenceEquals(operation.Args.Value[0], operatorReceiver) || !ReferenceEquals(operation.Args.Value[1], operatorArgument)
            || operation.ArgTypes.Value.Any(type => type != decimalType))
            throw new InvalidOperationException("Native binding lost a selected member, exact signature, result stamp or occurrence");
        var inlineBytes = ExecutableBirCodec.EncodeInline(ExecutableBirCodec.Codec, opaque);
        var opaqueFile = new BirFileDeclaration { Methods = new[] { new BirFunctionDeclaration { Name = "carrier", InlineBir = inlineBytes } } };
        Apply(opaqueFile, refs);
        if (!ReferenceEquals(opaqueFile.Methods.Value[0].InlineBir.Value, inlineBytes))
            throw new InvalidOperationException("Native binding entered an opaque executable payload");
        var empty = Array.Empty<TypeNode>();
        var signatures = SelectSignatures(empty, new TypeNode[] { floating }, new TypeNode[] { text });
        if (!ReferenceEquals(signatures.Declaration, empty) || signatures.Declared.Single() != text
            || signatures.Generic.Single() != floating || !ReferenceEquals(signatures.Operator, empty))
            throw new InvalidOperationException("Native binding conflated use-specific declaration vectors or lost explicit empty");
        var variable = new TypeNode.Tv("method", 0);
        if (NormalizeSignature(new TypeNode[] { new TypeNode.Array(new TypeNode.Nullable(variable)), new TypeNode.Nullable(integer) })
                is not [TypeNode.Array { Elem: TypeNode.Tv { Scope: "method", I: 0 } }, TypeNode.Nullable { Of: TypeNode.Fqn { Name: "kotlin.Int" } }])
            throw new InvalidOperationException("Native declaration normalization changed a real nullable value or retained a nullable open variable");
        var originalArgs = new TypeNode[] { text };
        var snapshot = SourceTypeVector.Create(new TypeNode[] { new TypeNode.Fqn("probe.Owner", originalArgs) });
        originalArgs[0] = floating;
        if (snapshot.Types.Single() is not TypeNode.Fqn { Args: [TypeNode.Fqn { Name: "System.String" }] }
            || SourceTypeVector.ReadFrozenText(snapshot.FrozenText).Types.Single() != snapshot.Types.Single()
            || SourceTypeVector.Create(empty).FrozenText != "[]")
            throw new InvalidOperationException("Frozen native declaration signature aliased its source vector");
        var genericOwner = new TypeNode.Fqn("System.Collections.Generic.List");
        if (CloseStaticOwner(genericOwner, typeof(List<>)) is not TypeNode.Fqn { Args: [TypeNode.Fqn { Name: "kotlin.Any" }] }
            || CloseStaticOwner(new TypeNode.Fqn(genericOwner.Name, empty), typeof(List<>)) is not TypeNode.Fqn { Args.Length: 0 }
            || CloseStaticOwner(new TypeNode.Nullable(genericOwner), typeof(List<>)) is not TypeNode.Nullable { Of: TypeNode.Fqn { Args: null } })
            throw new InvalidOperationException("Static-owner closure inferred absent arguments through wrappers or an explicit empty vector");
        var effect = new BirCallStatic { Method = "sourceEffect", Ret = integer, Args = Array.Empty<IBirExpression>() };
        var lowered = new BirConstant { Type = integer, Value = new BirLiteral.IntegerLiteral(7) };
        var preserved = TypedCallEvalLowering.PreserveUnreadValueBefore(effect, lowered, "native self-test");
        if (preserved is not BirValueBlock { Stmts.Value: [BirVariable { Init.Value: BirCallStatic { Method.Value: "sourceEffect" } }],
                Result.Value: BirConstant { Value.Value: BirLiteral.IntegerLiteral { Value: 7 } } }
            || TypedCallEvalLowering.PreserveUnreadValueBefore(Local("unused"), lowered, "native self-test") is not BirConstant)
            throw new InvalidOperationException("Mapped-away native receiver was duplicated, discarded or reordered");
        Console.WriteLine("[typed native binding] self-test OK (owning declarations, native calls/fields/indexers/delegates, enum ordinal, stamps, exact vectors, opaque payload, frozen signature, receiver evaluation)");
    }
}
