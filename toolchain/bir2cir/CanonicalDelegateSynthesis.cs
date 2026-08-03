using System.Text.Json.Nodes;
using DotKt.Bir;

// #220 — materialize the CLR declarations selected by BirTypeLowering for Kotlin function arities 17..22.
// The family is part of the stdlib ABI, so bir2cir (the Kotlin-semantics -> CLR-representation owner) authors
// every physical fact in CIR. ilemit only realizes kind:"delegate"; it does not choose the family, its range,
// names, generic variance, parameter vector, or return position.
static class CanonicalDelegateSynthesis
{
    const string Ns = "DotKt.Runtime.CompilerServices.";
    // Sort after source CIR. The former ilemit-owned synthesis defined this family after all ordinary TypeDefs;
    // preserving that metadata order avoids turning an ownership move into unrelated stdlib token churn.
    public const string OutputName = "zzz-dotkt-wide-delegates.cir.json";

    public static JsonObject SynthDefsFile()
    {
        var types = new JsonArray();
        for (var arity = BirTypeLowering.CanonicalDelegateMinArity;
             arity <= BirTypeLowering.CanonicalDelegateMaxArity;
             arity++)
        {
            types.Add(Delegate("KAction", arity, returnsValue: false));
            types.Add(Delegate("KFunc", arity, returnsValue: true));
        }

        return new JsonObject
        {
            ["fileClass"] = "",
            ["hasMain"] = false,
            ["methods"] = new JsonArray(),
            ["fields"] = new JsonArray(),
            ["types"] = types,
        };
    }

    static JsonObject Delegate(string family, int kotlinArity, bool returnsValue)
    {
        var genericArity = kotlinArity + (returnsValue ? 1 : 0);
        var typeParams = new JsonArray();
        var parameters = new JsonArray();
        for (var i = 0; i < kotlinArity; i++)
        {
            typeParams.Add(TypeParam("T" + (i + 1), "in"));
            parameters.Add(new JsonObject
            {
                ["name"] = "p" + (i + 1),
                ["type"] = TypeJson.Write(new TypeNode.Tv("type", i)),
            });
        }
        if (returnsValue) typeParams.Add(TypeParam("TResult", "out"));

        return new JsonObject
        {
            // This is an exact CLR metadata identity, including arity. Unlike Kotlin classifiers, the CLR permits
            // KFunc`18 ... KFunc`23 to share one unsuffixed source name, so the physical CIR name stays explicit.
            ["name"] = Ns + family + "`" + genericArity,
            ["kind"] = "delegate",
            ["vis"] = "public",
            ["generated"] = true,
            ["typeParams"] = typeParams,
            ["params"] = parameters,
            ["ret"] = returnsValue
                ? TypeJson.Write(new TypeNode.Tv("type", genericArity - 1))
                : TypeJson.Fqn("System.Void"),
            ["interfaces"] = new JsonArray(),
            ["fields"] = new JsonArray(),
            ["ctors"] = new JsonArray(),
            ["methods"] = new JsonArray(),
        };
    }

    static JsonObject TypeParam(string name, string variance) => new()
    {
        ["name"] = name,
        ["variance"] = variance,
    };
}
