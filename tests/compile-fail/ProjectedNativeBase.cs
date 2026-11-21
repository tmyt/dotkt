namespace ProjectedNativeBase;

public class Root { }
public interface IMark { }
public class NativeRoot<A> where A : Root, IMark { }
public interface INative<A> where A : Root, IMark { }
