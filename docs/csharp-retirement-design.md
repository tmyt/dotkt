# 設計: C# バックエンドの完全廃止（E トラック完遂）

> **✅ 完了（2026-06-18）。** C# の3役すべてを置換: (a) 出荷 emit→純 IL（events/generic/coroutines まで gap ゼロ）、(b) オラクル→`verify-differential` が IL 経路を kotlin/jvm と突合（25 MATCH・C# 非依存）、(c) 意味解決→FIR（両経路同一 IR）。MSBuild 既定 `il`・C# は `KOTLIN_CLR_EMIT_CS=1` の opt-in（出荷経路から Kotlin 由来 .cs/csc ゼロ）。3 ハーネス（verify-il / verify-differential / verify-all）緑＋CI 化。
> **唯一の残**: 5.2 コンパイル時 `<Reference>` retargeting は本トラックのブロッカーではなく（reflection-load 逆interop は動作）、**1.0 出荷タスクへ移管**（`docs/remaining-tasks.md`）。

**目的**: 出荷バックエンドを純 IL（`BirEmitter.kt` → `tools/ilemit`）に一本化し、C# コード生成（`CSharpCodegen.kt` → csc）を出荷経路から外す。本書は「C# を捨てられた」の定義・残タスクの正確な棚卸し・各タスクの設計・着手順を固定する（[[il-primary-backend-pivot]] / [[kotlin-net-1.0-definition]] の E トラック完遂）。

作成 2026-06-18。前提コミット時点で IL バックエンドは 52 サンプル PASS＋ilverify-clean、ユーザ定義 generics・非/ジェネリック .NET 基底継承・generic .NET 型 FIR 注入まで到達済み。

---

## 1. 「C# を捨てられた」の定義（exit criteria）

C# 経路は出荷フォーマットだけでなく**3 役**を兼ねる。各々が置換されたときに捨てられる（docs E §168-172）。

| 役割 | 置換先 | 現状 |
|---|---|---|
| (a) **出荷 emit** | IL parity（本書 §2 の gap ゼロ） | gap が数件残る（§2） |
| (b) **正解器（differential oracle）** | JVM 差分ハーネス（`verify-differential.sh`, kotlin/jvm）＋ `ilverify` | **既に C# 非依存**。verify-il は固定 expected、verify-differential は kotlin/jvm が正解。corpus を IL 経路で全 pure に広げれば完了（§3.6） |
| (c) **意味解決**（オーバーロード/target typing/暗黙変換） | FIR が IR 時点で解決済み。両バックエンドは同一 IR を受ける | **C# は意味解決していない**ことを確認済み（両経路は BIR JSON 分岐前で同一 IR）。引き取り済 |

**Done = 次がすべて真**:
1. 全出荷機能が IL で動作（§2 の機能 gap ゼロ）。
2. 全 pure サンプルが **IL 経路で** kotlin/jvm と stdout 一致（E-2）＋全 IL アセンブリ ilverify-clean。
3. MSBuild `<KotlinClrBackend>` 既定が `il`、出荷経路に csc/C# 依存が無い（E-5）。C# 経路は dev/oracle フラグへ降格（repo には温存可）。

---

## 2. 現状の正確な gap（C# 独占 ＝ IL 未）

> ⚠ 調査の補正: 初回ギャップ調査はサンプル一覧（verify-il が dedicated `il-*` を使う）から「未実装」を誤推定した。実際には **scope 関数（`inlineScope`）・require/error（`il-reqnn`）・LINQ（`il-coll*`）・math/string（`il-math`/`il-str`）・local fn・companion・enum rich は IL に在る**。grep 実測で確認済（events/coroutine は BirEmitter/ilemit ともに 0 ヒット）。

### 2.1 真の C# 独占機能（IL に無い＝出荷ブロッカー）

