using System;
using System.Collections.Generic;
using System.Linq;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

sealed partial class NullableWitnessDemand
{
    internal static NullableWitnessDemand Collect(IReadOnlyList<BirFileDeclaration> sources, ReferenceMetadataIndex references,
        ReflectedDeclarationParameters declarationsOfParameters = null)
    {
        var declarations = new TypedInlineBirIndex(sources);
        var roots = references == null ? sources.ToArray()
            : sources.Select(source => DefaultArgSplice.CreateDemandView(source, references, declarations)).ToArray();
        var methods = new Dictionary<string, BirFunctionDeclaration>(StringComparer.Ordinal);
        foreach (var owner in roots.SelectMany(TypedOwners))
            foreach (var method in TypedMethods(owner))
                if (method.DeclarationId.HasValue && !methods.TryAdd(method.DeclarationId.Value, method)
                    && !ReferenceEquals(methods[method.DeclarationId.Value], method))
                    throw new InvalidOperationException($"bir2cir: duplicate declaration identity '{method.DeclarationId.Value}' in nullable-witness analysis");
        var ordinary = Solve(false);
        bool usesReflection = false;
        foreach (var root in roots)
            root.Visit(node => {
                if (usesReflection) return;
                var target = node is BirCallInline inline ? inline.DeclarationId.OrDefault() : node.DeclarationIdentityFact.OrDefault();
                usesReflection = node is BirReflectedType || target != null && references?.ReflectedWitnessIndices(target) is { Length: > 0 };
            });
        if (!usesReflection) return ordinary;
        var reflected = Solve(true);
        return new NullableWitnessDemand(Join(ordinary.LocalDeclarations, reflected.LocalDeclarations), references) { Reflected = reflected };

        NullableWitnessDemand Solve(bool reflection)
        {
            var demands = methods.Keys.ToDictionary(identity => identity, _ => new HashSet<int>(), StringComparer.Ordinal);
            bool changed;
            do
            {
                changed = false;
                var analyzer = new NullableWitnessDemand(Freeze(demands), references) { reflectedOnly = reflection };
                var generated = roots.Select(analyzer.TypedGeneratedFrames).ToArray();
                foreach (var (identity, method) in methods)
                    foreach (var index in analyzer.AnalyzeTyped(method, generated).Where(variable => variable.Scope == "method")
                        .Select(variable => variable.Index).Distinct().OrderBy(index => index))
                    {
                        if (index < 0 || index >= method.TypeParams.OrEmpty().Length)
                            throw new InvalidOperationException($"bir2cir: nullable-witness demand for declaration '{identity}' names method type parameter {index}, but its generic arity is {method.TypeParams.OrEmpty().Length}");
                        if (reflection && declarationsOfParameters?.Method(identity) is { } declared &&
                            !declared.Parameters[index].Reified.OrDefault()) continue;
                        if (demands[identity].Add(index)) changed = true;
                    }
            } while (changed);
            return new NullableWitnessDemand(Freeze(demands), references) { reflectedOnly = reflection };
        }
    }

    static IReadOnlyDictionary<string, int[]> Join(IReadOnlyDictionary<string, int[]> a, IReadOnlyDictionary<string, int[]> b) =>
        a.ToDictionary(pair => pair.Key, pair => pair.Value.Union(b.GetValueOrDefault(pair.Key) ?? []).Order().ToArray(), StringComparer.Ordinal);

    static IEnumerable<BirGraphValue> TypedOwners(BirGraphValue owner)
    {
        yield return owner;
        if (owner is BirFileDeclaration file)
            foreach (var child in file.Types.OrEmpty()) yield return (BirGraphValue)child;
    }

    internal static BirFunctionDeclaration[] TypedMethods(BirGraphValue owner) => owner switch {
        BirFileDeclaration file => file.Methods.OrEmpty(), BirClassDefinition type => type.Methods.OrEmpty(),
        BirInterfaceDefinition type => type.Methods.OrEmpty(), BirEnumDefinition type => type.Methods.OrEmpty(),
        _ => Array.Empty<BirFunctionDeclaration>(),
    };

    internal sealed record TypedFrameDemands(IReadOnlyDictionary<string, int[]> Types, IReadOnlyDictionary<string, int[]> Targets,
        TypedFrameDemands Reflected = null);

    internal TypedFrameDemands AnalyzeGeneratedFrames(BirFileDeclaration file)
    {
        var frames = TypedGeneratedFrames(file);
        if (Reflected == null) return frames;
        var reflected = Reflected.TypedGeneratedFrames(file);
        return new(Join(frames.Types, reflected.Types), Join(frames.Targets, reflected.Targets), reflected);
    }

