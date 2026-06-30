# DotKt 言語基盤へのフィードバック — 実 .NET 型 interop の課題

> **状態 (2026-06-30 見直し)**: これは 2026-06 の WinUI bring-up スナップショット（当時の不具合一覧）であり、**生きたブロッカー一覧ではない**。下記のうち (1)(2)(7)(8)(9) は既に解消済み（各項目に RESOLVED を付記）。現行アーキテクチャの正は [docs/ship-tasks.md](ship-tasks.md) §0。
>
> 解消の要点: .NET 代入互換（基底クラス＋ジェネリック/明示/自己参照インターフェース）が完成し、WinUI の Counter は実機で動作する（メモリ `inheritance-interface-injection`）。レシーバ付きラムダ・多段クロージャキャプチャ・ネストパラメータラムダは Flow の `capturedVars` 修正と suspend レシーバラムダ対応で解消。`<KotlinClrType>` は旧名で現行は `<DotKtImport>`（本文 (5) 参照）。

`dotktx.ui.winui`（WinUI を Kotlin で宣言的に駆動するライブラリ）を実装する中で、DotKt の
**型インジェクタ（`facadegen`）** と **IL バックエンド（`ilemit`）** に、実用上ふさぎたい
制約が見つかった。Windows 実機ビルドのエラーと、各ツールのソース解析から、症状・根本原因・最小再現・
修正方針をまとめる。`file:line` は `kotlin.net` リポジトリの該当箇所。

総括: **WinUI のような「継承と protected 仮想メソッドが中心の API」は、現状の façade-free 注入では
ほぼ駆動できない**。下記 Part 1 の (1)(2) が本丸。Part 2 は別件（IL バックエンドの閉包/状態系）で、
DSL の書き味（Compose 風レシーバラムダなど）を制約した。

---

## Part 1. 型インジェクタ（facadegen）— WinUI を阻む本丸

### (1) 注入型に基底クラス／インターフェース階層が無い ★最優先 — ✅ RESOLVED (2026-06-22)
> **RESOLVED**: .NET 代入互換が完成（基底クラス鎖＋ジェネリック/明示/自己参照インターフェースを注入）。`StackPanel` を `Panel` として、`TextBlock` を `UIElement` として渡せる。メモリ `inheritance-interface-injection`。
- **症状（Windows 実機）**:
  `argument type mismatch: actual type is 'TextBlock', but 'UIElement' was expected`
  （`Panel.Children.Add(UIElement)` に `TextBlock` を渡せない。`StackPanel` を `Panel` として扱えない）
- **根本原因**: 注入メタは `class <Name> <DotNetName> <open|sealed> [<TypeParam>...]`（`Program.cs:171`）
  しか持たず、**基底型・実装インターフェースを一切出力しない**。よって注入された各型は互いに無関係。
- **最小再現**: `import Microsoft.UI.Xaml.Controls.TextBlock` / `...StackPanel` / `...Panel` して
  `panel.Children.Add(textBlock)` を呼ぶ。
- **修正方針**: メタに基底クラス鎖と実装インターフェースを出す。FIR インジェクタ側で、
  少なくとも「同時に注入されている祖先型」へはスーパータイプを張る。理想は、参照アセンブリから
  祖先・実装インターフェースを**自動的に併せて注入**して鎖を再構築する（→ (6) と同根）。

### (2) public メンバしか注入しない → protected 仮想メソッドを override できない ★最優先 — ✅ RESOLVED (2026-06-22)
> **RESOLVED**: protected/virtual メンバを注入し `open`/`abstract` として出すので Kotlin サブクラスから override 可能。`Application.OnLaunched` の override が通り、WinUI Counter が実機で起動する。
- **症状（Windows 実機）**: `'OnLaunched' overrides nothing`
  （`Microsoft.UI.Xaml.Application` の `protected virtual void OnLaunched(...)` を override できない）
- **根本原因**: メンバ収集が `BindingFlags.Public | Instance/Static`（`Program.cs:142, 222, 332`）。
  **protected（Family/FamORAssem）が落ちる**。WinUI / Avalonia / WPF のアプリモデルは
  「protected 仮想ライフサイクルメソッドを override する」のが基本なので、これが無いと起動部が書けない。
- **修正方針**: protected メンバも注入する。さらに virtual/abstract を `open`/`abstract` として出し、
  Kotlin サブクラスから `override` 可能にする（注入型の継承可否は既に `open|sealed` を出しているので拡張は素直）。

### (3) ジェネリック型のメンバが `Any?` に潰れる
- **症状（Windows 実機）**: `unresolved reference 'MergedDictionaries'`
  （`Application.Resources.MergedDictionaries`。`MergedDictionaries` は `IList<ResourceDictionary>`）
- **根本原因**: `CrossType` で `if (t.IsGenericType ...) return "Any?"`（`Program.cs:401`）。
  ジェネリック構築型は戻り値・引数とも `Any?` 化し、その先のメンバ（`.Add` 等）に到達できない。
- **修正方針**: 最低限 `IList<T>` / `ICollection<T>` / `IEnumerable<T>` / `List<T>` の構築型を
  注入対象にし、要素型が注入済みなら構築ジェネリックとして出す（少なくとも for-in と `Add`/`get` が通る形で）。

### (4) デリゲート型引数が `Any?` に潰れ、オーバーロードが曖昧になる
- **症状（Windows 実機）**:
  `overload resolution ambiguity`（`new Thread(ThreadStart)` と `new Thread(ParameterizedThreadStart)` が
  どちらも `Thread(Any?)` になり区別不能）, `unresolved reference 'Start'`（`Application.Start(ApplicationInitializationCallback)`）
