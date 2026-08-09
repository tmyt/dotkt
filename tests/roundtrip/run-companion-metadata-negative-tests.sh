#!/usr/bin/env bash
SCRIPT_NAME="$(basename -- "$0")"
source "$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd -P)/scripts/lib.sh"

FIXTURE="$ROOT/tests/roundtrip/malformed-companion-fixtures/MalformedCompanionFixtures.csproj"
need_tool dll2klib
need_tool bir2cir
DLL2KLIB="$DLL2KLIB_DLL"
BIR2CIR="$BIR2CIR_DLL"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

DOTNET_BIN="$(readlink -f "$(command -v dotnet)")"
DOTNET_REF_PACK="$(dirname "$DOTNET_BIN")/packs/Microsoft.NETCore.App.Ref"
CORE_REF="$(find "$DOTNET_REF_PACK" -path '*/ref/net10.0/System.Runtime.dll' -print | sort -V | tail -1)"
[[ -n "$CORE_REF" ]] || { echo "companion-negative: System.Runtime reference assembly not found" >&2; exit 1; }

BIR="$WORK/probe.bir.json"
printf '%s\n' '{"fileClass":"CompanionNegativeProbeKt","hasMain":false,"fields":[],"methods":[],"types":[]}' >"$BIR"

run_rejected() {
    local tool="$1"
    local dll="$2"
    local expected="$3"
    local log="$WORK/$(basename "$dll").$tool.log"
    if [[ "$tool" == "dll2klib" ]]; then
        printf '%s\n' "$dll" >"$WORK/refs.rsp"
        if dotnet "$DLL2KLIB" --out "$WORK/klib" "@$WORK/refs.rsp" >"$log" 2>&1; then
            echo "companion-negative: dll2klib accepted malformed trusted carrier $dll" >&2
            return 1
        fi
    elif dotnet "$BIR2CIR" "$WORK/cir" --compile-refs "$CORE_REF;$dll" "$BIR" >"$log" 2>&1; then
        echo "companion-negative: bir2cir accepted malformed trusted carrier $dll" >&2
        return 1
    fi
    if ! grep -Eqi "$expected" "$log"; then
        echo "companion-negative: $tool rejected malformed metadata for the wrong reason" >&2
        tail -20 "$log" >&2
        return 1
    fi
}

build_fixture() {
    local name="$1" define="$2" out="$WORK/$1"
    dotnet build "$FIXTURE" -v:q --nologo \
        -p:AssemblyName="$name" \
        -p:DefineConstants="$define" \
        -p:BaseIntermediateOutputPath="$out/obj/" \
        -p:OutputPath="$out/bin/"
}

build_fixture MalformedCompanionCarrier ''
NON_NESTED="$WORK/MalformedCompanionCarrier/bin/MalformedCompanionCarrier.dll"
run_rejected dll2klib "$NON_NESTED" 'ordinary nested'
run_rejected bir2cir "$NON_NESTED" 'ordinary nested'

BAD_NAME="$WORK/MalformedCompanionCarrier/bin/BadSemanticNameCarrier.dll"
cp "$NON_NESTED" "$BAD_NAME"
sed -i 's/"name":"Companion"/"name":"Bad.Name!"/g' "$BAD_NAME"
run_rejected dll2klib "$BAD_NAME" 'semantic name segment'
run_rejected bir2cir "$BAD_NAME" 'semantic owner/name'

build_fixture NonPublicCompanionCarrier NON_PUBLIC_CARRIER
NON_PUBLIC="$WORK/NonPublicCompanionCarrier/bin/NonPublicCompanionCarrier.dll"
# C# cannot spell '$' in an identifier. Keep the fixture otherwise structurally valid and make the same-length
# metadata-string substitution so an implementation that wrongly accepts NestedFamily reaches the singleton check.
sed -i 's/XINSTANCE/$INSTANCE/g' "$NON_PUBLIC"
run_rejected dll2klib "$NON_PUBLIC" 'NestedPublic visibility'
run_rejected bir2cir "$NON_PUBLIC" 'NestedPublic visibility'

