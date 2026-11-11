using System.Text.Json;
using System.Text.Json.Nodes;
using DotKt.Bir;
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
            ["super"] = true,
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
            || document.RootElement.TryGetProperty("super", out _)
            || document.RootElement.GetProperty("recv").TryGetProperty("sty", out _)
            || sourceLocal["sty"] is null)
            throw new InvalidOperationException("A consumed bir2cir binding marker reached physical CIR");
        var metadataOnly = new JsonObject {
            ["fields"] = new JsonArray(new JsonObject {
                ["init"] = new JsonObject { ["k"] = "const", ["type"] = TypeJson.Fqn("kotlin.Double"), ["value"] = 1.0 }
            }),
        };
        RefBodySquash.LowerLiteralPlaceholders(metadataOnly);
        if (ReadCirNode(metadataOnly["fields"]![0]!["init"]!) is not CirConstant {
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
        foreach (var result in new[] { "System.Text.StringBuilder", "System.Single" })
        {
            var generic = result == "System.Text.StringBuilder";
            var expression = new JsonObject {
                ["k"] = generic ? "callStatic" : "field",
                ["ownerType"] = TypeJson.Fqn(generic ? "System.Activator" : "System.Numerics.Vector3"),
                [generic ? "method" : "name"] = generic ? "CreateInstance" : "X",
                ["ret"] = TypeJson.Fqn(result),
            };
            if (generic)
            {
                expression["typeArgs"] = new JsonArray(TypeJson.Fqn(result));
                expression["args"] = new JsonArray();
            }
            else expression["recv"] = Local("vector");
            NetInteropBinding.Apply(expression, interopReferences);
            if (expression["k"]!.GetValue<string>() != (generic ? "clrGenericStatic" : "clrPropGet")
                || NodeType.Of(expression) != new TypeNode.Fqn(result))
                throw new InvalidOperationException("Interop reshaping lost the intermediate result fact before operand planning");
        }
        Console.WriteLine("[CIR executable boundary] self-test OK (scalar widths, source handoffs, exact calls, executable type lowering)");
    }
}
