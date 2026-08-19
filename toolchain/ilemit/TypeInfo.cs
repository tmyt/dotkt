// AUTO-SPLIT from Program.cs — part of the `Emitter` partial class (see Program.cs for the overview).
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text.Json;

// ECMA-335 I.8.6.1.6: a method's generic arity is part of its identity independently of its parameter vector.
// Keep that CLR fact explicit in ilemit's in-memory index so `f(object)` and `f<T>(object)` remain distinct.
readonly record struct MethodSigKey(string Name, int GenericArity, string Parameters)
{
    public override string ToString() =>
        GenericArity == 0
            ? Name + "(" + Parameters + ")"
            : Name + "``" + GenericArity + "(" + Parameters + ")";
}

sealed class TypeInfo
{
    public TypeBuilder TB;
    public JsonElement Def;
    public bool IsFileClass;
    public JsonElement? FileElem; // for file classes: the whole file (for hasMain)
    public string BaseName;                 // the base's bare FQN name (for _types lookup / chain-walk)
    public DotKt.Bir.TypeNode.Fqn BaseFqn;  // the base as a structured Fqn (with args), for constructing a generic base
    public Type ClrBase;   // set when the base is a REFERENCED .NET type; resolved by reflection, not in _types
    public readonly Dictionary<string, FieldBuilder> Fields = new();
    public readonly Dictionary<string, MethodBuilder> Methods = new();
    // Overloaded methods share a name, so `Methods` (name-keyed) collides — the last-declared wins, and the others'
    // bodies/calls get misrouted. `MethodsBySig` keys by the complete CLR method identity available before return-type
    // emission: name + METHOD generic arity + parameter vector. Each MethodDef has an exact key that retains generic
    // parameter scope/index, plus a first-wins erased alias used only by current call-side linking until #395 carries
    // frontend-selected declaration identity. Definition bodies and MethodImpl links always use the exact key.
    public readonly Dictionary<MethodSigKey, MethodBuilder> MethodsBySig = new();
    // How many members share each NAME. `Methods` cannot say (it is last-wins), and the difference decides whether a
    // name-only lookup is safe: with one member there is no overload to mis-select, with several the descriptor is
    // the only thing that picks the right one.
    public readonly Dictionary<string, int> MethodNameCounts = new();
    public ConstructorBuilder Ctor;       // primary ctor (Ctors[0]) — convenience for the common single-ctor path
    public JsonElement CtorDef;
    public readonly List<ConstructorBuilder> Ctors = new();   // all ctors (primary + secondary)
    public readonly List<JsonElement> CtorDefs = new();
    public bool CtorsDefined;              // guards EnsureCtorsDefined (may run early from BuildAttribute, then again in pass 3)
    public bool IsInterface;
    public bool IsDelegate;
    public bool IsEnum;
    public Type Created;                   // baked enum Type (created early so its tokens are valid in other IL)
    // All physical generic parameters: source-declared plus any explicit nested captures chosen by bir2cir.
    public readonly Dictionary<string, GenericTypeParameterBuilder> TypeParams = new();
    public bool IsGeneric => TypeParams.Count > 0;
    public Type AsType => Created ?? TB;
}