| gap | C# 実装 | IL 状態 | 規模 | ブロックするもの |
|---|---|---|---|---|
| **コルーチン/suspend** | `CSharpCodegen.kt:358-550`（@Sm 状態機械 CPS）＋`310-313`（suspend→async Task）＋runtime `runtime/csharp/KfcCoroutines/Coroutines.cs` | **ゼロ**（grep 0） | **XL** | `m-d2`, `m-d2-sm`, async UI、構造化並行性 |
| **.NET イベント `+=`/`-=`** | `CSharpCodegen.kt:1205-1207`（`ClrEventRegistry` lookup→`+=`/`-=`） | **ゼロ**（grep 0、ilemit も 0） | **M** | 対話的 UI（ボタン onClick 等）＝windowing の完全 IL 化 |
| **generic .NET メソッド `obj.M<T>()`** | façade 経路（generic façade） | 無（facadegen が `IsGenericMethod` を skip×3） | **S** | injected 型の generic method、LINQ-on-injected |
| **generic .NET indexer `this[i]`**（注入経路） | façade `.kt` には在り（`operator get/set`） | 注入経路は無 | **S** | injected generic collection の添字 |
| **逆 interop 公開面の磨き** | 既存（`revinterop`＝C# が Kotlin assembly を ProjectReference 消費） | IL 出力も通常 .NET assembly＝構造的に可、だが未検証＋公開名/nullability 注釈 未（docs:122） | **S-M** | C#/他言語からの消費体験 |

> PDB/sequence point は本トラックの対象外（出荷ブロッカーでなく、IL 経路は元々 C# から PDB を得ていない＝C# 脱却と独立）。デバッガ体験の別タスクとして本書から除外（2026-06-18）。

### 2.2 検証カバレッジの gap（機能はあるが IL 回帰に未投入）

- pure サンプル `m-a1..m-a8`, `m-b1..m-b13`, `m-s1..m-s3` は **IL で収束済**（[[il-primary-backend-pivot]]「FIR→BIR→IL 完成 25/25」）が、daily `verify-il.sh` は dedicated `il-*` を回す。`verify-differential.sh`（kotlin/jvm 正解）の PURE corpus（m-a*/m-b* を含む）は現状 **既定バックエンド（=cs）** で走る可能性が高い。→ **IL 経路で全 pure を JVM 差分にかける**のが E-2 の実体（機能追加でなく検証の付け替え）。

### 2.3 ブロッカーでないもの（誤解除去）

- **inlining 残**（非ローカル return・可変ローカルキャプチャ・crossinline）= C# 経路も完全には実装していない*non-projecting tail*。C# 廃止の前提条件ではない（IL/C# 共通の将来 TODO＝[[function-inlining-spike]]）。
- **scope 関数・stdlib・LINQ・local fn 等** = 既に IL に在る（§2 冒頭の補正）。

---

## 3. 各残タスクの設計

### 3.1 コルーチンの IL 化（最大・核心）— D/E-1

**ABI は不変**（[[coroutine-abi-decision]]）: `suspend fun f(args): T` ⇔ CLR `Task<T> F(args)`（Unit→`Task`）、`Continuation` は公開 ABI に漏らさない。実装戦略を入れ替えても ABI は固定。

**構造（C# 設計の移植）**: `@Sm suspend fun` → 状態機械クラス。
- フィールド: `int __state`、params→fields、**suspension をまたいで生存するローカル→fields**（`collectCpsVars` 相当を BirEmitter に移植）。
- 駆動メソッド（`ResumeWith`/`MoveNext`）: 先頭で `__state` による **dispatch（IL `switch` opcode＝ジャンプテーブル、C# の `goto __Rk` に対応）**→各 suspension 点で `__state=k` 保存→awaitable 開始（`this` を継続として渡す）→`ret`、再開ラベルで結果読取。
- 制御フロー（if/while 内の suspension）の linearize: C# は `goto`。IL は `br`/`switch` で同じ（既存の labeled-break/goto 機構＝increment㉒ と同型）。**フル CFG は現サブセット（@Sm 制約）には不要**。

