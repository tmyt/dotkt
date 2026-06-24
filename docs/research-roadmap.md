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

- **D1.0 — スパイク: 手書き hello.dll（S）✅ 達成.**
  `ilemit`（`PersistedAssemblyBuilder` + `ManagedPEBuilder`）が IL のみで `hello.dll` + `hello.runtimeconfig.json` を生成、`dotnet hello.dll` が `Hello from IL` を出力。C# ソース/csc を一切経由しないことを実証。CIL 経路の実現可能性を確認。
- **D1.1 — BIR スキーマ v0 + Kotlin 側出力（M）✅ 達成.**
  `BirEmitter.kt` が M0 subset（file→static class, method[name/params/ret], body=構造化 stmt/expr: const/local/bin/un/console/callStatic/concat/cond/var/setLocal/return/while/if）を JSON 化。`ClrBackendPhase` が `*.bir.json` を C# と並行出力。m0 で妥当な JSON（main/sum/fizz, hasMain）を確認。
- **D1.2 — ilemit M0（L）✅ 達成.**
  `ilemit` が BIR→CIL を emit: ldstr/ldc, add/sub/mul/div/rem, 比較(ceq/clt/cgt 合成), `string.Concat(object[])`(box), `Console.WriteLine(object)`, sibling static 呼び出し(2-pass で解決), local/arg(ldloc/stloc/ldarg/starg), ret, while/if/ternary の分岐(br/brfalse + label)。**Kotlin→BIR→CIL→dotnet が C# を一切経由せず m0 を実行、出力が C# 経路と一致**（`scripts/run-il-m0.sh`）。
- **D1.3 — 文字列テンプレ/println（S）.**
  string template → `string.Concat(object[])`（値型は `box`）。**判定**: m0 のテンプレ一致。
- **D1.4 — クラス/フィールド/ctor（L）✅ 達成.**
  BIR にクラス定義(fields/ctors/methods/base/override/virtual)。ilemit が**複数 BIR→1 アセンブリ**で型を emit（newobj/ldfld/stfld/base ctor、ctor を本体前に宣言）。`scripts/verify-il.sh` の `il:mc1` が C# 差分一致。
- **D1.5 — 継承/virtual/interface（M）✅ 達成.**
  継承/virtual は D1.4。interface は本段: BIR に interface 定義+実装リスト、ilemit が interface TypeBuilder(abstract)/`AddInterfaceImplementation`/`DefineMethodOverride`/interface 経由 `callvirt`。`il:iface` が C# 差分一致。
- **D1.6 — BCL interop（@Clr）（L）✅ 達成（静的/インスタンス/new/プロパティ）.**
  BIR が @Clr を `clrNew`/`clrStatic`/`clrInstance`/`clrPropGet`/`clrPropSet`（+argTypes/retType）で符号化、ilemit が reflection で実 .NET 型/メソッド/ctor/property を解決し `newobj`/`call`/`callvirt`（+box）。`il:m2`(System.Math 静的)・`il:mi1`(StringBuilder: new/Append連鎖/ToString/Length) が C# 差分一致。残: 総称(`MakeGenericType`)/indexer（m-i3 相当）。
- **D1.7 — 例外/ループ/enum（M）✅ 達成.**
  enum（ordinal）+ when(subject)（`il:enum`）。for-in range→IL counter loop（`il:for`）。String `+`→concat 修正。**例外**: IL 例外領域（`BeginExceptionBlock`/`BeginCatchBlock`/`EndExceptionBlock`）+ try 内 return を result local+`leave` へ変換、java.lang 例外→System.* 写像。`il:exc`（safeDiv で DivideByZeroException を Arithmetic catch）が C# 差分一致 & ilverify clean。

**=> M-D1（CIL 直接出力）D1.0–D1.9 完了。** `scripts/verify-il.sh` が 8 サンプル差分一致 + 全 ilverify clean。残（小）: 総称/indexer の IL 化。
- **D1.8 — MSBuild 統合（M）✅ 達成.**
  `<KotlinClrBackend>il</KotlinClrBackend>` で `KotlinClr.targets` が、csc には placeholder Main を与えて valid assembly を作らせ、`AfterTargets CoreCompile` で `ilemit` が BIR→CIL を emit してアセンブリを上書き。`samples/ktproj-il` が `dotnet build`/`run` で純 IL アセンブリを生成・実行（`Hello, ktproj, from IL!`）。※コンパイラ変更後は `installDist` 更新が前提。
