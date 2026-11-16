using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class ClrVarianceFacts
{
    static bool Participates(bool isStatic, bool isVirtual, bool isAbstract) => !isStatic || isVirtual || isAbstract;

    // Input adapter to the same variance analysis used by DOM and final CIR.
    // Only current structured physical signatures participate, never frozen
    // Kotlin source facts or executable bodies.
    internal static Declaration FromSource(IBirTypeDeclaration definition)
    {
        var kind = definition.Kind.OrDefault();
        var parameters = definition.TypeParams.OrEmpty();
        var variances = parameters.Select(parameter => parameter.Variance.OrDefault()).ToArray();
        var captured = definition.CapturedTypeParams.OrEmpty().Length;
        if (kind is not ("interface" or "delegate")) return new(kind, captured, variances, []);
        var occurrences = new List<Occurrence>();
        void Add(TypeNode type, int context) { if (type != null) occurrences.Add(new(type, context)); }
        void Signature(BirParameter[] inputs, TypeNode result, BirTypeParameter[] generic)
        {
            foreach (var parameter in inputs) Add(parameter.Type.OrDefault(), -1);
            Add(result, 1);
            foreach (var parameter in generic)
                foreach (var constraint in parameter.Constraints.OrEmpty()) Add(constraint, -1);
        }
        Add(definition.Base.OrDefault(), 1);
        foreach (var type in definition.Interfaces.OrEmpty()) Add(type, 1);
        if (kind == "delegate") Signature(definition.Params.OrEmpty(), definition.Ret.OrDefault(), parameters);
        foreach (var method in definition.Methods.OrEmpty())
            if (Participates(method.Static.OrDefault(), method.Virtual.OrDefault(), method.Abstract.OrDefault()))
                Signature(method.Params.OrEmpty(), method.Ret.OrDefault(), method.TypeParams.OrEmpty());
        return new(kind, captured, variances, occurrences.ToArray());
    }
}
