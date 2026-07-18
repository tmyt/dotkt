#!/usr/bin/env bash
# Generate FIR-injection metadata for .NET types (façade-FREE interop — facadegen's only mode; the old
# @Clr .kt-facade generation is retired, apps take .NET types via `import System.X` + this metadata).
# Input: fully-qualified .NET type names. Output: the metadata file, passed to kotc via
# CLR_TYPES_METADATA=<out.meta> (the MSBuild targets do this automatically).
source "$(dirname "$0")/lib.sh"

usage() {
	cat <<EOF
usage: $SCRIPT_NAME <out.meta> <Type.Full.Name> [<Type.Full.Name> ...]
Writes FIR-injection metadata for the given .NET types to <out.meta>. -h for this help.
EOF
}
[[ "${1:-}" == "-h" || "${1:-}" == "--help" ]] && { usage; exit 0; }
(( $# >= 2 )) || usage_error "need an output file and at least one type name"

OUT="$1"; shift
need_tool facadegen
dotnet "$FACADEGEN_DLL" "$OUT" "$@"
info "wrote $OUT"