- **D1.9 — 健全性（L）✅ 達成（ilverify clean）.**
  `verify-il.sh` に `ilverify` パス追加、生成 6 アセンブリ全て **"Verified"**。ilverify が「interface 引数を object 型にしていた」検証エラーを検出 → `birType` を interface→`@Name` へ修正。差分一致＋形式検証の両方が緑。残: PDB/sequence point（任意）、`conv.*` 数値変換の網羅。

## リスク
IL の stack 型規律（オペランド型の厳密一致）、例外領域のエンコード、generics の具体化、値型/参照型の box 境界。緩和: 各段で `ilverify` を回し、C# 経路との差分で機能退行を即検知。

---

# M-D2: async/Task ⇄ Kotlin coroutine 相互運用

## 背景と方針

Kotlin の `suspend` は `Continuation<T>` + `COROUTINE_SUSPENDED` センチネルを使う **state machine** にコンパイルされる。`org.jetbrains.kotlin.backend.common.lower.AbstractSuspendFunctionsLowering` / `…coroutines.AbstractAddContinuationToFunctionsLowering`（platform 非依存、本 jar に存在を確認）を **CLR 用に継承**して state machine を IR で入手し、生成された state machine クラス（= ふつうの class + when/switch + field）を既存 class codegen で C# 化する。**suspend の正しい意味論を公式 lowering から継承する**のが要。

- **対象 = `kotlin.coroutines.*` stdlib intrinsics ＋最小 dispatcher ＋ Task ブリッジ.** `kotlinx.coroutines`（`launch`/`async`/`Flow`/構造化並行性）は巨大ゆえスコープ外。
- **CLR ランタイム** = C# 実装の `Continuation`/`CoroutineContext`/intrinsics（`runtime/csharp/KfcCoroutines`）。Task との橋は `TaskCompletionSource` ⇄ `Continuation`。

## 実装メモ（2026-06-16）— async-mapping 方式で非ブロッキング Task interop 達成（部分）

state machine lowering の代わりに、より実用的な **`suspend` → C# `async Task<T>` 写像**を採用:
- `suspend fun` → `async global::System.Threading.Tasks.Task<T>`、suspend 呼び出し → `await`、suspend ラムダ → `async (...) =>`。
- **汎用 interop ポイント `@ClrAwait suspend fun <T> Task<T>.await(): T`**（WinRT `AsTask()` 着想の逆向き橋）。codegen は `@ClrAwait` 注釈の呼び出しを「拡張レシーバ＝awaitable」に畳み、suspend ラッパが `await <receiver>` を生成。→ **任意の .NET `Task<T>` を返す素の API をラップ無しで await 可能**（`IAsyncOperation`/`ValueTask` も同型注釈で拡張可）。
- `runBlocking` 相当 = 境界で `body().GetAwaiter().GetResult()`。
- `samples/m-d2`：素の .NET async API `Api.FetchAsync(): Task<int>` を `task.await()` で await、`result = 42`（非ブロッキング）。

**達成範囲**: suspend/await/Task の基本相互運用（非ブロッキング）。**残**: 完全な Kotlin coroutine 意味論（state machine lowering, `Dispatchers`, 構造化並行性, cancellation, `Flow`）は C# async 写像では近似に留まり、下記の正攻法（lowering 再利用）が要る。

## マイルストーン（正攻法・state machine）

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

