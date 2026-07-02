# Future work — interop / consuming DotKt-built assemblies from Kotlin

後々やりたいことのメモ（2026-06-23 起票）。

> **状態 (2026-07-02 見直し)**: #1（ProjectReference）/ #2（DotKt 製アセンブリを Kotlin として消費）/ #5（往復抜け漏れ）は **DONE で着地済み**、#3（名前空間射影）は削除済み、**#4（推移的/オンデマンド型注入）も DONE**（本文参照 — facadegen の到達可能閉包 BFS で解消、`cases/il-transinj` で常設検証）。現行アーキテクチャの正は [docs/ship-tasks.md](ship-tasks.md) §0。
>
> 着地の要点: infix/operator/suspend/top-level/inline（non-local return 含む）は **アセンブリ埋め込みの DotKt メタ属性**（`DotKt.Runtime.CompilerServices.*` 配下の `[Kotlin*]` 型）で復元（メモリ `embedded-metadata-attrs-embedded-nrt-nullability`、`kotlin-modifier-roundtrip`）。reverse の ProjectReference は Cecil ベースの retarget ツールで実現（サンプル `ktproj-bidir`、メモリ `r1-reverse-projectreference-retargeter`）。`scripts/verify-roundtrip.sh` 常設。

## 1. ktproj の `ProjectReference` — ✅ DONE

> **DONE**: `.ktproj` からの `<ProjectReference>`（`.csproj` / 別 `.ktproj`）が着地。reverse 方向（C#→Kotlin の compile-time 参照）は Cecil ベースの retarget ツール（CoreLib→contract 参照の付け替え + 3 つの MSBuild 修正）で実現、サンプル `ktproj-bidir`。メモリ `r1-reverse-projectreference-retargeter` / `r1-compiletime-reference-blocker`。以下は起票当時の課題メモ。

`.ktproj` から別プロジェクト（`.csproj` / 別の `.ktproj`）を `<ProjectReference>` で参照できるようにする。
現状は `<PackageReference>` / `<Reference>`（= 解決済みアセンブリ）経由の .NET 型注入は通る（`@(ReferencePath)`
を facadegen に渡す）が、`ProjectReference` の出力をビルド順序込みで正しく食わせる導線が要る。

- ビルド順序: 参照先プロジェクトを先にビルドし、その出力 dll を `@(ReferencePath)` に載せる（MSBuild の
  `ResolveProjectReferences` 連携）。
