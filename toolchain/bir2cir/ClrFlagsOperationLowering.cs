using System;
using System.Linq;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

// A dll2klib-synthesized CLR [Flags] member is a metadata-only Kotlin declaration. kotc carries the exact selected
// declaration's ClrFlagsOperation role; this pass consumes that source meaning and resolves the target enum's concrete
// CLR width. No fabricated MethodDef call survives. enumBits states that an explicitly converted integral expression
// inhabits the named enum slot, leaving ilemit only the one-to-one conv/bit/comparison emission.
static partial class ClrFlagsOperationLowering
{
    static int _binding;

    public static void Apply(BirFileDeclaration root, ReferenceMetadataIndex refs)
    {
        _binding = 0;
        BirGraphValue Walk(BirGraphValue node)
        {
            if (node is IBirExecutablePayload) return node;
            node.VisitChildSlots(slot => slot.Replace(Walk(slot.Value)));
            if (Carrier(node).HasValue)
            {
                if (node is not BirCallInstance call)
                    throw new InvalidOperationException("bir2cir: ClrFlagsOperation must annotate an instance call");
                return Lower(call, refs);
            }
            return node;
        }
        Walk(root);
        void AssertConsumed(BirGraphValue node)
        {
            if (node is IBirExecutablePayload) return;
            if (Carrier(node).IsPresent)
                throw new InvalidOperationException("bir2cir: ClrFlagsOperation carrier survived lowering");
            node.VisitChildren(AssertConsumed);
        }
        AssertConsumed(root);
    }

    static OptionalField<string> Carrier(BirGraphValue node) => node switch {
        BirCallInstance value => value.ClrFlagsOperation, BirCallStatic value => value.ClrFlagsOperation, _ => default,
    };

    static BirCallEvaluation Lower(BirCallInstance call, ReferenceMetadataIndex refs)
    {
        var role = call.ClrFlagsOperation.Value;
        if (role is not ("or" or "and" or "xor" or "inv" or "contains"))
            throw new InvalidOperationException($"bir2cir: unknown ClrFlagsOperation role '{role}'");
        var owner = call.OwnerType.OrDefault()
            ?? throw new InvalidOperationException("bir2cir: ClrFlagsOperation call carries no owner type");
        var representation = refs.ResolveFlagsEnum(owner)
            ?? throw new InvalidOperationException(
                $"bir2cir: ClrFlagsOperation owner '{(owner as TypeNode.Fqn)?.Name}' is not an exact referenced CLR [Flags] enum");
        var args = call.Args.OrDefault()
            ?? throw new InvalidOperationException("bir2cir: ClrFlagsOperation call carries no argument vector");
        var argumentTypes = call.ArgTypes.OrDefault()
            ?? throw new InvalidOperationException("bir2cir: ClrFlagsOperation call carries no argument-type vector");
        var unary = role == "inv";
        if (args.Length != (unary ? 0 : 1) || argumentTypes.Length != args.Length)
            throw new InvalidOperationException($"bir2cir: ClrFlagsOperation '{role}' has an invalid argument vector");
        if (!unary && !SameFlagsEnumDefinition(argumentTypes[0], representation, refs))
            throw new InvalidOperationException($"bir2cir: ClrFlagsOperation '{role}' argument must name the receiver's exact enum declaration");
        var returnType = call.Ret.OrDefault();
        var validReturn = role == "contains" ? returnType == new TypeNode.Fqn("kotlin.Boolean")
            : SameFlagsEnumDefinition(returnType, representation, refs);
        if (!validReturn)
            throw new InvalidOperationException($"bir2cir: ClrFlagsOperation '{role}' carries an invalid return type");
        var receiver = call.Recv.OrDefault()
            ?? throw new InvalidOperationException("bir2cir: ClrFlagsOperation call carries no receiver");
        var argument = unary ? null : args[0]
            ?? throw new InvalidOperationException($"bir2cir: ClrFlagsOperation '{role}' carries a null argument node");
        return Plan(role, representation, Copy(receiver), Copy(argument));
    }

