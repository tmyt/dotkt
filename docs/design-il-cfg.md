# 設計: E-0.5 — 制御フローの CFG ブロック IR 化（AST→CFG）

> **状態 (2026-06-30 見直し)**: HISTORICAL-DONE（主要部）。CFG コア — **5.1 label/goto/brIf・5.2 while/do-while・5.3 if/when・5.5 break/continue/ラベル** は実装済み（2026-06-20）。**live な残りは 5.6 非ローカル return と 5.7 try/catch/finally 領域マーカーのみ。** 本書は CFG/制御フロー lowering を `BirEmitter → ilemit` に置く前提で書かれているが、**現行アーキテクチャでは制御フロー lowering は bir2cir（BIR→CIR）の責務へ移管中（migration in-flight・未完＝done とは主張しない）**。本書の「differential」検証は **IL 専用ハーネス（`verify-il` ＋ kotlin/jvm 差分の `verify-differential`）であって C# オラクルではない**（C# バックエンドは完全引退）。現行アーキテクチャの正は [docs/ship-tasks.md](ship-tasks.md) §0。

**目的**: 構造化 AST（BIR の `if`/`while`/`when`/`try`/`break`…）から IL を直接吐くのをやめ、**基本ブロック＋明示分岐**の中間形（CFG ブロック IR）を BirEmitter で一度だけ生成し、ilemit はそれを機械的に emit する。これにより `do-while` 空ボディ・`break@outer`・**非ローカル return**・コルーチンの **spilling/条件式 suspend**（D トラック）が全部「ただの分岐」になり、形状ごとの特別扱いとバグ class が消える（[[lowering-lives-in-bir]] の三層 (2)）。

作成 2026-06-20。前提: IL バックエンドは出荷経路（56 サンプル緑＋ilverify-clean）。**各増分で緑を維持**（[[no-half-baked-public-state]]）＝big-bang 書換をしない。

---

## 0. 既にある実証（コルーチンの flat-step）

コルーチン CPS lowering で **`coLabel`/`coGoto`/`coCondGoto`/`coReturn` ＋通常文のフラット列**を BirEmitter が生成し、ilemit が「ラベル define → 分岐 emit」で機械的に消費する形を既に実装済み（`EmitCoroutine`）。**これは CFG ブロック IR そのもの**で、suspend fun の中だけに閉じている。E-0.5 は **この語彙を一般の制御フローへ昇格**させる作業。

## 1. CFG ブロック IR の語彙（BIR ノード）

コルーチンの `co*` を一般化した、suspend 非依存の最小プリミティブ:

- `{"k":"label","id":N}` — 基本ブロック境界（分岐先）。
- `{"k":"goto","id":N}` — 無条件分岐。
- `{"k":"brIf","id":N,"cond":<expr>,"on":true|false}` — `cond` が `on` のとき `id` へ分岐（`on:false` ＝ `if (!cond) goto`、ループ脱出に使う）。
- 通常の式文/代入（既存ノード `exprStmt`/`var`/`setLocal`/`setField`/`return`/`throw` …）はそのまま。
- `return`/`throw` は既存どおり（メソッド境界）。**非ローカル return** は「結果を保存して関数末尾ラベルへ goto」へ lowering（§3.5）。

`id` は関数内ユニークな整数。コルーチンの `coLabel` 等とは別系統だが ilemit 側の emit ロジックは共通化できる（ラベル table ＋ 分岐）。

**重要**: これは式の lowering ではなく**文（制御フロー）の lowering**。式 breadth（collection/scope/拡張）は従来どおり高レベル BIR のまま（CFG 不要・並行可）。

## 2. ilemit 側（薄い・機械的）

