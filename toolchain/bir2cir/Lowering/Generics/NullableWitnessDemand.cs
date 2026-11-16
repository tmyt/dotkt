using System;
using System.Collections.Generic;
using System.Linq;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

// Computes the CLR-only demand for a nullable-instantiation witness.  Kotlin `reified` is deliberately absent from
// this analysis: it is a source/declaration fact, whereas a witness is needed only when a nullable-sensitive operation
// consumes a type variable.  Demand flows backwards through exact declaration identities and through the explicit
// positional type-argument correspondence on every lifted/materialized frame.
// Before the fixed point, private source-graph snapshots materialize actually omitted defaults, including
// callInline slots. Those operations belong to their omitting callers, not unconditionally to their declarations.
sealed partial class NullableWitnessDemand
{
    readonly IReadOnlyDictionary<string, int[]> _localDeclarations;
    readonly ReferenceMetadataIndex _refs;

    NullableWitnessDemand(IReadOnlyDictionary<string, int[]> localDeclarations, ReferenceMetadataIndex refs)
    {
        _localDeclarations = localDeclarations;
        _refs = refs;
    }

    public IReadOnlyDictionary<string, int[]> LocalDeclarations => _localDeclarations;

    public static void SelfTest()
    {
        var directId = "selftest:direct";
        var forwardId = "selftest:forward";
        var unusedId = "selftest:unused";
        var boolean = new TypeNode.Fqn("kotlin.Boolean");
        var variable = new TypeNode.Tv("method", 0);
        BirConstant Null() => new() { Type = new TypeNode.Nullable(new TypeNode.Fqn("kotlin.Nothing")), Value = new BirLiteral.NullLiteral() };
        BirFunctionDeclaration Method(string name, string id, IBirExpression value) => new() {
            Name = name, DeclarationId = id, Static = true, TypeParams = new[] { new BirTypeParameter { Name = "T", Reified = true } },
            Params = Array.Empty<BirParameter>(), Ret = boolean,
            Body = new IBirStatement[] { new BirReturn { Value = OptionalField<IBirExpression>.Of(value) } },
        };
        BirCallStatic Forward(TypeNode argument) => new() {
            OwnerType = new TypeNode.Fqn("WitnessSelfTest"), Method = "direct", DeclarationId = directId,
            TypeArgs = new[] { argument }, Args = Array.Empty<IBirExpression>(), Sig = Array.Empty<TypeNode>(), Ret = boolean,
        };
        var root = new BirFileDeclaration {
            FileClass = "WitnessSelfTest", Types = Array.Empty<IBirTypeDeclaration>(),
            Methods = new[] {
                Method("direct", directId, new BirIsInstance { Type = variable, E = Null() }),
                Method("forward", forwardId, Forward(variable)),
                Method("unused", unusedId, new BirConstant { Type = boolean, Value = new BirLiteral.BooleanLiteral(true) }),
                Method("safe", "selftest:safe", new BirInstanceOrNull { Type = variable, ReifiedTypeOperand = true, E = Null() }),
                Method("unchecked", "selftest:unchecked", new BirInstanceOrNull { Type = variable, ReifiedTypeOperand = false, E = Null() }),
                Method("nullableForward", "selftest:nullable-forward", Forward(new TypeNode.Nullable(variable))),
            },
        };
        root.Methods.Value[3].Ret = new TypeNode.Nullable(variable);
        root.Methods.Value[4].Ret = new TypeNode.Nullable(variable);
        var withMarkers = Collect(new[] { root }, null).LocalDeclarations;
        var withoutMarkersRoot = (BirFileDeclaration)root.DeepClone();
        withoutMarkersRoot.Visit(node => { if (node is BirTypeParameter parameter) parameter.Reified = default; });
        var withoutMarkers = Collect(new[] { withoutMarkersRoot }, null).LocalDeclarations;
        if (!withMarkers[directId].SequenceEqual(new[] { 0 })
            || !withMarkers[forwardId].SequenceEqual(new[] { 0 })
            || withMarkers[unusedId].Length != 0
            || !withMarkers["selftest:safe"].SequenceEqual(new[] { 0 })
            || withMarkers["selftest:unchecked"].Length != 0
            || !withMarkers["selftest:nullable-forward"].SequenceEqual(new[] { 0 })
            || withMarkers.Any(entry => !entry.Value.SequenceEqual(withoutMarkers[entry.Key])))
            throw new InvalidOperationException(
                "nullable-witness demand self-test failed: demand depends on Kotlin reified markers or call propagation");
        TypedDefaultDemandSelfTest();
    }

    static IReadOnlyDictionary<string, int[]> Freeze(
        IReadOnlyDictionary<string, HashSet<int>> source) => source.ToDictionary(
            entry => entry.Key,
            entry => entry.Value.OrderBy(index => index).ToArray(),
            StringComparer.Ordinal);

    static int[] ResolveFramePositions(
        IEnumerable<TypeVariable> variables,
        IReadOnlyList<TypeNode> typeArguments,
        string context,
        bool dense)
    {
        var positions = new HashSet<int>();
        foreach (var variable in variables)
        {
            if (dense)
            {
                if (variable.Index < 0 || variable.Index >= typeArguments.Count)
                    throw new InvalidOperationException(
                        $"bir2cir: {context} has no dense type argument at nullable-witness position {variable.Index}");
                positions.Add(variable.Index);
                continue;
            }

            var matches = typeArguments.Select((argument, index) => (argument, index))
                .Where(entry => entry.argument is TypeNode.Tv tv
                    && tv.Scope == variable.Scope && tv.I == variable.Index)
                .Select(entry => entry.index)
                .ToArray();
            if (matches.Length != 1)
                throw new InvalidOperationException(
                    $"bir2cir: {context} maps nullable-sensitive {variable.Scope} type parameter {variable.Index} " +
                    $"to {matches.Length} materialized type arguments");
            positions.Add(matches[0]);
        }
        return positions.OrderBy(index => index).ToArray();
    }

    readonly record struct TypeVariable(string Scope, int Index);
}
