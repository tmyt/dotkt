using NUnit.Framework;

namespace DotKt.Tests.Bidirectional;

public class VirtualArrayBoundaryTests
{
    sealed class Native : ArrayInspection
    {
        public int Calls;
        public object inspect(object value) { Calls++; return value; }
    }
    sealed class Inherits : KotlinArrayInspection { }
    struct NativeRef : RefArrayInspection
    {
        public static int Calls;
        public object inspectRef(ref object value)
        {
            Calls++;
            var original = value;
            value = "written";
            return original;
        }
    }
    sealed class InheritsRef : KotlinRefInspection { }
    sealed class Overrides : KotlinArrayInspection
    {
        public int Calls;
        public override object inspect(object value) { Calls++; return value; }
    }
    sealed class Reimplements : KotlinArrayInspection, ArrayInspection
    {
        public int Calls;
        object ArrayInspection.inspect(object value) { Calls++; return value; }
    }

    [Test]
    public void PublishesForActualNativeBodiesButNotInheritedKotlinBodies()
    {
        NativeRef.Calls = 0;
        Assert.Throws<InvalidCastException>(() => VirtualArrayBoundaryKt.inspectRefThroughKotlin(new NativeRef(), false));
        Assert.That(NativeRef.Calls, Is.Zero);
        Assert.That(VirtualArrayBoundaryKt.inspectRefThroughKotlin(new NativeRef(), true), Is.True);
        Assert.That(NativeRef.Calls, Is.EqualTo(1));
        Assert.That(VirtualArrayBoundaryKt.inspectRefThroughKotlin(new InheritsRef(), false), Is.True);
        foreach (bool bound in new[] { false, true })
        {
            var direct = new Native();
            var derived = new Overrides();
            var explicitSlot = new Reimplements();
            foreach (var view in new ArrayInspection[] { direct, derived, explicitSlot })
            {
                Assert.Throws<InvalidCastException>(() => VirtualArrayBoundaryKt.inspectThroughKotlin(view, bound, false));
                Assert.That(VirtualArrayBoundaryKt.inspectThroughKotlin(view, bound, true), Is.True);
            }
            Assert.That(new[] { direct.Calls, derived.Calls, explicitSlot.Calls }, Is.EqualTo(new[] { 1, 1, 1 }));
            Assert.That(VirtualArrayBoundaryKt.inspectThroughKotlin(new Inherits(), bound, false), Is.True);
        }
    }
}