    internal int[] MaterializedFrameIndices(BirGraphValue frame, TypeNode[] typeArguments,
        TypedFrameDemands generated, bool dense = false) => ResolveFramePositions(
            AnalyzeTyped(frame, new[] { generated }).Union(Reflected?.AnalyzeTyped(frame, new[] { generated.Reflected }) ?? []), typeArguments, "materialized frame",
            dense || ClosureSynthesis.HasPreboundFrame(frame));

    TypedFrameDemands TypedGeneratedFrames(BirFileDeclaration file)
    {
        var referencedTargets = new HashSet<string>(StringComparer.Ordinal);
        file.Visit(node => {
            if (node is BirNewDelegate { Method.HasValue: true } target) referencedTargets.Add(target.Method.Value);
        });
        var types = new Dictionary<string, (BirGraphValue Node, int Arity)>(StringComparer.Ordinal);
        var targets = new Dictionary<string, BirFunctionDeclaration>(StringComparer.Ordinal);
        foreach (var owner in TypedOwners(file))
        {
            foreach (var method in TypedMethods(owner))
                if (method.Generated.OrDefault() && method.Name.HasValue && referencedTargets.Contains(method.Name.Value)
                    && !targets.TryAdd(method.Name.Value, method) && !ReferenceEquals(targets[method.Name.Value], method))
                    throw new InvalidOperationException($"bir2cir: ambiguous generated delegate target '{method.Name.Value}'");
            var fact = owner switch {
                BirClassDefinition type => (type.Generated.OrDefault(), type.Name.OrDefault(), type.TypeParams.OrEmpty().Length),
                BirInterfaceDefinition type => (type.Generated.OrDefault(), type.Name.OrDefault(), type.TypeParams.OrEmpty().Length),
                BirEnumDefinition type => (type.Generated.OrDefault(), type.Name.OrDefault(), type.TypeParams.OrEmpty().Length),
                _ => (false, (string)null, 0),
            };
            if (fact.Item1 && fact.Item2 != null && !types.TryAdd(fact.Item2, (owner, fact.Item3))
                && !ReferenceEquals(types[fact.Item2].Node, owner))
                throw new InvalidOperationException($"bir2cir: ambiguous generated type '{fact.Item2}'");
        }
        var typeDemands = types.Keys.ToDictionary(name => name, _ => new HashSet<int>(), StringComparer.Ordinal);
        var targetDemands = targets.Keys.ToDictionary(name => name, _ => new HashSet<int>(), StringComparer.Ordinal);
        bool changed;
        do
        {
            changed = false;
            var frame = new TypedFrameDemands(Freeze(typeDemands), Freeze(targetDemands));
            foreach (var (name, type) in types)
                foreach (var index in AnalyzeTyped(type.Node, new[] { frame }).Where(variable => variable.Scope == "type")
                    .Select(variable => variable.Index).Distinct())
                {
                    if (index < 0 || index >= type.Arity)
                        throw new InvalidOperationException($"bir2cir: generated type '{name}' demands generic position {index}, but its generic arity is {type.Arity}");
                    if (typeDemands[name].Add(index)) changed = true;
                }
            foreach (var (name, target) in targets)
                foreach (var index in AnalyzeTyped(target, new[] { frame }).Where(variable => variable.Scope == "method")
                    .Select(variable => variable.Index).Distinct())
                {
                    if (index < 0 || index >= target.TypeParams.OrEmpty().Length)
                        throw new InvalidOperationException($"bir2cir: generated delegate target '{name}' demands generic position {index}, but its generic arity is {target.TypeParams.OrEmpty().Length}");
                    if (targetDemands[name].Add(index)) changed = true;
                }
        } while (changed);
        return new(Freeze(typeDemands), Freeze(targetDemands));
    }

