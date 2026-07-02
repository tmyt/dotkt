> **HISTORICAL — superseded by `docs/master-task-inventory.md` 【1】.** Its core findings ("bir2cir is a skeleton",
> "@ClrIntrinsic substitution ZERO") were closed by `MemberCallSubstitution` + bundles 1–5 (2026-07-03). Kept for
> the gap-analysis method/rationale only.

# ギャップ分析: 設計 vs 現状コード (2026-06-30)

正 = [design-compiler-modes.md](../design-compiler-modes.md) + [ship-tasks.md](../ship-tasks.md) §0。5ステージ（facadegen/kotc/bir2cir/ilemit/属性·stdlib）を担当エージェントで調査した統合結果。各ギャップは file:line 付き。

## 0. 結論 — 単一の根本ギャップ

**Kotlin↔CLR の関係（substitution / lowering / 出力モード）が層を間違えて実装されている。**

- **bir2cir（本来の置き場）は実質スケルトン**。`--compat-bir`(既定)/`--native-cir` の2 CLI モードだけで、設計の ref/rt/app 3出力モードも、6フェーズ（phase1 load 以外）も、@ClrIntrinsic 置換も**無い**。しかも **stdlib ビルドは bir2cir を mode 無し・`--ref` 無しで呼ぶ＝no-op pass-through**（`scripts/build-clr-stdlib.sh:43`, `build-clr-stdlib-runtime.sh:36`）。bir2cir は本物のビルド経路に**実質関与していない**。
- **kotc の `BirEmitter` が実質フル Kotlin→CLR バックエンド**（`BirMappings.kt` 140行 + `netType`/`birType`/`clrName`/`appColl` + ~40 lowering サイト）。ref/rt/app の出力モードも kotc が `DOTKT_STDLIB_*` env で持つ。
- **ilemit に Kotlin-stdlib-op lowering + inline splice + coroutine ABI が残存**（compat-bir 経路で live）。

∴ **「4層分離」は設計上の理想で、現状の実パイプラインは旧3段（kotc-backend=CLR-backend → bir2cir=passthrough → ilemit）**。ship-tasks §3/§6/§7 の核 = この移設、が丸ごと未着手。

## 1. ステージ別ギャップ要約

| ステージ | 設計適合 | 主要ギャップ（file:line） |
|---|---|---|
| **facadegen** | 大半 MATCHES | **REQ7 CONTRADICTS**: `@ClrIntrinsic`/`@Clr` を読み meta に BCL bind（`=`/`clr:`）を埋込→kotc が消費（`Program.cs:225-229,255,282,293`）。`@ClrTypeAlias` 未対応・read-back 属性消費なし。`.kt` 経路が legacy `package clr`/`@Clr` 生成（`:51-58`） |
| **kotc** | **CONTRADICTS（核）** | `BirEmitter` がフル CLR backend。`BirMappings.kt` 全体 + `netType`(`:4297`)/`birType`(`:4460`)/`clrName`(`:4247`)/`appColl`(`:4228`) + ~40 lowering。@ClrIntrinsic を `clrName→clrStatic`(`:3183`) で解決（素通しでない）。**JVM `kotlin-stdlib.jar` 使用→java.* leak**（`appColl`/`NET_EXCEPTIONS`/`SEQUENCED_COLLECTION_LEAK` はこのため存在） |
| **bir2cir** | **MISSING（核）** | 3出力モード無し（2 CLI モードのみ）。@ClrIntrinsic/@ClrTypeAlias 置換 **ZERO**（`ReferenceMetadataIndex` は ref.dll を読み `isNaN→kotlin.NumbersKt.isNaN` まで解決するが intrinsic を適用しない）。primitive/inline lowering 不在。**実ビルド経路で no-op pass-through** |
| **ilemit** | PARTIAL | 既定 suspend SM は strategy B で clean（`Coroutines.cs:15-229`、入力は CPS 線形化済）✓。だが **legacy Kotlin-stdlib op lowering**（groupBy/associate/linq*/split/listNew/console/objEq/concat、`Expressions.cs:245-1037`）+ **inline splice**（`EmitInlineSplice` `Program.cs:2121`）+ **coroutine class/seq の hardcode `kotlin.coroutines.*`/`kotlin.sequences.*`**（`Program.cs:143-151`）。**良い境界**: @ClrIntrinsic 非消費・java/math/netType 無し・unsigned は CIR 由来 |
| **属性·stdlib** | 2/4 新規必要 | `ClrTypeAlias` **未存在**（型 subst は今 class-level `@ClrIntrinsic`、`Comparable.kt:20`）。`ClrRefArguments(mask)` **未存在**（atomics は Monitor 迂回 `Atomics.kt:24`）。`byref`/`ClrRef` は**既に `kotlin.clr.*`＝MATCHES**。legacy `clr.Clr` dual-match 残（3サイト + facadegen + `clr/Clr.kt`） |

## 2. マスター移行（全ギャップの根） — kotc+ilemit → bir2cir

