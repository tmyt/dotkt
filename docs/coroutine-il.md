# 設計スパイク: コルーチン（suspend）の IL 化 — 戦略 A/B 決定

フェーズ3（[[csharp-retirement-design]] §3.1）着手前の設計固定。ABI は不変（[[coroutine-abi-decision]]）:
`suspend fun f(args): T` ⇔ CLR `Task<T> F(args)`（Unit→`Task`）、`Continuation` は公開 ABI に漏らさない。
実装戦略を入れ替えても ABI は固定。本書で A/B を決め、ユーザ判断を仰ぐ（[[design-first-on-hard-features]]）。

作成 2026-06-18（フェーズ1=イベント・フェーズ2=generic/indexer 完了後）。

---

## 0. 現状

- **C# 経路には 2 つのコルーチン実装がある**:
  1. 単純 `suspend fun` → C# `async Task<T>` ＋ `await`（`CSharpCodegen.kt:310-316`）。状態機械は **csc が生成**（IL には持ち込めない＝C# コンパイラ依存）。
  2. **D2.1 `@Sm` 手書き CPS lowering**（`CSharpCodegen.kt:358-560`）: goto ベースの状態機械を**自前ランタイム** `Kotlin.Coroutines.IContinuation<T>`/`KResult`/`CoroutineContext` 上で駆動。`runtime/csharp/KfcCoroutines/Coroutines.cs`。TCS で `Task<T>` 境界に橋渡し。
- **IL 経路はゼロ**（grep 0）。IL は csc に頼れないので、**手書き状態機械が必須**（D2.1 と同じ発想）。問題は「どのランタイム形に降ろすか」＝戦略 A/B。

## 1. スパイク結果（戦略 B = CLR ネイティブ async の目標形）

最小 C# `async Task<int> AddAsync(Task<int> t){ int x = await t; return x+1; }` を Release/Optimize でビルドし、生成メタデータを反射ダンプ（`/tmp/asyncspike`）:

```
TYPE A
   method AddAsync [attr] AsyncStateMachineAttribute(typeof(<AddAsync>d__0))
   method AddAsync(Task`1) -> Task`1
TYPE A+<AddAsync>d__0  base=ValueType  valueType=True  ifaces=[IAsyncStateMachine]
   [attr] CompilerGeneratedAttribute
   field Int32                       <>1__state
   field AsyncTaskMethodBuilder`1    <>t__builder
   field Task`1                      t            // captured param
   field TaskAwaiter`1               <>u__1       // awaiter cache
   method MoveNext() -> Void
   method SetStateMachine(IAsyncStateMachine) -> Void