- **S1 — null 安全（M）✅ 達成.** `?:`（elvis → C# `(a ?? b)`）、`!!`（CHECK_NOT_NULL → 値）、`?.`（safe-call → `(a == null ? (T?)null : a.b)`、値型メンバ対応）。`String.length`→`.Length`。`samples/m-s1`（`len hello = 5`）。
- **S2 — data class（S）✅ 達成.** `toString`/`copy`/`componentN`、フィールドから値等価 `Equals`/`GetHashCode` を生成、`==`(EQEQ) を値型/string/enum は `==`・参照型は `System.Object.Equals` へ写像、Object メソッド名を C# 名へ。`samples/m-s2`（`a==b` 構造等価が True）。
- **S3 — collections stdlib（M）— 部分達成.** `listOf`/`mutableListOf`/`arrayListOf` → `new List<T>{...}`、`kotlin.collections.List/Set/Map` → BCL generics、`.size`→`.Count`、for-in→`foreach` 達成（`samples/m-s3`）。残: `mapOf`/`setOf`、`map`/`filter`/`forEach` 等拡張。
- **S4 — generic 型の façade 自動生成（M）✅ 達成.** `facadegen` が generic type definition（`List\`1`）から `class List<T>` を自動生成（型パラメータ、indexer→operator get/set、generic param→T）。`samples/m-s4` が生成 façード経由で `List<Int>` を使用（count/indexer/add）。手書き m-i3 façード相当を自動化。
- **S5 — FIR シンボル直接注入（✅ 達成・メタデータ駆動・実機）.** `import clrgen.Math; Math.Abs(-9)` / `Console.WriteLine(...)` を **façade .kt 無し**で解決 → `global::System.Math.Abs(-9)` 等 → 実行（`samples/m-s5`：`abs(-9)=9 / max(3,7)=7 / min(3,7)=3`、`verify-all` 常設）。
  - **機構**: `kotc/.../frontend/ClrTypeInjection.kt` の `FirDeclarationGenerationExtension` が .NET 型を FIR に合成（object=静的メソッド / class=コンストラクタ+インスタンスメソッド、オーバーロード対応）。`ClrCompilerPluginRegistrar`→`COMPILER_PLUGIN_REGISTRARS` 経由で**再利用中の JVM frontend に登録**（`ClrPluginRegistrationPhase`、frontend 差し替え不要）。合成 FIR には注釈を付けず `ClrTypeRegistry`（Kotlin-FQN→.NET 名）を backend `clrName` が参照（注釈の Fir2Ir 透過問題を回避）。
  - **メタデータ駆動**: 注入型集合はハードコードでなく、`facadegen --meta`（既存 reflection を再利用）が**実 .NET アセンブリ**から生成する metadata ファイルから読む（環境変数 `CLR_TYPES_METADATA`）。
  - **対応メンバ（穴なし）**: object(静的メソッド) / class(コンストラクタ + インスタンスメソッド + **プロパティ**) / オーバーロード / 自己・他注入型参照。`samples/m-s5` は System.Math(90 メソッド)+System.Console+**System.Text.StringBuilder**(ctor/`Append` 自己返し/`Length` プロパティ/`ToString`) を façade 無しで実行。
  - **MSBuild 統合**: `.ktproj` に `<KotlinClrType Include="System.X"/>` を書くと façade 無しで注入（`KotlinClrInjectTypes` ターゲットが metadata を生成し `CLR_TYPES_METADATA` を渡す）。`samples/ktproj-inject` が `dotnet build/run` で実動作。**総称型**は `<KotlinClrType>` では明示メッセージで `<KotlinClrFacade>`（実証済 façade 経路・`m-s4` の List<T>）へ誘導＝穴のない一体運用。
  - **残（内部最適化・ユーザ可視な穴ではない）**: 総称型の FIR 直接注入（現状は façade 経路で網羅）、`AssemblyResolver`（参照アセンブリ走査）化。
  - **確認済み seam（実装の土台）:**
    1. `FirDeclarationGenerationExtension`（`getTopLevelClassIds`/`generateTopLevelClassLikeDeclaration`/`generateConstructors`/`generateFunctions`/`generateProperties`/`getCallableNamesForClass`/`hasPackage`）で .NET 型を **FIR に合成**。`createTopLevelClass`/`createMemberFunction`/`createConstructor` ヘルパが合成を補助。
    2. これを `FirExtensionRegistrarAdapter`（= `ProjectExtensionDescriptor`）として **再利用中の JVM frontend セッションへ登録**（Configuration と Frontend の間に登録フェーズを差すだけ。frontend 差し替え不要）。
    3. **合成 FIR に `@Clr("System.X")` 注釈を付与**すれば、既存 backend codegen がそのまま .NET へ写像（= facadegen が `.kt` に書くものを in-memory で生成するだけ）。新 backend 経路は不要。
  - 対象型集合は compiler arg / `<KotlinClrType>` で与える（現 `<KotlinClrFacade>` の façード生成を FIR 注入へ置換）。
  - **残る難所（= XL・複数セッション）**: FIR symbol の正しい構築（resolution phase、`ConeKotlinType`/`FirResolvedTypeRef`、lazy body、注釈の Fir2Ir 透過）。ここを品質を保って実装するのが本体。`AssemblyResolver`（reflection/Roslyn）で型集合のメタデータを供給。

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

---

# 残マイルストーン計画（2026-06-16）— production grade への道

