using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using DotKt.Bir;

// Bind source-native member identities to their CLR representation before the
// DOM frontier. The owning adapter and scalar selection policy live in partials;
// these reflection helpers also serve actual late member/override consumers.
// Every call/read reshape retains its source result stamps. Dynamic-dispatch
// instructions do not travel onto a bound CLR slot; final member resolution
// authors the exact physical reference that ilemit emits one-to-one.
static partial class NetInteropBinding
{
    // A projected static on a generic CLR TypeDef has no enclosing type argument in Kotlin syntax, but its CIL
    // MemberRef parent must still be a TypeSpec. Close the selected exact reflected owner, rather than using a
    // name-only arity table (Task and Task<T>, for example, deliberately share the same source FQN).
    internal static JsonNode CloseStaticOwner(JsonNode ownerJson, Type netType)
        => TypeJson.Write(CloseStaticOwner(TypeJson.Read(ownerJson), netType));

    internal static TypeNode CloseStaticOwner(TypeNode ownerType, Type netType)
    {
        if (ownerType is not TypeNode.Fqn { Args: null } owner ||
            netType == null || !netType.IsGenericTypeDefinition)
            return DotKt.Bir.Lowering.BirGraphValue.CopyType(ownerType);
        var arity = netType.GetGenericArguments().Length;
        if (arity == 0) return DotKt.Bir.Lowering.BirGraphValue.CopyType(ownerType);
        return new TypeNode.Fqn(owner.Name,
            Enumerable.Repeat<TypeNode>(new TypeNode.Fqn("kotlin.Any"), arity).ToArray());
    }

    // Annotation-only nullability over an open variable does not participate
    // in CLR declaration matching; concrete Nullable<V> remains a real type.
    static TypeNode NormSigTv(TypeNode t) => t switch
    {
        TypeNode.Oblivious o => NormSigTv(o.Of),                          // annotation-only wrapper: always unwrap
        TypeNode.Nullable n => NormSigTv(n.Of) is TypeNode.Tv tv ? tv     // `T?` reflects as bare `T`
                               : new TypeNode.Nullable(NormSigTv(n.Of)),  // `Int?` stays Nullable<Int32>
        TypeNode.Fqn { Args: { } fa } f => new TypeNode.Fqn(f.Name, fa.Select(NormSigTv).ToArray()),
        TypeNode.Array a => new TypeNode.Array(NormSigTv(a.Elem)),
        TypeNode.ByRef b => new TypeNode.ByRef(NormSigTv(b.Of)),
        TypeNode.Fn fn => new TypeNode.Fn(fn.Suspend, NormSigTv(fn.Ret), fn.Params.Select(NormSigTv).ToArray(),
                                          fn.Recv == null ? null : NormSigTv(fn.Recv)),
        _ => t,
    };

    // Peel Nullable/Oblivious/ByRef wrappers off an owner type slot to reach the underlying .NET Fqn (name + type-args),
    // so a `List<Item>?`/`T!`/byref receiver resolves its open .NET definition.
    static TypeNode.Fqn UnwrapFqn(TypeNode t)
    {
        while (true)
            switch (t)
            {
                case TypeNode.Fqn f: return f;
                case TypeNode.Nullable nu: t = nu.Of; break;
                case TypeNode.Oblivious ob: t = ob.Of; break;
                case TypeNode.ByRef br: t = br.Of; break;
                default: return null;
            }
    }

    // The INVERSE of dll2klib's OPERATOR_NAMES (dll2klib Program.cs): a Kotlin `operator fun` name -> the .NET `op_X`
    // static-method slot. kotc emits the Kotlin identity; this pass reconstructs the .NET operator off the refs.
    static readonly Dictionary<string, string> OperatorToNet = new(StringComparer.Ordinal)
    {
        ["plus"] = "op_Addition",
        ["minus"] = "op_Subtraction",
        ["times"] = "op_Multiply",
        ["div"] = "op_Division",
        ["rem"] = "op_Modulus",
        ["unaryMinus"] = "op_UnaryNegation",
        ["unaryPlus"] = "op_UnaryPlus",
        ["inc"] = "op_Increment",
        ["dec"] = "op_Decrement",
    };

