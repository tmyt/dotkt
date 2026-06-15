// facadegen — reads .NET type metadata via reflection and emits @Clr-annotated Kotlin façades,
// so a kotlin/clr program can call those .NET types with no hand-written façade.
//
//   facadegen <outDir> <Type.Full.Name> [<Type.Full.Name> ...]
//
// Emits <outDir>/clr/_Clr.kt (the annotation) and one <outDir>/clr/<Name>.kt per type.
using System.Reflection;
using System.Text;

static class FacadeGen
{
    static int Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("usage: facadegen <outDir> <TypeFullName>...");
            return 1;
        }
        var clrDir = Path.Combine(args[0], "clr");
        Directory.CreateDirectory(clrDir);
        File.WriteAllText(Path.Combine(clrDir, "_Clr.kt"),
            "package clr\n\n" +
            "@Target(AnnotationTarget.CLASS, AnnotationTarget.FUNCTION, AnnotationTarget.PROPERTY)\n" +
            "annotation class Clr(val name: String)\n");

        foreach (var typeName in args.Skip(1))
        {
            var t = Type.GetType(typeName) ?? Type.GetType(typeName + ", System.Runtime");
            if (t == null) { Console.Error.WriteLine($"type not found: {typeName}"); continue; }
            File.WriteAllText(Path.Combine(clrDir, t.Name + ".kt"), GenerateType(t));
            Console.WriteLine($"generated clr/{t.Name}.kt  <-  {t.FullName}");
        }
        return 0;
    }

    static string GenerateType(Type t)
    {
        var sb = new StringBuilder();
        sb.Append("package clr\n\n");
        sb.Append($"@Clr(\"{t.FullName}\")\n");
        sb.Append($"class {t.Name} {{\n");
        var seen = new HashSet<string>();

        foreach (var c in t.GetConstructors(BindingFlags.Public | BindingFlags.Instance))
        {
            var ps = c.GetParameters();
            if (!ps.All(p => Supported(p.ParameterType))) continue;
            if (!seen.Add("ctor(" + Sig(ps, t) + ")")) continue;
            sb.Append($"\tconstructor({KParams(ps, t)})\n");
        }

        foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (p.GetIndexParameters().Length > 0 || !Supported(p.PropertyType)) continue;
            var nm = Decap(p.Name);
            if (!seen.Add("prop:" + nm)) continue;
            var kt = Map(p.PropertyType, t);
            if (p.CanWrite)
                sb.Append($"\t@Clr(\"{p.Name}\") var {nm}: {kt} get() = TODO(); set(value) {{}}\n");
            else
                sb.Append($"\t@Clr(\"{p.Name}\") val {nm}: {kt} get() = TODO()\n");
        }

        foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Instance))
        {
            if (m.IsSpecialName || m.IsGenericMethod) continue;
            // Skip Object identity members that would clash with Kotlin's Any (keep ToString).
            if (OBJECT_MEMBERS.Contains(m.Name)) continue;
            if (m.DeclaringType == typeof(object) && m.Name != "ToString") continue;
            var ps = m.GetParameters();
            if (!ps.All(p => Supported(p.ParameterType)) || !Supported(m.ReturnType)) continue;
            var nm = Decap(m.Name);
            if (!seen.Add(nm + "(" + Sig(ps, t) + ")")) continue;
            if (m.Name == "ToString" && ps.Length == 0)
                sb.Append("\t@Clr(\"ToString\") override fun toString(): String = TODO()\n");
            else
                sb.Append($"\t@Clr(\"{m.Name}\") fun {nm}({KParams(ps, t)}): {Map(m.ReturnType, t)} = TODO()\n");
        }

        var statics = t.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => !m.IsSpecialName && !m.IsGenericMethod).ToList();
        if (statics.Count > 0)
        {
            sb.Append($"\n\t@Clr(\"{t.FullName}\")\n\tcompanion object {{\n");
            var seenS = new HashSet<string>();
            foreach (var m in statics)
            {
                var ps = m.GetParameters();
                if (!ps.All(p => Supported(p.ParameterType)) || !Supported(m.ReturnType)) continue;
                var nm = Decap(m.Name);
                if (!seenS.Add(nm + "(" + Sig(ps, t) + ")")) continue;
                sb.Append($"\t\t@Clr(\"{m.Name}\") fun {nm}({KParams(ps, t)}): {Map(m.ReturnType, t)} = TODO()\n");
            }
            sb.Append("\t}\n");
        }

        sb.Append("}\n");
        return sb.ToString();
    }

    static bool Supported(Type t) =>
        !t.IsByRef && !t.IsPointer && !t.IsGenericParameter && !t.ContainsGenericParameters && !t.IsArray;

    // Map a .NET type to a Kotlin façade type. Primitives map precisely; the type itself maps to its
    // own façade (enables chaining); everything else degrades to Any? (call still passes through).
    static string Map(Type t, Type self)
    {
        if (t == typeof(void)) return "Unit";
        if (t == self) return self.Name;
        return t.FullName switch
        {
            "System.Int32" => "Int",
            "System.Int64" => "Long",
            "System.Int16" => "Short",
            "System.Byte" => "Byte",
            "System.Boolean" => "Boolean",
            "System.Double" => "Double",
            "System.Single" => "Float",
            "System.Char" => "Char",
            "System.String" => "String",
            _ => "Any?",
        };
    }

    static string Sig(ParameterInfo[] ps, Type self) => string.Join(",", ps.Select(p => Map(p.ParameterType, self)));

    static string KParams(ParameterInfo[] ps, Type self) =>
        string.Join(", ", ps.Select((p, i) => $"{ParamName(p, i)}: {Map(p.ParameterType, self)}"));

    static string ParamName(ParameterInfo p, int i)
    {
        var n = p.Name;
        if (string.IsNullOrEmpty(n) || !IsIdent(n)) n = "arg" + i;
        return KEYWORDS.Contains(n) ? "`" + n + "`" : n;
    }

    static string Decap(string s) => string.IsNullOrEmpty(s) ? s : char.ToLowerInvariant(s[0]) + s.Substring(1);
    static bool IsIdent(string s) => s.Length > 0 && (char.IsLetter(s[0]) || s[0] == '_') && s.All(c => char.IsLetterOrDigit(c) || c == '_');

    static readonly HashSet<string> OBJECT_MEMBERS = new()
    { "Equals", "GetHashCode", "GetType", "ReferenceEquals", "Finalize", "MemberwiseClone", "Clone" };

    static readonly HashSet<string> KEYWORDS = new()
    { "value", "object", "fun", "val", "var", "this", "is", "in", "when", "if", "else",
      "class", "interface", "return", "null", "true", "false", "typealias", "for", "while", "do", "as" };
}
