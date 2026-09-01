using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using BidirectionalInterop;
using NUnit.Framework;

public class BidirectionalTests
{
    private static T EnumIdentity<T>(T value) where T : struct, Enum => value;

    private sealed class CSharpPropertyOverride : BidirectionalPropertyBase
    {
        private int storage;

        public CSharpPropertyOverride() : base(0) { }

        public override int value
        {
            get => storage;
            set => storage = value;
        }

        public override int get_value() => 200;
        public override void set_value(int next) => storage = next + 200;
    }

    private sealed class CSharpPropertyImplementation : BidirectionalPropertyInterface
    {
        private int storage;

        int BidirectionalPropertyInterface.value
        {
            get => storage;
            set => storage = value;
        }

        public int get_value() => 300;
        public void set_value(int next) => storage = next + 300;
    }

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
    public void CSharpCallsKotlinManagedReferenceParameters()
    {
        var value = 7;
        Assert.That(LibraryKt.bidirectionalRefIncrement(ref value, 5), Is.EqualTo(12));
        Assert.That(value, Is.EqualTo(12), "the Kotlin callee must write through the caller's slot");

        var first = "left";
        var second = "right";
        LibraryKt.bidirectionalRefSwap<string>(ref first, ref second);
        Assert.That((first, second), Is.EqualTo(("right", "left")));

        var firstNumber = 1;
        var secondNumber = 2;
        LibraryKt.bidirectionalRefSwap<int>(ref firstNumber, ref secondNumber);
        Assert.That((firstNumber, secondNumber), Is.EqualTo((2, 1)));

        var method = typeof(LibraryKt).GetMethod(nameof(LibraryKt.bidirectionalRefIncrement))!;
        Assert.That(method.GetParameters()[0].ParameterType, Is.EqualTo(typeof(int).MakeByRefType()),
            "ClrRef<Int> must be exported as int&, not as a materialized wrapper class");
    }

    [Test]
    public void CSharpConsumesKotlinExplicitClrEnumContract()
    {
        static string Describe(BidirectionalAccess value) => value switch
        {
            BidirectionalAccess.NONE => "none",
            BidirectionalAccess.READ => "read",
            BidirectionalAccess.WRITE => "write",
            BidirectionalAccess.READ_WRITE => "read-write",
            BidirectionalAccess.HIGH => "high",
            _ => "unknown",
        };

        Assert.That(Describe(BidirectionalAccess.WRITE), Is.EqualTo("write"));
        Assert.That(EnumIdentity(BidirectionalAccess.HIGH), Is.EqualTo(BidirectionalAccess.HIGH));
        Assert.That(Enum.GetUnderlyingType(typeof(BidirectionalAccess)), Is.EqualTo(typeof(uint)));
        Assert.That(typeof(BidirectionalAccess).IsDefined(typeof(FlagsAttribute), inherit: false), Is.True);
        Assert.That((uint)BidirectionalAccess.NONE, Is.EqualTo(0u));
        Assert.That((uint)BidirectionalAccess.WRITE, Is.EqualTo(4u));
        Assert.That((uint)BidirectionalAccess.HIGH, Is.EqualTo(0x80000000u));

        var fields = typeof(BidirectionalAccess).GetFields(BindingFlags.Public | BindingFlags.Static);
        Assert.That(fields.Select(field => field.Name), Is.EqualTo(
            new[] { "NONE", "READ", "WRITE", "READ_WRITE", "HIGH" }));
        Assert.That(fields.Select(field => (uint)field.GetRawConstantValue()!), Is.EqualTo(
            new uint[] { 0u, 1u, 4u, 5u, 0x80000000u }));

        Assert.That(LibraryKt.bidirectionalEnumDefault(), Is.EqualTo(BidirectionalAccess.WRITE));
        Assert.That(LibraryKt.bidirectionalEnumOrdinal((BidirectionalAccess)2u), Is.EqualTo(-1));
        var optional = typeof(LibraryKt).GetMethod(nameof(LibraryKt.bidirectionalEnumDefault))!
            .GetParameters().Single();
        Assert.That(optional.IsOptional, Is.True);
        Assert.That(optional.DefaultValue, Is.EqualTo(BidirectionalAccess.WRITE));

        var marker = typeof(BidirectionalAccessMarked).GetCustomAttributesData()
            .Single(attribute => attribute.AttributeType.Name == nameof(BidirectionalAccessMarker));
        var markerValue = marker.ConstructorArguments.Single();
        Assert.That(markerValue.ArgumentType, Is.EqualTo(typeof(BidirectionalAccess)));
        Assert.That(Convert.ToUInt32(markerValue.Value), Is.EqualTo((uint)BidirectionalAccess.READ_WRITE));
    }

