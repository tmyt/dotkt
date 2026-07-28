namespace Probe.Contracts;

public delegate int ExternalTransformer(int value);

public delegate TResult ExternalGenericTransformer<T, TResult>(T value);