    static BirCallEvaluation Plan(string role, FlagsEnumRepresentation representation,
        IBirExpression receiver, IBirExpression argument)
    {
        var receiverRef = Bind("recv", "receiver", representation.EnumType, EnumCast(representation, receiver), out var receiverBinding);
        var bindings = new List<BirCallBinding> { receiverBinding };
        BirBindingReference argumentRef = null;
        if (role != "inv")
        {
            argumentRef = Bind("arg", "argument", representation.EnumType, EnumCast(representation, argument), out var argumentBinding);
            bindings.Add(argumentBinding);
        }
        IBirExpression result = role switch {
            "or" => EnumBits(representation, Binary("|", receiverRef, argumentRef)),
            "and" => EnumBits(representation, Binary("&", receiverRef, argumentRef)),
            "xor" => EnumBits(representation, Binary("^", receiverRef, argumentRef)),
            "inv" => EnumBits(representation, new BirUnaryOperation { Op = "~", E = Value(Copy(receiverRef)) }),
            "contains" => Binary("==", EnumBits(representation, Binary("&", receiverRef, argumentRef)), Copy(argumentRef)),
            _ => throw new InvalidOperationException(),
        };
        return new BirCallEvaluation {
            Type = BirGraphValue.CopyType(role == "contains" ? new TypeNode.Fqn("kotlin.Boolean") : representation.EnumType),
            Bindings = bindings.ToArray(), Expr = Value(result),
        };
    }

    static BirBindingReference Bind(string phase, string role, TypeNode type, IBirExpression expression, out BirCallBinding binding)
    {
        var id = $"cir$flags{_binding++}";
        binding = new BirCallBinding {
            Id = id, Phase = phase, Kind = "value", Stable = false,
            Type = BirGraphValue.CopyType(type), Role = $"[Flags] {role}", Expr = Value(expression),
        };
        return new BirBindingReference { Id = id, Sty = BirGraphValue.CopyType(type) };
    }

    static OptionalField<IBirExpression> Value(IBirExpression expression) => OptionalField<IBirExpression>.Of(expression);
    static IBirExpression Copy(IBirExpression expression) => expression == null ? null
        : (IBirExpression)((BirGraphValue)expression).DeepClone();
    static BirBinaryOperation Binary(string operation, IBirExpression left, IBirExpression right) => new() {
        Op = operation, Lhs = Value(Copy(left)), Rhs = Value(Copy(right)),
    };
    static BirEnumBits EnumBits(FlagsEnumRepresentation representation, IBirExpression expression) => new() {
        Type = BirGraphValue.CopyType(representation.EnumType), Underlying = BirGraphValue.CopyType(representation.Underlying),
        E = Value(new BirConvert { To = BirGraphValue.CopyType(representation.Underlying), E = Value(expression) }),
    };

    // A generic receiver still needs the exact enum conversion before integer
    // operations. For an exact enum this is an identity conversion.
    static BirCast EnumCast(FlagsEnumRepresentation representation, IBirExpression expression) => new() {
        Type = BirGraphValue.CopyType(representation.EnumType), E = Value(expression),
    };

    static bool SameFlagsEnumDefinition(
        TypeNode candidate,
        FlagsEnumRepresentation expected,
        ReferenceMetadataIndex refs)
    {
        var resolved = refs.ResolveFlagsEnum(candidate);
        if (resolved is null ||
            resolved.EnumType is not TypeNode.Fqn actual ||
            expected.EnumType is not TypeNode.Fqn wanted)
            return false;
        return StringComparer.Ordinal.Equals(actual.Name, wanted.Name) &&
            (actual.Args?.Length ?? 0) == (wanted.Args?.Length ?? 0) &&
            resolved.Underlying == expected.Underlying;
    }
}

sealed record FlagsEnumRepresentation(TypeNode EnumType, TypeNode Underlying);
