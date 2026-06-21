# BIR coverage — どの FIR/IR 構文を backend が lower できるか（チェックリスト）

`compiler/src/main/kotlin/clrc/backend/BirEmitter.kt` が Kotlin IR（Fir2Ir 出力＝FIR を降ろした IR）を BIR(JSON) に
変換する。**フロントエンド（FIR）は本物の `kotlin-compiler-embeddable` なので有効な Kotlin 構文はすべて解決**する。
ギャップが出るのは backend = BirEmitter 側だけ。

**重要な不変条件**: 未対応構文は黙ってクラッシュ/誤コンパイルせず、**必ずソース位置付きのコンパイルエラー**になる
（`BirEmitter.unsupported(node, what, detail)` が message collector に ERROR を報告し、`ClrBackendPhase` が
`COMPILATION_ERROR` を返す）。`unsupported()` 呼び出しは現在 **8 箇所**（B5 out/ref が surface 化）。

最終更新: 2026-06-21。検証方法: 広範な構文 probe ＋ 全サンプルコーパス（IL ~80 + 差分 ~36、すべて緑）。

---

## ✅ 明示ハンドル済みの IR ノード型（39）

`IrAnonymousInitializer IrBlock IrBlockBody IrBreak IrCall IrClassReference IrComposite IrConst IrConstructorCall
IrContinue IrDelegatingConstructorCall IrDoWhileLoop IrExpression IrExpressionBody IrFunctionExpression
IrFunctionReference IrGetClass IrGetEnumValue IrGetField IrGetObjectValue IrGetValue IrInstanceInitializerCall
IrLocalDelegatedProperty IrProperty IrPropertyReference IrReturn IrSetField IrSetValue IrSimpleFunction
IrSpreadElement IrStringConcatenation IrThrow IrTry IrTypeOperatorCall IrValueParameter IrVararg IrVariable
IrWhen IrWhileLoop`

これに加え、enum 構築（`IrEnumConstructorCall`）・分解・委譲 `by`・interface default・ローカル関数・無名オブジェクト
等は専用パスや上記ノードの組合せで処理され、probe で OK 確認済み。

---

## 🚧 BIR が処理しない FIR/IR 構文（gap チェックリスト）

> すべて **clean なソース位置付きコンパイルエラー**。実装したらここを `[x]` にする。

### A. 汎用フォールバック（`else -> unsupported`）に落ちる IR ノード型 — **厳密に確定済み**
検証法: kotlin-compiler-embeddable 2.2.0 jar から全 `Ir*` 具体ノード型を列挙 → `stmt()`/`expr()` の `is X ->`
ハンドル集合との差（16 型）→ 各候補を probe し、`unsupported()` が出す **実 `Impl` クラス名**で到達可否を確定。

**式の `else`（`BirEmitter:1175`）= 有効 Kotlin からは到達不能（純粋なセーフティネット）。** 未ハンドル式16型は
すべて (a) Kotlin/JS 専用 (b) 不正コードのみ (c) 自前で走らせない lowering の内部 (d) 自前 CPS で生成・消費する
coroutine 内部 (e) 専用パスで処理／定数畳み込み済み のいずれかで、probe でも1つも `else` に落ちなかった。

| 未ハンドル式16型 | 分類 | else 到達 |
|---|---|---|
| `IrDynamicMemberExpression` `IrDynamicOperatorExpression` | Kotlin/JS 専用 | ✗ |
| `IrErrorExpression` `IrErrorCallExpression` | 不正コードのみ | ✗ |
| `IrInlinedFunctionBlock` `IrReturnableBlock` `IrRawFunctionReference` `IrRichFunctionReference` `IrRichPropertyReference` | inline/K2 lowering 内部（未実行） | ✗（probe: 参照は `IrFunctionReference`/`IrPropertyReference` で handled） |
| `IrSuspendableExpression` `IrSuspensionPoint` | coroutine lowering 内部（自前 CPS） | ✗ |
| `IrEnumConstructorCall` | enum 専用パスで処理 | ✗（probe OK） |
| `IrConstantPrimitive` `IrConstantArray` `IrConstantObject` | const-eval（注釈は drop／定数は `IrConst` に畳む） | ✗（probe: const val/注釈配列 OK） |
| `IrLocalDelegatedPropertyReference` | `::localProp` は Kotlin で書けない | ✗（frontend reject） |