新規ノードは3つだけ。`EmitMethodBody` の本体走査で:
1. **プリスキャン**: body 内の全 `label` id に `_il.DefineLabel()` を割当（前方参照に対応）。
2. 走査:
   - `label` → `MarkLabel`。
   - `goto` → `Br`。
   - `brIf on:true` → `EmitExpr(cond); Brtrue(id)`。`on:false` → `Brfalse(id)`。
   - その他 → 既存 `EmitStmt`。
3. ネスト排除: CFG 形式の body はフラット列なので、`while`/`if`/`when` の case は **CFG body では出現しない**（BirEmitter が分岐に落とすため）。ilemit の既存構造化ハンドラ（`while`/`if`/`for`…）は**当面残す**（未変換構文の互換）。

ローカル変数のスコープ: フラット化でブロックスコープが消えるが、ilemit は名前→LocalBuilder の単純 map（`_locals`）なので、関数内で名前ユニークなら問題なし。シャドーイングは BirEmitter 側で rename（コルーチンと同方針）。

## 3. AST→CFG 変換（BirEmitter・構文別）

`stmt()` とは別に、**関数本体を CFG 列へ落とす `lowerCfg(statements): List<step>`** を新設。各構文を分岐へ:

### 3.1 `if` / `when`
```
when { c1 -> A; c2 -> B; else -> C }  ⇒
  brIf L1 (!c1); A; goto END
  L1: brIf L2 (!c2); B; goto END
  L2: C
  END:
```
（コルーチンの `emitWhenCps` と同型。subject 付き when は `is`/`==` を cond に。）

### 3.2 `while` / `do-while`
```
while(c){B}    ⇒  START: brIf END (!c); B; goto START; END:
do{B}while(c)  ⇒  START: B; brIf START (c); 
```
（do-while 空ボディ無限ループ問題が構造的に消える。）

### 3.3 `for` (range/array/iterable)
既存の `for`/`forArray`/`forEachInline` lowering を CFG（カウンタ var＋`brIf`）へ展開。range は `i=from; START: brIf END (cmp); B; i+=step; goto START; END:`。

### 3.4 `break`/`continue`（ラベル付き含む）
ループ変換時に各ループの **continue ターゲット（START 相当）/ break ターゲット（END）ラベルを stack で保持**。`break`→`goto END`、`continue`→`goto CONT`、`break@outer`→ラベル名で対応ループの END へ。**`break@outer` が動機**（[[il-primary-backend-pivot]]）。

### 3.5 非ローカル return（inline ラムダからの `return`）
inline 展開後のラムダ body 内 `return`（呼び出し元関数を抜ける）→ **「結果を `__nlret` ローカルへ保存し、関数末尾の `RET` ラベルへ goto」**。関数末尾に `RET: return __nlret`。これで inline ラムダの非ローカル return が分岐に。E-1 の inlining 残（[[function-inlining-spike]]）の中核ピース。

### 3.6 `try`/`catch`/`finally`
**例外領域は分岐に落とせない**（IL の `.try`/`catch` 構造が必要）。CFG 列の中に**領域マーカー**（`tryBegin`/`catchBegin`/`tryEnd`）を持たせ、ilemit が `BeginExceptionBlock`/`BeginCatchBlock`/`EndExceptionBlock` を発行（既存 `try` ハンドラのロジックを領域マーカー対応へ）。`finally` も同様。**領域内の return は leave へ**（既存 `_tryStack` 機構を流用）。これでコルーチンの try/catch-around-await も将来解禁（D の後段）。

## 4. SSA（後段・D 着手時）

CFG の上に φ を載せ、suspend をまたぐ live 変数の field 昇格（spilling）・部分式 suspend（`f(g().await())`）を正確化。**E-0.5 では CFG まで**。SSA は D（コルーチン完全意味論）着手時に CFG の上へ追加。

## 5. 段階的ロールアウト（緑を維持）

big-bang にしない。**構文単位で CFG へ移し、毎回 verify-il + verify-differential で parity 確認**:

