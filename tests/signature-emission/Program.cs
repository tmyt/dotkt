using System.Reflection;
using System.Text.Json.Nodes;

// Isolate physical signature emission from Kotlin representation decisions.
if (args[0] == "generate")
{
    var source = JsonNode.Parse(File.ReadAllText(args[1]))!;
    var root = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "methods.cir.json")))!;
    root["wellKnownRefs"] = source["wellKnownRefs"]!.DeepClone();
    var overloads = root["methods"]!.AsArray().Where(m => m!["name"]!.GetValue<string>() == "Select").ToArray();
    var constructorRows = new JsonArray();
    var ownerGenericMethods = new JsonArray();
    for (var i = 0; i < overloads.Length; i++)
    {
        var original = overloads[i]!;
        constructorRows.Add(new JsonObject {
            ["vis"] = "public", ["params"] = original["params"]!.DeepClone(), ["body"] = new JsonArray(),
            ["baseCtorRef"] = source["wellKnownRefs"]!["Object.ctor"]!.DeepClone()
        });
        var generic = original.DeepClone();
        generic["name"] = "GenericSelect";
        generic["typeParams"] = new JsonArray("T");
        var variable = new JsonObject { ["t"] = "tv", ["scope"] = "method", ["i"] = 0 };
        generic["params"]![0]!["type"]!["m"] = new JsonObject {
            ["t"] = "fqn", ["name"] = i == 0 ? "System.Action" : "System.Func",
            ["args"] = i == 0 ? new JsonArray(variable.DeepClone()) : new JsonArray(variable.DeepClone(), variable.DeepClone())
        };
        root["methods"]!.AsArray().Add(generic);
        root["methods"]!.AsArray().Add(Call("CallGeneric" + i, Named("ModifiedMethods"), generic,
            new JsonArray(Named("System.Int32"))));
        var ownerGeneric = generic.DeepClone();
        ownerGeneric.AsObject().Remove("typeParams");
        foreach (var parameter in ownerGeneric["params"]![0]!["type"]!["m"]!["args"]!.AsArray())
            parameter!["scope"] = "type";
        ownerGenericMethods.Add(ownerGeneric);
        var constructedOwner = Named("GenericMethods");
        constructedOwner["args"] = new JsonArray(Named("System.Int32"));
        root["methods"]!.AsArray().Add(Call("CallOwner" + i, constructedOwner, ownerGeneric));
        var returnOverload = original.DeepClone();
        returnOverload["name"] = "ReturnSelect";
        returnOverload["params"] = new JsonArray();
        returnOverload["ret"] = new JsonObject {
            ["t"] = "mod", ["req"] = false, ["m"] = original["params"]![0]!["type"]!["m"]!.DeepClone(),
            ["of"] = original["ret"]!.DeepClone()
        };
        root["methods"]!.AsArray().Add(returnOverload);
        root["properties"]!.AsArray().Add(new JsonObject {
            ["name"] = "Selected", ["type"] = returnOverload["ret"]!.DeepClone(),
            ["get"] = "ReturnSelect", ["getSig"] = new JsonArray(), ["getRet"] = returnOverload["ret"]!.DeepClone()
        });
    }
    foreach (var isVolatile in new[] { false, true })
        root["fields"]!.AsArray().Add(new JsonObject {
            ["name"] = isVolatile ? "VolatileMarked" : "Marked", ["vis"] = "public", ["volatile"] = isVolatile,
            ["type"] = overloads[0]!["params"]![0]!["type"]!.DeepClone()
        });
    var combined = ownerGenericMethods[0]!.DeepClone();
    combined["name"] = "Echo";
    combined["typeParams"] = new JsonArray("U");
    var methodVariable = new JsonObject { ["t"] = "tv", ["scope"] = "method", ["i"] = 0 };
    combined["params"]![0]!["type"]!["m"]!["args"]![0] = methodVariable.DeepClone();
    combined["params"]!.AsArray().Add(new JsonObject { ["name"] = "value", ["type"] = methodVariable.DeepClone() });
    combined["ret"] = methodVariable.DeepClone();
    combined["body"] = new JsonArray(new JsonObject { ["k"] = "return", ["value"] = new JsonObject { ["k"] = "local", ["name"] = "value" } });
    ownerGenericMethods.Add(combined);
    var combinedOwner = Named("GenericMethods");
    combinedOwner["args"] = new JsonArray(Named("System.String"));
    var combinedCall = Call("CallCombined", combinedOwner, combined, new JsonArray(Named("System.Int32")));
    combinedCall["ret"] = Named("System.Int32");
    var combinedExpression = combinedCall["body"]![0]!["value"]!;
    combinedExpression["ret"] = Named("System.Int32");
    combinedExpression["args"]!.AsArray().Add(new JsonObject { ["k"] = "const", ["type"] = Named("System.Int32"), ["value"] = 42 });
    root["methods"]!.AsArray().Add(combinedCall);
    root["types"]!.AsArray().Add(new JsonObject {
        ["name"] = "ModifiedConstructors", ["kind"] = "class", ["vis"] = "public",
        ["base"] = new JsonObject { ["t"] = "fqn", ["name"] = "System.Object" },
        ["ctors"] = constructorRows, ["fields"] = new JsonArray(), ["methods"] = new JsonArray(), ["interfaces"] = new JsonArray()
    });
    root["types"]!.AsArray().Add(new JsonObject {
        ["name"] = "GenericMethods", ["kind"] = "class", ["vis"] = "public", ["typeParams"] = new JsonArray("T"),
        ["base"] = Named("System.Object"), ["ctors"] = new JsonArray(), ["fields"] = new JsonArray(),
        ["methods"] = ownerGenericMethods, ["interfaces"] = new JsonArray()
    });
    var genericConstructors = new JsonArray();
    for (var i = 0; i < 2; i++)
    {
        var constructor = constructorRows[i]!.DeepClone();
        constructor["params"] = ownerGenericMethods[i]!["params"]!.DeepClone();
        var openOwner = Named("GenericConstructors");
        openOwner["args"] = new JsonArray(new JsonObject { ["t"] = "tv", ["scope"] = "type", ["i"] = 0 });
        constructor["body"] = new JsonArray(new JsonObject {
            ["k"] = "setField", ["ownerType"] = openOwner, ["recv"] = new JsonObject { ["k"] = "this" },
            ["name"] = "Tag", ["value"] = new JsonObject { ["k"] = "const", ["type"] = Named("System.Int32"), ["value"] = i + 1 }
        });
        genericConstructors.Add(constructor);
        var closedOwner = Named("GenericConstructors");
        closedOwner["args"] = new JsonArray(Named("System.Int32"));
        root["methods"]!.AsArray().Add(new JsonObject {
            ["name"] = "CallConstructor" + i, ["static"] = true, ["vis"] = "public", ["override"] = false, ["virtual"] = false,
            ["params"] = new JsonArray(), ["ret"] = Named("System.Object"),
            ["body"] = new JsonArray(new JsonObject { ["k"] = "return", ["value"] = new JsonObject {
                ["k"] = "new", ["type"] = closedOwner, ["localCtorIndex"] = i,
                ["argTypes"] = new JsonArray(constructor["params"]![0]!["type"]!["of"]!.DeepClone()),
                ["args"] = new JsonArray(new JsonObject { ["k"] = "const", ["type"] = Named("System.Object"), ["value"] = null })
            } })
        });
    }
    root["types"]!.AsArray().Add(new JsonObject {
        ["name"] = "GenericConstructors", ["kind"] = "class", ["vis"] = "public", ["typeParams"] = new JsonArray("T"),
        ["base"] = Named("System.Object"), ["ctors"] = genericConstructors, ["methods"] = new JsonArray(), ["interfaces"] = new JsonArray(),
        ["fields"] = new JsonArray(new JsonObject { ["name"] = "Tag", ["vis"] = "public", ["type"] = Named("System.Int32") })
    });
    var contractMethods = new JsonArray();
    var implementationMethods = new JsonArray();
    var contractEdge = Named("ModifierContract");
    contractEdge["args"] = new JsonArray(new JsonObject { ["t"] = "tv", ["scope"] = "type", ["i"] = 1 });
    foreach (var original in ownerGenericMethods.Where(m => m!["name"]!.GetValue<string>() == "GenericSelect"))
    {
        var contractMethod = original!.DeepClone();
        contractMethod["static"] = false;
        contractMethod["virtual"] = true;
        contractMethod["abstract"] = true;
        contractMethod["body"] = new JsonArray();
        contractMethods.Add(contractMethod);
        var implementation = original.DeepClone();
        implementation["static"] = false;
        implementation["virtual"] = true;
        implementation["name"] = "Body" + implementationMethods.Count;
        foreach (var parameter in implementation["params"]![0]!["type"]!["m"]!["args"]!.AsArray())
            parameter!["i"] = 1;
        implementation["clrInterfaceImpls"] = new JsonArray(new JsonObject {
            ["owner"] = contractEdge.DeepClone(), ["member"] = "GenericSelect", ["arity"] = 0, ["typeParams"] = new JsonArray(),
            ["params"] = new JsonArray(implementation["params"]![0]!["type"]!.DeepClone()),
            ["ret"] = Named("System.Int32")
        });
        implementationMethods.Add(implementation);
    }
    root["types"]!.AsArray().Add(new JsonObject {
        ["name"] = "ModifierContract", ["kind"] = "interface", ["vis"] = "public", ["typeParams"] = new JsonArray("T"),
        ["ctors"] = new JsonArray(), ["fields"] = new JsonArray(), ["methods"] = contractMethods, ["interfaces"] = new JsonArray()
    });
    root["types"]!.AsArray().Add(new JsonObject {
        ["name"] = "ModifiedImplementation", ["kind"] = "class", ["vis"] = "public", ["typeParams"] = new JsonArray("Ignored", "T"),
        ["base"] = Named("System.Object"), ["fields"] = new JsonArray(), ["methods"] = implementationMethods,
        ["interfaces"] = new JsonArray(contractEdge.DeepClone()),
        ["ctors"] = new JsonArray(new JsonObject {
            ["vis"] = "public", ["params"] = new JsonArray(), ["body"] = new JsonArray(),
            ["baseCtorRef"] = source["wellKnownRefs"]!["Object.ctor"]!.DeepClone()
        })
    });
    File.WriteAllText(args[2], root.ToJsonString());
    return;
}
var emitted = Assembly.LoadFrom(Path.GetFullPath(args[1])).GetType("ModifiedMethods")!;
if ((int)emitted.GetMethod("CallInt")!.Invoke(null, null)! != 1
    || (int)emitted.GetMethod("CallText")!.Invoke(null, null)! != 2)
    throw new Exception("CIR modifier-selected call bound to the wrong overload body");
