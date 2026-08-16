// SHARED across bir2cir / ilemit via a <Compile Link/> (like TypeNode.cs — no build-order dependency).
// The #370 scalar member reference: ONE fully resolved external-member identity, authored by bir2cir and
// consumed by ilemit.
//
// NORMATIVE: docs/bir-cir.schema.json $defs/memberRef.
//
// WHY A SINGLE SCALAR. bir2cir resolves a Kotlin operation to exactly one declaration in the target
// compile-reference universe. Splitting that answer across a parameter vector, an owner name and a set of
// adjacent flags leaves ilemit holding pieces it must re-combine — and re-combining candidates IS member
// selection, which belongs upstream. Everything an ECMA MemberRef/MethodSpec needs is here, so ilemit can
// look the member up EXACTLY (declaring type, then the one DeclaredOnly member whose signature matches) and
// encode it. If a reader of this record ever needs applicability, assignability, most-derived rules or a
// name/arity fallback, the producer wrote an incomplete reference and that is the bug.
//
// The signature is the OPEN declared one (ECMA II.9.8: a MemberRef carries the UNINSTANTIATED signature;
// instantiation rides on the MethodSpec / TypeSpec built around it). Generic parameters are therefore
// positional `tv` nodes, exactly as in the reflected declaration.

#nullable enable
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DotKt.Bir;

