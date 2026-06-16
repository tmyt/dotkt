# kotlin/clr — 長期研究ロードマップ

二大研究トラック **M-D1（CIL 直接出力）** と **M-D2（async/Task ⇄ coroutine）** を、検証可能な細かいマイルストーンへ分解する。加えて両者の前提となる **M-S（言語/stdlib 補強）** を定義する。

## 共通原則

1. **差分オラクル（differential oracle）.** 既存の C# バックエンドは正解器として使える。新バックエンド（IL）や新機能の出力 stdout を、C# 経路の出力と一致比較する。`scripts/verify-all.sh` の枠組みを流用。
2. **公式 lowering の再利用.** coroutine の state machine 変換など難所は、`org.jetbrains.kotlin.backend.common.lower.*` の抽象 lowering を継承して入手する（車輪の再発明をしない）。
3. **façade 方式の継承.** .NET 型参照は `@Clr` façade（手書き or `facadegen` 自動生成）で表現済み。IL/coroutine ランタイムもこの seam の上に乗せる。
4. **各マイルストーンに「動いた」判定（サンプル＋アサート）を必須化.** 量より検証可能性。

凡例: 規模感 S=数時間 / M=1〜2日 / L=数日 / XL=1週間超（個人＋Claude 駆動の体感）。

---

# M-D1: CIL 直接出力バックエンド（C# 非経由）

## アーキテクチャ決定

- **IL を吐く場所 = C# 側ツール.** JVM から .NET アセンブリ（PE/ECMA-335 メタデータ）を書く実用ライブラリは無い。よって **Kotlin バックエンドは「Backend IR（BIR）」を JSON にシリアライズ**し、**C# ツール `ilemit` が BIR を読んで IL を emit** する。参考実装の BIR-XML と同思想。
- **IL emitter = `System.Reflection.Emit.PersistedAssemblyBuilder`（.NET 9+、本機 .NET 10 で確認済み）.** `ILGenerator` で命令列、`Save()` で .dll 化。メタデータ細部の制御が要れば **Mono.Cecil** へ切替（custom attribute / 複雑な generics）。
- **BIR の粒度 = 構造化 AST（stack 化しない）.** 文/式のツリーを serialize し、IL への stack 化（式→push/pop）は `ilemit` 側で行う。Kotlin 側は現 `CSharpCodegen` の IR 走査ロジックを BIR 出力に流用でき、stack 規律という IL 固有の難所を C# 側に閉じ込められる。
- **外部型解決.** `@Clr` façade が `System.Console` 等の完全修飾名を与える。`ilemit` は reflection（`Type.GetType` + `GetMethod`）または Cecil import で `MethodReference` を得る。参照アセンブリは `@(ReferencePath)` から load。

## マイルストーン

- **D1.0 — スパイク: 手書き hello.dll（S）.**
  `ilemit` 雛形が `PersistedAssemblyBuilder` で「Hello from IL」を出す .dll/.exe を生成し `dotnet hello.dll` で実行。*肝*: 実行用 `.runtimeconfig.json` 生成、entry point 設定、core 参照の解決。**判定**: 実行して期待出力。
- **D1.1 — BIR スキーマ v0 + Kotlin 側出力（M）.**
  M0 subset（file→static class, method[name/params/ret], body=構造化 stmt/expr: const/call/binop/var/setvar/return/if/while）の JSON スキーマ定義。`ClrBackendPhase` に BIR 出力を追加（`*.bir.json`）。現 IR 走査を流用。**判定**: m0 の BIR が妥当な JSON。
- **D1.2 — ilemit M0（L）.**
  BIR→IL: `ldstr`/`ldc.i4`、算術（add/sub/mul/div）、static 呼び出し（`Console.WriteLine` を reflection 解決）、local（`stloc`/`ldloc`）、`ret`、分岐（`br`/`brtrue`/`brfalse` で if/while）。**判定**: 生成 .dll の stdout == C# 経路（m0）。
- **D1.3 — 文字列テンプレ/println（S）.**
  string template → `string.Concat(object[])`（値型は `box`）。**判定**: m0 のテンプレ一致。