for (var i = 0; i < 2; i++)
    foreach (var prefix in new[] { "CallGeneric", "CallOwner" })
        if ((int)emitted.GetMethod(prefix + i)!.Invoke(null, null)! != i + 1)
            throw new Exception("Constructed generic call lost its modifier-selected declaration");
if ((int)emitted.GetMethod("CallCombined")!.Invoke(null, null)! != 42)
    throw new Exception("Constructed owner and method generic argument frames were conflated");
for (var i = 0; i < 2; i++)
{
    var constructed = emitted.GetMethod("CallConstructor" + i)!.Invoke(null, null)!;
    if (constructed.GetType().GetGenericArguments().Single() != typeof(int)
        || (int)constructed.GetType().GetField("Tag")!.GetValue(constructed)! != i + 1)
        throw new Exception("Generic constructor call lost its exact modifier-selected body");
}
var modifiers = emitted.GetMethods().Where(m => m.Name == "Select")
    .Select(m => m.GetParameters()[0].GetOptionalCustomModifiers().Single()).ToHashSet();
if (!modifiers.SetEquals(new[] { typeof(Action<int>), typeof(Action<string>) }))
    throw new Exception("MethodDef custom modifiers were not preserved");
var genericMethods = emitted.GetMethods().Where(m => m.Name == "GenericSelect").ToArray();
if (genericMethods.Length != 2 || genericMethods.Sum(m => (int)m.MakeGenericMethod(typeof(int)).Invoke(null, [null])!) != 3)
    throw new Exception("Generic modifier MethodDefs did not retain their separate bodies");
