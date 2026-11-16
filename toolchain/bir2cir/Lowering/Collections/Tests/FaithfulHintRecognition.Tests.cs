using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class FaithfulHintRecognition
{
    internal static void TypedSelfTest()
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        var variable = new TypeNode.Tv("method", 0);
        var text = new TypeNode.Fqn("kotlin.String");
        var any = new TypeNode.Fqn("kotlin.Any");
        var anyN = new TypeNode.Nullable(any);
        var list = new TypeNode.Fqn("kotlin.collections.List", new TypeNode[] { variable });
        var map = new TypeNode.Fqn("kotlin.collections.Map", new TypeNode[] { text, variable });
        var star = new TypeNode.Fqn(map.Name, new TypeNode[] { anyN, anyN });
        BirLocal Local(string name) => new() { Name = name };
        BirExpressionStatement Statement(IBirExpression value) => new() { Expr = OptionalField<IBirExpression>.Of(value) };
        var source = new BirFunctionDeclaration {
            Name = "recognition", TypeParams = new[] { new BirTypeParameter { Name = "T" } },
            Params = new[] {
                new BirParameter { Name = "input", Type = list }, new BirParameter { Name = "map", Type = map },
                new BirParameter { Name = "nullable", Type = new TypeNode.Nullable(text) }, new BirParameter { Name = "opaque", Type = any },
                new BirParameter { Name = "bytes", Type = new TypeNode.Fqn("kotlin.UByteArray") },
            },
            Body = new IBirStatement[] {
                new BirVariable { Name = "value", Type = list, Init = Local("input") },
                Statement(new BirConcat { Parts = new IBirExpression[] {
                    new BirCast { Type = any, E = Local("value") }, Local("nullable"),
                } }),
                Statement(new BirObjectMethod { Method = "ToString", Recv = Local("map") }),
                Statement(new BirCallStatic { Method = "println", Args = new IBirExpression[] {
                    new BirCast { Type = star, E = Local("opaque") }, Local("value"),
                } }),
                Statement(new BirCallInstance { OwnerType = new TypeNode.Fqn("kotlin.Double"), Method = "compareTo",
                    Recv = new BirConstant { Type = new TypeNode.Fqn("kotlin.Double"), Value = new BirLiteral.RealLiteral(-0.0) },
                    Args = new IBirExpression[] { new BirConstant { Type = new TypeNode.Fqn("kotlin.Double"), Value = new BirLiteral.RealLiteral(0.0) } } }),
                Statement(new BirCallStatic { Method = "toByteArray", Args = new IBirExpression[] { Local("bytes") } }),
            },
        };
        BirCallStatic Bind(BirCallStatic call) => call with { DeclarationId = "helper:" + call.Method.Value };
        var originalVariable = source.Body.Value[0];
        var concatInput = (BirConcat)((BirExpressionStatement)source.Body.Value[1]).Expr.Value;
        var concatCollectionInput = ((BirCast)concatInput.Parts.Value[0]).E.Value;
        var nullableInput = concatInput.Parts.Value[1];
        var mapInput = ((BirObjectMethod)((BirExpressionStatement)source.Body.Value[2]).Expr.Value).Recv.Value;
        var printInput = (BirCallStatic)((BirExpressionStatement)source.Body.Value[3]).Expr.Value;
        var starInput = printInput.Args.Value[0];
        var printCollectionInput = printInput.Args.Value[1];
        var compareInput = (BirCallInstance)((BirExpressionStatement)source.Body.Value[4]).Expr.Value;
        var bytesInput = ((BirCallStatic)((BirExpressionStatement)source.Body.Value[5]).Expr.Value).Args.Value[0];
        BirFunctionDeclaration actual = null;
        FaithfulHints.WithHelperBinding(Bind, () => actual = (BirFunctionDeclaration)Apply(source, names));
        if (!ReferenceEquals(actual, source) || !ReferenceEquals(actual.Body.Value[0], originalVariable))
            throw new InvalidOperationException("Owning recognition replaced a continuing declaration or local");
        BirCallStatic Helper(IBirExpression value, string owner, string method, TypeNode[] signature, TypeNode[] arguments = null)
        {
            if (value is not BirCallStatic call || call.Owner.OrDefault() is not TypeNode.Fqn { Args: null } named
                || named.Name != owner || call.Method.OrDefault() != method || call.DeclarationId.OrDefault() != "helper:" + method
                || !call.Sig.HasValue || !call.Sig.Value.SequenceEqual(signature)
                || (arguments == null ? call.TypeArgs.IsPresent : !call.TypeArgs.HasValue || !call.TypeArgs.Value.SequenceEqual(arguments)))
                throw new InvalidOperationException("Owning recognition changed the selected helper declaration or signature: " + method);
            return call;
        }
        var concat = (BirConcat)((BirExpressionStatement)actual.Body.Value[1]).Expr.Value;
        var collectionHelper = Helper(concat.Parts.Value[0], "kotlin.collections.ClrCollectionDefaultsKt", "clrCollToString",
            new TypeNode[] { new TypeNode.Fqn("kotlin.collections.Collection", new TypeNode[] { variable }) }, new TypeNode[] { variable });
        var nullableHelper = Helper(concat.Parts.Value[1], "kotlin.LibraryKt", "toString", new TypeNode[] { anyN });
        var mapHelper = Helper(((BirExpressionStatement)actual.Body.Value[2]).Expr.Value, "kotlin.collections.ClrMapDefaultsKt", "clrMapToString",
            new TypeNode[] { any, anyN }, map.Args);
        var print = (BirCallStatic)((BirExpressionStatement)actual.Body.Value[3]).Expr.Value;
        var starHelper = Helper(print.Args.Value[0], "kotlin.collections.ClrNestedToStringKt", "clrElemToString", new TypeNode[] { anyN });
        var printedCollection = Helper(print.Args.Value[1], "kotlin.collections.ClrCollectionDefaultsKt", "clrCollToString",
            collectionHelper.Sig.Value, new TypeNode[] { variable });
        var compareHelper = Helper(((BirExpressionStatement)actual.Body.Value[4]).Expr.Value, "kotlin.NumbersKt", "clrDoubleCompare",
            new TypeNode[] { new TypeNode.Fqn("kotlin.Double"), new TypeNode.Fqn("kotlin.Double") });
        if (!ReferenceEquals(collectionHelper.Args.Value.Single(), concatCollectionInput)
            || !ReferenceEquals(nullableHelper.Args.Value.Single(), nullableInput)
            || !ReferenceEquals(mapHelper.Args.Value[0], mapInput) || mapHelper.Args.Value.Length != 2
            || mapHelper.Args.Value[1] is not BirCallStatic { Method.Value: "clrMapViewPreference" }
            || !ReferenceEquals(print, printInput) || print.DeclarationId.IsPresent
            || !ReferenceEquals(starHelper.Args.Value.Single(), starInput)
            || !ReferenceEquals(printedCollection.Args.Value.Single(), printCollectionInput)
            || !ReferenceEquals(compareHelper.Args.Value[0], compareInput.Recv.Value)
            || !ReferenceEquals(compareHelper.Args.Value[1], compareInput.Args.Value[0])
            || ((BirExpressionStatement)actual.Body.Value[5]).Expr.Value is not BirCast {
                Type.Value: TypeNode.Array { Elem: TypeNode.Fqn { Name: "kotlin.Byte" } }, E.HasValue: true } bytesCast
            || !ReferenceEquals(bytesCast.E.Value, bytesInput))
            throw new InvalidOperationException("Owning recognition changed operand ownership, rendering or byte-array view");
        var operand = new BirCallStatic { Method = "sideEffect", Args = Array.Empty<IBirExpression>() };
        var mapRender = FaithfulHints.CollToString((IBirExpression)operand, FaithfulHints.CollKind.Map, map.Args);
        if (!ReferenceEquals(mapRender.Args.Value[0], operand) || mapRender.Args.Value.Length != 2
            || mapRender.Args.Value[1] is not BirCallStatic { Method.Value: "clrMapViewPreference" })
            throw new InvalidOperationException("Owning map rendering duplicated its operand or omitted the selected source view");
        foreach (var (owner, key, keyVariance, value, valueVariance) in new (TypeNode.Fqn, TypeNode, long, TypeNode, long)[] {
            (map, text, 0, variable, 1),
            (new TypeNode.Fqn("kotlin.collections.MutableMap", new TypeNode[] { new TypeNode.Projection("in", text), variable }), text, -1, variable, 0),
            (new TypeNode.Fqn(map.Name, new TypeNode[] { new TypeNode.Array(new TypeNode.Star()), variable }), null, 0, variable, 1),
            (new TypeNode.Fqn(map.Name, new TypeNode[] { new TypeNode.Star(), new TypeNode.Star() }), null, 0, null, 0),
        })
            foreach (var phase in new[] { MemberCallSubstitution.CollectionOperationPhase.Source, MemberCallSubstitution.CollectionOperationPhase.Physical })
            {
                var preference = MemberCallSubstitution.StaticMapPreferenceValue(owner, null, phase);
                bool Token(IBirExpression token, TypeNode type) => type == null
                    ? token is BirConstant { Value.Value: BirLiteral.NullLiteral }
                    : token is BirClassReference reference && reference.Type.OrDefault() == type;
                if (key == null && value == null)
                {
                    if (!Token(preference, null)) throw new InvalidOperationException("Unknown Map view preference acquired a generic token");
                }
                else if (preference is not BirCallStatic { Method.Value: "clrMapViewPreference", TypeArgs.Value.Length: 0, Args.Value.Length: 4 } selected
                    || selected.Owner.OrDefault() is not TypeNode.Fqn { Name: "kotlin.collections.ClrNativeMapAccessKt" }
                    || !Token(selected.Args.Value[0], key) || !Token(selected.Args.Value[2], value)
                    || selected.Args.Value[1] is not BirConstant { Value.Value: BirLiteral.IntegerLiteral keyFlag } || keyFlag.Value != keyVariance
                    || selected.Args.Value[3] is not BirConstant { Value.Value: BirLiteral.IntegerLiteral valueFlag } || valueFlag.Value != valueVariance)
                    throw new InvalidOperationException("Typed Map view preference changed its source/physical tokens or variance");
            }
        var boxedNullable = new BirCast { Type = any, E = Local("later") };
        var resultRegion = new BirValueBlock {
            Stmts = new IBirStatement[] { new BirVariable { Name = "later", Type = new TypeNode.Nullable(text) } },
            Result = new BirConcat { Parts = new IBirExpression[] { boxedNullable } },
        };
        var renderedRegion = (BirValueBlock)Apply(resultRegion, names);
        if (renderedRegion.Result.Value is not BirConcat { Parts.HasValue: true } resultConcat
            || resultConcat.Parts.Value.Single() is not BirCallStatic { Method.Value: "toString", Args.HasValue: true } renderer
            || !ReferenceEquals(renderer.Args.Value.Single(), boxedNullable))
            throw new InvalidOperationException("Result rendering lost its local's nullable type or stripped the required Any cast");
        BirCallStatic Reinterpret() => new() { Method = "toByteArray", Args = new IBirExpression[] {
            new BirLocal { Name = "bytes", Sty = new TypeNode.Fqn("kotlin.UByteArray") },
        } };
        var shadowed = Reinterpret();
        if (!ReferenceEquals(Apply(shadowed, new HashSet<string> { "toByteArray" }), shadowed))
            throw new InvalidOperationException("A user function was reinterpreted as the stdlib byte-array view");
        var native = Reinterpret();
        native.OwnerType = new TypeNode.Fqn("probe.Native");
        if (!ReferenceEquals(Apply(native, names), native))
            throw new InvalidOperationException("A CLR static method was reinterpreted as a Kotlin extension");
        var payload = new BirInlinePayload { Body = new IBirStatement[] { Statement(Reinterpret()) } };
        if (!ReferenceEquals(Apply(payload, names), payload) || ((BirExpressionStatement)payload.Body.Value[0]).Expr.Value is not BirCallStatic)
            throw new InvalidOperationException("Source recognition entered an opaque executable carrier");
        var late = (BirCallStatic)Apply(new BirCallInstance {
            OwnerType = new TypeNode.Fqn("kotlin.Float"), Method = "compareTo", Recv = Local("a"), Args = new IBirExpression[] { Local("b") },
        }, names);
        if (late.DeclarationId.IsPresent || late.Method.OrDefault() != "clrFloatCompare" || late.TypeArgs.IsPresent)
            throw new InvalidOperationException("Late recognition eagerly bound a helper outside the source binding scope");
        Console.WriteLine("[typed faithful recognition] self-test OK (lexical rendering, exact helper facts, Map views, byte-array guards, opaque payloads, late unbound helper)");
    }
}
