using System.Reflection;
using NUnit.Framework;

public class VirtualIndexerRoundtripTests
{
    [Test]
    public void ExportedIndexersPreserveDispatchAndProtectedVisibility()
    {
        VirtualIndexers.Grid grid = new ExportedVirtualIndexer();
        Assert.That(grid[2], Is.EqualTo(119));
        grid[2] = 41;
        Assert.That(grid[2], Is.EqualTo(341));
        var restricted = new ExportedProtectedIndexer();
        Assert.That(restricted.Read(1), Is.EqualTo(31));
        restricted.Write(1, 53);
        Assert.That(restricted.Read(1), Is.EqualTo(53));
        const BindingFlags flags = BindingFlags.DeclaredOnly | BindingFlags.Instance
            | BindingFlags.Public | BindingFlags.NonPublic;
        Assert.That(typeof(ExportedProtectedIndexer).GetMethod("get_Entry", flags)!.IsFamily, Is.True);
        Assert.That(typeof(ExportedProtectedIndexer).GetMethod("set_Entry", flags)!.IsFamily, Is.True);
        VirtualIndexers.NominalGrid nominal = new ExportedCovariantIndexer();
        Assert.That(nominal[1], Is.TypeOf<VirtualIndexers.ResultDerived>());
    }
}
