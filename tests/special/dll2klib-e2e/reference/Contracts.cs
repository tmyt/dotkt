using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Probe")]

namespace Probe.Contracts;

public delegate int ExternalTransformer(int value);

public delegate TResult ExternalGenericTransformer<T, TResult>(T value);

public interface IVisibleGeneric<T>
{
}

public interface IVisibleEnvelope<T>
{
}

internal sealed class HiddenContractArgument
{
}
