using DotKt.Bir;
using DotKt.Cir;

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

    public static CirFileDeclaration SynthDefsFile()
    {
        var types = new List<CirTypeDeclaration>();
        for (var arity = BirTypeLowering.CanonicalDelegateMinArity;
             arity <= BirTypeLowering.CanonicalDelegateMaxArity;
             arity++)
        {
            types.Add(Delegate("KAction", arity, returnsValue: false));
            types.Add(Delegate("KFunc", arity, returnsValue: true));
        }

        return new CirFileDeclaration
        {
            FileClass = "", HasMain = false, Methods = [], Fields = [], Types = types.ToArray(),
        };
    }

    static CirTypeDeclaration Delegate(string family, int kotlinArity, bool returnsValue)
    {
        var genericArity = kotlinArity + (returnsValue ? 1 : 0);
        var typeParams = new List<CirTypeParameter>();
        var parameters = new List<CirParameter>();
        for (var i = 0; i < kotlinArity; i++)
        {
            typeParams.Add(new CirTypeParameter { Name = "T" + (i + 1), Variance = "in" });
            parameters.Add(new CirParameter
            {
                Name = "p" + (i + 1), Type = new TypeNode.Tv("type", i),
            });
        }
        if (returnsValue) typeParams.Add(new CirTypeParameter { Name = "TResult", Variance = "out" });

        return new CirTypeDeclaration
        {
            // This is an exact CLR metadata identity, including arity. Unlike Kotlin classifiers, the CLR permits
            // KFunc`18 ... KFunc`23 to share one unsuffixed source name, so the physical CIR name stays explicit.
            Name = Ns + family + "`" + genericArity, Kind = CirTypeKind.Delegate,
            Vis = "public", Generated = true, TypeParams = typeParams.ToArray(), Params = parameters.ToArray(),
            Ret = returnsValue ? new TypeNode.Tv("type", genericArity - 1) : new TypeNode.Fqn("System.Void"),
            Interfaces = Array.Empty<TypeNode>(), Fields = Array.Empty<CirField>(),
            Ctors = Array.Empty<CirConstructor>(), Methods = Array.Empty<CirMethod>(),
        };
    }
}