**未決の設計判断（要スパイク＋ユーザ確認）— ランタイム戦略**:
- **戦略 A（C# 直接移植）**: Kotlin Continuation runtime（`IContinuation`/`KResult`/`Future`⇄TCS）を使う。**pure-binding 原則**（[[kotlin-net-is-pure-binding]]）に従い、これらの runtime 型を**ユーザアセンブリに合成**（KProperty/KIterator/委譲クラスと同じ機構）。確実だが runtime 型の合成が要る。
- **戦略 B（CLR ネイティブ async）**: .NET の `IAsyncStateMachine` + `AsyncTaskMethodBuilder<T>` を IL で直接生成し、真の CLR async/Task に乗せる（[[clr-not-jvm-discard-jvmisms]] の精神＝JVM 由来の Continuation を持ち込まず CLR の async を使う）。最も idiomatic・GC/Task 統合・runtime 同梱不要だが、IL で IAsyncStateMachine を正しく吐くのは精緻（`AsyncTaskMethodBuilder` の `Start`/`AwaitUnsafeOnCompleted`/`SetResult`、`[AsyncStateMachine]` 属性、struct 状態機械の box 規律）。
- **推奨**: **B を第一候補**として設計スパイク（最小 `suspend fun f() = task.await()` を IAsyncStateMachine で点ける）。困難なら A にフォールバック（C# 設計の移植で確実）。**着手前に `docs/coroutine-il.md` で固定し、ユーザ判断を仰ぐ**（[[design-first-on-hard-features]]）。
- **段階**: ① 単純 suspend（直線、await 1〜N）→② ループ/分岐内 suspension（@Sm の現サブセット）→③ 部分式 suspension・ループ条件 suspension（**CFG/SSA = E-0.5 が要る**＝後段）。①②で m-d2/m-d2-sm parity、③以降は新規 breadth。

### 3.2 .NET イベント `+=`/`-=` の IL 化 — C-track / E-1

frontend は既に整備済（FIR injector が `add_<E>`/`remove_<E>` を合成、`ClrEventRegistry` に (event名, op) を記録）。残りは backend のみ。
- **BirEmitter**: call が injected `add_`/`remove_` のとき（`ClrEventRegistry.lookup`）→ 新ノード `clrEventAdd`/`clrEventRemove`（`type`=.NET 型, `event`=イベント名, `recv`, `handler`=delegate 式）。handler は既存の lambda→delegate 経路（`closureNew`/`delegateInvoke`）。
- **ilemit**: `ResolveType(type).GetEvent(name).GetAddMethod()/GetRemoveMethod()` を `callvirt`、引数は handler delegate。
- これで `class App : Application()` 上で `button.Click += { … }` 相当が IL で動く＝**windowing の完全 IL 化**（基底継承は Round 8 で済、イベントが最後のピース）。Avalonia/WPF サンプルを IL で点灯。

### 3.3 generic .NET メソッド/indexer — C-track

- **generic method**: facadegen が `m.IsGenericMethod` を skip 中。メタデータに generic method（型パラメータ名）を吐き、injector が generic method を合成（`createMemberFunction` の `returnTypeProvider` でメソッド型パラメータを宣言＝Round 9 の class 型パラメータと同型）。backend は呼出時 `MakeGenericMethod`（既存）。
- **indexer（注入経路）**: 注入型の `get_Item`/`set_Item` を `operator get/set` にマップ（façade `.kt` 経路に既存ロジック＝facadegen GenerateType:220-229 を注入経路へ）。

### 3.4 逆 interop 公開面の磨き — C-track refinement（docs:122）

IL 出力は通常の .NET アセンブリ＝C# から `ProjectReference` で消費可能（`revinterop` は構造的に動くはず）。残: 公開名の C# 慣習化（任意）、**nullability 注釈 `[Nullable]`**、generics の公開形。IL 経路で `revinterop` を検証するサンプルを追加。

### 3.6 オラクル常設 — E-2

- `verify-differential.sh` を **IL 経路で**全 pure corpus（m-a*/m-b*/m-s*）にかけ kotlin/jvm と一致を assert。`ilverify` を全 IL アセンブリに常設（既に verify-il で実施）。CI 化（F-track）。
- これが揃った時点で**正解器としての C# が完全に不要**。

### 3.7 出荷除去 — E-5

