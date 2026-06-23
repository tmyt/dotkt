# Future work — interop / consuming DotKt-built assemblies from Kotlin

後々やりたいことのメモ（2026-06-23 起票）。まだ未着手。優先度・設計は未確定。

## 1. ktproj の `ProjectReference`

`.ktproj` から別プロジェクト（`.csproj` / 別の `.ktproj`）を `<ProjectReference>` で参照できるようにする。
現状は `<PackageReference>` / `<Reference>`（= 解決済みアセンブリ）経由の .NET 型注入は通る（`@(ReferencePath)`
を facadegen に渡す）が、`ProjectReference` の出力をビルド順序込みで正しく食わせる導線が要る。

- ビルド順序: 参照先プロジェクトを先にビルドし、その出力 dll を `@(ReferencePath)` に載せる（MSBuild の
  `ResolveProjectReferences` 連携）。
- 別 `.ktproj` を参照する場合は「DotKt でビルドしたアセンブリを Kotlin 側から再 Emit/消費する」(#2) と直結。

## 2. DotKt でビルドしたアセンブリを Kotlin 側で正しく消費（再 Emit）する仕組み

DotKt が出した dll を、別の Kotlin コンパイルから `import` して使うとき、.NET の素朴な型/メンバ射影では
**Kotlin 固有の構造が落ちる**。次を復元できるようにする:

- **top level function**（Kotlin のトップレベル関数 = .NET では `XxxKt` クラスの static メソッド）
- **suspend fun**（ABI: `suspend ⇔ Task<T>`。呼ぶ側で suspend として認識し直す必要がある）
- **inline fun**（インライン本体／`reified`／`crossinline`・`noinline` の情報）
- **infix fun / operator fun** など（呼び出し構文に効くメタ情報）
- **名前空間の自動射影**（下記 #3）

### 方針: メタ情報を .NET 属性でフラグして相互運用

.NET の素のシグネチャだけでは上記の Kotlin 性質が判別できないので、**DotKt が emit する時に属性で印を付け、
消費する時に読む**。属性の例（名前は仮）:

- `[DotKtSuspendable]` … この static メソッドは元 `suspend fun`（`Task<T>` 戻りを suspend として再射影）
- top level function / inline / infix なども同様に属性でフラグ:
  - `[DotKtTopLevel]`（`XxxKt` の static を Kotlin トップレベル関数として見せる）
  - `[DotKtInline]`（+ 必要なら本体やインライン種別。`reified` は別途）
    - ⚠️ **そもそも `inline` をアセンブリ境界に切り出す意味があるか自体が別問題**。実インライン展開には
      呼び出し側に**本体（IR）が必要**で、属性フラグだけでは展開できない（JVM の kotlinx は `@Metadata` に
      本体を持つ）。選択肢: (a) 本体を何らかの形で同梱して跨ぎインラインする（重い）／(b) 跨ぎでは普通の
      呼び出しに格下げ（`inline` の non-local return・`reified` 等が絡むと不可な場合あり）。属性は「意図の
      記録」に留まる可能性が高い。要設計。
  - `[DotKtInfix]` / `[DotKtOperator(...)]`
- 消費側コンパイル（FIR 注入）で、これらの属性を見て元の Kotlin 宣言形へ復元する。

> 既存の forward 方向（`@Clr*` で .NET → Kotlin）と対になる reverse 方向のメタ。
> 関連: メモリ `r1-compiletime-reference-blocker`（compile-time `<Reference>` の MetadataLoadContext 課題）、
> `csharp-retirement-design`（R-1）。

## 3. 名前空間の自動射影（assembly 単位）— ✅ 実装済み（2026-06-24）

`kotlinx.coroutines` ⇔ `DotKt.Coroutines` のような **名前空間の読み替え**をアセンブリ単位で宣言できる:

```kotlin
// producer 側（.ktproj）— ライブラリは .NET 名前空間 DotKt.Coroutines に出るが Kotlin では kotlinx.coroutines として見せる
// <ItemGroup><DotKtNamespaceProjection Include="kotlinx.coroutines=DotKt.Coroutines" /></ItemGroup>
```

実装（**2 引数の prefix 射影** `[assembly: DotKtNamespaceProjection(kotlinPrefix, dotNetPrefix)]`、AllowMultiple）:
- **属性**: `DotKt.Metadata.DotKtNamespaceProjectionAttribute`（assembly 対象）。
- **producer**: `ilemit --ns-projection <kotlinPrefix>=<dotNetPrefix>` が刻む（SDK は `@(DotKtNamespaceProjection)` から渡す）。
- **消費側 facadegen**: 参照アセンブリの属性を読み、メタに `nsproj <k> <d>` 行を出力。import 解決で **逆射影**
  （`kotlinx.coroutines.X` → 実体 `DotKt.Coroutines.X`、ワイルドカード `import kotlinx.coroutines.*` も `TypesInNamespace` で射影）。
- **消費側 injector**: `nsproj` を読み、`namespaceOf`（.NET 名前空間 → Kotlin パッケージ）で **順射影**。型は Kotlin パッケージ
  `kotlinx.coroutines` 配下に登録され、バックエンドはレジストリ（Kotlin fqn → 実 .NET 名）で実体を呼ぶ。
- **ImportScan 修正**: `kotlinx.*` は stdlib ではなく外部ライブラリなので、スキャナの除外を `kotlin.`（stdlib のみ）に絞った
  （以前は `startsWith("kotlin")` で `kotlinx` も巻き込んでいた）。
- prefix ベースなので sub-package（`kotlinx.coroutines.flow`）も自動追従。`scripts/verify-roundtrip.sh` の `roundtrip-nsproj` で常設。
- これで「kotlinx ライブラリを CLR 向けにコンパイルして配布する」構想（メモリ `dotkt-compile-kotlin-libraries`
  / `dotktx-coroutines-path-b`）の消費体験が `import kotlinx.coroutines.*` で素直になる。
  （前提: ライブラリ側が実際に `DotKt.Coroutines` 名前空間に出ること。`package DotKt.Coroutines` で書くか、将来の
  ビルド時 namespace リネーム機能で実現。）

## 4. 推移的（transitive / on-demand）型注入

**問題（ユーザー指摘 2026-06-23）**: 注入は **import 駆動**（C-2 / [[s5-fir-injection-seam]]）なので、`import` した型しか FIR に materialize されない。ある注入型のメンバ・シグネチャに**間接的に出るだけの型**（イベントハンドラ引数、戻り値、プロパティ型）は自動注入されず、`e.Message` のように「値の型は分かっているのにメンバが見えない」という直感に反する挙動になる（facadegen は簡易名で出すが、未 import だと injector が解決できず実質 `Any?`）。

**あるべき姿**: 型 A を注入したら、A のメンバの引数/戻り値/プロパティ型を**連鎖的に注入**。そうすれば中間型を import せずに `a.member().memberOfB()` が通る。

**注意/設計事項**: WinUI 等は参照型が数百に膨れるので素朴な全推移は爆発する。要・制御:
- 深さ制限（1〜2 段）、または
- 「実際にアクセスされたメンバ経由のみ遅延注入」（オンデマンド）、または
- import 集合のスキャン段で member-signature 型を集合に足す（facadegen 側でクロージャを取る）。

現状の運用は「触る型は明示 import」。優先度は中（WinUI のような型リッチな相互運用で効く）。

## 5. ラウンドトリップ抜け漏れ一覧（検証済み 2026-06-24）

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

**残る既知の限界**（往復のブロッカーではない）: デフォルト引数の**名前付き中間省略**（`copy(y=5)` は非定数デフォルト `this.x`
を要し JVM の `copy$default` 相当が必要）・**ジェネリッククラスの消費側**・**object シングルト**ンは別途。`private`/`internal`
メンバは非エクスポートで往復対象外。

**設計メモ**: プロパティ往復は emit 側の .NET プロパティ化（private backing + PropertyDef + accessor）も選択肢だったが、
ilemit の既存 field-fallback を活かす facadegen 側復元の方が低リスクで全 property サンプル無回帰を達成。`private set` の
非対称可視性は `[KotlinReadOnly]` マーカーで運ぶ（フィールドは public のまま＝同一モジュール/C# 書込は可、Kotlin 消費側
のみ読取専用）。新メタ属性は `[KotlinNullable]`・`[KotlinReadOnly]`（`DotKt.Runtime/Metadata.cs`）。

## メモ

- #2/#3 は対で、「DotKt 製ライブラリ（dotktx.* 含む）を Kotlin から自然に使う」体験を作る。
- forward interop（.NET → Kotlin、`s5-fir-injection-seam`）は完成済み。これは reverse（Kotlin製 → Kotlin）方向。
- 静的メンバの companion 必須ルール（メモリ `injected-static-members-need-companion`）と同様、ここでも
  「.NET の素の形」と「Kotlin の意図した形」のギャップを属性メタで埋めるのが基本戦略。
