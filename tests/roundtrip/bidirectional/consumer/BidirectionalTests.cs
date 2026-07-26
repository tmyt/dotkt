using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

public class BidirectionalTests
{
    [Test]
    public void CSharpAndKotlinProjectReferencesWorkInBothDirections()
    {
        var greeter = new BidirectionalGreeter("Visual Studio");
        Assert.That(greeter.greet(), Is.EqualTo("Hi, Visual Studio (accent=cyan)"));
        IReadOnlyList<string> names = greeter.roster();
        Assert.That(string.Join(", ", names), Is.EqualTo("Visual Studio A, Visual Studio B, Visual Studio C"));
    }

    [Test]
    public void CSharpCallsKotlinTopLevelFunctionAtCompileTime()
    {
        Assert.That(LibraryKt.bidirectionalAdd(2, 3), Is.EqualTo(5));
    }

    // #251 — the emitted CONSTRUCTOR parameter must carry NullableAttribute(2), the same NRT annotation a C#
    // consumer reads off a nullable METHOD parameter. This project does not enable NRT, so a `new … (null)` call
    // compiles either way: the metadata assert is the proof, the behavioral line only guards the runtime.
    [Test]
    public void KotlinNullableConstructorParameterCarriesNullableAttribute()
    {
        static byte? NullableByte(System.Reflection.ParameterInfo p) =>
            p.GetCustomAttributesData()
                .Where(a => a.AttributeType.FullName == "System.Runtime.CompilerServices.NullableAttribute")
                .Select(a => (byte?)a.ConstructorArguments[0].Value)
                .SingleOrDefault();

        var ctorParam = typeof(BidirectionalNullableCtor).GetConstructors().Single().GetParameters().Single();
        var methodParam = typeof(BidirectionalNullableCtor)
            .GetMethod(nameof(BidirectionalNullableCtor.takeNullable))!.GetParameters().Single();
        // A NESTED type's ctor param goes through the same walk's type recursion.
        var nestedCtorParam = typeof(BidirectionalNullableCtor.Nested)
            .GetConstructors().Single().GetParameters().Single();
        Assert.That(NullableByte(ctorParam), Is.EqualTo((byte)2), "ctor param lost its NullableAttribute");
        Assert.That(NullableByte(methodParam), Is.EqualTo((byte)2), "method param lost its NullableAttribute");
        Assert.That(NullableByte(nestedCtorParam), Is.EqualTo((byte)2), "nested-type ctor param lost its NullableAttribute");

        Assert.That(new BidirectionalNullableCtor(null).labelLength(), Is.EqualTo(-1));
        Assert.That(new BidirectionalNullableCtor("abcd").labelLength(), Is.EqualTo(4));
        Assert.That(new BidirectionalNullableCtor.Nested(null).tagLength(), Is.EqualTo(-1));
    }
}