- `msbuild/KotlinClr.targets`: `<KotlinClrBackend>` 既定 `cs`→`il`（:17）。`KotlinClrCollect`（C# を compile set に追加, :79-84）を非既定化、`KotlinClrIlEmit`（:105-112）を既定経路に。il 経路の placeholder-C# bootstrap（csc に stub を作らせ ilemit で上書き）はそのまま or 簡素化。
- `compiler:run` から C# codegen を外す（dev-only フラグ `--emit cs` に降格）。`CSharpCodegen.kt` は **repo に温存**（壊れた時の参照・oracle、`il-primary-backend-pivot` の方針）。
- ドキュメント・サンプル・`dotnet new ktproj` テンプレートを IL 既定に。

---

## 4. 着手順（依存順）

1. **イベント `+=`/`-=`**（M, 独立・即着手可）→ windowing の完全 IL 化を解禁。Avalonia サンプル点灯で「純 IL で実 UI」を実証。
2. **generic .NET メソッド/indexer**（S, Round 9 generic 注入の延長）。
3. **コルーチン IL 化**（XL）: 設計スパイク（戦略 A/B 決定、`docs/coroutine-il.md`）→ 単純 suspend → ループ/分岐 suspension（m-d2/m-d2-sm parity）。**最大の山**。
4. **E-2 検証拡張**（pure corpus を IL 経路で JVM 差分＋ilverify 常設、CI 化）。
5. **逆 interop 検証 + nullability 注釈**（S-M）。
6. **E-5 出荷除去**（MSBuild 既定切替＋C# 経路を dev/oracle へ降格）。

**部分式/ループ条件 suspension** と **inlining 残**（非ローカル return 等）は CFG/SSA（E-0.5）を要し、コルーチン後段＋[[function-inlining-spike]]。これらは C# も未完なので **C# 廃止のブロッカーではない**（廃止後の共通 breadth）。

---

## 5. リスク / 未決事項

- **コルーチン runtime 戦略（A: Continuation 合成 / B: CLR-native IAsyncStateMachine）** = 最大の未決。設計スパイクで決め、ユーザ確認（[[design-first-on-hard-features]]）。
- IAsyncStateMachine を IL で吐く精緻さ（struct 状態機械の box 規律、AsyncTaskMethodBuilder の正しい呼び順）= 戦略 B のリスク。Reflection.Emit の落とし穴は設計エージェントで事前検証（Round 5/9 と同じ運用）。
- pure-binding 原則（runtime 同梱禁止）と coroutine runtime の両立 = 戦略 A なら合成、B なら不要。
- 「C# を消すと参照実装が消える」リスク → C# 経路は repo に温存（出荷から外すだけ）。

---

## 6. 完了判定（このトラックの Done）

- [x] イベント `+=`/`-=` が IL で動作（il-event 実機正＋ilverify-clean, 2026-06-18）。Avalonia 実点灯は E-5 サンプル整備時。
- [x] generic .NET メソッド/indexer が注入経路で動作（il-netgen3 実機正＋ilverify-clean, 2026-06-18）。generic instance は実装済・回帰はフェーズ5。
- [x] コルーチン: suspend fun→CLR-native IAsyncStateMachine（戦略B）が **IL で**動作（il-coro 実機正＋ilverify-clean, 2026-06-18）。try/catch-around-await 等はクリーンエラー（E-0.5 後段、C# 手書きも未対応）。
- [x] 全 pure サンプルが **IL 経路で** kotlin/jvm と一致（E-2、25 MATCH）。全 IL ilverify-clean。CI 化（`.github/workflows/verify.yml`）。
- [x] MSBuild 既定 `il`、出荷経路に csc/C# 依存ゼロ（`ClrBackendPhase` は既定 BIR のみ、C# は opt-in）。
- [x] 逆 interop（.NET ホストが IL 出力アセンブリを reflection 消費）を検証（`il-revinterop`）。コンパイル時 `<Reference>`（5.2）は 1.0 出荷タスクへ移管（[[kotlin-net-1.0-definition]] / `docs/remaining-tasks.md`）。

