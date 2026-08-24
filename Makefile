# kotlin/clr — unified build interface.
#
# A THIN ORCHESTRATOR over build helpers in scripts/ and test suites in tests/. This file only sequences
# them and adds incremental file targets.
# The artifact DAG:
#
#   kotc ──┬────────────────────────────────► stdlib-klib (frontend KLIB, kotc -classpath)
#          ├─ ilemit/bir2cir/dll2klib
#          └────────────► stdlib-ref ──► stdlib-rt ──► pack (5 NuGet packages -> build/nuget-feed)
#
# Output paths are LOAD-BEARING (dotkt.sh, test runners, and eng/KotlinClr.targets hard-reference
# build/<tool>-bin, build/clr-stdlib*/dll, build/clr-stdlib-frontend-klib) — do not rename them here.
#
#   make help          # this listing (the default goal)
#   make all           # one-shot: toolchain -> stdlib -> pack
#   make -j toolchain  # the independent tools build in parallel
#   make dev SRC=Foo.kt RUN=1

SHELL := /bin/bash
.DEFAULT_GOAL := help

# ---- artifacts -----------------------------------------------------------------------------------
KOTC       := toolchain/kotc/build/install/kotc/bin/kotc
TOOLS      := ilemit bir2cir dll2klib
TOOL_DLLS  := $(foreach t,$(TOOLS),build/$(t)-bin/$(t).dll)
FE_KLIB    := build/clr-stdlib-frontend-klib/kotlin-stdlib-clr-frontend.klib
STDLIB_REF := build/clr-stdlib/dll/DotKt.Private.Stdlib.dll
STDLIB_RT  := build/clr-stdlib-rt/dll/DotKt.Stdlib.dll
FEED       := build/nuget-feed

# ---- source sets (prerequisites for incrementality) ----------------------------------------------
KOTC_SRC   := $(shell find toolchain/kotc/src -type f 2>/dev/null) toolchain/kotc/build.gradle.kts settings.gradle.kts
STDLIB_SRC := $(shell find libraries/stdlib -name '*.kt' 2>/dev/null)
# bir-common/TypeNode.cs is <Compile Link/>-shared into bir2cir/ilemit/dll2klib, so it is a source
# prerequisite of every C# tool — include it for incrementality.
tool_src    = $(shell find toolchain/$(1) toolchain/bir-common -name '*.cs' -o -name '*.csproj' 2>/dev/null | grep -vE '/(obj|bin)/')

# ==================================================================================================
# Aggregate targets
# ==================================================================================================
.PHONY: all toolchain kotc $(TOOLS) stdlib stdlib-klib stdlib-ref stdlib-rt pack \
        verify verify-core verify-tests verify-nunit verify-compile-fail verify-test-corpus verify-integration \
        verify-schema verify-sanity verify-lowering verify-msbuild verify-gate-selection verify-packaged-sdk \
        verify-target-universe verify-csharp14-extension-abi verify-xfail-policy \
        dev dll2klib-e2e dll2klib-index-benchmark clean clean-tools clean-stdlib clean-pack help

all: pack ## one-shot: toolchain -> stdlib -> the 5 NuGet packages in build/nuget-feed

toolchain: $(KOTC) $(TOOL_DLLS) ## the compiler toolchain

# ---- individual tools ----------------------------------------------------------------------------
kotc: $(KOTC) ## the Kotlin frontend (FIR/IR -> BIR) launcher, via gradlew installDist

$(KOTC): $(KOTC_SRC)
	./gradlew -q :kotc:installDist
	@touch "$@"

# One rule per .NET tool: build/<t>-bin/<t>.dll from toolchain/<t>/** (plus a phony alias `make <t>`;
# these show up in `make help` via the static line there, not per-target ## comments).
define TOOL_RULE
$(1): build/$(1)-bin/$(1).dll
build/$(1)-bin/$(1).dll: $$(call tool_src,$(1))
	dotnet build toolchain/$(1) -c Release -o build/$(1)-bin -v q --nologo
endef
$(foreach t,$(TOOLS),$(eval $(call TOOL_RULE,$(t))))

# ---- stdlib (klib + ref + rt); see CLAUDE.md "Building the CLR stdlib" -----------------------------
stdlib: stdlib-klib stdlib-ref stdlib-rt ## the CLR stdlib: frontend KLIB + reference dll + runtime dll

