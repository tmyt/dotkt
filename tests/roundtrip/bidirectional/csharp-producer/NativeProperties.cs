namespace BidirectionalInterop;

public abstract class NativePropertyBase
{
    public abstract System.IComparable Item { get; set; }
    public virtual string Caption => "base";
    protected virtual int ProtectedValue => 1;
    public int ReadProtected() => ProtectedValue;
}
