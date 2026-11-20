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
        IReadOnlyDictionary<int, IBirExpression> Type, ReflectedDeclarationParameters.Scope Parameters = null);

    internal static void Apply(BirFileDeclaration file, NullableWitnessDemand demand,
        ReferenceMetadataIndex refs, ValueRepresentationLowering values,
        Action<BirFunctionDeclaration> retireSourceDeclaration, SourceHelperBinder helpers = null,
        ReflectedDeclarationParameters declarationsOfParameters = null)
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
            var scope = new ReflectedDeclarationParameters.Scope(null, null, declarationsOfParameters?.Type(type?.Name.OrDefault()),
                type == null ? null : NullableRepresentationViews.TypeFacts(owner)?.NullableFrame, declarationsOfParameters);
            var typeWitnesses = type?.Name.HasValue == true
                && generatedTypes.TryGetValue(type.Name.Value, out var generated)
                && ReferenceEquals(type, generated.Type) ? MaterializeSourceType(type, generated.Indices) : null;
            var methods = owner is BirFileDeclaration source ? source.Methods : type.Methods;
            foreach (var method in methods.OrEmpty())
                if (!targets.Values.Any(target => ReferenceEquals(target.Method, method))) Method(method, typeWitnesses, scope);
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
                foreach (var ctor in type.Ctors.OrEmpty()) Walk(ctor, new(null, typeWitnesses, scope));
            foreach (var field in owner is BirFileDeclaration fieldsFile ? fieldsFile.Fields.OrEmpty() : type.Fields.OrEmpty())
                Walk(field, new(null, typeWitnesses, scope));
            if (type != null)
                foreach (var property in type.Properties.OrEmpty()) Walk(property, new(null, typeWitnesses, scope));
        }

        void Method(BirFunctionDeclaration method, IReadOnlyDictionary<int, IBirExpression> typeWitnesses, ReflectedDeclarationParameters.Scope scope)
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
                            or NullableRepresentationFrame.Role.NullableStorage or NullableRepresentationFrame.Role.NullableElement
                            ? KotlinRuntimeTypeWitness.NullableValue(value, demand.Reflected?.Indices(identity).Contains(index) == true ? helpers : null) : SourceCopy(value);
                    }
                }
                method.Params = parameters.ToArray();
                method.NullableWitnessIndices = indices.ToArray();
                if (demand.Reflected?.Indices(identity) is { Length: > 0 } reflection)
                    method.ReflectedWitnessIndices = reflection;
            }
            Body(method.Body, new(witnesses, typeWitnesses, scope with {
                Method = declarationsOfParameters?.Method(identity), MethodFrame = method.NullableRepresentationFrame.OrDefault() }));
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
            IBirExpression LowerShape(IBirExpression shape) => (IBirExpression)Walk((BirGraphValue)shape, witnesses);
            if (node is BirReflectedType operation)
                return (BirGraphValue)Reflect(operation.Operand.Value, witnesses, values, helpers,
                    ReflectedKotlinType.Projections(operation, helpers, LowerShape));
            if (node is BirReflectedParameter parameter)
                return (BirGraphValue)ReflectedKotlinType.Declared(parameter, helpers, LowerShape);
            Func<int, TypeNode, IBirExpression> Capture(int[] reflected) =>
                (index, argument) => SourceWitnessFor(argument, witnesses, values, reflected?.Contains(index) == true ? helpers : null);
            int[] ReflectedFrame(BirGraphValue body, TypeNode[] args, bool dense = false) =>
                demand.Reflected?.MaterializedFrameIndices(body, args, frames.Reflected, dense);
            if (node is BirNewClosure { SynthClass.HasValue: true, TypeArgs.HasValue: true } closure
                && demand.MaterializedFrameIndices(closure.SynthClass.Value, closure.TypeArgs.Value, frames) is { Length: > 0 } closureIndices)
            {
                MaterializeSourceClosure(closure, closureIndices, witnesses, Capture(ReflectedFrame(closure.SynthClass.Value, closure.TypeArgs.Value)), Walk);
                return node;
            }
            if (node is BirNewSam { SynthClass.HasValue: true, TypeArgs.HasValue: true } sam
                && demand.MaterializedFrameIndices(sam.SynthClass.Value, sam.TypeArgs.Value, frames) is { Length: > 0 } samIndices)
            {
                MaterializeSourceSam(sam, samIndices, witnesses, Capture(ReflectedFrame(sam.SynthClass.Value, sam.TypeArgs.Value)), Walk);
                return node;
            }
            if (node is BirNewSuspendLambda suspend)
            {
                var dense = suspend.TypeFrame.OrDefault() == "dense";
                if (dense || suspend.TypeArgs.HasValue)
                {
                    var arguments = dense ? suspend.TypeArgs.OrEmpty() : suspend._suspendDeclarationTypeArgs.OrDefault() ?? suspend.TypeArgs.Value;
                    var body = new BirBlock { Body = suspend.Body.OrEmpty() };
                    var indices = demand.MaterializedFrameIndices(body, arguments, frames, dense);
                    if (dense || indices.Length != 0)
                    {
                        MaterializeSourceSuspend(suspend, indices, witnesses, Capture(ReflectedFrame(body, arguments, dense)), Walk, dense);
                        return node;
                    }
                }
            }
            if (node is BirNewDelegate { Method.HasValue: true } target && targets.TryGetValue(target.Method.Value, out var generated))
                return MaterializeSourceDelegate(target, generated, Capture(frames.Reflected?.Targets.GetValueOrDefault(target.Method.Value)), Walk);

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
                var flags = OptionalField<IBirExpression>.Of(KotlinRuntimeTypeWitness.FlagsValue(witness));
                var classifier = OptionalField<IBirExpression>.Of(KotlinRuntimeTypeWitness.ClassifierValue(witness));
                var predicate = OptionalField<IBirExpression>.Of(KotlinRuntimeTypeWitness.PredicateValue(witness));
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
                    PrepareSourceWitnesses(node, node.ApplicationTypeArguments.Value, indices, witnesses, values, $"nullable-sensitive call '{id}'",
                        demand.Reflected?.Indices(id), helpers);
                }
            }
            if (node is BirNew { Type.Value: TypeNode.Fqn constructed }
                && generatedTypes.TryGetValue(constructed.Name, out var construction))
                PrepareSourceWitnesses(node, constructed.Args ?? Array.Empty<TypeNode>(), construction.Indices, witnesses, values,
                    $"generated type '{constructed.Name}'", frames.Reflected?.Types.GetValueOrDefault(constructed.Name), helpers);
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

    static IBirExpression Reflect(TypeNode type, SourceWitnessFrame witnesses, ValueRepresentationLowering values, SourceHelperBinder helpers,
        IBirExpression[] projections = null) =>
        ReflectedKotlinType.Value(type, helpers, (tv, nullable) => witnesses?.Parameters?.IsDeclared(tv) == true
            ? witnesses.Parameters.Value(tv, nullable, helpers)
            : ReflectedKotlinType.Read(SourceWitnessFor(tv, witnesses, values), nullable, helpers), projections: projections);

    static IBirExpression SourceWitnessFor(TypeNode type, SourceWitnessFrame witnesses, ValueRepresentationLowering values, SourceHelperBinder reflection = null) => type switch {
        TypeNode.Nullable nullable => KotlinRuntimeTypeWitness.NullableValue(SourceWitnessFor(nullable.Of, witnesses, values, reflection), reflection),
        TypeNode.Oblivious oblivious => SourceWitnessFor(oblivious.Of, witnesses, values, reflection),
        TypeNode.Tv tv when reflection != null && witnesses?.Parameters?.IsDeclared(tv) == true =>
            SourceDeclaredWitness(tv, witnesses, values, reflection),
        TypeNode.Tv { Scope: "method" } variable when witnesses?.Method != null && witnesses.Method.TryGetValue(variable.I, out var value) => SourceCopy(value),
        TypeNode.Tv { Scope: "type" } variable when witnesses?.Type != null && witnesses.Type.TryGetValue(variable.I, out var value) => SourceCopy(value),
        TypeNode.Tv variable => throw new InvalidOperationException($"bir2cir: nullable-witness demand reached unbound {variable.Scope} type parameter {variable.I} (method {witnesses?.Parameters?.Method?.Identity}, owner {witnesses?.Parameters?.Type?.Owner}, reflected={reflection != null})"),
        _ => KotlinRuntimeTypeWitness.KnownValue(type, values, reflection == null ? null : Reflect(type, witnesses, values, reflection)),
    };

    static IBirExpression SourceDeclaredWitness(TypeNode.Tv tv, SourceWitnessFrame frame, ValueRepresentationLowering values, SourceHelperBinder helpers)
    {
        var reflected = Reflect(tv, frame, values, helpers);
        var slots = tv.Scope == "method" ? frame.Method : frame.Type;
        return slots != null && slots.TryGetValue(tv.I, out var witness)
            ? KotlinRuntimeTypeWitness.CreateValue(KotlinRuntimeTypeWitness.FlagsValue(witness),
                KotlinRuntimeTypeWitness.ClassifierValue(witness), KotlinRuntimeTypeWitness.PredicateValue(witness), reflected)
            : KotlinRuntimeTypeWitness.KnownValue(tv, values, reflected);
    }

    static OptionalField<IBirExpression[]> SourcePending(BirGraphValue node) => node switch {
        BirCallStatic call => call.NullablePendingWitnesses, BirCallInstance call => call.NullablePendingWitnesses,
        BirConstrainedCall call => call.NullablePendingWitnesses, BirNew call => call.NullablePendingWitnesses, _ => default,
    };

    static void PrepareSourceWitnesses(BirGraphValue call, TypeNode[] arguments, int[] indices,
        SourceWitnessFrame witnesses, ValueRepresentationLowering values, string context, int[] reflected = null, SourceHelperBinder helpers = null)
    {
        if (SourcePending(call).HasValue) throw new InvalidOperationException($"bir2cir: {context} was assigned nullable witnesses twice");
        var shapes = ReflectedKotlinType.TakeCallShapes(call);
        var pending = indices.Select(index => {
            if (index < 0 || index >= arguments.Length) throw new InvalidOperationException($"bir2cir: {context} has no type argument at index {index}");
            var type = arguments[index];
            var position = reflected == null ? -1 : Array.IndexOf(reflected, index);
            if (position < 0 || shapes == null)
                return SourceWitnessFor(type, witnesses, values, position < 0 ? null : helpers);
            var shape = shapes[position];
            if (KotlinTypeWitness.Variable(type) is not { } variable ||
                (variable.Scope == "method" ? witnesses?.Method : witnesses?.Type)?.ContainsKey(variable.I) != true)
                return KotlinRuntimeTypeWitness.KnownValue(type, values, shape);
            var scalar = SourceWitnessFor(type, witnesses, values);
            return KotlinRuntimeTypeWitness.CreateValue(KotlinRuntimeTypeWitness.FlagsValue(scalar),
                KotlinRuntimeTypeWitness.ClassifierValue(scalar), KotlinRuntimeTypeWitness.PredicateValue(scalar), shape);
        }).ToArray();
        switch (call)
        {
            case BirCallStatic value: value.NullablePendingWitnesses = pending; break;
            case BirCallInstance value: value.NullablePendingWitnesses = pending; break;
            case BirConstrainedCall value: value.NullablePendingWitnesses = pending; break;
            case BirNew value: value.NullablePendingWitnesses = pending; break;
        }
    }
}
