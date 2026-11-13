using System.Text.Json.Nodes;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class PhysicalValueCoercion
{
    internal static void SelfTest()
    {
        NumericOperandsSelfTest();
        var root = JsonNode.Parse("""
        {"fileClass":"ValueEdges","methods":[
          {"name":"nativeSlot","typeParams":[{"name":"T"}],"params":[{"name":"value","type":{"t":"tv","scope":"method","i":0}}],
           "ret":{"t":"tv","scope":"method","i":0},"body":[]},
          {"name":"read","typeParams":[{"name":"T"},{"name":"P"}],"params":[{"name":"value","type":{"t":"tv","scope":"method","i":0}}],
           "ret":{"t":"tv","scope":"method","i":1},"body":[{"k":"return","value":{
             "k":"callStatic","owner":{"t":"fqn","name":"ValueEdges"},"method":"nativeSlot",
             "sig":[{"t":"tv","scope":"method","i":0}],"typeArgs":[{"t":"tv","scope":"method","i":0}],
             "args":[{"k":"local","name":"value"}],"ret":{"t":"tv","scope":"method","i":1}}}]},
          {"name":"view","params":[{"name":"value","type":{"t":"fqn","name":"System.Object"}}],
           "ret":{"t":"fqn","name":"System.Collections.Generic.IReadOnlyList","args":[{"t":"fqn","name":"System.String"}]},
           "body":[{"k":"return","value":{"k":"local","name":"value"}}]}]}
        """)!.AsObject();
        var carrierView = root["methods"][2].DeepClone().AsObject();
        carrierView["name"] = "carrierView";
        carrierView["params"][0]["type"] = TypeJson.Fqn("Carrier");
        root["methods"].AsArray().Add(carrierView);
        root["types"] = new JsonArray(new JsonObject { ["name"] = "PlainOwner", ["kind"] = "class" });
        root["types"].AsArray().Add(new JsonObject {
            ["name"] = "GenericOwner", ["kind"] = "class", ["typeParams"] = new JsonArray(new JsonObject { ["name"] = "T" }),
            ["interfaces"] = new JsonArray(TypeJson.Fqn("Carrier")),
        });
        root["types"].AsArray().Add(new JsonObject {
            ["name"] = "UnrelatedOwner", ["kind"] = "class", ["typeParams"] = new JsonArray(new JsonObject { ["name"] = "T" }),
        });
        root["types"].AsArray().Add(new JsonObject {
            ["name"] = "InterfaceOwner", ["kind"] = "interface", ["typeParams"] = new JsonArray(new JsonObject { ["name"] = "T" }),
            ["interfaces"] = new JsonArray(TypeJson.Fqn("Carrier")),
        });
        root["types"].AsArray().Add(new JsonObject {
            ["name"] = "DerivedCarrier", ["kind"] = "interface",
            ["interfaces"] = new JsonArray(TypeJson.Fqn("Carrier")),
        });
        var exactLocalView = carrierView.DeepClone().AsObject();
        exactLocalView["name"] = "exactLocalView";
        exactLocalView["typeParams"] = new JsonArray(new JsonObject { ["name"] = "T" });
        exactLocalView["ret"] = TypeJson.Write(new TypeNode.Fqn("GenericOwner", new TypeNode[] { new TypeNode.Tv("method", 0) }));
        root["methods"].AsArray().Add(exactLocalView);
        var interfaceView = exactLocalView.DeepClone().AsObject();
        interfaceView["name"] = "interfaceView";
        interfaceView["params"][0]["type"] = TypeJson.Fqn("DerivedCarrier");
        interfaceView["ret"] = TypeJson.Write(new TypeNode.Fqn("InterfaceOwner", new TypeNode[] { new TypeNode.Tv("method", 0) }));
        root["methods"].AsArray().Add(interfaceView);
        var blockLocalView = exactLocalView.DeepClone().AsObject();
        blockLocalView["name"] = "blockLocalView";
        blockLocalView["body"][0]["value"] = new JsonObject {
            ["k"] = "valueBlock", ["type"] = blockLocalView["ret"].DeepClone(),
            ["stmts"] = new JsonArray(new JsonObject {
                ["k"] = "var", ["name"] = "stored", ["type"] = TypeJson.Fqn("Carrier"),
                ["init"] = new JsonObject { ["k"] = "local", ["name"] = "value" },
            }),
            ["result"] = new JsonObject { ["k"] = "local", ["name"] = "stored" },
        };
        root["methods"].AsArray().Add(blockLocalView);
        var unrelatedView = exactLocalView.DeepClone().AsObject();
        unrelatedView["name"] = "unrelatedView";
        unrelatedView["ret"] = TypeJson.Write(new TypeNode.Fqn("UnrelatedOwner", new TypeNode[] { new TypeNode.Tv("method", 0) }));
        root["methods"].AsArray().Add(unrelatedView);
        var referencedView = exactLocalView.DeepClone().AsObject();
        referencedView["name"] = "referencedView";
        referencedView["ret"] = TypeJson.Fqn("ReferencedOwner");
        root["methods"].AsArray().Add(referencedView);
        var localReceiver = JsonNode.Parse("""
          {"name":"localReceiver","typeParams":[{"name":"P"}],
           "params":[{"name":"value","type":{"t":"tv","scope":"method","i":0}}],
           "ret":{"t":"fqn","name":"System.Int32"},"body":[{"k":"return","value":{
             "k":"constrainedCall","iface":{"t":"fqn","name":"PlainOwner"},
             "recvType":{"t":"tv","scope":"method","i":0},"method":"Read",
             "recv":{"k":"local","name":"value"},"sig":[],"args":[],
             "ret":{"t":"fqn","name":"System.Int32"}}}]}
          """).AsObject();
        root["methods"].AsArray().Add(localReceiver);
        root["types"].AsArray().Add(JsonNode.Parse("""
          {"name":"CapturedValue","kind":"class","typeParams":[{"name":"T"}],
           "ctors":[{"params":[{"name":"value","type":{"t":"tv","scope":"type","i":0}}],"body":[]}]}
          """));
        var closureCapture = JsonNode.Parse("""
          {"name":"closureCapture","typeParams":[{"name":"T"},{"name":"P"}],
           "params":[{"name":"value","type":{"t":"tv","scope":"method","i":1}}],
           "ret":{"t":"fqn","name":"void"},"body":[
             {"k":"exprStmt","expr":{"k":"newClosure","closureType":{"t":"fqn","name":"CapturedValue"},
               "typeArgs":[{"t":"tv","scope":"method","i":0}],"captures":[{"k":"local","name":"value"}]}},
             {"k":"exprStmt","expr":{"k":"newClosure","closureType":{"t":"fqn","name":"CapturedValue"},
               "typeArgs":[{"t":"tv","scope":"method","i":1}],"captures":[{"k":"local","name":"value"}]}}]}
          """).AsObject();
        root["methods"].AsArray().Add(closureCapture);
        var opaqueResults = JsonNode.Parse("""
          {"fileClass":"OpaqueResults","methods":[
            {"name":"nativeResult","params":[],"ret":{"t":"fqn","name":"NativeView"},"body":[]},
            {"name":"opaqueResult","params":[],"ret":{"t":"fqn","name":"System.Object"},"body":[]},
            {"name":"probe","params":[],"ret":{"t":"fqn","name":"void"},"body":[
              {"k":"var","name":"nativeAsObject","type":{"t":"fqn","name":"System.Object"},"init":{"k":"callStatic","owner":{"t":"fqn","name":"OpaqueResults"},
                "method":"nativeResult","sig":[],"args":[],"ret":{"t":"fqn","name":"System.Object"}}},
              {"k":"var","name":"opaqueAsNative","type":{"t":"fqn","name":"NativeView"},"init":{"k":"callStatic","owner":{"t":"fqn","name":"OpaqueResults"},
                "method":"opaqueResult","sig":[],"args":[],"ret":{"t":"fqn","name":"NativeView"}}},
              {"k":"var","name":"propertyAsObject","type":{"t":"fqn","name":"System.Object"},"init":{"k":"clrPropGet","type":{"t":"fqn","name":"NativeOwner"},
                "name":"Value","static":false,"recv":{"k":"const","type":{"t":"fqn","name":"NativeOwner"},"value":null},
                "ret":{"t":"fqn","name":"System.Object"},"memberRef":{"kind":"propertyAccessor",
                  "declaringType":{"t":"fqn","name":"NativeOwner"},"name":"get_Value",
                  "returnType":{"t":"fqn","name":"NativeView"},"parameterTypes":[]}}}]}]}
          """).AsObject();
        ApplyFixtureForSelfTest(new JsonNode[] { opaqueResults }, () => throw new InvalidOperationException("Unexpected Unit conversion"), _ => false);
        var opaqueEdges = opaqueResults["methods"][2]["body"].AsArray();
        foreach (var (edge, expected) in opaqueEdges.Select((edge, index) => (edge,
                     index == 1 ? "System.Object" : "NativeView")))
            if (Str(edge["init"]["k"]) != "cast"
                || TypeJson.Read(edge["init"]["e"]["ret"]) != new TypeNode.Fqn(expected))
                throw new InvalidOperationException("Opaque reference projection lost an exact local/native result contract");
        var genericResults = JsonNode.Parse("""
          {"fileClass":"GenericResults","methods":[
            {"name":"read","typeParams":[{"name":"T"}],"params":[],"ret":{"t":"tv","scope":"method","i":0},"body":[]},
            {"name":"probe","typeParams":[{"name":"T"}],"params":[],"ret":{"t":"fqn","name":"void"},"body":[
              {"k":"var","name":"value","type":{"t":"fqn","name":"object"},"init":
                {"k":"callStatic","owner":{"t":"fqn","name":"GenericResults"},"method":"read",
                 "typeArgs":[{"t":"tv","scope":"method","i":0}],"sig":[],"args":[],"ret":{"t":"fqn","name":"object"}}}]}]}
          """).AsObject();
        ApplyFixtureForSelfTest(new JsonNode[] { genericResults }, () => throw new InvalidOperationException("Unexpected Unit conversion"), _ => false);
        var genericBox = genericResults["methods"][1]["body"][0]["init"];
        if (Str(genericBox["k"]) != "cast"
            || TypeJson.Read(genericBox["type"]) != new TypeNode.Fqn("object")
            || TypeJson.Read(genericBox["e"]["ret"]) != new TypeNode.Tv("method", 0))
            throw new InvalidOperationException("A generic physical return lost boxing at its opaque value boundary");
        var valueResults = JsonNode.Parse("""
          {"fileClass":"ValueResults","methods":[
            {"name":"read","params":[],"ret":{"t":"fqn","name":"object"},"body":[]},
            {"name":"probe","params":[],"ret":{"t":"fqn","name":"void"},"body":[]}]}
          """).AsObject();
        var valueBody = valueResults["methods"][1]["body"].AsArray();
        JsonObject ReadAs(TypeNode type) => new() {
            ["k"] = "callStatic", ["owner"] = TypeJson.Fqn("ValueResults"), ["method"] = "read",
            ["sig"] = new JsonArray(), ["args"] = new JsonArray(), ["dynRet"] = TypeJson.Write(type),
        };
        var integer = new TypeNode.Fqn("System.Int32");
        var nullableInteger = new TypeNode.Nullable(integer);
        foreach (var type in new TypeNode[] { integer, nullableInteger, new TypeNode.Fqn("object") })
            valueBody.Add(new JsonObject { ["k"] = "var", ["name"] = "value" + valueBody.Count,
                ["type"] = TypeJson.Write(type), ["init"] = ReadAs(type) });
        valueBody.Add(new JsonObject { ["k"] = "exprStmt", ["expr"] = ReadAs(integer) });
        ApplyFixtureForSelfTest(new JsonNode[] { valueResults }, () => throw new InvalidOperationException("Unexpected Unit conversion"),
            type => type.Name == "System.Int32");
        valueBody = valueResults["methods"][1]["body"].AsArray();
        foreach (var position in new[] { 0, 1 })
            if (Str(valueBody[position]["init"]["k"]) != "cast"
                || TypeJson.Read(valueBody[position]["init"]["type"]) != (position == 0 ? integer : nullableInteger)
                || TypeJson.Read(valueBody[position]["init"]["e"]["dynRet"]) != new TypeNode.Fqn("object"))
                throw new InvalidOperationException("Consumed erased invocation lost its concrete value projection");
        if (Str(valueBody[2]["init"]["k"]) != "callStatic" || Str(valueBody[3]["expr"]["k"]) != "callStatic"
            || TypeJson.Read(valueBody[3]["expr"]["dynRet"]) != new TypeNode.Fqn("object"))
            throw new InvalidOperationException("Object storage or discarded invocation acquired a premature extraction");
        ApplyFixtureForSelfTest(new JsonNode[] { root }, () => throw new InvalidOperationException("Unexpected Unit conversion"), _ => false,
            isExistentialOwner: owner => owner.Name is "Carrier" or "DerivedCarrier",
            isNativeReference: owner => owner.Name == "System.Collections.Generic.IReadOnlyList",
            referencedCarrier: (target, source) => target.Name == "ReferencedOwner" && source.Name == "Carrier");
        JsonObject Method(string name) => root["methods"].AsArray().OfType<JsonObject>().Single(method => Str(method["name"]) == name);
        carrierView = Method("carrierView");
        exactLocalView = Method("exactLocalView");
        interfaceView = Method("interfaceView");
        blockLocalView = Method("blockLocalView");
        unrelatedView = Method("unrelatedView");
        referencedView = Method("referencedView");
        localReceiver = Method("localReceiver");
        closureCapture = Method("closureCapture");
        var result = root["methods"][1]["body"][0]["value"];
        if (Str(result["k"]) != "cast" || TypeJson.Read(result["type"]) != new TypeNode.Tv("method", 1)
            || TypeJson.Read(result["e"]["ret"]) != new TypeNode.Tv("method", 0))
            throw new InvalidOperationException("Local generic value conversion lost the exact inner result slot");
        var view = root["methods"][2]["body"][0]["value"];
        if (Str(view["k"]) != "cast" || TypeJson.Read(view["type"]) != TypeJson.Read(root["methods"][2]["ret"]))
            throw new InvalidOperationException("Object storage did not convert to the declared reference value slot");
        if (Str(carrierView["body"][0]["value"]["k"]) != "cast")
            throw new InvalidOperationException("A declared carrier value lost its checked native interface view");
        if (Str(exactLocalView["body"][0]["value"]["k"]) != "cast"
            || TypeJson.Read(exactLocalView["body"][0]["value"]["type"]) != TypeJson.Read(exactLocalView["ret"])
            || Str(unrelatedView["body"][0]["value"]["k"]) != "local")
            throw new InvalidOperationException("Local carrier conversion guessed a class or discarded the exact declaration closure");
        if (Str(referencedView["body"][0]["value"]["k"]) != "cast"
            || TypeJson.Read(referencedView["body"][0]["value"]["type"]) != TypeJson.Read(referencedView["ret"]))
            throw new InvalidOperationException("A referenced carrier lost its recorded target declaration");
        if (Str(interfaceView["body"][0]["value"]["k"]) != "cast"
            || TypeJson.Read(interfaceView["body"][0]["value"]["type"]) != TypeJson.Read(interfaceView["ret"]))
            throw new InvalidOperationException("A derived carrier lost its declared generic interface value slot");
        if (Str(blockLocalView["body"][0]["value"]["result"]["k"]) != "cast"
            || TypeJson.Read(blockLocalView["body"][0]["value"]["result"]["type"])
                != TypeJson.Read(blockLocalView["ret"]))
            throw new InvalidOperationException("A value block concealed its physical result from the declared value slot");
        var referenceCall = localReceiver["body"][0]["value"];
        if (TypeJson.Read(referenceCall["recvType"]) != new TypeNode.Fqn("PlainOwner")
            || Str(referenceCall["recv"]["k"]) != "cast"
            || TypeJson.Read(referenceCall["recv"]["type"]) != new TypeNode.Fqn("PlainOwner"))
            throw new InvalidOperationException("A local non-generic declaring class lost its explicit reference receiver");
        var captures = closureCapture["body"][0]["expr"]["captures"];
        if (Str(captures[0]["k"]) != "cast"
            || TypeJson.Read(captures[0]["type"]) != new TypeNode.Tv("method", 0)
            || Str(closureCapture["body"][1]["expr"]["captures"][0]["k"]) != "local")
            throw new InvalidOperationException("Closure captures did not use the exact allocated constructor frame");
        var unusedParameters = JsonNode.Parse("""
          [{"name":"<unused var>","type":{"t":"fqn","name":"object"}},
           {"name":"<unused var>","type":{"t":"fqn","name":"object"}}]
          """)!.AsArray().OfType<JsonObject>().ToArray();
        var emptyBody = new JsonArray();
        var unchanged = new JsonArray(unusedParameters.Select(parameter => parameter.DeepClone()).ToArray()).ToJsonString();
        AllocateNativeLocals(emptyBody, unusedParameters, emptyBody);
        if (emptyBody.Count != 0 || unchanged != new JsonArray(
                unusedParameters.Select(parameter => parameter.DeepClone()).ToArray()).ToJsonString())
            throw new InvalidOperationException("Unused anonymous parameters acquired addressable storage");
        var owningParameters = unusedParameters.Select(BirGraphDom.ReadBirParameter).ToArray();
        var owningBody = new List<IBirStatement>();
        TypedPhysicalValueCoercion.AllocateNativeLocals(Array.Empty<BirGraphValue>(), owningParameters, owningBody);
        if (owningBody.Count != 0 || unchanged != new JsonArray(owningParameters.Select(parameter => (JsonNode)parameter.ToDom()).ToArray()).ToJsonString())
            throw new InvalidOperationException("Owning unused anonymous parameters acquired addressable storage");
        Console.WriteLine("[physical value edges] self-test OK (local generic result, erased result, native carrier view)");
    }

    // Existing authored fixtures enter/leave the owning pass only inside tests.
    // No final DOM rewrite is retained as an alternative production algorithm.
    internal static void ApplyFixtureForSelfTest(IReadOnlyList<JsonNode> roots, Func<JsonObject> unitValue,
        ValueTypeOracle isValue, bool referenceBuild = false, Func<TypeNode.Fqn, bool> isReferenceOwner = null,
        Func<TypeNode.Fqn, bool> isExistentialOwner = null, Func<TypeNode.Fqn, bool> isNativeReference = null,
        Func<TypeNode.Fqn, TypeNode.Fqn, bool> referencedCarrier = null, ReferenceMetadataIndex references = null)
    {
        var owning = roots.Select(BirGraphDom.ReadBirFileDeclaration).ToArray();
        TypedPhysicalValueCoercion.ApplyAll(owning, () => BirGraphDom.ReadBirExpression(unitValue()),
            isValue, referenceBuild, isReferenceOwner, isExistentialOwner, isNativeReference, referencedCarrier, references);
        for (var index = 0; index < roots.Count; index++)
        {
            var result = owning[index].ToDom();
            var fixture = roots[index].AsObject();
            fixture.Clear();
            foreach (var field in result) fixture[field.Key] = field.Value?.DeepClone();
        }
    }

    static JsonObject NormalizeNumericForSelfTest(JsonObject before,
        IReadOnlyDictionary<string, TypeNode> locals, bool referenceBuild)
    {
        var expression = BirGraphDom.ReadBirExpression(before);
        var statement = new BirExpressionStatement { Expr = OptionalField<IBirExpression>.Of(expression) };
        var root = new BirFileDeclaration { FileClass = "NumericValueEdges", Methods = new[] { new BirFunctionDeclaration {
            Name = "probe", Params = locals.Select(local => new BirParameter { Name = local.Key, Type = local.Value }).ToArray(),
            Ret = new TypeNode.Fqn("void"), Body = new IBirStatement[] { statement },
        } } };
        TypedPhysicalValueCoercion.ApplyAll(new[] { root }, () => throw new InvalidOperationException("Unexpected Unit conversion"),
            _ => false, referenceBuild);
        return ((BirGraphValue)statement.Expr.Value).ToDom();
    }

    static void NumericOperandsSelfTest()
    {
        foreach (var referenceBuild in new[] { false, true })
        foreach (var (left, right, target) in new[] {
            ("long", "int", "System.Int64"), ("int", "double", "System.Double"),
            ("uint", "ulong", "System.UInt64"), ("short", "sbyte", "System.Int32"),
            ("char", "ushort", "System.Int32"), ("kotlin.Long", "kotlin.Int", "System.Int64"),
            ("uint", "int", "System.UInt32"), ("int", "uint", "System.Int32"),
        })
        {
            var locals = new Dictionary<string, TypeNode> {
                ["left"] = new TypeNode.Fqn(left), ["right"] = new TypeNode.Fqn(right),
            };
            var operation = new JsonObject {
                ["k"] = "binOp", ["op"] = "+",
                ["lhs"] = new JsonObject { ["k"] = "local", ["name"] = "left" },
                ["rhs"] = new JsonObject { ["k"] = "local", ["name"] = "right" },
            };
            operation = NormalizeNumericForSelfTest(operation, locals, referenceBuild);
            var physicalResult = ResultTypeOracle(Array.Empty<JsonNode>());
            foreach (var side in new[] { "lhs", "rhs" })
                if (BirTypeLowering.CanonicalExpressionResult(physicalResult(operation[side], BirScope.FromVars(locals)))
                    != new TypeNode.Fqn(target))
                    throw new InvalidOperationException($"Mixed arithmetic lost its physical width: {left}/{right}");
            var once = operation.DeepClone();
            operation = NormalizeNumericForSelfTest(operation, locals, referenceBuild);
            if (!JsonNode.DeepEquals(once, operation))
                throw new InvalidOperationException("Numeric operand coercion is not stable on its first result");
            foreach (var shift in new[] { "<<", ">>", ">>>" })
            {
                if (left != "long" || right != "int") continue;
                var shifted = new JsonObject {
                    ["k"] = "binOp", ["op"] = shift,
                    ["lhs"] = new JsonObject { ["k"] = "local", ["name"] = "left" },
                    ["rhs"] = new JsonObject { ["k"] = "local", ["name"] = "right" },
                };
                var before = shifted.DeepClone();
                shifted = NormalizeNumericForSelfTest(shifted, locals, referenceBuild);
                if (!JsonNode.DeepEquals(before, shifted))
                    throw new InvalidOperationException("Numeric coercion rewrote a shift distance");
            }
        }
        foreach (var kind in new[] { "clrStatic", "clrInstance", "callStatic", "callInstance", "constrainedCall" })
        {
            var call = new JsonObject { ["k"] = kind };
            if (kind is "clrStatic" or "clrInstance")
                call["memberRef"] = new JsonObject {
                    ["returnType"] = TypeJson.Write(new TypeNode.ByRef(new TypeNode.Fqn("System.Int32"))),
                };
            else call["calleeRet"] = TypeJson.Fqn("System.Int32");
            var operation = new JsonObject {
                ["k"] = "binOp", ["op"] = "+", ["lhs"] = call,
                ["rhs"] = new JsonObject { ["k"] = "const", ["type"] = TypeJson.Fqn("System.Int64"), ["value"] = 1 },
            };
            operation = NormalizeNumericForSelfTest(operation, new Dictionary<string, TypeNode>(), referenceBuild: false);
            if (Str(operation["lhs"]?["k"]) != "conv"
                || TypeJson.Read(operation["lhs"]?["to"]) != new TypeNode.Fqn("System.Int64")
                || !JsonNode.DeepEquals(operation["lhs"]?["e"], call))
                throw new InvalidOperationException($"Numeric coercion lost the exact copied result of {kind}");
        }
        Console.WriteLine("[numeric operands] self-test OK (widths, stack categories, signed ties, ref mode, shifts, call values)");
    }

}
