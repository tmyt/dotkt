namespace InheritedNativeSpecialConstraint;
public interface IFactory { string Make<T>(); }
public class NativeBase { public virtual string Make<T>() where T : class => "native"; }
