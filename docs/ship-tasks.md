# kotlin/clr — 出荷タスクリスト（stdlib + パイプライン）

最終更新: 2026-06-30。ユーザ指示で整理した **現在の出荷スコープ**。広域 1.0 チェックリストは `docs/remaining-tasks.md`、
本書は「stdlib を全 Projection 対応し、kotc/bir2cir/ilemit パイプラインを出荷品質にする」現タスクの単一の真実とする。

## 0. 確定アーキテクチャ（層責務 — これに反する実装はバグ）

参照の三分割（ユーザ確定 2026-06-30、[[compiler-layer-responsibilities]] / [[artifact-emission-policy]]）:

| ステージ | 参照する成果物 | 責務 |
|---|---|---|
| **facadegen** | CLR DLL を読む | CLR DLL → kotlin metadata 生成。Roundtrip Attribute で TopLevelFunction/inline を復元。`System.Int32→kotlin.Int` の型読み替え。**@ClrIntrinsic のバインドはしない**（シンボル面のみ生成）。 |
| **kotc** | **stdlib.jar**（stdlib 空間）+ facadegen meta（.NET 空間） | ユーザソース → FIR → **BIR**。シンボル解決のみ。**CLR を知らない**。 |
| **bir2cir** | **stdlib.ref.dll**（= DotKt.Private.Stdlib.dll、全 attribute 保持） | BIR → CIR。inline lowering / **type substitute** / suspend lowering。**@ClrIntrinsic はここで「何に substitute するか」のラベルとして消費し、CIR には出力しない**（plain な BCL 呼び出しを emit）。 |
| **ilemit** | **stdlib.rt.dll**（= DotKt.Stdlib.dll、実装） | CIR → IL。**Kotlin を知らない**。 |

> 重要な不変条件: **@ClrIntrinsic は ref.dll が出所**で、**bir2cir が消費**する。jar（artifact A）は inline/expect-actual で @ClrIntrinsic を落とすので出所にできない。ilemit に @ClrIntrinsic（や intrinsic ラベル）を渡すのは**明確な誤り**。

---

## 1. stdlib.dll の全 Projection 対応
- [ ] `runtime/stdlib/clr/` の **未束縛** `actual` をゼロにする = ほぼ全 `actual` に `@kotlin.clr.ClrIntrinsic` を付与（①直接対応が既定。audit 台帳 490 スタブ → 残 ~363 が未束縛。直接対応クラス無し→②全 Kotlin 実装、単一 member が 1:1 無し→③クラス `@ClrIntrinsic`＋"Rule 3" 実 body）。
  - ⚠️ **判別子は `@kotlin.clr.ClrIntrinsic` の有無。`TODO("clr binding should be implemented")` body は束縛しても消えず、そのまま runtime (`DotKt.Stdlib.dll`) に "未呼び出しの throw スタブ" として乗る**（呼び出し側が app-emit で BCL に substitute されるので body が実行されないだけ）。逆に未束縛のまま出荷すると実際に `NotImplementedError` を投げる。∴ **進捗を TODO 数で測らない** — 未束縛 actual 数 / ref・runtime ビルドの load count（例 724/0）/ `docs/clr-stdlib-intrinsic-audit.md` で測る。`grep TODO|wc` は誤指標。
- [ ] 各 `@ClrIntrinsic` が bir2cir の type-substitute で正しく BCL 呼び出しへ落ちる（型メンバ・top-level・expect/actual・inline すべて）。
- [ ] BCL に転送できない箇所は Kotlin 実装（rule-3 等）で body を持つ。
- 状態: バインド作業は大半完了。**substitute の出所が問題**（下記 #3 と一体）。`docs/clr-stdlib-intrinsic-audit.md` が検証台帳。

## 2. stdlib.jar の生成
- [ ] CLR stdlib ソースから frontend jar（`kotlin-clr-stdlib.jar` 相当）を生成（[[frontend-stdlib-jar]]）。
- [ ] jar は kotc の `-classpath` として JVM `kotlin-stdlib.jar` を置換し、`java.*` typealias 漏れが無い。
- 状態: K2JVMCompiler 経路は実証済み。出荷用ビルドの固定化が残。

