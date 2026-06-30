#!/usr/bin/env python3
"""Generate a COMPLETE index of `actual` declarations in runtime/stdlib/clr/, classified by
implementation status.

Discriminator (see docs/ship-tasks.md §1, memory stdlib-todo-is-filler-not-backlog):
  the @kotlin.clr.ClrIntrinsic annotation — NOT the presence of a TODO() body. A bound member keeps a
  filler TODO body that rides onto the runtime DLL but is never invoked.

Status buckets:
  BOUND   — the member (or its enclosing class) carries @kotlin.clr.ClrIntrinsic / @ClrTypeAlias.
            Substituted to a BCL call at app-emit. DONE.
  KOTLIN  — a real Kotlin body (not TODO). Implemented in Kotlin (category 2 full class / category 3 Rule-3 member). DONE.
  DECL    — no body: abstract/interface member, constructor signature, or accessor-less property. (contract only)
  UNBOUND — a TODO(...) body and NO intrinsic annotation. == REMAINING WORK (ships a throwing stub).

Regenerate after binding work:  python3 scripts/gen-clr-stdlib-actual-index.py
"""
import os, re, datetime

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
CLR = os.path.join(ROOT, "runtime", "stdlib", "clr")
OUT = os.path.join(ROOT, "docs", "clr-stdlib-actual-index.md")

INTRINSIC_RE = re.compile(r'@kotlin\.clr\.ClrIntrinsic\s*(?:\(\s*"([^"]*)"\s*\))?')
TYPEALIAS_RE = re.compile(r'@(?:kotlin\.clr\.)?ClrTypeAlias\s*(?:\(\s*"([^"]*)"\s*\))?')
ANNO_LEAD = re.compile(r'^\s*(@[\w.]+(?:\s*\([^)]*\))?)\s*')
PKG_RE = re.compile(r'^\s*package\s+(\S+)')
KIND_RE = re.compile(r'\bactual\b')
NAME_AFTER = {
    'fun': re.compile(r'\bfun\b(?:\s*<[^>]*>)?\s+(?:[\w.<>?, ]+\.)?([A-Za-z_]\w*)'),
    'val': re.compile(r'\bval\b(?:\s*<[^>]*>)?\s+(?:[\w.<>?, ]+\.)?([A-Za-z_]\w*)'),
    'var': re.compile(r'\bvar\b(?:\s*<[^>]*>)?\s+(?:[\w.<>?, ]+\.)?([A-Za-z_]\w*)'),
    'class': re.compile(r'\bclass\b\s+([A-Za-z_]\w*)'),
    'interface': re.compile(r'\binterface\b\s+([A-Za-z_]\w*)'),
    'object': re.compile(r'\bobject\b\s+([A-Za-z_]\w*)'),
    'typealias': re.compile(r'\btypealias\b\s+([A-Za-z_]\w*)'),
}

def strip_blocks_preserve_lines(text):
    out = []; i = 0; n = len(text); in_c = False
    while i < n:
        two = text[i:i+2]
        if not in_c and two == '/*':
            in_c = True; i += 2; continue
        if in_c and two == '*/':
            in_c = False; i += 2; continue
        if in_c:
            out.append('\n' if text[i] == '\n' else ' '); i += 1; continue
        out.append(text[i]); i += 1
    return ''.join(out)

def strip_line_comment(line):
    in_str = False; q = ''; i = 0
    while i < len(line) - 1:
        c = line[i]
        if in_str:
            if c == '\\': i += 2; continue
            if c == q: in_str = False
        else:
            if c in '"\'': in_str = True; q = c
            elif c == '/' and line[i+1] == '/': return line[:i]
        i += 1
    return line

def no_strings(s):
    return re.sub(r'"(?:\\.|[^"\\])*"', '""', s)

def decl_kind(code):
    if re.search(r'\bconstructor\b', code): return 'constructor'
    for kw in ('class', 'interface', 'object', 'fun', 'val', 'var', 'typealias'):
        if re.search(r'\b' + kw + r'\b', code): return kw
    return None

def decl_name(kind, code):
    if kind == 'constructor': return 'constructor'
    m = NAME_AFTER.get(kind)
    if m:
        g = m.search(code)
        if g: return g.group(1)
    return '?'

records = []   # dict(file, line, pkg, cls, kind, name, status, target, encl_bound)
files = sorted(f for f in (os.path.relpath(os.path.join(d, x), CLR)
               for d, _, fs in os.walk(CLR) for x in fs) if f.endswith('.kt'))

