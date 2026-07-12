# kotlin/clr — 残タスク計画書 ＝ Kotlin.NET 1.0 出荷チェックリスト

> **RECONCILE 2026-07-05:** all gates are XFAIL-ZERO (verify-il 209/0, differential ALL MATCH, ktproj 9/9); coroutine bundle-6, the A2 interop-no-registry keystone (4 registries deleted), the Polish layer-purity, and the 2026-07-05 final-review findings (N1-N8, F1/F2) are all DONE. Any item below marked open/TODO that concerns those is STALE. Genuine residuals: roundtrip-memext2 (with{}-scope suspend), interface events, and the LOW hardening items in the session task list.


> **状態 (2026-07-03 見直し)**: 広域 1.0 チェックリスト。完了済みトラック A/B/C/E は historical 集約（下記の各トラック先頭ポインタ）、live な残タスクは **D coroutine（= master-task-inventory 【6】）と F production ツーリング（= inventory 【7】）のみ**。現行アーキテクチャの正は [docs/ship-tasks.md](ship-tasks.md) §0、日次のタスク台帳は [docs/master-task-inventory.md](master-task-inventory.md)。
>
> **現行アーキテクチャ（4 層パイプライン）**: facadegen / kotc / bir2cir / ilemit（単一経路 — `--compat-bir`/`--native-cir` の二重化は 2026-06-30 撤去）。C# バックエンドは完全引退し `scripts/verify-all.sh` は削除済み（オラクル＝JVM 差分ハーネス）。`runtime/csharp/` ツリーと `clrgen` 合成パッケージは撤去（`import System.X` がそのまま解決）。リポジトリ再編済み（compiler/→toolchain/kotc、tools/→toolchain/、samples/→cases/）。`@Clr`/`clr.Clr` は `kotlin.clr.ClrIntrinsic` に改称。

これは「純 .NET Binding として縦は貫通済み」状態からの**残タスク網羅リスト**。
**本書のチェックボックスを全て埋めた時点を Kotlin.NET 1.0 のリリース可能ライン（definition of done）とする。**
完了済みの大枠（M0 / M-D1 IL / M-D2 coroutine CPS / M-S S1–S5 / interop I2–I4 / framework-direct 継承 W0–W1）は
`docs/archive/research-roadmap.md`（historical）を参照。本書は**まだ無いもの**を漏れなく列挙し、チェックボックスで進捗を追う。

## 1.0 の出荷ゲート（全カテゴリ完了に加え、横断で必須）
- [ ] **任意の実用 Kotlin プログラム**が kotlin/clr でコンパイル・実行できる（A 言語網羅 ＋ B stdlib）。
- [ ] **双方向 interop**: Kotlin→.NET だけでなく **.NET→Kotlin**（生成アセンブリを C# 等から自然に消費）も成立（C-0）。
- [ ] **唯一の出荷バックエンドが直接 IL**（C# コード生成を廃止＝下記 Track E）。
- [ ] **JVM 差分ハーネス**が corpus 全体で緑（kotlin/jvm 出力 == kotlin/clr 出力）＋生成 IL が全件 `ilverify` clean。
- [ ] **配布可能**（`dotnet new ktproj` / NuGet・SDK 化 / バージョン付きリリース）＋**ライセンス/帰属クリア**（F）。
- [ ] 想定外入力は明示エラー、診断はソース位置付き。純バインディング原則を維持。

> **スコープ確定（ユーザ判断 2026-06-16）**: 縮小はしない。**本書の全項目を 1.0 必須**とする（構造化並行性・増分コンパイル・性能・VS-LSP も含め全部）。

## 運用原則（各タスク共通の Done 条件）
- end-to-end で動く（frontend だけ通っても不可。**生成コードが `dotnet run` で期待出力**＝移行期は C#→csc、1.0 ゴールは IL 単独）。
- サンプルを追加し緑にする（IL 経路＝`scripts/verify-il.sh`、MSBuild/.ktproj 統合＝`scripts/verify-ktproj.sh`）。※旧 C# バックエンドのスイート `verify-all.sh` は引退済みバックエンドのテストに意味がないため削除。
- 想定外入力は**明示エラー**（silent miscompile 禁止）。— [[no-half-baked-public-state]]
- コアは**純バインディング**を保つ（UI 等のライブラリを同梱しない）。— [[kotlin-net-is-pure-binding]]
- サイズ目安: S=数時間 / M=1–2日 / L=数日 / XL=1週間超。

---

# A. Kotlin 言語 breadth（codegen）

> **DONE（historical, 2026-06-30 見直し）** — Kotlin 言語 breadth（拡張関数 / when 式 / 配列 / コレクション操作 / スコープ関数 / デフォルト引数 / ユーザ定義ジェネリクス / コルーチン構文 等）は実装済み。IR ノード単位の網羅チェックリストは [docs/bir-coverage.md](bir-coverage.md)、言語カバレッジの現況と現行アーキテクチャの正は [docs/ship-tasks.md](ship-tasks.md) §0。詳細な実装ログは git 履歴を参照。

---

# B. CLR 版 Kotlin stdlib（最大の山）