    HashSet<TypeVariable> AnalyzeTyped(BirGraphValue root, IReadOnlyList<TypedFrameDemands> frames)
    {
        var required = new HashSet<TypeVariable>();
        void Require(TypeNode operand)
        {
            if (operand == null) return;
            if (reflectedOnly)
                BirGraphNameFields.VisitTypeNodes(operand, node => {
                    if (node is TypeNode.Tv tv) required.Add(new(tv.Scope, tv.I));
                });
            else if (KotlinTypeWitness.Variable(operand) is { } tv) required.Add(new(tv.Scope, tv.I));
        }
        int[] GeneratedDemand(string name, bool target) => frames.Select(frame =>
            (target ? frame.Targets : frame.Types).TryGetValue(name, out var demand) ? demand : null)
            .FirstOrDefault(demand => demand != null) ?? Array.Empty<int>();
        void Map(IEnumerable<int> positions, IReadOnlyList<TypeNode> arguments, string context)
        {
            foreach (var index in positions)
            {
                if (index < 0 || index >= arguments.Count)
                    throw new InvalidOperationException($"bir2cir: {context} has no type argument at demanded nullable-witness position {index}");
                Require(arguments[index]);
            }
        }
        void Frame(BirGraphValue declaration, TypeNode[] arguments, TypeNode[] declarationArguments, string context, bool dense)
            => Map(ResolveFramePositions(AnalyzeTyped(declaration, frames), declarationArguments ?? arguments, context, dense), arguments, context);
        void Walk(BirGraphValue node)
        {
            if (node == null || node is IBirExecutablePayload) return;
            switch (node)
            {
                case BirNewClosure { SynthClass.HasValue: true } closure:
                    foreach (var capture in closure.Captures.OrEmpty()) Walk((BirGraphValue)capture);
                    Frame(closure.SynthClass.Value, closure.TypeArgs.OrEmpty(), null,
                        $"closure '{closure.SynthClass.Value.Name.OrDefault()}'", closure.SynthClass.Value._syntheticFrameBound.HasValue);
                    return;
                case BirNewSam { SynthClass.HasValue: true } sam:
                    foreach (var capture in sam.Captures.OrEmpty()) Walk((BirGraphValue)capture);
                    Frame(sam.SynthClass.Value, sam.TypeArgs.OrEmpty(), null,
                        $"SAM '{sam.SynthClass.Value.Name.OrDefault()}'", sam.SynthClass.Value._syntheticFrameBound.HasValue);
                    return;
                case BirNewSuspendLambda lambda:
                    var body = new BirBlock { Body = lambda.Body.OrEmpty() };
                    var bodyChildren = lambda.Body.OrEmpty().Cast<BirGraphValue>().ToHashSet(ReferenceEqualityComparer.Instance);
                    var parameters = lambda.TypeParams.OrEmpty().Cast<BirGraphValue>().ToHashSet(ReferenceEqualityComparer.Instance);
                    lambda.VisitChildren(child => { if (!bodyChildren.Contains(child) && !parameters.Contains(child)) Walk(child); });
                    Frame(body, lambda.TypeArgs.OrEmpty(), lambda._suspendDeclarationTypeArgs.OrDefault(), "suspend lambda", lambda.TypeFrame.OrDefault() == "dense");
                    return;
                case BirNewDelegate { Method.HasValue: true } target:
                    target.VisitChildren(Walk);
                    Map(GeneratedDemand(target.Method.Value, true), target.TypeArgs.OrEmpty(), $"generated delegate '{target.Method.Value}'");
                    return;
                case BirNew { Type.Value: TypeNode.Fqn constructed } construction:
                    construction.VisitChildren(Walk);
                    Map(GeneratedDemand(constructed.Name, false), constructed.Args ?? Array.Empty<TypeNode>(), $"generated type '{constructed.Name}'");
                    return;
            }
            node.VisitChildren(Walk);
            var tested = node switch {
                BirIsInstance operation when KotlinTypeWitness.NeedsWitness("isInst", false) => operation.Type.OrDefault(),
                BirInstanceOrNull operation when KotlinTypeWitness.NeedsWitness("isInstRef", operation.ReifiedTypeOperand.OrDefault()) => operation.Type.OrDefault(),
                BirCast operation when KotlinTypeWitness.NeedsWitness("cast", operation.ReifiedTypeOperand.OrDefault()) => operation.Type.OrDefault(),
                _ => null,
            };
            if (reflectedOnly) tested = node is BirReflectedType { ArgumentShapes.HasValue: false } reflected ? reflected.Operand.OrDefault() : null;
            Require(tested);
            var call = node switch {
                BirCallStatic value => (value.DeclarationId.OrDefault(), value.TypeArgs.OrEmpty()),
                BirCallInstance value => (value.DeclarationId.OrDefault(), value.TypeArgs.OrEmpty()),
                BirConstrainedCall value => (value.DeclarationId.OrDefault(), value.TypeArgs.OrEmpty()),
                BirCallInline value => (value.DeclarationId.OrDefault(), value.TypeArgs.OrEmpty()),
                _ => ((string)null, Array.Empty<TypeNode>()),
            };
            if (call.Item1 != null) Map(Indices(call.Item1), call.Item2, $"call '{call.Item1}'");
        }
        Walk(root);
        return required;
    }
}