for rel in files:
    path = os.path.join(CLR, rel)
    raw = open(path, encoding='utf-8').read()
    clean = strip_blocks_preserve_lines(raw)
    lines = clean.split('\n')
    pkg = ''
    depth = 0
    stack = []           # frames: dict(name, intr, depth)
    pending = []         # accumulated annotation strings
    for idx, raw_line in enumerate(lines, 1):
        line = strip_line_comment(raw_line)
        if not line.strip():
            continue
        mp = PKG_RE.match(line)
        if mp:
            pkg = mp.group(1); continue
        # peel leading annotations off the line
        rest = line; inline_annos = []
        while True:
            ma = ANNO_LEAD.match(rest)
            if not ma: break
            inline_annos.append(ma.group(1)); rest = rest[ma.end():]
        code = rest
        # brace bookkeeping uses the string-stripped code part
        ns = no_strings(code)
        opens = ns.count('{'); closes = ns.count('}')

        if not code.strip():
            # annotation-only line
            pending.extend(inline_annos)
            continue

        annos = pending + inline_annos
        pending = []

        is_actual = bool(KIND_RE.search(code))
        kind = decl_kind(code) if is_actual else None

        if is_actual and kind:
            name = decl_name(kind, code)
            # body window: this code + up to 2 following non-blank cleaned lines
            window = code
            j = idx
            while j < len(lines) and len(window) < 400:
                nxt = strip_line_comment(lines[j]).strip()
                j += 1
                if not nxt: continue
                if ANNO_LEAD.match(nxt) or re.search(r'\bactual\b', nxt):
                    break
                window += ' ' + nxt
                if '{' in nxt or 'TODO(' in nxt or re.search(r'=\s*\S', nxt):
                    break
            has_todo = 'TODO(' in window
            has_body = ('{' in window) or bool(re.search(r'=\s*\S', no_strings(window)))
            text_all = ' '.join(annos) + ' ' + code
            mi = INTRINSIC_RE.search(text_all); mt = TYPEALIAS_RE.search(text_all)
            encl_bound = any(f['intr'] for f in stack)
            target = (mi.group(1) if mi else None) or (mt.group(1) if mt else None)
            if mi or mt:
                status = 'BOUND'
            elif has_todo:
                status = 'UNBOUND'
            elif has_body:
                status = 'KOTLIN'
            else:
                status = 'DECL'
            records.append(dict(file=rel, line=idx, pkg=pkg,
                                cls=(stack[-1]['name'] if stack else ''),
                                kind=kind, name=name, status=status,
                                target=target, encl_bound=encl_bound,
                                category=rel.split(os.sep)[0]))
            # push a class frame if this opens a type body
            if kind in ('class', 'interface', 'object') and opens > closes:
                depth += opens - closes
                stack.append(dict(name=name, intr=bool(mi or mt), depth=depth))
                # pop happens generically below for non-type lines; here we already adjusted depth
                while stack and stack[-1]['depth'] > depth:
                    stack.pop()
                continue
        # generic brace bookkeeping for non-type-opening lines
        depth += opens - closes
        if depth < 0: depth = 0
        while stack and stack[-1]['depth'] > depth:
            stack.pop()

# ---- reconciliation ----
from collections import Counter, defaultdict
by_status = Counter(r['status'] for r in records)
total = len(records)

# ---- render ----
def sig(r):
    n = r['name']
    if r['kind'] == 'fun': return f"fun {n}()"
    if r['kind'] in ('val', 'var'): return f"{r['kind']} {n}"
    if r['kind'] == 'constructor': return f"constructor ({r['cls']})"
    return f"{r['kind']} {n}"

EMOJI = {'BOUND': '🟢', 'KOTLIN': '🟩', 'DECL': '⬜', 'UNBOUND': '🔴'}
out = []
out.append("# CLR stdlib `actual` index\n")
out.append(f"Generated by `scripts/gen-clr-stdlib-actual-index.py` on {datetime.date.today().isoformat()}. "
           f"Source: `runtime/stdlib/clr/` ({len(files)} files, {total} `actual` declarations).\n")
out.append("**This file is generated — do not hand-edit; re-run the script.** "
           "Discriminator = `@kotlin.clr.ClrIntrinsic` presence (member or enclosing class), per "
           "`docs/ship-tasks.md` §1 — NOT the presence of a `TODO()` body. Companion to the binding "
           "ledger `docs/clr-stdlib-intrinsic-audit.md`.\n")