stdlib-klib: $(FE_KLIB) ## kotlin-stdlib-clr-frontend.klib (kotc -classpath input)
$(FE_KLIB): $(KOTC) $(STDLIB_SRC) scripts/build-stdlib-klib.sh scripts/lib.sh
	SCRIPT_NAME=make bash -c 'source scripts/lib.sh; need_fe_klib'
	@touch "$@"

# The stdlib dlls depend on the emitter tools via their SOURCES (real change signal) plus ORDER-ONLY
# deps on the dlls (existence). Depending on the dll mtimes directly would spuriously retrigger these
# slow builds: the verify scripts' internal `dotnet build` refreshes the dlls even when nothing changed.
stdlib-ref: $(STDLIB_REF) ## DotKt.Private.Stdlib.dll (compile-time @Clr metadata; bir2cir's --ref)
$(STDLIB_REF): $(KOTC) $(STDLIB_SRC) scripts/build-stdlib-ref.sh scripts/lib.sh \
               $(call tool_src,bir2cir) $(call tool_src,ilemit) \
               | build/bir2cir-bin/bir2cir.dll build/ilemit-bin/ilemit.dll
	SCRIPT_NAME=make bash -c 'source scripts/lib.sh; need_stdlib_ref'
	@touch "$@"
	@test -f "$@" || { echo "make: stdlib-ref did not produce $@ (see build/clr-stdlib/*.err)"; exit 1; }

stdlib-rt: $(STDLIB_RT) ## DotKt.Stdlib.dll (the shipping runtime assembly)
# The script exits 0 on success / nonzero on real failure (the old final-error-grep footgun — exit 1
# exactly when the build was CLEAN — is fixed, so no compensating `|| true` here any more).
$(STDLIB_RT): $(STDLIB_REF) $(STDLIB_SRC) scripts/build-stdlib-rt.sh scripts/lib.sh \
              $(call tool_src,bir2cir) $(call tool_src,ilemit) \
              | build/bir2cir-bin/bir2cir.dll build/ilemit-bin/ilemit.dll
	SCRIPT_NAME=make bash -c 'source scripts/lib.sh; need_stdlib_rt'
	@touch "$@"
	@test -f "$@" || { echo "make: stdlib-rt did not produce $@ (see build/clr-stdlib-rt/*.err)"; exit 1; }

# ---- packaging -----------------------------------------------------------------------------------
pack: toolchain stdlib ## the 5 NuGet packages (Sdk/Sdk.Mpp/Toolchain/Stdlib/Templates) -> build/nuget-feed
	bash scripts/pack-nuget.sh

# ==================================================================================================
# Verification gates (test suite entry points live beside their tests under tests/)
# ==================================================================================================
verify: ## run ALL gates (the canonical set + the packaged-SDK release gate)
	+$(MAKE) verify-core
	+$(MAKE) verify-packaged-sdk

# The canonical gate set EXCEPT the packaged-SDK gate. CI invokes the independently-runnable shard
# aggregates below. `verify-test-corpus` deliberately keeps schema/sanity after NUnit because those gates
# inspect the fresh BIR/CIR corpus emitted by that run. `make verify` remains the complete local set.
verify-core: ## every gate except the packaged-SDK release gate
	+$(MAKE) verify-tests
	+$(MAKE) verify-schema verify-sanity
	+$(MAKE) verify-lowering
	+$(MAKE) verify-integration

# Stable developer/gate.sh alias. The leaf gates themselves are owned by the CI shard targets below;
# keep this as composition only so adding behavior cannot create a second, CI-invisible gate definition.
verify-tests: ## canonical compiler behavior gate (categorized NUnit suites + ILVerify + the negative compile lane)
	+$(MAKE) verify-nunit
	+$(MAKE) verify-compile-fail

verify-nunit: pack ## categorized NUnit suites + ILVerify; emits the fresh corpus consumed by schema/sanity
	bash tests/run-nunit-tests.sh

verify-compile-fail: toolchain stdlib ## isolated negative-compilation diagnostic lane
	bash tests/compile-fail/run.sh

# CI shard: preserve the producer -> consumer ordering even if the outer make was invoked with -j.
verify-test-corpus: verify-nunit ## NUnit/ILVerify followed by schema + sanity over its freshly-emitted BIR/CIR
	$(MAKE) verify-schema verify-sanity

