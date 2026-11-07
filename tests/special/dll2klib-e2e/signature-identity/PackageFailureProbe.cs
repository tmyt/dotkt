using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;

internal static class PackageFailureProbe
{
    public static void Run(Assembly tool)
    {
        var root = Path.Combine(Path.GetTempPath(), "dotkt-package-failures-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "CurrentPayloadFailures.dll");
            Build(tool, path);
            var response = Path.Combine(root, "references.rsp");
            File.WriteAllLines(response, [path]);
            var main = tool.GetType("Program", true)!.GetMethod("Main", BindingFlags.Static | BindingFlags.Public)!;
            string Error(int jobs)
            {
                var output = Path.Combine(root, "out" + jobs);
                var previous = Console.Error;
                using var error = new StringWriter();
                Console.SetError(error);
                try
                {
                    var result = (Task<int>)main.Invoke(null,
                        [new[] { "--out", output, "--jobs", jobs.ToString(), "@" + response }])!;
                    if (result.GetAwaiter().GetResult() == 0) throw new Exception("invalid visibility was accepted");
                    if (Directory.GetFiles(output, "*.klib").Length != 0)
                        throw new Exception("failed package scan published a KLIB");
                    return error.ToString();
                }
                finally { Console.SetError(previous); }
            }
            var serial = Error(1);
            var parallel = Error(4);
            if (serial != parallel || !serial.Contains("invalid Kotlin callable visibility 'badA'"))
                throw new Exception("serial/parallel package failure selection differs: " + serial + parallel);
            Console.WriteLine("Package failures retain sorted selection and do not publish outputs: PASS");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static void Build(Assembly tool, string path)
    {
        var assembly = new PersistedAssemblyBuilder(new AssemblyName("CurrentPayloadFailures"), typeof(object).Assembly);
        assembly.SetCustomAttribute(new CustomAttributeBuilder(
            typeof(AssemblyMetadataAttribute).GetConstructor([typeof(string), typeof(string)])!,
            ["DotKt.Compiler", "metadata-v1"]));
        var module = assembly.DefineDynamicModule("CurrentPayloadFailures");
        var carrier = module.DefineType("DotKt.Runtime.CompilerServices.KotlinCallableVisibilityAttribute",
            TypeAttributes.NotPublic | TypeAttributes.Sealed, typeof(Attribute));
        carrier.SetCustomAttribute(new CustomAttributeBuilder(
            typeof(CompilerGeneratedAttribute).GetConstructor(Type.EmptyTypes)!, []));
        var constructor = carrier.DefineConstructor(MethodAttributes.Public, CallingConventions.Standard,
            [typeof(string), typeof(byte[])]);
        var body = constructor.GetILGenerator();
        body.Emit(OpCodes.Ldarg_0);
        body.Emit(OpCodes.Call, typeof(Attribute).GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic,
            null, Type.EmptyTypes, null)!);
        body.Emit(OpCodes.Ret);
        carrier.CreateType();
        var codec = tool.GetType("DotKt.Bir.BirCarrier", true)!;
        var version = (string)codec.GetField("JsonV1")!.GetRawConstantValue()!;
        foreach (var name in new[] { "A", "B" })
        {
            var type = module.DefineType(name + ".Bad", TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);
            var method = type.DefineMethod("Run", MethodAttributes.Public | MethodAttributes.Static, typeof(void), Type.EmptyTypes);
            var bytes = (byte[])codec.GetMethod("EncodeBody")!.Invoke(null,
                [version, new JsonObject { ["visibility"] = "bad" + name }])!;
            method.SetCustomAttribute(new CustomAttributeBuilder(constructor, [version, bytes]));
            method.GetILGenerator().Emit(OpCodes.Ret);
            type.CreateType();
        }
        assembly.Save(path);
    }
}
