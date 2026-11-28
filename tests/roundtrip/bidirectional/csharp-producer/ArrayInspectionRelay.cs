public interface IArrayInspectionRelay
{
    object Inspect<S>(S first, S replacement) where S : ArrayInspection;
}

public static class ArrayReceiverArgument
{
    public static object Replace<T>(ref T receiver, T replacement, object argument)
    {
        receiver = replacement;
        return argument;
    }
}