    [Test]
    public void CSharpConsumesOverridesAndImplementsDedicatedKotlinPropertyAccessors()
    {
        var derived = new CSharpPropertyOverride();
        BidirectionalPropertyBase throughBase = derived;
        throughBase.value = 7;
        Assert.That(throughBase.value, Is.EqualTo(7));
        Assert.That(throughBase.get_value(), Is.EqualTo(200));
        throughBase.set_value(8);
        Assert.That(throughBase.value, Is.EqualTo(208));

        BidirectionalPropertyInterface throughInterface = new CSharpPropertyImplementation();
        throughInterface.value = 9;
        Assert.That(throughInterface.value, Is.EqualTo(9));
        Assert.That(throughInterface.get_value(), Is.EqualTo(300));
        throughInterface.set_value(10);
        Assert.That(throughInterface.value, Is.EqualTo(310));

        var baseAccessor = typeof(BidirectionalPropertyBase).GetProperty("value")!.GetMethod!;
        var interfaceAccessor = typeof(BidirectionalPropertyInterface).GetProperty("value")!.GetMethod!;
        Assert.That(baseAccessor.Name, Is.EqualTo("prop_get<value>"));
        Assert.That(interfaceAccessor.Name, Is.EqualTo("prop_get<value>"));
        Assert.That(typeof(BidirectionalPropertyBase).GetMethod("get_value"), Is.Not.Null);
        Assert.That(typeof(BidirectionalPropertyInterface).GetMethod("get_value"), Is.Not.Null);
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

    [Test]
    public void GenericCompanionExtensionsUseStandardWrappersAndOneKotlinCore()
    {
        Assert.That(BidirectionalGenericStatic<string>.genericAnswer(), Is.EqualTo(389));
        BidirectionalGenericStatic<int>.genericCounter = 4;
        Assert.That(BidirectionalGenericStatic<string>.genericCounter, Is.EqualTo(4),
            "generic receiver closures must share the Kotlin declaration's one storage slot");
        Assert.That(BidirectionalGenericStatic<object>.echoGeneric(17), Is.EqualTo(17),
            "a source method type parameter must not collide with the synthetic receiver block");
        Assert.That(ReferenceConstrainedTarget<string>.referenceConstraint(), Is.EqualTo("reference"));
        Assert.That(StructConstrainedTarget<int>.structConstraint(), Is.EqualTo("struct"));
        Assert.That(RefLikeConstrainedTarget<Span<int>>.refLikeConstraint(), Is.EqualTo("ref-like"));
        Assert.That(RepeatedGenericOuter<string>.RepeatedGenericInner<int>.repeatedGenericNames(),
            Is.EqualTo("nested-generic"),
            "outer and inner receiver slots with the same metadata name must remain distinct");
        Assert.That(IReadOnlyList<string>.listAliasAnswer(), Is.EqualTo(144),
            "a generic Kotlin alias receiver must lower to its CLR classifier before emitting the marker");

        var assembly = typeof(BidirectionalGenericStatic<>).Assembly;
        var wrappers = assembly.GetTypes()
            .Where(type => type.DeclaringType is null && type.IsAbstract && type.IsSealed &&
                type.IsDefined(typeof(ExtensionAttribute), inherit: false))
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Static |
                BindingFlags.DeclaredOnly))
            .Where(method => method.Name is "genericAnswer" or "prop_get<genericCounter>" or "prop_set<genericCounter>")
            .ToArray();
        Assert.That(wrappers, Has.Length.EqualTo(3));
        Assert.That(wrappers.All(method => method.IsGenericMethodDefinition &&
            method.GetGenericArguments().Length == 1), Is.True,
            "the C# implementation wrappers must carry the receiver block parameter");
        Assert.That(wrappers.All(method => method.GetCustomAttributesData().Any(attribute =>
            attribute.AttributeType.FullName ==
                "DotKt.Runtime.CompilerServices.KotlinExtensionCoreAttribute")), Is.True,
            "every generic wrapper must explicitly name its Kotlin semantic core");

