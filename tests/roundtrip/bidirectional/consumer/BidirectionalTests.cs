using System.Collections.Generic;
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
}
