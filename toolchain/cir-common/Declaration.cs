#nullable enable
using System;
using System.Linq;
using DotKt.Bir;

namespace DotKt.Cir;

public interface ICirContainer
{
    CirMethod[] DeclaredMethods { get; }
    CirConstructor[] DeclaredConstructors { get; }
    CirField[] DeclaredFields { get; }
    CirProperty[] DeclaredProperties { get; }
    OptionalField<AttributeApplication[]> Attrs { get; }
    OptionalField<CirSourcePosition> Pos { get; }
}

public interface ICirCallable
{
    CirParameter[] Params { get; }
    OptionalField<AttributeApplication[]> Attrs { get; }
    OptionalField<CirSourcePosition> Pos { get; }
}

public sealed partial record CirFileDeclaration
{
    CirMethod[] ICirContainer.DeclaredMethods => Methods;
    CirConstructor[] ICirContainer.DeclaredConstructors => Array.Empty<CirConstructor>();
    CirField[] ICirContainer.DeclaredFields => Fields;
    CirProperty[] ICirContainer.DeclaredProperties => Properties.OrEmpty();

}

public sealed partial record CirTypeDeclaration
{
    CirMethod[] ICirContainer.DeclaredMethods => Methods.OrEmpty();
    CirConstructor[] ICirContainer.DeclaredConstructors => Ctors.OrEmpty();
    CirField[] ICirContainer.DeclaredFields => Fields.OrEmpty();
    CirProperty[] ICirContainer.DeclaredProperties => Properties.OrEmpty();
}

// Descriptor equality is structural and independent of JSON property order or spelling. It is used only for
// consumption bookkeeping, never as a new overload-resolution policy.
public sealed partial record CirTypeParameter
{
    public bool Equals(CirTypeParameter? other) => other != null && Name == other.Name && Variance == other.Variance
        && DeclarationArrays.Equal(Constraints, other.Constraints) && DeclarationArrays.Equal(SpecialConstraints, other.SpecialConstraints);
    public override int GetHashCode() => HashCode.Combine(Name, Variance,
        DeclarationArrays.Hash(Constraints), DeclarationArrays.Hash(SpecialConstraints));
}

public sealed partial record CirMethodImpl
{
    public bool Equals(CirMethodImpl? other) => other != null && Owner.Equals(other.Owner) && Member == other.Member
        && Arity == other.Arity && Params.SequenceEqual(other.Params) && Ret.Equals(other.Ret)
        && DeclarationArrays.Equal(TypeParams, other.TypeParams) && MemberRef.Equals(other.MemberRef);
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Owner); hash.Add(Member); hash.Add(Arity);
        foreach (var parameter in Params) hash.Add(parameter);
        hash.Add(Ret); hash.Add(DeclarationArrays.Hash(TypeParams)); hash.Add(MemberRef);
        return hash.ToHashCode();
    }
}

static class DeclarationArrays
{
    internal static bool Equal<T>(OptionalField<T[]> left, OptionalField<T[]> right)
        => left.IsPresent == right.IsPresent && left.IsNull == right.IsNull
            && (!left.HasValue || left.Value.SequenceEqual(right.Value));
    internal static int Hash<T>(OptionalField<T[]> field)
    {
        var hash = new HashCode();
        hash.Add(field.IsPresent); hash.Add(field.IsNull);
        if (field.HasValue) foreach (var item in field.Value) hash.Add(item);
        return hash.ToHashCode();
    }
}
