# BIR/CIR 契約の凍結 — 設計 (#37)

> Audit: `docs/bir-audit/{kotc-emit,bir2cir,ilemit-consume,facadegen-meta}.md`（5 read/write 地点の網羅カタログ）。
> 前提: BIR は `[KotlinInline]` に生 JSON で焼かれて出荷される **serialization format**。unpublished なので
> **完全に破壊してよい**（後方互換なし・全層 lockstep 書換え・stdlib 全再ビルド）。

## 1. Audit の統合結論 — drift は「2層」に分かれる

BIR には**構造化の度合いが違う2つの表現層**があり、drift の性質が正反対:

### (A) node kind 層 — 既に structured、drift は軽微
`{"k":"...", ...}` の JSON オブジェクト。~95 種（kotc emit）／~100 消費（ilemit）。既に木なのでパース不要。
drift は **synonym と dead と mid-migration** のみ:
- **DEAD（削除）**: `clr.*` twin family 全部（`clr.const`/`clr.bin`/`clr.stelem`/… producer-zero、live は非 `clr.` 版）。bir2cir の `KotlinInlineAttr`（Program.cs:450）も定義のみ未参照の dead code
- **同形 variant（統合検討）**: `setField`/`setFieldExpr`/`staticFieldSet`（stmt/expr/toplevel の field-write 3種）、`objMethod`（`callInstance` に吸収可能な near-orphan）
- **正当な別物（残す）**: `staticField`≠`clrStaticField`、`callInstance`≠`clrInstance`、`field`≠`clrPropGet`（意味が違う）
- **mid-migration**: 構造化 `for*` family と CFG `label`/`brIf`/`goto` while-family が併存（D8）
- → **対応: dead 削除 + variant 統合 + canonical set 明文化。表現変更は不要**（既に structured）

### (B) type 表現層 — **string token = drift 震源**
型は**コンパウンドな文字列トークン**（`func:nullable:kotlin.Int:@Foo[gp:T]` 等）。これを **~20 箇所で hand-scan**:
- `SplitTopLevel` が **9 コピー**（同一 comma-splitter、bir2cir 全域）
- `func:`/`nullable:` の return-boundary が **3つの相互結合 scanner**（後者に「他を踏まない」ハードコード skip、silent desync 温床）
- `BareOwner` ×3 / generic-arg 抽出 ×3 / `array:nullable:` modifier-stacking を StartsWith/Contains で手パース
- **BIR コロン形式 vs META ブラケット形式の二重語彙**（facadegen `BirTokenToMeta` 77行 = ilemit `SkipTypeToken` の逐語コピー）
- primitive が 3 通り（`kotlin.Int` / `int` shorthand / `nullable:int`）— D1
- `gp:` が **NAME 依存**（`CanonSig` が positional に remap、`FindReflectedMethodBySigLoose` が def/call 名不一致を吸収）
- **これらは全て「文字列を分割・走査する」ことに由来**。あなたが予言した「不安定な表現がたくさん」の実体

## 2. 設計判断 (0b) — **型を structured node にする**（string-parse を消滅させる）

**決定: type 表現を string token → structured node へ。** これが drift の**全クラスを一撃で消す**唯一の変更:
`SplitTopLevel`×9・`FuncRetEnd`・3 scanner・`BareOwner`×3・generic-args×3・`BirTokenToMeta`・`gp:` remap —
**全て「木を walk する」に置換され、パーサが消える。**

### canonical type schema（唯一の型表現）— **FULL structured（全振り、hybrid にしない）**
「10年 ABI」基準では **uniformity が命**。atomic を裸文字列で残す hybrid は「string か object か」の**二重表現**を作り、
それ自体が drift の温床（今の string-token 地獄と同じ轍）。→ **全ての型を `{t:...}` node に統一、例外なし。**
冗長性は carrier のバイナリ化（MessagePack）で消える。規則性のためにコンパクトさを捨てる、が正しいトレードオフ。
```jsonc
// Type は「常に」オブジェクト。裸文字列の型は存在しない。reader は t で dispatch するのみ、parse ゼロ。
{ "t":"fqn", "name":"kotlin.Int" }                               // atomic も明示 node（例外なし）
{ "t":"fqn", "name":"kotlin.collections.List", "args":[T,...] }  // 名前型 + generic 適用
{ "t":"tv",  "i":0 }                             // 型変数 = POSITIONAL index（gp:-name-remap を殺す）
{ "t":"fn",  "suspend":false, "ret":T, "params":[T,...], "recv":T? }  // 関数型（func:/sfunc: を包摂、suspend は flag）
{ "t":"nullable", "of":T }                       // T?
{ "t":"array",    "elem":T }                     // Array<T>
{ "t":"byref",    "of":T }                       // byref
```
**消える語彙**: `func:`/`sfunc:`→`fn`(+suspend flag)、`nullable:`→`nullable`、`array:`→`array`、`byref:`→`byref`、
`gp:X`→`tv{i}`（positional）、`clr:`/`clrg:`/`@`/primitive shorthand/裸 FQN 文字列→**すべて `{t:"fqn"}`**。
**BIR に「文字列で表される型」は一切残さない**（scanner が復活する隙をゼロにする）。

