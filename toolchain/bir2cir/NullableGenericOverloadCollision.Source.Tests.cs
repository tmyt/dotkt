using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class NullableGenericOverloadCollision
{
    internal static void SourceSelfTest()
    {
        var physical = new TypeNode.Fqn("object");
        var canonicalPhysical = new TypeNode.Fqn("System.Object");
        var nullable = new TypeNode.Nullable(new TypeNode.Tv("type", 0));
        var firstParameter = new BirParameter { Type = physical, NullableGeneric = nullable };
        var secondParameter = new BirParameter { Type = canonicalPhysical, NullableFlags = new[] { 2 } };
        var first = new BirFunctionDeclaration { Name = "read", Params = new[] { firstParameter } };
        var second = new BirFunctionDeclaration { Name = "read", Params = new[] { secondParameter } };
        var methods = new[] { first, second };
        var owner = new BirClassDefinition { Name = "Owner", Kind = "class", TypeParams = new[] { new BirTypeParameter { Name = "Element" } }, Methods = methods };
        var file = new BirFileDeclaration { FileClass = "Facade", Types = new IBirTypeDeclaration[] { owner } };
        Refuse("Owner: two declarations", "read(Element?)", "read(Any?)");
        first.DeclarationId = "one"; second.DeclarationId = "two";
        Check(file, "Source.kt"); // The following exact identity allocator owns this distinct pair.
        second.DeclarationId = "one";
        Refuse("Owner: two declarations", "read(Element?)");
        second.TypeParams = new[] { new BirTypeParameter { Name = "Method" } };
        Check(file, "Source.kt"); // CLI method arity distinguishes this slot.
        second.TypeParams = default;

        // Ordinary reference nullability is diagnostic spelling, not part of
        // the differential key. The source variable names are likewise not keys.
        firstParameter.NullableGeneric = default; firstParameter.NullableFlags = new[] { 1 };
        Check(file, "Source.kt");
        first.TypeParams = new[] { new BirTypeParameter { Name = "T" } };
        second.TypeParams = new[] { new BirTypeParameter { Name = "Renamed" } };
        firstParameter.NullableGeneric = new TypeNode.Nullable(new TypeNode.Tv("method", 0));
        secondParameter.NullableGeneric = new TypeNode.Nullable(new TypeNode.Tv("method", 0));
        Check(file, "Source.kt");
        first.TypeParams = default; second.TypeParams = default;
        firstParameter.NullableGeneric = nullable; secondParameter.NullableGeneric = default;
        secondParameter.Type = new TypeNode.Fqn("System.String");
        Check(file, "Source.kt"); // A genuinely different physical slot never collides.

        // Constructor vector policy is separate (no method generic-arity key).
        secondParameter.Type = canonicalPhysical;
        var firstCtor = new BirConstructor { Params = new[] { firstParameter } };
        var secondCtor = new BirConstructor { Params = new[] { secondParameter } };
        owner.Methods = default; owner.Ctors = new[] { firstCtor, secondCtor };
        Refuse("Owner: two constructors", "Owner(Element?)", "Owner(Any?)");
        firstCtor.DeclarationId = "ctor-one"; secondCtor.DeclarationId = "ctor-two";
        Check(file, "Source.kt");
        if (!ReferenceEquals(first.Params.Value[0], firstParameter) || !ReferenceEquals(owner.Ctors.Value[0], firstCtor)
            || !ReferenceEquals(firstParameter.Type.Value, physical) || !ReferenceEquals(firstParameter.NullableGeneric.Value, nullable)
            || !ReferenceEquals(methods[1], second))
            throw new InvalidOperationException("Collision checkpoint mutated source/physical facts or declaration identity");
        owner.Ctors = default; owner.Methods = default;
        file.Methods = methods;
        first.DeclarationId = default; second.DeclarationId = default;
        Refuse("Facade: two declarations", "read(type#0?)");
        Console.WriteLine("[owning nullable overload collisions] self-test OK (actual source/physical vectors, identities, method arity, constructors, nullability and frames)");

        void Refuse(params string[] parts)
        {
            try { Check(file, "Source.kt"); }
            catch (InvalidOperationException exception) when (parts.All(part => exception.Message.Contains(part, StringComparison.Ordinal))) { return; }
            throw new InvalidOperationException("Nullable collision expectation missed: " + string.Join(", ", parts));
        }
    }
}
