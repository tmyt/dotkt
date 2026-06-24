// AUTO-SPLIT from Program.cs — part of the `Emitter` partial class (see Program.cs for the overview).
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text.Json;

sealed class TypeInfo
{
    public TypeBuilder TB;
    public JsonElement Def;
    public bool IsFileClass;
    public JsonElement? FileElem; // for file classes: the whole file (for hasMain)
    public string BaseName;
    public Type ClrBase;   // set when the base is a .NET type (`clr:`/`clrg:`); resolved by reflection, not in _types
    public readonly Dictionary<string, FieldBuilder> Fields = new();
    public readonly Dictionary<string, MethodBuilder> Methods = new();
    // Overloaded methods share a name, so `Methods` (name-keyed) collides — the last-declared wins, and the others'
    // bodies/calls get misrouted. `MethodsBySig` keys by name + parameter-type signature so each overload is distinct
    // (e.g. `text(string)` vs `text(func:string:)`). Both body emission and call resolution prefer it.
    public readonly Dictionary<string, MethodBuilder> MethodsBySig = new();
    public ConstructorBuilder Ctor;       // primary ctor (Ctors[0]) — convenience for the common single-ctor path
    public JsonElement CtorDef;
    public readonly List<ConstructorBuilder> Ctors = new();   // all ctors (primary + secondary)
    public readonly List<JsonElement> CtorDefs = new();
    public bool IsInterface;
    public bool IsEnum;
    public EnumBuilder EB;                 // set for enums (EnumBuilder is not a TypeBuilder)
    public Type Created;                   // baked enum Type (created early so its tokens are valid in other IL)
    // Generic type parameters (`class Box<T>`): name -> the GenericTypeParameterBuilder defined in pass 1.
    public readonly Dictionary<string, GenericTypeParameterBuilder> TypeParams = new();
    public bool IsGeneric => TypeParams.Count > 0;
    public Type AsType => Created ?? (EB != null ? (Type)EB : TB);
}