- **根本原因**: デリゲート型は `Supported`（`Program.cs:360`）を通るが、`Map` で `Any?` 化される（(3) と同じ経路）。
- **修正方針**: デリゲート型を **Kotlin の関数型 `(A, B) -> R` にマップ**する（バックエンドはラムダ→デリゲート
  変換を既に持つ）。これでオーバーロードが区別でき、ラムダが正しいデリゲートにバインドされる。
  併せて、イベントの `add_X` が 2 引数ハンドラを受けるのと整合する。

### (5) import パーサが脆い（aliased import を黙って無視）★メモ: 安定化したい
- **症状（Windows 実機）**: `unresolved reference 'System'`
  （`import System.Collections.Generic.List as ClrList` が**何も注入しない**）
- **根本原因**: スキャンの正規表現（`Program.cs:103`）が `import A.B.C` のみを拾い、
  **`as` 別名と `.*` を明示的に除外**（`Program.cs:99` のコメント通り）。`kotlin.collections.List` との
  名前衝突を避けるための別名がそのまま「注入なし」になる。
- **修正方針**: aliased import に対応（右辺の型を注入し、別名にバインド）。さらに、`.NET` 型に見える
  import が注入されなかった場合は**黙殺せず警告**を出す（debuggability）。`<DotKtImport>`（明示注入。
  `KotlinClr.targets` の `@(DotKtImport)` / 旧 `<KotlinClrType>`）で個別救済はできるが、
  普通の `import` で安定して通るのが望ましい。

### (6) 中間戻り値の型が未注入だと連鎖アクセスが切れる
- **症状（Windows 実機）**: `unresolved reference 'Add'`（`panel.Children.Add(...)`。
  `Children` の戻り `UIElementCollection` が未注入だと `Any?` になり `.Add` に届かない）
- **根本原因**: `CrossType` は「同時に注入された型のみ単純名で解決、その他は `Any?`」（`Program.cs:400-403`）。
  ユーザーは `Children` の戻り型まで個別に import しないと連鎖が切れる。
- **修正方針**: import された型の **API サーフェスが参照する型（戻り値・引数・基底・要素型）を推移的に自動注入**
  する（到達可能閉包）。少なくとも 1〜2 ホップは自動で引き込むと WinUI のようなオブジェクトグラフが扱える。

---

## Part 2. IL バックエンド（ilemit）— DSL の書き味を制約した閉包/状態系

Linux 上で純 Kotlin として bring-up する過程で判明（WinUI 非依存。再現コマンドは
`dotktx.ui.winui/tools/verify-core.sh` 系）。回避はできているが、本来は通したい。

- **(7) レシーバ付きラムダ非対応 — ✅ RESOLVED**: `build: Scope.() -> Unit` の暗黙レシーバ `$this$build` が
  `load unknown var` で落ちていた。suspend レシーバラムダ対応（`emitCoroutineBody` が拡張レシーバを先頭パラメータ/フィールド化）と
  併せて解消し、Compose 風の `Column { Text() }` 形が書ける（design-coroutines-clr.md §13p）。
- **(8) クロージャのキャプチャが 1 ラムダ境界まで — ✅ RESOLVED**: 変数を使わない中間ラムダを跨ぐ多段キャプチャが
  `load unknown var X` で落ちていたが、Flow の `capturedVars` 修正（ネストラムダ自身のパラメータを宣言済み集合に加える）で解消（design-coroutines-clr.md §13i）。
- **(9) ネストしたラムダが自前パラメータを持つと不可 — ✅ RESOLVED**: `outer { x -> inner { s -> ... } }` の内側
  `{ s -> }` の `load unknown var s` も同じ `capturedVars` 修正で解消。
- **(10) `object` シングルトン未 lowering**: `IrGetObjectValueImpl has no .NET lowering`。
  共有状態に `object` が使えない（companion object は facadegen が静的注入に使っているので要注意）。
- **(11) クロスファイルのトップレベル可変プロパティ参照が壊れる**: 参照元ファイルの `<File>Kt` を
  誤って見て `field XKt.foo not found`。→ 可変トップレベルは定義ファイルに閉じ、他ファイルからは関数経由に。
- **(12) BCL ジェネリックを自前型で実体化不可**: `mutableSetOf<UserType>()`→`new HashSet<UserType>` が
  `TypeBuilderInstantiation.GetConstructor` で落ちる。
- **(13) ジェネリック・ファクトリ関数が不正 IL**: `fun <T> state(i:T):State<T> = State(i)` は型引数が
  欠落して ilverify `StackUnexpected`。呼び出し箇所での直接 `State<Int>(0)` は OK・ilverify クリーン。
- **(14) マルチファイル＋別パッケージの TypeBuilder 型で Save 落ち**:
  `The invoked member is not supported before the type is created`（`CreateType` 順序の疑い）。
  注入された外部型を実 TypeBuilder としてモックした検証足場で発生。本番の外部型では起きない見込みだが、
  ilemit の型生成順を要確認。

---

## 優先度（WinUI を Kotlin だけで駆動するために）

1. **(2) protected/virtual の注入と override** — 起動部（`Application.OnLaunched`）の前提。
2. **(1) 基底クラス／インターフェース階層** — コントロールの受け渡し全般の前提。
3. **(4) デリゲート→関数型マップ** — `Application.Start` / `Thread` / イベントの前提。
4. **(6) 参照型の推移的自動注入** + **(3) 代表的ジェネリックの構築型注入** — 連鎖アクセスの前提。
5. **(5) import パーサ安定化（alias 対応 + 警告）** — 開発体験。

(1)〜(4) が入れば、`dotktx.ui.winui` の `App.kt`（`Application` サブクラス + `OnLaunched` override +
STA スレッド + `Application.Start`）が**ワークアラウンドなしの純 Kotlin で書ける**ようになる。
