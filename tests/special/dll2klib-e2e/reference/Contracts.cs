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

public interface IExternalDefaultSlot
{
    int Value();
}

public interface IExternalExplicitEventSlot
{
    event System.Action<int>? Changed;
}

internal interface IExternalHiddenDefaultProvider : IExternalDefaultSlot
{
    int IExternalDefaultSlot.Value() => 31;
}

internal sealed class HiddenContractArgument
{
}
