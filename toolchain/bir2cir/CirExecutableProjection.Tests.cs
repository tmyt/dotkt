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
            ["super"] = true, ["retNothing"] = false,
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
            || document.RootElement.TryGetProperty("retNothing", out _)
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
        Console.WriteLine("[CIR executable boundary] self-test OK (scalar widths, source handoffs, exact calls, reusable input)");
    }
}
