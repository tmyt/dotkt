using System;
using System.Collections.Generic;
using System.Linq;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class ReifiedNullabilityWitnessLowering
{
    sealed record SourceGeneratedTarget(BirFunctionDeclaration Method, string ClosureName, string Owner, int[] Indices);
    sealed record SourceGeneratedType(IBirTypeDeclaration Type, int[] Indices);
    sealed record SourceWitnessFrame(IReadOnlyDictionary<int, IBirExpression> Method,
        IReadOnlyDictionary<int, IBirExpression> Type);

    internal static void Apply(BirFileDeclaration file, NullableWitnessDemand demand,
        ReferenceMetadataIndex refs, ValueRepresentationLowering values,
        Action<BirFunctionDeclaration> retireSourceDeclaration)
    {
        var frames = demand.AnalyzeGeneratedFrames(file);
        var targets = SourceGeneratedTargets(file, frames.Targets);
        var generatedTypes = new Dictionary<string, SourceGeneratedType>(StringComparer.Ordinal);
        foreach (var type in file.Types.OrEmpty())
            if (type.Generated.OrDefault() && type.Name.HasValue
                && frames.Types.TryGetValue(type.Name.Value, out var indices) && indices.Length != 0
                && !generatedTypes.TryAdd(type.Name.Value, new(type, indices)))
                throw new InvalidOperationException($"bir2cir: ambiguous nullable-sensitive generated type '{type.Name.Value}'");

        void Owner(BirGraphValue owner)
        {
            var type = owner as IBirTypeDeclaration;
            var typeWitnesses = type?.Name.HasValue == true
                && generatedTypes.TryGetValue(type.Name.Value, out var generated)
                && ReferenceEquals(type, generated.Type) ? MaterializeSourceType(type, generated.Indices) : null;
            var methods = owner is BirFileDeclaration source ? source.Methods : type.Methods;
            foreach (var method in methods.OrEmpty())
                if (!targets.Values.Any(target => ReferenceEquals(target.Method, method))) Method(method, typeWitnesses);
            if (methods.HasValue)
            {
                var retained = methods.Value.Where(method => !targets.Values.Any(target => ReferenceEquals(target.Method, method))).ToArray();
                if (retained.Length != methods.Value.Length)
                {
                    foreach (var target in targets.Values.Where(target => methods.Value.Any(method => ReferenceEquals(method, target.Method))))
                        retireSourceDeclaration?.Invoke(target.Method);
                    if (owner is BirFileDeclaration sourceFile) sourceFile.Methods = retained;
                    else type.Methods = retained;
                }
            }
            if (type != null)
                foreach (var ctor in type.Ctors.OrEmpty()) Walk(ctor, new(null, typeWitnesses));
            foreach (var field in owner is BirFileDeclaration fieldsFile ? fieldsFile.Fields.OrEmpty() : type.Fields.OrEmpty())
                Walk(field, new(null, typeWitnesses));
            if (type != null)
                foreach (var property in type.Properties.OrEmpty()) Walk(property, new(null, typeWitnesses));
        }

        void Method(BirFunctionDeclaration method, IReadOnlyDictionary<int, IBirExpression> typeWitnesses)
        {
            var semantic = method.TypeParams.OrEmpty().Select((parameter, index) => (parameter, index))
                .Where(entry => entry.parameter.Reified.OrDefault()).Select(entry => entry.index).ToArray();
            var identity = method.DeclarationId.OrDefault();
            if (semantic.Length != 0 && identity != null) method.SemanticReifiedIndices = semantic;
            var indices = identity != null && demand.LocalDeclarations.TryGetValue(identity, out var required)
                ? required : Array.Empty<int>();
            Dictionary<int, IBirExpression> witnesses = null;
            if (indices.Length != 0)
            {
                var parameters = method.Params.OrEmpty().ToList();
                var used = parameters.Select(parameter => parameter.Name.OrDefault()).Where(name => name != null)
                    .ToHashSet(StringComparer.Ordinal);
                witnesses = new();
                var frame = method.NullableRepresentationFrame.OrDefault();
                foreach (var index in indices)
                {
                    var name = SourceWitnessName(index, used);
                    parameters.Add(new() { Name = name, Type = KotlinRuntimeTypeWitness.Type });
                    var value = new BirLocal { Name = name };
                    if (frame == null) witnesses[index] = value;
                    else for (var physical = 0; physical < frame.PhysicalArity; physical++)
                    {
                        var slot = frame.PhysicalSlot(physical);
                        if (slot.SourceIndex != index) continue;
                        witnesses[physical] = slot.Representation is NullableRepresentationFrame.Role.Nullable
                            or NullableRepresentationFrame.Role.NullableStorage
                            ? KotlinRuntimeTypeWitness.NullableValue(value) : SourceCopy(value);
                    }
                }
                method.Params = parameters.ToArray();
                method.NullableWitnessIndices = indices.ToArray();
            }
            Body(method.Body, new(witnesses, typeWitnesses));
        }

        void Body(OptionalField<IBirStatement[]> statements, SourceWitnessFrame witnesses)
        {
            if (!statements.HasValue) return;
            for (var index = 0; index < statements.Value.Length; index++)
                statements.Value[index] = (IBirStatement)Walk((BirGraphValue)statements.Value[index], witnesses);
        }

        BirGraphValue Walk(BirGraphValue node, SourceWitnessFrame witnesses)
        {
            if (node == null || node is IBirExecutablePayload) return node;
            if (node is BirNewClosure { SynthClass.HasValue: true, TypeArgs.HasValue: true } closure
                && demand.MaterializedFrameIndices(closure.SynthClass.Value, closure.TypeArgs.Value, frames) is { Length: > 0 } closureIndices)
            {
                MaterializeSourceClosure(closure, closureIndices, witnesses, Walk, values);
                return node;
            }
            if (node is BirNewSam { SynthClass.HasValue: true, TypeArgs.HasValue: true } sam
                && demand.MaterializedFrameIndices(sam.SynthClass.Value, sam.TypeArgs.Value, frames) is { Length: > 0 } samIndices)
            {
                MaterializeSourceSam(sam, samIndices, witnesses, Walk, values);
                return node;
            }
            if (node is BirNewSuspendLambda suspend)
            {
                var dense = suspend.TypeFrame.OrDefault() == "dense";
                if (dense || suspend.TypeArgs.HasValue)
                {
                    var arguments = dense ? suspend.TypeArgs.OrEmpty() : suspend._suspendDeclarationTypeArgs.OrDefault() ?? suspend.TypeArgs.Value;
                    var indices = demand.MaterializedFrameIndices(new BirBlock { Body = suspend.Body.OrEmpty() }, arguments, frames, dense);
                    if (dense || indices.Length != 0)
                    {
                        MaterializeSourceSuspend(suspend, indices, witnesses, Walk, values, dense);
                        return node;
                    }
                }
            }
            if (node is BirNewDelegate { Method.HasValue: true } target && targets.TryGetValue(target.Method.Value, out var generated))
                return MaterializeSourceDelegate(target, generated, witnesses, Walk, values);

            // Pending witnesses already belong to this caller frame. Never analyze or prepare their generated
            // expressions again; repeat admission is rejected by PrepareSourceWitnesses below.
            var pending = SourcePending(node).OrEmpty().Cast<BirGraphValue>().ToHashSet(ReferenceEqualityComparer.Instance);
            node.VisitChildSlots(slot => { if (!pending.Contains(slot.Value)) slot.Replace(Walk(slot.Value, witnesses)); });
            var tested = node switch {
                BirCast cast when KotlinTypeWitness.NeedsWitness(cast.Tag, cast.ReifiedTypeOperand.OrDefault()) => cast.Type.OrDefault(),
                BirIsInstance test => test.Type.OrDefault(),
                BirInstanceOrNull test when KotlinTypeWitness.NeedsWitness(test.Tag, test.ReifiedTypeOperand.OrDefault()) => test.Type.OrDefault(),
                _ => null,
            };
            if (KotlinTypeWitness.Variable(tested) != null)
            {
                var witness = SourceWitnessFor(tested, witnesses, values);
                var flags = KotlinRuntimeTypeWitness.FlagsValue(witness);
                var classifier = KotlinRuntimeTypeWitness.ClassifierValue(witness);
                var predicate = KotlinRuntimeTypeWitness.PredicateValue(witness);
                switch (node)
                {
                    case BirCast cast:
                        cast.KotlinTypeWitness = flags; cast.KotlinRuntimeTypeWitness = classifier; cast.KotlinClassifierPredicate = predicate; break;
                    case BirInstanceOrNull test:
                        test.KotlinTypeWitness = flags; test.KotlinRuntimeTypeWitness = classifier; test.KotlinClassifierPredicate = predicate; break;
                    case BirIsInstance test:
                        test.KotlinTypeWitness = flags; test.KotlinRuntimeTypeWitness = classifier; test.KotlinClassifierPredicate = predicate;
                        test.NullWitness = OptionalField<IBirExpression>.Of(KotlinTypeWitness.AllowsNullValue(KotlinRuntimeTypeWitness.FlagsValue(witness))); break;
                }
            }
            if (node is BirCallStatic or BirCallInstance or BirConstrainedCall && node.DeclarationIdentityFact.HasValue)
            {
                var id = node.DeclarationIdentityFact.Value;
                var indices = demand.LocalDeclarations.TryGetValue(id, out var local) ? local : refs?.NullableWitnessTypeParameterIndices(id);
                if (indices?.Length > 0)
                {
                    if (!node.ApplicationTypeArguments.HasValue)
                        throw new InvalidOperationException($"bir2cir: nullable-sensitive call '{id}' has no type arguments");
                    PrepareSourceWitnesses(node, node.ApplicationTypeArguments.Value, indices, witnesses, values, $"nullable-sensitive call '{id}'");
                }
            }
            if (node is BirNew { Type.Value: TypeNode.Fqn constructed }
                && generatedTypes.TryGetValue(constructed.Name, out var construction))
                PrepareSourceWitnesses(node, constructed.Args ?? Array.Empty<TypeNode>(), construction.Indices, witnesses, values,
                    $"generated type '{constructed.Name}'");
            return node;
        }

        Owner(file);
        foreach (var type in file.Types.OrEmpty()) Owner((BirGraphValue)type);
        file.Visit(node => {
            var name = node switch {
                BirCallStatic call => call.Method.OrDefault(), BirCallInstance call => call.Method.OrDefault(),
                BirConstrainedCall call => call.Method.OrDefault(), BirNewDelegate call => call.Method.OrDefault(),
                BirNewClosure call => call.Method.OrDefault(), BirObjectMethod call => call.Method.OrDefault(),
                BirNewBoundDelegate call => call.Method.OrDefault(), BirGenericStatic call => call.Method.OrDefault(),
                BirGenericInstance call => call.Method.OrDefault(), BirNativeInstance call => call.Method.OrDefault(),
                BirNativeStatic call => call.Method.OrDefault(), BirBoundClrDelegate call => call.Method.OrDefault(),
                BirClrStaticDelegate call => call.Method.OrDefault(),
                _ => null,
            };
            if (name != null && targets.ContainsKey(name))
                throw new InvalidOperationException($"bir2cir: nullable-sensitive generated method '{name}' survives outside its delegate construction");
            if (node is BirTypeParameter parameter) parameter.Reified = default;
        });
    }

    static Dictionary<string, SourceGeneratedTarget> SourceGeneratedTargets(BirFileDeclaration file,
        IReadOnlyDictionary<string, int[]> demands)
    {
        var referenced = new HashSet<string>(StringComparer.Ordinal);
        file.Visit(node => { if (node is BirNewDelegate { Method.HasValue: true } target) referenced.Add(target.Method.Value); });
        var result = new Dictionary<string, SourceGeneratedTarget>(StringComparer.Ordinal);
        var ordinal = 0;
        void Owner(BirGraphValue owner, string name)
        {
            foreach (var method in NullableWitnessDemand.TypedMethods(owner))
                if (method.Generated.OrDefault() && method.Name.HasValue && referenced.Contains(method.Name.Value)
                    && demands.TryGetValue(method.Name.Value, out var indices) && indices.Length != 0
                    && !result.TryAdd(method.Name.Value, new(method, $"dotkt${Sanitize(name)}$NullableWitnessClosure{ordinal++}", name, indices)))
                    throw new InvalidOperationException($"bir2cir: ambiguous nullable-sensitive generated delegate target '{method.Name.Value}'");
        }
        Owner(file, file.FileClass.OrDefault() ?? "file");
        foreach (var type in file.Types.OrEmpty()) Owner((BirGraphValue)type, type.Name.OrDefault() ?? file.FileClass.OrDefault() ?? "file");
        return result;
    }

    static string SourceWitnessName(int index, HashSet<string> used)
    {
        var name = Prefix + index;
        while (!used.Add(name)) name += "$";
        return name;
    }

    static IBirExpression SourceCopy(IBirExpression value) => (IBirExpression)((BirGraphValue)value).DeepClone();

    static IBirExpression SourceWitnessFor(TypeNode type, SourceWitnessFrame witnesses, ValueRepresentationLowering values) => type switch {
        TypeNode.Nullable nullable => KotlinRuntimeTypeWitness.NullableValue(SourceWitnessFor(nullable.Of, witnesses, values)),
        TypeNode.Oblivious oblivious => SourceWitnessFor(oblivious.Of, witnesses, values),
        TypeNode.Tv { Scope: "method" } variable when witnesses?.Method != null && witnesses.Method.TryGetValue(variable.I, out var value) => SourceCopy(value),
        TypeNode.Tv { Scope: "type" } variable when witnesses?.Type != null && witnesses.Type.TryGetValue(variable.I, out var value) => SourceCopy(value),
        TypeNode.Tv variable => throw new InvalidOperationException($"bir2cir: nullable-witness demand reached unbound {variable.Scope} type parameter {variable.I}"),
        _ => KotlinRuntimeTypeWitness.KnownValue(type, values),
    };

    static OptionalField<IBirExpression[]> SourcePending(BirGraphValue node) => node switch {
        BirCallStatic call => call.NullablePendingWitnesses, BirCallInstance call => call.NullablePendingWitnesses,
        BirConstrainedCall call => call.NullablePendingWitnesses, BirNew call => call.NullablePendingWitnesses, _ => default,
    };

    static void PrepareSourceWitnesses(BirGraphValue call, TypeNode[] arguments, int[] indices,
        SourceWitnessFrame witnesses, ValueRepresentationLowering values, string context)
    {
        if (SourcePending(call).HasValue) throw new InvalidOperationException($"bir2cir: {context} was assigned nullable witnesses twice");
        var pending = indices.Select(index => index >= 0 && index < arguments.Length ? SourceWitnessFor(arguments[index], witnesses, values)
            : throw new InvalidOperationException($"bir2cir: {context} has no type argument at index {index}")).ToArray();
        switch (call)
        {
            case BirCallStatic value: value.NullablePendingWitnesses = pending; break;
            case BirCallInstance value: value.NullablePendingWitnesses = pending; break;
            case BirConstrainedCall value: value.NullablePendingWitnesses = pending; break;
            case BirNew value: value.NullablePendingWitnesses = pending; break;
        }
    }
}