/// <summary>
/// A complete, already-resolved reference to a member of another assembly.
/// </summary>
/// <param name="Kind">What the member IS — see <see cref="Kinds"/>. Selects the lookup family
/// (method / constructor / field) and records the accessor role for diagnostics.</param>
/// <param name="Assembly">Simple name of the assembly that must own the emitted reference. This is the
/// PHYSICAL identity, not "the file bir2cir happened to read": deciding the physical CLR representation is
/// bir2cir's job, so where a reference twin and its runtime twin differ in name, this already names the
/// runtime one.</param>
/// <param name="DeclaringType">The exact declaring type: an <c>fqn</c> whose name is the OPEN definition's
/// metadata FullName VERBATIM — arity backtick and <c>+</c> nesting included, because that is the physical
/// name the target universe is keyed by, and re-deriving either downstream would be guessing —
/// and whose <c>args</c>, when present, are the use-site instantiation of that declaring type.
/// The declarer is stated, never derived: a member inherited by the receiver is anchored on the type that
/// DECLARES it, so no base walk or most-derived rule is needed downstream.</param>
/// <param name="Name">The exact metadata name: <c>.ctor</c> for a constructor, <c>get_X</c>/<c>set_X</c>/
/// <c>add_X</c>/<c>remove_X</c> for an accessor, otherwise the member name verbatim.</param>
/// <param name="GenericArity">The member's own generic-parameter count (0 for everything but a generic
/// method). Stated rather than inferred from a call site's type-argument count.</param>
/// <param name="ReturnType">The OPEN declared return type — the void <c>fqn</c> for a void method and for a
/// constructor, and the declared FIELD type when <see cref="Kind"/> is <c>field</c>. Part of member identity:
/// two members can differ only by return type (an inherited slot shadowed by a covariant redeclaration).</param>
/// <param name="CallingConvention">HASTHIS, and whether the signature is vararg: <c>static</c>,
/// <c>instance</c>, <c>varargStatic</c> or <c>varargInstance</c>. Absent for a field.</param>
/// <param name="ParameterTypes">The OPEN declared parameter vector. Absent for a field.</param>
public sealed record MemberRefNode(
    string Kind,
    string Assembly,
    TypeNode DeclaringType,
    string Name,
    int GenericArity,
    TypeNode ReturnType,
    string? CallingConvention = null,
    TypeNode[]? ParameterTypes = null)
{
    /// <summary>The frozen <c>kind</c> vocabulary.</summary>
    public static class Kinds
    {
        public const string Method = "method";
        public const string Ctor = "ctor";
        public const string Field = "field";
        public const string PropertyAccessor = "propertyAccessor";
        public const string EventAccessor = "eventAccessor";

        public static bool IsKnown(string k) =>
            k is Method or Ctor or Field or PropertyAccessor or EventAccessor;

        /// <summary>A field is the one kind with neither a calling convention nor a parameter vector.</summary>
        public static bool IsField(string k) => k == Field;
    }

    public const string Static = "static";
    public const string Instance = "instance";
    // A vararg signature is a different member from its fixed-arity neighbour, so the convention records it
    // rather than being refused: the frontend accepted the program, and a producer that aborted here would be
    // rejecting source over a fact it could simply state.
    public const string VarargStatic = "varargStatic";
    public const string VarargInstance = "varargInstance";

    static bool IsConvention(string? cc) => cc is Static or Instance or VarargStatic or VarargInstance;
    static bool IsInstanceConvention(string? cc) => cc is Instance or VarargInstance;

    /// <summary>
    /// The generic arity a metadata FullName encodes, summed over the nesting chain: `Outer`1+Inner`1` has two
    /// parameters, of which the outer one comes first. Reading it back is how the declaring type's argument
    /// list is checked against the name it is attached to.
    /// </summary>
    public static int ArityOfName(string fullName)
    {
        int total = 0;
        for (int i = fullName.IndexOf('`'); i >= 0; i = fullName.IndexOf('`', i + 1))
        {
            int j = i + 1, n = 0;
            while (j < fullName.Length && char.IsAsciiDigit(fullName[j])) { n = n * 10 + (fullName[j] - '0'); j++; }
            total += n;
        }
        return total;
    }

    /// <summary>The canonical void spelling shared with the rest of the document vocabulary.</summary>
    public static readonly TypeNode Void = new TypeNode.Fqn("void");

    public const string CtorName = ".ctor";

    public bool Equals(MemberRefNode? o) =>
        o is not null && Kind == o.Kind && Assembly == o.Assembly && DeclaringType == o.DeclaringType
        && Name == o.Name && GenericArity == o.GenericArity && ReturnType == o.ReturnType
        && CallingConvention == o.CallingConvention && SeqEq(ParameterTypes, o.ParameterTypes);

    public override int GetHashCode() =>
        System.HashCode.Combine(Kind, Assembly, DeclaringType, Name, GenericArity, ReturnType,
            ParameterTypes?.Length ?? -1);

    static bool SeqEq(TypeNode[]? a, TypeNode[]? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a is null || b is null || a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
            if (a[i] != b[i]) return false;
        return true;
    }

    /// <summary>
    /// The invariants that make this a COMPLETE identity. Checked on both read and write, because a
    /// half-filled reference is exactly the failure this type exists to prevent, and it is far cheaper to
    /// name at the producer than to debug as a lookup that found nothing.
    /// </summary>
    public void Validate()
    {
        if (!Kinds.IsKnown(Kind)) throw new FormatException($"memberRef.kind=`{Kind}` is not a known member kind");
        if (string.IsNullOrEmpty(Assembly)) throw new FormatException("memberRef.assembly must be a non-empty simple assembly name");
        if (DeclaringType is not TypeNode.Fqn) throw new FormatException("memberRef.declaringType must be an fqn type node");
        if (string.IsNullOrEmpty(Name)) throw new FormatException("memberRef.name must be a non-empty metadata member name");
        // A non-generic declarer OMITS its args. An empty list would be a second spelling of the same shape,
        // and this record's structural equality treats "absent" and "empty" as different — so one member
        // would acquire two identities that never compare equal.
        if (DeclaringType is TypeNode.Fqn { Args: { Length: 0 } })
            throw new FormatException("memberRef.declaringType must omit `args` when the declarer is non-generic, not carry an empty list");
        // The declarer's name states its own arity, so an argument list of any other length describes an
        // instantiation that type cannot have. Checking it is what catches a projection that silently ran
        // short or long — an identity that still looks coherent but names nothing.
        if (DeclaringType is TypeNode.Fqn declarer)
        {
            int want = ArityOfName(declarer.Name), got = declarer.Args?.Length ?? 0;
            if (want != got)
                throw new FormatException(
                    $"memberRef.declaringType `{declarer.Name}` declares {want} generic parameter(s) but carries {got} argument(s)");
        }
        if (Kind != Kinds.Ctor && Name == CtorName)
            throw new FormatException($"memberRef.name `{CtorName}` names a constructor, but kind is `{Kind}`");
        if (GenericArity < 0) throw new FormatException($"memberRef.genericArity must be >= 0, got {GenericArity}");
        if (GenericArity > 0 && Kind != Kinds.Method)
            throw new FormatException($"memberRef.genericArity must be 0 for kind `{Kind}` (only a method has its own generic parameters)");
        if (Kinds.IsField(Kind))
        {
            if (CallingConvention is not null) throw new FormatException("memberRef.callingConvention must be absent for a field");
            if (ParameterTypes is not null) throw new FormatException("memberRef.parameterTypes must be absent for a field");
        }
        else
        {
            if (!IsConvention(CallingConvention))
                throw new FormatException(
                    $"memberRef.callingConvention=`{CallingConvention}` must be `static`, `instance`, `varargStatic` or `varargInstance`");
            if (ParameterTypes is null) throw new FormatException($"memberRef.parameterTypes is required for kind `{Kind}`");
        }
        if (Kind == Kinds.Ctor)
        {
            if (Name != CtorName) throw new FormatException($"memberRef.name for a ctor must be `{CtorName}`, got `{Name}`");
            if (!IsInstanceConvention(CallingConvention)) throw new FormatException("a ctor memberRef must be an instance convention");
            if (ReturnType != Void) throw new FormatException("a ctor memberRef must return void");
        }
    }

    // --- Read / Write (the canonical serialization; required fields first, optional last) ---------------

    public static MemberRefNode Read(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object)
            throw new FormatException($"memberRef must be a JSON object, got {e.ValueKind}");
        var node = new MemberRefNode(
            Kind: Str(e, "kind"),
            Assembly: Str(e, "assembly"),
            DeclaringType: TypeNode.Read(Required(e, "declaringType")),
            Name: Str(e, "name"),
            GenericArity: Int(e, "genericArity"),
            ReturnType: TypeNode.Read(Required(e, "returnType")),
            // A key present with a JSON null is NOT the same as an absent key: it states the field and states
            // nothing, which is how a producer half-writes a reference. Absence is the only way to omit one.
            CallingConvention: Optional(e, "callingConvention")?.GetString(),
            ParameterTypes: Optional(e, "parameterTypes") is { } ps ? ReadTypes(ps) : null);
        node.Validate();
        return node;
    }

    /// <summary>Read the memberRef stored under <paramref name="key"/>, or null when the key is absent.</summary>
    public static MemberRefNode? ReadOptional(JsonElement owner, string key) =>
        owner.ValueKind == JsonValueKind.Object && owner.TryGetProperty(key, out var e) ? Read(e) : null;

    // Every read failure is a FormatException naming the field: a reference is read at a layer boundary, and
    // a KeyNotFoundException from a raw GetProperty tells the operator nothing about which document is wrong.
    static JsonElement Required(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null
            ? v
            : throw new FormatException($"memberRef missing required field `{name}`");

    static JsonElement? Optional(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null ? v : null;

    static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()!
            : throw new FormatException($"memberRef missing required string field `{name}`");

    static int Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i)
            ? i
            : throw new FormatException($"memberRef missing required integer field `{name}`");

    static TypeNode[] ReadTypes(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Array)
            throw new FormatException($"memberRef.parameterTypes must be an array of Types, got {e.ValueKind}");
        var list = new List<TypeNode>(e.GetArrayLength());
        foreach (var item in e.EnumerateArray()) list.Add(TypeNode.Read(item));
        return list.ToArray();
    }

    public JsonObject Write()
    {
        Validate();
        var o = new JsonObject
        {
            ["kind"] = Kind,
            ["assembly"] = Assembly,
            ["declaringType"] = TypeNode.Write(DeclaringType),
            ["name"] = Name,
            ["genericArity"] = GenericArity,
            ["returnType"] = TypeNode.Write(ReturnType),
        };
        if (CallingConvention is not null) o["callingConvention"] = CallingConvention;
        if (ParameterTypes is not null)
        {
            var arr = new JsonArray();
            foreach (var p in ParameterTypes) arr.Add(TypeNode.Write(p));
            o["parameterTypes"] = arr;
        }
        return o;
    }

    public string ToJson() => Write().ToJsonString(new JsonSerializerOptions { WriteIndented = false });

    public static MemberRefNode Parse(string json)
    {
        using var doc = JsonDocument.Parse(json, BirJson.DocOptions);
        return Read(doc.RootElement);
    }

    /// <summary>
    /// The COMPLETE reference in one human line — what a failed lookup must print. A diagnostic that names
    /// only the member name is what makes a target mismatch expensive to diagnose, so this deliberately
    /// spells the assembly, the declaring instantiation, every parameter (modifiers included) and the return.
    /// </summary>
    public string Describe()
    {
        var sb = new StringBuilder();
        sb.Append(Assembly).Append('!').Append(TypeNode.ToJson(DeclaringType));
        sb.Append("::").Append(Name);
        if (GenericArity > 0) sb.Append("<`").Append(GenericArity).Append('>');
        if (ParameterTypes is not null)
        {
            sb.Append('(');
            for (int i = 0; i < ParameterTypes.Length; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(TypeNode.ToJson(ParameterTypes[i]));
            }
            sb.Append(')');
        }
        sb.Append(" : ").Append(TypeNode.ToJson(ReturnType));
        sb.Append(" [").Append(Kind);
        if (CallingConvention is not null) sb.Append(", ").Append(CallingConvention);
        sb.Append(']');
        return sb.ToString();
    }
}

