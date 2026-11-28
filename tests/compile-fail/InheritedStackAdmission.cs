namespace InheritedStackAdmission;
public interface IFactory
{
    object Make<S, U>() where S : allows ref struct;
    object Other<S, U>() => "other";
}
