using System.Text.Json.Nodes;
using DotKt.Bir;
using DotKt.Bir.Lowering;

// Opaque executable BIR crosses an ownership boundary in KotlinInline/KotlinDefault carriers. The producer stores
// frontend vocabulary intentionally; the consuming bir2cir instance must put that payload through the same
// representation-entry contract as source BIR before any payload-specific rewrite inspects or clones it.
//
// Keep this chokepoint small and ordered. A new representation-entry invariant belongs here when it must hold for
// both inline and default payloads; callers must not repair a materialized payload later with an idempotent file-wide
// sweep. Normal source BIR takes the same first step before Program's source helper recognition.
static partial class MaterializedBirPayload
{
    // Defaults nested on inline parameters use the same versioned byte-array carrier.
    public static void HideDefaultSourceSpans(JsonObject obj)
    {
        if (DefaultBirCarrier.IsCarrier(obj)) DefaultBirCarrier.RewriteTyped(obj, expression => {
            HideSourceSpans((BirGraphValue)expression);
            return expression;
        });
    }

    public static void SelfTest()
    {
        BirReturn AnyCall(string name, bool superCall = false) => new() {
            Value = new BirCallInstance {
                Method = name, AnySlot = true, Super = superCall,
                Recv = new BirLocal { Name = "value" },
                Args = name == "equals" ? new IBirExpression[] { new BirLocal { Name = "other" } }
                    : Array.Empty<IBirExpression>(),
            },
        };
        var payload = new BirInlinePayload {
            Body = new IBirStatement[] {
                AnyCall("toString"), AnyCall("equals"), AnyCall("hashCode"), AnyCall("equals", superCall: true),
            },
            Lifted = new[] {
                new BirFunctionDeclaration { Name = "hashCode", ObjectOverride = true },
            },
        };

        Normalize(payload);
        IBirExpression Result(int index) => ((BirReturn)payload.Body.Value[index]).Value.OrDefault();
        bool IsEqualityHelper(int index, string method) => Result(index) is BirCallStatic result
            && result.Owner.OrDefault() is TypeNode.Fqn { Name: "DotKt.Runtime.CompilerServices.KotlinEqualityKt" }
            && result.Method.OrDefault() == method && !result.AnySlot.IsPresent;
        if (Result(0) is not BirCallInstance call || call.Method.OrDefault() != "ToString"
            || !call.AnySlot.OrDefault() || payload.Lifted.Value[0].Name.OrDefault() != "GetHashCode"
            || !IsEqualityHelper(1, "kotlinInstanceEquals") || !IsEqualityHelper(2, "kotlinInstanceHashCode")
            || Result(3) is not BirCallInstance superCall || superCall.Method.OrDefault() != "Equals"
            || !superCall.AnySlot.OrDefault() || !superCall.Super.OrDefault())
            throw new InvalidOperationException("materialized BIR payload normalization self-test failed");

        var first = payload.DeepClone();
        Normalize(payload);
        if (!payload.StructurallyEquals(first))
            throw new InvalidOperationException("materialized BIR payload normalization is not idempotent");
    }
}
