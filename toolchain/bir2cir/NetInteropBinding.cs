using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using DotKt.Bir;

// .NET-INTEROP CALL BINDING (A2 / #61): the Kotlin<->CLR binding for a facadegen-injected .NET member call. kotc
// emits a PLAIN `callStatic`/`callInstance` by the .NET owner's FQN IDENTITY (`callStatic Kfc.App.get_Count`,
// `callInstance System.Text.StringBuilder.Append`) carrying only frontend FACTS — static-ness (callStatic vs
// callInstance), the accessor name (`get_X`/`set_X`), `typeArgs`, the `op_` name with the receiver already
// prepended, the constructed-generic owner IDENTITY (memberType supertype walk) — and does NOT decide the .NET call
// SHAPE. THIS pass resolves the owner FQN against the loaded .NET reference assemblies (ReferenceMetadataIndex's
// long-lived MetadataLoadContext) and, when it IS a reachable .NET type, reflects the member to bind the shape:
// static/instance method -> `clrStatic`/`clrInstance`; a `get_X`/`set_X` naming a .NET property OR field ->
// `clrPropGet`/`clrPropSet`; a generic method (`typeArgs` present) -> `clrGenericStatic`/`clrGenericInstance`; an
// indexer (`get_Item`/`set_Item`, an indexed property) or a synthetic member-extension accessor (no matching
// property/field) stays a plain instance method call. A `kotlin.*`/local/unresolvable owner is left untouched (the
// stdlib is bound by MemberCallSubstitution off the ref.dll; a local type is emitted here). CLR-ONLY vocabulary that
// has no plain-Kotlin form — `.NET events` (ClrEvent<T>), `byref`/`ClrRef<T>` — is NOT emitted as a plain call by
// kotc (kotc lowers it directly, as facadegen-injected CLR vocab), so it never reaches this pass. Runs BEFORE
// ClrEventOperatorBinding/KClassMemberBinding/MemberCallSubstitution and before BirTypeLowering, so the shaped `clr*`
// nodes still carry pure-Kotlin type tokens that the subsequent lowering turns into the CLR forms — the CIR is
// byte-identical to what kotc used to emit directly (the shape decision merely moved down a layer). Bottom-up walk,
// mirroring ClrEventOperatorBinding/KClassMemberBinding.
static class NetInteropBinding
{
    static ReferenceMetadataIndex _refs;

    // Mutates IN PLACE (like ShapeSynthesis): this runs in bir2cir's phase-1 per-file region where every pass edits
    // `bir.Root` in place (BirFile.Root is init-only, not reassignable). The node identity is preserved (its parent link
    // stays valid); only its `k` + field set change from a plain call to the CLR shape.
    public static void Apply(JsonNode root, ReferenceMetadataIndex refs) { _refs = refs; Walk(root); }

    static string Str(JsonNode n) => (n as JsonValue)?.GetValue<string>();

