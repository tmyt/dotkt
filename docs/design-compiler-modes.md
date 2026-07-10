# Compiler Modes — 各ステージの動作モード・属性出力・Lowering

> **状態 (2026-06-30 確定 / ユーザ確定スペック)**: コンパイラ各ステージの「モード」と、出力成果物ごとの属性付与・Lowering 規則の**正本**。[[artifact-emission-policy]] の 4-artifact マトリクスを精緻化し、一部を訂正する（§6）。層責務は [[compiler-layer-responsibilities]] / [design-fir-bir-cir-il.md](design-fir-bir-cir-il.md)、三参照モデルは [ship-tasks.md](ship-tasks.md) §0。

## 0. 原則

- toolchain = **facadegen / kotc / bir2cir / ilemit**。
- **出力モードを持つのは bir2cir のみ**（`ref.dll` / `rt.dll` / `app` の 3 モード）。他 3 ステージはモード不変。
- frontend klib（`kotlin-stdlib-clr-frontend.klib`）は kotc の metadata pipeline（`build-stdlib-klib.sh`）で別途生成する成果物（[[artifact-emission-policy]] の artifact A）であり、bir2cir のモードではない（§2）。旧 JVM frontend jar は #67 で退役。

---

## 1. facadegen — 常に同じ

- 渡されたアセンブリ情報から **kotlin metadata を生成**する。
- **CLR primitive → kotlin.primitive** に置換する（例: `System.Int32` → `kotlin.Int`）。
- **Roundtrip Attribute を元に Kotlin メタデータを復元**する（TopLevelFunction / inline / infix / operator / suspend 等）。
- **Attribute を元に caller argument / return type の Kotlin 型を復元**する。
- .NET の **ref/out 引数を `ClrRef<T>` 引数**として facade 生成する（Kotlin は `fn(byref(x))` で呼ぶ。§3.5-1。これは正規の byref interop 経路で、`@ClrRefArguments` エスケープハッチとは別物）。
- kotc が読み込める **facade を生成**する。
- **`@ClrIntrinsic` のバインドはしない**（シンボル面＝「その関数/型が存在する」ことの生成のみ。バインド＝substitute は bir2cir の責務）。

> facadegen の入力は **BCL DLL（→ .NET 空間の meta）** と **kcc toolchain が生成した DotKt アセンブリ（→ Kotlin 意味論の round-trip 復元）**。`stdlib.ref.dll`/`stdlib.rt.dll` は facadegen の入力ではない（だから ref/rt では Roundtrip 属性を生成しない、§3.2）。

---

## 2. kotc — 常に同じ（stdlib ビルドモードは no-op）

- **stdlib ビルドモード**はあるが、kotlin compiler jar 組み込みのものなので特別なことはしない。
- **FIR 以降の動作は常に同じ**。
- **Kotlin 意味論を素直に BIR へ出力する**。過去 FIR→BIR で一部のメソッド呼び出しを LINQ 等へ Lowering していたが、これは**古い実装で誤り**。
- `@ClrIntrinsic` のような **CLR 固有 Attribute は、Kotlin から見れば単なる Attribute**。解決せず**直接 BIR へ出力**するのが正しい。
- このレイヤーは **出力先（CLR）の事情を知らない**ものとして設計・実装する。
- frontend metadata には **`kotlin-stdlib-clr-frontend.klib`（CLR コンパイラ向け）** を使う。従来の JVM 向け `kotlin-stdlib.jar` は `java.util.*` など **java 依存空間を巻き込む**ため使わない（下流への java 空間流出を防ぐ）。

---

## 3. bir2cir — 3 出力モード + 固定フェーズ

### 3.1 フェーズ（全モード共通）

1. **BIR を読み込む**
2. **Primitive Lowering**
3. **Inline Lowering** — `inline` 関数の呼び出しを展開する。**展開元の BIR は ref.dll（参照アセンブリ）の `KotlinInlineAttribute` から読む**。∴ ref.dll は inline 関数の BIR を `KotlinInlineAttribute` に保持する必要がある（§3.2 matrix）。
4. **Type Substitution** — `@ClrTypeAlias`（型読み替え）と `@ClrIntrinsic`（メンバ名読み替え）を消費して、Kotlin 型／呼び出しを BCL へ置換する（§3.4）。
5. **Suspend Lowering**
6. **CIR を生成**

