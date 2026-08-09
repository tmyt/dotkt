using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
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

    [Test]
    public void CSharpConsumesKotlinCompanionFunctionsAsStaticExtensionMembers()
    {
        Assert.That(BidirectionalStaticAlpha.answer(), Is.EqualTo(42));
        Assert.That(BidirectionalStaticAlpha.answer(2), Is.EqualTo(42));
        Assert.That(BidirectionalStaticBeta.answer(), Is.EqualTo(84));
        Assert.That(BidirectionalStaticAlpha.echo("typed"), Is.EqualTo("typed"));
        Assert.That(LibraryKt.bidirectionalStaticCalls(), Is.EqualTo("42:42:84:ok:7:9:11"));

        static MethodInfo[] Implementations(string name) => typeof(BidirectionalStaticAlpha).Assembly.GetTypes()
            .Where(type => type.DeclaringType is null && type.IsAbstract && type.IsSealed &&
                type.IsDefined(typeof(ExtensionAttribute), inherit: false))
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic |
                BindingFlags.Static | BindingFlags.DeclaredOnly))
            .Where(method => method.Name == name)
            .ToArray();

        var answers = Implementations("answer");
        Assert.That(answers, Has.Length.EqualTo(3));
        Assert.That(answers.All(method => method.IsPublic), Is.True);
        Assert.That(Implementations("internalAnswer").Single().IsAssembly, Is.True);
        Assert.That(Implementations("privateAnswer").Single().IsPrivate, Is.True);
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
        // A nested Kotlin class becomes a real CLR nested type (kotc flattens it into the file's type list with a
        // `nestedIn` marker); its ctor param must be annotated like any other.
        var nestedCtorParam = typeof(BidirectionalNullableCtor.Nested)
            .GetConstructors().Single().GetParameters().Single();
        Assert.That(NullableByte(ctorParam), Is.EqualTo((byte)2), "ctor param lost its NullableAttribute");
        Assert.That(NullableByte(methodParam), Is.EqualTo((byte)2), "method param lost its NullableAttribute");
        Assert.That(NullableByte(nestedCtorParam), Is.EqualTo((byte)2), "nested-type ctor param lost its NullableAttribute");

        Assert.That(new BidirectionalNullableCtor(null).labelLength(), Is.EqualTo(-1));
        Assert.That(new BidirectionalNullableCtor("abcd").labelLength(), Is.EqualTo(4));
        Assert.That(new BidirectionalNullableCtor.Nested(null).tagLength(), Is.EqualTo(-1));
    }

    // #383 — Kotlin declares ONE companion object on the generic class declaration, and that companion does not have
    // the owner's `T` as a type parameter of its own. A carrier nested in the owner would nevertheless land in a
    // different CLR static region per closed instantiation, so `Host<int>` and `Host<string>` would each get their own
    // companion and their own state. C# is the only consumer that can name those instantiations separately, so it is
    // the only place this contract can be asserted.
    [Test]
    public void KotlinCompanionOfAGenericOwnerIsOneObjectAcrossClosedInstantiations()
    {
        object fromInt = BidirectionalGenericCompanionHost<int>.Companion;
        object fromString = BidirectionalGenericCompanionHost<string>.Companion;
        Assert.That(ReferenceEquals(fromInt, fromString), Is.True,
            "a generic owner's companion must be one object for every closed instantiation");

        // The physical shape that guarantees it: one non-generic TypeDef beside the owner rather than inside it.
        var carrier = fromInt.GetType();
        Assert.That(carrier.DeclaringType, Is.Null, "the carrier of a generic owner must not be nested in it");
        Assert.That(carrier.GetGenericArguments(), Is.Empty, "the carrier must declare no generic parameters");
        Assert.That(typeof(BidirectionalGenericCompanionHost<int>).GetField("Companion")!.IsInitOnly, Is.True,
            "the C# companion accessor must not be replaceable");
        Assert.That(carrier.GetField("$INSTANCE")!.IsInitOnly, Is.True,
            "the singleton store must not be replaceable");

        BidirectionalGenericCompanionHost<int>.Companion.opened = 5;
        Assert.That(BidirectionalGenericCompanionHost<string>.Companion.opened, Is.EqualTo(5),
            "companion state must be shared across closed instantiations");
        BidirectionalGenericCompanionHost<string>.Companion.opened = 0;

        // Hoisting costs the carrier CLR nested access to the owner's private members; Kotlin's lexical access
        // must survive that unchanged.
        Assert.That(
            BidirectionalGenericCompanionHost<int>.Companion.peek(new BidirectionalGenericCompanionHost<int>(1)),
            Is.EqualTo(7));
    }
}