**→ E トラック完了。** 残る 5.2 は脱却ブロッカーでなく 1.0 出荷の磨きとして `docs/remaining-tasks.md` に移管。

---

## 7. タスクリスト（実行用・粒度細）

各タスクに**受入基準**を付す。`[ ]` 未着手 / `[~]` 着手中 / `[x]` 完了。フェーズは §4 の依存順。

### フェーズ 1 — イベント `+=`/`-=`（M・即着手可）✅ 2026-06-18 完了
- [x] **1.1** BirEmitter: `ClrEventRegistry.lookup(declFq, name)` で injected `add_`/`remove_` 呼出を検出。`declFq` = resolveFakeOverride 経由の実 .NET 宣言型 FQN。
- [x] **1.2** BirEmitter: `clrEventAdd`/`clrEventRemove` ノードを emit（`type`=構築済 .NET 型, `event`=イベント名, `recv`, `handler`=既存 lambda→delegate 経路の delegateNew/closureNew or 保存ローカル）。
- [x] **1.3** ilemit: `EmitClrEvent`＝`ClrRef(type).GetEvent(name).GetAddMethod()/GetRemoveMethod()` を `callvirt`。`EmitHandlerAsDelegate` がハンドラを**イベント固有のデリゲート型**へバインド（リテラルは直接、保存値は `Invoke` 経由で再ラップ＝デリゲート等価性を保ち `-=` を成立）。
- [x] **1.4** サンプル `samples/il-event`（`ObservableCollection<Int>` の `CollectionChanged` を `+=`/`-=`、同期発火）＋ `verify-il.sh` 投入（`il:event` PASS, `VERIFY event` clean）。
- [~] **1.5** Avalonia/WPF サンプルを **IL 経路で点灯**: イベント機構は IL で完成（基盤確立）。実 UI 点灯は Avalonia アセンブリ注入（`--refs`/`<KotlinClrType>`）の IL 経路配線が要るため E-5 サンプル整備時に実施。
- **受入**: ✅ il-event 実機正＋ilverify-clean（53 サンプル回帰なし）。windowing 完全 IL 化の中核（イベント）は IL で動作。

### フェーズ 2 — generic .NET メソッド/indexer（S）✅ 2026-06-18 完了
- [x] **2.1** facadegen `--meta`: generic method を skip せず出力。`fun <Name> <ret> <open|final> [<TP>...] [<p>:<t>]*`（bare 末尾トークン＝メソッド型パラメータ、`:`付き＝値パラメータ）。ret/param が `T` なら Map がパラメータ名を返す。
- [x] **2.2** injector: generic method を `createMemberFunction(returnTypeProvider)` ＋ `typeParameter(...)` で合成。ret/param の `T` 参照を `coneOfMethod`（メソッド型パラメータ→provider 形）で解決。
- [x] **2.3** backend: `callee.typeParameters.isNotEmpty()` を検出し `clrGenericStatic`（static）/`clrGenericInstance`（instance）を emit。ilemit `ResolveGenericMethod`（name+型アリティ+param shape）→ `MakeGenericMethod`（既存 LINQ 経路を一般化、`instance` フラグ追加）。
- [x] **2.4** 注入型 indexer: meta `index <idxT> <valT> <ro|rw>` → injector が `operator fun get/set`（`status{isOperator=true}`）合成。backend は `get`/`set` operator を構築済 .NET 型の `get_Item`/`set_Item`（clrInstance）へ。
- [x] **2.5** サンプル `samples/il-netgen3`（`Unsafe.SizeOf<Int/Long/Double>`＝4/8/8、`RuntimeHelpers.IsReferenceOrContainsReferences<Int/String>`＝False/True、`Collection<Int>` の `c[i]`/`c[i]=v`）＋ verify-il 投入。
- **受入**: ✅ 実機正＋ilverify-clean。**注記**: generic **instance** メソッドは `clrGenericInstance` を実装済（static 経路の `MakeGenericMethod` コアを共有する忠実なミラー）だが、BCL にプリミティブ signature の generic インスタンスメソッドが皆無のため専用回帰テストは無し→フレームワーク型を `--refs` で注入するフェーズ5 で回帰投入。

