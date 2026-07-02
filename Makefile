# kotlin/clr — unified build interface.
#
# A THIN ORCHESTRATOR over the canonical scripts (scripts/*.sh stay the single source of truth for
# each stage's env gates / flags; this file only sequences them and adds incremental file targets).
# The artifact DAG:
#
#   kotc ──┬────────────────────────────────► stdlib-jar (frontend jar, kotc -classpath)
#          ├─ ilemit/bir2cir/facadegen/retarget
#          └────────────► stdlib-ref ──► stdlib-rt ──► pack (4 NuGet packages -> build/nuget-feed)
#
# Output paths are LOAD-BEARING (dotkt.sh, verify-*.sh, cases/KotlinClr.targets hard-reference
# build/<tool>-bin, build/clr-stdlib*/dll, build/clr-stdlib-frontend-jvm) — do not rename them here.
#
#   make help          # this listing (the default goal)
#   make all           # one-shot: toolchain -> stdlib -> pack
#   make -j toolchain  # the independent tools build in parallel
#   make dev SRC=Foo.kt RUN=1

SHELL := /bin/bash
.DEFAULT_GOAL := help

# ---- artifacts -----------------------------------------------------------------------------------
KOTC       := toolchain/kotc/build/install/kotc/bin/kotc
TOOLS      := ilemit bir2cir facadegen retarget
TOOL_DLLS  := $(foreach t,$(TOOLS),build/$(t)-bin/$(t).dll)
FE_JAR     := build/clr-stdlib-frontend-jvm/kotlin-stdlib-clr-frontend.jar
STDLIB_REF := build/clr-stdlib/dll/DotKt.Private.Stdlib.dll
STDLIB_RT  := build/clr-stdlib-rt/dll/DotKt.Stdlib.dll
FEED       := build/nuget-feed

# ---- source sets (prerequisites for incrementality) ----------------------------------------------
KOTC_SRC   := $(shell find toolchain/kotc/src -type f 2>/dev/null) toolchain/kotc/build.gradle.kts settings.gradle.kts
STDLIB_SRC := $(shell find runtime/stdlib -name '*.kt' 2>/dev/null)
tool_src    = $(shell find toolchain/$(1) -name '*.cs' -o -name '*.csproj' 2>/dev/null | grep -vE '/(obj|bin)/')

# ==================================================================================================
# Aggregate targets
# ==================================================================================================
.PHONY: all toolchain kotc $(TOOLS) stdlib stdlib-jar stdlib-ref stdlib-rt pack \
        verify verify-il verify-ktproj verify-roundtrip verify-differential verify-widedelegates \
        dev facades clean clean-tools clean-stdlib clean-pack help

all: pack ## one-shot: toolchain -> stdlib -> the 4 NuGet packages in build/nuget-feed

toolchain: $(KOTC) $(TOOL_DLLS) ## the compiler toolchain: kotc + ilemit + bir2cir + facadegen + retarget

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

# ---- stdlib (jar + ref + rt); see CLAUDE.md "Building the CLR stdlib" -----------------------------
stdlib: stdlib-jar stdlib-ref stdlib-rt ## the CLR stdlib: frontend jar + reference dll + runtime dll

stdlib-jar: $(FE_JAR) ## kotlin-stdlib-clr-frontend.jar (kotc -classpath input)
$(FE_JAR): $(KOTC) $(STDLIB_SRC) scripts/build-clr-stdlib-frontend.sh
	bash scripts/build-clr-stdlib-frontend.sh

stdlib-ref: $(STDLIB_REF) ## DotKt.Private.Stdlib.dll (compile-time @Clr metadata; bir2cir's --ref)
$(STDLIB_REF): $(KOTC) build/bir2cir-bin/bir2cir.dll build/ilemit-bin/ilemit.dll build/retarget-bin/retarget.dll \
               $(STDLIB_SRC) scripts/build-clr-stdlib.sh
	bash scripts/build-clr-stdlib.sh --emit
	@test -f "$@" || { echo "make: stdlib-ref did not produce $@ (see build/clr-stdlib/*.err)"; exit 1; }