1. **5.1 基盤** ✅(2026-06-20): `label`/`goto`/`brIf` を ilemit に追加（`EmitStmt` 3 ケース＋`PrescanCfgLabels` 再帰プリスキャン＝前方参照対応、`EmitMethodBody`/`EmitCtorBody`/`EmitCoroutine` で実行）。
2. **5.2 `while`/`do-while` を CFG 化** ✅(2026-06-20): BirEmitter `cfgWhile`/`cfgDoWhile`（`block` ラップの `label`/`brIf(on:false/true)`/`goto` 列）。**`hasLoopJump` で break/continue を含むループは構造化 `while`/`dowhile` にフォールバック**（jump ターゲットは ilemit のループ stack 任せ＝§5.5 まで温存）。ラベル id はファイル全局一意（`cfgLabelN`）で衝突回避。m0 の `while` が CFG 化＋ilverify-clean、verify-il 全 PASS＋verify-differential 全 MATCH で parity 確認。
3. **5.5 `break`/`continue`/ラベル** ✅(2026-06-20, 5.2 直後に前倒し): BirEmitter に `cfgLoopStack`（loop, continueId, breakId）を導入。`cfgWhile`/`cfgDoWhile` が push/pop し、`IrBreak`/`IrContinue` を **loop 参照同一性**でスタック照合→ CFG ループ対象なら `goto`（`break@outer` 含む）、構造化 for 対象なら従来 `break`/`continue` ノード（ilemit ループ stack）。**`hasLoopJump` フォールバック撤廃＝全 while/do-while が CFG**。新サンプル `il-loopjump`（while+break・while+continue・nested `break@outer`）＋ m-a2 do-while で parity・ilverify-clean。
4. **5.3 `if`/`when` を CFG 化** ✅(2026-06-20): `cfgWhen`（文位置）＝各非 else 枝 `brIf NEXT(!cond); body; goto END; NEXT:`、else は END へ fall through。式位置 if/when は `ternary`(expr) のまま。il-smartcast/il-enum/m-a1 で parity 緑。**→ CFG コア（ループ＋分岐）完成。**
5. **5.4 `for`/range を CFG 化**（保留＝cosmetic）: ilemit の既存 `for` は `to` を毎反復再評価する int 限定ループで CFG 化しても挙動同一＝payoff 無し・リスクのみのため後回し。
6. **5.6 非ローカル return** ← **インライン化が前提**（lambda 引数あり inline fun の body を呼び元へ splice）。stdlib inline は body 不在のため対象はユーザ inline fun（[[function-inlining-spike]]）。
7. **5.7 try/catch/finally 領域マーカー** ← コルーチン try（D）で必要。
6. **5.6 非ローカル return**: inline ラムダ（`il-inline` 拡張）で実証。
7. **5.7 try/catch/finally を領域マーカー化**: `il-exc`/`m-c2`。
8. **完了判定**: 全既存サンプル緑＋ilverify-clean を維持しつつ、`break@outer`・非ローカル return が IL で動作。→ D（spilling/条件式 suspend）の基盤完成。

各段で「変換対象の構文だけ CFG、残りは従来」を保てるよう、`lowerCfg` は**未対応構文に当たったら従来 `stmt()` をそのまま埋め込む**（混在可）。これが緑維持の鍵。

## 6. リスク / 緩和

- **混在の整合**: CFG 列に従来構造化ノード（未変換の `try` 等）が混ざる。ilemit は両方を1つの走査で扱う（label/goto/brIf＋既存ケース）。問題なし（コルーチンで実証済の混在パターン）。
- **ラベルの前方参照**: プリスキャンで全 `DefineLabel` 済にしてから走査（コルーチンと同じ）。
- **スコープ/シャドーイング**: 名前衝突は BirEmitter で rename。
- **出荷バックエンドの破壊**: 段階ごとに3ハーネス回帰。1構文ずつなので切り分け容易。big-bang 厳禁。
- **SSA を急がない**: E-0.5 は CFG まで。spilling は D で。