### フェーズ 3 — コルーチン IL 化（XL・最大）✅ 2026-06-18 完了（戦略B）
- [x] **3.1** 設計スパイク: 戦略 B PoC（`/tmp/smpoc`）で `suspend fun f()=await t` を struct `IAsyncStateMachine` として `PersistedAssemblyBuilder` で emit→実機 `42`＋ilverify-clean。自己参照 generic（`Start<TSM>`/`AwaitUnsafeOnCompleted<TAwaiter,TSM>` の TSM=TypeBuilder 自身）が動くと実証。`docs/coroutine-il.md` に固定。**戦略Bに確定（自走判断）**。
- [x] **3.2** PoC で Reflection.Emit の async 規律（struct SM、AwaitUnsafeOnCompleted の呼び順、ref this＝value-type の ldarg.0）を実機確認。
- [x] **3.3** BirEmitter: `fn.isSuspend` 検出→`suspendMethod`（params/live-locals→cpsFields）。
- [x] **3.4** BirEmitter: `collectCpsVars` を移植（suspension をまたぐローカルの field 昇格）。
- [x] **3.5** BirEmitter: CPS lowering（`emitCps`/`emitWhenCps`/`emitWhileCps` 移植）→ フラットな `coSuspend`/`coLabel`/`coGoto`/`coCondGoto`/`coReturn` ステップ列。
- [x] **3.6** ilemit: `EmitCoroutine`＝struct SM 合成＋`AsyncTaskMethodBuilder<T>` プロトコル＋`__state` の `beq` dispatch＋cpsField リダイレクト。kickoff は Create/Start/return Task。`suspend ()->T` ラムダ⇔`Func<Task<T>>`（ABI）。`--ref` で外部ランタイムをロード。
- [x] **3.7** ランタイム不要（戦略B＝`AsyncTaskMethodBuilder` 直生成、pure-binding 維持）。
- [x] **3.8/3.9** サンプル `samples/il-coro`（線形マルチawait・param→field・直接suspend呼出・ループ内suspension・分岐内suspension）＝m-d2/m-d2-sm の手書きlowering能力を **IL で** カバー、実機正＋ilverify-clean。
- **受入**: ✅ il-coro 実機正＋ilverify-clean、ABI（`Task<T>`）維持。**残（CFG/SSA=E-0.5 後段、C# も手書きlowering未対応）**: try/catch-around-await（例外リージョン）・部分式 suspension・ループ条件 suspension → クリーンエラー（`coUnsupported`）。

### フェーズ 4 — E-2 オラクル常設（M）✅ 2026-06-18（4.1/4.2 完了、4.3 任意）
- [x] **4.1** `verify-differential.sh` を **IL 経路（BIR→ilemit）で**全 pure corpus（m0/m-a*/m-b*/m-s*）にかけ kotlin/jvm と一致 assert（C# r.csproj 経路を撤去）。
- [x] **4.2** `ilverify` を全 IL アセンブリに常設（verify-il 拡張済、coro 含む）。
- [x] **4.3** CI 化 ✅ — `.github/workflows/verify.yml`（push/PR で verify-il + verify-differential + verify-all、.NET 10 + JDK 21 + gradle cache + dotnet-ilverify）。
- **受入**: ✅ 全 pure サンプルが **IL 経路で** kotlin/jvm 一致（25 MATCH）、全 IL ilverify-clean、3 ハーネスを CI 化。

