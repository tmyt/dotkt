using NUnit.Framework;
using RoundtripDependentNative;
using roundtrip.selectedbounds;

public class FixedNativePairTests
{
    sealed class Derived : KotlinPairReader
    {
        public int Picks;
        public override T Pick<T>(T item) {
            Assert.That(typeof(T), Is.EqualTo(item.GetType()));
            ++Picks;
            return item;
        }
        public override string Read<A,B>(A first, B second) =>
            typeof(A) == typeof(Concrete) && typeof(B) == typeof(JointValue) ? "C#" : "wrong generic arguments";
        public override T Replace<T>(ref T slot, T next) { slot = next; return next; }
    }

    [Test]
    public void BothViewsReachTheSameMostDerivedOverride()
    {
        foreach (var (reader, expected) in new (KotlinPairReader, string)[] { (new KotlinPairReader(), "Kotlin"), (new Derived(), "C#") })
        {
            NativePairReader native = reader;
            Assert.That(native.Read(new Concrete(), new JointValue()), Is.EqualTo(expected));
            Assert.That(FixedNativePairKt.readPair(reader, new Concrete(), new JointValue()), Is.EqualTo(expected));
            var first = new Concrete();
            var next = new Concrete();
            Assert.That(FixedNativePairKt.pickPair(reader, first), Is.True);
            if (reader is Derived child) Assert.That(child.Picks, Is.EqualTo(2));
            Assert.That(FixedNativePairKt.replacePair(reader, first, next), Is.SameAs(reader is Derived ? next : first));
        }
    }
}
