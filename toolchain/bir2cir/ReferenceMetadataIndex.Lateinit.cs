using System.Reflection;
using System.Text.Json.Nodes;

sealed record LateinitStateTarget(string Owner, string Method, bool Static);

sealed partial class ReferenceMetadataIndex
{
    const string KotlinLateinitStateAttr = "DotKt.Runtime.CompilerServices.KotlinLateinitStateAttribute";
    internal LateinitStateTarget LateinitStateForDeclaration(string id) =>
        _declarationById.TryGetValue(id, out var getter) && getter.LateinitState is { } target
            ? target
            : throw new InvalidOperationException($"Kotlin lateinit declaration '{id}' has no initialization-state binding");

    static LateinitStateTarget ReadLateinitState(MethodInfo getter)
    {
        var payload = CarrierJsonOf(getter.GetCustomAttributesData(), getter.DeclaringType?.Assembly,
            KotlinLateinitStateAttr) as JsonObject;
        if (payload == null) return null;
        if (payload.Count != 3 || payload["owner"] is not JsonValue ownerValue
            || !ownerValue.TryGetValue<string>(out var owner) || string.IsNullOrEmpty(owner)
            || payload["method"] is not JsonValue methodValue
            || !methodValue.TryGetValue<string>(out var method) || string.IsNullOrEmpty(method)
            || payload["static"] is not JsonValue staticValue || !staticValue.TryGetValue<bool>(out var isStatic))
            throw new InvalidDataException("malformed [KotlinLateinitState] payload");
        return new(owner, method, isStatic);
    }
}