    static void Walk(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            foreach (var kv in obj.ToList()) if (kv.Value != null) Walk(kv.Value);   // children first (bottom-up)
            Reshape(obj);
        }
        else if (node is JsonArray arr)
        {
            foreach (var item in arr) if (item != null) Walk(item);
        }
    }

    static void Reshape(JsonObject node)
    {
        var k = Str(node["k"]);
        // #73 M4-b: a FIELD read/write on a facadegen-injected .NET owner. kotc emits a plain `field`/`setField` by the
        // .NET-FQN identity (no shape decision); the .NET member SHAPE is bound HERE — the same axis #61 used for calls.
        // A `field`/`setField` whose owner resolves to a .NET type declaring a property OR field of that name (both, via
        // MemberIsPropertyOrField) -> clrPropGet/clrPropSet, whose EmitClrPropGet/Set is struct-receiver-safe + inlines a
        // const field (unlike the plain-field external Ldfld/Callvirt route) — matching the old kotc clrPropGet parity,
        // which reshaped unconditionally. A member the refs can't see (a non-.NET owner, or a name absent from the .NET
        // type) never resolves here -> the plain `field`/`setField` is left for ilemit's own handler.
        if (k == "field" || k == "setField") { ReshapeField(node, write: k == "setField"); return; }
        // #73 M4.4: a BOUND method reference `netObj::m`. kotc emits a NEUTRAL `newBoundDelegate` (the same kind it uses
        // for a Kotlin-owner bound ref) carrying the owner FQN identity + argTypes; bir2cir decides the SHAPE. When the
        // owner resolves to a .NET type off the refs -> the CLR bound-delegate dialect node `newBoundClrDelegate` (ilemit
        // binds the target by reflection). A Kotlin/local owner never resolves here -> the plain `newBoundDelegate` is
        // left for ilemit's own FindMethod-based handler. Byte-identical to kotc's former newBoundClrDelegate emit.
        if (k == "newBoundDelegate") { ReshapeBoundDelegate(node); return; }
        if (k != "callStatic" && k != "callInstance") return;
        var ownerJson = node["ownerType"];
        // Peel Nullable/Oblivious/ByRef wrappers to reach the underlying .NET Fqn (a `List<Item>?` receiver's owner is
        // spelled `nullable(fqn List<Item>)`); the ORIGINAL wrapped node is preserved verbatim in the `type` slot below
        // (ilemit unwraps nullability when resolving the owner — byte-identical to the old kotc `clrInstance.type`).
        var ownerFqnNode = ownerJson == null ? null : UnwrapFqn(ownerJson);
        if (ownerFqnNode == null) return;
        var bare = ReferenceMetadataIndex.BareOwnerFqn(ownerFqnNode.Name);
        var netType = _refs.ResolveNetType(bare, ownerFqnNode.Args?.Length ?? 0);
        if (netType == null) return;   // not a reachable .NET-interop owner -> leave for the other binders

        var isStatic = k == "callStatic";
        var method = Str(node["method"]);
        var hasTypeArgs = node["typeArgs"] is JsonArray ta && ta.Count > 0;

        // Detach every current field (removing a key from a JsonObject detaches its value) so it can be re-added in the
        // CLR-shape order — byte-identical to what kotc used to emit directly, only the shape decision moved here.
        var v = new Dictionary<string, JsonNode>(StringComparer.Ordinal);
        foreach (var key in node.Select(kv => kv.Key).ToList()) { var val = node[key]; node.Remove(key); v[key] = val; }
        JsonNode Take(string key) => v.TryGetValue(key, out var x) ? x : null;
        var owner = Take("ownerType");
        var args = Take("args") as JsonArray ?? new JsonArray();

        // GENERIC .NET method: the presence of `typeArgs` (a frontend fact) is the signal. ilemit MakeGenericMethods it;
        // ShapeSynthesis (which runs right after this pass) derives the overload-matcher `shapes` from `shapeTypes`.
        if (hasTypeArgs)
        {
            node["k"] = isStatic ? "clrGenericStatic" : "clrGenericInstance";
            node["type"] = owner;
            node["method"] = method;
            node["typeArgs"] = Take("typeArgs");
            node["shapeTypes"] = Take("shapeTypes") ?? new JsonArray();
            if (!isStatic) node["recv"] = Take("recv");
            node["args"] = args;
            if (Take("suspendCall") is JsonNode sc1) node["suspendCall"] = sc1;
            return;
        }

        // PROPERTY ACCESSOR by the frontend get/set KIND (A2 step 3): kotc emits the BARE property NAME + a
        // `"prop":"get"/"set"` marker (the accessor KIND — a frontend fact from correspondingPropertySymbol), NOT the
        // `get_`/`set_` .NET accessor slot. bir2cir APPLIES the .NET accessor convention off the refs: a real non-indexed
        // .NET property/field of that bare name -> clrPropGet/clrPropSet (the SAME node the legacy get_-prefix path
        // produces); otherwise (a synthetic member-extension / top-level-extension accessor with no matching .NET member)
        // reconstruct the `get_`/`set_<name>` plain method call and fall through — byte-identical to the old kotc emission.
        var propKind = Str(Take("prop"));
        // .NET DEFAULT INDEXED PROPERTY (A2 step 4): kotc emits the faithful Kotlin get/set operator identity
        // (`method:"get"/"set"`) + an index marker; it does NOT bake the `get_Item`/`set_Item` slot (WRONG for a custom
        // `[IndexerName]`). Resolve the .NET type's default indexed property off the refs (its DefaultMember/[IndexerName]
        // name) -> its `get_`/`set_` accessor method, then fall through to the PLAIN clrInstance method path — an indexer
        // is an INDEXED property, so MemberIsPropertyOrField excludes it and it stays a method call, byte-identical to the
        // old hardcoded `get_Item`/`set_Item` for the standard case.
        if (propKind == "index-get" || propKind == "index-set")
        {
            var isIxSet = propKind == "index-set";
            method = DefaultIndexerAccessor(netType, isIxSet) ?? (isIxSet ? "set_Item" : "get_Item");
        }
        else if (propKind == "get" || propKind == "set")
        {
            var isSet = propKind == "set";
            if (method != null && MemberIsPropertyOrField(netType, method))
            {
                if (!isSet)
                {
                    node["k"] = "clrPropGet";
                    node["type"] = owner;
                    node["name"] = method;
                    node["ret"] = Take("ret");
                    node["static"] = isStatic;
                    node["recv"] = isStatic ? null : Take("recv");
                    return;
                }
                node["k"] = "clrPropSet";
                node["type"] = owner;
                node["name"] = method;
                node["static"] = isStatic;
                node["recv"] = isStatic ? null : Take("recv");
                JsonNode setVal = null;
                if (args.Count > 0) { setVal = args[0]; args.RemoveAt(0); }
                node["value"] = setVal;
                return;
            }
            // No matching .NET property/field -> a synthetic accessor METHOD: apply the get_/set_ convention and fall
            // through to the plain instance/static method path (byte-identical to the old kotc-baked get_/set_<name>).
            method = (isSet ? "set_" : "get_") + method;
        }

        // PROPERTY / FIELD accessor: a `get_X`/`set_X` that names a real .NET property (non-indexed) or field ->
        // clrPropGet/clrPropSet (ilemit emits the accessor call or an ldsfld/ldfld for a field-backed one). A `get_X`
        // that names NEITHER (a hand-written `get_`-prefixed method, an indexer `get_Item`, a synthetic
        // member-extension accessor) falls through to the plain method path below — exactly as kotc emitted before.
        if (method != null && (method.StartsWith("get_", StringComparison.Ordinal) || method.StartsWith("set_", StringComparison.Ordinal))
            && method.Length > 4 && MemberIsPropertyOrField(netType, method.Substring(4)))
        {
            var propName = method.Substring(4);
            if (method.StartsWith("get_", StringComparison.Ordinal))
            {
                node["k"] = "clrPropGet";
                node["type"] = owner;
                node["name"] = propName;
                node["ret"] = Take("ret");
                node["static"] = isStatic;
                node["recv"] = isStatic ? null : Take("recv");
                return;
            }
            node["k"] = "clrPropSet";
            node["type"] = owner;
            node["name"] = propName;
            node["static"] = isStatic;
            node["recv"] = isStatic ? null : Take("recv");
            JsonNode value = null;
            if (args.Count > 0) { value = args[0]; args.RemoveAt(0); }   // detach args[0] from the (already-detached) array
            node["value"] = value;
            return;
        }

        // .NET OPERATOR: kotc emits a .NET-type operator (`Vec2 + Vec2`, `-a`) as the PLAIN Kotlin operator identity
        // (`callInstance method="plus" recv:<a> args:[<b>]`) — it does NOT know the CLR `op_X` slot (layer purity).
        // Reconstruct the .NET static operator off the refs: map the Kotlin operator name to its `op_X` slot, confirm the
        // CLR type declares that `op_X` as a `public static` method (DON'T rewrite a Kotlin `plus` on a non-operator .NET
        // type), and emit `clrStatic op_X` with the receiver PREPENDED as the first arg (binary: [recv, arg]; unary
        // unaryMinus/unaryPlus/inc/dec: [recv] only). This is the exact node kotc used to emit directly (callStatic op_X,
        // receiver already prepended) -> byte-identical CIR. The receiver's type is the declaring .NET type = the owner,
        // mirroring kotc's old `birType(recv.type)` for argTypes[0].
        if (!isStatic && method != null && OperatorToNet.TryGetValue(method, out var opNet)
            && DeclaresPublicStaticMethod(netType, opNet))
        {
            var recv = Take("recv");
            var argTypes0 = Take("argTypes") as JsonArray ?? new JsonArray();
            var newArgTypes = new JsonArray { owner.DeepClone() };
            while (argTypes0.Count > 0) { var at = argTypes0[0]; argTypes0.RemoveAt(0); newArgTypes.Add(at); }
            var newArgs = new JsonArray { recv };
            while (args.Count > 0) { var a = args[0]; args.RemoveAt(0); newArgs.Add(a); }
            node["k"] = "clrStatic";
            node["type"] = owner;
            node["method"] = opNet;
            node["argTypes"] = newArgTypes;
            node["ret"] = Take("ret");
            node["args"] = newArgs;
            if (Take("suspendCall") is JsonNode scOp) node["suspendCall"] = scOp;
            return;
        }

        // PLAIN static/instance method (incl. indexer get_Item/set_Item, member-extension synthetic accessor).
        node["k"] = isStatic ? "clrStatic" : "clrInstance";
        node["type"] = owner;
        node["method"] = method;
        node["argTypes"] = Take("argTypes") ?? new JsonArray();
        node["ret"] = Take("ret");
        if (!isStatic) node["recv"] = Take("recv");
        node["args"] = args;
        if (Take("suspendCall") is JsonNode sc2) node["suspendCall"] = sc2;
    }

    // #73 M4-b — bind a `field`/`setField` on a facadegen-injected .NET owner to clrPropGet/clrPropSet. Resolves the
    // owner off the refs (skips kotlin.*/local owners); a name that is a real .NET property OR field (MemberIsProperty-
    // OrField matches both) is reshaped — EmitClrPropGet/Set falls through property -> get_ accessor -> field, so it
    // serves a genuine field too (with const-inlining + struct-safe receiver). A name the refs can't see stays plain.
    static void ReshapeField(JsonObject node, bool write)
    {
        var ownerJson = node["ownerType"];
        var ownerFqnNode = ownerJson == null ? null : UnwrapFqn(ownerJson);
        if (ownerFqnNode == null) return;
        var netType = _refs.ResolveNetType(ReferenceMetadataIndex.BareOwnerFqn(ownerFqnNode.Name), ownerFqnNode.Args?.Length ?? 0);
        if (netType == null) return;
        var name = Str(node["name"]);
        if (name == null || !MemberIsPropertyOrField(netType, name)) return;
        var v = new Dictionary<string, JsonNode>(StringComparer.Ordinal);
        foreach (var key in node.Select(kv => kv.Key).ToList()) { var val = node[key]; node.Remove(key); v[key] = val; }
        JsonNode Take(string key) => v.TryGetValue(key, out var x) ? x : null;
        node["k"] = write ? "clrPropSet" : "clrPropGet";
        node["type"] = Take("ownerType");
        node["name"] = Take("name");
        node["static"] = false;
        node["recv"] = Take("recv");
        if (write) node["value"] = Take("value");
    }

    // #73 M4.4 — reshape a BOUND method-ref `newBoundDelegate` on a facadegen-injected .NET owner to the CLR
    // `newBoundClrDelegate` dialect node (ilemit resolves the target by reflection over the .NET type). Resolves the
    // owner off the refs (skips kotlin.*/local owners — those stay a plain newBoundDelegate ilemit binds via FindMethod).
    // The field set + order mirror kotc's former newBoundClrDelegate emission exactly (clrType from the owner identity,
    // method/argTypes/virtual/recv/funcType carried verbatim — including the method already Object-slot-renamed upstream).
    static void ReshapeBoundDelegate(JsonObject node)
    {
        // Only the .NET-bound producer (BirEmitter method-ref, clrOwner branch) carries `argTypes`; the Kotlin-owner
        // bound ref emits NONE. Gate on it so a cross-module Kotlin owner (a ProjectReference lib loaded via --ref,
        // which ResolveNetType WOULD resolve) is never mis-reshaped into a newBoundClrDelegate claiming `argTypes:[]`
        // — it stays the plain newBoundDelegate ilemit binds by FindMethod, exactly as before Wave 8.
        if (node["argTypes"] == null) return;
        var ownerJson = node["ownerType"];
        var ownerFqnNode = ownerJson == null ? null : UnwrapFqn(ownerJson);
        if (ownerFqnNode == null) return;
        var netType = _refs.ResolveNetType(ReferenceMetadataIndex.BareOwnerFqn(ownerFqnNode.Name), ownerFqnNode.Args?.Length ?? 0);
        if (netType == null) return;   // a Kotlin/local owner -> leave the plain newBoundDelegate for ilemit's handler
        var v = new Dictionary<string, JsonNode>(StringComparer.Ordinal);
        foreach (var key in node.Select(kv => kv.Key).ToList()) { var val = node[key]; node.Remove(key); v[key] = val; }
        JsonNode Take(string key) => v.TryGetValue(key, out var x) ? x : null;
        node["k"] = "newBoundClrDelegate";
        node["clrType"] = Take("ownerType");
        node["method"] = Take("method");
        node["argTypes"] = Take("argTypes") ?? new JsonArray();
        node["virtual"] = Take("virtual");
        node["recv"] = Take("recv");
        node["funcType"] = Take("funcType");
    }

    // Peel Nullable/Oblivious/ByRef wrappers off an owner type slot to reach the underlying .NET Fqn (name + type-args),
    // so a `List<Item>?`/`T!`/byref receiver resolves its open .NET definition. Also accepts a LEGACY STRING owner token
    // (kotc emits some owners — a referenced file class `LibKt`, the await marker `kotlin.clr.CoroutinesKt` — as a bare
    // string, not a structured `{t:fqn}` node); it carries no structured args (a method-generic's args live in
    // `typeArgs`). null when there is no Fqn underneath.
    static TypeNode.Fqn UnwrapFqn(JsonNode ownerJson)
    {
        if (ownerJson is JsonValue sv && sv.TryGetValue<string>(out var s) && s != null)
            return new TypeNode.Fqn(s);
        var t = TypeJson.Read(ownerJson);
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

    // The INVERSE of facadegen's OPERATOR_NAMES (facadegen Program.cs): a Kotlin `operator fun` name -> the .NET `op_X`
    // static-method slot. kotc emits the Kotlin identity; this pass reconstructs the .NET operator off the refs.
    static readonly Dictionary<string, string> OperatorToNet = new(StringComparer.Ordinal)
    {
        ["plus"] = "op_Addition", ["minus"] = "op_Subtraction", ["times"] = "op_Multiply", ["div"] = "op_Division",
        ["rem"] = "op_Modulus", ["unaryMinus"] = "op_UnaryNegation", ["unaryPlus"] = "op_UnaryPlus",
        ["inc"] = "op_Increment", ["dec"] = "op_Decrement",
    };

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
    static string DefaultIndexerAccessor(Type type, bool isSet)
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

    // True iff the .NET type (or a base/interface) declares a method of this name (any arity), public OR protected —
    // a Kotlin class can override a PROTECTED VIRTUAL .NET member (the WinUI OnLaunched pattern: `override fun Tag()`
    // over a protected `Base.Tag`). Used by DeclarationRename's facadegen-override slot resolution (A2 step 5) to
    // confirm a Kotlin override binds a REAL .NET method before it keeps the identity slot — facadegen injects the
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
}