foreach (var method in genericMethods)
{
    var marker = method.GetParameters()[0].GetOptionalCustomModifiers().Single();
    if (marker.GetGenericArguments().Any(t => t != method.GetGenericArguments()[0]))
        throw new Exception("Method modifier lost its declared generic-parameter frame");
}
var returnMethods = emitted.GetMethods().Where(m => m.Name == "ReturnSelect").ToArray();
if (returnMethods.Length != 2 || returnMethods.Sum(m => (int)m.Invoke(null, null)!) != 3
    || !returnMethods.Select(m => m.ReturnParameter.GetOptionalCustomModifiers().Single()).ToHashSet().SetEquals(modifiers))
    throw new Exception("Return-position custom modifiers were not preserved");
var constructors = emitted.Assembly.GetType("ModifiedConstructors")!.GetConstructors();
if (constructors.Length != 2
    || !constructors.Select(c => c.GetParameters()[0].GetOptionalCustomModifiers().Single()).ToHashSet().SetEquals(modifiers))
    throw new Exception("Constructor custom modifiers were not preserved");
foreach (var constructor in constructors) _ = constructor.Invoke([null]);
var properties = emitted.GetProperties();
if (properties.Length != 2 || properties.Sum(p => (int)p.GetValue(null)!) != 3
    || !properties.Select(p => p.GetOptionalCustomModifiers().Single()).ToHashSet().SetEquals(modifiers))
    throw new Exception("PropertyDef modifiers or accessor identities were not preserved");