- **D1.4 — クラス/フィールド/ctor（L）.**
  型定義（field, instance method, ctor）、`newobj`/`ldfld`/`stfld`/`call`/base ctor。**判定**: m-c1 差分一致。
- **D1.5 — 継承/virtual/interface（M）.**
  base type、`virtual`/`override`（`callvirt`）、interface 実装と `callvirt` 経由のディスパッチ。**判定**: m-c1/m-c3 差分一致。
- **D1.6 — BCL interop（@Clr）（L）.**
  instance `new`/`callvirt`、プロパティ（`get_X`/`set_X` 呼び出し）、総称（`MakeGenericType`/`MakeGenericMethod`）、indexer、参照アセンブリ解決。**判定**: m-i1/m-i3/m-i4 差分一致。
- **D1.7 — 例外/ループ/enum（M）.**
  IL の例外ハンドラ領域（try/catch/finally の `ExceptionHandler`）、ループ分岐、enum（int backing）。**判定**: m-c2 差分一致。
- **D1.8 — MSBuild 統合（M）.**
  `<KotlinClrBackend>il</KotlinClrBackend>` で `KotlinClr.targets` が `ilemit` を起動しアセンブリを直接生成（CoreCompile/C# を完全スキップ）。`.ktproj` が `cs`（現行）/`il` を選択可能に。**判定**: `dotnet build` で IL 経路のみのアセンブリが走る。
- **D1.9 — 健全性（L）.**
  stack 型整合（`box`/`unbox.any`/`conv.*` 数値変換）、`ilverify` による検証通過、PDB/sequence point（任意・デバッグ用）。**判定**: `ilverify` が clean、全 verify-all 差分一致。

## リスク
IL の stack 型規律（オペランド型の厳密一致）、例外領域のエンコード、generics の具体化、値型/参照型の box 境界。緩和: 各段で `ilverify` を回し、C# 経路との差分で機能退行を即検知。

---

# M-D2: async/Task ⇄ Kotlin coroutine 相互運用

## 背景と方針

Kotlin の `suspend` は `Continuation<T>` + `COROUTINE_SUSPENDED` センチネルを使う **state machine** にコンパイルされる。`org.jetbrains.kotlin.backend.common.lower.AbstractSuspendFunctionsLowering` / `…coroutines.AbstractAddContinuationToFunctionsLowering`（platform 非依存、本 jar に存在を確認）を **CLR 用に継承**して state machine を IR で入手し、生成された state machine クラス（= ふつうの class + when/switch + field）を既存 class codegen で C# 化する。**suspend の正しい意味論を公式 lowering から継承する**のが要。

- **対象 = `kotlin.coroutines.*` stdlib intrinsics ＋最小 dispatcher ＋ Task ブリッジ.** `kotlinx.coroutines`（`launch`/`async`/`Flow`/構造化並行性）は巨大ゆえスコープ外。
- **CLR ランタイム** = C# 実装の `Continuation`/`CoroutineContext`/intrinsics（`runtime/csharp/KfcCoroutines`）。Task との橋は `TaskCompletionSource` ⇄ `Continuation`。

## マイルストーン

- **D2.0 — coroutine ランタイム雛形（M）.**
  C# 実装: `kotlin.coroutines.Continuation<T>`, `CoroutineContext`, `EmptyCoroutineContext`, `ContinuationInterceptor`, `intrinsics.COROUTINE_SUSPENDED`, `suspendCoroutineUninterceptedOrReturn`, `createCoroutine`/`startCoroutine`。Kotlin 側 façade/expect で対応付け。**判定**: 単体で resume が回る最小テスト。
- **D2.1 — suspend lowering 有効化（L）.**
  `AbstractSuspendFunctionsLowering` 等の CLR サブクラスを backend lowering phase に組み込み、suspend fun が state machine 化された IR（`KIR@Lowered`）になることを確認。*肝*: JVM が使う具体 phase 群のうち platform 非依存なものを選別・移植。**判定**: lowered IR に state machine クラス（label switch + locals フィールド）が出る。