各モードは、このフェーズ群の **どれを有効化し、どの属性を出力するか** で定義される。

### 3.2 出力モード matrix

| 項目 | **a. stdlib.ref.dll** | **b. stdlib.rt.dll** | **c. user library / app** |
|---|---|---|---|
| 関数 body | 強制的に**全て `NotImplementedException`** に落とす | **そのまま**生成 | **そのまま**生成 |
| 属性出力 | **全部**出力 | **Kotlin 定義のもの**を出力 | **全部**出力 |
| Type Substitution | **無し** | 有り | 有り |
| 読み替え (read-back) 属性 | 無し（substitution が無いため） | 無し（facadegen が読まないため） | **有り**（引数・戻り値が Kotlin で何だったかを復元する情報） |
| Roundtrip 属性 | 無し（facadegen が読まないため） | 無し（facadegen が読まないため） | **有り** |
| `KotlinInlineAttribute` への BIR 埋込 | **有り**（後述: bir2cir が ref.dll から読んで inline 展開する） | — | **有り**（BIR を復元可能な状態で出力） |
| Primitive Lowering | **無効** → 全 kotlin primitive は **boxed 型のまま** IL へ | **有効** → 全 kotlin primitive を **CLR primitive** へ置換 | **有効** → CLR primitive へ置換 |
| `@ClrTypeAlias`（型の読み替え）で差し替えられるクラス | 出力する（subst 無し） | **出力から Omit**。結果的に **Kotlin Primitive の Boxed 型** も、正しく TypeAlias されていれば Omit される | **Omit**（rt.dll に合わせる。app に `@ClrTypeAlias` は通常存在しないため実質 moot） |

> モードの直感:
> - **ref.dll** = 「純 `kotlin.*` の参照面。全 attribute を持つが body は空（throw スタブ）、primitive は boxed のまま」。`@ClrIntrinsic` の**出所**（[ship-tasks.md](ship-tasks.md) §0 の不変条件）。
> - **rt.dll** = 「実行時実装。body あり、primitive は CLR 化、BCL に TypeAlias されるクラスは Omit」。
> - **app** = 「利用者コード。subst 済みかつ、下流の facadegen が Kotlin として再消費できるよう read-back/Roundtrip 属性と inline BIR を持つ」。

### 3.3 属性の分類

- **Kotlin 定義属性** — `@ClrIntrinsic` / `@ClrTypeAlias` / ユーザ注釈など、**Kotlin 側で宣言された**属性。ref / rt / app いずれも（rt は「Kotlin 定義のもの」に限定して）出力。
- **Roundtrip Attribute**（`KotlinInlineAttribute` / `KotlinFunctionAttribute` 等）— kcc toolchain が生成したアセンブリを **facadegen が読み込むとき**に、元の Kotlin 型・修飾子を復元するための情報。**facadegen が読む出力＝app モードのみ**生成。
- **読み替え (read-back) 属性** — Substitution で CLR 型に化けた引数・戻り値が「Kotlin で何だったか」を復元する情報。**Substitution が起き、かつ facadegen が読む＝app モードのみ**生成。

### 3.4 CLR Intrinsic 系 Attribute（bir2cir が Type Substitution で消費）