foreach (var name in new[] { "Marked", "VolatileMarked" })
{
    var field = emitted.GetField(name)!;
    if (field.GetOptionalCustomModifiers().Single() != typeof(Action<int>))
        throw new Exception("Field custom modifier was dropped");
    var required = field.GetRequiredCustomModifiers();
    if (name == "VolatileMarked" ? required.Single() != typeof(System.Runtime.CompilerServices.IsVolatile) : required.Length != 0)
        throw new Exception("Volatile field marker changed while preserving optional modifiers");
}
var contract = emitted.Assembly.GetType("ModifierContract`1")!.MakeGenericType(typeof(int));
var implementationType = emitted.Assembly.GetType("ModifiedImplementation`2")!.MakeGenericType(typeof(string), typeof(int));
var instance = Activator.CreateInstance(implementationType);
if (contract.GetMethods().Sum(method => (int)method.Invoke(instance, [null])!) != 3)
    throw new Exception("Generic explicit interface MethodImpl lost modifiers or the owner argument frame");
Console.WriteLine("PASS: method/constructor/property/field modifiers, generic frames, exact calls and independent bodies");

static JsonObject Named(string name) => new() { ["t"] = "fqn", ["name"] = name };
static JsonObject Call(string name, JsonNode owner, JsonNode method, JsonArray? typeArguments = null)
{
    var call = new JsonObject {
        ["k"] = "callStatic", ["owner"] = owner["name"]!.DeepClone(), ["calleeOwner"] = owner.DeepClone(),
        ["method"] = method["name"]!.DeepClone(), ["calleeRet"] = method["ret"]!.DeepClone(), ["ret"] = method["ret"]!.DeepClone(),
        ["sig"] = new JsonArray(method["params"]!.AsArray().Select(p => p!["type"]!.DeepClone()).ToArray()),
        ["args"] = new JsonArray(new JsonObject { ["k"] = "const", ["type"] = Named("System.Object"), ["value"] = null })
    };
    if (typeArguments != null) call["typeArgs"] = typeArguments;
    return new JsonObject {
        ["name"] = name, ["static"] = true, ["vis"] = "public", ["override"] = false, ["virtual"] = false,
        ["params"] = new JsonArray(), ["ret"] = method["ret"]!.DeepClone(),
        ["body"] = new JsonArray(new JsonObject { ["k"] = "return", ["value"] = call })
    };
}