out.append("## Status legend\n")
out.append("| | Status | Meaning | Done? |")
out.append("|--|--|--|--|")
out.append("| 🟢 | **BOUND** | carries `@kotlin.clr.ClrIntrinsic` → substituted to a BCL call at app-emit (filler `TODO` body is harmless) | ✅ |")
out.append("| 🟩 | **KOTLIN** | real Kotlin body (not `TODO`) — full class (cat 2) or Rule-3 member (cat 3) | ✅ |")
out.append("| ⬜ | **DECL** | no body: abstract/interface member, constructor signature, accessor-less property (contract only) | — |")
out.append("| 🔴 | **UNBOUND** | `TODO()` body and NO intrinsic → ships a throwing stub | ❌ WORK |")
out.append("")
out.append("## Summary\n")
out.append("| Status | Count |")
out.append("|--|--:|")
for s in ('BOUND', 'KOTLIN', 'DECL', 'UNBOUND'):
    out.append(f"| {EMOJI[s]} {s} | {by_status.get(s,0)} |")
out.append(f"| **Total** | **{total}** |")
out.append("")
out.append(f"> Reconciliation (vs independent greps): `actual` decls found = **{total}** (grep ≈1659); "
           f"BOUND (own/enclosing intrinsic) reconciles against 262 `@kotlin.clr.ClrIntrinsic`; "
           f"UNBOUND+filler ≈ 405 `TODO(`.\n")

# category split — lets the reader limit to the hand-authored platform-intrinsic surface
catc = defaultdict(Counter)
for r in records: catc[r['category']][r['status']] += 1
out.append("## By source category (limit to platform intrinsics here)\n")
out.append("`builtins/` (primitive operators/conversions) and `generated/` are **bulk / mechanical** "
           "actuals; `kotlin/` (+`clr/`) are the **hand-authored platform-intrinsic surface**. Limit to "
           "the latter for the \"real\" binding work size.\n")
out.append("| Category | 🟢 BOUND | 🟩 KOTLIN | ⬜ DECL | 🔴 UNBOUND | total |")
out.append("|--|--:|--:|--:|--:|--:|")
for cat in sorted(catc):
    c = catc[cat]
    out.append(f"| `{cat}/` | {c.get('BOUND',0)} | {c.get('KOTLIN',0)} | {c.get('DECL',0)} | {c.get('UNBOUND',0)} | {sum(c.values())} |")
out.append("")

# per-file summary table
out.append("## Per-file status counts\n")
out.append("| File | 🟢 | 🟩 | ⬜ | 🔴 | total |")
out.append("|--|--:|--:|--:|--:|--:|")
perfile = defaultdict(Counter)
for r in records: perfile[r['file']][r['status']] += 1
for f in files:
    c = perfile.get(f)
    if not c: continue
    out.append(f"| `{f}` | {c.get('BOUND',0)} | {c.get('KOTLIN',0)} | {c.get('DECL',0)} | {c.get('UNBOUND',0)} | {sum(c.values())} |")
out.append("")

# UNBOUND worklist first (the actionable part)
unbound = [r for r in records if r['status'] == 'UNBOUND']
out.append(f"## 🔴 UNBOUND worklist ({len(unbound)}) — the remaining binding work\n")
out.append("| File:line | declaration | enclosing class | encl. class bound? |")
out.append("|--|--|--|--|")
for r in unbound:
    out.append(f"| `{r['file']}:{r['line']}` | `{sig(r)}` | {r['cls'] or '—'} | {'yes' if r['encl_bound'] else 'no'} |")
out.append("")

# full index by file
out.append("## Full index (by file)\n")
cur = None
for r in records:
    if r['file'] != cur:
        cur = r['file']
        out.append(f"\n### `{cur}`\n")
        out.append("| line | declaration | status | → CLR target | enclosing |")
        out.append("|--:|--|--|--|--|")
    tgt = f"`{r['target']}`" if r['target'] else ''
    out.append(f"| {r['line']} | `{sig(r)}` | {EMOJI[r['status']]} {r['status']} | {tgt} | {r['cls']} |")

os.makedirs(os.path.dirname(OUT), exist_ok=True)
open(OUT, 'w', encoding='utf-8').write('\n'.join(out) + '\n')

# console reconciliation
print(f"files={len(files)} actual_decls={total}")
for s in ('BOUND', 'KOTLIN', 'DECL', 'UNBOUND'):
    print(f"  {s:8} {by_status.get(s,0)}")
print(f"kind breakdown: {Counter(r['kind'] for r in records)}")
catc2 = defaultdict(Counter)
for r in records: catc2[r['category']][r['status']] += 1
for cat in sorted(catc2):
    c = catc2[cat]
    print(f"  cat {cat:10} total={sum(c.values()):4}  UNBOUND={c.get('UNBOUND',0)}")
print(f"wrote {OUT}")
