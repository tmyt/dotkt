var reader = new DerivedReader();
var key = new MoreSpecificKey();
if (!ReferenceEquals(key, FixedNativePairKt.readSelf(reader, key)) || reader.Calls != 1)
    throw new System.Exception("The separate Kotlin interface missed the native override");
var ordinary = new OrdinarySelfReader();
if (!ReferenceEquals(key, FixedNativePairKt.readSelf(ordinary, key)) || ordinary.calls != 1)
    throw new System.Exception("The interface default dispatch missed the ordinary Kotlin override");
FixedNativePairKt.checkRelatedCalls();

sealed class MoreSpecificKey : SelfKey {}
sealed class DerivedReader : KotlinSelfReader
{
    public int Calls;
    public override T PickSelf<T>(T key)
    {
        if (typeof(T) != typeof(SelfKey)) throw new System.Exception("Lost the declared type argument");
        Calls++;
        return key;
    }
}