    // True iff the resolved owner is a .NET `enum` type (#107) — its members (name/ordinal) bind to the CLR enum
    // semantics rather than the plain property-accessor path. IsEnum is available on a MetadataLoadContext type.
    static bool IsNetEnum(Type type) { try { return type.IsEnum; } catch { return false; } }

    // True iff the .NET type declares `name` as a public static method (a `op_X` operator is a public static special
    // method on the declaring type). Guards against rewriting a Kotlin `plus` on a .NET type that has no such operator.
    static bool DeclaresPublicStaticMethod(Type type, string name)
    {
        try { return type.GetMethods(BindingFlags.Public | BindingFlags.Static).Any(m => m.Name == name); }
        catch { return false; }
    }

    // The .NET DEFAULT INDEXED PROPERTY's `get_`/`set_` accessor slot name (A2 step 4). kotc's old hardcode was always
    // `get_Item`/`set_Item`; reflecting the type's `DefaultMemberAttribute` (which `[IndexerName("X")]` sets) honors a
    // custom-named indexer (e.g. `get_Chars`). Walks the type + bases + interfaces; prefers the indexed property whose
    // name matches the DefaultMember, else any indexed property. Returns the accessor MethodInfo.Name, or null if none.
    internal static string DefaultIndexerAccessor(Type type, bool isSet)
    {
        const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
            | BindingFlags.Static | BindingFlags.DeclaredOnly;
        var seen = new HashSet<Type>();
        var stack = new Stack<Type>();
        stack.Push(type);
        while (stack.Count > 0)
        {
            var cur = stack.Pop();
            if (cur == null || !seen.Add(cur)) continue;
            string defaultMember = null;
            try
            {
                var dm = cur.GetCustomAttributesData()
                    .FirstOrDefault(a => a.AttributeType.FullName == "System.Reflection.DefaultMemberAttribute");
                if (dm != null && dm.ConstructorArguments.Count > 0) defaultMember = dm.ConstructorArguments[0].Value as string;
            }
            catch { }
            try
            {
                PropertyInfo chosen = null;
                foreach (var p in cur.GetProperties(Flags))
                {
                    if (p.GetIndexParameters().Length == 0) continue;   // not an indexer
                    // Overloaded indexers need not expose both accessors on every overload.
                    if ((isSet ? p.SetMethod : p.GetMethod) == null) continue;
                    if (defaultMember != null && p.Name == defaultMember) { chosen = p; break; }
                    chosen ??= p;
                }
                if (chosen != null)
                {
                    var acc = isSet ? chosen.SetMethod : chosen.GetMethod;
                    if (acc != null) return acc.Name;
                }
            }
            catch { /* metadata-load edge on a malformed member table — keep walking */ }
            Type baseType = null; try { baseType = cur.BaseType; } catch { }
            if (baseType != null) stack.Push(baseType);
            try { foreach (var i in cur.GetInterfaces()) stack.Push(i); } catch { }
        }
        return null;
    }

    // True iff the .NET type (or a base/interface) declares a NON-indexed property OR a field of this name — the two
    // members kotc's clrPropGet/clrPropSet covers (a property accessor, or a static/instance field read as ldsfld/ldfld).
    // An INDEXER (an indexed property, e.g. "Item") is excluded (it stays a plain get_Item/set_Item method call).
    internal static bool MemberIsPropertyOrField(Type type, string name)
    {
        const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
            | BindingFlags.Static | BindingFlags.DeclaredOnly;
        var seen = new HashSet<Type>();
        var stack = new Stack<Type>();
        stack.Push(type);
        while (stack.Count > 0)
        {
            var cur = stack.Pop();
            if (cur == null || !seen.Add(cur)) continue;
            try
            {
                foreach (var p in cur.GetProperties(Flags))
                    if (p.Name == name && p.GetIndexParameters().Length == 0) return true;
                foreach (var fi in cur.GetFields(Flags))
                    if (fi.Name == name) return true;
            }
            catch { /* metadata-load edge on a malformed member table — treat as no match */ }
            Type baseType = null; try { baseType = cur.BaseType; } catch { }
            if (baseType != null) stack.Push(baseType);
            try { foreach (var i in cur.GetInterfaces()) stack.Push(i); } catch { }
        }
        return false;
    }