build_fixture GenericOwnerNestedCompanionCarrier GENERIC_OWNER_NESTED_CARRIER
GENERIC_OWNER_NESTED="$WORK/GenericOwnerNestedCompanionCarrier/bin/GenericOwnerNestedCompanionCarrier.dll"
sed -i 's/XINSTANCE/$INSTANCE/g' "$GENERIC_OWNER_NESTED"
run_rejected dll2klib "$GENERIC_OWNER_NESTED" 'non-generic physical owner'
run_rejected bir2cir "$GENERIC_OWNER_NESTED" 'non-generic physical owner'

build_fixture NestedSidecarCompanionCarrier NESTED_SIDECAR_CARRIER
NESTED_SIDECAR="$WORK/NestedSidecarCompanionCarrier/bin/NestedSidecarCompanionCarrier.dll"
sed -i 's/XINSTANCE/$INSTANCE/g' "$NESTED_SIDECAR"
run_rejected dll2klib "$NESTED_SIDECAR" 'must be a top-level type'
run_rejected bir2cir "$NESTED_SIDECAR" 'must be a top-level type'

build_fixture NonGenericSidecarCompanionCarrier NON_GENERIC_SIDECAR_CARRIER
NON_GENERIC_SIDECAR="$WORK/NonGenericSidecarCompanionCarrier/bin/NonGenericSidecarCompanionCarrier.dll"
sed -i 's/XINSTANCE/$INSTANCE/g' "$NON_GENERIC_SIDECAR"
run_rejected dll2klib "$NON_GENERIC_SIDECAR" 'requires a generic physical owner'
run_rejected bir2cir "$NON_GENERIC_SIDECAR" 'requires a generic physical owner'

build_fixture NonGenericStaticCarrier NON_GENERIC_STATIC_CARRIER
NON_GENERIC_STATIC="$WORK/NonGenericStaticCarrier/bin/NonGenericStaticCarrier.dll"
run_rejected dll2klib "$NON_GENERIC_STATIC" 'malformed KotlinStaticCarrier'
run_rejected bir2cir "$NON_GENERIC_STATIC" 'malformed trusted.*KotlinStaticCarrier'

build_fixture NonPublicStaticCarrier NON_PUBLIC_STATIC_CARRIER
NON_PUBLIC_STATIC="$WORK/NonPublicStaticCarrier/bin/NonPublicStaticCarrier.dll"
run_rejected dll2klib "$NON_PUBLIC_STATIC" 'KotlinStaticCarrier.*must be public'
run_rejected bir2cir "$NON_PUBLIC_STATIC" 'KotlinStaticCarrier.*must be public'

build_fixture ExtraStaticCarrierPayload EXTRA_STATIC_CARRIER_PAYLOAD
EXTRA_STATIC_PAYLOAD="$WORK/ExtraStaticCarrierPayload/bin/ExtraStaticCarrierPayload.dll"
run_rejected dll2klib "$EXTRA_STATIC_PAYLOAD" 'payload must contain exactly one'
run_rejected bir2cir "$EXTRA_STATIC_PAYLOAD" 'malformed trusted.*KotlinStaticCarrier'

build_fixture InstanceStaticCarrierMember INSTANCE_STATIC_CARRIER_MEMBER
INSTANCE_STATIC_MEMBER="$WORK/InstanceStaticCarrierMember/bin/InstanceStaticCarrierMember.dll"
run_rejected dll2klib "$INSTANCE_STATIC_MEMBER" 'contains an instance method or constructor'
run_rejected bir2cir "$INSTANCE_STATIC_MEMBER" 'contains an instance declaration'