`docs/bir2cir-migration-inventory.md` の 6-wave がこれ。具体的には:
1. **出力モードを bir2cir へ**: `DOTKT_STDLIB_COMPILE/SUBSTITUTE/STRIP_METADATA` 分岐（kotc `BirEmitter.kt:109,113,117`、ilemit `Program.cs:103,522`）を bir2cir の ref/rt/app モードへ。
2. **@ClrIntrinsic/@ClrTypeAlias 置換を bir2cir へ**: `ReadDotKtMetadata`(`bir2cir/Program.cs:445`) に属性捕捉を追加 → `ExecutableCirDraft`(`:1094`) で解決済み候補を BCL へ向け直す。kotc `clrName`(`:4247`)/`:3183` を削除。
3. **primitive lowering を bir2cir phase2 へ**: `BirMappings.kt` の `VALUE_PRIM_BIR`/`PRIMITIVE_ARRAY_ELEM` + `netType`/`birType` の primitive 部。
4. **inline lowering を bir2cir phase3 へ**: kotc 同一モジュール splice(`BirEmitter.kt:2579`) + ilemit cross-module splice(`Program.cs:2121`) → bir2cir が `KotlinInlineAttribute` の BIR を読んで展開。
5. **ilemit の legacy Kotlin-stdlib op lowering を retire**: stdlib `@ClrIntrinsic` 化 + bir2cir 消費（`Expressions.cs:245-1037`）。
6. **bir2cir を実ビルド経路に乗せる**: ビルドスクリプトが bir2cir に mode + `--ref ref.dll` を渡すよう変更。

## 3. 独立ギャップ（マスター移行に依存しない）

- **`@ClrTypeAlias` 新設**: `kotlin.clr.ClrTypeAlias(name)` `@Target(CLASS)` を作り、class-level `@ClrIntrinsic`（`Comparable.kt:20` 等）を移行、matcher を fork（class=ClrTypeAlias / member=ClrIntrinsic）。`@ClrIntrinsic` の `@Target` を FUNCTION/PROPERTY に絞る。**工数: 小（注釈）+ stdlib 機械的移行**。
- **`@ClrRefArguments(mask)` 新設**: bitmask（bit 位置=引数位置）。substitution 段で `@ClrIntrinsic` と併用時に該当引数を managed pointer 化。atomics CAS を `Interlocked` へ。**工数: 中**。⚠️ **`docs/clr-stdlib-intrinsic-audit.md` が単数 `@ClrRefArgument(index)` と書いており canonical の bitmask と食い違う→doc 修正要**。
- **frontend jar 切替**: JVM `kotlin-stdlib.jar`（全配線）→ `kotlin-clr-stdlib.jar`（CLR向け）。生成スクリプト `scripts/build-clr-stdlib-frontend.sh` は**未追跡・未配線**。切替で `appColl`/`NET_EXCEPTIONS`/`SEQUENCED_COLLECTION_LEAK` の java.* 処理を削除可。**工数: 中**。
- **legacy `clr.Clr` 撤去**: dual-match 3サイト（`BirEmitter.kt:1203,2141,4260`）+ facadegen（`Program.cs:54,227`）+ `runtime/stdlib/clr/clr/Clr.kt` 削除。**工数: 小**。
- **coroutine class/sequence ABI**: hardcode `kotlin.coroutines.*`/`kotlin.sequences.*`/`kotlin.Result`（ilemit `Program.cs:143-151`, `Coroutines.cs:313-671`）。コルーチン runtime の stdlib 移植に依存（[coroutine-stdlib-port-plan.md](../coroutine-stdlib-port-plan.md)）。

## 4. 既達 / MATCHES（再実装しない）

- **`byref`/`ClrRef<T>`** = 既に `kotlin.clr.*`（kotc 合成、`ClrTypeInjection.kt:311,315,331`）→ **ship-tasks §6「byref/ClrRef を root→kotlin.clr.* 移動」は stale／既達**（※ `stackBuffer`/`Span` は root 残存だが別件）。
- **LINQ/`COLLECTION_OPS` lowering** = 既に dead code（kotc `BirMappings.kt:70,74` に live caller なし）。
- **既定 suspend SM** = strategy B、入力は線形化済 CPS（clean CIR）、ilemit で生成（場所は正しい）。
- **facadegen**: primitive map(`:1113`)/Roundtrip 復元/ref-out→ClrRef<T>(`:1078`)/embedded 属性読込 = MATCHES。
- **ilemit の良い境界**: @ClrIntrinsic 非消費、java/math/netType マップ無し、unsigned は CIR spec 由来、`forRange` は node 名解決（hardcode 無し）= 漏れ修正の手本。

## 5. 推奨着手順

1. **最低リスクの実証**: bir2cir で @ClrIntrinsic を ref.dll から消費 + kotc `:3183` seed 削除 → **expect/actual `isNaN` 系が通る**（ship-tasks §3「今すぐの着手点」）。移行パターンの実証。
2. **マスター移行（§2）を wave で**: §2 の 1→6。最大の塊。
3. **並行独立（§3）**: `@ClrTypeAlias` 新設 / `@ClrRefArguments` 新設 / frontend jar 切替（java leak 解消）/ legacy `clr.Clr` 撤去。
4. **Milestone 0**: bir2cir 既定を native-cir へ・compat-bir 削除・ilemit の legacy BIR dialect 削除。

## 6. 本分析で判明した doc/task の要修正

- `docs/clr-stdlib-intrinsic-audit.md:26,32` の `@ClrRefArgument(index)`（単数・index）→ canonical の `@ClrRefArguments(mask)`（bitmask）に修正。
- `ship-tasks.md §6` の「byref/ClrRef を root→kotlin.clr.* 移動」はチェック可（既達）。