> **DONE（historical, 2026-06-30 見直し）** — CLR 版 stdlib 写像（コレクション / 文字列・文字 / Math / Pair-Triple-Result / 例外ヘルパ / Sequence 等）は実装済み。現行アーキテクチャは stdlib を pure `kotlin.*` CLR アセンブリとして出荷し `@ClrIntrinsic`（旧 `@Clr`）を bir2cir（BIR→CIR）で消費する形。正は [docs/ship-tasks.md](ship-tasks.md) §0。

---

# C. interop 完全化（残り）

> **DONE（historical, 2026-06-30 見直し）** — 双方向 interop（forward: Kotlin→.NET の import 駆動解決＝`import System.X`、reverse: .NET→Kotlin のコンパイル時 `<Reference>`／`retarget` ツール）と .NET 機能消費（基底継承 / event `+=`/`-=` / generic / 値型 / 拡張メソッド / enum 等）は実装済み。reverse の retarget 設計記録は [docs/csharp-retirement-design.md](csharp-retirement-design.md)、現行アーキテクチャの正は [docs/ship-tasks.md](ship-tasks.md) §0。

---

# D. coroutine 完全意味論

> **⚠️ 現況補正（2026-07-03）**: 下記 2026-06-23 の「全面実装済み」は **pre-stdlib 時代（合成 facade + 手書き
> DotKt.Runtime ランタイム）の記録**。実 CLR stdlib への移行でその stopgap 経路と `il-k*` サンプル群は撤去され、
> コルーチンは **master-task-inventory 【6】として意図的に deferred**（ABI は確定済み — `suspend`⇔`Task<T>`、hot
> start。`docs/dotkt-semantics.md` §4）。**live な実装計画は [docs/coroutine-stdlib-port-plan.md](coroutine-stdlib-port-plan.md)**
> （coroutine ランタイムを stdlib `clr/` actuals として移植）。下記の個別 ✅ は当時の実証記録として読むこと。
>
> **状態（2026-06-23、historical）: コルーチン表面はコンパイラ機能として全面実装済み**（design-coroutines-clr.md §§13a–§14a / task #55 dotktx 基盤）。
> 単発 suspend・spilling・条件式内 suspend・try-catch/try-finally-around-await・suspend lambda（receiver 形含む）・
> generic/Unit/extension suspend・raw intrinsics・resume・startCoroutine・suspendCancellableCoroutine・unified Result・
> user `Continuation<T>` 実装・`Unit` 型引数・sequence/yieldAll/generateSequence・**Flow（generic 含む）+ Flow⇄
> IAsyncEnumerable・Channel・select・CoroutineContext 代数+coroutineContext・ContinuationInterceptor/intercepted+
> dispatcher** をすべて standalone（合成 facade）で実証済（各 `il-k*` サンプル、緑・ilverify-clean）。
> **下記の D 残項目（CancellationToken・構造化並行・Dispatchers）の「本物のライブラリ形」は Track 2**＝実 `kotlinx-coroutines-core`
> を DotKt でコンパイルする段階で揃う（現状の手書き stopgap はそこで置換）。

- [x] 部分式内サスペンドの spilling（`f(g().await())`）。**(L)** ✅ 2026-06-20 — `spillExpr`（BirEmitter）が式中の各サスペンド呼出を**評価順（post-order）**で fresh な状態機械フィールド＋`coSuspend` ステップに hoist し、残余式を `expr()`（`coSpill` を参照）で再レンダ＝サスペンドフリー化。`a.await() + b.await()`（第1結果が第2サスペンドを跨いで生存＝両方フィールド）・val 初期化子・非 suspend 関数への await 引数を解禁。`val x = …`/`return …`/`x = …`/呼出文の4位置を配線。`cases/il-coro`（`spillSum=30`/`spillNested=17`/`spillArg=16`）実機正＋ilverify-clean。残: **条件式**内 suspend（下記）。
- [x] ループ/分岐の**条件式**内サスペンド。**(M)** ✅ 2026-06-20 — `emitWhileCps` は条件式の await を **START ラベル直後**に spill（後退辺 `coGoto start` で毎反復 re-suspend＝ループ body サスペンドと同型）、`emitWhenCps` は各 branch 条件を test 直前に spill。`spillExpr` 再利用でゼロ新規 ilemit。`cases/il-coro`（`loopCond=3`＝while 条件 await、`condBranch=6`＝if 条件 await＋branch await）実機正＋ilverify-clean。
- [ ] `CancellationToken` を ABI に。**(S)**
- [x] `Flow` ⇄ `IAsyncEnumerable` ✅ 2026-06-23（`il-kasflow`：`asFlow`/`asAsyncEnumerable` 橋＝GFlows.FromAsync/ToAsync）。**(L)**
- [~] 構造化並行性（`Job`/`CoroutineScope`/`launch`/`async`）。**(XL)** — async/await/runBlocking はコンパイラ機能として実装済（`il-kstruct`）。本物の Job/Scope/cancel = Track 2（kotlinx をコンパイル）。
- [~] `Dispatchers`（Default→ThreadPool / Main→SynchronizationContext）。**(L)** — `ContinuationInterceptor`/`intercepted` の継ぎ目＋合成 dispatcher は実装済（`il-kintercept`、T3c）。本物の Dispatchers.* は Track 2 の actual セット（同じ継ぎ目に差す）。

