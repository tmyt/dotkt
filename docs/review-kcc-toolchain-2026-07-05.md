# kcc ツールチェーン レビュー報告（2026-07-05, レビューチーム統合）

> **レビュー体制**：リーダー（統合・横断）＋ 各レイヤー専門エージェント（kotc / bir2cir / ilemit /
> facadegen+retarget、全員 codex を併用）＋ codex（アーキテクチャ横断）。
> **手法**：前回レビュー `docs/polish-review-layer-purity.md`（2026-07-04）を鵜呑みにせず、**全所見を現行コードで
> 再検証**（grep＋codex トレース＋ゲート確認）。行番号は 2026-07-05 時点の `main`（`9ad6465`）基準。

---

## 総評（結論）

**ツールチェーンの健全性は高い。** 全ゲートが真に XFAIL-zero（`verify-il.sh` の `XFAIL_RUN`/`XFAIL_ILVERIFY` マップは
両方とも空を確認）、技術的負債マーカーは全ツールチェーン ~24k 行で 22 件のみ（kotc 6 / bir2cir 3 / ilemit 2 /
facadegen 1 / retarget 0）、4層アーキテクチャの純度移行はほぼ完了。前回レビューが挙げた「サイレント劣化」の
見出し級項目は**軒並み loud throw 化済み**（後掲「修正済みと再確認」参照）。

残る実質的な問題は少数に収束する。**もし1つだけ直すなら H1（Rule-4 ゲートなし動的ディスパッチ）** — これが唯一の
「clean にコンパイルが通って実行時に不透明クラッシュする」正真正銘の正当性リスク。他は ABI 忠実度の穴1件、
ilemit のデッドコード一掃、kotc の小さな純度是正1件、facadegen の不変条件のレイヤー内未強制、および staleness。

### 重大度サマリ

