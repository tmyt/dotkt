// AUTO-SPLIT from Program.cs — part of the `Emitter` partial class (see Program.cs for the overview).
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text.Json;

// Cross-module inline-splice emission — QUARANTINED for #71/#75 step-3 deletion.
// When step-3 deletes this, delete WITH it these 4 external touchpoints:
//   1. Emitter.Expressions.cs: the `_inlineSubst` local-substitution peek (a `local` referencing a bound name).
//   2. Emitter.Expressions.cs: the `_inlineLambdas` delegateInvoke splice (calls EmitSplicedStmts).
//   3. Emitter.Expressions.cs: `case "inlineSplice":` dispatch to EmitInlineSplice.
//   4. Emitter.Bodies.cs: the `_inlineThis` peek inside EmitAddr (bound extension `this`).
// DecodeCarrier STAYS in Emitter.CompilerServices.cs (shared with metadata reading) — not quarantined.
sealed partial class Emitter
{
    // Cross-module inline splice substitution: a callee-body `local` referencing one of these names emits the bound
    // value instead; a `delegateInvoke` on a lambda-param name splices the caller's lambda body (binding its param).
    readonly Dictionary<string, JsonElement> _inlineSubst = new();

    readonly Dictionary<string, (string lamParam, JsonElement body)> _inlineLambdas = new();

    readonly List<JsonDocument> _inlineDocs = new();   // keep parsed [KotlinInline] bodies alive

    readonly Stack<LocalBuilder> _inlineThis = new();   // bound `this` (extension receiver) for the current inline splice

    // Cross-module inline splice: read the callee's carried BIR body from its [KotlinInline] (on a --ref'd assembly)
    // and emit it HERE with the call's bindings substituted (param `local`s -> bound values; lambda-param invokes ->
    // the caller's lambda body). A non-local `return` in a spliced lambda body emits a `ret` from the caller. Scope:
    // lambda-taking inline funcs (the only ones whose body must travel); callee-local name scoping is not handled yet.
    // Emit spliced statements giving their CFG labels FRESH Label objects for THIS emission (the BIR's label ids are
    // baked, so re-splicing a body — or one whose ids collide with the caller's — would MarkLabel a Label twice).
    void EmitSplicedStmts(JsonElement stmts)
    {
        var ids = new List<int>();
        void Collect(JsonElement el)
        {
            if (el.ValueKind == JsonValueKind.Object)
            {
                if (el.TryGetProperty("k", out var k) && k.GetString() == "label") ids.Add(el.GetProperty("id").GetInt32());
                foreach (var p in el.EnumerateObject()) Collect(p.Value);
            }
            else if (el.ValueKind == JsonValueKind.Array) foreach (var c in el.EnumerateArray()) Collect(c);
        }
        foreach (var st in stmts.EnumerateArray()) Collect(st);
        var saved = new Dictionary<int, Label?>();
        foreach (var id in ids) { saved[id] = _cfgLabels.TryGetValue(id, out var L) ? L : (Label?)null; _cfgLabels[id] = _il.DefineLabel(); }
        foreach (var st in stmts.EnumerateArray()) EmitStmt(st);
        foreach (var kv in saved) { if (kv.Value.HasValue) _cfgLabels[kv.Key] = kv.Value.Value; else _cfgLabels.Remove(kv.Key); }
    }

    Type EmitInlineSplice(JsonElement e)
    {
        var typeName = SlotName(e.GetProperty("type"));
        var method = e.GetProperty("method").GetString();
        // Disambiguate overloads (forEach/count for Iterable/Array/CharSequence...) by param count + generic arity, since
        // GetMethod(name) throws AmbiguousMatch. Older nodes without pc/ga fall back to the by-name lookup.
        MethodInfo mi;
        if (e.TryGetProperty("pc", out var pcEl))
        {
            int pc = pcEl.GetInt32(), ga = e.GetProperty("ga").GetInt32();
            // Search ALL referenced assemblies: the runtime stdlib is metadata-stripped (no [KotlinInline]); the inline
            // body lives in the @Clr-metadata REF assembly (DotKt.Private.Stdlib). ResolveType returns just the first.
            mi = AppDomain.CurrentDomain.GetAssemblies().Select(a => { try { return a.GetType(typeName); } catch { return null; } })
                     .Where(t => t != null).SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static))
                     .FirstOrDefault(m => m.Name == method && m.GetParameters().Length == pc && m.GetGenericArguments().Length == ga
                          && m.GetCustomAttributesData().Any(c => c.AttributeType.FullName == "DotKt.Runtime.CompilerServices.KotlinInlineAttribute"))
                 ?? throw new NotSupportedException($"inline splice: {typeName}.{method} (pc={pc} ga={ga}) with [KotlinInline] not found");
        }
        else mi = ResolveType(typeName).GetMethod(method)
                 ?? throw new NotSupportedException($"inline splice: method {typeName}.{method} not found");
        var cad = mi.GetCustomAttributesData().FirstOrDefault(c => c.AttributeType.FullName == "DotKt.Runtime.CompilerServices.KotlinInlineAttribute")
                  ?? throw new NotSupportedException($"inline splice: [KotlinInline] body missing on {typeName}.{method}");
        var doc = JsonDocument.Parse(DecodeCarrier(cad));
        _inlineDocs.Add(doc);
        var addedVals = new List<string>(); var addedLams = new List<string>();
        foreach (var b in e.GetProperty("bindings").EnumerateArray())
        {
            var pn = b.GetProperty("name").GetString();
            if (b.TryGetProperty("lambdaParam", out var lp)) { _inlineLambdas[pn] = (lp.GetString(), b.GetProperty("lambdaBody")); addedLams.Add(pn); }
            else { _inlineSubst[pn] = b.GetProperty("value"); addedVals.Add(pn); }
        }
        // An EXTENSION inline fun's body references the receiver via `this`; evaluate the bound receiver ONCE into a
        // local and push it so a `this` node in the spliced body loads it (instead of the enclosing method's arg0).
        LocalBuilder thisLoc = null;
        if (e.TryGetProperty("thisValue", out var tv))
        {
            var tt = EmitExpr(tv);
            thisLoc = _il.DeclareLocal(tt);
            _il.Emit(OpCodes.Stloc, thisLoc);
            _inlineThis.Push(thisLoc);
        }
        EmitSplicedStmts(doc.RootElement.GetProperty("body"));
        if (thisLoc != null) _inlineThis.Pop();
        foreach (var s in addedVals) _inlineSubst.Remove(s);
        foreach (var s in addedLams) _inlineLambdas.Remove(s);
        return typeof(void);
    }

}
