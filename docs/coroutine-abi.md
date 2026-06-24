# Kotlin `suspend` ⇔ CLR の ABI 契約（coroutine 完全実装の前提）

完全な coroutine ランタイムを実装する**前に**、「CLR から見た `suspend fun` の見え方」を**実装戦略から独立した不変契約**として固定する。これにより内部実装（後述 A/B）を入れ替えても、C#/F# 等の消費側コードは変わらない。

## 1. 不変契約（CLR から見た `suspend fun`）

- `suspend fun foo(args): T` ⇔ CLR では **`Task<T> Foo(args)`**（`T = Unit` なら `Task`）。
- **隠れた `Continuation` 引数は公開 ABI に漏らさない**（Kotlin 内部表現）。
- キャンセル対応時は末尾に `CancellationToken` を足し **`Task<T> Foo(args, CancellationToken)`** に見せる（C# 慣習と一致）。
- 消費側（C#/F#）はただの Task 返しメソッドとして扱える:
  ```csharp
  int r = await ns.Foo(args);   // 普通に await
  ns.Foo(args).ContinueWith(...); // 可
  var x = ns.Foo(args).Result;    // 可（非推奨）
  ```
- 即完了する suspend（サスペンドポイント無し）は **同期完了済みの Task**（`Task.FromResult` 相当）を返す。

> 要点: **「Continuation を隠して `Task<T>` として見せ、内部で結線する」**。この契約は実装戦略 A/B のどちらでも同一。

## 2. 実装戦略 A（async 写像）— 現状

`suspend` を C# `async Task<T>` に写像。C# コンパイラが state machine を生成する。

**この契約をすでに満たしている範囲:**
- `suspend fun foo(): T` → `public async global::System.Threading.Tasks.Task<T> Foo()` を生成 = **ABI 準拠**。`Continuation` は C# の state machine 内に隠れ、公開 ABI に漏れない。
- **CLR → Kotlin**（CLR が Kotlin の suspend を await）: 成立。返り値が Task なのでそのまま `await`。
- **Kotlin → CLR**（Kotlin が .NET awaitable を await）: `@ClrAwait suspend fun <T> Task<T>.await(): T` 一点で成立（WinRT `AsTask()` の逆向き橋）。`IAsyncOperation`/`ValueTask` も同型注釈で拡張可。
- 即完了 suspend（await 無し）→ C# async が完了済み Task を返す（= `Task.FromResult` 相当）。
- 例外: Kotlin `throw` → async が faulted Task に伝播（自然）。`try/catch` を跨いだ await も C# async がそのまま処理。

**A の限界（= B が要る理由）:**
- 意味論が C# async であり、Kotlin coroutine 固有の **Job / 構造化並行性 / dispatcher / `Flow`** は持たない。
- キャンセルは `CancellationToken` 経由で、Kotlin の協調キャンセル（`Job`/`CancellationException`）とは別物。
- → これらが要るなら戦略 B（自前ランタイム）へ。ABI は不変のまま移行できる。

## 3. 実装戦略 B（自前 Kotlin coroutine ランタイム）— 完全実装

Kotlin の suspend lowering を再利用して state machine を IR で入手し、CLR 側に Continuation ランタイムを用意。公開境界で **Continuation ⇄ `TaskCompletionSource<T>`** を結線する（`future { foo() }` 逆ブリッジ）。

- 公開 `Task<T> Foo(args)` は内部 `fooImpl(args, rootContinuation)` を起動し、`TaskCompletionSource<T>` を返すブリッジに合成（Continuation は隠す）。
- root continuation の `resumeWith`:
  - 正常完了 → `tcs.SetResult(value)`
  - 例外 → `tcs.SetException(ex)`（`CancellationException` → `tcs.SetCanceled()` / `OperationCanceledException`）
- state machine 変換 = `org.jetbrains.kotlin.backend.common.lower.AbstractSuspendFunctionsLowering`（platform 非依存、jar 内に存在確認済み）を CLR 用に継承し `ClrBackendPhase` の lowering に組み込む。
- 生成された state machine クラス（`Continuation` 実装 + label switch + locals フィールド）は既存 class codegen でそのまま出力できる（class/when/field は実装済み）。

## 4. 意味論の対応表（A→B で埋める差分）

| Kotlin | CLR |
|---|---|
| 例外 | faulted Task（`SetException`） |
| `CancellationException` | `OperationCanceledException` / canceled Task（`SetCanceled`） |
| キャンセル（`Job` 協調） | `CancellationToken`（末尾引数で受け、協調キャンセルに変換） |
| dispatcher / continuation context | `SynchronizationContext` / `ConfigureAwait` |
| `Flow<T>` | `IAsyncEnumerable<T>`（`await foreach`） |

## 5. 着手順（この ABI を前提に）

1. **ABI 契約を固定**（本書）。← 完全実装の前提。✅ 確定
2. 戦略 A で Task interop を提供（**済**: `cases/m-d2`、非ブロッキング `await`）。
3. **CancellationToken を ABI に追加**（小）: suspend → `Task<T> Foo(args, CancellationToken)`。A でも `ct` を末尾引数として透過し、`@ClrAwait` 側で `task.WaitAsync(ct)` 等に渡せる。
4. 戦略 B（自前ランタイム + TCS ブリッジ + state machine lowering）へ移行。**公開 ABI 不変**のまま意味論を Kotlin 準拠へ。
5. `Flow` ⇔ `IAsyncEnumerable<T>` は B の上に別途。

