using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

// Kotlin Any equality and hashing have one meaning regardless of the receiver's static
// type. Native CLR object calls and nonvirtual super calls retain their target.
static class KotlinEqualityLowering
{
    const string Owner = "DotKt.Runtime.CompilerServices.KotlinEqualityKt";
    static string EqualsMethod(bool nullSafe) => nullSafe ? "kotlinEquals" : "kotlinInstanceEquals";
    static TypeNode AnyNullableType() => new TypeNode.Nullable(new TypeNode.Fqn("kotlin.Any"));

    internal static JsonObject Call(JsonNode left, JsonNode right, bool nullSafe) => new()
    {
        ["k"] = "callStatic",
        ["owner"] = TypeJson.Fqn(Owner),
        ["method"] = EqualsMethod(nullSafe),
        ["sig"] = new JsonArray { AnyNullable(), AnyNullable() },
        ["args"] = new JsonArray { left?.DeepClone(), right?.DeepClone() },
    };

    static JsonNode AnyNullable() => TypeNode.Write(AnyNullableType());

    internal static BirCallStatic Call(IBirExpression left, IBirExpression right, bool nullSafe) => new() {
        Owner = new TypeNode.Fqn(Owner), Method = EqualsMethod(nullSafe),
        Sig = new[] { AnyNullableType(), AnyNullableType() }, Args = new[] { left, right },
    };

    static bool IsNull(IBirExpression value) => value is BirConstant constant
        && (!constant.Value.HasValue || constant.Value.Value is BirLiteral.NullLiteral);

    internal static IBirExpression NullComparison(IBirExpression left, IBirExpression right) =>
        IsNull(left) || IsNull(right) ? new BirBinaryOperation {
            Op = "===", Lhs = new BirCast { Type = AnyNullableType(), E = OptionalField<IBirExpression>.Of(left) },
            Rhs = new BirCast { Type = AnyNullableType(), E = OptionalField<IBirExpression>.Of(right) },
        } : null;

    // One scalar decision serves owning source BIR and the remaining late/payload
    // DOM consumers. A preserved super call keeps its semantic slot for rebinding.
    static (bool Consume, string Helper) Select(string kind, string method, bool anySlot, bool superCall, bool preserveSuperIntent)
    {
        if (kind is not ("objMethod" or "callInstance") || method is not ("Equals" or "GetHashCode")
            || kind == "callInstance" && !anySlot || preserveSuperIntent && superCall) return (false, null);
        return (true, superCall ? null : method == "GetHashCode" ? "kotlinInstanceHashCode" : EqualsMethod(false));
    }

    internal static BirGraphValue Apply(BirGraphValue node, bool preserveSuperIntent = false)
    {
        if (node is IBirExecutablePayload) return node;
        node.VisitChildSlots(child => child.Replace(Apply(child.Value, preserveSuperIntent)));
        if (node is BirObjectEquals equality && NullComparison(equality.Lhs.OrDefault(), equality.Rhs.OrDefault()) is { } check)
            return (BirGraphValue)check;
        var (kind, method, anySlot, superCall, receiver, argument) = node switch {
            BirObjectMethod value => (value.Tag, value.Method.OrDefault(), value.AnySlot.OrDefault(), false,
                value.Recv.OrDefault(), value.Arg.OrDefault()),
            BirCallInstance value => (value.Tag, value.Method.OrDefault(), value.AnySlot.OrDefault(), value.Super.OrDefault(),
                value.Recv.OrDefault(), value.Args.HasValue && value.Args.Value.Length > 0 ? value.Args.Value[0] : null),
            _ => ((string)null, null, false, false, (IBirExpression)null, null),
        };
        var selection = Select(kind, method, anySlot, superCall, preserveSuperIntent);
        if (!selection.Consume) return node;
        if (node is BirObjectMethod objectMethod) objectMethod.AnySlot = default;
        else ((BirCallInstance)node).AnySlot = default;
        if (selection.Helper == null) return node;
        return selection.Helper == "kotlinInstanceHashCode" ? new BirCallStatic {
            Owner = new TypeNode.Fqn(Owner), Method = selection.Helper,
            Sig = new[] { AnyNullableType() }, Args = new[] { receiver },
        } : Call(receiver, argument, nullSafe: false);
    }

    static bool IsNull(JsonNode value) => value is JsonObject obj
        && obj["k"]?.GetValue<string>() == "const" && obj["value"] == null;

    internal static JsonObject NullComparison(JsonNode left, JsonNode right) =>
        IsNull(left) || IsNull(right) ? new JsonObject {
            ["k"] = "binOp", ["op"] = "===",
            ["lhs"] = new JsonObject { ["k"] = "cast", ["type"] = AnyNullable(), ["e"] = left?.DeepClone() },
            ["rhs"] = new JsonObject { ["k"] = "cast", ["type"] = AnyNullable(), ["e"] = right?.DeepClone() },
        } : null;

    internal static void Apply(JsonNode node, bool preserveSuperIntent = false)
    {
        if (node is JsonArray array)
        {
            foreach (var child in array) if (child != null) Apply(child, preserveSuperIntent);
            return;
        }
        if (node is not JsonObject obj) return;
        foreach (var child in obj.Select(pair => pair.Value).ToArray()) if (child != null) Apply(child, preserveSuperIntent);
        var kind = obj["k"]?.GetValue<string>();
        // Compiler-authored null guards (including !!) test a reference, not
        // virtual equality. Calling Equals here would observe the receiver twice.
        if (kind == "objEq" && NullComparison(obj["lhs"], obj["rhs"]) is JsonObject check)
        {
            Replace(obj, check);
            return;
        }
        if (kind is not ("objMethod" or "callInstance")) return;
        var method = obj["method"]?.GetValue<string>();
        var selection = Select(kind, method, obj["anySlot"]?.GetValue<bool>() == true,
            obj["super"]?.GetValue<bool>() == true, preserveSuperIntent);
        if (!selection.Consume) return;
        obj.Remove("anySlot");
        if (selection.Helper == null) return;
        if (selection.Helper == "kotlinInstanceHashCode")
        {
            Replace(obj, new JsonObject {
                ["k"] = "callStatic",
                ["owner"] = TypeJson.Fqn(Owner),
                ["method"] = selection.Helper,
                ["sig"] = new JsonArray { AnyNullable() },
                ["args"] = new JsonArray { obj["recv"]?.DeepClone() },
            });
            return;
        }
        var right = kind == "objMethod" ? obj["arg"] : (obj["args"] as JsonArray)?[0];
        var replacement = Call(obj["recv"], right, nullSafe: false);
        Replace(obj, replacement);
    }

    static void Replace(JsonObject obj, JsonObject replacement)
    {
        obj.Clear();
        foreach (var pair in replacement.ToArray()) obj[pair.Key] = pair.Value?.DeepClone();
    }
}