M-D1 / M-D2 / M-S(S1–S5) は達成。ここからは「サンプルが動く」→「信頼できるコンパイラ・実用フレームワーク」への引き上げ。
> **未実装の網羅チェックリストは [`docs/remaining-tasks.md`](remaining-tasks.md)（living）に集約**。本書は研究トラックの設計背景、あちらは漏れ防止のタスク表。
原則1（穴なし）: 各段は **end-to-end・穴なし・verify-all 緑・想定外は明示メッセージ**。サイズ目安 S/M/L/XL。

**原則2（スコープ＝純粋な .NET バインディング）.** Kotlin.NET は **Kotlin→.NET の「バインディング」（言語コンパイラ＋包括 interop）に徹し、独自ライブラリ（特に UI ライブラリ）を同梱しない**。
- Windowing は `System.Windows`/XAML・WPF・WinUI・Avalonia の**実型を `import` して直接**使う。Kotlin.NET 提供の UI ランタイムに依存させない。
- Kotlin らしい UI DSL（Avalonia/WPF/WinUI ラッパ）は、Kotlin.NET の**上に乗る別の派生プロダクト**（KMP が Kotlin の上に立つのと同じ関係）として分離。コアには入れない。
- **帰結（✅ 実施済）**: `runtime/csharp/KfcUi`（C# 製 UI shim）と依存サンプル（`samples/win*`）・`scripts/run-window.sh` を**削除**。windowing は framework-direct（実型を import）のみ。下記 Track W は I2/I3/I4 の上の「バインディング検証」。

## Track P — production 基盤（信頼性。最優先）
- **P1 差分テストハーネス（M）**: 同一 `.kt` を kotlin/jvm と kotlin/clr で実行し stdout 一致を自動 assert。corpus を増やし回帰の正本を JVM oracle に。＝「デモ」と「コンパイラ」を分ける核。M0 からの積み残し。
- **P2 診断品質（M）**: 未対応構文・interop エラーをソース位置付き・読めるメッセージで報告（現状は一部 throw/握りつぶし）。`-Xverify-ir` 相当の健全性ゲート常設。
- **P3 境界の null 正当性（M）**: Kotlin null 安全 ↔ .NET 参照型 / `Nullable<T>` / NRT 注釈の対応を厳密化。プラットフォーム型の扱いを定義。
- **P4 CI（S）**: verify-all + verify-il を CI 化、サンプル行列を拡張。

## Track I — interop 完全化（穴を残さない）
- **I1 総称型の FIR 直接注入（L）**: `<KotlinClrType>` で `List<T>`/`Dictionary<K,V>` を façade 無し注入（現状は façade 経路へ誘導）。型パラメータ付き FIR 合成＋ codegen 総称。これで注入経路の最後の穴が閉じる。
- **I2 AssemblyResolver（M）— ✅ 達成・実機.** facadegen が `--refs <参照アセンブリパス;…>` を受け、`Assembly.LoadFrom`＋`AssemblyResolve` で**任意の参照アセンブリ**から型解決（BCL の `Type.GetType` プローブに加え）。型比較は assembly identity 非依存の FullName ベースへ。MSBuild は `KotlinClrInjectTypes`（`DependsOnTargets=ResolveReferences`）で `@(ReferencePath)` を渡す。`samples/ktproj-extlib`：外部 C# アセンブリ（`Ext.Widget`）を `ProjectReference`＋`<KotlinClrType>` で **façade-free 消費**（`dotnet build/run` で `Add(2,3)=5`）。これで Avalonia/WPF を `<PackageReference>` から注入する道が通った。
- **I3 .NET 基底クラス継承（L）— ✅ メカニズム達成・実機.** 注入型を Kotlin class が継承（`class Sub : Base()`）、base ctor 呼び出し、継承メンバ参照、**.NET virtual メンバの override（virtual dispatch）**、継承チェーンまで実機動作。`samples/m-i5`（façade-free で `System.Exception` を継承し `Message` を override、`FatalError : AppError : Exception` の多態）。実装: 注入型/メンバを .NET sealed/virtual に応じて `open`(modality) 化（`ClrTypeInjection`）、property の `override`/`virtual` 修飾子を codegen 追加。残: ジェネリック基底（`List<T>` 継承）。これで `class App : Application()` の前提が立つ。
- **I4 delegate/event interop（M）— ✅ イベント subscribe/unsubscribe 達成・実機.** .NET event を façade-free 注入型が `add_<E>`/`remove_<E>`（ハンドラ＝Kotlin 関数型 `kotlin.FunctionN`）として公開、codegen が `recv.<E> += handler` / `-= handler` を生成（`ClrEventRegistry`）。`samples/ktproj-extlib`：外部アセンブリの `event Action<int> Changed` を **Kotlin ラムダで購読**し発火（`changed: 5 / changed: 9`）。これでフレームワークの click/イベント駆動 UI を純 Kotlin で書ける。残: `out`/`ref`、nullable 値型、.NET enum 取り込み、総称 delegate の網羅。

