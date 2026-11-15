// Shared evaluation order and CFG solver for the owning suspend liveness walk.
// CallEvalLowering consumes the same rank table when realizing evaluated plans.
using System;
using System.Collections.Generic;
using System.Linq;

static partial class SuspendLiveness
{
    // Evaluation-order rank of an operand key, for the generic ordered walk. Lower runs first. Keys absent from
    // the table run last, in insertion order. The ranks encode the operand order of the CIR node shapes ilemit
    // emits (receiver, then the indexed/sized operand, then the value, then the argument list).
    static readonly Dictionary<string, int> KeyRank = new(StringComparer.Ordinal)
    {
        ["recv"] = 1,
        ["array"] = 2, ["src"] = 2, ["target"] = 2, ["ptr"] = 2,
        ["lhs"] = 3,
        ["index"] = 4, ["len"] = 4,
        ["from"] = 5, ["to"] = 5, ["step"] = 5, ["size"] = 5, ["count"] = 5,
        ["e"] = 6, ["key"] = 6, ["inner"] = 6,
        ["value"] = 7,
        ["rhs"] = 8,
        ["args"] = 9, ["elems"] = 9, ["parts"] = 9, ["captures"] = 9, ["capValues"] = 9,
        ["init"] = 10,
        ["stmts"] = 11, ["result"] = 12,
    };

    /// The evaluation-order rank of an operand key — the toolchain's ONE statement of "which operand of a node runs
    /// first". Shared with CallEvalLowering, which needs the same order to know where an inlined plan binding will be
    /// evaluated; keeping it here keeps the two answers from drifting apart.
    internal static int OperandRank(string key) => KeyRank.GetValueOrDefault(key, 50);

    enum EvKind { Use, Def, Susp, Label, Goto, BrIf, Stop }

    readonly struct Ev
    {
        public readonly EvKind Kind;
        public readonly string Name;     // Use/Def: the variable; Susp: the callee description
        public readonly int Label;       // Label/Goto/BrIf: the target id
        public Ev(EvKind kind, string name, int label) { Kind = kind; Name = name; Label = label; }
    }

    // Typed event emission supplies the one backward CFG solve.
    static Dictionary<string, string> SolveEvents(IReadOnlyList<Ev> events,
        IReadOnlyDictionary<int, List<int>> extraSuccessors, IReadOnlyDictionary<string, int> ids, bool bailOut)
    {
        var across = new Dictionary<string, string>(StringComparer.Ordinal);
        if (ids.Count == 0 || events.Count == 0) return across;
        if (bailOut)
        {
            foreach (var name in ids.Keys) across.TryAdd(name, "a suspending call");
            return across;
        }

        var n = events.Count;
        var labelAt = new Dictionary<int, int>();
        for (var i = 0; i < n; i++)
            if (events[i].Kind == EvKind.Label) labelAt[events[i].Label] = i;

        // Normal and EXCEPTIONAL successors are kept apart: an exception can be raised BEFORE this event's own
        // write completes, so the handler's live set must not be killed by this event's def. (`try { x = f() }
        // catch { use(x) }` needs the pre-assignment `x`.)
        var succ = new List<int>[n];
        var hsucc = new List<int>[n];
        for (var i = 0; i < n; i++)
        {
            var s = new List<int>();
            switch (events[i].Kind)
            {
                case EvKind.Goto:
                    if (labelAt.TryGetValue(events[i].Label, out var g)) s.Add(g);
                    break;
                case EvKind.Stop:
                    break;                                       // only the handler edges
                case EvKind.BrIf:
                    if (labelAt.TryGetValue(events[i].Label, out var t)) s.Add(t);
                    if (i + 1 < n) s.Add(i + 1);
                    break;
                default:
                    if (i + 1 < n) s.Add(i + 1);
                    break;
            }
            var h = new List<int>();
            if (extraSuccessors.TryGetValue(i, out var extra))
                foreach (var lbl in extra)
                    if (labelAt.TryGetValue(lbl, out var hi) && !h.Contains(hi)) h.Add(hi);
            succ[i] = s;
            hsucc[i] = h;
        }

        var live = new HashSet<int>[n];
        for (var i = 0; i < n; i++) live[i] = new HashSet<int>();
        // Round-robin over the event list in reverse. Backward liveness on this CFG converges in a small
        // number of sweeps (one per loop-nesting depth plus one); the bound is a tripwire, not a policy —
        // hitting it would mean the CFG is not what this pass models, so it fails loud rather than silently
        // reporting a variable dead.
        const int MaxRounds = 2000;
        var rounds = 0;
        bool changed;
        do
        {
            if (++rounds > MaxRounds)
                throw new InvalidOperationException(
                    "bir2cir: suspend-lowering: local liveness did not converge over the state-machine body "
                    + $"({n} events) — refusing to guess which locals survive a suspension.");
            changed = false;
            for (var i = n - 1; i >= 0; i--)
            {
                var e = events[i];
                var next = new HashSet<int>();
                foreach (var s in succ[i]) next.UnionWith(live[s]);
                if (e.Kind == EvKind.Def && ids.TryGetValue(e.Name, out var dId)) next.Remove(dId);
                if (e.Kind == EvKind.Use && ids.TryGetValue(e.Name, out var uId)) next.Add(uId);
                foreach (var h in hsucc[i]) next.UnionWith(live[h]);   // never killed by this event's def
                if (next.Count != live[i].Count || !next.SetEquals(live[i])) { live[i] = next; changed = true; }
            }
        } while (changed);

        // A variable needs an SM field iff it is LIVE-OUT at some suspension point (a suspension neither reads
        // nor writes a local, so live-out equals this event's own live set).
        var byId = new string[ids.Count];
        foreach (var kv in ids) byId[kv.Value] = kv.Key;
        for (var i = 0; i < n; i++)
        {
            if (events[i].Kind != EvKind.Susp) continue;
            foreach (var id in live[i]) across.TryAdd(byId[id], events[i].Name);
        }
        return across;
    }
}
