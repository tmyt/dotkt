# #72 stdlibCompile-branch removal — plan (Fable audit 2026-07-10)

Post-klib(#80)+#76: 6 live `stdlibCompile` readers in kotc. Goal: DELETE the `DOTKT_STDLIB_COMPILE` env var + `stdlibCompile` field; genuine core re-keys off `-Xstdlib-compilation` (arguments.stdlibCompilation) which all build-stdlib scripts already pass.

## Classification (BirEmitter.kt unless noted)
- #5 accAttrs :948 `if(stdlibCompile) attrs else ""` — REMOVABLE → always-emit (attrsJson doctrine :971-977; plain methods :1331 + iface accessors :531 already unconditional; ref.dll needs @ClrIntrinsic on accessors; rt strips via ilemit --build-stdlib=runtime). Load-bearing app-diff (annotated-property accessors gain attrs) — bless via gate.
- #7 isStdlibCollectionIterable :4045 (use :1706) — MOVE-TO-bir2cir ForInLowering.cs. stdlib routes for(x in kotlin.collections.*/Sequence)→forEachInline(GetEnumerator); app keeps iterator. FQN+supertype-walk = CLR-rep decision. HIGHEST RISK: without compensator rt.dll coll ops emit iterator()/hasNext → EntryPointNotFound. forIn carries srcType+fallback for exactly this. bir2cir ForInLowering already has stdlibBuild param (Program.cs:191).
- #6 downTo :1697 `!stdlibCompile && name=="downTo"` — MOVE-TO-bir2cir ForInLowering.cs. app direct-lowers for(i in a downTo b)→counted for. 3 strikes: (a) name-only match, no FQN → user infix downTo miscompiled; (b) ilemit `for` re-evals `to` each iter (the hazard :1694 cites for rangeTo/until); (c) forIn+fallback + RangeConstructionLowering FQN pattern already exist. Fix = FQN-keyed (kotlin.ranges.downTo / srcType IntProgression) counted for w/ temp bounds.
- #4 skipStdlibHighArityFunctionType :584 (uses 406/539/672/700/736/1113/1118) — MOVE-TO-bir2cir. stdlib silently drops >16-param-function-type decls (6 context() overloads arity 17-22). Func/Action 16-cap = CLR-rep fact (bir2cir Program.cs:1583). New bir2cir decl-filter keyed off BuildStdlibMode!=App: scan Fn arity, drop+warn in stdlib, hard-error in app.
- #3 unsupported() :131 — GENUINE(diagnostics) but shrinkable: stdlib=throwing stub+warn, app=hadError. 2 live stub sites (::indentWidth ::isNotBlank .NET method refs). Fix those 2 stdlib sites (wrap in lambdas / impl .NET-method fn-refs) → uniform hard-error → delete branch.
- #1 ClrCliPipeline.kt:148 frontend select (ClrStdlibFrontendPipelinePhase vs ClrAppFrontendPipelinePhase) — GENUINE(build-mode). Re-key off arguments.stdlibCompilation. Irreducible (stdlib self-build needs fragment-actualized source frontend; klib carries no bodies).
- #2 field :120 — dies with last reader; interim = ctor param from arguments.stdlibCompilation via ClrBackendPhase.kt:45.

## DOTKT_STDLIB_COMPILE deletable. Genuine core (1,3) = the -Xstdlib-compilation distinction, NOT bir2cir --build-stdlib (kotc runs ONCE producing shared BIR before ref/rt split; kotc axis=stdlib-vs-app, bir2cir=metadata-vs-rt-vs-app). Step 0: confirm stdlibCompilation on K2MetadataCompilerArguments base (accepted today — every green stdlib build passes it).

## Removal sequence (gate each):
1. #5 accAttrs: drop gate, always-emit. stdlib BIR byte-identical; app diff on annotated props — bless verify-il+differential.
2. #7: land ForInLowering stdlib forIn→forEachInline FIRST; prove stdlib CIR identical; then delete :4045+:1706.
3. #6: land FQN-keyed downTo in ForInLowering (app) counted-for w/ temp bounds; prove app CIR equiv; delete :1697-1701 (fixes miscompile+re-eval).
4. #4: land bir2cir stdlib-mode Fn-arity decl filter (same warn text); prove same 6 drop; delete :584 + 7 filterNot sites.
5. #3: fix 2 .NET-method-ref stub sites in stdlib source; stub count→0; delete :131 (uniform hard-error). If fn-ref stalls: interim re-key :131 off arguments.stdlibCompilation.
6. #1+#2: ClrCliPipeline:148→arguments.stdlibCompilation; delete BirEmitter:120; strip DOTKT_STDLIB_COMPILE=1 from build-stdlib{,-ref,-rt}.sh + verify-il:86 comment. Grep-invariant: 0 DOTKT_STDLIB_COMPILE outside CHANGELOG/docs.

Load-bearing order #7>#4>#6>#5; pure policy/wiring #3,#1. bir2cir/ilemit already retired their env reads (#66); kotc is the last holdout.
