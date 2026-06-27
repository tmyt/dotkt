// AUTO-SPLIT from Program.cs — part of the `Emitter` partial class (see Program.cs for the overview).
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text.Json;

// The `[Kotlin*]` round-trip metadata attributes (from DotKt.Runtime) and the BIR `attr`-node constant decoding.
sealed partial class Emitter
{
    // The embedded `DotKt.Runtime.CompilerServices.*` attribute types — defined into THIS module by EnsureKotlinAttrs
    // (Emitter.CompilerServices.cs). Always available once defined (no external reference needed to stamp).
    bool _kAttrsResolved;
    Type _kFuncAttr, _kFileAttr, _kInlineAttr, _kReadOnlyAttr, _nullableAttr, _nullableCtxAttr;

    // [KotlinReadOnly] — a public backing field whose Kotlin property isn't publicly settable (restore as `val`).
    void ApplyKotlinReadOnly(FieldBuilder fb)
    {
        EnsureKotlinAttrs();
        fb.SetCustomAttribute(new CustomAttributeBuilder(_kReadOnlyAttr.GetConstructor(Type.EmptyTypes), new object[0]));
    }

    // [KotlinInline(body)] — the inline+lambda fn's BIR body, for cross-module splicing.
    void ApplyKotlinInline(MethodBuilder mb, string body)
    {
        EnsureKotlinAttrs();
        mb.SetCustomAttribute(new CustomAttributeBuilder(_kInlineAttr.GetConstructor(new[] { typeof(string) }), new object[] { body }));
    }

    // [KotlinFunction(flags)] — Kotlin modifiers with no .NET analog (infix/operator/suspend), for Kotlin re-consumption.
    void ApplyKotlinFunction(MethodBuilder mb, int flags)
    {
        EnsureKotlinAttrs();
        mb.SetCustomAttribute(new CustomAttributeBuilder(_kFuncAttr.GetConstructor(new[] { typeof(int) }), new object[] { flags }));
    }

    // [KotlinFileClass] — marks a `<File>Kt` facade so its statics restore as top-level Kotlin functions.
    void ApplyKotlinFileClass(TypeBuilder tb)
    {
        EnsureKotlinAttrs();
        tb.SetCustomAttribute(new CustomAttributeBuilder(_kFileAttr.GetConstructor(Type.EmptyTypes), new object[0]));
    }

    CustomAttributeBuilder BuildCab(JsonElement a)
    {
        var attr = a.GetProperty("attr").GetString();
        var args = a.GetProperty("args").EnumerateArray().Select(ConstArgValue).ToArray();
        if (attr.StartsWith("clr:"))
        {
            // An imported .NET attribute (#54): bind its real constructor (resolved by the declared arg types,
            // falling back to arity) and apply it with the constant args.
            var at = ClrRef(attr);
            var argTypes = a.GetProperty("argTypes").EnumerateArray().Select(s => ClrRef(s.GetString())).ToArray();
            var nctor = at.GetConstructor(argTypes)
                        ?? at.GetConstructors().FirstOrDefault(c => c.GetParameters().Length == args.Length);
            return new CustomAttributeBuilder(nctor, args);
        }
        var ti = _types[attr];
        var ctor = ti.Ctors.Count > 0 ? ti.Ctors[0] : ti.TB.DefineDefaultConstructor(MethodAttributes.Public);
        return new CustomAttributeBuilder(ctor, args);
    }

    static object ConstArgValue(JsonElement e)
    {
        // Annotation arguments are always compile-time constants (const nodes).
        if (!e.TryGetProperty("value", out var v)) return null;
        switch (v.ValueKind)
        {
            case JsonValueKind.String: return v.GetString();
            case JsonValueKind.True: return true;
            case JsonValueKind.False: return false;
            case JsonValueKind.Number:
                return e.GetProperty("type").GetString() switch
                {
                    "long" => (object)v.GetInt64(),
                    "double" => v.GetDouble(),
                    "float" => (float)v.GetDouble(),
                    "short" => (short)v.GetInt32(),
                    "byte" => (sbyte)v.GetInt32(),
                    _ => v.GetInt32(),
                };
            default: return null;
        }
    }
}