```

**kickoff メソッド** `AddAsync`: `d__0 sm; sm.t = t; sm.<>t__builder = AsyncTaskMethodBuilder<int>.Create(); sm.<>1__state = -1; sm.<>t__builder.Start(ref sm); return sm.<>t__builder.Task;`

**MoveNext** の骨格:
```
switch(<>1__state){ case 0: goto resume; }
var awaiter = t.GetAwaiter();
if(!awaiter.IsCompleted){ <>1__state=0; <>u__1=awaiter; <>t__builder.AwaitUnsafeOnCompleted(ref awaiter, ref this); return; }
resume: awaiter = <>u__1; int x = awaiter.GetResult();
result = x+1; <>1__state=-2; <>t__builder.SetResult(result);
// 例外時: <>1__state=-2; <>t__builder.SetException(e);
```

### B の Reflection.Emit 上の難所
1. **struct 状態機械**（ValueType + IAsyncStateMachine）。`MoveNext`/`SetStateMachine` を struct に実装。
2. **`AsyncTaskMethodBuilder<T>` プロトコル**: `Create()`/`Start<TSM>(ref sm)`/`Task`(getter)/`AwaitUnsafeOnCompleted<TAwaiter,TSM>(ref awaiter, ref sm)`/`SetResult(T)`/`SetException(Exception)` の**正しい呼び順**。
3. **自己参照ジェネリクス**: `Start<TSM>` / `AwaitUnsafeOnCompleted<TAwaiter,TSM>` の `TSM` ＝**今 emit 中の状態機械 struct（TypeBuilder）自身**。`MakeGenericMethod(TypeBuilder)` ＋ ベイク前トークン規律（[[il-primary-backend-pivot]] の Round 5/9 と同型のリスク）。
4. **ref ローカル / `ldflda`**: builder・awaiter を ref で渡す（managed pointer 規律）。
5. `[AsyncStateMachine]`/`[CompilerGenerated]` 属性（任意・デバッガ用）。

→ いずれも前例（generic TypeBuilder、constrained call）と同型で**実装可能**だが精緻。設計検証エージェントで事前確認推奨（§3.2）。

### ✅ PoC 実証済み（2026-06-18, `/tmp/smpoc`）
`PersistedAssemblyBuilder`（ilemit と同じ機構）で `Task<int> AddAsync(Task<int> t) => await t + 1` を struct `IAsyncStateMachine` として emit し、**実機で `42` を出力**（`Task.Delay(30).ContinueWith(_=>41)` ＝未完了タスクを await → 真にサスペンド→再開→`GetResult`=41→+1）、**ilverify clean**。確認できた要点:
- struct（ValueType）+ IAsyncStateMachine の DefineType/DefineMethodOverride。
- **自己参照ジェネリクス OK**: `builder.Start<Sm>(ref sm)` / `AwaitUnsafeOnCompleted<TaskAwaiter<int>, Sm>(ref awaiter, ref this)` の `Sm` に **TypeBuilder 自身**を `MakeGenericMethod` で渡せる（ベイク前でも PersistedAssemblyBuilder が解決）。
- value-type メソッドの `ldarg.0` ＝ `ref this`（managed pointer）をそのまま AwaitUnsafeOnCompleted の `ref TStateMachine` に渡せる。
- `AsyncTaskMethodBuilder<T>` プロトコル（Create/Start/AwaitUnsafeOnCompleted/get_Task/SetResult）の呼び順を実機確定。例外パス（SetException）は機構的に同型で後付け可。
→ **戦略 B の最大不確実性（自己参照 generic・struct SM）は解消。B で実装続行。**

## 2. 戦略 A = Continuation ランタイム合成（C# D2.1 の移植）

D2.1 の goto 状態機械（`ResumeWith(KResult<object>)` ＋ `switch(__label) case s: goto __Rs;`）を BIR ノードへ移植し、`IContinuation<T>`/`KResult<T>`/`CoroutineContext` を**ユーザアセンブリに合成**（KProperty/委譲クラスと同じ機構）。`Task<T>` 境界は TCS ブリッジ。

- **利点**: 既に動く C# D2.1 のロジックを再利用（CPS lowering の正しさは検証済）。状態機械は **class**（struct でない）＝ Reflection.Emit が素直（自己参照ジェネリクス不要、ref struct 規律不要）。`collectCpsVars`/`emitWhenCps`/`emitWhileCps` の移植が主作業。
- **欠点**: **Continuation ランタイムをユーザアセンブリに毎回合成**＝[[clr-not-jvm-discard-jvmisms]] が「捨てろ」と言う JVM 由来の Continuation を CLR に持ち込む。`Task` 統合は TCS 経由の間接（真の CLR async ではない）。

## 3. 比較と推奨

| 観点 | A: Continuation 合成 | B: CLR ネイティブ |
|---|---|---|
| idiom（[[clr-not-jvm-discard-jvmisms]]） | ✗ JVM-ism 持ち込み | ✓ CLR async そのもの |
| pure-binding（[[kotlin-net-is-pure-binding]]） | △ runtime 合成 | ✓ 合成不要 |
| Task 統合 | △ TCS 間接 | ✓ ネイティブ |
| Reflection.Emit 難度 | ○ class・既存ロジック移植 | △ struct・自己参照ジェネリクス |
| デバッガ async 対応 | ✗ | ✓ |
| CPS lowering ロジック | ✓ D2.1 再利用 | ✓ D2.1 再利用（降ろし先だけ差替） |

**重要**: CPS lowering 本体（どこが suspension 点か、live-local の field 昇格、制御フローの linearize）は **A/B 共通**で D2.1 から再利用できる。**違うのは「状態機械を class+Continuation に降ろすか、struct+AsyncTaskMethodBuilder に降ろすか」だけ**。つまり A/B はランタイム降ろし層の差で、CPS フロントは共通。

**推奨 = B を第一候補**（[[clr-not-jvm-discard-jvmisms]] の精神＝JVM の Continuation を持ち込まず CLR の async に乗せる、pure-binding、Task ネイティブ統合、デバッガ対応）。Reflection.Emit の精緻さが障害になった場合のみ A にフォールバック（C# D2.1 の確実な移植）。

## 4. 段階（推奨 B 前提・A でも同じ段階）

1. **3.1 PoC**: `suspend fun f(t: Task<Int>): Int = t.await()`（await 1 個・直線）を IAsyncStateMachine で点ける最小 IL。`AsyncTaskMethodBuilder<T>` 呼び順を実機で確定。
2. **3.2 設計検証**: Reflection.Emit の async 落とし穴（struct box、自己参照 generic、呼び順、属性）を設計エージェントで事前確認。
3. **3.3-3.6**: BirEmitter に状態機械合成（`__state`/params/live-locals→fields）＋ CPS lowering（D2.1 移植）＋ ilemit driver（switch/br dispatch）。
4. **3.8 m-d2**（単純 suspend+await）IL parity → **3.9 m-d2-sm**（ループ/分岐内 suspension）IL parity。
5. **残**: 部分式 suspension・ループ条件 suspension は CFG/SSA（E-0.5）後段（C# も未完＝廃止ブロッカーでない）。

## 5. 決定（2026-06-18・自走）

- **戦略 = B（CLR ネイティブ `IAsyncStateMachine`）に確定**。理由は §3（[[clr-not-jvm-discard-jvmisms]] の精神＝JVM の Continuation を持ち込まない、pure-binding、Task ネイティブ統合、デバッガ対応）。
- **フォールバック規律**: B の Reflection.Emit 上の特定の障害（自己参照 generic 等）が実装中に詰んだ場合のみ、**自分の判断で** A（Continuation 合成）へ降ろす。CPS フロント（D2.1 再利用）と ABI（`Task<T>`）は A/B 共通なので切替コストは「ランタイム降ろし層」に限局。ユーザ判断は仰がない（[[autonomous-execution-no-asking]]）。
- 次は §4 の段階で実装着手（3.1 PoC から）。
