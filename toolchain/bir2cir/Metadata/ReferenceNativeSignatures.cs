using System.Reflection;
using DotKt.Bir;
using DotKt.Bir.Lowering;

// One compilation owns the decoder cache. Reflection handles are query inputs,
// never retained; consumers receive independent structured signature vectors.
sealed class ReferenceNativeSignatures : IDisposable
{
    NativeSignatureReader _reader = new();

    internal NativeSignatureReader.Signature Method(MethodBase declaration)
    {
        ObjectDisposedException.ThrowIf(_reader == null, this);
        return _reader.Method(declaration) is { } signature
            ? new(BirGraphValue.CopyType(signature.Return),
                signature.Parameters.Select(BirGraphValue.CopyType).ToArray()) : null;
    }

    internal TypeNode Slot(ParameterInfo parameter)
    {
        ObjectDisposedException.ThrowIf(_reader == null, this);
        return parameter.Member is MethodBase method && _reader.Method(method) is { } signature
            ? BirGraphValue.CopyType(parameter.Position < 0
                ? signature.Return : signature.Parameters[parameter.Position]) : null;
    }

    internal TypeNode Field(FieldInfo declaration)
    {
        ObjectDisposedException.ThrowIf(_reader == null, this);
        return _reader.Field(declaration) is { } signature ? BirGraphValue.CopyType(signature) : null;
    }

    public void Dispose() => _reader = null;
}