### フェーズ 5 — 逆 interop（S-M）✅ 2026-06-18（5.1 完了、5.2 はアーキ制約でブロック）
- [x] **5.1** IL 出力を **.NET（C#）ホストが消費**：`samples/il-revinterop`（C# `Program.cs` が `KotlinLib.dll` を reflection ロードし `Greeter("World").greet()`＝"Hi, World"、`LibKt.add(2,3)`＝5 を呼ぶ）＋ verify-il `il_revinterop`。IL 出力が**一級の消費可能 .NET アセンブリ**であることを実機実証。
- [ ] **5.2 コンパイル時 `<Reference>`（根本ブロック・要 Reflection.Emit 新 API）**：ilemit は BCL を runtime reflection 型で解決するため、出力の **CoreLib 型全部（Object/String/`List`/`Dictionary`/`Task`…）が単一の `System.Private.CoreLib` AssemblyRef を共有**。C# の コンパイル時 `<Reference>` には型ごとに**正しいコントラクトアセンブリ**（Object/String→System.Runtime、`List`→System.Collections…）への分離が要る。
  - **正攻法 MetadataLoadContext は不適合と実証**：MLC のジェネリック型/メソッドに**ユーザ TypeBuilder 型引数**を渡すと "not loaded by the MLC" 例外＝lambda→`Func<UserT>`・closure・`List<UserT>`・コルーチン `Start<SM>` 等が全滅。
  - **単一 AssemblyRef の PE 書換も不可と実証**：単一 CoreLib ref を System.Runtime に向けると `Object`/`String` は通るが `List<T>` が `TypeLoadException`（System.Runtime は List を forward しない＝System.Collections の管轄）。→ **撤回**。
  - 残された道＝型ごとの per-ref retargeting（メタデータ全面再構築）か、Reflection.Emit の参照アセンブリ対応 API 待ち。**C# 廃止のブロッカーではない**（reflection 消費は可・出荷経路は IL）。`[Nullable]` 注釈も後段。
  - **→ 1.0 出荷タスクへ移管（2026-06-18）**: `docs/remaining-tasks.md`（[[kotlin-net-1.0-definition]]）に技術的所見ごと積み直し。脱却トラックからは外す。
- **受入**: ✅ .NET ホストが IL 出力アセンブリの class/top-level を呼べる（reflection、実機正）。コンパイル時 `<Reference>` は 1.0 出荷タスク。

### フェーズ 6 — （PDB は本トラックから除外, 2026-06-18）
PDB/sequence point は C# 脱却のブロッカーでなく（IL 経路は元々 csc から PDB を得ていない）、デバッガ体験の別タスクとして本書から除外。

### フェーズ 7 — E-5 出荷除去（S）✅ 2026-06-18 完了
- [x] **7.1** MSBuild `<KotlinClrBackend>` 既定を `cs`→`il`（`msbuild/KotlinClr.targets`）。`StartupObject` を持つ .ktproj も il で動くよう、il プレースホルダ target が csc 用に `StartupObject=_IlPlaceholder` へ上書き（ilemit が最終 entry を設定）。
- [x] **7.2** `KotlinClrCollect`（cs のみ）/`KotlinClrIlEmit`（il 既定）は条件分岐済。既定が il なので IL 経路が標準。
- [x] **7.3** `ClrBackendPhase` の C# codegen を **opt-in**（`KOTLIN_CLR_EMIT_CS=1`）に降格。既定は BIR のみ出力＝CSharpCodegen を一切呼ばない（出荷経路から C# 完全排除、IL 専用機能が凍結済 C# backend を破壊しない）。
- [x] **7.4** `samples/ktproj`（StartupObject 付き）が IL 既定で実機正。`verify-all.sh` は C# オラクルとして明示 `KotlinClrBackend=cs`＋`KOTLIN_CLR_EMIT_CS=1` を export し継続。
- [x] **7.5** `CSharpCodegen.kt` は repo 温存（呼出を opt-in 化しただけ）。
- **受入**: ✅ 出荷経路に csc/C# 依存ゼロ（既定ビルドは BIR→ilemit のみ、Kotlin 由来 .cs を生成しない）、`samples/ktproj` 既定 il で緑、`KotlinClrBackend=cs` は明示時のみ。

### 横断（全フェーズ共通の規律）
- [ ] 各機能追加は `verify-il.sh` に専用サンプルを投入し、**実機正＋ilverify-clean** を必須ゲートに。
- [ ] 大物（コルーチン）は着手前に設計を `docs/` に固定（[[design-first-on-hard-features]]）。
- [ ] JVM 差分が取れる pure 機能は `verify-differential.sh` にも投入し kotlin/jvm 一致を確認。
