#!/bin/sh

SCRIPT_DIR=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
REPO_ROOT=$(CDPATH= cd -- "$SCRIPT_DIR/.." && pwd)

rm -rf ~/.nuget/packages/dotkt*
rm -rf "$REPO_ROOT"/build/test-package-cache/dotkt*
dotnet nuget locals http-cache --clear
