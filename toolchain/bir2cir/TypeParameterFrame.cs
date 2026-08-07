using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

// The CLR generic frame of a nested TypeDef is the captured enclosing prefix followed by the declaration's own
// parameters. TypeOwnershipLowering separates those two representation facts so dll2klib can recover Kotlin's own
// arity, but every later pass that resolves !N slots or constructs the physical owner must use the complete frame.
static class TypeParameterFrame
{
    public static IEnumerable<JsonNode> Declarations(JsonObject type)
    {
        if (type?["capturedTypeParams"] is JsonArray captured)
            foreach (var parameter in captured) yield return parameter;
        if (type?["typeParams"] is JsonArray declared)
            foreach (var parameter in declared) yield return parameter;
    }

    public static int Count(JsonObject type) => Declarations(type).Count();

    public static JsonArray CloneDeclarations(JsonObject type) =>
        new(Declarations(type).Select(parameter => parameter?.DeepClone()).ToArray());
}
