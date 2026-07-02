# kotlin/clr — 出荷タスクリスト（stdlib + パイプライン）

最終更新: 2026-07-03。**§0（確定アーキテクチャ）は現在も binding**（CLAUDE.md が直接参照）。
一方 §1 以降の 8 ゴールは **完了または `docs/master-task-inventory.md` に吸収済み**（下記クローズアウト参照）—
現在のタスク台帳は master-task-inventory、広域 1.0 チェックリストは `docs/remaining-tasks.md`。

## 0. 確定アーキテクチャ（層責務 — これに反する実装はバグ）

参照の三分割（ユーザ確定 2026-06-30、[[compiler-layer-responsibilities]] / [[artifact-emission-policy]]）:

| ステージ | 参照する成果物 | 責務 |
|---|---|---|
| **facadegen** | CLR DLL を読む | CLR DLL → kotlin metadata 生成。Roundtrip Attribute で TopLevelFunction/inline を復元。`System.Int32→kotlin.Int` の型読み替え。**@ClrIntrinsic のバインドはしない**（シンボル面のみ生成）。 |
| **kotc** | **stdlib.jar**（stdlib 空間）+ facadegen meta（.NET 空間） | ユーザソース → FIR → **BIR**。シンボル解決のみ。**CLR を知らない**。 |
| **bir2cir** | **stdlib.ref.dll**（= DotKt.Private.Stdlib.dll、全 attribute 保持） | BIR → CIR。inline lowering / **type substitute** / suspend lowering。**@ClrIntrinsic はここで「何に substitute するか」のラベルとして消費し、CIR には出力しない**（plain な BCL 呼び出しを emit）。 |
| **ilemit** | **stdlib.rt.dll**（= DotKt.Stdlib.dll、実装） | CIR → IL。**Kotlin を知らない**。 |

> 重要な不変条件①: **@ClrIntrinsic は ref.dll が出所**で、**bir2cir が消費**する。jar（artifact A）は inline/expect-actual で @ClrIntrinsic を落とすので出所にできない。ilemit に @ClrIntrinsic（や intrinsic ラベル）を渡すのは**明確な誤り**。
>
> 重要な不変条件②: **`kotlin.*`（stdlib 全体）は jar から供給する。facadegen 経由で注入しては絶対にならない。** kotc は stdlib 空間を frontend **jar**（`-classpath`）から解決し、jar は Companion object を含む Kotlin 意味論を完全に保持する。facadegen は **.NET 空間専用**（`System.*` + 参照 .NET アセンブリ。System.* に限らない）。
> - 理由: facadegen は inline/operator/infix を Roundtrip Attribute で復元できるが、**Companion object の暗黙呼び出し（`Type.method`、method が Companion 上）を完全復元できない**（`Type.Companion.method` が要る）。stdlib は Companion object 前提で実装されているため、facadegen 製の `kotlin.*` シンボルは意味論が劣化し、かつ jar のものと**二重化して衝突**する（本セッション実例: facadegen の非reified `arrayOf` が jar の reified `arrayOf` と衝突 → `overload resolution ambiguity`）。
> - 直し方: 「アプリビルドで stdlib シンボルが無い/曖昧」の修正は**常に jar**。facadegen 側に `kotlin.*` ガードを足すのは**対症療法で筋が悪い** — 根本は **stdlib.dll を facadegen に `--scan-asm` で渡していること自体**。
> - 状態: 本番経路 `packaging/DotKt.Toolchain/build/DotKt.Toolchain.targets` + `scripts/dotkt.sh` から除去済（commit `522bdc8`）。`scripts/verify-il.sh` / `scripts/verify-differential.sh` の `--scan-asm` も**除去済み**（2026-07-02、master-task-inventory META 参照）。`[[stdlib-jar-only-not-facadegen]]`

---

## 1–8. ゴールのクローズアウト（2026-07-03）

8 ゴールは完了、または現行台帳 `docs/master-task-inventory.md` に吸収済み。要点のみ:

- **#1 stdlib 全 Projection** — ✅ 実質完了（監査で「~363 未束縛」は約 3.8 倍の過大計上と判明。実態 1481 actuals /
  93.5% bound-or-implemented。残＝coroutine intrinsics 等、inventory 【2】）。
- **#2 stdlib.jar 生成** — ✅ `scripts/build-stdlib-jar.sh` として本番化（`kotlin-stdlib-clr-frontend.jar`）。
- **#3 三参照コード生成** — ✅ **bir2cir `MemberCallSubstitution` が ref.dll の `@ClrIntrinsic`/`@ClrTypeAlias`/
  `@ClrProperty` を消費して substitute**（本書が指摘した「kotc 側に居る」欠陥は解消 — kotc の `clrName`/`annClr`
  読み取りは削除済み）。
- **#4 facadegen round-trip 復元** — ✅（bounds/variance/sealed/fun-interface まで拡張。`dotkt-semantics.md` §10）。
- **#5 アプリのビルド・実行** — ✅ ゲート green（verify-il run-FAIL 0 / PASS 132、verify-ktproj 9/9、2026-07-03
  クローズアウト）。
- **#6 リファクタポイント** — ✅ dll 名確定 / `clr.Clr` legacy 撤去 / `@ClrTypeAlias` 役割分離、いずれも完了。
  残る ilemit/kotc の細目は inventory 【1】②③。
- **#7 パイプライン出荷品質** — 層分離 ✅（kotc の CLR 直下ろしは bundle 1 で退役）。`--native-cir`/`--compat-bir`
  は「既定化」ではなく **両方撤去**（2026-06-30、単一経路化）。診断品質は inventory 【7】へ。
- **#8 リポジトリ出荷品質** — 旧 script 削除 ✅（`build-dotkt-stdlib.sh`/`build-stdlib.sh`/native-cir 系 verify）。
  CI/配布の残は inventory 【7】へ。