---

# E. IL バックエンド parity ＆ C# コード生成の廃止（1.0 の中核ゴール）

> **DONE（historical, 2026-06-30 見直し）** — IL バックエンド parity と C# コード生成の廃止は完了（2026-06-18、E-0〜E-5）。C# バックエンドは完全引退（出荷経路から csc/C# 依存ゼロ）、オラクルは JVM 差分ハーネス（`verify-il` / `verify-differential` / `verify-ktproj`、`verify-all.sh` は削除済み）。reverse コンパイル時 `<Reference>`（R-1 retarget ツール）も 2026-06-23 実装済み。設計・各フェーズの記録は [docs/csharp-retirement-design.md](csharp-retirement-design.md)、IR ノード網羅は [docs/bir-coverage.md](bir-coverage.md)、現行 4 層パイプライン（facadegen/kotc/bir2cir/ilemit）の正は [docs/ship-tasks.md](ship-tasks.md) §0。

---

# F. production ツーリング / 信頼性

- [x] **差分テストハーネス（JVM oracle）— 達成・実機 ✅**: `scripts/verify-differential.sh` が pure-Kotlin サンプル（16件）を kotlin/jvm（正解器）と kotlin/clr で実行し stdout 一致を検証。**ALL MATCH**。codegen＋stdlib 写像が実 Kotlin 意味論と一致することを実証。
  - **設計判断（ユーザ確定 2026-06-17）— primitive 文字列化は CLR ネイティブ**: Kotlin.NET プログラムは .NET プログラムなので、Boolean→`True`/`False`、Double→`4` 等、**ホスト（CLR）の慣習に揃える**（相互運用・逆方向 interop に一貫）。Kotlin の `true`/`4.0` は JVM/JS の慣習継承で言語の本質ではない、との判断。差分ハーネスは表記差（bool 大小・`.0`）を正規化してロジック一致を検証。
  - 残: corpus 拡張（pure サンプルを増やす）、CI 化。
- [ ] **診断品質**: 未対応構文・interop エラーをソース位置付き・読めるメッセージで。`-Xverify-ir` 相当の健全性ゲート常設。**(M)**
- [ ] 境界の null 正当性（プラットフォーム型 `T!` の扱い定義）。**(M)**
- [ ] 増分コンパイル。**(L)**
- [ ] 性能（コンパイル時間・生成コード）。**(L)**
- [~] 配布（基盤あり）: `dotnet new ktproj` テンプレート（`packaging/DotKt.Templates/` 存在）・MSBuild SDK / NuGet 化（`scripts/pack-nuget.sh`＝DotKt.Sdk/Toolchain/Runtime/Templates をパック）は実装済。残: 相対パス依存の排除・self-contained コンパイラ・1.0 versioned release（現状 0.9.0 pre-1.0）。**(M–L)**
- [ ] VS / VS Code 体験（ビルド/実行統合。フル LSP は別スコープ）。**(M–L)**
- [x] CI ✅（`.github/workflows/verify.yml`＝verify-il + verify-differential + verify-ktproj を push/PR で実行。旧 C# オラクル `verify-all` は引退済みバックエンドのため除去）。残: サンプル行列の継続拡張・ネット依存サンプル（Avalonia）のキャッシュ戦略。**(S–M)**
- [ ] **ライセンス / 帰属（出荷必須）**: 参考実装（`KotlinForCLR`、Apache-2.0）からの移植部分のライセンス遵守・NOTICE/帰属、kotlin-compiler-embeddable 等依存のライセンス確認、本体ライセンス確定。**(S)**
- [ ] **利用者ドキュメント**: README からの getting-started、`.ktproj` の書き方、.NET 型の取り込み方（C-2 で一本化した**単一の方法**を説明。使い分けは存在しない形に）、対応/非対応機能一覧。**(M)**
- [ ] **バージョン / サポート方針**: Kotlin 2.4.0 ピン留めの位置づけ、対応 .NET TFM、semver 方針を明文化。**(S)**

---

# 補足: 別プロダクト（コア外）
- Kotlin らしい UI DSL（Avalonia/WPF/WinUI ラッパ、lambda-with-receiver ベース）。**コアに入れない**。A-1 のスコープ関数/DSL 基盤が前提。— [[kotlin-net-is-pure-binding]]

---

## 進め方メモ
- **A と B は相互依存**（stdlib は拡張関数＋lambda-with-receiver で構成）。A-1（拡張関数・スコープ関数・配列・網羅 when・デフォルト引数）と B（コレクション/stdlib 写像）はセットで詰めると効率的。
- 横断で `verify-il.sh`／`verify-ktproj.sh` 緑を維持、IL 出力は kotlin/jvm 差分一致（`verify-differential.sh`）を必須ゲート。
- 大物（B の方針、D の構造化並行性、C のジェネリック注入）は着手前に設計を `docs/` に固定。