build_fixture InstanceCompanionExtensionMethod INSTANCE_COMPANION_EXTENSION_METHOD
INSTANCE_EXTENSION_METHOD="$WORK/InstanceCompanionExtensionMethod/bin/InstanceCompanionExtensionMethod.dll"
run_rejected dll2klib "$INSTANCE_EXTENSION_METHOD" 'KotlinCompanionExtension.*static method'
run_rejected bir2cir "$INSTANCE_EXTENSION_METHOD" 'malformed trusted.*KotlinCompanionExtension.*method'

build_fixture InstanceCompanionExtensionField INSTANCE_COMPANION_EXTENSION_FIELD
INSTANCE_EXTENSION_FIELD="$WORK/InstanceCompanionExtensionField/bin/InstanceCompanionExtensionField.dll"
run_rejected dll2klib "$INSTANCE_EXTENSION_FIELD" 'KotlinCompanionExtension.*static field'
run_rejected bir2cir "$INSTANCE_EXTENSION_FIELD" 'malformed trusted.*KotlinCompanionExtension.*field'

build_fixture WrongTargetCompanionExtension COMPANION_EXTENSION_WRONG_TARGET
WRONG_TARGET_EXTENSION="$WORK/WrongTargetCompanionExtension/bin/WrongTargetCompanionExtension.dll"
run_rejected dll2klib "$WRONG_TARGET_EXTENSION" 'KotlinCompanionExtension.*MethodDefinition or FieldDefinition.*TypeDefinition'
run_rejected bir2cir "$WRONG_TARGET_EXTENSION" 'KotlinCompanionExtension.*MethodDefinition or FieldDefinition.*TypeDefinition'

build_fixture WrongPropertyTargetCompanionExtension COMPANION_EXTENSION_PROPERTY_TARGET
WRONG_PROPERTY_TARGET_EXTENSION="$WORK/WrongPropertyTargetCompanionExtension/bin/WrongPropertyTargetCompanionExtension.dll"
run_rejected dll2klib "$WRONG_PROPERTY_TARGET_EXTENSION" 'KotlinCompanionExtension.*MethodDefinition or FieldDefinition.*PropertyDefinition'
run_rejected bir2cir "$WRONG_PROPERTY_TARGET_EXTENSION" 'KotlinCompanionExtension.*MethodDefinition or FieldDefinition.*PropertyDefinition'

build_fixture NonFileCompanionExtensionMethod NON_FILE_COMPANION_EXTENSION_METHOD
NON_FILE_EXTENSION_METHOD="$WORK/NonFileCompanionExtensionMethod/bin/NonFileCompanionExtensionMethod.dll"
run_rejected dll2klib "$NON_FILE_EXTENSION_METHOD" 'KotlinCompanionExtension.*static method.*file facade'
run_rejected bir2cir "$NON_FILE_EXTENSION_METHOD" 'malformed trusted.*KotlinCompanionExtension.*method'

build_fixture SpecialNameCompanionExtension SPECIAL_NAME_COMPANION_EXTENSION
SPECIAL_NAME_EXTENSION="$WORK/SpecialNameCompanionExtension/bin/SpecialNameCompanionExtension.dll"
run_rejected dll2klib "$SPECIAL_NAME_EXTENSION" 'KotlinCompanionExtension.*ordinary static method'
run_rejected bir2cir "$SPECIAL_NAME_EXTENSION" 'malformed trusted.*KotlinCompanionExtension.*method'

build_fixture ConstructorCompanionExtension CONSTRUCTOR_COMPANION_EXTENSION
CONSTRUCTOR_EXTENSION="$WORK/ConstructorCompanionExtension/bin/ConstructorCompanionExtension.dll"
run_rejected dll2klib "$CONSTRUCTOR_EXTENSION" 'KotlinCompanionExtension.*static method'
run_rejected bir2cir "$CONSTRUCTOR_EXTENSION" 'malformed trusted.*KotlinCompanionExtension.*constructor'

