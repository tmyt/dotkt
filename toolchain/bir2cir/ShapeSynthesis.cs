using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

// #55 §4 (kotc-purity): DERIVE the `clrGenericStatic`/`clrGenericInstance` overload-matcher `shapes` array here, in
// the Kotlin<->CLR layer, instead of in the kotc frontend. kotc used to carry a `clrMethodShape(IrType)` that emitted
// the ilemit `Shape(Type)` tokens directly — including the .NET SIMPLE NAMES (`Int64`/`SByte`/`Single`/…), which is
// CLR knowledge that must NOT live in the frontend (the keystone leak for the Kotlin 2.4 bump). kotc now emits the
// DECLARED parameter types as PURE-KOTLIN `birType` identities in a transient `shapeTypes` array; this pass converts
// each to the ilemit shape token and writes the frozen `shapes` string array (the SIG-KEY reflection island, §2.2.1),
// then removes `shapeTypes`. The .NET simple names come from the ref.dll `@ClrTypeAlias` index (kotlin.Long ->
// System.Int64 -> "Int64"), with a hardcoded primitive fallback for the (alias-less) ref build — mirroring the
// KotlinAllToClr decision (bir2cir keeps the primitive CLR names because the ref build has no ref.dll).
//
// The token vocabulary MUST match ilemit's `Shape(Type)` (Program.cs) exactly, since ilemit compares these strings
// against `Shape(reflectedParamType)`: gp / array / string / char / int / ienum / func:N / generic / <.NET simple name>.
//
// Runs in the Phase-1 per-file loop right after MemberCallSubstitution (so `shapes` exists before SuspendColdLowering
// reads it, and before the final CIR emit) and BEFORE type lowering (the `shapeTypes` nodes are still pure kotlin.*
// identities; the special int/string/char tokens depend on that). A no-op when a node carries no `shapeTypes`
// (the ValueTypeNullableCollectionArg-synthesized `Cast` node already writes `shapes` directly; the ref build emits
// no `clrGeneric*` nodes at all).
static class ShapeSynthesis
{
    // Kotlin primitive FQN -> its .NET simple-name shape token (== ilemit `Shape(Type).Name`). Used ONLY as the
    // fallback when the ref.dll alias index has no entry (the reference build, which has no ref.dll loaded). This is
    // CLR knowledge legitimately in bir2cir. #53/#54 signedness: kotlin.Byte is SIGNED (System.SByte); kotlin.UByte is
    // the UNSIGNED System.Byte.
    static readonly Dictionary<string, string> PrimShapeName = new(StringComparer.Ordinal)
    {
        ["kotlin.Long"] = "Int64", ["kotlin.Short"] = "Int16", ["kotlin.Byte"] = "SByte",
        ["kotlin.Float"] = "Single", ["kotlin.Double"] = "Double", ["kotlin.Boolean"] = "Boolean",
        ["kotlin.Unit"] = "Void",
        ["kotlin.UByte"] = "Byte", ["kotlin.UShort"] = "UInt16", ["kotlin.UInt"] = "UInt32", ["kotlin.ULong"] = "UInt64",
    };

    public static void Apply(JsonNode root, ReferenceMetadataIndex refs, bool refBuild) =>
        Walk(root, refs, refBuild);

    static void Walk(JsonNode node, ReferenceMetadataIndex refs, bool refBuild)
    {
        switch (node)
        {
            case JsonObject obj:
                MaybeSynth(obj, refs, refBuild);
                foreach (var kv in obj) Walk(kv.Value, refs, refBuild);
                break;
            case JsonArray arr:
                foreach (var it in arr) Walk(it, refs, refBuild);
                break;
        }
    }

    static void MaybeSynth(JsonObject node, ReferenceMetadataIndex refs, bool refBuild)
    {
        var k = (node["k"] as JsonValue)?.TryGetValue<string>(out var kv) == true ? kv : null;
        if (k != "clrGenericStatic" && k != "clrGenericInstance") return;
        if (node["shapeTypes"] is not JsonArray shapeTypes) return;   // already lowered / synthesized elsewhere
        var shapes = new JsonArray();
        foreach (var st in shapeTypes)
        {
            var t = TypeJson.Read(st) ?? throw new FormatException($"shapeTypes entry is not a Type node: {st?.ToJsonString()}");
            shapes.Add(Shape(t, refs, refBuild));
        }
        node["shapes"] = shapes;
        node.Remove("shapeTypes");
    }