**文の `else`（`BirEmitter:893`）= かつて `IrClass`（関数ローカルクラス）だけが到達。✅ 2026-06-21 実装済みで到達不能化。**

> **結論: 両方の汎用 else は、有効な Kotlin からは到達不能になった（純粋なセーフティネット）。**

- [x] **`try`/`catch` を式として使う**（`val x = try{}catch{}` / `return try` / ラムダ内 try）— ✅ 2026-06-21（`il-tryexpr`、JVM 差分一致）。`IrTry` を value 位置で valueBlock + temp 代入に降ろす。→ **式 else 到達不能化**。
- [x] **関数ローカルクラス**（`fun f(){ class L(...){...} }`、local data class 含む）— ✅ 2026-06-21（`il-localclass`、JVM 差分一致）。`liftLocalClass`: トップレベル合成型 `<>dotkt_<Name>_N` に平坦化、参照する外側ローカル（囲み `this` 含む）を先頭 ctor 引数＋capture フィールドに（無名オブジェクトの機構を流用）、構築点 `L(args)` は capture 値を前置。複数インスタンス・loop 内宣言・data class equals まで対応。残: 可変キャプチャ（外側ローカルへ書込）は clean error（ref-cell 要）。→ **文 else 到達不能化**。

### B. 🛑 意図的にクリーンエラーにしている構文（recognized-but-refused・実装可能だが設計判断で保留）
**FIR は解決でき backend も認識するが、設計判断で実装せずソース位置付きエラーにしているもの。** 各々「なぜ保留か」と
「実装に要るもの」を明記。実装したら `[x]` にする。

| # | 構文 | 箇所 | なぜ保留（理由） | 実装に要るもの | 優先 |
|---|---|---|---|---|---|
| ~~B1~~ | ~~object 式から外側ローカルへ書込~~ ✅ 2026-06-21 実装（`il-refcell`） | — | heap ref-cell 実装済 | 単型化 `<>dotkt_Ref_<elem>{ var v }` に昇格、読書を `.v` に。lambda/object/local-class 横断、++/+= 含む | done |
| ~~B2~~ | ~~ローカルクラスから外側ローカルへ書込~~ ✅ 2026-06-21 実装（`il-refcell`） | — | B1 と同一機構 | — | done |
| B3 | **.NET メソッド参照** `obj::netMethod` / `NetType::method` | `BirEmitter:1289` | .NET メソッドの delegate 束縛は稀＋自明な回避策あり | lifted `__mref`/`boundDelegateNew` を .NET 受け手にも対応。回避: ラムダ `{ a -> x.m(a) }` | S–M |
| B4 | **非リテラル `String.format`** | `BirEmitter:2441` | printf↔.NET composite（`%d`↔`{0}`）は非互換。実行時変換には runtime helper が要る | DotKt.Runtime に printf→composite 変換器、または `String.Format` 直叩き | S |
| ~~B5~~ | ~~`out`/`ref` パラメータ~~ ✅ 2026-06-21 実装（`il-outref`） | — | __clrout/__clrref マーカー方式で実装済 | frontend に `fun <T>__clrout/__clrref(x:T):T` を注入→backend がマーカーを読み arg を `byref:` 型＋lvalue アドレス渡し、ilemit が `MakeByRefType`+`ldloca`。out/ref 両対応 | done |

> B1/B2（heap ref-cell）✅・B5（out/ref）✅ 実装済。残: 回避策のある B3（.NET メソッド参照）、B4（非リテラル String.format）。

