using System.Text.Json;
using System.Text.Json.Nodes;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class CirDeclarationProjection
{
    public static void SelfTestExecutable()
    {
        JsonObject Local(string name) => new() { ["k"] = "local", ["name"] = name };
        var sourceLocal = Local("receiver");
        sourceLocal["sty"] = TypeJson.Fqn("Consumer");
        var source = new JsonObject {
            ["k"] = "var", ["name"] = "temporary", ["type"] = TypeJson.Fqn("System.UInt64"),
            [CallEvalLowering.ValueTemporaryKey] = true, ["role"] = "argument",
            ["init"] = new JsonObject { ["k"] = "const", ["type"] = TypeJson.Fqn("System.UInt64"),
                ["value"] = -1L },
        };
        var variable = (CirVariable)ReadCirNode(source);
        if (variable.Init.Value is not CirConstant { Value: AttributeArgument.UInt64Value { Value: ulong.MaxValue } }
            || source[CallEvalLowering.ValueTemporaryKey]?.GetValue<bool>() != true
            || source["role"]?.GetValue<string>() != "argument")
            throw new InvalidOperationException("Physical executable projection changed scalar bits or its source tree");
        var encoded = JsonSerializer.Serialize<ICirStatement>(variable, AttributeJson.Options);
        var copy = JsonSerializer.Deserialize<ICirStatement>(encoded, AttributeJson.Options) as CirVariable;
        if (copy?.Init.Value is not CirConstant { Value: AttributeArgument.UInt64Value { Value: ulong.MaxValue } })
            throw new InvalidOperationException("Physical executable constant did not use the typed scalar codec");
        var call = new JsonObject {
            ["k"] = "callInstance", ["method"] = "Read", ["ownerType"] = TypeJson.Fqn("Consumer"),
            ["recv"] = sourceLocal, ["args"] = new JsonArray(Local("temporary")),
            ["clrOwnerResolved"] = true, ["virtual"] = false,
            ["super"] = true, ["static"] = false, ["sty"] = TypeJson.Fqn("System.UInt64"), ["prop"] = "index-get",
            [GenericRepresentationPolicy.PhysicalDeclarationSignatureKey] = true,
            ["calleeParams"] = new JsonArray(TypeJson.Fqn("System.UInt64")),
            ["calleeRet"] = TypeJson.Fqn("System.UInt64"),
        };
        var projected = (CirCallInstance)ReadCirNode(call);
        if (projected.CalleeParams.Value.Single() != new TypeNode.Fqn("System.UInt64")
            || projected.CalleeRet.Value != new TypeNode.Fqn("System.UInt64")
            || projected.Recv is not CirLocal { Name: "receiver" }
            || projected.Args.Single() is not CirLocal { Name: "temporary" }
            || projected.Virtual.Value || call["clrOwnerResolved"]?.GetValue<bool>() != true)
            throw new InvalidOperationException("Executable projection changed exact call facts or evaluation edges");
        using var document = JsonDocument.Parse(JsonSerializer.Serialize<ICirExpression>(projected, AttributeJson.Options));
        if (document.RootElement.TryGetProperty("clrOwnerResolved", out _)
            || document.RootElement.TryGetProperty("static", out _)
            || document.RootElement.TryGetProperty(GenericRepresentationPolicy.PhysicalDeclarationSignatureKey, out _)
            || document.RootElement.TryGetProperty("sty", out _)
            || document.RootElement.TryGetProperty("prop", out _)
            || document.RootElement.TryGetProperty("super", out _)
            || document.RootElement.GetProperty("recv").TryGetProperty("sty", out _)
            || sourceLocal["sty"] is null)
            throw new InvalidOperationException("A consumed bir2cir binding marker reached physical CIR");
        var staticSource = new JsonObject {
            ["k"] = "callStatic", ["method"] = "Read", ["owner"] = TypeJson.Fqn("Consumer"),
            ["sig"] = new JsonArray(TypeJson.Fqn("System.UInt64")),
            ["argTypes"] = new JsonArray(TypeJson.Fqn("System.UInt64")),
            ["dynRet"] = TypeJson.Fqn("System.UInt64"), ["virtual"] = false,
            ["args"] = new JsonArray(Local("temporary")),
        };
        var staticCall = (CirCallStatic)ReadCirNode(staticSource);
        var castSource = new JsonObject {
            ["k"] = "cast", ["type"] = TypeJson.Fqn("System.Object"),
            ["sty"] = TypeJson.Fqn("System.Object"), ["e"] = Local("temporary"),
        };
        var objectSource = new JsonObject {
            ["k"] = "objMethod", ["method"] = "ToString", ["recv"] = Local("receiver"), ["anySlot"] = true,
        };
        if (staticCall.Sig.Value.Single() != new TypeNode.Fqn("System.UInt64")
            || ReadCirNode(castSource) is not CirCast { Type: TypeNode.Fqn { Name: "System.Object" } }
            || ReadCirNode(objectSource) is not CirObjectMethod { Method: "ToString" })
            throw new InvalidOperationException("Consuming source typing facts changed an established physical operation");
        var constrainedSource = new JsonObject {
            ["k"] = "constrainedCall", ["method"] = "Read", ["iface"] = TypeJson.Fqn("ISlot"),
            ["recvType"] = TypeJson.Write(new TypeNode.Tv("method", 0)), ["recv"] = Local("receiver"),
            ["args"] = new JsonArray(), ["argTypes"] = new JsonArray(), ["calleeParams"] = new JsonArray(),
        };
        if (ReadCirNode(constrainedSource) is not CirConstrainedCall { Method: "Read", CalleeParams.Value.Length: 0 })
            throw new InvalidOperationException("Consuming argument source types changed a constrained call's physical signature");
        var closureSource = JsonNode.Parse("""
            {"k":"newClosure","closureType":{"t":"fqn","name":"Closure"},"method":"invoke","captures":[],
             "funcType":{"t":"fn","suspend":false,"ret":{"t":"fqn","name":"void"},"params":[],"clr":"System.Action"},
             "calleeParams":[],"delegateCtorRef":{"kind":"ctor","assembly":"System.Runtime",
              "declaringType":{"t":"fqn","name":"System.Action"},"name":".ctor","genericArity":0,
              "returnType":{"t":"fqn","name":"void"},"callingConvention":"instance",
              "parameterTypes":[{"t":"fqn","name":"System.Object"},{"t":"fqn","name":"System.IntPtr"}]}}
            """)!;
        if (ReadCirNode(closureSource) is not CirClosure { Method: "invoke", Captures.Length: 0,
            ClosureType: TypeNode.Fqn { Name: "Closure" }, DelegateCtorRef.Name: ".ctor" })
            throw new InvalidOperationException("Consuming a closure binding signature changed its physical construction");
        foreach (var method in new[] { "ToString", "GetHashCode", "Equals" })
        {
            var enumCall = new JsonObject {
                ["k"] = "callInstance", ["ownerType"] = TypeJson.Fqn("SampleEnum"), ["method"] = method,
                ["recv"] = Local("receiver"),
                ["args"] = method == "Equals" ? new JsonArray(Local("other")) : new JsonArray(),
                ["argTypes"] = method == "Equals" ? new JsonArray(TypeJson.Fqn("System.Object")) : new JsonArray(),
            };
            EnumMemberBinding.Apply(enumCall, new HashSet<string> { "SampleEnum" });
            var enumSlot = (CirObjectMethod)ReadCirNode(enumCall);
            if (enumSlot.Method != method || enumSlot.Recv is not CirLocal { Name: "receiver" }
                || (method == "Equals" && enumSlot.Arg.Value is not CirLocal { Name: "other" }))
                throw new InvalidOperationException("Enum object-slot binding changed an operand or retained call-only facts");
        }
        var metadataOnly = new CirFileDeclaration {
            FileClass = "ReferenceLiteral", HasMain = false, Types = [], Methods = [],
            Fields = new[] { new CirField {
                Name = "value", Type = new TypeNode.Fqn("kotlin.Double"),
                Init = new CirConstant { Type = new TypeNode.Fqn("kotlin.Double"), Value = new AttributeArgument.Float64Value(BitConverter.DoubleToInt64Bits(1.0)) },
            } },
        };
        var metadataPlaceholders = RefBodySquash.LowerLiteralPlaceholders(metadataOnly);
        if (metadataPlaceholders.Fields[0].Init.Value is not CirConstant {
            Type: TypeNode.Fqn { Name: "System.Object" }, Value: AttributeArgument.NullValue })
            throw new InvalidOperationException("A metadata-only facade literal became a native runtime value");
        var malformedOptional = new JsonObject {
            ["k"] = "callInstance", ["method"] = "Read", ["ownerType"] = TypeJson.Fqn("Consumer"),
            ["recv"] = Local("receiver"), ["args"] = new JsonArray(), ["virtual"] = null,
        };
        try
        {
            ReadCirNode(malformedOptional);
            throw new InvalidOperationException("An explicit null became a physical dispatch boolean");
        }
        catch (FormatException) { }
        var suspendType = JsonNode.Parse("""{"t":"fn","suspend":true,"params":[],"ret":{"t":"fqn","name":"kotlin.Int"}}""")!;
        foreach (var stashed in new[] { false, true })
        foreach (var expression in new JsonObject[] {
            new() { ["k"] = "cond", ["type"] = suspendType.DeepClone(), ["cond"] = Local("condition"),
                ["then"] = Local("first"), ["else"] = Local("second") },
            new() { ["k"] = "callStatic", ["method"] = "keep", ["ret"] = suspendType.DeepClone(),
                ["args"] = new JsonArray(Local("value")) },
            new() { ["k"] = "callStatic", ["method"] = "fail", ["ret"] = TypeJson.Fqn("kotlin.Nothing"),
                ["args"] = new JsonArray() },
            new() { ["k"] = "var", ["name"] = "block", ["type"] = suspendType.DeepClone(), ["init"] = Local("value") },
            new() { ["k"] = "cast", ["type"] = suspendType.DeepClone(), ["e"] = Local("value") },
        })
        {
            if (stashed)
                expression[expression.ContainsKey("type") ? NullableGenericErasure.SuspendFnPre
                    : NullableGenericErasure.RetSuspendFnPre] = suspendType.ToJsonString();
            var lowered = BirTypeLowering.Lower(expression, refBuild: false,
                aliases: new Dictionary<string, string> { ["kotlin.Nothing"] = "object" });
            var node = ReadCirNode(lowered);
            if (node.Tag != expression["k"]!.GetValue<string>()
                || lowered.AsObject().ContainsKey("suspendFnType")
                || lowered.AsObject().ContainsKey("retSuspendFnType")
                || lowered.AsObject().ContainsKey("retNothing"))
                throw new InvalidOperationException("Executable type lowering produced declaration-only positional metadata");
        }
        var interopReferences = ReferenceMetadataIndex.Build(new[] {
            typeof(object).Assembly.Location, typeof(System.Numerics.Vector3).Assembly.Location,
        }.Distinct().ToArray());
        foreach (var add in new[] { false, true })
        foreach (var local in new[] { false, true })
        {
            var accessor = add ? "add_ProcessExit" : "remove_ProcessExit";
            var operation = new JsonObject {
                ["k"] = add ? "clrEventAdd" : "clrEventRemove", ["type"] = TypeJson.Fqn("System.AppDomain"),
                ["event"] = "ProcessExit", ["static"] = false, ["recv"] = Local("receiver"),
                ["handler"] = Local("handler"), ["handlerExact"] = true,
            };
            if (local)
            {
                operation["localAccessor"] = true;
                operation["accessor"] = accessor;
                operation["accessorOwner"] = TypeJson.Fqn("LocalOwner");
                operation["delegateType"] = TypeJson.Fqn("System.EventHandler");
                operation["sig"] = new JsonArray(TypeJson.Fqn("System.EventHandler"));
                operation["dispatch"] = "callvirt";
                operation[DeclarationIdentityBinding.Key] = "selected-event-accessor";
                operation["memberVisibility"] = "public";
            }
            ClrMemberResolution.Apply(operation, interopReferences, new HashSet<string>());
            var typedOperation = (BirGraphValue)BirGraphDom.ReadBirExpression(operation);
            ForeignNullableGenericCrossing.Check(typedOperation, "event-boundary");
            var projectedEvent = (ICirExpression)new TypedCirProjection(new HashSet<string>()).ProjectNode(typedOperation);
            using var physicalEvent = JsonDocument.Parse(JsonSerializer.Serialize(projectedEvent, AttributeJson.Options));
            var value = physicalEvent.RootElement;
            if (value.GetProperty("accessor").GetString() != accessor
                || value.GetProperty("dispatch").GetString() != (local ? "callvirt" : "call")
                || value.GetProperty("handlerExact").GetBoolean() != true
                || (local ? value.GetProperty("sig").GetArrayLength() != 1
                    : value.GetProperty("memberRef").GetProperty("name").GetString() != accessor))
                throw new InvalidOperationException("Event projection lost its exact physical accessor or handler");
            foreach (var fact in new[] { "event", "delegateType", DeclarationIdentityBinding.Key, "memberVisibility" })
                if (value.TryGetProperty(fact, out _))
                    throw new InvalidOperationException("Event binding source facts reached physical CIR");
        }
        foreach (var result in new[] { "System.Text.StringBuilder", "System.Single" })
        {
            var generic = result == "System.Text.StringBuilder";
            IBirExpression expression = generic ? new BirCallStatic {
                OwnerType = new TypeNode.Fqn("System.Activator"), Method = "CreateInstance",
                Ret = new TypeNode.Fqn(result), TypeArgs = new TypeNode[] { new TypeNode.Fqn(result) },
                Args = Array.Empty<IBirExpression>(),
            } : new BirFieldGet {
                OwnerType = new TypeNode.Fqn("System.Numerics.Vector3"), Name = "X",
                Ret = new TypeNode.Fqn(result), Recv = new BirLocal { Name = "vector" },
            };
            var declaration = new BirFunctionDeclaration {
                Name = "nativeResult", Body = new IBirStatement[] {
                    new BirExpressionStatement { Expr = OptionalField<IBirExpression>.Of(expression) },
                },
            };
            NetInteropBinding.Apply(new BirFileDeclaration { Methods = new[] { declaration } }, interopReferences);
            var bound = (BirGraphNode)((BirExpressionStatement)declaration.Body.Value.Single()).Expr.Value;
            if (bound.Tag != (generic ? "clrGenericStatic" : "clrPropGet")
                || TypedCallEvalLowering.StaticTypeOf(bound) != new TypeNode.Fqn(result))
                throw new InvalidOperationException("Interop reshaping lost the intermediate result fact before operand planning");
        }
        Console.WriteLine("[CIR executable boundary] self-test OK (scalar widths, source handoffs, exact calls, executable type lowering)");
    }
}