## Track L — 言語/stdlib breadth（実プログラム網羅）
- **L1 collections stdlib（M）**: `map`/`filter`/`fold`/`forEach`/`associate`/`mapOf`/`setOf` 等を BCL/LINQ へ写像。
- **L2 ユーザ定義総称（L）**: Kotlin 側の generic class/fun を codegen（façade だけでなく自前型の型パラメータ）。
- **L3 関数型 breadth（M）**: クロージャ捕捉・inline 関数・拡張関数（自前型）・演算子オーバーロード網羅。
- **L4 言語 breadth（M）**: sealed + exhaustive when、inner/nested、default/vararg、destructuring。

## Track C — coroutine 完全意味論
- **C1 一般 CPS 完成（L）**: 部分式内サスペンドの spilling、ループ/分岐 **条件式** 内サスペンド（現状は明示エラー）。
- **C2 CancellationToken を ABI に（S）**.
- **C3 Flow ⇄ IAsyncEnumerable（L）**.
- **C4 構造化並行性（XL）**: Job/CoroutineScope/dispatcher。戦略B ランタイムの本丸。

## Track D-IL — IL バックエンドを C# 経路と parity に
- **D-IL1 IL で状態機械（L）**: coroutine を IL 直接出力（現状 C# 経路のみ）。
- **D-IL2 IL で総称（L）**、**D-IL3 BCL interop parity + PDB/デバッグ情報（L）**。

## Track W — framework-direct windowing（= バインディングの完全性テスト。UI ライブラリは作らない）
原則2 に従い、これは「Kotlin.NET の windowing 機能」ではなく **「実 UI フレームワークを Kotlin から丸ごと消費できるか」のバインディング検証**。
- **W0 KfcUi 撤去（M）— ✅ 達成**: C# 製 UI shim・`samples/win*`・`run-window.sh` を削除。
- **W1 純 Kotlin App（L）— ✅ コア達成・実機（描画はスコープ外）.** `<PackageReference Include="Avalonia"/>` の実型 `Avalonia.Application` を façade-free 注入し、**Kotlin が直接継承** `class MyApp : Application()`＋virtual `Initialize()` を override → `dotnet build/run` で実行（`samples/ktproj-avalonia`）。I2 は ref アセンブリ（`ref/`）を読むため `MetadataLoadContext` 化（`Assembly.LoadFrom` は ref を拒否）。**PackageReference 型を Kotlin 基底にできることを実証**＝framework-direct windowing の中核成立。Avalonia の実描画は目標外（Kotlin.NET は純バインディングであることの確認が目的）。
- **W2 XAML/WPF・WinUI（M〜L, Windows）**: `System.Windows`/XAML 名前空間の import と XAML ロード。WPF はコンパイル可、Windows 実行で点灯。
- **W3（別プロダクト）Kotlin-idiomatic UI DSL**: Avalonia/WPF/WinUI をラップした KMP 的派生プロダクト。**Kotlin.NET コア外**。リポジトリ/パッケージを分離。

## Track X — 配布/DX（publishable）
- **X1 `dotnet new ktproj` テンプレート + MSBuild SDK/NuGet 化（M）**: targets をパッケージ配布、相対パス依存を排除。
- **X2 VS/VS Code 体験（M〜L）**: ビルド/実行統合（フル LSP は別スコープ）。
- **X3 リリース（S）**: self-contained コンパイラ、バージョン付き配布。

## 推奨シーケンス
1. **P1 差分ハーネス**（信頼性の土台・公開時の説得力）→ P2 診断。
2. **I1 総称注入 → I2 AssemblyResolver → I3 .NET 継承**（interop の穴を全閉）。
3. **W1 純 Kotlin App**（I3 の上に flagship デモ）。
4. L1–L4（実プログラム網羅）→ C1–C3（coroutine）→ D-IL（IL parity）→ X（配布）。

各段 verify-all/verify-il 緑を必須ゲート、IL は常に C# 経路と差分一致。