### B'. 未実装 stdlib 関数（実装すれば動く・ガードがクリーンエラー化）
`BirEmitter:2551` のガードが `kotlin.collections/sequences/text/ranges/comparisons` の未対応 free/extension 関数を拒否。
- [x] `partition` / `withIndex`（→ ValueTuple、`for ((i,v) in …)` 分解）/ `associate` / `scan`・`runningFold` / `windowed` / `getOrElse(index){}` — ✅ 2026-06-21（`il-collops2`、custom ilemit ノード linqPartition/WithIndex/Associate/Scan/Windowed/GetOrElse、JVM 差分一致）。併せて `IndexedValue<T>`→`ValueTuple<int,T>`（.index/.value/component1/2）と bare `println()` の修正。
- [ ] `sortedWith(compareBy{})`（compareBy が key を `Comparable<*>` に erase＝実キー型が取れない。単一キーは `sortedBy`/`sortedByDescending` で対応済＝**唯一の残**）
- 注: `map/filter/flatMap/flatten/mapNotNull/filterNotNull/mapIndexed/chunked/average/indexOf/zip/groupBy/associate{With,By}/fold/reduce/sum/sumOf/...` 等 40+ は対応済み。

### B''. 稀な解決不能エッジ（ほぼ書けない・clean error）
- [ ] **解決不能なコンストラクタ参照** `::Ctor`（class 解決不可）— `BirEmitter:1255`。ユーザ/注入型の `::Ctor` は対応済み。
- [ ] **非 simple 関数参照**（fake override 等）— `BirEmitter:1258`。
- [ ] **解決不能な委譲プロパティ**（lazy/カスタム getValue/Map 以外）— `BirEmitter:2354`。

### C. 設計上 BIR に現れない IR 型（対応不要）
- `IrDynamicOperatorExpression` / `IrDynamicMemberExpression` — Kotlin/JS 専用、CLR 文脈に出ない。
- `IrErrorExpression` / `IrErrorCallExpression` — コンパイルエラーのあるコードでのみ生成。
- `IrReturnableBlock` / `IrInlinedFunctionBlock` — stdlib inline lowering の内部（本パイプラインは自前で inline するため出ない）。
- `IrSuspendableExpression` / `IrSuspensionPoint` — coroutine lowering の内部（自前 CPS 変換で生成・消費）。
- `IrRawFunctionReference` / `IrConstantValue`（const-eval）等 — 通常の式位置には現れない。

---

## `unsupported()` 呼び出し一覧（10）= 上記 gap の発生点
| # | 位置 | 種別 | 上記項目 |
|---|---|---|---|
| 1 | `BirEmitter:898`  | 汎用 statement フォールバック（**有効 Kotlin からは到達不能**） | A |
| 2 | `BirEmitter:1182` | 汎用 expression フォールバック（**到達不能**） | A |
| 3 | `BirEmitter:1255` | コンストラクタ参照（class 解決不可） | B'' |
| 4 | `BirEmitter:1258` | 非 simple 関数参照 | B'' |
| 5 | `BirEmitter:1289` | .NET メソッド参照 | B3 |
| 6 | `BirEmitter:1656` | 可変キャプチャ・ローカルクラス | B2 |
| 7 | `BirEmitter:1679` | 可変キャプチャ・object 式 | B1 |
| 8 | `BirEmitter:2354` | 解決不能な委譲プロパティ | B'' |
| 9 | `BirEmitter:2441` | 非リテラル String.format | B4 |
| 10 | `BirEmitter:2551` | 未実装 stdlib 関数 | B' |

> 行番号は更新で動く。再取得: `grep -n 'unsupported(' compiler/src/main/kotlin/clrc/backend/BirEmitter.kt`
> facadegen 側の意図的省略（out/ref=非 surface、façade-.kt 経路のクロス型=Any? 縮退）は B5 と §A 表に記載。