> **本書は 2 部構成。** **Part 1（下記）＝静的レビュー**：レイヤー純度・failure posture・デッドコード・coroutine 整合性。
> **[Part 2](#part-2--観点拡張レビュー実行時-correctness--coverage--il品質2026-07-05)＝観点拡張レビュー**：実際に動かして
> 検証した**実行時 correctness（15 の確定 miscompile、うち C1 は CRITICAL）**・**coverage の構造的欠陥（COV1）**・IL品質。
> **出荷品質に直結するのは Part 2**（ユーザーの正しい Kotlin コードが誤結果/クラッシュ/サイレントなデータ損失を起こす）。
>
> **件数について（Part 1）**：下表の 9 行は**グルーピングされたトップレベル・バケット**であり、各行は複数の個別所見を束ねる。
> Part 1 で列挙している**個別の指摘は約 27 件**（H1=2 / H2=1 / M1=2 / M2=6 / M3=8 / L1=1 / L2=5 / L3=1 / X1=1）。
> Part 1 のスコープは **①レイヤー純度 ②failure posture ③デッドコード ④coroutine 整合性** に限定。
> 「一般的な機能バグの網羅的ハント（振る舞い差分・エッジケース・性能）」は **Part 2** で実施した。

| ID | 重大度 | 所見 | レイヤー | 種別 |
|----|--------|------|----------|------|
| H1 | **High** | Rule-4 → ゲートなし動的ディスパッチ → 実行時 NRE | bir2cir → ilemit | 正当性 / make-it-loud |
| H2 | Med-High | suspend 関数型の POSITION メタデータ往復欠落 | bir2cir + ilemit | ABI 忠実度 |
| M1 | Med | ilemit デッドコード（約 59 ケース＋6 ヘルパ） | ilemit | 掃除 / no-compat |
| M2 | Med | kotc 残存 CLR 知識（うち 1 件は clean な是正対象） | kotc | レイヤー純度 |
| M3 | Med | facadegen `kotlin.*` 不変条件がレイヤー内で未強制 | facadegen | 不変条件 / 防御 |
| L1 | Low-Med | suspend intrinsic をエラーメッセージ文字列で検出 | bir2cir (+kotc) | 脆さ |
| L2 | Low | staleness クラスタ（死語エントリ・古コメント） | 全般 | 掃除 |
| L3 | Low | transformability fixpoint のサイレント drop（診断品質） | bir2cir | 診断品質 |
| X1 | Low | `dotkt-out/` の DLL 90 個が git 追跡・未 ignore | リポジトリ | 衛生 |

---

## 重大度順 所見（重複排除済み）

### 🔴 H1 [High] Rule-4 → ゲートなし動的ディスパッチ → 実行時 NullReferenceException
- **場所**：bir2cir `toolchain/bir2cir/Program.cs:4518`（Rule 4 最終フォールスルー）→ ilemit
  `toolchain/ilemit/Program.cs:3037`（`EmitDynamicCall`）
- **裏付け**：codex #1・bir2cir エージェント（HIGH）
- **内容**：bir2cir の Rule 1–5 で解決できなかったメンバは **Rule-4 が無条件に `clrInstance` を吐く**。ilemit 側の
  `clrInstance` フォールバック（`Program.cs:3037`）は、兄弟の `callInstance` 経路
  （`Emitter.Expressions.cs:172`、`dynRet && OwnerHasClrInterface` でゲート）と違い**ゲートが無い**ため、静的解決に
  失敗すると常にリフレクション（`recv.GetType().GetMethod(name)`、**シグネチャ照合なし**）へ落ちる。
- **発火条件**：`@ClrTypeAlias` の付いた owner 上の、束縛（`@ClrIntrinsic`/`@ClrProperty`）も BCL 等価も無い小文字
  Kotlin メンバ（stdlib の束縛漏れ・タイポ）。→ `GetMethod(name)` が null → 続く `MethodInfo.Invoke` で
  **メソッド名すら出ない不透明な NullReferenceException**（`MissingMethodException` ですらない）。
- **なぜ重大**：Rule-4 は「意図した動的ディスパッチ（`MutableCollection.addAll/removeAll/retainAll` の `ICollection`
  経由）」と「ルーティング漏れ」を区別できない。両方 clean に通り、漏れだけが遠く離れた実行時クラッシュになる。
- **是正（make-it-loud）**：
  1. ilemit の `clrInstance` フォールバック（`Program.cs:3037`）を `callInstance` 同様 `OwnerHasClrInterface` で
     ゲートし、非該当ミスは emit 時 throw（`Program.cs:3038`）に。
  2. bir2cir Rule-4 は「インターフェイス裏付けの無い CLR-bound owner 上の小文字メンバ」を `owner.member` を名指しで
     コンパイル時 refuse。PascalCase 名・`get_`/`set_` アクセサ・`clr:`/`clrg:` インターフェイス実装 owner のみ許可。
- **副次**：この修正は **`CollElemArg → "object"` → `clrCollAdd<object>` → 実行時 EntryPointNotFound 残渣
  （`Program.cs:4661` フィード `Program.cs:4388`）も同時に包摂**する（非インライン汎用コレクション構築の element 型
  回収失敗ケース。common なインライン `mapTo`/`filterTo`/`groupValues` は commit a8fab5b で修正済み）。

### 🟠 H2 [Med-High] suspend 関数型の POSITION メタデータ往復欠落
- **場所**：ilemit `Emitter.CompilerServices.cs:46-64`（`EnsureKotlinAttrs`）／bir2cir `Program.cs:647,1934`
  （`sfunc:` → `object`/`func:` への畳み込み）／メソッド属性 ilemit `Program.cs:645,649`
- **裏付け**：codex #2・ilemit エージェント（Medium）・bir2cir エージェント
- **内容**：suspend のメタデータは**メソッド階層の `[KotlinFunction(Suspend)]`（flag 4）のみ**。param/return/
  property/field 位置の `suspend (…) -> T` は bir2cir で型スロットが畳まれ、**suspend 由来を刻む属性がどこにも
  出ない**（`KotlinSuspendFunctionTypeAttribute` が存在しない）。
- **影響**：`fun run(block: suspend () -> T)` の引数は CLR 上 `Func<…,Task<T>>` になり、**CLR 由来の普通の
  `Func<…,Task<T>>` と区別不能**＝別 DotKt アセンブリからの再消費で suspend 型が復元できない（ABI 忠実度の穴）。
  頻度は低いが公開 API 面で実在。
- **是正**：bir2cir が位置別 suspend-origin ファクトを CIR 契約として出し、ilemit が
  `KotlinSuspendFunctionTypeAttribute` を新設して `ParameterBuilder`/return/property/field に刻む（既存の
  `Nullable`/`NullableContext` 位置属性機構を踏襲）。クロスレイヤーの中期作業。

### 🟡 M1 [Med] ilemit のデッドコード負荷（約 59 ケース＋6 ヘルパ）
- **場所**：ilemit `Emitter.Expressions.cs` / `Program.cs`
- **裏付け**：ilemit エージェント筆頭所見・codex 追認（grep＋codex で producer ゼロ確認）
- **内容**：
  - **到達不能な `clr.*` 物理ノード 38 ラベル**（`clr.const`/`clr.bin`/`clr.newobj`/`clr.call`/`clr.ldfld`/
    `clr.stelem`/`clr.enum.*`… kotc/bir2cir に producer ゼロ。kotc は非ドットのセマンティック kind を emit）。
  - **専属デッドヘルパ 6 個**：`EmitNativeClrNewObj` / `EmitNativeClrCall` / `EmitNativeClrFieldGet` /
    `EmitNativeClrFieldSet` / `EmitNativeClrIsInst` / `EmitNativeClrCastClass`（`clr.*` ケースからのみ到達）。
    ※他 11 個の `EmitNativeClr*` は plain ケースと**共用で生存 → 残す**。
  - **到達不能な retire-list 21 ケース**：`nullableOf`/`strRepeat`/`split`/`associateWith`/`associateBy`/
    `groupBy`/`linqPartition`/`linqWithIndex`/`linqAssociate`/`linqScan`/`linqWindowed`/`linqGetOrElse`/
    `listGet`/`listSet`/`mapGet`/`mapSet`/`mapSize`/`linqSum`/`linqSumOf`/`tupleNew`/`tupleItem`。
- **残す（生存確認済み）**：`strReversed`（`Emitter.Expressions.cs:605`、kotc `BirEmitter.kt:4032` が現状 emit）、
  `listNew`/`setNew`（計算 `kind` 経由、`BirEmitter.kt:3283`）、`mapNew`（`BirEmitter.kt:3309`）。
- **害**：ilemit の真の CIR 表面を誤認させ、no-compat 方針に反する「旧 CIR 方言」を黙認する。1スイープで削除可、
  ゲートは XFAIL-zero のまま（誰も到達しない）。`docs/master-task-inventory.md` 【1】② が既にスコープ化。

### 🟡 M2 [Med] kotc に残る CLR 知識 — うち1件はクリーンな是正対象
- **裏付け**：kotc エージェント・codex #3（一部補正）
- **✅ 是正対象（見落とし・安全・gate-neutral）**：`s.length` on `kotlin.String` → `System.String.Length` を直書き
  （`BirEmitter.kt:3727-3731`）。stdlib `@ClrIntrinsic("Length")`（`libraries/stdlib/clr/builtins/String.kt:33`）＋
  bir2cir `MemberCallSubstitution`（`Program.cs:4421/4867`）で**完全に冗長**（兄弟の `String.get`→`get_Chars` は既に
  同様にクリーン化済み `BirEmitter.kt:3479`）。分岐を削除して汎用プロパティ経路に落とすだけ。**今回一番明快な純度改善**。
- **ブロック中の意図的キープ（根本原因は他レイヤー、kotc 側は外せない）**：
  - `toString(radix)` → `System.Convert.ToString`（`BirEmitter.kt:3863-3874`）。
    **⚠️ 訂正（Part 2 / stdlib エージェントが実測）**：この行の in-code コメント「stdlib 本体が radix>10 で誤コンパイル」は
    **STALE（誤り）**。stdlib actual `StringNumberConversionsClr.kt:58-89` は**正しい**（同一本体の in-module 複製は
    `ff`/`-ff`/`z`/`-80000000` を正しく出す）。実際は **kotc の特例が正しい stdlib 本体を隠しており**、`System.Convert.ToString`
    自体が (a) 負数を2の補数で出す（`(-255).toString(16)="ffffff01"`）(b) 基数∉{2,8,10,16} で**クラッシュ**（`Invalid Base`）。
    → **是正は「kotc 特例を削除して stdlib actual にディスパッチさせる」**（cardinal-rule 違反の除去）。Part 2 C4 参照。
  - `strReversed`（`BirEmitter.kt:4029-4033`）— stdlib の `StringBuilder(CharSequence)` ctor 欠落。根本原因は **stdlib**。
- **Low**：`Any`/enum 普遍メソッド → `System.Object` スロット名 `GetHashCode`/`ToString`/`Equals`
  （`:3428/3852-3861/4346-4362`）；`kotlin.clr.Span` → `clrg:System.Span`（`:4422-4424`、`@ClrTypeAlias` 無しの
  ハードコードだが `kotlin.clr` compiler-intrinsic 面で境界的、`ClrRef`→`byref:` の peer）；A5 プリミティブ形状
  （`BirMappings.kt:107-115`、`nullable:int`/array `elem:` — 追跡済み・今日は no-op）。
- **✅ 補正（codex 過剰報告の是正）**：codex が違反とした**注入インデクサ `get_Item`/`set_Item` は正当な facadegen
  相互運用**（`clrInteropName != null` でゲート、kotlin.* 漏れではない）。同様に `.NET` イベント `add_/remove_`、
  数値 `toInt/toLong`→`conv`（プリミティブ IL op）、プリミティブ `bin`/`un`/`ceq` も **LEGIT**。
- **failure posture**：良好。`unsupported()` は `hadError`＋`file:line` 付き ERROR、`ClrBackendPhase` が各ファイルの
  emit を try/catch でクラッシュも ERROR 化。サイレント劣化する kotc サイトは無し。

### 🟡 M3 [Med] facadegen: `kotlin.*` 不変条件がレイヤー内で未強制
- **場所**：facadegen `Program.cs:217-218`（seed）/`:229-232`（closure）/`:784-801`（`NO_INJECT`/`ShouldInject`）
- **裏付け**：facadegen エージェント（Medium）・codex #4 を精密化
- **内容**：seed も closure も `kotlin.*` 除外を持たず、不変条件は**下流の `ClrTypeInjection.kt:330`
  （`!dotNetName.startsWith("kotlin.")`、ただし injected クラス/インターフェイスのみ・top-level 関数は非対象）＋
  「stdlib を `--scan-asm` しない」運用規律**でのみ担保。今日は安全だが、**保証がそれを所有すべきレイヤーの外**にあり、
  しかも `docs/master-task-inventory.md` 【3】(6)「closure は kotlin.* 除外」の記述は下流限定で不正確（ドキュメントギャップ）。
- **是正**：`ShouldInject` と seed 解決に `kotlin.*` 短絡を追加（意図的な `kotlin.clr.await` ブリッジ `:268-272` は
  ホワイトリスト）。source-of-truth で BINDING invariant を強制（defense-in-depth、現状リグレッションではない）。
- **併記（同レイヤーの小所見）**：
  - 未解決シグネチャ型が診断なしで `Any?` に静かに劣化（`:1353` の `Map` 既定、`:1391-1393` の `CrossType`
    フォールバック）＝弱いオーバーロード解決に化ける posture 問題。member 位置の `Any?` 降格に `note:` を出す＋
    wrapper が facadegen stderr をビルドログへ流す（wrapper 変更、レイヤー外）。
  - `System.Nullable\`1` の迷子注入（`NO_INJECT` 未登録、`:784-787`）— 無害だが `Span\`1` と非一貫（Low）。
  - stale コメント：`clrgen`（`:103`）、`func:<ret>:<arg>`（`:1308`、実際は bracketed `func:[…]`）（Low）。
  - vestigial な `@ClrIntrinsic`/`@ClrTypeAlias` 読み戻し経路（`:280-284/338-340/386-399`）— ref.dll を scan しない
    production 経路では常に null。削除前に bir2cir 所有者と確認（Low）。
- **retarget（`toolchain/retarget/Program.cs`）**：健全。CoreLib→contract の repoint は `GetTypeReferences()` で網羅的。
  稀ケースのみ — 未解決 CoreLib TypeRef のフォールバック `System.Runtime` に PublicKeyToken 欠落
  （`:95-97`、well-known ECMA PKT `b03f5f7f11d50a3a` を既定に）（Low）。forwarder 経由のみ到達する型が
  `System.Runtime` にフォールバックするのは通常正しい（`:166-172`、警告あり・情報）。

### 🟢 L1 [Low-Med] suspend intrinsic をエラーメッセージ文字列で検出
- **場所**：bir2cir `SuspendColdLowering.cs:113-127`（`SuspendIntrinsicMarker = "suspendCoroutineUninterceptedOrReturn
  is intrinsic"`、`IsSuspendIntrinsicBlock`）
- **裏付け**：bir2cir エージェント・codex #5
- **内容**：安定フラグ `suspendIntrinsic:true` を優先読みするが kotc が未刻印のため、生きている経路は stdlib の
  ダミー本文**メッセージ文字列一致**。硬化（型名非依存）＋集約済みで、文言が変わって検出失敗しても ilemit で loud
  （サイレントハングではない）だが、stdlib 文言（`Intrinsics.kt:43`）に結合。
- **是正**：kotc が `suspendIntrinsic:true` を刻む（レシーバ側は既に読む）→ 文字列経路は死重として削除可能（kotc 側 1 行）。

### 🟢 L2 [Low] staleness クラスタ
- bir2cir `LambdaKinds` の死語 `steps`/`coClass`（`SuspendColdLowering.cs:82`、`sequenceNew` は除去済み）。
- `InteropBridgeFileClass` の stale `delay` コメント＋file-class 単位の粗いスキップ（`SuspendColdLowering.cs:68-73`、
  `delay`/`blockOn` は stdlib から削除済み。`await` マーカーへ絞るのが理想）。
- ilemit の `cps-field` コメント（`Program.cs:1128`、CPS 概念は消滅）。
- kotc の `native-cir`/`compat-passthrough` コメント（`BirEmitter.kt:926-927`、dual-track は 2026-06-30 削除）。
- ilemit に残る唯一の Kotlin 名ハードコード `kotlin.collections.Iterator`＋`iterator`/`hasNext`/`next`
  （`Emitter.ReverseBridge.cs:24-26,129`、封じ込め済み・理想はセマンティックマーカ駆動）。

### 🟢 L3 [Low] transformability fixpoint のサイレント drop（診断品質）
- **場所**：bir2cir `SuspendColdLowering.cs:262-276`
- **裏付け**：bir2cir エージェント
- **内容**：suspend fun 内に解決不能な suspend 呼び出し（catch/finally サスペンド、未解決 cold shape 等）があると、
  その fun を `transformable` 集合から**サイレントに落とす**が、bir2cir 側の診断は出ない。app ビルドでは ilemit 境界で
  loud（H1 修正済みの `Program.cs:989` throw）になるので**サイレントハングではない**が、ilemit エラーは「生き残った
  メソッド」しか名指しせず、bir2cir が諦めた**具体的な呼び出し／shape** を指さない。
- **是正**：drop サイトで理由（どの呼び出し・どの shape で諦めたか）を出し、診断が境界ではなく根本を指すように。

### 🟢 X1 [Low] リポジトリ衛生：ビルド成果物の git 追跡
- `dotkt-out/` にコンパイル済み **DLL 90 個が git 追跡**され `.gitignore` 未登録（合計 3.1M）。ビルドのたび dirty 化し
  diff を汚染（レビュー時点で 2 個が変更扱い）、stdlib リグレッションを隠す既知ランドマイン
  （MEMORY `build-cache-masks-stdlib-regressions`）と関連。
- **是正**：`dotkt-out/` を `.gitignore` 化。必要なテスト用フィクスチャのみ選別追跡。

---

## ✅ 前回レビューから修正済みと再確認（公正のため明記）

| 項目 | 現状 | 確認箇所 |
|------|------|----------|
| `?? cands[0]` 任意オーバーロード | **loud 化**：候補が異なる CLR ターゲットに束縛すると throw（一致時のみ `cands[0]`） | `Program.cs:582/604/726` |
| 未解決 suspendCoroutine → 恒久サスペンド | **loud throw**（"refusing to emit a permanently-suspending coroutine"） | `SuspendColdLowering.cs:1556` |
| Unit 公開 Task ブリッジ ABI | **修正済み**：非ジェネリック `Task`（前回の `Task<Unit>` 指摘は解消） | `SuspendColdLowering.cs:2576` |
| ilemit suspend throw-stub | **emit 時 loud error**（bir2cir transform MISS を名指し）、stub は `StdlibStub` 時のみ | `Program.cs:989-993/1626` |
| Throwable.message/cause 二重降格 | kotc・ilemit 双方から**除去済み**（@ClrProperty＋bir2cir Rule-2p-inherited） | `Emitter.Expressions.cs:86` 他 |
| Lazy / Regex / Closeable / 例外マップ | bir2cir へ**真に移行済み**（kotc は CLR アノテーション不読、`ClrTypeRegistry.kt` 自体消滅） | `BirEmitter.kt:4425` 他 |
| conv の Kotlin-aware 文言 / ReverseBridge 名リスト | **除去済み**（純粋な opcode switch / `clr:`エイリアス駆動） | `Program.cs:2366` / `Emitter.ReverseBridge.cs:101` |
| ContinuationErasure 冪等性 / forArray サスペンド | 冪等確認 / forArray は**誤警報**確定（`il-coforarray` で緑固定） | `ContinuationErasure.cs:182` |
| `ilemitCompatBir` envelope | **既に削除済み**（producer ゼロ） | — |

---

## 推奨着手順（すべてゲート XFAIL-zero を維持できる）

1. **H1 Rule-4 ゲート化**（唯一の実害リスク）。ilemit フォールバックを `OwnerHasClrInterface` でゲート＋bir2cir で
   名指し refuse。→ M の `CollElemArg`→`object` 残渣も包摂。← **最優先**。
2. **M1 ilemit デッドコード一掃**（38 `clr.*`＋6 専属ヘルパ＋21 retire ケース。低リスク・大掃除、
   `master-task-inventory` 【1】② 済みスコープ）。
3. **M2 の clean win**：`s.length` ハードコード削除（数行・gate-neutral）。
4. **M3 facadegen 不変条件をレイヤー内へ**（`ShouldInject`＋seed に `kotlin.*` 短絡、`await` は白）＋ドキュメント修正。
5. **L1**：kotc が `suspendIntrinsic:true` を刻印 → bir2cir の文字列検出を削除。
6. **L2 / X1**：staleness コメント整理・`.gitignore` に `dotkt-out/` 追加。
7. **H2 suspend 型 POSITION 属性**は設計を要す中期作業（bir2cir CIR 契約＋ilemit 属性）。ABI 忠実度目的、
   単独の軽量セッション推奨。

**1〜3・6 はゲート中立で安全な変更**。各所見は担当レイヤーエージェント（H1=bir2cir＋ilemit、M1=ilemit、M2=kotc、
M3=facadegen）へ割り当て可能。

---
---

# Part 2 — 観点拡張レビュー（実行時 correctness / coverage / IL品質、2026-07-05）

> **なぜ Part 2 か。** Part 1 は「各レイヤーが正しい場所に正しい知識を持つか（静的な設計純度・失敗姿勢・デッドコード・
> coroutine 整合性）」を見た。Part 2 は直交する観点 — **①実際に動かして Kotlin として正しく振る舞うか（behavioral
> correctness）②緑のゲートが実バグを隠していないか（coverage）③IL の品質（perf）** — を、専門エージェント（behavioral /
> coverage / IL品質 / stdlib）＋codex で**経験的に**（`dotkt.sh --run` で実行、実測 actual vs expected）検証した。

## Part 2 総括 — 二つのパスの物語

- **Part 1 の結論**：設計純度・失敗姿勢は高水準、ゲートは真に XFAIL-zero、残債は少数。→ **正しい。**
- **Part 2 の発見**：それでも**日常的なイディオムに実行時 miscompile の層が存在する**（null許容プリミティブ、
  プリミティブ値のMap/ジェネリクス、クロスモジュールdefault引数、`toString(radix)`、`Pair`-toString、`list+list`…）。
- **橋渡し（なぜ緑なのに壊れているか）**：coverage の構造的欠陥 COV1 — **JVMオラクル（実 Kotlin 意味論と照合する唯一の
  ゲート）が約200サンプル中 ~43件しか見ず、残り ~120 の純Kotlinケースは "DotKt 出力から採取した固定文字列" で自己採点**。
  自己整合しているが Kotlin 的に誤ったマッピングは永久に緑。**C1（null許容プリミティブ）がこの生きた実例** — `println(n)` は
  ボックス化経由で正しく出るためゲートを通り、`val z: Int = n` の unwrap 経路だけが壊れている。
- **系統的根本原因は 2 つに集約**：**(a) ジェネリクスの `T`/`V` 経由のボックス化プリミティブ二重表現**（C1,C2 系）と
  **(b) クロスモジュールのデフォルト引数**（C3 系）。この 2 ファミリ＋C4/C5 を潰せばリストの大半が解消する。

> **重要な性格の違い**：Part 1 の所見は主に「掃除・純度・防御」。Part 2 の C1〜C9 は**ユーザーの正しい Kotlin コードが
> 誤結果を出す/クラッシュする**実バグで、一部は**サイレントなデータ損失**（C2 の `getOrPut`）。出荷品質に直結する。

---

## 2A. 確定した実行時 miscompile（重大度順・根本原因ファミリ別）

すべて最小 `.kt` を `dotkt.sh --run` で実行し、実測。`docs/dotkt-semantics.md` /
`docs/user/kotlin-on-clr-differences.md` に**設計逸脱としての記載なし**＝本物のバグ（C4/C15 の doc 状況は各項に付記）。

### 🔴 C1 [CRITICAL] null許容プリミティブの smart-cast が `Nullable<T>.Value` でなく `.HasValue` を読む
- **再現**：`val n: Int? = 7; if (n != null) { val z: Int = n; println(z); println(z + 100) }` → 実測 **`1`** then **`101`**
  （期待 `7`/`107`）。`println(n + 1)` → **InvalidProgramException**、算術で **SIGSEGV**、`if (n != null && n > 5)` が **else**
  （7>5 なのに "small"）。`Int?`/`Long?`/`Double?`・関数引数・比較で確認。
- **なぜ緑をすり抜けるか**：`println(n)` 単体はボックス化経由で `7`（COV1 の実例）。壊れているのは unwrap-to-value 経路のみ。
- **根本原因**：`IMPLICIT_CAST(T? → T)` の値型 smart-cast が nullable-unwrap に lower されていない。ilemit には正しいノード
  `EmitNativeClrNullableValue`（`Emitter.Expressions.cs:560`、`ldloca; call Nullable.get_Value`）が**あるのに emit されない**。
- **担当**：kotc/bir2cir（smart-cast 読みで unwrap ノードを出す）。**最優先**（極めて高頻度・一部サイレント）。

### 🟠 C2 [HIGH] ジェネリクス経由のボックス化プリミティブ二重表現（ファミリ・複数クラッシュ/データ損失）
共通根：`T`/`V` がボックス化プリミティブのとき、生成 IL の不変ジェネリクス・token・unbox がずれる。**bir2cir/ilemit。**
- **`getOrPut` on `MutableMap<K,primitive>` が 0 を返し挿入もしない（サイレントなデータ損失）**：
  `mutableMapOf<Int,Int>().getOrPut(5){42}` → `0/0/null`（期待 `42/1/42`）。String 値なら正常。inlined `get()` の
  プリミティブ結果が `null` と読めず `value==null` が false に。
- **`getOrElse(presentKey){...}` がゴミを返す**：`mapOf(1 to 10,2 to 20).getOrElse(1){-1}` → `-1442783864`（期待 `10`）。
  present なボックス化プリミティブの `value as V` unbox がクロスモジュールでゴミ。
- **`compareBy`/`compareValuesBy` のプリミティブセレクタ → NullReferenceException**：
  `listOf(3,1,2).sortedWith(compareBy { it })` → NRE。`sortedWith(compareBy { it.x })` は**極めて高頻度**。
- **`groupBy` 結果 Map の反復/index/print → InvalidCastException**（`Dictionary<K,IList<V>>`→`IDictionary<K,IReadOnlyList<V>>`、
  不変性）。`getValue` は emit 時 VerificationException。behavioral・stdlib 両エージェントで確認。
- **`MutableMap.merge(k,v){...}` → InvalidCastException**（`Func<Int,Int,V>`→`Func<Object,Object,Object>`、デリゲート反変は
  値型引数に効かない）。
- **`Array<Int?>` の非null要素 → SEGFAULT**（`arrayOf(1,null,3)[0]` / `arrayOfNulls<Int>(3).also{it[0]=5}`）。参照配列スロットへの
  unboxed 値の `stelem`/`ldelem` ボックス化が誤り。← ilemit 配列要素ボックス化。
- **`T : Enum<T>` 境界のジェネリック → VerificationException**（`fun <T:Enum<T>> nameOf(e:T)=e.name`）。`kotlin.Enum<T>` の
  CLR 制約が実 enum 型で満たされない。← bir2cir/ilemit 制約 emit。

### 🟠 C3 [HIGH] クロスモジュールのデフォルト引数（ファミリ）
共通根：参照 DotKt dll のデフォルト値/`$default` synthetic が保存されず、中間 param 省略時に**末尾ラムダが誤スロットへ**ずれる。
MEMORY `cross-module-default-args-not-preserved`。**kotc/bir2cir。高頻度・広範囲。**
- **`joinToString("-"){ "x$it" }` が transform を誤束縛**：実測 `System.Func`2[...]1-2-3`（transform が `prefix` に漏れ、
  **適用されない**）。named 指定でも壊れる。全引数明示なら正常＝機構自体は健全、default 省略時のスロット割当が原因。
- **`substringAfter("=")` / `substringBefore`（`missingDelimiterValue = this` 依存）→ InvalidProgramException**。default 明示なら正常。
- **`data class` の `copy(field = x)` が同一モジュールでもコンパイル不可**（"omitting a non-constant default argument"）。
  生成 `copy` の `y = this.y`（receiver 参照）default を呼び出し側が拒否。`dotkt-semantics.md §10` はクロスモジュール前提で
  記すが、**実際はどこでも使う基本イディオムが通らない** → 再スコープ要。

### 🟠 C4 [HIGH] `Int/Long.toString(radix)` — 負数2の補数 + 基数∉{2,8,10,16} クラッシュ
- **再現**：`(-255).toString(16)` → `ffffff01`（期待 `-ff`）、`Int.MIN_VALUE.toString(16)` → `80000000`（期待 `-80000000`）、
  `35.toString(36)` → **crash** `ArgumentException: Invalid Base`。
- **根本原因（訂正）**：kotc の特例（`BirEmitter.kt:3863-3876`）が `System.Convert.ToString(value,base)` に lower し、**正しい
  stdlib actual `StringNumberConversionsClr.kt:58-89` を隠している**。stdlib 本体は in-module 複製で正しく動く（＝「cross-module
  miscompile」コメントは stale）。**是正＝kotc 特例を削除**（cardinal-rule 違反の除去）。Part 1 M2 を上書き。ユーザー semantics
  doc に未記載＝通常 Kotlin でクラッシュ。**担当 kotc。**

### 🟠 C5 [HIGH] `String.hashCode()` が非決定的（.NET ランダム化 GetHashCode 束縛）
- `"Aa".hashCode()` がプロセス毎に別値、`"".hashCode()` が `0` にならない。Kotlin は決定的多項式ハッシュを契約 → 再現性・
  永続化ハッシュ・クロスラン一致が壊れる。`Double`/`Float` ハッシュも非 Kotlin。
- **担当**：stdlib 側（`String`/`Double`/`Float` に Kotlin 実装の `hashCode()`、`Any.kt:31` の `@ClrIntrinsic("GetHashCode")` を上書き）。

### 🟠 C6 [HIGH] `maxOrNull`/`minOrNull` on ジェネリック `Collection<T>` receiver → EntryPointNotFound
- `fun <T:Comparable<T>> mx(c: Collection<T>) = c.maxOrNull()` → `EntryPointNotFound at <>dotkt_KIterable_kotlin_Double.iterator()`。
  直接 `listOf(3,1,2).maxOrNull()` は正常。`Double`/`Float` の兄弟オーバーロードを持つ pair だけ失敗。
- **根本原因**：kotc のクロスモジュール overload 復元 `ClrTypeInjection.kt:156/163/373` が同名 top-level 拡張を
  `(package,name,value-arity)` でキー化し**拡張レシーバ要素型を落とす** → `Iterable<Double>/<Float>/<T>` が1キーに衝突 → 総称呼び出しが
  `Double` 兄弟に汚染。`CharSequence.count(predicate)`（同 synthetic-iterator 症状）も同族。**担当 kotc**（キーに receiver 要素型を追加）。

### 🟠 C7 [HIGH] クロスモジュール拡張プロパティ getter が EMIT でクラッシュ
- `listOf(1,2,3).lastIndex` / `.indices` / `"hi".lastIndex` / `(-3).sign` / `.absoluteValue` → emit 時
  `NotSupportedException: field <FileClass>.<name> not found`（ilemit `FindField`）。
- **根本原因**：`BirEmitter` のプロパティ読みが delegated/member（`declaringClass != null`, `:3827`）は扱うが、**top-level 非inline
  拡張プロパティ getter（`declaringClass == null`）が現ファイルクラス宛の `field` ノードに落ちる**。
- **担当 kotc**：top-level 拡張プロパティ読みを `callStatic <OwnerFileClass>.get_<name>(receiver)` に（拡張関数経路をミラー）。`cases/` に無し＝ゲート盲点。

### 🟠 C8 [HIGH] `list + list` / `list + element`（`List.plus`）→ InvalidProgramException
- `listOf(1,2) + listOf(3,4)`（`+ 3` も、`List<String>` も）→ `InvalidProgramException at _CollectionsKt.plus`。
- **根本原因**：**コンパイル済み `DotKt.Stdlib.dll` の `_CollectionsKt.plus` 本体の IL が不正**（stdlib-emit 欠陥、source ではない）。
- **担当**：stdlib-emit（bir2cir/ilemit で再emit）。**極めて高頻度。**

### 🟠 C9 [HIGH] `abs(Int.MIN_VALUE)` / `abs(Long.MIN_VALUE)` が OverflowException（Kotlin はラップ）
- `import kotlin.math.abs; abs(Int.MIN_VALUE)` → `OverflowException`（期待 `-2147483648`）。
- **根本原因**：`MathClr.kt:400/421` が `@ClrIntrinsic("System.Math.Abs")` に束縛、`int`/`long` overload が MIN で throw。
- **担当**：**stdlib 側（cardinal rule）** — `@ClrIntrinsic` を外し純Kotlin本体 `if (n<0) -n else n`（unchecked neg でラップ）に。清潔に修正可。

### 🟡 C10 [MED] `Int.MIN_VALUE / -1` と `% -1` が OverflowException（Kotlin はラップ：`/`→MIN、`%`→0）
- プリミティブ `div`/`rem` が生 IL opcode を出し、CLR は MIN/-1 で throw。**担当 bir2cir/ilemit**（MIN/-1 ガード）。

### 🟡 C11 [MED] `Pair`/`Triple` 内のコレクションが `toString` で生の .NET 型名
- `listOf(1,2) to listOf(3,4)` → `(System.Collections.Generic.List`1[System.Int32], ...)`。`partition{}`/`zip`-of-lists が壊れる。
  top-level `println(list)` と `List<List>` は正常＝`Pair`/`Triple.toString` 内のコレクション成分だけ .NET `ToString` に落ちる。
- **担当**：bir2cir/kotc の toString ルーティング（`Pair`/data-class toString 内でコレクション成分を Kotlin stringifier に）。

### 🟡 C12 [MED] 入れ子ラムダのリスト `(1..3).map { i -> { i } }` が emit 失敗
- `NotSupportedException: cannot resolve .NET type ::kotlin.Int`。関数型 type-arg 内で `kotlin.Int` 未置換。**担当 bir2cir/ilemit。**

### 🟡 C13 [MED] コンパイル済み stdlib の emit 欠陥（3件）
- `generateSequence(seed){next}` → `TypeLoadException`（closure 型 `<>dotkt_..._Closure134`1` が dll に未 emit）。
- `"abcd".windowed(2)`（CharSequence）→ `[DOTKT-STDLIB] not lowered`（未 lower の throw-stub）。`Iterable.windowed` は正常。
- `groupingBy{}.eachCount()` → `InvalidProgramException`（`eachCount` 本体 IL 不正）。
- **担当**：stdlib-emit（bir2cir/ilemit）。

### 🟢 C14 [LOW] `Double`/`Float` の `-0.0` 全順序未実装
- `(-0.0 as Any) == (0.0 as Any)` → `True`（Kotlin false）、`(-0.0).compareTo(0.0)` → `0`（Kotlin -1）。**担当 stdlib/bir2cir。**

### 🟢 C15 [LOW / doc-code 矛盾] `@kotlin.concurrent.Volatile` が no-op
- `VolatileClr.kt` は空の `annotation class`（`@ClrIntrinsic` なし）→ `volatile.`/`System.Threading.Volatile` 意味論なし＝
  クロススレッド可視性がサイレントに誤り。単一スレッドゲートでは観測不能。
- **⚠️ doc-code 矛盾**：`docs/dotkt-semantics.md §4c` は「real `modreq(IsVolatile)`」と主張するが**ソースは空アノテーション**。
  doc が誤りか、束縛が別所にあるか要確認。**担当**：stdlib 側で束縛 or「未対応」を doc 訂正。

---

## 2B. カバレッジの構造的欠陥（coverage エージェント）

### 🔴 COV1 [HIGH] 純Kotlin/stdlibサンプル約120件が JVMオラクルでなく手書き期待値だけで検証
- `verify-differential.sh:58` の `PURE` は約43件（25 `m-*` + 18 `il-*`）のみ。残り ~120 の `il-*`（`il-str`/`il-coll`/`il-math`/
  `il-unsigned`/`il-groupvalues`/`il-result`/`il-lazy`…）は `verify-il` で**DotKt 出力から採取した固定文字列**と照合するだけ。
- → **自己整合するが Kotlin 的に誤ったマッピングは永久に緑**（`sorted` 安定性、Regex group、rounding、Map/collection toString、
  unsigned wrap 等）。「differential が stdlib マッピングを実 Kotlin と照合」の主張（`verify-differential.sh:4-6`）と食い違う。**最大の盲点。**
- **是正**：JVM実行可能な純サブセット（string/collection/math/regex/unsigned…）を `PURE` に昇格。interop/coroutine 系のみ除外。
  → これが入れば C1〜C11 の多くは今後**自動検出**される。

### COV2〜COV6（要点）
- **[Med-High] atomics**（✅表記だがケース0、`Interlocked` byref 束縛が脆いのに無ゲート）→ `cases/il-atomics`。
- **[Med] typealias**（✅だが0）→ `cases/il-typealias`（JVM可＝PURE 追加）。
- **[Low-Med] Triple**（✅だが0）→ `il-pair` 拡張 or `cases/il-triple`。
- **[Low] tailrec**：唯一のケース `il-langfeat` は浅い深度のみ。TCO 未発行なら深い再帰で SO ＝**未文書の逸脱**（doc 追記 or 深再帰ケース）。
- **デッド/未配線フィクスチャ**：旧C#バックエンド runner 12件（`m-c2..m-s5`）、未配線 `.ktproj` 5件（うち `ktproj-il` は
  `README.md:139` でユーザー案内されているのに未検証＝腐りうる）。
- **[整合性] `ifacesuspend` の ilverify コード/コメント矛盾**（`verify-il.sh:625` に居るのにコメント `:629-632` は「ASMS に無い、
  REAL latent finding」と主張。`XFAIL_ILVERIFY` 空と併せ両立不能）→ 検証不能IL リグレッションを隠す恐れ、要 reconcile。

---

## 2C. IL品質 / 性能（IL品質エージェント、逆アセンブル実証、すべて正当性中立）

- ✅ **基礎は良好**：値型ジェネリクスは非ボックス維持（`List<Int>`→`List`1<int32>`）、range/array ループはアロケーションゼロの
  カウンタループ、`constrained.` も適切（欠落も冗長もなし）。
- **[High-perf] ILQ1：非キャプチャラムダが評価毎に `Func`/`Action` を新規確保（キャッシュなし）**（`Emitter.Expressions.cs:956`
  `delegateNew`）。ループ内なら反復毎にヒープ確保。Roslyn は静的フィールドにキャッシュ。**ilemit 内で修正可＝単発最大の勝ち。**
- **[High-perf] ILQ2：文字列テンプレート/連結が値型を毎回ボックス化＋常に `object[]` 確保**（`Program.cs:2539` `EmitConcat`）。
  Roslyn・kotlinc の**どちらより悪い**。値パートを `ToString()` 化＋小 N は固定アリティ `Concat`。
- **[Med] ILQ3：連鎖 `+` が入れ子 `Concat`**（根本 bir2cir/kotc、ilemit で平坦化可）。
- **[Med] ILQ4：`for(x in list)` がイテレータブリッジ確保＋二重ディスパッチ**（bir2cir/kotc lowering、直接 `foreach` 化可）。
- **[Low] ILQ5：捕捉 mutable var が Ref セル＋closure（JVM 相当・非退行、Roslyn は 1 alloc）／ILQ6：identity `conv` 非省略／
  ILQ7：`println(Any?)` 境界ボックス化（stdlib overload 次第）。**

---

## 2D. 系統的根本原因と推奨修正順（Part 2）

**2 大ファミリを潰すのが最効率**：
1. **(a) ボックス化プリミティブの二重表現（C1, C2 群, C10）** — bir2cir/ilemit の値型 `T`/`V` の unbox・null 表現・
   不変ジェネリクス dispatch。**C1（null許容 smart-cast）を最優先**（高頻度＋サイレント＋SIGSEGV）。
2. **(b) クロスモジュールのデフォルト引数（C3 群, C19/joinToString/substringAfter）** — kotc/bir2cir の default 値保存 / `$default` synthetic。

**清潔に単独修正できる高価値 quick win**：
- **C4** kotc 特例削除（stdlib actual が正しい）／**C9** `abs` を stdlib 実体化／**C5** `String.hashCode` を stdlib 実装／
  **C6** overload 復元キーに receiver 要素型追加／**C7** 拡張プロパティ getter emit。
- **C8/C13** はコンパイル済み stdlib の emit 欠陥（`plus`/generateSequence/windowed/eachCount）＝再 emit で解消。

**そして COV1（PURE 昇格）を土台修正として先行**させると、以後これらのクラスは回帰時に自動で赤くなる（今は緑をすり抜ける）。

**推奨順**：COV1（differential 拡張）→ C1 → C4/C9/C5（quick win）→ C2 ファミリ → C3 ファミリ → C6/C7/C8 → 残り。

---

## 2E. Swept-and-CORRECT（安心材料の要約）

両エージェントの広範スイープで**正しく動作**を確認：整数演算・ビットシフト（count-masking 含む）・unsigned 全般・Char 演算・
大半の文字列 op（trim/pad/replace/split/substring/repeat/reversed/unicode長/サロゲート）・大半のコレクション op（map/filter/fold/
reduce/sorted*/distinct/zip/flatMap/associate*/chunked/windowed(Iterable)/直接 max*/min*）・float（primitive NaN 比較・isNaN・∞）・
範囲/数列・例外（try/finally 一回・カスタム例外 catch）・クロージャ（可変捕捉・per-iteration ループ変数捕捉・`by lazy` 単回計算）・
制御フロー（sealed exhaustive when・参照型 smart-cast・elvis・safe-call・tailrec 浅）・data/enum メンバ・**クロスモジュールの定数
デフォルト引数**。→ **破綻は「値型を跨ぐジェネリクス」と「非定数クロスモジュール default」に集中**しており、それ以外の広い表面は健全。