| Attribute | 付与対象 | 役割 | 旧 |
|---|---|---|---|
| `ClrIntrinsic` | **メンバ** | **メンバ名の読み替え**（call-substitute。例: `Double.isNaN` → `System.Double.IsNaN`） | 旧 `clr.Clr`（メンバ用途） |
| `ClrTypeAlias` | **クラス／型** | **型の読み替え**（type-substitute。例: `Comparable` → `System.IComparable`、boxed `kotlin.Int` → `System.Int32`）。旧 `clr.Clr`/`ClrIntrinsic` に統合されていた型用途を切り出したもの（**名前の変更のみ**） | 旧 `clr.Clr`/`ClrIntrinsic`（型用途） |
| `ClrRefArguments(mask: Int)` | **メンバ**（`ClrIntrinsic` と併用） | `ClrIntrinsic` 束縛 stdlib 関数で `ClrRef<T>` 修飾を**付けられない**ものに byref を表明する**エスケープハッチ**。`mask` は bitmask（**bit 位置 = 引数位置**）。通常 interop の byref は `kotlin.clr.byref`/`ClrRef<T>`（§3.5-1）で**別物** | 新規（byref エスケープハッチ） |

> いずれも「Kotlin 定義属性」（§3.3）。kotc は BIR へ素通しし、**bir2cir の Type Substitution / byref 処理で消費**する（CIR には出ない）。`ClrIntrinsic`＝**呼び出し読み替え**、`ClrTypeAlias`＝**型読み替え**、の役割分離は bir2cir 内の **call-substitute / type-substitute の分離**と一致する。

### 3.5 byref（CLR の ref/out 引数）— **2 経路を混同しない**

1. **本来の byref ＝ `kotlin.clr.byref` + `ClrRef<T>`（実装済み・通常 interop の手段）**
   - kotc が `inline fun <T> byref(in: T): ClrRef<T>` を **固定で emit** する（`kotlin.clr.byref`）。
   - .NET の ref/out 引数を持つメソッドを facadegen に通すと、`fun fn(refIn: ClrRef<Int>): Unit` のように **`ClrRef<T>` 引数**として facade 生成される（§1）。
   - Kotlin 側は `fn(byref(intVar))` のように呼ぶ。**任意の .NET メソッド**に使える正規の byref 手段。

2. **エスケープハッチ ＝ `@ClrRefArguments(mask)`（§3.4）**
   - `@ClrIntrinsic` で BCL メソッドへ束縛する **kotlin stdlib 関数**のうち、シグネチャに `ClrRef<T>` 修飾を**付けられない**もの（Kotlin 本来のシグネチャに一致させる必要があるため）に対し、byref を表明するための**エスケープハッチ**。
   - bir2cir は @ClrIntrinsic 置換時に `mask` を読み、該当引数位置を **CLR managed pointer（ref）** で渡す（例: `Interlocked` 系 atomics）。
   - **(1) の `byref()`/`ClrRef<T>` とは別物**。(1) は interop の通常手段、(2) は @ClrIntrinsic stdlib 束縛専用の限定的エスケープハッチ。

---

## 4. ilemit — 常に同じ

- **suspend 状態機械の生成**を bir2cir でやるか ilemit でやるかは議論の余地があるが、**ilemit でやる**ことに決定。
- suspend 状態機械を **CLR の async/await** に変換する。

---

## 5. [[artifact-emission-policy]] との関係（精緻化・訂正）

本書はモード単位で精緻化したもの。旧 policy（jar/ref/rt/app × attribute/inline/body/Type）との対応と差分:

- **対応**: jar=artifact A（kotc/K2 の stdlib ビルド、本書 §2）/ ref.dll=B / rt.dll=C / app=app（本書 §3.2）。
- **訂正**: 旧 policy は **rt-dll を「attribute: 全 strip（none）」** としていたが、本スペックでは **rt.dll は「Kotlin 定義属性を出力する」**（Roundtrip / read-back のみ非生成）。`DOTKT_STRIP_METADATA` 系の「全 strip」挙動は本書に合わせて見直す。
- **整合**: primitive は ref=Lowering 無効（boxed のまま）、rt/app=有効（CLR primitive）— [[primitive-dual-representation]] と一致。
- **訂正（inline BIR）**: `KotlinInlineAttribute` に BIR を載せるのは **ref と app の両方**（bir2cir が inline 展開時に ref.dll から読むため、§3.1/§3.2）。rt は inline を通常メソッド body として emit し BIR は持たない。旧 policy は「app のみ BIR」としていたが、**ref も BIR を持つ**のが正。