> まとめ: **「Continuation を隠して `Task<T>` として見せ、TCS で結線する」** が答え。現状（A）は Kotlin→CLR の ABI をすでに満たしており、B 移行時もこの契約を破らない。

## 6. 戦略B 実装状況と手順

### D2.0 — CLR Continuation ランタイム（✅ 構築・**end-to-end 実動作検証済**）
`runtime/csharp/KfcCoroutines/Coroutines.cs`: `KResult<T>`, `IContinuation<T>`, `CoroutineContext`, `Intrinsics.CoroutineSuspended`, **`CoroutineBuilders.Future`（Continuation⇄`TaskCompletionSource` ブリッジ：正常→SetResult / 例外→SetException / OperationCanceled→SetCanceled）**, `RunBlocking`。依存なし。
**検証**: `ref/StateMachineRef.cs` が「lowering が生成すべき状態機械」を手書きで再現し、2段サスペンドの coroutine を **C# async/await を使わず** `IContinuation`+`Future`+TCS だけで非ブロッキング駆動 → `chain = 30`。**戦略B のエンジンが実動作することを実証**（戦略Aとは別経路）。この手書き state machine が **D2.1 の codegen ターゲット形**。

### D2.1a — 制約付き状態機械 codegen（✅ 達成・実機）
`@Sm` でオプトインした suspend fun（**線形 await 列**: `val xi = ei.await()` の並び + `return`）を、**コンパイラが state machine クラス（`IContinuation<T>` 実装 + label/locals フィールド + `ResumeWith` の label switch）へ変換**し、公開 `Task<T>` ブリッジ（`Future` 経由・Continuation 隠蔽）を生成。`cases/m-d2-sm`：`chain = 30` を **C# async/await 非使用**・D2.0 ランタイム（Continuation/TCS）の非ブロッキング駆動で達成。**「Kotlin suspend → コンパイラ生成状態機械 → 純ランタイム」が end-to-end で実動作**（strategy A/手書きと別）。
引数付き suspend も対応（パラメータを state machine フィールド化、`cases/m-d2-sm` の `fetchDouble(7)=14`）。
コルーチン合成（suspend が別 suspend を直接呼ぶ＝サスペンド点）も対応（`useChain=35`）。

### D2.1b — 一般 CPS lowering（✅ 達成・実機 / 自前実装）

**方針決定（ユーザ確定）: `AbstractSuspendFunctionsLowering` は再利用しない。** 実機調査で `buildStateMachine` が**抽象**＝CPS の核心は結局自前実装が前提と判明（base は params→fields / coroutine class 生成 / body 差し替えの足場だけを提供）。IR レベルの雛形 `JsSuspendFunctionsLowering` は ~46KB、加えて CLR `CommonBackendContext` 構築 + stdlib coroutine intrinsic（`Continuation`/`COROUTINE_SUSPENDED`）の CLR ランタイムへの写像が必要で、「再利用」は近道にならない。よって**変換を端から端まで自前所有し、自前ランタイム（`IContinuation`/`Future`）へ直接出力**する。

**鍵となる単純化: 出力が C# なので `goto` が使える。** JS/Native の lowering は IR（goto 無し）で状態機械を組むため relooper 相当の CFG 変換が要るが、こちらは構造化制御フロー＋サスペンド点を **label dispatch + goto** へ直接線形化できる（CFG/relooper 不要）。`CSharpCodegen.kt` の `emitCps`/`emitWhenCps`/`emitWhileCps`/`emitSuspend` が実装。

- **サスペンド点**: `val x = e`／`e`／`return e`。任意のネスト（block / `if`・`when` 分岐 / `while` ボディ）内で可。
- **field 昇格**: サスペンド `return` を跨いで生存するローカルは field 化（`collectCpsVars`）。C# ローカルは `ResumeWith` 呼び出し間で生存しないため。完全同期な島内のローカルはそのまま。
- **状態機械**: 各サスペンド点 = 1 state（ループ内でも static に 1）。`switch(__label){ case k: goto __Rk; }` で再入ディスパッチ。
- 実機: `cases/m-d2-sm` の `sumLoop(4)=6`（**ループ内サスペンド**）, `branch(true/false)=15/10`（**分岐内・パスごとに異なるサスペンド数**）。生成形は教科書的 CPS（`__L0:` ループ頭 → `if(!cond) goto __L1` → `__R1` サスペンド → 後退辺 `goto __L0`）。

**更新（2026-06-22）: かつての残項目 ① 部分式にネストしたサスペンド（`f(g().await())`＝spilling）, ② ループ/分岐の条件式内サスペンド は両方とも実装済み**（`BirEmitter.spillExpr`/`emitWhileCps`/`emitWhenCps`）。さらに try-catch- および try-finally-around-await も対応済み（design-coroutines-clr.md §13v）。コルーチン表面は全面実装済 — AS-BUILT は design-coroutines-clr.md §§13a–§14a を参照。残るは Track 2（本物の upstream のコンパイル）のみ。
