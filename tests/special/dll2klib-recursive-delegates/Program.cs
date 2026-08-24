using System.Reflection;
using System.Reflection.Emit;

if (args.Length != 1)
    throw new ArgumentException("usage: Generator <output-directory>");

var output = Path.GetFullPath(args[0]);
Directory.CreateDirectory(output);
WriteLocalCycle(output);
WriteGenericCycle(output);
WriteCrossAssemblyCycle(output);

static void WriteLocalCycle(string output)
{
    var assembly = NewAssembly("Recursive.Local", out var module);
    var a = NewDelegate(module, "Recursive.Local.A");
    var b = NewDelegate(module, "Recursive.Local.B");
    CompleteDelegate(a, typeof(void), b);
    CompleteDelegate(b, typeof(void), a);
    AddApi(module, "Recursive.Local.Api", ("UseA", a), ("UseB", b));
    a.CreateType();
    b.CreateType();
    assembly.Save(Path.Combine(output, "Recursive.Local.dll"));
}

static void WriteGenericCycle(string output)
{
    var assembly = NewAssembly("Recursive.Generic", out var module);
    var self = NewDelegate(module, "Recursive.Generic.Self`1");
    var typeParameter = self.DefineGenericParameters("T")[0];
    var constructedSelf = self.MakeGenericType(typeParameter);
    CompleteDelegate(self, typeof(void), constructedSelf);
    AddApi(module, "Recursive.Generic.Api", ("Use", self.MakeGenericType(typeof(int))));
    self.CreateType();
    assembly.Save(Path.Combine(output, "Recursive.Generic.dll"));
}

static void WriteCrossAssemblyCycle(string output)
{
    var assemblyA = NewAssembly("Recursive.CrossA", out var moduleA);
    var assemblyB = NewAssembly("Recursive.CrossB", out var moduleB);
    var a = NewDelegate(moduleA, "Recursive.Cross.A");
    var b = NewDelegate(moduleB, "Recursive.Cross.B");
    CompleteDelegate(a, typeof(void), b);
    CompleteDelegate(b, typeof(void), a);
    AddApi(moduleA, "Recursive.Cross.ApiA", ("Use", a));
    AddApi(moduleB, "Recursive.Cross.ApiB", ("Use", b));
    a.CreateType();
    b.CreateType();
    assemblyA.Save(Path.Combine(output, "Recursive.CrossA.dll"));
    assemblyB.Save(Path.Combine(output, "Recursive.CrossB.dll"));
}

static PersistedAssemblyBuilder NewAssembly(string name, out ModuleBuilder module)
{
    var assembly = new PersistedAssemblyBuilder(new AssemblyName(name), typeof(object).Assembly);
    module = assembly.DefineDynamicModule(name);
    return assembly;
}

static TypeBuilder NewDelegate(ModuleBuilder module, string name) => module.DefineType(
    name,
    TypeAttributes.Public | TypeAttributes.Class | TypeAttributes.Sealed,
    typeof(MulticastDelegate));

static void CompleteDelegate(TypeBuilder type, Type returnType, params Type[] parameters)
{
    var constructor = type.DefineConstructor(
        MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.RTSpecialName,
        CallingConventions.Standard,
        [typeof(object), typeof(IntPtr)]);
    constructor.SetImplementationFlags(MethodImplAttributes.Runtime | MethodImplAttributes.Managed);
    var invoke = type.DefineMethod(
        "Invoke",
        MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.NewSlot |
        MethodAttributes.Virtual,
        CallingConventions.Standard,
        returnType,
        parameters);
    invoke.SetImplementationFlags(MethodImplAttributes.Runtime | MethodImplAttributes.Managed);
}

static void AddApi(ModuleBuilder module, string name, params (string Name, Type Parameter)[] methods)
{
    var api = module.DefineType(
        name,
        TypeAttributes.Public | TypeAttributes.Class | TypeAttributes.Abstract | TypeAttributes.Sealed);
    foreach (var method in methods)
    {
        var definition = api.DefineMethod(
            method.Name,
            MethodAttributes.Public | MethodAttributes.Static,
            CallingConventions.Standard,
            typeof(void),
            [method.Parameter]);
        definition.GetILGenerator().Emit(OpCodes.Ret);
    }
    api.CreateType();
}