## 3. パイプライン三参照でのコード生成（**最重要・現在の主戦場**）
- [ ] kotc が (2) の **stdlib.jar** を参照してシンボル解決できる。
- [ ] **bir2cir が stdlib.ref.dll を参照**し、`@ClrIntrinsic` ラベルを読んで **CIR に BCL 呼び出しを substitute** する。
- [ ] ilemit が **stdlib.rt.dll** を参照して IL を生成できる。
- **既知の欠陥（このセッションで特定）**:
  - @ClrIntrinsic 置換機構は存在する（現状 `BirEmitter.kt:3183` の `clrName(callee)→clrStatic`）が、**kotc backend に居る**（本来 bir2cir）。
  - `isNaN` 等 **expect/actual top-level fun が失敗**: 置換は `clrName(callee)` がラベルを返した時だけ発火するが、app は jar の **expect**（無注釈）に解決し、ラベルは **actual（ref.dll）** にある → ラベルが置換点に届かない。
  - **正しい修正**: 置換を bir2cir 側で行い、**ref.dll の @ClrIntrinsic を出所**にする。`ReferenceMetadataIndex` は ref.dll を読めており、`isNaN`→`kotlin.NumbersKt.isNaN` まで解決済み（実証済み）。あとは「解決した参照メソッドが @ClrIntrinsic を持つなら CIR を BCL 呼び出しへ置換」を bir2cir に実装（ilemit へラベルを渡さない）。

## 4. facadegen の Kotlin 意味論復元（round-trip）
- [ ] (3) で生成したライブラリを参照した facadegen が、TopLevelFunction / inline / infix / operator / suspct 等の Kotlin 意味論を Roundtrip Attribute から復元できる（[[kotlin-modifier-roundtrip]]）。
- [ ] `System.Int32→kotlin.Int` 等の型読み替えが正しい。

## 5. アプリのビルド・実行
- [ ] (3) で生成したライブラリを参照したアプリが MSBuild / `.ktproj` でビルドでき、`dotnet run` で期待出力（[[clr-annotation-namespace-proposal]] の app build 経路）。
- [ ] 代表サンプル（コレクション/文字列/数値/StringBuilder/range 等）が緑。

## 6. 既知リファクタポイントの解消
- [ ] **DLL 名**を最終形に: `DotKt.Private.Stdlib.dll`（ref）/ `DotKt.Stdlib.dll`（rt）。※build 出力は既にこの名前。経路全体で一貫しているか確認。
- [ ] **`clr.Clr` → `kotlin.clr.ClrIntrinsic` リネーム完了**: stdlib 移行済みだが compiler が両方マッチ・facadegen が legacy 保持中（[[clr-annotation-namespace-proposal]]）。legacy 撤去で完了。
- [ ] **クラスに付く `@ClrIntrinsic` を `@ClrTypeAlias` にリネーム**: クラス注釈は「**型エイリアス**（型同一性／インスタンス生成の substitute）」で、メンバ注釈の「**呼び出し substitute**」とは役割が異なる。役割で注釈を分離する（メンバ＝`@ClrIntrinsic` のまま、クラス＝`@ClrTypeAlias`）。bir2cir の type-substitute（型解決）と call-substitute（呼び出し解決）の区別とも一致（[[comparable-iclr-typealias]]）。
- [ ] **ilemit から Kotlin の事情を排除**: `BirEmitter` / ilemit に残る Kotlin 特化（netType→System.*、math-map、primitive→System.X、@ClrIntrinsic lowering 等）を **bir2cir へ移設**（[[compiler-layer-responsibilities]] の "Current violation"）。
- [x] **`byref` / `ClrRef` を root パッケージから `kotlin.clr.*` へ移動** — **既達**（kotc 合成、`ClrTypeInjection.kt:311,315,331`、gap-analysis §4 確認）。※ `stackBuffer`/`Span` は `FqName.ROOT` 残存（別件、`ClrTypeInjection.kt:319,322`）。
- [ ] **Kotlin 実装（BCL 非転送）箇所の性能確認**: rule-3 helper 等が非効率になっていないか。

## 7. パイプライン（kotc/bir2cir/ilemit）の出荷品質化
- [ ] 三層の責務分離が完了（kotc=CLR 非依存 / bir2cir=Kotlin↔CLR / ilemit=Kotlin 非依存）。
- [ ] `--native-cir` が既定・`--compat-bir` 撤去（Milestone 0、ブロッカーの emit crash は解消済み）。
- [ ] 想定外入力は明示エラー（silent miscompile 禁止）、診断はソース位置付き。

## 8. リポジトリの出荷品質化
- [ ] 古い scripts の整理（引退済みバックエンド由来の不要 script を削除/統合）。
- [ ] verify 群が CI で緑（verify-il / differential / ktproj / native-cir-ilemit / roundtrip）。

---

## 今すぐの着手点
**#3 の bir2cir 実装**: `ReferenceMetadataIndex` で解決した候補が ref.dll 上で `@ClrIntrinsic` を持つ場合、`ExecutableCirDraft` で CIR を BCL 呼び出しへ substitute する（fqn を最後の `.` で owner/method に分割、`BirEmitter.kt:3183` と同型の変換、ただし bir2cir 側・CIR は plain BCL 呼び出し）。これが #1/#5 の expect/actual 系を一箇所で解く。
