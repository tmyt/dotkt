using NUnit.Framework;
using RoundtripDependentNative;
using roundtrip.selectedbounds;

public class FixedNativePairTests
{
    sealed class MoreSpecific : Concrete {}
    sealed class Derived : KotlinPairReader
    {
        public int Picks;
        public override T Pick<T>(T item) {
            Assert.That(typeof(T), Is.EqualTo(item is Concrete ? typeof(Concrete) : item.GetType()));
            ++Picks;
            return item;
        }
        public override string Read<A,B>(A first, B second) =>
            typeof(A) == typeof(Concrete) && typeof(B) == typeof(JointValue) ? "C#" : "wrong generic arguments";
        public override T Replace<T>(ref T slot, T next) { slot = next; return next; }
        public override string Describe<T>(System.Collections.Generic.Queue<T> items) => typeof(T) == typeof(Concrete) ? "C#" : "wrong";
    }

    [Test]
    public void BothViewsReachTheSameMostDerivedOverride()
    {
        foreach (var (reader, expected) in new (KotlinPairReader, string)[] { (new KotlinPairReader(), "Kotlin"), (new Derived(), "C#") })
        {
            NativePairReader native = reader;
            Assert.That(native.Read(new Concrete(), new JointValue()), Is.EqualTo(expected));
            Assert.That(FixedNativePairKt.readPair(reader, new Concrete(), new JointValue()), Is.EqualTo(expected));
            Assert.That(FixedNativePairKt.readQueue(reader), Is.EqualTo(expected[0]));
            var first = new MoreSpecific();
            var next = new Concrete();
            Assert.That(FixedNativePairKt.pickPair(reader, first), Is.True);
            if (reader is Derived child) Assert.That(child.Picks, Is.EqualTo(2));
            Assert.That(FixedNativePairKt.replacePair(reader, first, next), Is.SameAs(reader is Derived ? next : first));
        }
    }
}