    // Mirror of ilemit's `Shape(Type)` over a structured Kotlin `TypeNode` (pre-lowering). Nullability is IGNORED
    // (kotc's clrMethodShape read `classFqName` through the `?`), so unwrap Nullable/Oblivious first.
    static string Shape(TypeNode t, ReferenceMetadataIndex refs, bool refBuild)
    {
        t = t switch { TypeNode.Nullable n => n.Of, TypeNode.Oblivious o => o.Of, _ => t };
        switch (t)
        {
            case TypeNode.Tv:
                return "gp";
            case TypeNode.Array:
                return "array";
            case TypeNode.ByRef:
                // A `kotlin.clr.ClrRef<T>` param — clrMethodShape saw the raw generic IrType (1 type arg) as "generic".
                return "generic";
            case TypeNode.Fn fn:
                {
                    var baseN = fn.Params.Length + (fn.Recv != null ? 1 : 0);
                    var retUnit = fn.Ret is TypeNode.Fqn { Name: "kotlin.Unit" };
                    return "func:" + (retUnit ? baseN : baseN + 1);
                }
            case TypeNode.Fqn f:
                switch (f.Name)
                {
                    case "kotlin.String": return "string";
                    case "kotlin.Char": return "char";
                    case "kotlin.Int": return "int";
                }
                // kotlin.collections.Iterable @ClrTypeAlias-es to System.Collections.Generic.IEnumerable, whose ilemit
                // Shape is the special "ienum" (never in the ref build, which keeps the pure-Kotlin surface).
                if (!refBuild && (f.Name == "kotlin.collections.Iterable" || f.Name == "kotlin.collections.MutableIterable"))
                    return "ienum";
                // Any other parameterized generic .NET type (Task<T>, Continuation<T>, …) -> ilemit's IsGenericType default.
                if (f.Args is { Length: > 0 })
                    return "generic";
                return ShapeName(f.Name, refs, refBuild);
            default:
                return "Object";
        }
    }

    // The .NET SIMPLE NAME of a leaf type, matching ilemit `Shape(Type).Name`. PRIMARY source = the ref.dll
    // @ClrTypeAlias index (kotlin.Long -> System.Int64 -> "Int64"; kotlin.Any -> System.Object -> "Object"); the
    // primitive fallback covers the ref build (no ref.dll). A leaf that is NEITHER an aliased stdlib type NOR a
    // primitive but IS a facadegen-injected .NET interop type (issue #44: `JsonSerializerOptions` as a SIBLING param
    // of a generic method like `JsonSerializer.Serialize<T>(T, JsonSerializerOptions?)`) resolves off the refs to its
    // reflection Type — its `.Name` is EXACTLY what ilemit's `Shape(Type)` returns for that reference param
    // (`p.Name`), so the shape string matches and ResolveGenericMethod finds the overload. The refBuild carries no
    // facadegen interop (and no ref.dll to resolve against), so it stays on the alias/primitive path only. Anything
    // still unresolved erases to "Object" (ilemit's fallback shape for a reference param).
    static string ShapeName(string fqn, ReferenceMetadataIndex refs, bool refBuild)
    {
        if (refs.Aliases.TryGetValue(fqn, out var bcl)) return LastSegment(bcl);
        if (PrimShapeName.TryGetValue(fqn, out var prim)) return prim;
        if (!refBuild)
        {
            var netType = refs.ResolveNetType(ReferenceMetadataIndex.BareOwnerFqn(fqn));
            if (netType != null) return netType.Name;
        }
        return "Object";
    }

    static string LastSegment(string s)
    {
        var i = s.LastIndexOf('.');
        return i < 0 ? s : s.Substring(i + 1);
    }
}