stdlib-rt: $(STDLIB_RT) ## DotKt.Stdlib.dll (the shipping runtime assembly)
# NOTE the `|| true`: the script's final error-grep exits 1 precisely when it finds NO errors
# (a clean build); existence of the dll below is the real success signal.
$(STDLIB_RT): $(STDLIB_REF) $(STDLIB_SRC) scripts/build-clr-stdlib-runtime.sh
	bash scripts/build-clr-stdlib-runtime.sh --emit || true
	@test -f "$@" || { echo "make: stdlib-rt did not produce $@ (see build/clr-stdlib-rt/*.err)"; exit 1; }

# ---- packaging -----------------------------------------------------------------------------------
pack: toolchain stdlib ## the 4 NuGet packages (Sdk/Toolchain/Stdlib/Templates) -> build/nuget-feed
	bash scripts/pack-dotkt.sh

# ==================================================================================================
# Verification gates (the scripts are called VERBATIM; behavior is identical to invoking them)
# ==================================================================================================
verify: verify-il verify-ktproj verify-roundtrip verify-differential verify-widedelegates ## run ALL gates

verify-il: ## the canonical IL gate (compile -> IL -> run -> assert -> ilverify)
	bash scripts/verify-il.sh

verify-ktproj: ## MSBuild / .ktproj end-to-end
	bash scripts/verify-ktproj.sh

verify-roundtrip: ## Kotlin<->CLR round-trip (consume a DotKt dll as Kotlin)
	bash scripts/verify-roundtrip.sh

verify-differential: ## direct-IL differential vs the C# oracle
	bash scripts/verify-differential.sh

verify-widedelegates: ## >16-arg function types (KFunc/KAction synthesis)
	bash scripts/verify-ilemit-wide-delegates.sh

# ==================================================================================================
# Dev conveniences
# ==================================================================================================
dev: ## compile (and run) one .kt: make dev SRC=Foo.kt [RUN=1 EXE=1 REF=x.dll NO_STDLIB=1 RETARGET=1 OUT=name DIR=dir]
	@test -n "$(SRC)" || { echo "usage: make dev SRC=path/to/Foo.kt [RUN=1 EXE=1 REF=x.dll NO_STDLIB=1 RETARGET=1 OUT=name DIR=dir]"; exit 2; }
	bash scripts/dotkt.sh $(if $(RUN),--run) $(if $(EXE),--exe) $(if $(REF),--ref "$(REF)") \
		$(if $(NO_STDLIB),--no-stdlib) $(if $(RETARGET),--retarget) \
		$(if $(OUT),-o "$(OUT)") $(if $(DIR),-d "$(DIR)") $(SRC)

facades: ## generate @Clr Kotlin façades: make facades OUT=outDir TYPES="System.Text.StringBuilder ..."
	@test -n "$(OUT)" && test -n "$(TYPES)" || { echo 'usage: make facades OUT=outDir TYPES="Full.Type.Name ..."'; exit 2; }
	bash scripts/gen-facades.sh "$(OUT)" $(TYPES)

# ==================================================================================================
# Cleaning
# ==================================================================================================
clean: clean-tools clean-stdlib clean-pack ## everything below

clean-tools: ## the built tools (kotc install + build/<tool>-bin)
	rm -rf $(foreach t,$(TOOLS),build/$(t)-bin) toolchain/kotc/build/install

clean-stdlib: ## the built stdlib artifacts (jar + ref + rt)
	rm -rf build/clr-stdlib build/clr-stdlib-rt build/clr-stdlib-frontend-jvm

clean-pack: ## the NuGet feed + the assembled package staging dirs
	rm -rf $(FEED) packaging/DotKt.Toolchain/tools packaging/DotKt.Stdlib/lib \
	       packaging/*/bin packaging/*/obj

# ==================================================================================================
help: ## this help
	@echo "kotlin/clr build interface (thin wrapper over scripts/ — see CLAUDE.md for the pipeline)"
	@echo
	@grep -hE '^[a-zA-Z0-9_-]+:.*## ' $(MAKEFILE_LIST) | \
		awk -F':.*## ' '{ printf "  \033[1m%-22s\033[0m %s\n", $$1, $$2 }'
	@printf "  \033[1m%-22s\033[0m %s\n" "ilemit|bir2cir|facadegen|retarget" "build one .NET tool -> build/<tool>-bin"
	@echo
	@echo "Common flows:  make all   ·   make -j toolchain   ·   make stdlib   ·   make verify-il"
	@echo "               make dev SRC=cases/il/M0.kt RUN=1"