    // The static/instance bit belongs to the nearest visible class declaration, not every same-named member in
    // its hierarchy. A hidden base property must not turn a static field into an instance access.
    static bool MemberIsStaticPropertyOrField(Type type, string name)
    {
        const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
            | BindingFlags.Static | BindingFlags.DeclaredOnly;
        List<bool> Declared(Type current)
        {
            var matches = new List<bool>();
            try
            {
                foreach (var p in current.GetProperties(Flags))
                {
                    if (p.Name != name || p.GetIndexParameters().Length != 0) continue;
                    var accessor = p.GetMethod ?? p.SetMethod;
                    if (accessor != null && (accessor.IsPublic || accessor.IsFamily || accessor.IsFamilyOrAssembly))
                        matches.Add(accessor.IsStatic);
                }
                foreach (var field in current.GetFields(Flags))
                    if (field.Name == name && (field.IsPublic || field.IsFamily || field.IsFamilyOrAssembly))
                        matches.Add(field.IsStatic);
            }
            catch { /* metadata-load edge — ambiguity/failure remains false */ }
            return matches;
        }
        for (var current = type; current != null; current = current.BaseType)
        {
            var matches = Declared(current);
            if (matches.Count != 0) return matches.Count == 1 && matches[0];
        }
        var inherited = new List<bool>();
        try { foreach (var iface in type.GetInterfaces()) inherited.AddRange(Declared(iface)); } catch { }
        return inherited.Count == 1 && inherited[0];
    }

    // True iff the .NET type (or a base/interface) declares a method of this name (any arity), public OR protected —
    // a Kotlin class can override a PROTECTED VIRTUAL .NET member (the WinUI OnLaunched pattern: `override fun Tag()`
    // over a protected `Base.Tag`). Used by DeclarationRename's dll2klib-override slot resolution (A2 step 5) to
    // confirm a Kotlin override binds a REAL .NET method before it keeps the identity slot — dll2klib injects the
    // Kotlin method identity EQUAL to the .NET name. NonPublic covers the protected/family case.
    internal static bool DeclaresPublicMethodNamed(Type type, string name)
    {
        const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        var seen = new HashSet<Type>();
        var stack = new Stack<Type>();
        stack.Push(type);
        while (stack.Count > 0)
        {
            var cur = stack.Pop();
            if (cur == null || !seen.Add(cur)) continue;
            try { if (cur.GetMethods(Flags).Any(m => m.Name == name)) return true; }
            catch { /* metadata-load edge — keep walking */ }
            Type baseType = null; try { baseType = cur.BaseType; } catch { }
            if (baseType != null) stack.Push(baseType);
            try { foreach (var i in cur.GetInterfaces()) stack.Push(i); } catch { }
        }
        return false;
    }

    // True iff this resolved call is the owner's `Comparable<Self>.compareTo(Self)` slot. Both halves are load-bearing:
    // the reflected owner must implement IComparable with ITSELF (not merely some unrelated T), and the frontend's
    // resolved one-parameter signature must name that same owner. A sibling `compareTo(Int)` therefore remains
    // lowercase even though the class also has the PascalCase CLR self slot (#182).
    static bool IsComparableSelfCall(Type type, TypeNode.Fqn owner, IReadOnlyList<TypeNode> sig)
    {
        if (sig is not { Count: 1 } || sig[0] is not TypeNode argType
            || NormSigTv(argType) is not TypeNode.Fqn arg
            || arg != owner)
            return false;
        try
        {
            return type.GetInterfaces().Any(i =>
            {
                if (!i.IsGenericType || i.GetGenericTypeDefinition().FullName != "System.IComparable`1") return false;
                var target = i.GetGenericArguments()[0];
                if (target == type) return true;
                if (!type.IsGenericTypeDefinition || !target.IsGenericType
                    || target.GetGenericTypeDefinition() != type) return false;
                return target.GetGenericArguments().SequenceEqual(type.GetGenericArguments());
            });
        }
        catch { return false; }
    }

}