- **D2.2 — state machine の codegen（M）.**
  生成 state machine クラス（`Continuation` 実装＋`invokeSuspend`/`resumeWith` の label による switch）を既存 class codegen で C# 出力。不足 intrinsic 写像を追加。**判定**: 一度 suspend して resume する suspend fun が完走。
- **D2.3 — 最小 dispatcher / runBlocking（M）.**
  現スレッドで coroutine を完走させる `runBlocking` 相当（イベントループ）。**判定**: 「1回 suspend→resume→値返し」が逐次実行で正しい。
- **D2.4 — Task ブリッジ Kotlin→.NET（M）.**
  `suspend fun <T> await(task: Task<T>): T` — coroutine を suspend し `task.GetAwaiter().OnCompleted(resume)` で再開。**判定**: Kotlin suspend が `Task.Delay`/任意 async .NET を await して結果取得。
- **D2.5 — Task ブリッジ .NET→Kotlin（M）.**
  Kotlin suspend を `Task<T>` として公開する `future { … }` ビルダ（`TaskCompletionSource` 完了）。**判定**: C# 側が Kotlin suspend を `await` できる。
- **D2.6 — キャンセル（M）.**
  `CancellationToken` ⇄ Kotlin の cancellation（最小）。**判定**: 取消で coroutine が CancellationException 相当で停止。
- **D2.7 —（stretch）Dispatchers（L）.**
  `Dispatchers.Default`→ThreadPool、`Dispatchers.Main`→`SynchronizationContext`。

## リスク
coroutine lowering は compiler 内部・stdlib intrinsics に密結合。phase 選別と `kotlin.coroutines` ランタイムの忠実度が肝。意味論（例外伝播・キャンセル・構造化並行性）は繊細。`kotlinx.coroutines` 全体はスコープ外と明示。

---

# M-S: 前提となる言語/stdlib 補強（M-D2 と実用化の下地）

- **S1 — null 安全（M）.** `?.`（safe call: 脱糖された tmp+null check を C# `?.` へ畳む）、`?:`（elvis → `??`）、`!!`（→ 値そのまま or `!`）。
- **S2 — data class（S）.** `equals`/`hashCode`/`toString`/`copy`/`componentN` を C# record 風 or 明示メンバで生成。
- **S3 — collections stdlib（M）.** `listOf`/`mutableListOf`/`mapOf`/`setOf` を BCL（`List<>`/`Dictionary<>`/`HashSet<>`）へ写像。イテレーション・`map`/`filter`/`forEach` 等の主要拡張。
- **S4 — generic 型の façade 自動生成（M）.** `facadegen` を総称型（`List<T>`/`Dictionary<K,V>`）対応へ。型パラメータ宣言の出力。
- **S5 — FIR シンボル直接注入（XL・抜本）.** `JvmFrontendPipelinePhase` を CLR-aware frontend に差し替え、`AssemblyResolver` が解決した .NET 型を FIR symbol provider に注入。façade ファイルすら不要化（参考実装 `frontend/symbol/*` が移植元）。

---

# 推奨シーケンスと依存関係

```
（完了）M0/interop/classes/MSBuild/.ktproj/windowing
        │
        ├─ M-D1 (CIL)  ── 独立トラック。C#経路を差分オラクルにできるので自己完結性が高い
        │     D1.0→D1.1→D1.2→…→D1.9
        │
        ├─ M-S (補強)  ── S1/S2/S3 は実用プログラム & M-D2 の下地
        │
        └─ M-D2 (coroutine) ── class codegen(済)+例外(済)+BCL interop(済)+S3 に依存
              D2.0→D2.1→…→D2.7
```

**推奨着手順:** ① M-D1.0–D1.2（CIL の M0 を差分一致まで／自己完結で達成感が高い）→ ② M-S（S1 null安全, S3 collections）で実用度を底上げ → ③ M-D1 を D1.4 以降へ拡張（クラス/interop）→ ④ M-D2（coroutine）。M-D1 と M-D2 は独立に進行可。

**横断的検証:** すべての段で `scripts/verify-all.sh` を緑に保ち、新バックエンド（IL）は C# 経路との差分一致を必須ゲートにする。
