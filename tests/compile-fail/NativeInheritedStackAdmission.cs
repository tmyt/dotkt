namespace NativeInheritedStackAdmission;
public interface IFactory { object Make<S, U>() where U : allows ref struct; }
public class BaseFactory { public virtual object Make<S, U>() where S : allows ref struct => "base"; }