- 別 `.ktproj` を参照する場合は「DotKt でビルドしたアセンブリを Kotlin 側から再 Emit/消費する」(#2) と直結。

## 2. DotKt でビルドしたアセンブリを Kotlin 側で正しく消費（再 Emit）する仕組み — ✅ DONE

> **DONE**: top-level / suspend / inline（non-local return 含む）/ infix / operator の Kotlin 性質はすべてクロスモジュールで復元される（`reified` は落とす）。`scripts/verify-roundtrip.sh`・`docs/design-kotlin-metadata-attributes.md`、メモリ `kotlin-modifier-roundtrip`。
> 起票時に「（名前は仮）」とした属性名は**確定スキームに置き換わった**: 仮称 `[DotKtSuspendable]`/`[DotKtTopLevel]`/`[DotKtInline]` ではなく、**アセンブリ単位で埋め込まれる `DotKt.Runtime.CompilerServices.*` 配下の `[Kotlin*]` 型**で運ぶ（メモリ `embedded-metadata-attrs-embedded-nrt-nullability`）。以下は起票当時の構想メモ。

DotKt が出した dll を、別の Kotlin コンパイルから `import` して使うとき、.NET の素朴な型/メンバ射影では
**Kotlin 固有の構造が落ちる**。次を復元できるようにする:

- **top level function**（Kotlin のトップレベル関数 = .NET では `XxxKt` クラスの static メソッド）
- **suspend fun**（ABI: `suspend ⇔ Task<T>`。呼ぶ側で suspend として認識し直す必要がある）
- **inline fun**（インライン本体／`reified`／`crossinline`・`noinline` の情報）
- **infix fun / operator fun** など（呼び出し構文に効くメタ情報）
- **名前空間の自動射影**（下記 #3）

### 方針: メタ情報を .NET 属性でフラグして相互運用

.NET の素のシグネチャだけでは上記の Kotlin 性質が判別できないので、**DotKt が emit する時に属性で印を付け、
消費する時に読む**。**確定スキーム**（起票時の仮称 `[DotKt*]` は採用されず、アセンブリ埋め込みの
`DotKt.Runtime.CompilerServices.*` 配下 `[Kotlin*]` 型に置換）:

- 元 `suspend fun`（`Task<T>` 戻りを suspend として再射影）を `[Kotlin*]` 属性でフラグ
- top level function / inline / infix なども同様に `[Kotlin*]` 属性でフラグ:
  - top-level（`XxxKt` の static を Kotlin トップレベル関数として見せる）
  - inline（+ 必要なら本体やインライン種別。`reified` は別途。cross-module non-local return は `[KotlinInline]` の BIR スプライスで実現）
    - ⚠️ **そもそも `inline` をアセンブリ境界に切り出す意味があるか自体が別問題**。実インライン展開には
      呼び出し側に**本体（IR）が必要**で、属性フラグだけでは展開できない（JVM の kotlinx は `@Metadata` に
      本体を持つ）。選択肢: (a) 本体を何らかの形で同梱して跨ぎインラインする（重い）／(b) 跨ぎでは普通の
      呼び出しに格下げ（`inline` の non-local return・`reified` 等が絡むと不可な場合あり）。属性は「意図の
      記録」に留まる可能性が高い。要設計。
  - infix / operator も同様に `[Kotlin*]` 属性
- 消費側コンパイル（FIR 注入）で、これらの属性を見て元の Kotlin 宣言形へ復元する。

> 既存の forward 方向（`@Clr*` で .NET → Kotlin）と対になる reverse 方向のメタ。
> 関連: メモリ `r1-compiletime-reference-blocker`（compile-time `<Reference>` の MetadataLoadContext 課題）、
> `csharp-retirement-design`（R-1）。

## 3. 名前空間の自動射影（assembly 単位）— ❌ 削除（2026-06-28）

`[DotKtNamespaceProjection]` による Kotlin パッケージ ⇔ .NET 名前空間の読み替え（`kotlinx.coroutines` ⇔ `DotKt.Coroutines`
など）は**削除した**。用途が無いと判断したため(producer/ilemit/bir2cir/facadegen/kotc/属性/round-trip テストを撤去)。
DotKt アセンブリの型は実 .NET 名前空間 = Kotlin パッケージとして 1:1 で見える。`kotlinx.coroutines` のようなライブラリは
最初から `package kotlinx.coroutines` で書く（名前空間リネームに頼らない）。

## 4. 推移的（transitive / on-demand）型注入 — ✅ DONE（2026-07-02 検証）

> **DONE**: facadegen `EmitMeta` が import 型をシードに、API サーフェス参照型（基底鎖・インターフェース・
> メンバの戻り値/引数/要素/型引数）を **BFS で到達可能閉包ごと注入**する。採った設計は下記候補の 3 番目
> （facadegen 側でクロージャ）＋爆発ガードは深さ制限ではなく **ハードキャップ 5000 型**（`NO_INJECT` の
> BCL 特別型・`kotlin.*` は除外、dedupe、1 型の反射失敗は warning でスキップ）。深さ制限を捨てた理由:
> 「N+1 hop 目でまた `Any?` に切れる」段差が無く、実測の閉包は小さい（Console+Exception で ~265 型、
> WinUI 級でも数百）。未 import の 2 hop 連鎖（`a.member(): B` → `b.member(): C`）は `cases/il-transinj`
> （verify-il gate 常設）で検証。構築ジェネリック・メンバ型（interop-feedback (3)）も同時に解消済み。

**問題（ユーザー指摘 2026-06-23）**: 注入は **import 駆動**（C-2 / [[s5-fir-injection-seam]]）なので、`import` した型しか FIR に materialize されない。ある注入型のメンバ・シグネチャに**間接的に出るだけの型**（イベントハンドラ引数、戻り値、プロパティ型）は自動注入されず、`e.Message` のように「値の型は分かっているのにメンバが見えない」という直感に反する挙動になる（facadegen は簡易名で出すが、未 import だと injector が解決できず実質 `Any?`）。

**あるべき姿**: 型 A を注入したら、A のメンバの引数/戻り値/プロパティ型を**連鎖的に注入**。そうすれば中間型を import せずに `a.member().memberOfB()` が通る。

**注意/設計事項**: WinUI 等は参照型が数百に膨れるので素朴な全推移は爆発する。要・制御:
- 深さ制限（1〜2 段）、または
- 「実際にアクセスされたメンバ経由のみ遅延注入」（オンデマンド）、または
- import 集合のスキャン段で member-signature 型を集合に足す（facadegen 側でクロージャを取る）。

現状の運用は「触る型は明示 import」。優先度は中（WinUI のような型リッチな相互運用で効く）。

## 5. ラウンドトリップ抜け漏れ一覧（検証済み 2026-06-24）— ✅ DONE（残既知限界のみ）

> **DONE**: 下表の構文はすべてクロスモジュールで Kotlin 形に復元され、`scripts/verify-roundtrip.sh` で常設緑。残るのは「残る既知の限界」（object シングルトン等・往復ブロッカーではない）のみ。

#2 のうち **infix / operator（`+` `<` `in` `()` `[]`…）/ suspend / top-level / inline（non-local return 含む）/
reified（落とす）** は実装済み（`docs/design-kotlin-metadata-attributes.md`、`scripts/verify-roundtrip.sh`）。
クロスモジュール消費を実機プローブした結果、**まだ Kotlin 形へ復元されない**もの:

**完了（2026-06-24 ✅ 全解消）**: 下表の構文はすべてクロスモジュールで Kotlin 形に復元される。`scripts/verify-roundtrip.sh`
roundtrip-pkg で常設、各実装で verify-il 緑。

| 構文 | 復元方法 |
|---|---|
| **プロパティ** `val`/`var`・カスタム getter | facadegen が public field / 非 special な `get_`/`set_` を `prop` 化、ilemit `clrPropGet/Set` が field→`get_`/`set_` フォールバック（emit refactor 不要・既存 field-fallback 活用） |
| **非対称可視性** `val` / `var ... private set` | not-publicly-settable な backing field に `[KotlinReadOnly]`、facadegen が `ro` で出す → 消費側は `val`（外部書込拒否）。`val x` が `rw` で出ていた健全性バグも同時解消 |
| **拡張関数** `fun T.f()` / **トップレベル拡張演算子** `operator fun T.plus` | facadegen `__self`→`,ext`、injector `extensionReceiverType`（operator と合成）。`isBuiltin` の top-level 誤判定（`+`→`bin`）も修正 |
| **拡張プロパティ** `val T.p` | BirEmitter が backing-field 無しトップレベルプロパティの `get_/set_<name>(__self)` を static 出力、facadegen `tlextprop`、injector `createTopLevelProperty + extensionReceiverType`、backend が `x.p`→`clrStatic get_/set_(receiver)` |
| **vararg** `vararg xs: T` | ilemit `[ParamArray]`、facadegen `vararg:<elem>`、injector `isVararg`。空 `f()` は ilemit が空配列を補填 |
| **デフォルト引数** `f(x = 5)`（定数・末尾省略） | @JvmOverloads 方式：末尾デフォルト T 個 → T+1 オーバーロード注入（`hasDefaultValue` は fir2ir を STUB で落とすため不可）。ilemit `[DefaultParameterValue]` を `EmitDefaultArg` が呼出側で補填 |
| **nullable** `String?` | BIR の param/return nullable → ilemit `[KotlinNullable(mask)]`（bit0=戻り, biti+1=param i）→ facadegen `?` サフィックス → injector `withNullability`。型レベルで本物（null 非許容 param への null は拒否） |
| **data class** | 派生で自動成立（プロパティ + componentN operator〔往復済〕 + equals/toString〔.NET ディスパッチ〕）。`data` 修飾子は復元しない（消費側 fir2ir が二重合成して衝突するため・メンバ駆動で十分） |

**ジェネリクスの往復は全位置・全機能組み合わせで成立**（2026-06-24）: ジェネリッククラス（`Box<T>`、`operator`/`infix`
メンバ・ジェネリックメソッド `fun <R> mapTo`）、2型パラメータ、戻り/引数位置のジェネリックユーザ型（`fun <T> wrap(x:T):Box<T>`）、
ジェネリック拡張関数・拡張演算子、ジェネリック top-level `suspend`、nullable/デフォルト引数/vararg との組み合わせ。`verify-roundtrip.sh`
（roundtrip-generic）で網羅。三層の修正: facadegen（root-namespace 開放名の先頭ドット `.Box`／シグネチャ内ジェネリックユーザ型を
`Any?` に落としていた）・ilemit（CLR アリティ名 ``Box`1``／ジェネリック拡張の `__self` シェイプ欠落／ジェネリック+デフォルト引数の
オーバーロード解決）・injector（`coneOf` が `generic:Box:T` 内の型変数を `Any?` に潰す／ジェネリック top-level 経路が
ext-receiver・inline・infix・operator・vararg・デフォルト引数を無視）。

**高階ジェネリクス（ラムダ引数にネストしたジェネリックユーザ型）の往復は成立**（2026-06-24）: `(Box<U>) -> Box<V>` のような
関数型パラメータの arg/ret がジェネリックユーザ型でも往復する（top-level/メンバ/拡張/infix/operator/inline）。鍵は**メタ型文法を
再帰（括弧）化**したこと（`generic:Box[V]`・`func:[ret,a,b]`、injector はブラケット深さ0で分割）。従来は平坦文法（`func:<ret>:<args>`）
で `func:` 内に `generic:` をネストできず、`Any?` に潰して型変数が推論不能になっていた。`verify-roundtrip.sh`（roundtrip-generic-hof）。

**メンバ拡張関数の往復も成立**（2026-06-24）: `class C { fun T.f() }`（plain/infix/operator/inline+ジェネリックメソッド/protected）を
`with(c) { x.f() }` で消費できる。**単一モジュールの既存バグも修正**: メンバ拡張の2つの暗黙レシーバ（dispatch `this` と拡張 `__self`、
IR ではどちらも名前 `<this>`）が名前キーで取り違えられ誤結果になっていた→シンボル同一性で `__self` を置換、呼出は囲みインスタンスに
dispatch して拡張レシーバを先頭に付与。facadegen がメンバ `fun` 行に `,ext`/`,inline` を付与、injector が復元（`fun` 行パーサが
`,ext`/`,inline` を落としていたのも修正）。`verify-roundtrip.sh`（roundtrip-memext）。

**メンバ拡張プロパティの往復も成立**（2026-06-24）: `class C { val T.p }`（`var` も、public+protected）。`memextprop` メタ行で
`get_p(__self)`/`set_p(__self,v)` メンバアクセサを運び、injector が拡張レシーバ付きメンバプロパティとして復元、`with(c)` 内の
`x.p` 読書は C の `get_`/`set_` に拡張レシーバを先頭付与してルーティング。`verify-roundtrip.sh`（roundtrip-memext2）。

**`suspend` メンバ拡張の往復も成立**（2026-06-24）: `class C { suspend fun T.f() }`（public+protected）を自然な
`with(c){ x.f() }` で消費。2つの一般 coroutine 修正で実現: ①状態機械が top-level 型で owner の protected/private に触れると
`MethodAccessException`→**SM を owner にネスト**（非ジェネリック owner）して到達可能に。②**inline スコープ関数内の suspend 呼び出し**
（`with(x){ f() }`・`run`/`let`/`apply`/`also`）を**状態機械へ CPS 線形化**（旧 `InvalidProgram`）: スコープのレシーバを SM フィールドに
束縛、`this`/`it` を置換、ラムダ本体の suspend を本物の await ポイントに（ネストしたスコープ関数・suspend 引数・複数文本体も対応）。
`verify-roundtrip.sh`（roundtrip-memext2）。残: スコープ関数を**部分式**で使う（`c.apply{ f() }.x`）はクリーンエラー（先に `val` に束縛）。

**残る既知の限界**（往復のブロッカーではない・いずれもソース位置付きクリーンエラー）:
- **コンテキストレシーバ/パラメータ**（`context(B) fun A.f()`）— フロントエンドが実験的機能として拒否（`-Xcontext-parameters` 必須）。
  「優勝」級の `protected inline suspend fun <reified T> ...context(B)...` はそもそもコンパイルされない。
- **object シングルトン**（往復消費）。
- **ジェネリッククラスのメンバ `suspend`**（`class Box<T> { suspend fun f(): T }`）— 単一モジュールでも `BadImageFormatException`
  で落ちる既存の coroutine×ジェネリッククラスの穴。
- **`kotlin.Pair`/`Triple` をジェネリック型引数で構築**（`Pair<T, T>(a, b)`）— ilemit `ParseOwner` が落ちる既存バグ。`Pair2<A,B>` で代替可。
- `private`/`internal` メンバは非エクスポートで往復対象外。

**設計メモ**: プロパティ往復は emit 側の .NET プロパティ化（private backing + PropertyDef + accessor）も選択肢だったが、
ilemit の既存 field-fallback を活かす facadegen 側復元の方が低リスクで全 property サンプル無回帰を達成。`private set` の
非対称可視性は `[KotlinReadOnly]` マーカーで運ぶ（フィールドは public のまま＝同一モジュール/C# 書込は可、Kotlin 消費側
のみ読取専用）。新メタ属性は `[KotlinNullable]`・`[KotlinReadOnly]`。
> **更新 (2026-06-30)**: 起票当時これらは `DotKt.Runtime/Metadata.cs` にあったが、`DotKt.Runtime` は廃止中で、メタ属性は
> **アセンブリ単位で埋め込まれる `DotKt.Runtime.CompilerServices.*` 型**に移行した（IN-FLIGHT、メモリ `embedded-metadata-attrs-embedded-nrt-nullability`）。

## メモ

- #2/#3 は対で、「DotKt 製ライブラリ（dotktx.* 含む）を Kotlin から自然に使う」体験を作る。
- forward interop（.NET → Kotlin、`s5-fir-injection-seam`）は完成済み。これは reverse（Kotlin製 → Kotlin）方向。
- 静的メンバの companion 必須ルール（メモリ `injected-static-members-need-companion`）と同様、ここでも
  「.NET の素の形」と「Kotlin の意図した形」のギャップを属性メタで埋めるのが基本戦略。
