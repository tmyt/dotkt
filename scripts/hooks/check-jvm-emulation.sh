#!/usr/bin/env bash
# PostToolUse hook (Edit|Write): when newly-written compiler/stdlib text carries
# JVM-emulation signals ("matches JVM", "JVM parity", the hashCode 31-polynomial, ...),
# inject a self-check reminder into the model context. Non-blocking by design:
# it questions the judgment, it never vetoes the edit.
#
# Rule enforced: CLAUDE.md "Design doctrine" — the acceptance test for behavior choices
# is "consistent, documented, convincingly explainable"; "the JVM does it" passes none
# of the three conditions. Canonical statement: docs/dotkt-semantics.md.
set -euo pipefail

input=$(cat)
file=$(jq -r '.tool_input.file_path // empty' <<<"$input")
[ -n "$file" ] || exit 0

# Only compiler + stdlib code (worktree copies match too via the */ prefix).
case "$file" in
  */toolchain/*|*/libraries/stdlib/*) ;;
  *) exit 0 ;;
esac
# Docs/build files legitimately discuss the JVM (recorded deviations, JVM toolchain).
case "$file" in
  *.md|*.gradle|*.gradle.kts|*gradle.properties*|*.json) exit 0 ;;
esac

# Only the text being ADDED by this call (Write content / Edit new_string).
added=$(jq -r '[.tool_input.content // empty, .tool_input.new_string // empty] | join("\n")' <<<"$input")
[ -n "$added" ] || exit 0

# Emulation-INTENT phrases, not the bare token "jvm" (kotc is Kotlin/JVM-hosted and the
# stdlib sources carry kotlin.jvm imports — the bare token would be pure noise).
EMU='[Mm]atch(es|ing)? (the )?(JVM|Java)\b|same as (the )?(JVM|Java)\b|like the JVM|JVM.?s? (behavior|behaviour|semantics|compat|parity|convention|value)|as (on|in) (the )?JVM|emulat[a-z]* (the )?JVM|JVM[- ]emulat|mimic[a-z]* (the )?JVM|align[a-z]* with (the )?JVM|consistent with (the )?JVM|JVM does|JVM-(style|compatible|equivalent)'
# The classic JVM hashCode polynomial, replicated with or without naming the JVM.
HASH31='31 \* (h\b|result\b|hash)|h = 31 \*|hashCode\(\) \* 31'

matches=$( { grep -nE "$EMU" <<<"$added" || true; grep -nE "$HASH31" <<<"$added" || true; } | head -5 )
[ -n "$matches" ] || exit 0

context=$(cat <<EOF
JVM-EMULATION SELF-CHECK (automatic hook — the text just written to $file pattern-matched JVM emulation):
$matches

Project rule (CLAUDE.md Design doctrine / docs/dotkt-semantics.md): the acceptance test for every behavior choice is "consistent, documented, convincingly explainable" — Kotlin/JVM is a reader reference, NEVER a compat target, and "the JVM does it" passes none of the three conditions. Re-verify this change, in order:
1. Does the Kotlin spec/KDoc contract fix this behavior? -> honor it by default and frame it "Kotlin contract" (cite the spec/KDoc, not the JVM).
2. Does Kotlin leave it unspecified? -> take the CLR-NATIVE form (native GetHashCode, CLR boxing/identity, BCL formatting/ordering/case mapping) and frame it "deliberate CLR choice (reason)". Do NOT hand-force the JVM value.
3. Does CLR/interop consistency convincingly outweigh even the KDoc letter? -> an "interop-first deviation" is allowed (exemplar: "SS-less" case mapping — "ß".uppercase() stays "ß" because one-to-one mapping is mscorlib-general), but it must be consistent, convincingly explained, and recorded in docs/dotkt-semantics.md.

If this edit replicates a JVM-specific artifact (the hashCode 31-polynomial, JVM boxing caches, JVM exception message text, @Jvm* semantics) with no justification beyond JVM behavior, STOP and reconsider — the CLR-native form is almost certainly the right one. If the JVM mention is legitimate (Kotlin/JVM-hosted frontend infrastructure, or a comment RECORDING an accepted deviation), state that justification explicitly in your report and continue.
EOF
)

jq -n --arg ctx "$context" --arg f "$(basename "$file")" '{
  systemMessage: ("⚠ JVMエミュレーション自己点検が発火: " + $f),
  hookSpecificOutput: {
    hookEventName: "PostToolUse",
    additionalContext: $ctx
  }
}'