        var refLikeWrapper = assembly.GetTypes()
            .Where(type => type.DeclaringType is null && type.IsAbstract && type.IsSealed)
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Static |
                BindingFlags.DeclaredOnly))
            .Single(method => method.Name == "refLikeConstraint");
        Assert.That(refLikeWrapper.GetGenericArguments().Single().GenericParameterAttributes &
            GenericParameterAttributes.AllowByRefLike, Is.Not.EqualTo(0),
            "the C# wrapper must retain the receiver's allows-ref-struct anti-constraint");
    }

    [Test]
    public void CSharpConsumesKotlinCompanionPropertiesAsStaticExtensionMembers()
    {
        Assert.That(BidirectionalStaticAlpha.label, Is.EqualTo("alpha"));
        Assert.That(BidirectionalStaticAlpha.marker, Is.EqualTo("m"));
        Assert.That(BidirectionalStaticAlpha.code, Is.EqualTo(17));
        BidirectionalStaticAlpha.counter = 3;
        Assert.That(BidirectionalStaticAlpha.counter, Is.EqualTo(3));
        BidirectionalStaticAlpha.later = "csharp";
        Assert.That(BidirectionalStaticAlpha.later, Is.EqualTo("csharp"));
        Assert.That(BidirectionalStaticAlpha.computed, Is.EqualTo(10));
        Assert.That(BidirectionalStaticAlpha.restricted, Is.EqualTo(1));
        LibraryKt.updateRestrictedCompanionProperty();
        Assert.That(BidirectionalStaticAlpha.restricted, Is.EqualTo(2));
        Assert.That(BidirectionalStaticBeta.label, Is.EqualTo("beta"));
        Assert.That(LibraryKt.bidirectionalStaticPropertyCalls(), Is.EqualTo("alpha:m:17:6:ready:21:beta"));
        Assert.That(LibraryKt.bidirectionalCompanionExtensionInitializationOrder(), Is.EqualTo("A:B:AB"));
        Assert.That(LibraryKt.bidirectionalCompanionExtensionInitializationOrder(), Is.EqualTo("A:B:AB"),
            "field-backed companion extensions must initialize exactly once");

        var container = typeof(BidirectionalStaticAlpha).Assembly.GetTypes()
            .Single(type => type.DeclaringType is null && type.IsAbstract && type.IsSealed &&
                type.IsDefined(typeof(ExtensionAttribute), inherit: false) &&
                type.GetMethod("prop_get<label>", BindingFlags.Public | BindingFlags.Static) is not null &&
                type.GetMethod("prop_get<counter>", BindingFlags.Public | BindingFlags.Static) is not null);
        foreach (var name in new[] { "prop_get<label>", "prop_get<marker>", "prop_get<code>",
                     "prop_get<counter>", "prop_set<counter>", "prop_get<later>", "prop_set<later>",
                     "prop_get<computed>" })
        {
            var accessor = container.GetMethod(name, BindingFlags.Public | BindingFlags.Static)!;
            Assert.That(accessor.IsSpecialName, Is.False,
                $"implementation accessor {name} must remain an ordinary executable method");
        }
        Assert.That(container.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static), Is.Empty,
            "receiver-partitioned extension containers must not split the file's static storage");
        var storage = typeof(LibraryKt).GetFields(BindingFlags.NonPublic | BindingFlags.Static)
            .Where(field => field.Name.EndsWith("$storage", StringComparison.Ordinal))
            .ToArray();
        Assert.That(storage, Has.Length.EqualTo(8));
        Assert.That(storage.All(field => field.IsPrivate), Is.True,
            "companion extension storage must remain private on its single initialization owner");
        Assert.That(container.GetMethod("prop_set<computed>", BindingFlags.NonPublic | BindingFlags.Static)!.IsPrivate, Is.True,
            "a private Kotlin setter must remain a private implementation accessor");
        Assert.That(container.GetMethod("prop_set<restricted>", BindingFlags.NonPublic | BindingFlags.Static)!.IsPrivate, Is.True,
            "a field-backed var's private default setter must survive the C# 14 graph");
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