build_fixture StaticConstructorCompanionExtension STATIC_CONSTRUCTOR_COMPANION_EXTENSION
STATIC_CONSTRUCTOR_EXTENSION="$WORK/StaticConstructorCompanionExtension/bin/StaticConstructorCompanionExtension.dll"
run_rejected dll2klib "$STATIC_CONSTRUCTOR_EXTENSION" 'KotlinCompanionExtension.*ordinary static method'
run_rejected bir2cir "$STATIC_CONSTRUCTOR_EXTENSION" 'malformed trusted.*KotlinCompanionExtension.*constructor'

build_fixture NamedArgumentCompanionExtension NAMED_ARGUMENT_COMPANION_EXTENSION
NAMED_ARGUMENT_EXTENSION="$WORK/NamedArgumentCompanionExtension/bin/NamedArgumentCompanionExtension.dll"
run_rejected dll2klib "$NAMED_ARGUMENT_EXTENSION" 'malformed.*KotlinCompanionExtension.*carrier named arguments'
run_rejected bir2cir "$NAMED_ARGUMENT_EXTENSION" 'malformed.*KotlinCompanionExtension.*carrier named arguments'

UNSUPPORTED_TARGET_EXTENSION="$WORK/UnsupportedCompanionExtensionTarget.dll"
dotnet run --project "$ROOT/tests/roundtrip/malformed-companion-fixtures/UnsupportedTargetGenerator.csproj" \
    -v:q -- "$UNSUPPORTED_TARGET_EXTENSION"
run_rejected dll2klib "$UNSUPPORTED_TARGET_EXTENSION" 'KotlinCompanionExtension.*MethodDefinition or FieldDefinition.*InterfaceImplementation'
run_rejected bir2cir "$UNSUPPORTED_TARGET_EXTENSION" 'KotlinCompanionExtension.*MethodDefinition or FieldDefinition.*InterfaceImplementation'

UNSUPPORTED_TARGET_STATIC="$WORK/UnsupportedStaticCarrierTarget.dll"
dotnet run --project "$ROOT/tests/roundtrip/malformed-companion-fixtures/UnsupportedTargetGenerator.csproj" \
    -v:q -- "$UNSUPPORTED_TARGET_STATIC" static
run_rejected dll2klib "$UNSUPPORTED_TARGET_STATIC" 'KotlinStaticCarrier.*TypeDefinition.*InterfaceImplementation'
run_rejected bir2cir "$UNSUPPORTED_TARGET_STATIC" 'KotlinStaticCarrier.*TypeDefinition.*InterfaceImplementation'

build_fixture MalformedPrivateCompanionExtension MALFORMED_PRIVATE_COMPANION_EXTENSION
MALFORMED_PRIVATE_EXTENSION="$WORK/MalformedPrivateCompanionExtension/bin/MalformedPrivateCompanionExtension.dll"
run_rejected dll2klib "$MALFORMED_PRIVATE_EXTENSION" 'malformed.*KotlinCompanionExtension.*payload'
run_rejected bir2cir "$MALFORMED_PRIVATE_EXTENSION" 'malformed.*KotlinCompanionExtension.*payload'

build_fixture LegacyStringCompanionExtension LEGACY_STRING_COMPANION_EXTENSION
LEGACY_STRING_EXTENSION="$WORK/LegacyStringCompanionExtension/bin/LegacyStringCompanionExtension.dll"
run_rejected dll2klib "$LEGACY_STRING_EXTENSION" 'Type must be a JSON object'
run_rejected bir2cir "$LEGACY_STRING_EXTENSION" 'companion-extension receiver is not a bare classifier type'

build_fixture ParameterizedCompanionExtension PARAMETERIZED_COMPANION_EXTENSION
PARAMETERIZED_EXTENSION="$WORK/ParameterizedCompanionExtension/bin/ParameterizedCompanionExtension.dll"
run_rejected dll2klib "$PARAMETERIZED_EXTENSION" 'malformed.*KotlinCompanionExtension.*payload'
run_rejected bir2cir "$PARAMETERIZED_EXTENSION" 'companion-extension receiver is not a bare classifier type'

echo "malformed companion/static trusted carriers rejected consistently by dll2klib + bir2cir: OK"
