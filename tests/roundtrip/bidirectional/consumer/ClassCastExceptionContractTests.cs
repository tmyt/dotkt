using System;
using NUnit.Framework;
using roundtrip.castcontract;

namespace DotKt.Tests.Bidirectional;

public class ClassCastExceptionContractTests
{
    [Test]
    public void KotlinCastsAndExplicitThrowsExposeTheNativeExceptionContract()
    {
        var api = new CastExceptionApi();
        Assert.Throws<InvalidCastException>(() => api.failValue("wrong"));
        Assert.Throws<InvalidCastException>(() => api.failClassifier("wrong"));
        var failure = Assert.Throws<InvalidCastException>(() => api.failAsKotlin("explicit"));
        Assert.That(failure!.Message, Is.EqualTo("explicit"));
        InvalidCastException created = api.construct("factory");
        Assert.That(created.GetType(), Is.EqualTo(typeof(InvalidCastException)));
        Assert.That(created.Message, Is.EqualTo("factory"));
    }
}
