using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;

// The C# delegate declarations below are an independent consumer of emitted native signatures.
// They do not use the compiler's TypeNode reader or a generic argument containing a pointer.
internal static unsafe class FunctionPointerSignatures
{
    static JsonObject Named(string name) => new() { ["t"] = "fqn", ["name"] = name };
    static JsonObject Variable(string scope) => new() { ["t"] = "tv", ["scope"] = scope, ["i"] = 0 };
    static JsonObject Pointer(int convention, JsonNode result, params JsonNode[] parameters) => new() {
        ["t"] = "fnPtr", ["cc"] = convention, ["genericArity"] = 0, ["requiredParams"] = parameters.Length,
        ["ret"] = result.DeepClone(), ["params"] = new JsonArray(parameters.Select(p => p.DeepClone()).ToArray()),
    };
    static JsonObject Owner() => new() { ["t"] = "fqn", ["name"] = "FunctionPointerMethods", ["args"] = new JsonArray(Variable("type")) };
    static JsonObject Local(string name) => new() { ["k"] = "local", ["name"] = name };
    static JsonObject Method(string name, JsonNode result, JsonArray parameters, params JsonNode[] body) => new() {
        ["name"] = name, ["vis"] = "public", ["static"] = true, ["override"] = false, ["virtual"] = false,
        ["params"] = parameters, ["ret"] = result.DeepClone(), ["body"] = new JsonArray(body),
    };

    internal static void Add(JsonObject document)
    {
        var typePointer = Pointer(0, Variable("type"), Variable("type"));
        var methodPointer = Pointer(0, Variable("method"), Variable("type"), Variable("method"));
        var cdecl = Pointer(1, Named("System.Int32"), Named("System.Int32"));
        var echo = Method("Echo", methodPointer,
            new JsonArray(new JsonObject { ["name"] = "p", ["type"] = methodPointer.DeepClone() }),
            new JsonObject { ["k"] = "var", ["name"] = "copy", ["type"] = methodPointer.DeepClone(), ["init"] = Local("p") },
            new JsonObject { ["k"] = "return", ["value"] = Local("copy") });
        echo["typeParams"] = new JsonArray("U");
        var fields = new JsonArray();
        var methods = new JsonArray(echo);
        foreach (var (name, pointer) in new[] { ("Pointer", typePointer), ("Cdecl", cdecl) })
        {
            fields.Add(new JsonObject { ["name"] = name, ["vis"] = "public", ["static"] = true, ["type"] = pointer.DeepClone() });
            methods.Add(Method("Set" + name, Named("System.Void"),
                new JsonArray(new JsonObject { ["name"] = "p", ["type"] = pointer.DeepClone() }),
                new JsonObject { ["k"] = "exprStmt", ["expr"] = new JsonObject {
                    ["k"] = "staticFieldSet", ["ownerType"] = Owner(), ["name"] = name, ["value"] = Local("p") } }));
            methods.Add(Method("Get" + name, pointer, new JsonArray(),
                new JsonObject { ["k"] = "return", ["value"] = new JsonObject { ["k"] = "staticField", ["ownerType"] = Owner(), ["name"] = name } }));
        }
        fields.Add(new JsonObject { ["name"] = "Pointers", ["vis"] = "public", ["static"] = true,
            ["type"] = new JsonObject { ["t"] = "array", ["elem"] = typePointer.DeepClone() } });
        fields.Add(new JsonObject { ["name"] = "NestedPointer", ["vis"] = "public", ["static"] = true,
            ["type"] = new JsonObject { ["t"] = "ptr", ["of"] = typePointer.DeepClone() } });
        document["types"]!.AsArray().Add(new JsonObject {
            ["name"] = "FunctionPointerMethods", ["kind"] = "class", ["vis"] = "public", ["typeParams"] = new JsonArray("T"),
            ["base"] = Named("System.Object"), ["interfaces"] = new JsonArray(), ["ctors"] = new JsonArray(),
            ["fields"] = fields, ["methods"] = methods, ["properties"] = new JsonArray(new JsonObject {
                ["name"] = "Value", ["type"] = typePointer.DeepClone(), ["get"] = "GetPointer",
                ["getSig"] = new JsonArray(), ["getRet"] = typePointer.DeepClone() }),
        });
    }

    delegate delegate* managed<string, int, int> Echo(delegate* managed<string, int, int> callback);
    delegate void SetManaged(delegate* managed<string, string> callback);
    delegate delegate* managed<string, string> GetManaged();
    delegate void SetCdecl(delegate* unmanaged[Cdecl]<int, int> callback);
    delegate delegate* unmanaged[Cdecl]<int, int> GetCdecl();
    static int Add(string value, int n) => value.Length + n;
    static string Identity(string value) => value;
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static int Twice(int n) => n * 2;

    internal static void Verify(Assembly assembly)
    {
        var definition = assembly.GetType("FunctionPointerMethods`1")!;
        var owner = definition.MakeGenericType(typeof(string));
        var echo = (Echo)owner.GetMethod("Echo")!.MakeGenericMethod(typeof(int)).CreateDelegate(typeof(Echo));
        if (echo(&Add)("abc", 39) != 42) throw new Exception("Function-pointer method/local generic frames changed");
        ((SetManaged)owner.GetMethod("SetPointer")!.CreateDelegate(typeof(SetManaged)))(&Identity);
        var getter = (GetManaged)owner.GetProperty("Value")!.GetMethod!.CreateDelegate(typeof(GetManaged));
        if (getter()("ok") != "ok") throw new Exception("Function-pointer field/property identity changed");
        ((SetCdecl)owner.GetMethod("SetCdecl")!.CreateDelegate(typeof(SetCdecl)))(&Twice);
        var cdecl = (GetCdecl)owner.GetMethod("GetCdecl")!.CreateDelegate(typeof(GetCdecl));
        if (cdecl()(21) != 42) throw new Exception("Native calling convention changed");
        if (!definition.GetField("Pointers")!.FieldType.GetElementType()!.IsFunctionPointer
            || !definition.GetField("NestedPointer")!.FieldType.GetElementType()!.IsFunctionPointer)
            throw new Exception("Nested function-pointer identity changed");
        Console.WriteLine("Function-pointer declaration/local/field/property/compound signatures and managed/unmanaged execution OK");
    }
}
