using System;
using System.Reflection;
using System.Reflection.Emit;

namespace DotKt.Tests.Suppression;

public class NativeCustomDescription : Exception
{
    public override string ToString() => "native-custom-description";
}

public class NativeHiddenDescription : Exception
{
    public NativeHiddenDescription() : base("native-hidden-message") { }
    public new virtual string ToString() => "hidden-slot-not-object-tostring";
    public string ToString<T>() => "generic-overload-not-object-tostring";
}

public class NativeHiddenDescriptionChild : NativeHiddenDescription
{
    public override string ToString() => "hidden-child-not-object-tostring";
}

public static class NativeDescriptionFactory
{
    public static Exception RenamedOverride()
    {
        var type = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("NativeDescriptionSlot"), AssemblyBuilderAccess.RunAndCollect)
            .DefineDynamicModule("Main").DefineType("RenamedExceptionDescription", TypeAttributes.Public, typeof(Exception));
        type.DefineDefaultConstructor(MethodAttributes.Public);
        var method = type.DefineMethod("DifferentName", MethodAttributes.Public | MethodAttributes.Virtual,
            typeof(string), Type.EmptyTypes);
        var body = method.GetILGenerator();
        body.Emit(OpCodes.Ldstr, "renamed-native-slot-description");
        body.Emit(OpCodes.Ret);
        type.DefineMethodOverride(method, typeof(object).GetMethod(nameof(ToString), Type.EmptyTypes)!);
        return (Exception)Activator.CreateInstance(type.CreateType()!)!;
    }
}