### durable-ABI 原則（「10年使える ABI」の設計基準）
1. **Uniformity（規則性）**: 一概念一表現。型は常に node、node kind は常に `{k}`-tagged。特例・短縮・二重表現を作らない
2. **Self-describing**: 構造が schema。外部 grammar（コロン/ブラケット規則）を持たない → drift の入る余地が構造的に無い
3. **Additive-extensible**: 新型種 = 新 `t` tag、新フィールド = 追加のみ（既存を壊さない）。version tag で未知を明示 reject
4. **Codec-agnostic**: 論理表現（typed tree）と物理 codec（JSON / MessagePack）を分離。carrier の version tag が両者を decouple
5. **Single source**: 各語彙（node kind / type）の read/write は**全層で1つの共有 helper**。「N 箇所を直す」を構造的に不可能にする

### 層純度の構造的強制（副次的だが重要）
`clr:`/`clrg:`/`@`/shorthand は「その型が CLR のどこに住み何種か」という **CLR-resolution 決定**を kotc 出力に焼いていた（層侵犯、D6）。
structured 化で **kotc は `{t:fqn,name:"kotlin.Int"}`（純 Kotlin identity）だけを吐き、bir2cir が resolution を導出**（primitive opcode 選択・generic 構築・参照型解決）。
→ 「kotc は Kotlin FQN のみ、CLR-resolution は bir2cir」が**文字列 prefix でなく型の構造で強制**される。

### なぜ hybrid（atomic=string, compound=object）か
全型を object にすると `kotlin.Int` が毎回 `{t:fqn,name:...}` で冗長。**atomic FQN は裸文字列のまま**にし、compound だけ object。
reader は `is string ? atomic : dispatch(t)` の**1分岐**（コロン分割でない）。common case を compact に保ちつつ scanner を全滅させる sweet spot。

### BIR = META 統一（二重語彙の解消）
META（facadegen↔coneOf）も**同じ structured type node**を使う。→ `BirTokenToMeta`/`BirSkipTypeToken`/`BirSplitTopLevel` 削除、
重複 `SkipTypeToken` 解消。tlfun/tlextprop/tlprop の型スロットも structured に。

## 3. carrier envelope（ユーザー要望、同波で）

- `KotlinInlineAttribute(string body)` → **`KotlinInlineAttribute(string version, byte[] content)`**。
  version = codec+schema tag `"bir-json/1"`（将来 `"bir-msgpack/1"`）、content = byte[]（今 UTF8(json)）。
  **先例: `NullableAttribute` が既に `(byte)`/`(byte[])` dual overload を持つ**（ilemit CompilerServices.cs:68）— これをミラー。
- 同 envelope を **`KotlinSuspendFunctionTypeAttribute`** にも（structured type を運ぶので `sfunc:` 文字列が消え、これ自体 structured 化で自然に）。
- reader は version→**単一 `DecodeInlineBody(version, byte[])`**。structured type node なので **MessagePack が typed 木を素直に serialize**（バイナリ化の下地が型設計と直結）。

## 4. フェーズ計画（lockstep、大きく赤→緑）

0. ✅ audit（4カタログ）
0b. ✅ 設計判断（本ドキュメント）= **structured type + hybrid + BIR/META 統一 + positional tv + versioned carrier**
1. **共有型モデル**: kotc(Kotlin) と bir2cir/ilemit/facadegen(C#) に **単一の Type node 定義 + 単一 read/write helper**（`TypeNode` emit/parse を1箇所）。これが凍結の本体
2. **kotc**: `birType()` を structured Type emit へ（clr:/clrg:/@/shorthand/gp:/func:/sfunc:/nullable:/array: 撤廃 → fqn/tv/fn/nullable/array/byref）
3. **bir2cir**: 全 hand-scanner（SplitTopLevel×9 / FuncRetEnd / 3 scanner / BareOwner×3 / generic-args×3）を Type-walk に置換・削除。resolution（primitive/generic/ref）を fqn から導出
4. **ilemit**: `MapType`/token 解決を structured Type consume へ。`gp:` positional 化で `CanonSig`/`FindReflectedMethodBySigLoose` 削除
5. **facadegen**: META を structured Type へ統一 → `BirTokenToMeta` クラスタ削除。node cleanup（clr.* dead 削除、setField variant 統合）
6. **carrier**: `(version, byte[])` + `DecodeInlineBody` 単一化。stdlib ref/rt/jar 全再ビルド
7. **spec + validator**: `docs/bir-cir-spec.md`（node-kind 一覧 + Type schema + label/naming + carrier）+ 生 BIR/CIR と [KotlinInline] body の schema 検証（未知 k:/不正 Type/未知 version を gate で reddening）

各フェーズ後に全4ゲート XFAIL-zero を確認。型モデルは producer/consumer 同時 flip なので、1-4 は**一つの大きな lockstep**（途中の中間 commit は赤）。
