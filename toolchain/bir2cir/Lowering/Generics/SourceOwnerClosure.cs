using System;
using System.Collections.Generic;
using System.Linq;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;
using static ConstrainedReceiverPolicy;

// Close the source input's receiver-bound owners before representation passes.
// Late closing over physically erased frames remains a separate consumer.
static class SourceOwnerClosure
{
    internal static BirFileDeclaration Apply(BirFileDeclaration file) => file with {
        Methods = Map(file.Methods, method => Method(method, Array.Empty<BirTypeParameter>())),
        Types = Map(file.Types, Type),
    };

    static OptionalField<T[]> Map<T>(OptionalField<T[]> values, Func<T, T> rewrite)
        => values.HasValue ? OptionalField<T[]>.Of(values.Value.Select(rewrite).ToArray()) : values;

    static IBirTypeDeclaration Type(IBirTypeDeclaration type)
    {
        BirTypeParameter[] Frame(OptionalField<BirTypeParameter[]> captured, OptionalField<BirTypeParameter[]> declared)
            => captured.OrEmpty().Concat(declared.OrEmpty()).ToArray();
        var frame = type switch {
            BirClassDefinition owner => Frame(owner.CapturedTypeParams, owner.TypeParams),
            BirInterfaceDefinition owner => Frame(owner.CapturedTypeParams, owner.TypeParams),
            BirEnumDefinition owner => Frame(owner.CapturedTypeParams, owner.TypeParams),
            _ => throw new InvalidOperationException("Unknown owning type declaration"),
        };
        return type switch {
            BirClassDefinition owner => owner with {
                Methods = Map(owner.Methods, method => Method(method, frame)),
                Ctors = Map(owner.Ctors, constructor => Constructor(constructor, frame)),
            },
            BirInterfaceDefinition owner => owner with {
                Methods = Map(owner.Methods, method => Method(method, frame)),
                Ctors = Map(owner.Ctors, constructor => Constructor(constructor, frame)),
            },
            BirEnumDefinition owner => owner with {
                Methods = Map(owner.Methods, method => Method(method, frame)),
                Ctors = Map(owner.Ctors, constructor => Constructor(constructor, frame)),
            },
            _ => throw new InvalidOperationException("Unknown owning type declaration"),
        };
    }

    static BirFunctionDeclaration Method(BirFunctionDeclaration method, BirTypeParameter[] owner)
        => method with { Body = Body(method.Body, method.Params.OrEmpty(), owner, method.TypeParams.OrEmpty()) };

    static BirConstructor Constructor(BirConstructor constructor, BirTypeParameter[] owner)
        => constructor with { Body = Body(constructor.Body, constructor.Params.OrEmpty(), owner, Array.Empty<BirTypeParameter>()) };

    static OptionalField<IBirStatement[]> Body(OptionalField<IBirStatement[]> body, BirParameter[] parameters,
        BirTypeParameter[] owner, BirTypeParameter[] method)
    {
        if (!body.HasValue) return body;
        var locals = new Dictionary<string, TypeNode>(StringComparer.Ordinal);
        var ambiguous = new HashSet<string>(StringComparer.Ordinal);
        foreach (var parameter in parameters) RecordLocal(locals, ambiguous, parameter.Name.OrDefault(), parameter.Type.OrDefault());
        foreach (var statement in body.Value)
            ((BirGraphValue)statement).Visit(value => {
                if (value is BirVariable variable) RecordLocal(locals, ambiguous, variable.Name.OrDefault(), variable.Type.OrDefault());
            });

        (TypeNode.Fqn Owner, bool Erased)? Select(TypeNode type, IBirExpression receiver)
        {
            if (type is not TypeNode.Fqn named || receiver == null) return null;
            var receiverType = receiver is BirLocal local && local.Name.HasValue
                && locals.TryGetValue(local.Name.Value, out var declared)
                ? declared : BirGraphStaticType.Surface(receiver, locals);
            if (Peel(receiverType) is not TypeNode.Tv variable) return null;
            var matches = ConstraintDeclarations(variable, owner, method, static p =>
                (p.Constraints.OrEmpty(), p.ErasedInnerConstraints.OrEmpty()))
                .Where(bound => bound.Constraint.Name == named.Name).ToArray();
            if (matches.Length != 1) return null;
            var selected = matches[0];
            return (named.Args == null || selected.Erased) && selected.Constraint.Args != null
                ? selected : (named, selected.Erased);
        }

        BirGraphValue Rewrite(BirGraphValue node)
        {
            // A suspend lambda owns a different method frame. Only capture
            // operands execute in this enclosing method's frame.
            if (node is BirNewSuspendLambda lambda)
                return lambda with {
                    CapValues = lambda.CapValues.HasValue
                        ? OptionalField<IBirExpression[]>.Of(lambda.CapValues.Value.Select(value =>
                            value == null ? null : (IBirExpression)Rewrite((BirGraphValue)value)).ToArray()) : lambda.CapValues,
                };
            node = node switch {
                BirCallInstance call when Select(call.OwnerType.OrDefault(), call.Recv.OrDefault()) is { } selected
                    => call with { OwnerType = selected.Owner, _erasedConstraintDispatch = selected.Erased },
                BirNativeInstance call when Select(call.Type.OrDefault(), call.Recv.OrDefault()) is { } selected
                    => call with { Type = selected.Owner, _erasedConstraintDispatch = selected.Erased },
                BirGenericInstance call when Select(call.Type.OrDefault(), call.Recv.OrDefault()) is { } selected
                    => call with { Type = selected.Owner, _erasedConstraintDispatch = selected.Erased },
                BirPropertyGet call when Select(call.Type.OrDefault(), call.Recv.OrDefault()) is { } selected
                    => call with { Type = selected.Owner, _erasedConstraintDispatch = selected.Erased },
                BirPropertySet call when Select(call.Type.OrDefault(), call.Recv.OrDefault()) is { } selected
                    => call with { Type = selected.Owner, _erasedConstraintDispatch = selected.Erased },
                _ => node,
            };
            return node.RewriteChildren(Rewrite);
        }
        return body.Value.Select(statement => (IBirStatement)Rewrite((BirGraphValue)statement)).ToArray();
    }
}
