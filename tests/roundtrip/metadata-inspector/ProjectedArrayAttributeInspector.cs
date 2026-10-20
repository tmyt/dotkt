using System.Reflection;
using System.Runtime.Loader;

static class ProjectedArrayAttributeInspector
{
    public static void Verify(string producerPath)
    {
        producerPath = Path.GetFullPath(producerPath);
        var context = new AssemblyLoadContext("projected-array-attributes", isCollectible: true);
        context.Resolving += (_, name) => {
            var dependency = Path.Combine(Path.GetDirectoryName(producerPath)!, name.Name + ".dll");
            return File.Exists(dependency) ? context.LoadFromAssemblyPath(dependency) : null;
        };
        try
        {
            var assembly = context.LoadFromAssemblyPath(producerPath);
            var marker = assembly.GetType("roundtrip.arraysource.ArraySourceNames", throwOnError: true)!;
            if (marker.GetConstructors().Single().GetParameters().Single().ParameterType != typeof(string[]))
                throw new Exception("Attribute constructor lost its exact native array");
            var fields = marker.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (fields.Length != 1 || fields[0].FieldType != typeof(string[]))
                throw new Exception("Attribute field lost its exact native array");
            if (!fields[0].IsPrivate)
                throw new Exception("Annotation property leaked its backing storage");
            var property = marker.GetProperty("names", BindingFlags.Public | BindingFlags.Instance);
            if (property == null || property.GetMethod is not { IsPublic: true } getter ||
                property.PropertyType != typeof(string[]) || getter.ReturnType != property.PropertyType ||
                getter.GetParameters().Length != 0)
                throw new Exception("Annotation property lost its exact public getter signature");
            var owner = assembly.GetType("roundtrip.arraysource.ArraySourceSignaturesKt", throwOnError: true)!;
            var method = owner.GetMethod("relayStarArray")!;
            if (method.GetParameters().Single().ParameterType != typeof(Array) || method.ReturnType != typeof(Array))
                throw new Exception("Attribute preservation leaked into ordinary projected array slots");
            var attribute = method.GetCustomAttributesData().Single(data => data.AttributeType == marker);
            var values = (IEnumerable<CustomAttributeTypedArgument>)attribute.ConstructorArguments.Single().Value!;
            if (!values.Select(value => (string)value.Value!).SequenceEqual(new[] { "star", "array" }))
                throw new Exception("Attribute array payload was not encoded");
        }
        finally { context.Unload(); }
    }
}
