namespace ProjectedNativeBase;

public class Root { }
public interface IMark { }
public class NativeRoot<A> where A : Root, IMark { }