/// <summary>
/// Round-trip self-test of the shared MemberRefNode contract: Read(Write(node)) == node for every kind, the
/// canonical byte form, and each completeness invariant refusing its own malformed shape. Called by the same
/// throwaway harness as <see cref="TypeNodeSelfTest"/>.
/// </summary>
public static class MemberRefNodeSelfTest
{
    public static void Run()
    {
        var cases = new (MemberRefNode node, string json)[]
        {
            // A static method on a non-generic external type.
            (new MemberRefNode(MemberRefNode.Kinds.Method, "System.Runtime",
                    new TypeNode.Fqn("System.Math"), "Abs", 0,
                    new TypeNode.Fqn("System.Int32"), MemberRefNode.Static,
                    new TypeNode[] { new TypeNode.Fqn("System.Int32") }),
                "{\"kind\":\"method\",\"assembly\":\"System.Runtime\",\"declaringType\":{\"t\":\"fqn\",\"name\":\"System.Math\"},\"name\":\"Abs\",\"genericArity\":0,\"returnType\":{\"t\":\"fqn\",\"name\":\"System.Int32\"},\"callingConvention\":\"static\",\"parameterTypes\":[{\"t\":\"fqn\",\"name\":\"System.Int32\"}]}"),
            // An instance method DECLARED on a constructed generic owner: the parameter is the owner's own
            // positional type variable, and the instantiation lives on declaringType.args.
            (new MemberRefNode(MemberRefNode.Kinds.Method, "System.Collections",
                    new TypeNode.Fqn("System.Collections.Generic.List`1",
                        new TypeNode[] { new TypeNode.Fqn("System.String") }),
                    "Add", 0, MemberRefNode.Void, MemberRefNode.Instance,
                    new TypeNode[] { new TypeNode.Tv("type", 0) }),
                "{\"kind\":\"method\",\"assembly\":\"System.Collections\",\"declaringType\":{\"t\":\"fqn\",\"name\":\"System.Collections.Generic.List\\u00601\",\"args\":[{\"t\":\"fqn\",\"name\":\"System.String\"}]},\"name\":\"Add\",\"genericArity\":0,\"returnType\":{\"t\":\"fqn\",\"name\":\"void\"},\"callingConvention\":\"instance\",\"parameterTypes\":[{\"t\":\"tv\",\"scope\":\"type\",\"i\":0}]}"),
            // A generic METHOD: its own parameter space is `method`-scoped and its arity is stated.
            (new MemberRefNode(MemberRefNode.Kinds.Method, "System.Runtime",
                    new TypeNode.Fqn("System.Array"), "Empty", 1,
                    new TypeNode.Array(new TypeNode.Tv("method", 0)), MemberRefNode.Static,
                    System.Array.Empty<TypeNode>()),
                "{\"kind\":\"method\",\"assembly\":\"System.Runtime\",\"declaringType\":{\"t\":\"fqn\",\"name\":\"System.Array\"},\"name\":\"Empty\",\"genericArity\":1,\"returnType\":{\"t\":\"array\",\"elem\":{\"t\":\"tv\",\"scope\":\"method\",\"i\":0}},\"callingConvention\":\"static\",\"parameterTypes\":[]}"),
            // A constructor.
            (new MemberRefNode(MemberRefNode.Kinds.Ctor, "System.Runtime",
                    new TypeNode.Fqn("System.Object"), MemberRefNode.CtorName, 0,
                    MemberRefNode.Void, MemberRefNode.Instance, System.Array.Empty<TypeNode>()),
                "{\"kind\":\"ctor\",\"assembly\":\"System.Runtime\",\"declaringType\":{\"t\":\"fqn\",\"name\":\"System.Object\"},\"name\":\".ctor\",\"genericArity\":0,\"returnType\":{\"t\":\"fqn\",\"name\":\"void\"},\"callingConvention\":\"instance\",\"parameterTypes\":[]}"),
            // A field: no calling convention, no parameters, and the declared FIELD type as the return.
            (new MemberRefNode(MemberRefNode.Kinds.Field, "System.Runtime",
                    new TypeNode.Fqn("System.Int32"), "MaxValue", 0,
                    new TypeNode.Fqn("System.Int32")),
                "{\"kind\":\"field\",\"assembly\":\"System.Runtime\",\"declaringType\":{\"t\":\"fqn\",\"name\":\"System.Int32\"},\"name\":\"MaxValue\",\"genericArity\":0,\"returnType\":{\"t\":\"fqn\",\"name\":\"System.Int32\"}}"),
            // A property accessor carrying a modreq'd by-ref return — the shape that has no other spelling.
            (new MemberRefNode(MemberRefNode.Kinds.PropertyAccessor, "System.Runtime",
                    new TypeNode.Fqn("System.ReadOnlySpan`1", new TypeNode[] { new TypeNode.Fqn("System.Byte") }),
                    "get_Item", 0,
                    new TypeNode.Mod(true, new TypeNode.Fqn("System.Runtime.InteropServices.InAttribute"),
                        new TypeNode.ByRef(new TypeNode.Tv("type", 0))),
                    MemberRefNode.Instance,
                    new TypeNode[] { new TypeNode.Fqn("System.Int32") }),
                "{\"kind\":\"propertyAccessor\",\"assembly\":\"System.Runtime\",\"declaringType\":{\"t\":\"fqn\",\"name\":\"System.ReadOnlySpan\\u00601\",\"args\":[{\"t\":\"fqn\",\"name\":\"System.Byte\"}]},\"name\":\"get_Item\",\"genericArity\":0,\"returnType\":{\"t\":\"mod\",\"req\":true,\"m\":{\"t\":\"fqn\",\"name\":\"System.Runtime.InteropServices.InAttribute\"},\"of\":{\"t\":\"byRef\",\"of\":{\"t\":\"tv\",\"scope\":\"type\",\"i\":0}}},\"callingConvention\":\"instance\",\"parameterTypes\":[{\"t\":\"fqn\",\"name\":\"System.Int32\"}]}"),
            // An event accessor.
            (new MemberRefNode(MemberRefNode.Kinds.EventAccessor, "System.Runtime",
                    new TypeNode.Fqn("System.AppDomain"), "add_UnhandledException", 0,
                    MemberRefNode.Void, MemberRefNode.Instance,
                    new TypeNode[] { new TypeNode.Fqn("System.UnhandledExceptionEventHandler") }),
                "{\"kind\":\"eventAccessor\",\"assembly\":\"System.Runtime\",\"declaringType\":{\"t\":\"fqn\",\"name\":\"System.AppDomain\"},\"name\":\"add_UnhandledException\",\"genericArity\":0,\"returnType\":{\"t\":\"fqn\",\"name\":\"void\"},\"callingConvention\":\"instance\",\"parameterTypes\":[{\"t\":\"fqn\",\"name\":\"System.UnhandledExceptionEventHandler\"}]}"),
        };

        foreach (var (node, json) in cases)
        {
            string got = node.ToJson();
            if (got != json)
                throw new Exception($"[C# MemberRefNode] Write mismatch:\n  expected {json}\n  got      {got}");
            if (MemberRefNode.Parse(json) != node)
                throw new Exception($"[C# MemberRefNode] Read mismatch for {json}");
            if (MemberRefNode.Parse(node.ToJson()) != node)
                throw new Exception($"[C# MemberRefNode] round-trip mismatch for {json}");
            if (node.Describe().Length == 0)
                throw new Exception("[C# MemberRefNode] Describe produced nothing");
        }

        // Two members that differ ONLY in a way a flat descriptor would lose must stay distinguishable.
        var listAdd = cases[1].node;
        if (listAdd == listAdd with { DeclaringType = new TypeNode.Fqn("System.Collections.Generic.List`1",
                new TypeNode[] { new TypeNode.Fqn("System.Int32") }) })
            throw new Exception("[C# MemberRefNode] owner instantiation must participate in identity");
        if (listAdd == listAdd with { ReturnType = new TypeNode.Fqn("System.Int32") })
            throw new Exception("[C# MemberRefNode] return type must participate in identity");
        if (listAdd == listAdd with { Assembly = "System.Runtime" })
            throw new Exception("[C# MemberRefNode] defining assembly must participate in identity");

        // Every completeness invariant refuses its own malformed shape (an incomplete reference must never
        // reach a reader, because a reader that tolerates one is a reader that guesses).
        Refuse("unknown kind", () => new MemberRefNode("accessor", "A", new TypeNode.Fqn("T"), "m", 0,
            MemberRefNode.Void, MemberRefNode.Instance, System.Array.Empty<TypeNode>()).Validate());
        Refuse("empty assembly", () => new MemberRefNode(MemberRefNode.Kinds.Method, "", new TypeNode.Fqn("T"), "m", 0,
            MemberRefNode.Void, MemberRefNode.Instance, System.Array.Empty<TypeNode>()).Validate());
        Refuse("non-fqn declaringType", () => new MemberRefNode(MemberRefNode.Kinds.Method, "A",
            new TypeNode.Tv("type", 0), "m", 0, MemberRefNode.Void, MemberRefNode.Instance,
            System.Array.Empty<TypeNode>()).Validate());
        Refuse("method without parameterTypes", () => new MemberRefNode(MemberRefNode.Kinds.Method, "A",
            new TypeNode.Fqn("T"), "m", 0, MemberRefNode.Void, MemberRefNode.Instance).Validate());
        Refuse("method without callingConvention", () => new MemberRefNode(MemberRefNode.Kinds.Method, "A",
            new TypeNode.Fqn("T"), "m", 0, MemberRefNode.Void, null, System.Array.Empty<TypeNode>()).Validate());
        Refuse("field with a parameter vector", () => new MemberRefNode(MemberRefNode.Kinds.Field, "A",
            new TypeNode.Fqn("T"), "f", 0, MemberRefNode.Void, null, System.Array.Empty<TypeNode>()).Validate());
        Refuse("generic arity on a field", () => new MemberRefNode(MemberRefNode.Kinds.Field, "A",
            new TypeNode.Fqn("T"), "f", 1, MemberRefNode.Void).Validate());
        Refuse("misnamed ctor", () => new MemberRefNode(MemberRefNode.Kinds.Ctor, "A", new TypeNode.Fqn("T"),
            "New", 0, MemberRefNode.Void, MemberRefNode.Instance, System.Array.Empty<TypeNode>()).Validate());
        Refuse("static ctor reference", () => new MemberRefNode(MemberRefNode.Kinds.Ctor, "A", new TypeNode.Fqn("T"),
            MemberRefNode.CtorName, 0, MemberRefNode.Void, MemberRefNode.Static,
            System.Array.Empty<TypeNode>()).Validate());
        Refuse("ctor returning a value", () => new MemberRefNode(MemberRefNode.Kinds.Ctor, "A", new TypeNode.Fqn("T"),
            MemberRefNode.CtorName, 0, new TypeNode.Fqn("T"), MemberRefNode.Instance,
            System.Array.Empty<TypeNode>()).Validate());
        Refuse("`.ctor` under a non-ctor kind", () => new MemberRefNode(MemberRefNode.Kinds.Method, "A",
            new TypeNode.Fqn("T"), MemberRefNode.CtorName, 0, MemberRefNode.Void, MemberRefNode.Instance,
            System.Array.Empty<TypeNode>()).Validate());
        Refuse("an empty declaring-args list", () => new MemberRefNode(MemberRefNode.Kinds.Method, "A",
            new TypeNode.Fqn("T", System.Array.Empty<TypeNode>()), "m", 0, MemberRefNode.Void,
            MemberRefNode.Instance, System.Array.Empty<TypeNode>()).Validate());
        // A declarer's name states its own arity, nesting chain included, so any other argument count names
        // an instantiation that type cannot have.
        Refuse("too few declaring args", () => new MemberRefNode(MemberRefNode.Kinds.Method, "A",
            new TypeNode.Fqn("Outer`1+Inner`1", new TypeNode[] { new TypeNode.Fqn("System.Int32") }), "m", 0,
            MemberRefNode.Void, MemberRefNode.Instance, System.Array.Empty<TypeNode>()).Validate());
        Refuse("args on a non-generic declarer", () => new MemberRefNode(MemberRefNode.Kinds.Method, "A",
            new TypeNode.Fqn("T", new TypeNode[] { new TypeNode.Fqn("System.Int32") }), "m", 0,
            MemberRefNode.Void, MemberRefNode.Instance, System.Array.Empty<TypeNode>()).Validate());
        if (MemberRefNode.ArityOfName("Outer`1+Inner`2") != 3 || MemberRefNode.ArityOfName("Plain") != 0)
            throw new Exception("[C# MemberRefNode] nesting-chain arity must be summed over the whole name");
        // A vararg member is a different member, not an unrepresentable one.
        new MemberRefNode(MemberRefNode.Kinds.Method, "A", new TypeNode.Fqn("T"), "m", 0, MemberRefNode.Void,
            MemberRefNode.VarargStatic, System.Array.Empty<TypeNode>()).Validate();

        // A key present with a JSON null states the field and states nothing — the shape a half-written
        // reference takes. Absence is the only way to omit an optional field.
        Refuse("an explicitly null required field", () => MemberRefNode.Parse(
            "{\"kind\":\"method\",\"assembly\":\"A\",\"declaringType\":null,\"name\":\"m\"," +
            "\"genericArity\":0,\"returnType\":{\"t\":\"fqn\",\"name\":\"void\"}," +
            "\"callingConvention\":\"static\",\"parameterTypes\":[]}"));
        Refuse("an explicitly null calling convention", () => MemberRefNode.Parse(
            "{\"kind\":\"method\",\"assembly\":\"A\",\"declaringType\":{\"t\":\"fqn\",\"name\":\"T\"}," +
            "\"name\":\"m\",\"genericArity\":0,\"returnType\":{\"t\":\"fqn\",\"name\":\"void\"}," +
            "\"callingConvention\":null,\"parameterTypes\":[]}"));

        Console.WriteLine($"[C# MemberRefNode] self-test OK ({cases.Length} fixture cases + identity + 17 refusals)");
    }

    static void Refuse(string what, Action act)
    {
        try { act(); }
        catch (FormatException) { return; }
        throw new Exception($"[C# MemberRefNode] expected a FormatException for {what}");
    }
}
