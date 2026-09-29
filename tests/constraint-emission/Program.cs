using System.Reflection;
using System.Text.Json.Nodes;

// Test physical CIR constraints without depending on Kotlin representation choices.
string[][] cases = [
    ["System.Exception", "N"], ["N", "System.Exception"], ["N"], ["N", "P"],
    ["System.IDisposable", "N"], ["System.Exception", "N", "System.IDisposable"]
];
if (args[0] == "generate")
{
    var source = JsonNode.Parse(File.ReadAllText(args[1]))!;
    var root = new JsonObject {
        ["fileClass"] = "ConstraintMethods", ["hasMain"] = false,
        ["wellKnownRefs"] = source["wellKnownRefs"]!.DeepClone(),
        ["fields"] = new JsonArray(), ["types"] = new JsonArray(), ["methods"] = new JsonArray()
    };
    for (var i = 0; i < cases.Length; i++)
    {
        root["methods"]!.AsArray().Add(new JsonObject {
            ["name"] = "Check" + i, ["static"] = true, ["vis"] = "public",
            ["override"] = false, ["virtual"] = false, ["abstract"] = false,
            ["typeParams"] = Parameters(cases[i], "method"),
            ["params"] = new JsonArray(), ["ret"] = Named("void"), ["body"] = new JsonArray()
        });
        root["types"]!.AsArray().Add(new JsonObject {
            ["name"] = "ConstraintType" + i, ["kind"] = "class", ["vis"] = "public",
            ["typeParams"] = Parameters(cases[i], "type"), ["base"] = Named("System.Object"),
            ["fields"] = new JsonArray(), ["ctors"] = new JsonArray(), ["methods"] = new JsonArray()
        });
    }
    File.WriteAllText(args[2], root.ToJsonString());
    return;
}
var assembly = Assembly.LoadFile(Path.GetFullPath(args[1]));
for (var i = 0; i < cases.Length; i++)
{
    var method = assembly.GetType("ConstraintMethods")!.GetMethod("Check" + i)!;
    var type = assembly.GetType("ConstraintType" + i + "`3")!;
    foreach (var parameters in new[] { method.GetGenericArguments(), type.GetGenericArguments() })
    {
        var actual = parameters[0].GetGenericParameterConstraints()
            .Select(t => t.IsGenericParameter ? t.Name : t.FullName!).Order().ToArray();
        if (!actual.SequenceEqual(cases[i].Order()))
            throw new Exception($"Case {i}: expected {string.Join(",", cases[i])}; got {string.Join(",", actual)}");
        if (parameters.Skip(1).Any(p => p.GetGenericParameterConstraints().Length != 0))
            throw new Exception("Constraint leaked to another generic parameter");
    }
    Type[] valid = [typeof(DisposableException), typeof(Exception), typeof(object)];
    method.MakeGenericMethod(valid).Invoke(null, null);
    _ = type.MakeGenericType(valid);
    // Violate each bound independently, retaining the other bounds.
    foreach (var bound in cases[i])
    {
        Type[] invalid = (Type[])valid.Clone();
        if (bound == "N") invalid[1] = typeof(string);
        else if (bound == "P") invalid[2] = typeof(string);
        else if (bound == "System.IDisposable") invalid[0] = typeof(Exception);
        else { invalid[0] = typeof(MemoryStream); invalid[1] = typeof(object); }
        Reject(() => method.MakeGenericMethod(invalid));
        Reject(() => type.MakeGenericType(invalid));
    }
}
Console.WriteLine("PASS: class/method constraint rows, ordering, parameter-only/multiple/interface bounds and runtime enforcement");

static JsonObject Named(string name) => new() { ["t"] = "fqn", ["name"] = name };
static JsonArray Parameters(string[] constraints, string scope) => new(
    new JsonObject { ["name"] = "T", ["constraints"] = new JsonArray(constraints.Select(s =>
        (JsonNode)(s is "N" or "P" ? new JsonObject {
            ["t"] = "tv", ["scope"] = scope, ["i"] = s == "N" ? 1 : 2
        } : Named(s))).ToArray()) }, JsonValue.Create("N"), JsonValue.Create("P"));
static void Reject(Action action)
{
    try { action(); }
    catch (ArgumentException) { return; }
    throw new Exception("Invalid generic instantiation was accepted");
}
public sealed class DisposableException : Exception, IDisposable { public void Dispose() { } }
