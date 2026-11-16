using System;
using System.Collections.Generic;
using System.Linq;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class ReifiedNullabilityWitnessLowering
{
    static BirFieldGet SourceWitnessField(string owner, string name) => new() {
        OwnerType = new TypeNode.Fqn(owner), Recv = new BirThis(), Name = name,
    };

    static BirNewClosure MaterializeSourceDelegate(BirNewDelegate node, SourceGeneratedTarget target,
        SourceWitnessFrame caller, Func<BirGraphValue, SourceWitnessFrame, BirGraphValue> walk,
        ValueRepresentationLowering values)
    {
        var arguments = node.TypeArgs.HasValue ? node.TypeArgs.Value
            : throw new InvalidOperationException($"bir2cir: nullable-sensitive generated delegate '{node.Method.OrDefault()}' has no type arguments");
        var fields = new List<BirCaptureDescriptor>();
        var captures = new List<IBirExpression>();
        var witnesses = new Dictionary<int, IBirExpression>();
        foreach (var index in target.Indices)
        {
            if (index < 0 || index >= arguments.Length)
                throw new InvalidOperationException($"bir2cir: nullable-sensitive generated delegate '{node.Method.OrDefault()}' has no type argument at index {index}");
            var name = Prefix + index;
            fields.Add(new() { Name = name, Type = KotlinRuntimeTypeWitness.Type });
            captures.Add(SourceWitnessFor(arguments[index], caller, values));
            witnesses[index] = SourceWitnessField(target.ClosureName, name);
        }
        // The old generated target ceases to be a declaration; its independently owned body/parameters become
        // ingredients of this construction. Rebind its own dense method frame, never the caller's arguments.
        var source = new BirClosureDefinition {
            Name = target.ClosureName, Fields = fields.ToArray(),
            Params = target.Method.Params.OrEmpty().Select(parameter => ClosureSynthesis.ToLambdaParameter((BirParameter)parameter.DeepClone())).ToArray(),
            Ret = target.Method.Ret.HasValue ? OptionalField<TypeNode>.Of(BirGraphValue.CopyType(target.Method.Ret.Value)) : OptionalField<TypeNode>.Null,
            Body = target.Method.Body.OrEmpty().Select(statement => (IBirStatement)walk(((BirGraphValue)statement).DeepClone(), new(witnesses, null))).ToArray(),
            TypeParams = target.Method.TypeParams.HasValue
                ? OptionalField<BirTypeParameter[]>.Of(target.Method.TypeParams.Value.Select(parameter => (BirTypeParameter)parameter.DeepClone()).ToArray())
                : OptionalField<BirTypeParameter[]>.Null,
            SemanticOwner = target.Owner,
        };
        if (source.TypeParams.OrEmpty().Length != 0)
        {
            source = (BirClosureDefinition)TypedExecutableFrames.Rebind(source,
                Enumerable.Range(0, source.TypeParams.Value.Length).Select(index => (TypeNode)new TypeNode.Tv("method", index)).ToArray());
            source._syntheticFrameBound = true;
        }
        return new BirNewClosure {
            ClosureType = new TypeNode.Fqn(target.ClosureName), Captures = captures.ToArray(), Method = "invoke",
            FuncType = node.FuncType.HasValue ? OptionalField<TypeNode>.Of(BirGraphValue.CopyType(node.FuncType.Value)) : OptionalField<TypeNode>.Null,
            TypeArgs = arguments.Select(BirGraphValue.CopyType).ToArray(), SynthClass = source,
        };
    }

    static void MaterializeSourceClosure(BirNewClosure node, int[] indices, SourceWitnessFrame caller,
        Func<BirGraphValue, SourceWitnessFrame, BirGraphValue> walk, ValueRepresentationLowering values)
    {
        var source = node.SynthClass.Value;
        var arguments = node.TypeArgs.Value;
        var name = (node.ClosureType.OrDefault() as TypeNode.Fqn)?.Name
            ?? throw new InvalidOperationException("bir2cir: nullable-sensitive closure has no closure type");
        var fields = source.Fields.OrEmpty().ToList();
        var captures = node.Captures.OrEmpty().Select(capture => (IBirExpression)walk((BirGraphValue)capture, caller)).ToList();
        var methodWitnesses = new Dictionary<int, IBirExpression>();
        var typeWitnesses = new Dictionary<int, IBirExpression>();
        var used = fields.Select(field => field.Name.OrDefault()).Where(value => value != null).ToHashSet(StringComparer.Ordinal);
        foreach (var index in indices)
        {
            if (index < 0 || index >= arguments.Length)
                throw new InvalidOperationException($"bir2cir: nullable-sensitive closure has no type argument at index {index}");
            var fieldName = SourceWitnessName(index, used);
            fields.Add(new() { Name = fieldName, Type = KotlinRuntimeTypeWitness.Type });
            captures.Add(SourceWitnessFor(arguments[index], caller, values));
            var field = SourceWitnessField(name, fieldName);
            if (ClosureSynthesis.HasPreboundFrame(source)) typeWitnesses[index] = field;
            else BindSourceWitness(arguments[index], field, methodWitnesses, typeWitnesses);
        }
        source.Fields = fields.ToArray();
        node.Captures = captures.ToArray();
        SourceBody(source.Body, new(methodWitnesses, typeWitnesses), walk);
    }

    static void MaterializeSourceSam(BirNewSam node, int[] indices, SourceWitnessFrame caller,
        Func<BirGraphValue, SourceWitnessFrame, BirGraphValue> walk, ValueRepresentationLowering values)
    {
        var source = node.SynthClass.Value;
        var arguments = node.TypeArgs.Value;
        var name = source.Name.OrDefault() ?? throw new InvalidOperationException("bir2cir: nullable-sensitive SAM has no synthesized class name");
        var fields = source.Fields.OrEmpty().ToList();
        var captures = node.Captures.OrEmpty().Select(capture => (IBirExpression)walk((BirGraphValue)capture, caller)).ToList();
        var methodWitnesses = new Dictionary<int, IBirExpression>();
        var typeWitnesses = new Dictionary<int, IBirExpression>();
        var used = fields.Select(field => field.Name.OrDefault()).Where(value => value != null).ToHashSet(StringComparer.Ordinal);
        foreach (var index in indices)
        {
            if (index < 0 || index >= arguments.Length)
                throw new InvalidOperationException($"bir2cir: nullable-sensitive SAM has no type argument at index {index}");
            var fieldName = SourceWitnessName(index, used);
            fields.Add(new() { Name = fieldName, Type = KotlinRuntimeTypeWitness.Type });
            captures.Add(SourceWitnessFor(arguments[index], caller, values));
            AppendSourceWitnessConstructors(source, name, fieldName);
            var field = SourceWitnessField(name, fieldName);
            if (ClosureSynthesis.HasPreboundFrame(source)) typeWitnesses[index] = field;
            else BindSourceWitness(arguments[index], field, methodWitnesses, typeWitnesses);
        }
        source.Fields = fields.ToArray();
        node.Captures = captures.ToArray();
        foreach (var method in source.Methods.OrEmpty()) SourceBody(method.Body, new(methodWitnesses, typeWitnesses), walk);
    }

    static void MaterializeSourceSuspend(BirNewSuspendLambda node, int[] indices, SourceWitnessFrame caller,
        Func<BirGraphValue, SourceWitnessFrame, BirGraphValue> walk, ValueRepresentationLowering values, bool dense)
    {
        var parameters = node.TypeParams.OrEmpty();
        if (dense && !node.TypeArgs.HasValue && parameters.Length == 0)
        {
            if (node.CapValues.HasValue)
                for (var index = 0; index < node.CapValues.Value.Length; index++)
                    if (node.CapValues.Value[index] is { } value) node.CapValues.Value[index] = (IBirExpression)walk((BirGraphValue)value, caller);
            SourceBody(node.Body, new(null, null), walk);
            return;
        }
        var arguments = node.TypeArgs.HasValue ? node.TypeArgs.Value
            : throw new InvalidOperationException(dense ? "bir2cir: generic dense suspend frame has no type arguments"
                : "bir2cir: nullable-sensitive suspend lambda has no type arguments");
        if (dense && parameters.Length != arguments.Length)
            throw new InvalidOperationException("bir2cir: dense suspend frame has inconsistent type parameter bindings");
        var captures = node.Captures.OrEmpty().ToList();
        var capValues = node.CapValues.OrEmpty().Select(value => value == null ? null : (IBirExpression)walk((BirGraphValue)value, caller)).ToList();
        while (capValues.Count < captures.Count) capValues.Add(null);
        var methodWitnesses = new Dictionary<int, IBirExpression>();
        var typeWitnesses = new Dictionary<int, IBirExpression>();
        var used = captures.Select(capture => capture.Name.OrDefault()).Where(name => name != null).ToHashSet(StringComparer.Ordinal);
        var declarations = node._suspendDeclarationTypeArgs.OrDefault();
        foreach (var index in indices)
        {
            if (index < 0 || index >= arguments.Length)
                throw new InvalidOperationException(dense ? $"bir2cir: dense suspend frame has no demanded type argument at index {index}"
                    : $"bir2cir: nullable-sensitive suspend lambda has no type argument at index {index}");
            var name = SourceWitnessName(index, used);
            captures.Add(new() { Name = name, Type = KotlinRuntimeTypeWitness.Type });
            capValues.Add(SourceWitnessFor(arguments[index], caller, values));
            var local = new BirLocal { Name = name };
            var declaration = declarations == null ? arguments[index] : declarations[index];
            if (dense)
            {
                var destination = declaration is TypeNode.Tv { Scope: "type" } ? typeWitnesses : methodWitnesses;
                destination[index] = local;
            }
            else BindSourceWitness(declaration, local, methodWitnesses, typeWitnesses);
        }
        node.Captures = captures.ToArray();
        node.CapValues = capValues.ToArray();
        SourceBody(node.Body, new(methodWitnesses, typeWitnesses), walk);
    }

    static IReadOnlyDictionary<int, IBirExpression> MaterializeSourceType(IBirTypeDeclaration type, int[] indices)
    {
        var name = type.Name.OrDefault() ?? throw new InvalidOperationException("bir2cir: nullable-sensitive generated type has no name");
        var fields = type.Fields.OrEmpty().ToList();
        var witnesses = new Dictionary<int, IBirExpression>();
        var used = fields.Select(field => field.Name.OrDefault()).Where(value => value != null).ToHashSet(StringComparer.Ordinal);
        foreach (var index in indices)
        {
            var fieldName = SourceWitnessName(index, used);
            fields.Add(new() { Name = fieldName, Type = KotlinRuntimeTypeWitness.Type });
            AppendSourceWitnessConstructors(type, name, fieldName);
            witnesses[index] = SourceWitnessField(name, fieldName);
        }
        type.Fields = fields.ToArray();
        return witnesses;
    }

    static void AppendSourceWitnessConstructors(IBirTypeDeclaration type, string name, string field)
    {
        var constructors = type.Ctors.OrEmpty();
        if (constructors.Length == 0)
            throw new InvalidOperationException($"bir2cir: nullable-sensitive generated type '{name}' has no constructor");
        foreach (var ctor in constructors)
        {
            ctor.Params = ctor.Params.OrEmpty().Append(new BirParameter { Name = field, Type = KotlinRuntimeTypeWitness.Type }).ToArray();
            if (ctor.ThisArgs.HasValue)
            {
                ctor.ThisArgs = ctor.ThisArgs.Value.Append<IBirExpression>(new BirLocal { Name = field }).ToArray();
                ctor.DelegationSig = ctor.DelegationSig.OrEmpty().Append(KotlinRuntimeTypeWitness.Type).ToArray();
            }
            else ctor.Body = ctor.Body.OrEmpty().Append<IBirStatement>(new BirSetField {
                OwnerType = new TypeNode.Fqn(name), Recv = new BirThis(), Name = field, Value = new BirLocal { Name = field },
            }).ToArray();
        }
    }

    static void SourceBody(OptionalField<IBirStatement[]> body, SourceWitnessFrame witnesses,
        Func<BirGraphValue, SourceWitnessFrame, BirGraphValue> walk)
    {
        if (!body.HasValue) return;
        for (var index = 0; index < body.Value.Length; index++) body.Value[index] = (IBirStatement)walk((BirGraphValue)body.Value[index], witnesses);
    }

    static void BindSourceWitness(TypeNode argument, IBirExpression witness,
        IDictionary<int, IBirExpression> methods, IDictionary<int, IBirExpression> types)
    {
        if (argument is not TypeNode.Tv variable) return;
        var destination = variable.Scope == "method" ? methods : variable.Scope == "type" ? types : null;
        if (destination == null) return;
        if (destination.TryGetValue(variable.I, out var existing)
            && !((BirGraphValue)existing).StructurallyEquals((BirGraphValue)witness))
            throw new InvalidOperationException($"bir2cir: conflicting nullable-witness captures for {variable.Scope} type parameter {variable.I}");
        destination[variable.I] = SourceCopy(witness);
    }
}
