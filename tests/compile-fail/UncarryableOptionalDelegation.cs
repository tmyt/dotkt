namespace UncarryableOptionalDelegation;

public class Key<T> { }

public class Base
{
    public Base(Key<int> key, [System.Runtime.InteropServices.Optional] object value) { }
    public Base(Key<string> key, object value = null) { }
}