verify-integration: toolchain stdlib ## independent MSBuild/target/ABI/policy gates
	+$(MAKE) verify-msbuild
	+$(MAKE) verify-target-universe
	+$(MAKE) verify-csharp14-extension-abi
	+$(MAKE) verify-xfail-policy
	+$(MAKE) verify-gate-selection

verify-schema: ## BIR/CIR schema contract enforcer (types-are-nodes + canonical k over fresh BIR/CIR); run AFTER verify-nunit
	bash tests/ir/run-schema.sh
	bash tests/ir/check-emitter-residual.sh

verify-sanity: ## offline IR-sanity gate (semantic invariants over fresh BIR/CIR); run AFTER verify-nunit
	bash tests/ir/run-sanity.sh

verify-lowering: ## lowering self-tests (synthetic BIR -> bir2cir -> CIR assertions, for rules the corpus no longer witnesses)
	bash tests/ir/run-lowering.sh

verify-msbuild: ## stateful MSBuild integration (same obj/ across source mutation)
	bash tests/msbuild/run.sh

verify-gate-selection: ## change-aware gate selector policy self-test
	bash tests/gate-selection/run.sh

verify-packaged-sdk: ## packaged nupkg-resolution + cross-module async coroutine gate
	bash tests/packaged-sdk/run.sh

verify-target-universe: ## host-vs-target metadata scope calibration
	bash tests/target-universe/run.sh

verify-csharp14-extension-abi: ## released C# 14 static extension-member metadata oracle
	bash tests/special/csharp14-static-extensions/run.sh

verify-xfail-policy: ## self-test the shared NEW/FIXED baseline verdict without building the toolchain
	bash tests/xfail/run.sh

# ==================================================================================================
# Dev conveniences
# ==================================================================================================
dev: ## compile (and run) one .kt: make dev SRC=Foo.kt [RUN=1 EXE=1 REF=x.dll NO_STDLIB=1 OUT=name DIR=dir]
	@test -n "$(SRC)" || { echo "usage: make dev SRC=path/to/Foo.kt [RUN=1 EXE=1 REF=x.dll NO_STDLIB=1 OUT=name DIR=dir]"; exit 2; }
	bash scripts/dotkt.sh $(if $(RUN),--run) $(if $(EXE),--exe) $(if $(REF),--ref "$(REF)") \
		$(if $(NO_STDLIB),--no-stdlib) \
		$(if $(OUT),-o "$(OUT)") $(if $(DIR),-d "$(DIR)") $(SRC)

dll2klib-e2e: ## CLR reference DLL -> standard metadata-only KLIB end-to-end regression
	bash tests/special/dll2klib-e2e/run.sh

dll2klib-index-benchmark: ## repeatable synthetic benchmark for dll2klib local TypeDef lookup
	bash tests/special/dll2klib-local-index-benchmark/run.sh

# ==================================================================================================
# Cleaning
# ==================================================================================================
clean: clean-tools clean-stdlib clean-pack ## everything below

clean-tools: ## the built tools (kotc install + build/<tool>-bin)
	rm -rf $(foreach t,$(TOOLS),build/$(t)-bin) toolchain/kotc/build/install

clean-stdlib: ## the built stdlib artifacts (klib + ref + rt)
	rm -rf build/clr-stdlib build/clr-stdlib-rt build/clr-stdlib-frontend-klib

clean-pack: ## the NuGet feed + the assembled package staging dirs
	rm -rf $(FEED) packaging/DotKt.Toolchain/tools packaging/DotKt.Stdlib/lib \
	       packaging/*/bin packaging/*/obj

# ==================================================================================================
help: ## this help
	@echo "kotlin/clr build interface (thin wrapper over scripts/ — see CLAUDE.md for the pipeline)"
	@echo
	@grep -hE '^[a-zA-Z0-9_-]+:.*## ' $(MAKEFILE_LIST) | \
		awk -F':.*## ' '{ printf "  \033[1m%-22s\033[0m %s\n", $$1, $$2 }'
	@printf "  \033[1m%-22s\033[0m %s\n" "ilemit|bir2cir|dll2klib" "build one .NET tool -> build/<tool>-bin"
	@echo
	@echo "Common flows:  make all   ·   make -j toolchain   ·   make stdlib   ·   make verify-tests"
	@echo "               make dev SRC=path/to/Foo.kt RUN=1"
