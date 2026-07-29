namespace ExplicitMethodInterop;

public interface IOperations
{
    int Compute(int value);
    string Compute(string value);
    T Echo<T>(T value);
    string Name { get; }
}

public class ExplicitOperations : IOperations
{
    int IOperations.Compute(int value) => value + 10;

    string IOperations.Compute(string value) => value + "!";

    T IOperations.Echo<T>(T value) => value;

    string IOperations.Name => "explicit";
}

public interface IBaseOperation
{
    int BaseCompute(int value);
}

public interface IDerivedOperation : IBaseOperation
{
}

public class InheritedExplicitOperation : IDerivedOperation
{
    int IBaseOperation.BaseCompute(int value) => value + 20;
}

public interface ITransformer<T>
{
    T Transform(T value);
}

public class StringTransformer : ITransformer<string>
{
    string ITransformer<string>.Transform(string value) => value + "?";
}
