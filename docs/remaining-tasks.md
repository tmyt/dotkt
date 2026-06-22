# kotlin/clr — 残タスク計画書 ＝ Kotlin.NET 1.0 出荷チェックリスト

最終更新: 2026-06-16。これは「純 .NET Binding として縦は貫通済み」状態からの**残タスク網羅リスト**。
**本書のチェックボックスを全て埋めた時点を Kotlin.NET 1.0 のリリース可能ライン（definition of done）とする。**
完了済みの大枠（M0 / M-D1 IL / M-D2 coroutine CPS / M-S S1–S5 / interop I2–I4 / framework-direct 継承 W0–W1）は
`docs/research-roadmap.md` を参照。本書は**まだ無いもの**を漏れなく列挙し、チェックボックスで進捗を追う。

## 1.0 の出荷ゲート（全カテゴリ完了に加え、横断で必須）
- [ ] **任意の実用 Kotlin プログラム**が kotlin/clr でコンパイル・実行できる（A 言語網羅 ＋ B stdlib）。
- [ ] **双方向 interop**: Kotlin→.NET だけでなく **.NET→Kotlin**（生成アセンブリを C# 等から自然に消費）も成立（C-0）。
- [ ] **唯一の出荷バックエンドが直接 IL**（C# コード生成を廃止＝下記 Track E）。
- [ ] **JVM 差分ハーネス**が corpus 全体で緑（kotlin/jvm 出力 == kotlin/clr 出力）＋生成 IL が全件 `ilverify` clean。
- [ ] **配布可能**（`dotnet new ktproj` / NuGet・SDK 化 / バージョン付きリリース）＋**ライセンス/帰属クリア**（F）。
- [ ] 想定外入力は明示エラー、診断はソース位置付き。純バインディング原則を維持。

> **スコープ確定（ユーザ判断 2026-06-16）**: 縮小はしない。**本書の全項目を 1.0 必須**とする（構造化並行性・増分コンパイル・性能・VS-LSP も含め全部）。

## 運用原則（各タスク共通の Done 条件）
- end-to-end で動く（frontend だけ通っても不可。**生成コードが `dotnet run` で期待出力**＝移行期は C#→csc、1.0 ゴールは IL 単独）。
- `scripts/verify-all.sh` に最低1サンプルを追加し緑（IL 対応分は `verify-il.sh` も）。
- 想定外入力は**明示エラー**（silent miscompile 禁止）。— [[no-half-baked-public-state]]
- コアは**純バインディング**を保つ（UI 等のライブラリを同梱しない）。— [[kotlin-net-is-pure-binding]]
- サイズ目安: S=数時間 / M=1–2日 / L=数日 / XL=1週間超。

## 実機確認済みステータス（2026-06-16 の probe）
| 機能 | 状態 |
|---|---|
| ユーザ定義ジェネリクス `class Box<T>` / `fun <T>` | ✅ 動く |
| 拡張関数 `fun P.dbl()` | ❌ static に `this` を出す |
| 網羅 `when(x){ is T -> }`（式） | ❌ bool 期待箇所に型 |
| 配列 `arrayOf` / `IntArray` | ❌ 未マップ |
| stdlib コレクション `map`/`filter`/`forEach` | ❌ 未マップ |
| デフォルト引数 | ❌ 欠落 |
| スコープ関数 `apply`/`with`/`let`/`run`/`also` | ❌ lambda-with-receiver 未対応 |

---

# A. Kotlin 言語 breadth（codegen）

## A-0 言語機能カバレッジ表（網羅トラッキング・1.0 言語ゲートの正本）
> **BIR が処理しない FIR/IR 構文の正本チェックリストは [`docs/bir-coverage.md`](bir-coverage.md)**（IR ノード型レベルの gap 一覧）。
> 1.0 では **Kotlin 言語構文を漏れなく**追跡する。本表が正本（A-1〜A-3 は補足）。判定根拠＝(1) BirEmitter/ilemit の IR ノードハンドラ有無、(2) パスしているサンプル（verify-il / verify-differential＝IL 経路、verify-all＝C# 経路）。**未対応ノードは `unsupportedStmt`/`unsupportedExpr` でクリーンに停止**（誤コンパイルしない）。確信度: ✅ サンプル実証 / ⚠️ 未検証（サンプル無し・要サンプル追加で確定） / ❌ 未対応（IR ノード未ハンドル or 既知の穴）。更新時はこの表を必ず同期。

### ✅ 対応済（IL 経路でサンプル実証）
- 制御フロー: `if`/`when`(subject・`is`・範囲)/`while`/`do-while`/`for-in`(range/array/Iterable)/`break`/`continue`/ループ`label@`（`il-for`,`il-enum`,`m-a1`,`m-a2`）
- 関数: top-level/メンバ/ローカル関数/拡張関数/`operator`/定数デフォルト引数/`vararg`/可変長/高階・lambda・closure（`il-ext`,`il-lambda`,`il-closure`,`il-op`,`m-a4`）
- スコープ関数 `let/run/with/apply/also`（`il-scope`）
- クラス: `class`/`data class`/`enum`(基本・rich)/`object`/`companion`/`interface`/`inner`/nested/継承・`override`・`super`/2次コンストラクタ/`init`ブロック/可視性修飾（`il-ctor`,`il-inner`,`il-nested`,`il-enumrich`,`il-vis`,`m-s2`,`m-a5`）
- object 式（無名オブジェクト）（`il-objexpr`）
- プロパティ: `val`/`var`/カスタム get/set/`by lazy`/**メンバ/トップレベルの `by` 委譲**（任意 getValue/setValue・`ReadWriteProperty`・`Map`委譲・`Delegates.observable/vetoable/notNull`）（`il-lazy`,`il-deleg`,`il-rwp`,`il-bymap`,`il-deleg2`）
- 分解宣言（`val (a,b)=`）（`il-pair`,`m-a5`）
- null 安全 `?.`/`?:`/`!!`/スマートキャスト/`is`/`as`/`as?`（`il-null`,`il-smartcast`）
- 文字列テンプレート/範囲(`..`/`until`/`downTo`/`step`)/ビット演算/`in`/`!in`（`il-str`,`il-for`,`il-ops`）
- 例外 `try`/`catch`/`throw`/throw 式（`il-exc`,`il-throwexpr`）
- ジェネリクス: クラス/関数/境界`<T:…>`/宣言箇所変性`out`/`in`/`reified`（`il-generic`〜`generic6`,`il-reified`）
- コルーチン `suspend`（直線/ループ/分岐 await・直接 suspend 呼出・**部分式内サスペンドの spilling** `f(a.await())`/`a.await()+b.await()`・**条件式内サスペンド** `while(f().await())`/`if(g().await())`・**try/catch-around-await** `try{ a.await() }catch(e){ … }`・**try/finally-around-await**（2026-06-23、`il-kfinally`）＝言語レベルは網羅、残＝catch 内 suspend・catch+finally 併用・try 内 return のみクリーンエラー）（`il-coro`/`il-kfinally`）
- `T::class`（型リテラル・`IrClassReference`）（`il-reified`）

### ⚠️ 未検証（サンプル無し → 1.0 までにサンプル追加で ✅/❌ 確定）
- [x] **無名関数** `fun(x): T { … }` — ✅ 2026-06-20 動作（`il-langfeat`）
- [x] **`infix` 関数** — ✅ 2026-06-20 動作（`il-langfeat`、`2 pow 10`）
- [x] **`tailrec`** — ✅ 2026-06-20 通常再帰として動作（`il-langfeat`、TCO 最適化は無し＝深い再帰で stack overflow は残）
- [ ] **`lateinit` プロパティ**（`notNull` 委譲に近いが proper lateinit は未検証）
- [x] **`try`-`finally`** — ✅ 2026-06-20 **バグ修正**（`il-langfeat`）: try 文の fall-through 時に ilemit が `result` local を無条件 `ret` して後続文を捨てていた→`return` 含有時のみ専用 ret ラベルへ。`StmtsHaveReturn`/`StmtsAlwaysReturn` で fall-through/全 return を判定。
- [x] **分解宣言のラムダ引数 / for ループ** — ✅ ラムダ引数 `{ (a,b) -> }`（`il-langfeat`、componentN）＋ **`for ((k,v) in map)`**（2026-06-20、`il-mapdes`）: `birForLoop` が `isMapType` を Dictionary 列挙（`forEachInline`、要素＝`KeyValuePair<K,V>`）へ、`Map.Entry<K,V>`→`KeyValuePair`（`birType`）、`entry.component1/2()`（拡張関数）→`.Key`/`.Value`。付随の一般修正: ilemit `EmitClrPropGet` が**値型レシーバを `EmitAddr`**（KeyValuePair など struct プロパティ get は managed pointer 必須）。
- [x] **ユーザ定義アノテーションの保持** ✅（`il-annot`：`@Tag` 宣言＋クラス/関数へ適用→ilemit が `SetCustomAttribute`/`BuildCab` で .NET カスタム属性として emit、reflection 可視）
- [x] **`abstract class`** — ✅ 2026-06-20 **実装**（`il-langfeat`）: `abstract`/`sealed` クラス→CLR abstract type、`abstract fun`（body 無し）→ `Virtual|Abstract` メソッドとして emit（BirEmitter の body!=null フィルタに modality==ABSTRACT を追加）。base 型経由の仮想ディスパッチ（`shape.area()`）が解決。

### ❌ 未対応（IR ノード未ハンドル＝`unsupportedStmt`/`Expr` 直行・1.0 言語タスク）
- [x] **コール可能参照** `::foo` / `obj::method` / `::Ctor`（**`IrFunctionReference`**）— `Func`/`Action` デリゲートへ。**✅ 2026-06-20**（`il-funref`/`il-ctorref`）: `::foo`→`delegateNew`（static file-class メソッド）。`obj::method`→`boundDelegateNew`（`ldftn`/open は `dup`+`ldvirtftn`）。**`::Ctor`→合成 static factory `__ctorref(args)=new T(args)` を delegate 化**。付随: `KFunctionN` 型→`func:`、`(c::m)(x)` の delegate-invoke。**重要な一般修正**: `Func<…,UserType>`（ユーザ TypeBuilder を generic 引数に持つ delegate）の `GetConstructor`/`GetMethod`/`ReturnType` が Reflection.Emit 制限で失敗していた（[[il-primary-backend-pivot]] tail）→ `DelegateCtor`/`InvokeOf`（`TypeBuilder.GetConstructor`/`GetMethod` でブリッジ）＋ delegate-invoke の戻り型を BIR funcType から（焼く前 builder を反射しない）。**これで ::Ctor・lambda がユーザ型を返すケースが解禁**（`makeWith { Point(n,n) }`）。**非束縛 `Class::method` も ✅**（2026-06-20、`il-funref`、合成 `__mref(__self,args)=__self.method(args)`＝受け手が第1引数）。残（クリーン `unsupportedExpr`）: .NET メソッド参照（interop）。
- [x] **ローカル `by` 委譲** `fun f(){ val x:Int by D() }`（**`IrLocalDelegatedProperty`**）— ✅ 2026-06-20 delegate を local var 化（`stmt(node.delegate)`）＋getter/setter シンボルを `localDelegates` に登録し、`<get-x>`/`<set-x>` 呼出を delegate local アクセスへ lowering（メンバ委譲経路をミラー、thisRef=null）。`by lazy`→local の `.Value`、カスタム（duck-typed）クラス→`getValue/setValue(null, KProperty)`（合成 KProperty）。`il-localdeleg`（local `by lazy`=42 memoized、カスタム UpperDelegate の get/set=HI/WORLD）実機正＋ilverify-clean＋JVM 差分一致。
- [x] **`式::class`**（インスタンスの実行時クラス・**`IrGetClass`**）— ✅ 2026-06-20 `obj.GetType()` へ（値型/generic param は box してから `callvirt object.GetType()`）。`.simpleName`/`.qualifiedName` は既存の `T::class` 経路（→`Type.Name`/`FullName`）にそのまま乗る。`il-getclass`（`"hi"::class.simpleName`=String、ユーザ class Widget、`Any` 経由の実行時クラス回復）実機正＋ilverify-clean＋JVM 差分一致（名前が一致する String/ユーザ型のみ＝primitive は CLR 名 Int32≠Kotlin Int なので除外）。`T::class`(静的)は既存対応。
- [x] **スプレッド `*array`**（**`IrSpreadElement`**）— ✅ 2026-06-20（`il-mapdes`）単独 `f(*a)`（配列転送）＋全リテラル `f(1,2,3)`＋**混在 `f(1,*a,2)`**（`spreadConcat`＝`List<elem>` に Add/AddRange→ToArray）。`IrVararg` が spread を `filterIsInstance<IrExpression>` で落としていたバグも修正。
- [x] **`value class` / `inline class`** — ✅ 2026-06-20 `@JvmInline value class`（フィールドアクセス・メソッド・引数/戻り値渡し）動作（`il-valclass`、CLR 実機正＋ilverify-clean）。※JVM 差分は環境都合（`@JvmInline` の JVM codegen が kotlinx-coroutines を要求し oracle が `NoClassDefFoundError`）で verify-il のみに収録。box/unwrap 最適化は将来。
- [x] **非ローカル return**（inline ラムダからの `return`）✅(2026-06-20) — lambda 引数あり inline fun の実インライン化（`inlineCall`/`spliceLambdaCall`、body を `valueBlock`=インラインで splice）で解決。IR の IrReturn は既に呼び元 fun を target するので、splice すれば呼び元から return。**可変キャプチャも同時に解決**（呼び元の `var` を直接書込）。`samples/il-inline2`（findFirstEven=4／computed=42／sum=3）。**crossinline/noinline も済**（2026-06-22、`il-xinline`）: ネストしたラムダ/オブジェクトから呼ばれる crossinline ラムダは splice せず実デリゲート local に束縛（非ローカル return が無いと保証済み＝splice 不要）、ネスト側は通常のクロージャキャプチャで取り込む（`capValueExpr` が `valSubst` を尊重）。**inline トラックは意味論的に完了**（2026-06-22 検証）: 非ローカル return は inline fun へのリテラルラムダ引数でのみ合法（済＝`il-inline2`）、変数格納ラムダの非ローカル return は Kotlin 自体が禁止（`'return' is prohibited here`）＝そんなケースは存在しない。変数経由ラムダ（＝非ローカル return 無しの唯一合法形）は通常のデリゲート呼出に落ちて正しく動く（リテラル限定 inline は perf の話で意味論差は無い＝CLR JIT が inline）。残は stdlib inline 本体のみ（IR 不在＝直写像で代替）。
- [x] **部分式/ループ条件内 suspend** — ✅ 2026-06-20 D トラックで実装済（`spillExpr`／`emitWhileCps`／`emitWhenCps`、`il-coro`）。下記 D セクション参照。

### 設計上わざと非対応（CLR では破棄・[[clr-not-jvm-discard-jvmisms]]）
- `dynamic` 型（Kotlin/JS 専用、CLR 文脈で不要）／ `@Jvm*` アノテーション群 ／ JVM 固有 reflection 面

## A-1 確認済みの破綻（最優先・常用機能）
- [x] **拡張関数**（ユーザ型）— 実装済（レシーバを第1引数 `__self` 化、body の `this`→`__self`、呼び出しは `f(recv, args)`）。`samples/m-a1`。**(M)** ✅
- [x] **スコープ関数 / lambda-with-receiver** `apply`/`also`/`let`/`run`/`with` — 実装済（C# IIFE 写像：apply/also は receiver 返し、let/run/with は結果返し、receiver/`it` を IIFE 引数に束縛）。`samples/m-b2`。残: `takeIf`/`takeUnless`/`repeat`。**(M)** ✅
- [x] **網羅 `when` 式（subject + `is`/値）** — 実装済（`IrTypeOperator.INSTANCEOF`/`NOT_INSTANCEOF`→C# `is`、`IMPLICIT_CAST`→ダウンキャストで smart cast、`noWhenBranchMatchedException`→`throw`）。`samples/m-a1`。**(M)** ✅
- [x] **配列** — 実装済（`arrayOf`/`intArrayOf` 系＋`IrVararg`→`new T[]{…}`、`Array<T>`/`IntArray`→`T[]`、indexing、`.size`→`.Length`、for-in）。`samples/m-a1`。**(M)** ✅
- [x] **デフォルト引数** — 定数デフォルトは C# オプション引数（`= literal`）。`samples/m-a1`。残: 非定数/他引数参照デフォルトはオーバーロード展開（A-1 残）。**(M)** ◯（定数）

## A-2 未確認だが高確率で未対応
- [x] vararg → C# `params`（パラメータに `params T[]`、呼び出し側は `IrVararg` を可変長引数へ展開）。`samples/m-a4`。**(S)** ✅
- [x] 分解宣言 `val (a,b) = pair`（Pair/Triple→ValueTuple `.ItemN`、data class→自動生成 `componentN()`）。合成テンポラリ `<destruct>` はシンボル単位で一意名化（衝突回避）。`samples/m-a5`。残: for ループ分解（Map エントリ `for ((k,v) in m)`）。**(S)** ✅
- [x] 演算子オーバーロード網羅（ユーザ `plus`/`minus`/`times`/`get`/`set`/`invoke`/`compareTo`/`contains`/`unaryMinus` 等）— **大半は既存 callInstance フォールスルーで動作**（`a+b`→`a.plus(b)` 等の通常メソッド呼び出し）。併せて修正した2点: ① **`==`(EQEQ) の構造的等価**＝プリミティブは `ceq`、String/参照型は null-safe `Object.Equals`（新ノード `objEq`：`dup;brtrue`＋値型は box＝Nullable<T> も null 化）、`===`(EQEQEQ) は常に identity。② **Kotlin `Any` override の .NET 写像**＝`toString`/`equals`/`hashCode`→`ToString`/`Equals`/`GetHashCode`（定義側＝`DefineMethodOverride`＋`HideBySig|Virtual`、呼び出し側も改名）で `Console.WriteLine(obj)`・data class `==` が正答。`samples/il-op`（演算子）/`m-s2`（data class 等価）。残: `rangeTo`（範囲型が要る）/`plusAssign` 系。**(M)** ✅
  - 注: Codex 相談で EQEQ/EQEQEQ の per-type 降ろし（primitive/String/参照/Nullable<T>）と Object-override の MethodImpl を確定。
- [x] `inline` 関数 / `reified` 型パラメータ。**(L)** — **`reified` は普通の .NET ジェネリックメソッドとして emit**（2026-06-18 Round 6、旧 mini-inliner は退役）。**核心の設計判断**: `reified` は JVM の型消去への妥協で存在する＝CLR は**ジェネリクスが reified**なので不要（[[clr-not-jvm-discard-jvmisms]]）。`inline fun <reified T>` → `M<T>()`、`T::class`→`ldtoken !!0`+GetTypeFromHandle、`x is T`→`isinst !!0`、`x as T` がジェネリックメソッド本体でそのまま動く。Round 5 の generic TypeBuilder が前提。`inlineReified`/`typeSubst` を削除し、呼び出しは generic `callStatic`+`typeArgs` 経路へ落とす。**mini-inliner より厳密に一般**（`f<U>()`＝呼び出し側の型引数が型パラメータのケースも拾う）かつコード減。`samples/il-reified`（`String/Int32/True/False/True/yo/no` 実機正＋ilverify）。落とし穴＝メソッド型パラメータの置換は**参照同一性**で行う（un-baked builder の `ReturnType`/`DeclaringMethod`/`GenericParameterPosition` 反射は null/ゴミで値引数を誤 box＝`retType` と同じ「焼く前の builder を反射しない」教訓）。**`inline` の CLR でのスコープ整理**（ユーザ洞察）: ①reified→済、②**lambda 引数なし inline = ただの通常メソッド**（CLR JIT が自動 inline、`inline` キーワードは no-op ヒント）＝source inline 不要、③lambda ありで非ローカル return/crossinline なし＝通常メソッド＋delegate、④**真に inlining が要るのは inline＋lambda＋(非ローカル return | crossinline) のみ**＋可変ローカルキャプチャ。→[[function-inlining-spike]] のスコープは④に縮小。**残**: ④＋可変キャプチャ・stdlib inline 本体（IR 不在＝直写像）。**(L)**
- [x] 委譲プロパティ `by lazy`（カスタム `getValue`/`setValue` は別）。**(M)** ✅ — **`by lazy` を写像実装**（ユーザ確認: lazy のみ特殊化＋非 lazy はクリーン遅延が妥当）。`kotlin.Lazy<T>`→`System.Lazy<T>`（`birType`）、`lazy{}`→`new System.Lazy<T>(Func<T>)`（`clrNew`、mode 引数は drop＝System.Lazy 既定の synchronized が Kotlin 既定と一致）、委譲フィールド `x$delegate` は ctor で初期化、読取 `obj.x`（getValue）→`obj.x$delegate.Value`（**thisRef も `KProperty` も捨てる＝KProperty 合成不要**）。判定は「委譲フィールド型 == `kotlin.Lazy`」で一般化（プロパティ名ハードコードでない）。ilemit: `clrNew`/`clrPropGet`/`clrPropSet` を generic 対応に（新 `ClrRef`＝`clrg:`→`GenericType`/`func:`→`FuncType`/plain→`ResolveType`）。`samples/il-lazy`（参照型＋メモ化、値型 Int＋`this` 捕捉初期化子、実機正＋ilverify）。**カスタム委譲（ダックタイプ getValue/setValue）も実装**（ユーザ提案＝KProperty は compiler-generated 合成クラスにするしかない、Kotlin/JVM の `PropertyReferenceImpl` と同戦略）: `KProperty<*>` を**合成 `KProperty` インターフェース（`get_name():string`）＋ `KPropertyImpl(name)` クラス**でユーザアセンブリに生成（iterator のモノモーフ化機構の応用）。委譲読書 `obj.x`/`obj.x=v` → `obj.x$delegate.getValue(thisRef, new KPropertyImpl("x"))`/`.setValue(…, value)`、`property.name`→`get_name()`、`IrPropertyReference`→`new KPropertyImpl("x")`。`samples/il-deleg`（set/get＋`property.name` ログ、実機正＋ilverify）。**stdlib インターフェース委譲も実装**（`class D : ReadWriteProperty<Any?,V>`/`ReadOnlyProperty`）= `ReadOnly/ReadWriteProperty<T,V>` を**V でモノモーフ化した合成インターフェース**（`ROProperty_<V>`＝getValue、`RWProperty_<V>`＝getValue+setValue、`(thisRef:object, property:@KProperty[, value:V])`）に写像、ユーザ delegate が `DefineMethodOverride` で実装。委譲アクセスは `callvirt`（interface 実装で virtual 化＝`Call` だと ilverify `ThisMismatch`、callvirt はダックタイプ final にも合法）。`samples/il-rwp`（実機正＋ilverify）。**`by map` も実装**（`val x by map`→`map["x"]` をプロパティ型へ cast、`var`→`map["x"]=v`）`samples/il-bymap`。**`Delegates.observable`/`vetoable`/`notNull` も実装**（stdlib `ObservableProperty` 等本体が IR 不在なので**自前の合成委譲クラス**を V でモノモーフ化生成＝RWProperty_V を実装、observable は setValue で onChange 呼出、vetoable は callback が true の時のみ格納、notNull は未設定で `InvalidOperationException`）`samples/il-deleg2`。**Reflection.Emit 制限への対処**: 合成 `KProperty`（TypeBuilder）を BCL デリゲートの generic 引数にすると "TypeBuilder generic instantiation does not support resolving members" で落ちる→ **デリゲート/ラムダ署名でのみ KProperty を object に erase**（`birTypeDeleg`、observable の callback は KProperty を通常無視）。**委譲プロパティ全サーフェス完了。残**: KProperty の `.name` 以外のメンバ（`.returnType`/`.call()` ＝ reflection）＝需要次第（クリーン遅延＋下記レジスタ記載）。
  - **併せて this 捕捉ラムダ／ローカル関数を修正**（潜在バグ）: クラス内ラムダ／ローカル関数が member を参照すると外側 `this` を捕捉するが、`capturedVars` が `<this>` を除外＝非捕捉として誤って static lift→`InvalidProgram`。`capturedVars(includeThis=true)` ＋ 捕捉を identity ベース（`captureSubst`/`captureFieldName`→`__outer`/`capValueExpr`）に統一（lambda は closure フィールド、local fn は `__outer` 先頭パラメータ）して修正。`list.map { it + field }`／関数内 `fun h() = base + x` 等のクラス内ラムダ・ローカル関数全般に効く（IL ローダ検証済）。
- [x] ローカル関数（関数内 fun、クロージャ捕捉）— C# ローカル関数へ写像（修飾子なし、C# が hoist＆外側ローカルを capture）。`samples/m-a7`。**(M)** ✅
- [x] オブジェクト式・無名オブジェクト `object : I { }`。**(M)** ✅ — 匿名 `IrClass` を合成名 `__objN` に lift（`anonNames: IdentityHashMap<IrClass,String>` で名前写像＝IR 名 `<no name provided>` は IL 不正なので全 ownerType/型参照を `typeName()` 経由に）→ `new __objN(captures...)`。**インスタンスフィールド（状態）対応**＋**キャプチャ対応**: 外側の値（**囲み `this` 含む**）を ctor 追加パラメータ＆capture フィールド化。capture 解析はシンボル同一性で行う（anon 自身の `<this>` と捕捉した外側 `<this>` は同名だが別シンボル＝`captureSubst: IdentityHashMap` で `this.__outer` へ書換）。`samples/il-objexpr`（非捕捉）/`il-iter`（捕捉 iterator）。**残**: **可変キャプチャ**（外側ローカルを object 経由で書込＝ref セルが要る）はクリーンな `unsupportedExpr:"capturing-object-literal-mutable"` で遅延。`inner class` も同じ外側 this 捕捉機構を流用可（未着手）。
- [x] ユーザ companion object（メンバ/定数）— 囲みクラスの `static` メンバへ写像（`const` は use-site でインライン、非 const val は `static readonly` フィールド、メソッドは `static`、`Outer.member` 呼び出しへルーティング）。`samples/m-a6`。**(S)** ✅
- [x] nested / inner class（ユーザ定義）。**(M)** ✅ — **真の CLR ネスト型として emit**（`Outer+Inner`、`efe24af`／2026-06-23）。BirEmitter `typeDef` が IR の親ユーザクラスから `nestedIn` を自動付与、ilemit Pass1 が囲み `TypeBuilder.DefineNestedType`（`Nested*` アクセス）、`Ordered()` がネスト型を囲み型より先に `CreateType`。**ネスト型は囲み型の private にアクセス可**＝flatten 時に要した可視性回避が不要になり、真の private フィールドが成立（A-108）。**inner class**: 加えて外側インスタンス `this@Outer` を `__outer` フィールド＋ctor 先頭パラメータとして capture（`captureSubst` で `this.__outer` へ書換）。`samples/il-nested`/`il-inner`（実機正＋ilverify-clean）。残: **多段 inner**（孫の this 参照は `__outer.__outer` 連鎖＝単段のみ対応）。
- [x] `typealias`。**(S)** ✅ — frontend が IR 到達前に underlying 型へ解決済み＝backend は透過（`typealias Name = String`/`IntPair = Pair<Int,Int>` を実機確認）。新規対応コード不要。
- [x] ユーザ定義注釈（宣言・retention・適用）。**(M)** ✅ — **.NET カスタム属性として emit 済**（`SetCustomAttribute`/`BuildCab`、`il-annot`）。**実行も破綻しない**（`annotation class` 宣言＋適用は現状 drop され、注釈付き関数は正常実行）。残: retention/target の厳密写像のみ（任意・cosmetic）。
- [x] ビット演算 `and`/`or`/`xor`/`shl`/`shr`/`ushr`/`inv`（`m-b12`）。**(S)** ✅
- [x] zip（→ValueTuple）、`Char.code`（→`(int)c`）、char 範囲 `in`（`m-a3`）。**(S)** ✅
- [~] 数値変換: **済** `toInt`/`toLong`/`toDouble`/`toFloat`/`toShort`/`toByte`/`toChar`（数値レシーバ→C# キャスト `(int)x`）。`samples/m-a5`。**`toString(radix)` 済**（2026-06-20、`il-valclass`、`255.toString(16)`→`Convert.ToString(value, base)`、Int/Long）。**unsigned 型 ✅**（2026-06-20、`il-unsigned`）: `UInt`/`ULong`/`UByte`/`UShort`→CLR `UInt32`/`UInt64`/`Byte`/`UInt16`（frontend が算術を plain op に lower 済＋const は符号付き bit-pattern を保持）。**(M)** ✅
- [~] `lateinit` / `field` / backing field 制御。**(S)** — **`lateinit var` 動作**（通常フィールド＋代入に降りる、実機確認）。残: カスタムアクセサ内の `field` 識別子・get/set body。
- [x] スマートキャスト網羅、`as?`/`is` のエッジ。**(M)** ✅ — **`as?` 動作**: 参照型 T→`isinst T`（null or ref）、値型 T→`T?`＝`Nullable<T>`（isinst→unbox+wrap / 不一致は空 Nullable）。新ノード `isinstRef`/`safeCastValue`。**併せて値→`object` の引数 box を全 user 呼び出しで修正**（`callStatic`/`callInstance` が param 型でなく実引数型のままだった＝`Any` 引数に値型を渡すと未 box＝InvalidProgram。`_mparams` で宣言時 param 型を記録し call-site box）。`samples/il-smartcast`/`il-langtail`。**✅ 複合条件 smart-cast も修正済**（2026-06-23、`641e46d`）: `if (x is Int && x > 10)` が x=3 で誤って then 枝を取るバグ＝`>` の operand `x` が boxed（Any）のままだった→`bin` が boxed operand を相手 operand の型へ cast（＋`IrGetValue` が narrowed smart-cast 型を尊重）。`when`＋型は従来通り。
- [x] **二次コンストラクタ（`constructor(...)`）＋ `init {}` ブロック**（IL）。**(M)** ✅ — ilemit を**複数 ctor 対応**に（`TypeInfo.Ctors`/`CtorDefs` リスト、`new`/delegation は引数個数で ctor 選択＝`SelectCtor`）。BirEmitter が `this(...)`(同一クラス＝sibling ctor へ)と `base(...)`(基底へ)を区別（`thisArgs`/`baseArgs`）。`init{}`＋プロパティ初期化子は base 委譲する ctor（＝`IrInstanceInitializerCall` を持つ主 ctor）にのみ展開。`samples/il-ctor`（Rect の `this(side,side)`＋init、Labeled の本体付き二次 ctor）、実行正＋ilverify Verified.
- [x] **可視性修飾子** `private`/`internal`/`protected`/`public` → IL アクセス修飾子。**(S)** ✅ — **メソッド/コンストラクタ/クラス**: `visOf`→ ilemit `AccessOf`（`MethodAttributes.Private`/`Assembly`/`Family`/`Public`、型は `Public`/`NotPublic`、nested 型は `Nested*`）。**フィールド可視性も実装**（2026-06-23、`il-fieldvis`）: プロパティの可視性を backing field に反映＝`private`→真の `FieldAttributes.Private`／`internal`→`Assembly`／`protected`→`FamORAssem`。**真の private が成立するのは inner/nested を真の CLR ネスト型に出すようになった**から（ネスト型は囲み型の private にアクセス可、`efe24af`）。interface メンバ・Object-override・匿名/合成型は public 固定。`samples/il-vis`/`il-fieldvis`。残: protected override の可視性整合（任意）。
- [x] **enum リッチ API**: メンバ/メソッド/抽象メンバ/**per-entry 本体**を持つ enum。**(M-L)** ✅ — **基本 enum は CLR enum 維持（.NET interop）、rich enum は singleton class へ二経路化＝実装完了**。`isRichEnum`（ctor 引数 or ユーザメソッド or エントリ本体）→ `richEnumDef`: 平坦 class（`__name`/`__ordinal`+ユーザ field、private ctor、エントリは `static readonly` field を `.cctor` で `new`、`ToString`→`__name`、`values()`→fresh array、`valueOf`→線形 match＋`ArgumentException`、`==`は singleton 参照同一性）。消費側ルーティング（`IrGetEnumValue`→staticField、`.name`/`.ordinal`→field、`values`/`valueOf`→callStatic）。`samples/il-enumrich`（mass/heavy/name/ordinal/valueOf/values-for/==）。**✅ per-entry 本体/抽象メンバ実装済**（2026-06-23、`69c82d4`、`il-enumbody`）: 抽象メンバを持つ enum は base を abstract 化＋抽象メンバ宣言、各 body エントリを `<>Enum_NAME : Enum` サブクラス（override メソッド＋`(__name,__ordinal)` ctor が `NAME(args)` の enum-super 引数を base へ forward）に。`isOverride` が ENUM_CLASS 親も認識（base virtual スロット再利用）。`PLUS { override fun apply(a,b)=a+b }` 動作。
- [x] `when` 複数値分岐（`0,1,2 ->`）・`in`/範囲分岐、`do-while`、`IrComposite`、**増分演算子 `++`/`--`/`+=`**（POSTFIX/PREFIX_INCR の coercion-to-Unit ブロックを文展開、temp は `{}` で scope 化、`inc`/`dec`→`+1`/`-1`）。`samples/m-a2`（差分ハーネスで kotlin/jvm と一致確認）。subject 無し `when`（`when { cond -> }`）も対応（`samples/m-a4`、既存 IrWhen 経路で動作）。残: `return@label`、`this@Outer`。**(S)** ✅
- [x] 複数行/raw 文字列 `"""..."""`、エスケープ網羅。**(S)** ✅ — frontend が文字列リテラル（raw・エスケープ含む）を解決＝backend は IrConst 文字列としてそのまま emit（多行＋`\"`/`\\`/タブを実機確認）。対応コード不要。
- [x] `Nothing` 型 / `TODO()` / `throw`・`return` の式利用。**(S)** ✅ — **`throw` 式（Nothing 型）動作**: `x ?: throw …` / `if(c) v else throw …` → `throwExpr`（throw が制御を移すので merge に値が来ない＝EmitCond の片枝で OK）。併せて **Kotlin builtin 例外型→.NET 写像を IrConstructorCall に追加**（`IllegalStateException`→`InvalidOperationException` 等、throw 文/式 両方）＋**`T`→`T?`(Nullable<T>) 引数 wrap を全 call-site に追加**（`req(42)` で `Int?` 引数に int 直渡し＝未 wrap だった）。`TODO()`/`error()`/`require` は既存。`samples/il-throwexpr`。**✅ `return` の式利用も実装済**（2026-06-23、`641e46d`、`il-langtail`）: `val x = if (c) a else return b` → `returnExpr` ノード（throwExpr と同型＝制御を移すので merge に値が来ない、tryStack 対応）。非ローカル return は別途 inline splice で対応済（E-0.5 参照）。
- [x] トップレベル `val`/`var`/`const val`（ファイルクラスの `const`/`static readonly`/`static` フィールド化、非 const の読み書きは兄弟 static 参照へ畳む）。`samples/m-a4`。**(S)** ✅
- [x] `tailrec`（意味論）。**(S)** ✅ — **意味論は正しく動作**（通常の再帰として emit、`fact(5,1)`=120 実機確認）。残: 末尾呼び出しのループ化＝**最適化**（深い再帰の stack 効率。`.tail.` prefix or 手動ループ lowering）。正しさは満たすが最適化は未。

## A-3 切り出した遅延サブ機能レジスタ（親項目の「残」を1か所に集約）
**方針: 完成項目から切り出した残りは silent drop せず、(a) 親 `[~]`/`[x]` 項目の「残:」に明記、(b) コードで該当時に明示エラー（`unsupportedExpr/Stmt` → ilemit が `unsupported Kotlin construct (deferred): <理由>` を throw、`<理由>` が下表のキー）、の二重で追跡する。**

| 遅延サブ機能 | 親項目 | コードのマーカ／状態 | 想定対応タイミング |
|---|---|---|---|
| 可変キャプチャ（**inline ラムダ**から外側ローカルへ書込）✅ | — | inline 展開で呼び元の `var` を直接書込（`il-inline2` sum=3）。非 inline ラムダ/object/local fn の可変キャプチャ（ref セル）は残 | inline 経由は済。残りは closure の ref-cell |
| 非ローカル return（inline ラムダから外側関数）✅(2026-06-20) | — | lambda 引数あり inline の実インライン化で解決（`il-inline2`） | 済（[[function-inlining-spike]]） |
| crossinline / noinline ✅(2026-06-22) | A-2 inline | 実デリゲート local 化（splice 回避、`il-xinline`） | 済 |
| stdlib inline 本体 | A-2 inline / B | stdlib 本体は IR 不在＝(b)直写像で代替 | 本体入手可否次第（実用は手写像で代替済み） |
| ~~ダックタイプ カスタム委譲 `getValue`/`setValue`~~ | A-2 by lazy | **✅ 実装済**（compiler-generated `KProperty`） | — |
| ~~stdlib インターフェース委譲（`ReadWriteProperty`/`ReadOnlyProperty` 実装）~~ | A-2 by lazy | **✅ 実装済**（V-モノモーフ化合成インターフェース） | — |
| ~~`by map`・`Delegates.observable/vetoable/notNull`~~ | A-2 by lazy | **✅ 実装済**（map 直写像／合成委譲クラス） | — |
| KProperty `.name` 以外（`.returnType`/`.call()` 等 reflection） | A-2 by lazy | `unsupportedExpr`（委譲型 `kotlin.*` ガード）／合成 KProperty は `.name` のみ | **需要ドリブン・小**: 合成 `KPropertyImpl` にメンバ追加（`.returnType` は型情報の保持が要・中）。委譲の主要ユースは `.name` のみなので低優先 |
| 開いたジェネリック `Iterator<T>`（ユーザ総称関数） | A-2 iterator | **インフラ解禁済**（G-2 で generic interface 定義が可能に）。現状なお KIterator はモノモーフ化で運用 | **小・需要ドリブン**: `KIterator<T>` を generic interface 化（G-2 の機構をそのまま適用）。ユーザ総称関数で iterator を返す実例が出た時点で対応 |
| 多段 inner class（孫 this 参照） | A-2 inner | 単段のみ（`__outer` 1 段） | 小（`__outer.__outer` 連鎖）＝需要が出た時点で即対応可 |
| ~~per-entry 本体/抽象メンバ enum（`X { override … }`）~~ ✅ 実装済（2026-06-23、`il-enumbody`） | — | base abstract 化＋エントリ毎サブクラス | done |
| ~~フィールド可視性（真の private フィールド）~~ ✅ 実装済（2026-06-23、`il-fieldvis`） | — | inner/nested を真の CLR ネスト型化して private アクセスを確保（`efe24af`） | done |
| `toString(radix)` / unsigned 型 / カスタムアクセサ `field` | A-2 数値変換 / lateinit | 主要数値変換は済 | B stdlib 仕上げと一体（S） |

> **要約（ユーザ質問への回答）**: 上記はすべて 1.0 必須スコープ内で、**親項目に紐付けて追跡済み**。大別すると ① **inlining/CFG スパイク待ち**（可変キャプチャ・非ローカル return・crossinline）、② ~~ジェネリック TypeBuilder 待ち~~ **→ G-1/G-2 で解禁済**（開いた Iterator は generic interface 機構の適用待ち＝小・需要ドリブン）、③ **KProperty 表現決定待ち**（カスタム委譲）、④ **即対応可能な小物**（多段 inner・toString(radix)・enum per-entry）。①は専用の大型トラック（inlining-spike）に集約。②④は需要ドリブンで随時。
- [x] **iterator 演算子**（ユーザ型 `operator fun iterator()` で for-in 可能に）。**(M)** ✅ — frontend が for-in を Kotlin Iterator プロトコル（`val it = x.iterator(); while(it.hasNext()){ val e = it.next() }`）へ**完全脱糖**するので、.NET `IEnumerator` への写像は不要（意味論差の問題が消える）。2 経路: **(1) 具象イテレータクラス**（`operator fun hasNext()/next()` を持つユーザ class を返す）は既存の汎用 callInstance 経路でそのまま動作。**(2) `object : Iterator<T>` 慣用形**（無名オブジェクトが `kotlin.collections.Iterator<T>` を実装）= Codex 確認の**モノモーフ化合成インターフェース**: IL はジェネリックインターフェース定義が未対応なので、要素型ごとに非ジェネリック `KIterator_<elem>`（`hasNext():bool`/`next():<elem>`）を合成し、`Iterator<T>`→`@KIterator_<elem>` 写像、lifted anon が `DefineMethodOverride` で実装、`it.hasNext()/next()` は受け側型から interface dispatch。`samples/il-iter`（具象＋無名・捕捉あり、実機正＋ilverify-clean）。**併せて capturing object literal を実装**（下記オブジェクト式参照）。残: 開いたジェネリック `Iterator<T>`（ユーザ総称関数）＝ジェネリック TypeBuilder（Track E）後。

---

# B. CLR 版 Kotlin stdlib（最大の山）

現状: frontend は JVM の `kotlin-stdlib.jar` で解決、backend は `println` 等一部 intrinsic のみ写像。
方針判断（design-first 必要）: **(a) BCL への体系的写像** か **(b) C# 実装の CLR kotlin-stdlib** か、ハイブリッドか。— [[design-first-on-hard-features]]

- [x] **方針決定（ABI）**: **(a) inline lowering を軸に、(b) BCL 写像で補完**（ユーザ確定 2026-06-16）。(c) CLR stdlib ランタイム同梱は純バインディング原則違反で却下。
  - **(a) 実機 feasibility（調査済）**: `ir.inline.FunctionInlining(LoweringContext, InlineFunctionResolver)`。`LoweringContext` は5メンバ（軽い）だが `backend.common.ir.Symbols`(17 抽象＝intrinsic シンボル群)＋`SharedVariablesManager`＋resolver(`PreSerialization…Resolver(ctx, IrMangler)`)＋**stdlib inline body の入手可否**が未知＝**XL スパイク**（coroutine の CommonBackendContext 級）。→ 専用セッションで実施。
  - **(b) 当面の主戦力**: 共通 stdlib 表面（collection ops＝LINQ 静的写像、scope 関数）を直接 codegen 写像で先行提供。(a) 完成後は inline 版が一般ケースを引き取り、(b) 写像はクリーンな出力として残すか整理。
  - 副次改善: **`renderLambda` を値返し対応**（最後の式を `return`／Unit は Action のまま）。Func 型デリゲート・LINQ・イベントの前提。
- [x] コレクション生成 `listOf`/`mutableListOf`/`arrayListOf`（→List）、`setOf`/`mutableSetOf`/`hashSetOf`（→HashSet）、`mapOf`/`mutableMapOf`/`hashMapOf`（→Dictionary、`to`-ペアから indexer 初期化）。List/Map の `[]` indexing も。`m-b6`,`m-b7`。`emptyList`/`emptyMap`/`emptySet` も。**(S–M)** ✅
- [x] コレクション操作 → LINQ 静的写像。**済 30+種**: map/filter/flatMap/take/drop/takeWhile/dropWhile/sorted/sortedBy/sortedByDescending/sortedDescending/reversed/forEach/fold/reduce/any/all/none/count/sum/sumOf/first/firstOrNull/find/last/lastOrNull/single/singleOrNull/distinct/toSet/toList/max/min/maxOrNull/minOrNull/maxByOrNull/minByOrNull/average/contains/joinToString/asSequence（`m-b1`,`m-b3`,`m-b6`,`m-b9`、値返しラムダ）。**mapIndexed 済**（2026-06-20、`il-bmore`）= `Range(0,MAX).Zip(src, Func<int,T,R>)`（Zip の (first,second)=(index,value) が Kotlin 順と一致＝引数 swap 不要）。**chunked/filterNotNull 済**（2026-06-20、`il-chunk`）= `synthLambda`（合成1引数ラムダの汎用機構）で `chunked`→`Chunk(src,n).Select(c=>c.ToList())`（T[]→List<T>）、`filterNotNull`(参照型)→`Where(x=>x!=null)`。**mapNotNull/flatMap/flatten/filterNotNull(値型 T?) 済**（2026-06-21、`il-collmore`）: mapNotNull=Select→null除去、flatMap/flatten=SelectMany(+合成 identity `List<R>→IEnumerable<R>`)、値型 `T?` は `Nullable<T>` unwrap（HasValue/Value）。併せて **単一要素 `listOf(x)` の要素型バグ修正**（vararg でなく単項オーバーロード＝要素型は `call.type` の `List<T>` から、`Any` 誤判定を解消）、**値返し if/when の `T?` 合成**（`fun f():Int? = if(c) x else null`＝branch を Nullable<T> へ coerce、`EmitCond`/`EmitArg` 共有）。**average/indexOf 済**＋**emptyList/emptySet/emptyMap 済**（2026-06-21）。**未実装 stdlib 関数は ilemit クラッシュでなくソース位置付きコンパイルエラー**（BirEmitter のガード＝`kotlin.collections/sequences/text/ranges/comparisons` の未対応 free/extension 関数を明示エラー化、C# oracle 経路は非ブロック）。残（**全てクリーンエラーで安全に拒否**）: `windowed`/`partition`/`associate`/`getOrElse`/`runningFold`/`scan`/`withIndex`/`sortedWith(compareBy{})`（sortedBy/Descending で単一キー対応済）。**(M)** ✅（主要）
- [x] `Sequence`（遅延）/ `asSequence` / `sequence{}` / `yieldAll` / `generateSequence`。**(M)** ✅ — 2026-06-20 **`asSequence` ＋遅延シーケンス操作**: `Sequence<T>`→**遅延 .NET `IEnumerable<T>`**（LINQ は元々 deferred なので Kotlin の遅延意味論と一致）。`isSequenceType` を導入、collection-ops ブロックのゲートを `isCollectionType || isSequenceType` に拡張、`lazySeq` フラグで中間 list 生成op（map/filter/take/…）の `ToList` материализ化を抑止（eager コレクションは従来通り ToList、シーケンスは deferred 維持）。`asSequence()`→受け手の pass-through、`toSet`→`ToHashSet`／`takeWhile`/`dropWhile`（→`TakeWhile`/`SkipWhile`、遅延）／`single`/`singleOrNull`（→`Single`/`SingleOrDefault`）も追加。終端（toList/first/count/sum/single）が初めて評価を強制。`samples/il-seq`（map→filter→toList=`6,12`／map→filter→first 短絡=16／filter→count=3／map→sum=27／map→take→toList=`10-20-30`）実機正＋ilverify-clean＋**JVM 差分一致**（PURE corpus に追加）。**`sequence{}`/`yieldAll`/`generateSequence` も実装済**（2026-06-22〜23、`il-kseq`/`il-kgenseq`、ランタイムは DotKt.Sequences 名前空間。generateSequence の nullable `(T)->T?` は値型/参照型で variant 選択）。
- [~] 文字列 / 文字: **済 uppercase/lowercase/trim/trimStart/trimEnd/substring/replace/startsWith/endsWith/contains/indexOf/padStart/padEnd/split**（`m-b4`,`m-b7`、→.NET String メソッド）。**Char 操作済**（2026-06-20、`il-char`、JVM 差分一致）: `isDigit`/`isLetter`/`isWhitespace`/`isLetterOrDigit`/`isUpperCase`/`isLowerCase`（→`System.Char.IsX`）・`uppercaseChar`/`lowercaseChar`（→`ToUpper`/`ToLower`）・`.code`（Char→Int コードポイント）・`Int.toChar()`。**`toRegex` 済**（2026-06-20、`il-regex`、verify-il のみ＝JVM oracle が kotlinx-coroutines NoClassDefFound）: `"p".toRegex()`→`new System.Text.RegularExpressions.Regex`、`containsMatchIn`→`IsMatch`/`replace`→`Replace`。**`format` 済**（2026-06-20、`il-bmore`）= **literal printf を .NET composite format にコンパイル時変換**（`translatePrintf`: `%d`→`{0}`、`%.2f`→`{0:F2}`、`%05d`→`{0:D5}`、`%x`→`{0:x}`、`%%`→`%`、width/precision/flags 対応）→ `String.Format(fmt, object[]{…})`。非 literal format string ＝クリーンエラー。**✅ `Regex.matches`(完全一致)/`find` も実装済**（2026-06-23、`641e46d`、`il-regex`）: `DotKt.Text.Regexes` shim（matches＝全体一致、find→`Match?`）＋ `MatchResult.value`→`Match.Value`。**(M)**
- [x] Math（`kotlin.math.*`）→ `System.Math.*`（abs/max/min/sqrt/pow/round/floor/ceil/exp/ln/log10/sin/cos/tan）。`m-b4`。残: 範囲・進行を値として（`IntRange`/`step`/`reversed`）。**(M)** ✅
- [x] `Pair`/`Triple`/`to` → C# ValueTuple（`.first`→Item1 等）。`m-b5`。**`Result`/`runCatching` 済**（2026-06-20、`il-result`）= 合成 generic `Result<T>`（value/failure/isSuccess/isFailure フィールド）、`runCatching{}`→try/catch valueBlock で構築、accessors（getOrNull/getOrThrow/getOrDefault/exceptionOrNull/isSuccess/isFailure）は フィールド上で inline（getOrNull は値型 T→Nullable<T> 構築）。`Throwable.message`→`Exception.Message`。残: `Comparable`/`Comparator`（sortedWith は上記）。**(M)**
- [x] 例外/前提ヘルパ: `require`/`check`/`error`/`TODO`（→ throw/if-throw）、`requireNotNull`/`checkNotNull`（valueBlock で1度評価→null なら throw 否なら非 null 値、参照型＋値型 `T?` 両対応＝`il-reqnn`）、`IllegalArgumentException` 等の型写像。`m-b5`。残: `runCatching`（要 `Result` 型）。**(S–M)** ✅
- [x] `kotlin.io`（`readLine` 等）/ 標準入出力。**(S)** ✅ 2026-06-20 `readLine()`→`Console.ReadLine()`（String?、EOF で null）。`print`/`println` は既存。stdin 必須で no-stdin ハーネスに乗せにくく手動検証（`echo hello | run`→`got: hello`）。残: `readlnOrNull`/`System.out` 直）。

---

# C. interop 完全化（残り）

## C-0 双方向 interop（「.NET バインディング」の本質・最重要）
- [x] **逆方向 interop（基盤達成・実機）**: Kotlin 生成アセンブリを C# から `ProjectReference` で参照し、Kotlin の class（`new Greeter("World").greet()`）・top-level fun（`LibKt.add(2,3)`）を呼べる（`samples/revinterop`、別アセンブリとして消費）。生成 C# が public 型なので双方向が成立。**(L)** ✅（基盤）
- [x] 逆方向 interop **機能面** ✅ 2026-06-21（C# reflection で公開面を確認）: **generics の公開形**（`Box<T>` → C# generic）、**`suspend fun`↔`Task<T>` の公開**（`compute(Int): Task<Int>`）、class/メソッド/top-level fun/プロパティ。**(M)**
- [~] 逆方向 interop **意匠面**（optional polish）: C# 慣習の命名（lowercase fun → PascalCase・任意）、**nullability 注釈 `[Nullable]`**（C# 消費側が `string?` を見られる）。いずれも cosmetic で 1.0 出荷をブロックしない（C# は lowercase メソッドも呼べる）。要望時に実装。**(M, deferred-optional)**
- [x] 検証: `samples/revinterop`（C# consumer が Kotlin アセンブリを参照）を verify-all に常設。**(S)** ✅

## C-1 .NET 機能の消費（Kotlin → .NET）
- [x] **for-in で .NET `IEnumerable`/`IEnumerator` を反復** ✅ 2026-06-21（`il-forin`）: `birForLoop` が .NET 型ソースを `forEachInline`（GetEnumerator/MoveNext/Current）へ。façade の `operator iterator()` は frontend を満たすだけ（backend は bypass）。残: facadegen の iterator() 自動生成（DX）。**(M)**
- [x] **`use` / `IDisposable`**（try-finally で `Dispose`）— 実装済。併せて `AutoCloseable`/`Closeable`→`System.IDisposable`、`close()`→`Dispose()` を写像。`repeat(n){}` も。`samples/m-c4`。**(S)** ✅
- [x] **注入の取りこぼし解消（配列・クロス型）** ✅ 2026-06-21（`samples/il-firgap`）— FIR 注入が拾えていなかった2種を解消: ① **配列型メンバ**（`int[]`/`string[]` の param/return＝旧 `Supported` が `!IsArray` で丸ごとスキップ）→ `Array<T>`/`IntArray` 等として注入（`String.Split` 等が解決）。② **他 .NET 型を参照するメンバ**（旧 `Map` が `Any?` に縮退）→ **実型の単純名を出力**し、その型も import 済みなら `coneOf` が解決（未importなら従来通り Any? に degrade）。配列/クロス型対応は **`--meta`（注入）経路のみ**（`MetaMode`）＝façade-.kt 経路は valid Kotlin を保つため従来動作。`Engine().makeWidget().value()`=42・`Arr.sumArr(Arr.range3())`=60 実機正＋ilverify-clean。残: 総称の入れ子（`List<int>` param）は引き続き Any?。**(M)**
- [x] **ジェネリック .NET 型の FIR 直接注入（2026-06-18）** ✅ — 任意の generic .NET 型を façade 無しで `import`・構築・継承。`samples/il-netgen`（`Collection<Int>()` を構築し `Add`/`Count`/`Contains`/`IndexOf`、`3/True/2`）・`il-netgen2`（`class IntColl : Collection<Int>()` ＝**generic .NET 基底継承**、`3/True/2`）実機正＋ilverify-clean。**実装**: facadegen が generic 型定義（`Collection`1`）を解決し `class Collection <open名> open T`（型パラメータ名を末尾トークンで）＋`fun Add Unit final item:T`（`Map` が generic param→名前）を吐く。FIR インジェクタ（`ClrTypeInjection.kt`）が `typeParameter(Name,Variance.INVARIANT,false,key)` で型パラメータを宣言、`coneOf` が `owner.typeParameterSymbols…constructType` で `T` を解決（設計検証エージェントで 2.2.0 API 確定）。backend：`birType` が構築済み注入 generic を `clrg:<open>[args]`、`IrConstructorCall` も同様、メンバアクセスは**受け側が .NET 型なら受け側 birType（構築済み）／継承メンバなら subclass の .NET supertype（型引数を保持）**を type に。ilemit `EmitClrCall` を `ClrRef`（generic 対応）＋ overload は型引数解決失敗時に名前+arity fallback（`Add(T)` を構築型 `Collection<int>` 上で解決）。**型 RESOLUTION のフロントエンド機能＝[[s5-fir-injection-seam]] の generic 拡張**。残: generic .NET メソッド `M<T>()`（呼出時 MakeGenericMethod）・generic indexer（`this[i]`）・境界。**(L)**
- [x] **ジェネリック .NET 基底の継承** `class C : Collection<Int>()` ✅（上記 il-netgen2、IL 側 Round 8 ＋フロントエンド generic 注入で end-to-end 完了）。
- [x] **.NET インターフェース実装**（Kotlin class が注入した .NET interface を実装）— 実装済（facadegen が `interface` 種別検出、injector が `ClassKind.INTERFACE`＋abstract メンバ合成、Kotlin が override）。`samples/m-c5`：`class Money : System.IComparable` を façade-free 実装し多態使用。残: 総称 interface（`IComparable<T>`）。**(L)** ✅（非総称）
- [x] .NET enum 取り込み — 実装済（facadegen が `enum` を検出し **object＋val プロパティ**として出力＝FIR enum-entry 合成を回避、`DayOfWeek.Friday`→`System.DayOfWeek.Friday`）。`samples/m-c6`。併せて `OBJECT_METHODS`（toString/equals/hashCode）を @Clr/注入型のメソッド呼び出しでも適用。**(M)** ✅
- [x] nullable 値型 `Int?` → C# `int?`（`csType` が nullable 値型に `?`、`var` 不可なら明示型を出力）＋ `isEmpty`/`isNotEmpty`。`samples/m-c8`。残: `Nullable<T>` 引数の .NET API 往復。**(M)** ✅
- [~] `out` / `ref` パラメータ — **意図的に見送り**。Kotlin に out/ref 構文が無く、facadegen は byref param のメソッドを surface しない（＝壊れず単に不可視）。最頻ユース（`Int32.TryParse` 等）は Kotlin の `toIntOrNull()` 等のイディオムで充足済み。残りの out/ref API は holder 設計が必要だが価値が低く保留。**(M, deferred)**
- [x] ジェネリックメソッド `T M<T>(...)` ✅ 2026-06-21（`il-c1net`、`Util.echo<T>`＝既に動作・検証追加）。**(M)**
- [x] static フィールド・`const`・static プロパティ（facadegen が object 型に static field/prop を出力、`Math.PI`→`System.Math.PI`）。`samples/m-c7`。**(S)** ✅
- [x] .NET 拡張メソッド ✅ 2026-06-21（`il-c1net`）: `@Clr` object 上の Kotlin 拡張 `fun T.m()` → static `Owner.M(receiver, …)`（拡張レシーバを第1引数へ前置）。`5.tripled()`。**(M)**
- [x] 構造体（値型）interop ✅ 2026-06-21（`il-c1net` Vec2）: 値型インスタンスメソッド（`c.mag2()`）は `EmitClrCall` が **レシーバを EmitAddr**（managed pointer）で。残: コピー意味論の厳密検証・`readonly struct` 最適化。**(M)**
- [x] 演算子/変換メソッド ✅ 2026-06-21（`il-c1net` `Vec2 + Vec2`）: `@Clr("op_*")` は **static メソッド**＝Kotlin instance レシーバを第1引数へ前置（op_Addition/op_Equality/op_Implicit/op_Explicit）。**(M)**
- [x] params 配列・既定引数（.NET 側）✅ 2026-06-21（`il-c1net`）: params は既に動作、**.NET 既定引数**は `EmitArgs` が不足末尾を metadata の `DefaultValue` で補填。**(S)**
- [x] nested .NET 型 ✅ 2026-06-21: `@Clr("Outer+Inner")`（.NET nested 区切り `+`）で内部型を façade・呼び出し（`o.makeInner().value()`）。generic indexer は構築型上の operator get/set＝G-5 と同機構（注入型は injector の `indexer` フィールド）で対応済み。**(M)**
- [x] delegate 網羅 ✅ — event `+=`/`-=` 実機（`il-event`: `add_`/`remove_CollectionChanged`、`-=` は格納ハンドラで delegate 等価）、`Func`/`Action` 総称・戻り delegate は既存（il-c1net 等）。**(S–M)**

## C-2 取り込み UX の一本化（使い分けの排除・1.0 DX 必須）
現状 `<KotlinClrType>`（façade-free 注入）と `<KotlinClrFacade>`（.kt 生成）の2系統＋総称は façade、という**使い分けが利用者に難しすぎる**。これを無くす。
- [x] **理想形＝import 駆動の自動解決** ✅ 2026-06-21（`ktproj-import`）: MSBuild が .kt を scan し `import System.Text.StringBuilder` 等を facadegen `--scan` → FIR 注入。型は **実 .NET 名前空間そのものを Kotlin パッケージとして** 解決（`clrgen` 合成パッケージは撤廃）。型リストの手書き不要・注入/façade の選択不要。**(L, design-first)**
  - 実装案A: ビルドが `.kt` の `import` を走査して取り込み型集合を自動導出（手書きリスト撤廃）。
  - 実装案B: 参照アセンブリから classId をオンデマンド解決する **FIR symbol provider**（宣言生成拡張より強力）。総称も含め一経路化。
- [x] **宣言の一本化** ✅: 明示が要る場合のみ `<DotKtImport>`（escape hatch、注入経路）。`<KotlinClrType>` は撤廃しサンプルから削除（import scan で代替）。`<KotlinClrFacade>` は内部実装（auto-façade）に降格。**(M)**
- [x] 検証: BCL（`ktproj-import`/`ktproj-inject` = import のみ）・外部アセンブリ（`ktproj-extlib` ProjectReference・`ktproj-avalonia` PackageReference、いずれも import のみ）を verify-all で常設。
- [x] **Forward `<Reference>`（Kotlin → .NET）= 実装済 ✅**: `.ktproj` の `<Reference>` / `<PackageReference>` / `<ProjectReference>` で参照した .NET アセンブリの型は、そのまま `import` で Kotlin から使える（`msbuild/KotlinClr.targets` の `ResolveReferences` → @(ReferencePath) → facadegen `--refs`）。**※逆向き（.NET が Kotlin の IL アセンブリをコンパイル時 `<Reference>`）は別物で未実装＝R-1（下記 R セクション）。** 「`<Reference>` は実装済み？」は forward の話なら Yes、reverse なら R-1。

---

# D. coroutine 完全意味論

> **状態（2026-06-23）: コルーチン表面はコンパイラ機能として全面実装済み**（design-coroutines-clr.md §§13a–§14a / task #55 dotktx 基盤）。
> 単発 suspend・spilling・条件式内 suspend・try-catch/try-finally-around-await・suspend lambda（receiver 形含む）・
> generic/Unit/extension suspend・raw intrinsics・resume・startCoroutine・suspendCancellableCoroutine・unified Result・
> user `Continuation<T>` 実装・`Unit` 型引数・sequence/yieldAll/generateSequence・**Flow（generic 含む）+ Flow⇄
> IAsyncEnumerable・Channel・select・CoroutineContext 代数+coroutineContext・ContinuationInterceptor/intercepted+
> dispatcher** をすべて standalone（合成 facade）で実証済（各 `il-k*` サンプル、緑・ilverify-clean）。
> **下記の D 残項目（CancellationToken・構造化並行・Dispatchers）の「本物のライブラリ形」は Track 2**＝実 `kotlinx-coroutines-core`
> を DotKt でコンパイルする段階で揃う（現状の手書き stopgap はそこで置換）。

- [x] 部分式内サスペンドの spilling（`f(g().await())`）。**(L)** ✅ 2026-06-20 — `spillExpr`（BirEmitter）が式中の各サスペンド呼出を**評価順（post-order）**で fresh な状態機械フィールド＋`coSuspend` ステップに hoist し、残余式を `expr()`（`coSpill` を参照）で再レンダ＝サスペンドフリー化。`a.await() + b.await()`（第1結果が第2サスペンドを跨いで生存＝両方フィールド）・val 初期化子・非 suspend 関数への await 引数を解禁。`val x = …`/`return …`/`x = …`/呼出文の4位置を配線。`samples/il-coro`（`spillSum=30`/`spillNested=17`/`spillArg=16`）実機正＋ilverify-clean。残: **条件式**内 suspend（下記）。
- [x] ループ/分岐の**条件式**内サスペンド。**(M)** ✅ 2026-06-20 — `emitWhileCps` は条件式の await を **START ラベル直後**に spill（後退辺 `coGoto start` で毎反復 re-suspend＝ループ body サスペンドと同型）、`emitWhenCps` は各 branch 条件を test 直前に spill。`spillExpr` 再利用でゼロ新規 ilemit。`samples/il-coro`（`loopCond=3`＝while 条件 await、`condBranch=6`＝if 条件 await＋branch await）実機正＋ilverify-clean。
- [ ] `CancellationToken` を ABI に。**(S)**
- [x] `Flow` ⇄ `IAsyncEnumerable` ✅ 2026-06-23（`il-kasflow`：`asFlow`/`asAsyncEnumerable` 橋＝GFlows.FromAsync/ToAsync）。**(L)**
- [~] 構造化並行性（`Job`/`CoroutineScope`/`launch`/`async`）。**(XL)** — async/await/runBlocking はコンパイラ機能として実装済（`il-kstruct`）。本物の Job/Scope/cancel = Track 2（kotlinx をコンパイル）。
- [~] `Dispatchers`（Default→ThreadPool / Main→SynchronizationContext）。**(L)** — `ContinuationInterceptor`/`intercepted` の継ぎ目＋合成 dispatcher は実装済（`il-kintercept`、T3c）。本物の Dispatchers.* は Track 2 の actual セット（同じ継ぎ目に差す）。

---

# E. IL バックエンド parity ＆ C# コード生成の廃止（1.0 の中核ゴール）

**最終形**: 唯一の出荷バックエンドが**直接 IL**（`BirEmitter.kt` → `ilemit`）。C# 生成（`CSharpCodegen.kt` → csc）は 1.0 で出荷経路から外す。

> **★ 廃止までの残タスクと設計は `docs/csharp-retirement-design.md` に集約（2026-06-18）。** 真の C# 独占 gap は **①コルーチン/suspend（XL・最大）②イベント `+=`/`-=`（M・windowing 完全 IL 化）③generic .NET メソッド/indexer（S）④逆 interop 公開面（S-M）⑤PDB（M・任意）**＋検証カバレッジ（pure corpus を IL 経路で JVM 差分＝E-2）。scope/LINQ/stdlib/local-fn 等は IL に既存（誤報補正済）。runtime 戦略は **B（CLR-native IAsyncStateMachine）に確定・実装完了**（2026-06-18〜）。コルーチン表面はその後 dotktx 向けに全面実装済（startCoroutine/suspendCancellableCoroutine/Result-Unit/user Continuation/sequence-yieldAll-generateSequence/Flow-Channel-select/CoroutineContext-intercepted） — design-coroutines-clr.md §§13a–§14a。

## C# 生成が今こっそり兼ねる3つの役割と、その置き換え
C# 生成は単なる出力フォーマットではなく、以下3役を兼ねている。各々を別物で置換してはじめて「捨てられる」。
1. **出荷 emit** → IL parity（下記 E-1）。
2. **正解器（differential oracle）** → **JVM 差分ハーネス（F）＋ `ilverify`** へ置換（下記 E-2）。`verify-il.sh` の「IL==C#」比較は移行期のみ。
3. **意味解決器**（オーバーロード解決 / target typing / 暗黙変換 / async・generic lowering）→ 大半は **FIR が IR の時点で解決済み**。残るのは「ラムダ→デリゲートの対象型確定」「async/総称の lowering を IL で明示生成」。これを `ilemit`/lowering 側で引き取る（下記 E-3）。

## 移行フェーズ（C# は「踏み台かつ命綱」として段階的に外す）
- [x] **E-0 方針確定（ユーザ確定 2026-06-17 — IL を主軸へ）**: 「最終形＝純 IL、C# は parity と JVM オラクル達成まで dev-only に降格 → 1.0 で出荷経路から除去」を固定。**転換契機**: Kotlin 構文/意味論が C# に完全射影できない*non-projecting tail*（`Unit` 値・`reified`/`inline`・inline ラムダの非ローカル return・`value class`・coroutine 完全意味論・宣言箇所変性）の存在を確認。射影できる breadth は C# 経路でほぼ採取済み。**最大リスク＝出荷バックエンド（IL）が薄い**（IL 8 サンプル vs C# 50）ため、開発主軸を *いま* IL へ移す（後ろ倒しの再タイミング、方向は既定路線）。**進め方**: ① E-1 IL parity で既存 corpus を IL で緑＋`ilverify` clean、② tail は最初から IL 実装、③ C# は射影可能構文の差分オラクルとして温存（E-5 で除去）。JVM 差分ハーネスは C# 非依存なのでバックエンド入替を生き残る。— [[il-primary-backend-pivot]] **(S, design)** ✅
- [x] **E-0.5 IR 階層設計 — ✅ 不要と判明（superseded）**: 当初「CFG/SSA が要る」とした3つは**すべて別手段で実装済み**＝(1) labeled break / `break@outer` → loop-label stack + goto（`il-loopjump`）、(2) **非ローカル return** → inline fun ボディ＋ラムダの splice（literal ラムダ引数のみが非ローカル return 可で、それは inline されるので `return` が囲み関数の return になる。`il-inline2` `findFirstEven()`=4）、(3) **可変キャプチャ** → ref-cell 昇格（`il-refcell`／inline 経路は `il-inline2` `sum()`=3）。よって本格 CFG/SSA パスは現 corpus に不要。元の設計メモ（AST→CFG→SSA）: lowering は BirEmitter（IR→BIR）へ集約し ilemit は薄く保つ（[[lowering-lives-in-bir]]）。**構造化 AST のまま IL を吐き続けない**——AST→IL は形状ごとの特別扱いで高コスト＆バグ温床（実証: do-while 空ボディ無限ループ＋IrComposite/単文ボディの個別対応）。**三層に分離**: (1) 高レベル BIR（式 lowering の置き場）→ (2) **CFG ブロックIR**（基本ブロック＋明示分岐＝IL emit の本来基盤。制御フロー平坦化を一度だけ行い do-while/`break@outer`/非ローカル return を全部分岐化）→ (3) emit。**SSA は CFG の上に coroutine 着手時に追加**（dataflow: spilling/closure/最適化）。**導入時期**: CFG ブロックIR は**制御フロー breadth（labeled break・非ローカル return）着手の直前**に据える（最も苦しく最も配当が出る地点、`m-a2` の `break@outer` がトリガ候補）。式 breadth（collection/scope/拡張関数）は CFG 不要で並行可。— [[il-primary-backend-pivot]] **(L, design)**
  - [x] 着手済み増分①: do-while / bitwise・shift（and/or/xor/shl/shr/ushr/inv）/ 数値変換（toInt/toLong/…→CIL conv）を BIR+ilemit へ移植。`samples/il-ops`。**(S)** ✅
  - [x] 着手済み増分②: `kotlin.math.*`→`System.Math.*` を **BirEmitter で clrStatic に lower**（ilemit 変更なし＝薄いバックエンド原則の実証）。`samples/il-math`。**(S)** ✅
  - [x] 着手済み増分③: `kotlin.text` String ops→`System.String` instance（clrInstance）、`"42".toInt()`→`Int32.Parse`、Char 述語→`System.Char.*`（clrStatic）。すべて BirEmitter lowering のみ・ilemit 無変更。`samples/il-str`,`il-cp`。**(S)** ✅
  - [x] 着手済み増分④: ユーザ拡張関数 `fun T.f()`→`__self` 第1引数の static メソッド（BirEmitter で method 署名＋呼び出しを lower、ilemit 無変更）。`samples/il-ext`。**(S)** ✅
  - [x] 着手済み増分⑤: 配列（`intArrayOf`/`arrayOf` factory、indexing get/set、`.size`、indexed＋`for (x in a)` iteration）。BIR に newArray/arrayGet/arraySet/arrayLen/forArray ノード＋`array:<elem>` 型、ilemit に newarr/ldelem/stelem/ldlen プリミティブ＋indexed forArray 追加。`samples/il-arr`。**(S)** ✅
  - [x] 着手済み増分⑥: **lambda→delegate（非キャプチャ）**＝最大レバー。**BirEmitter で lambda-lifting**（非キャプチャ lambda→名前付き static メソッド）、`kotlin.FunctionN`→`func:<ret>:<args>` 型、`f(x)`→delegateInvoke。ilemit は薄いプリミティブ2つ（ldftn＋delegate ctor / Invoke）＋`func:`→`System.Func`/`Action` 解決。`samples/il-lambda`。**(M)** ✅（非キャプチャ）
  - [x] 着手済み増分⑦: **キャプチャ lambda（closure）**。BirEmitter で**自由変数解析**（IrVisitorVoid）→ closure クラス（捕捉フィールド＋ctor＋インスタンス `invoke`）を BIR types に合成、捕捉参照は `this.field` へ rewrite。呼び出し点 `closureNew`＝`new Closure(captures)`＋インスタンスメソッドから delegate（ldftn＋target）。**closure 型を JSON に出すので ilemit は通常パスで処理＝薄いまま**（プリミティブ1つ追加）。`makeAdder` 等が動作。`samples/il-closure`。**(M)** ✅
  - [x] 着手済み増分⑧: **スコープ関数 let/run/with/apply/also** を **inline 化**（delegate 不要、C# の IIFE 写像に対応）。receiver を一意ローカルに束縛、`it`/`this` を rewrite、let/run/with は最終式を、apply/also は receiver を yield。ilemit は `valueBlock` プリミティブ1つ追加。`samples/il-scope`。**(M)** ✅
  - [x] 着手済み増分⑨: **collection ops（LINQ generics）**＝ジェネリック解決基盤。ilemit に3プリミティブ：`clrg:`（generic 型構築 MakeGenericType）、`clrGenericStatic`（**ジェネリックメソッド解決＝オーバーロード選択は delegate arity 最小を優先＝非 indexed**＋MakeGenericMethod）、`listNew`。`map`/`filter`/`take`/`drop`/`reversed`/`distinct`/`toList`/`count`/`any`/`none`/`all`/`first`/`last`/`contains` を Select/Where/Take/Skip/Reverse/Distinct/Count/Any/All/First/Last/Contains へ写像（**extension ops＝拡張レシーバ、member ops（contains）＝dispatch レシーバの両方を型ベースで検出**）。`.size`/`listOf` も。**型引数は FIR 解決済みの `call.typeArguments` を読む**（推論ヒューリスティック排除＝ユーザ指摘②「FIR で generics は解決済み」を反映、member ops は非 generic なのでレシーバ要素型へ fallback）。`samples/il-coll`。IL **18 サンプル** ALL PASS＋ilverify clean。
    - **修正**: .NET 10 が `Reverse<T>(T[])` 配列オーバーロードを追加したため、reflection オーバーロード選択が array 版を誤選択（List→int[] で ilverify 失敗）。**array パラメータを持つオーバーロードを後回し**（canonical IEnumerable 形を優先）に修正。ユーザ指摘①②の「FIR 済みを再解決するな」の実例＝reflection 再解決の脆さ。
  - [x] 着手済み増分⑩: `fold`→`Enumerable.Aggregate<T,R>`、`joinToString(sep?)`→`String.Join<T>`（型引数は FIR から）。`samples/il-coll2`。
  - [x] **オーバーロード解決を決定的に（ユーザ指摘）**: Kotlin op は名前で一意（map/mapIndexed…）＝Kotlin 側に overload 曖昧性なし。overload は写像先 .NET 側のみ、かつ**どの .NET メソッドを呼ぶか写像時に確定している**。よって reflection ヒューリスティック（array後回し/値型優先/delegate arity）を**撤廃**し、`clrGenericStatic` に**狙ったオーバーロードのパラメータ形 `shapes`（ienum/func:N/string/gp/int）を明示**＝ilemit が完全一致で決定的選択。`Reverse(T[])`・`Join(char)` の whack-a-mole が構造的に消滅。**(S)** ✅
  - [x] 着手済み増分⑪: **forEach を inline 化**（enumerator ループに body を splice、`it` を一意ループ変数へ束縛）。**closure を使わず enclosing ローカルを直接 read/write**＝Kotlin の `inline fun` 同等＝**可変キャプチャ問題を回避**（closure は読み取り専用キャプチャのみ対応のため）。ilemit に `forEachInline`（IEnumerator<T> 反復）＝**.NET 任意 IEnumerable の for-in も解禁**（C-1 項目）。`samples/il-coll3`。IL **20 サンプル**。
  - [x] 着手済み増分⑫: **Pair/Triple → System.ValueTuple**。`a to b`→`tupleNew`（newobj で値型構築）、`.first`/`.second`/`.third`＆`componentN()`（分解宣言）→`tupleItem`（ItemN フィールド ldfld、値型 field アクセスも ilverify clean）。`birType` で Pair/Triple→`clrg:System.ValueTuple:…`。`samples/il-pair`。IL **21 サンプル**。
  - [x] 着手済み増分⑬: **nullable 参照型** — elvis `?:`・safe-call `?.`・`!!`（CHECK_NOT_NULL→値そのまま）・`String.length`→`System.String.Length`。elvis/safe-call は既存 cond 経路で動作（参照型は null 比較が Ceq でそのまま）。`samples/il-null`。IL **22 サンプル**。残: **nullable 値型 `Int?`**（Nullable<int>＝`s?.length` 等で int と null が混在＝値型 box が要る・複雑）。
  - [x] 着手済み増分⑭: **収束駆動の punch-list 修正**（既存 pure サンプルを IL で走らせて gap を実測）。(1) `noWhenBranchMatchedException`/`error`/`TODO`/`require`/`check` → throw（`throwExpr` primitive）、(2) **スマートキャスト**＝`is T`→`isinst`+null判定、`as T`/暗黙キャスト→`castclass`/`unbox.any`（従来は素通しで型不一致）、(3) **デフォルト引数を呼び出し点で補完**（IL に既定値機構なし）、(4) **arg 型追跡バグ修正**（ilemit が引数を常に int 扱い→参照型引数を誤 box＝concat で文字列が壊れる潜在バグ）、(5) `String.length`→`System.String.Length`。**m-a1 が IL で完全動作**（2/50/21/<def>/<hi>/2＋ilverify clean）。m-b1/m-b2 も既に動作。
  - [x] 着手済み増分⑮: LINQ 末端拡充（firstOrNull/lastOrNull/sum/sorted(Order)/maxOrNull/minOrNull/reduce(Aggregate)）＋ String 述語（isEmpty/isNotEmpty/isBlank/isNotBlank→Length/IsNullOrWhiteSpace）＋ String 添字 `s[i]`→get_Chars ＋ `coerceAtMost/atLeast/In`→Math.Min/Max/Clamp ＋ `repeat(n){}`→inline counter loop。
  - **収束計測（E-2 進捗）**: pure サンプル **11/25** が IL で完全動作＋ilverify clean（m0,m-a1,m-b1-5,m-b8,m-b12,m-b13,m-s1）。dedicated il-* 22 サンプルも全緑。残14は3カテゴリ: **①型emission gap**（data class/enum/for-in collection ＝`key 'Any'/'Color'/'Iterator'`）②未対応stmt/expr（companion/local fn）③stdlib末端（sumOf/groupBy/String.repeat）。
  - [x] 着手済み増分⑯: **data class** — `birType(Any)`→object 修正（従来 `@Any` で KeyNotFound）＋`objMethod` intrinsic（builtin レシーバの hashCode/equals/toString→System.Object 仮想、値型は box）。data class の toString/equals(instanceof+フィールド比較)/hashCode(フィールド GetHashCode)/componentN/copy が IL で動作。`m-a5`,`m-s2` OK＋ilverify clean。**収束 13/25**。
  - [x] 着手済み増分⑰: **companion object** — companion の非 const val→**static フィールド＋`.cctor`（静的初期化）**、メソッド→static、`Outer.member` を static 呼び出し/フィールドへルーティング（user-property field-access より先に）。ilemit に static field 宣言・型初期化子・`callStatic owner`・`staticField`/`staticFieldSet` 追加。`DeclareMethod` が JSON の `static` を尊重。`m-a6` OK＋ilverify clean。**収束 14/25**。
  - [x] 着手済み増分⑱: **for-in over collection**（`for (x in list)`→forEachInline=IEnumerator ループ、`forEachInline`/`repeatInline` を EmitStmt にも委譲）＝**.NET 任意 IEnumerable の for-in も解禁**。`m-s3` OK。**setOf→HashSet**（setNew、Set 型→`clrg:HashSet`、`.size`→Count）。`m-b6` OK。**収束 16/25**。
  - [x] 着手済み増分⑲: **map**（`mapOf`→Dictionary<K,V>=mapNew、`m[k]`→get_Item、`m[k]=v`→set_Item、`m.size`→get_Count）＋**`split`**→`String.Split(string[],None)`|>ToList。`m-b7` OK。**収束 17/25**。
  - [x] 着手済み増分⑳: **top-level プロパティ**（const inline、val/var→file-class static フィールド＋`.cctor`、アクセスを static field へルーティング）＋**vararg**（`IrVararg`→newArray、param は array 型）。`m-a4` OK。**収束 18/25（72%）**。
  - [x] 着手済み増分㉑: **enum を real .NET enum 化**（`DefineEnum`＋literals、`enumValue`＝ordinal定数 typed-as-enum、`when`/比較は ceq）。基本 enum（`il-enum`）は緑＋ilverify clean を維持。`c.name`→ToString/`c.ordinal`→conv.i4/`values()`/`valueOf`→`Enum.GetValues(Type)`/`Enum.Parse(Type,string)`（ldtoken 経由）も実装。
    - **⚠ enum rich API(m-a8) は tooling 壁**: `box`/`castclass`/`ldtoken` で **EnumBuilder の型トークンが PersistedAssemblyBuilder で正しく bake されず BadImageFormatException**。基本 enum は動くが rich API は要調査（既知の Reflection.Emit/EnumBuilder 限界）。
  - [x] 着手済み増分㉒: **制御フロー breadth ＝ CFG 不要と判明**。**labeled break/continue（`break@outer`）はループラベルスタック＋goto で実現**（loop毎に (label,cont,brk) を push、break/continue は一致ラベル or 最内へ br）＝**フル CFG ブロックIR は不要**。範囲メンバ `n in a..b`→`(x>=a && x<op b)`、`i++`/`i--`→inc/dec＝±1、coercion-to-Unit ブロックを文展開（`<unary>` temp の副作用保持）。`m-a2` 完全動作（low/mid/high/do-while/break at 1,3/sum=15）＋ilverify clean。**収束 19/25**。
    - **CFG 設計ゲートの再評価**: break@outer は goto で済むと実証。真に CFG/dataflow を要するのは **非ローカル return（inline ラムダから外側関数）＋可変キャプチャ Ref** のみ（現 corpus には非ローカル return なし）。
  - [x] 着手済み増分㉓: **local function**（capture 含む）。file-class static へ lift、**捕捉変数を先頭パラメータ化**（local fn は名前で直接呼ばれるので呼び出し点で捕捉値を prepend＝closure クラス不要）。非捕捉(square/addSquares)＋捕捉(bump captures base)が動作。`m-a7` OK。**収束 20/25（80%）**。
  - [x] 着手済み増分㉔: **String.repeat/reversed**、**sumOf**（selector 戻り型でオーバーロード選択）/**maxByOrNull/minByOrNull**（MaxBy/MinBy）、**zip**（Zip→ValueTuple-list）/**List 添字**（get_Item）/**Char.code**（→int）/**char 範囲**、**generic 型を bracket 符号化** `clrg:Open[a,b]`（ネスト generic 対応＝List<ValueTuple>/Dictionary<K,List>）、**groupBy/associateWith/associateBy**（dict-building ループノード＝selector を回して Dictionary 構築、synthetic lambda 不要）。`m-b11`/`m-b9`/`m-a3`/`m-b10` OK。**収束 24/25（96%）**。
  - [x] 着手済み増分㉕（**完遂**）: enum rich API(m-a8)＝**`EnumBuilder` を前倒し bake** して box/castclass が created-type トークンを参照するよう修正（token 壁を解消）。`.name`→ToString/`.ordinal`→conv.i4/`values()`/`entries`→`Enum.GetValues(Type)`+castclass/`valueOf`→`Enum.Parse(Type)`。**収束 25/25（全 pure サンプルが直接 IL で実行）、24/25 ilverify-clean**。
  - [x] 着手済み増分㉖（**完遂＝最後の gap**）: **nullable 値型 `Int?` → `System.Nullable<T>`**（Codex 相談）。`birType` が値型 nullable を `nullable:T` に写像、`blockExpr` が SAFE_CALL を `cond(a==null, nullableNull, nullableWrap(member))`・ELVIS を `valueBlock{ var nv; cond(nv.HasValue, nv.Value, fallback) }` に降ろす。ilemit 側: `MapType("nullable:T")`→`Nullable<T>`、新ノード `nullableNull`（`initobj`＝ldnull 不可）/`nullableWrap`（`newobj Nullable<T>(T)`）/`nullableHasValue`・`nullableValue`（**アドレス経由 `ldloca`＋`call get_HasValue/get_Value`**、`callvirt` 不可）。落とし穴: ELVIS は `when{tmp==null→fallback; else→tmp}`＝**fallback は branches[0]**。`samples/m-s1` 実行正＋**ilverify Verified.**。
  - **FIR→BIR→IL 完成（100%）**: 全 25 pure サンプル＋23 dedicated（m-s1 を nullv として常設）が直接 CIL で実行、**25/25 ilverify-clean**（実用言語＋stdlib サーフェス網羅）。現 corpus 外の deferred: 可変キャプチャ Ref・非ローカル return（要 full CFG）。: stdlib inline 本体は **IR に来ない**（`hasBody=false` 実測）＝(a)「そのまま読み替え」は本体 source（XL）が必要＋JVM 形で .NET 再写像も要る。よって **(b) 高レベル写像を継続**が .NET ターゲットでは合理的（(a) は ArrayList/add/iterator 等の非 inline コアにも底打ちし、結局 .NET 再写像が要る）。— [[design-first-on-hard-features]] **(L)** ✅（主要 LINQ）
- [~] **E-1 IL feature parity**: 以下を BIR/`ilemit` へ移植し、各々 C# 経路と差分一致＋`ilverify` clean。**(XL 合計)** — 真の C# 独占 gap（events/generic/coroutines）はすべて完了（2026-06-18、`docs/csharp-retirement-design.md` フェーズ1-3）。
  - [x] **coroutine 状態機械（IL, 戦略B＝CLR-native IAsyncStateMachine, 2026-06-18）** ✅ — `suspend fun`→`Task<T>` kickoff＋struct `IAsyncStateMachine`。BirEmitter が CPS linearize（`coSuspend`/`coLabel`/`coGoto`/`coCondGoto`/`coReturn`、live-local→field）、ilemit が `EmitCoroutine`（`AsyncTaskMethodBuilder<T>` プロトコル＋`beq` dispatch＋cpsField リダイレクト）。`suspend ()->T`⇔`Func<Task<T>>`、`--ref` で外部ランタイム。`samples/il-coro`（線形/param/直接呼出/ループ/分岐 suspension）実機正＋ilverify-clean。**+spilling（2026-06-20）**: `spillExpr` が部分式内サスペンドを評価順で fresh フィールド＋`coSuspend` に hoist→残余式を `coSpill` 経由で再レンダ（`f(a.await())`/`a.await()+b.await()`/val 初期化子/代入を解禁）。**+条件式内 suspend（2026-06-20）**: `emitWhileCps`/`emitWhenCps` が条件式の await を test 直前（while は START ラベル直後＝毎反復 re-suspend）に spill（`loopCond`/`condBranch`）。**+try/catch-around-await（2026-06-20, D capstone）**: `emitTryCps` が `coTryBegin`/`coCatchBegin`/`coTryEnd` マーカーを発行、ilemit が **二段ディスパッチ**（外側＝in-try 状態を try 入口の landing pad へ／内側＝try 内で resume へ。protected region へは外から分岐不可なので毎 MoveNext で try を再 enter）＋**単一出口 MoveNext**（try 内の suspension/return は `ret` 不可→`leave _coExit`）で `.try`/catch を emit。`tryOk=11`（happy）/`tryCatch=-99`（faulted task→GetResult が try 内で throw→catch）/`tryFallthrough=8`（try/catch とも return せず後続へ）実機正＋ilverify-clean。**+try/finally-around-await（2026-06-23, §13v）**: `emitTryCps`＋`EmitCoTryEnd`（struct/class 両形共有）が finally を CLR finally 句でなく**正常路＋合成 `catch(Exception){ finally; rethrow }`**に emit（suspend は `.try` を leave するので finally 句だと毎サスペンドで誤発火する）。`il-kfinally`（cleanup/15、try 内に 2 サスペンド）。残（catch 内 suspend・catch+finally 併用・try 内 return）のみクリーンエラー。[[coroutine-il]]
  - [x] **.NET イベント `+=`/`-=`（IL, 2026-06-18）** ✅ `samples/il-event`。**generic .NET メソッド/indexer（IL, 2026-06-18）** ✅ `samples/il-netgen3`。
  - [~] **ユーザ定義ジェネリクス（generic TypeBuilder）— G-1/G-2 完了** ✅（設計検証＝Codex/設計エージェントで .NET 10 Reflection.Emit の落とし穴を事前確認、[[design-first-on-hard-features]]・`docs/design-il-generics.md`）。**G-1**: generic class `Box<T>`（型パラメータをフィールド/ctor/メソッド戻り値で使用）＋ top-level generic fun `fun <T> id(x:T):T`。**G-2**: generic interface `Container<T>` ＋ それを構築済みインスタンス化で実装する class（`IntBox : Container<Int>`）。**実装**: BIR が型/メソッド宣言に `typeParams`、型パラメータ参照を `gp:T`、構築済みユーザ generic を `@Box[int]`/ownerType `Box[int]`、generic method 呼出に `typeArgs` を載せる。ilemit が Pass1 で `DefineGenericParameters`（SetParent/AddInterface より前＝順序が load-bearing）、generic method は段階的 define（`DefineGenericParameters`→`SetParameters`/`SetReturnType`）、構築済みジェネリックのメンバは**静的ヘルパ `TypeBuilder.GetMethod/GetField/GetConstructor`**で解決（`MakeGenericType` 結果の `.GetMethod` は persisted builder で `NotSupportedException`）、generic interface 実装は構築済み iface を `AddInterfaceImplementation`＋`DefineMethodOverride(impl, TypeBuilder.GetMethod(constructedIface, openMethod))`、`Ordered()` は generic iface 定義を実装者より先に bake。**戻り型は un-baked builder の `!0`/`!!0` を反射せず BIR が運ぶ concrete `retType` を優先**（builder トークン早期参照の回避＝設計エージェントの警告）。`samples/il-generic`（class+fun、`42/42/hello/7/world/3/three`）・`il-generic2`（interface、`99/…/tag/…`）実機正＋**ilverify clean**（generic フィールド load/store＝.NET 10 で修正された silent-corruption shape を含めて検証）。
  - [x] **ユーザ定義ジェネリクスの機構を完遂（G-3〜G-6, 2026-06-18）** ✅ — 純粋な generics 機構（言語側）はこれで全部。**G-3 境界型パラメータ** `<T : Comparable<T>>`（`samples/il-generic3` `7/banana/10`）: `kotlin.Comparable<T>`→`System.IComparable<T>`、型/メソッド型パラメータに `SetInterfaceConstraints`/`SetBaseTypeConstraint`、`a.compareTo(b)`→**`constrained.` callvirt**（受け手を managed pointer で＝新 `EmitAddr`＝local/arg→ldloca・arg→ldarga・field→ldflda・他は temp に spill；BCL generic interface のメソッドは `TypeBuilder.GetMethod` で解決）。BIR `typeParams` は無制約=`"T"`／制約付き=`{"name":"T","constraints":[…]}`。**G-4 generic-on-generic メソッド** `class Holder<T>{ fun <R> … }`（`il-generic4` `42/42 & hi/42 & 99/x`）: `TypeBuilder.GetMethod(構築型, generic-method-def).MakeGenericMethod` が機能。**G-5 generic indexer**（`il-generic5` `10/20/99/z`）＝operator get/set は構築型上の通常メソッドなので追加コードゼロ。**G-6 宣言箇所変性** `out`/`in`（`il-generic6` covariance+contravariance）→ CLR の Covariant/Contravariant（**interface のみ＝CLR の規則**、class では Kotlin 内検査のみで drop）。BIR `typeParams` に `variance`。**重要な意味論差**: 値型引数の変性（`Source<Int>`→`Source<Any>`）は CLR では**成立しない**（reified generics で別型＝C# と同じ）。これは JVM のボクシング変性を CLR では再現しない方の [[clr-not-jvm-discard-jvmisms]]＝変性は参照型のみ。**併せて修正**: ジェネリック param 値は `NeedsBoxToRef`（値型 OR generic param）で常に box（concat/console/引数）、ただし `isinstRef`（`x as? T`）の結果は ref を返す（再 box 防止）。全 48 IL サンプル PASS+ilverify-clean。
  - [x] **.NET 基底継承の IL 化（非ジェネリック）＋ .NET virtual override（2026-06-18）** ✅ — `class App : Application()` を純 IL で点けるための中核。`samples/il-netbase`（`class AppError : Exception("app error")`＝基底 ctor 呼出・SetParent・継承 .NET メンバ `.Message` アクセス、`app error/7`）・`il-netbase2`（`override val Message`＝.NET virtual property のオーバーライドを **.NET 基底型 param 経由で多態 dispatch**、`AppError #7/#21`）実機正＋ilverify-clean。**実装**: (1) **IL backend が ClrTypeRegistry を参照**（`clrName` に S5 registry fallback を追加＝従来 IL backend は @Clr façade のみで FIR 注入型が見えず user 型として漏れていた；これで `clrgen.Exception` が `System.Exception` に解決し、注入型は emission から除外）。(2) typeDef `base` を .NET 基底なら `clr:`/`clrg:` spec で出す。(3) ilemit `TypeInfo.ClrBase`＝Pass2 SetParent を reflection 解決、base ctor は arg 数一致で選択（constructed generic 基底は `TypeBuilder.GetConstructor`）。(4) 継承 .NET メンバ：注入型はプロパティを field としてモデル化するので `IrGetField`/`IrSetField`＋property-getter call を **.NET 所有なら `clrPropGet`/`clrPropSet` に re-route**、継承メンバは fake-override なので `resolveFakeOverride` で実 .NET 宣言型を特定。(5) **.NET virtual override**：`override val Message` を `get_Message` メソッドとして emit（`clrAccessorMethod`）、ilemit が Virtual+slot 再利用＋`DefineMethodOverride(mb, baseT.GetMethod("get_Message"))`。FindMethod/FindField の base 鎖は `_types.ContainsKey` でガード。**残**: **generic .NET 基底**（`class C : Collection<Int>()`）は IL emission 側は実装済（`TypeBuilder.GetConstructor` 分岐）だが、**フロントエンドの generic .NET 型 FIR 注入**（下記 C トラック `:128-129`）が基底型解決に必要＝そこで解禁。イベント `+=`/`-=` の IL 化も別途。
  - [x] **generic .NET 型 FIR 注入（2026-06-18）** ✅（C トラック `:128`）＝任意の .NET generic 型を façade 無しで `import`／構築／基底に。これで **generic .NET 基底継承が end-to-end で完了**（`samples/il-netgen2`）。残: イベント `+=`/`-=` の IL 化、generic .NET メソッド/indexer。
  - [ ] A/B（言語 breadth・stdlib）で増える構文の IL 化（C# で先行実装したものを追従）。
- [x] **E-2 オラクル切替（2026-06-18）** ✅ — `verify-differential.sh` の clr 側を **BIR→ilemit（出荷 IL 経路）**に付け替え、pure corpus 25 サンプルが kotlin/jvm と ALL MATCH。`ilverify` は全 IL アセンブリ（coro 含む）に常設。**正解器としての C# は不要**になった。**CI 化済**＝`.github/workflows/verify.yml`（verify-il + verify-differential + verify-all を push/PR で）。
- [ ] **E-3 意味解決の引き取り**: ラムダ→デリゲート対象型の確定、async/総称 lowering の IL emit、暗黙数値変換・box 境界。FIR が渡す解決情報を BIR に載せ、`ilemit` で直接 emit。**(L)**
- ~~**E-4 デバッグ情報（PDB）**~~ — C# 脱却トラックから除外（2026-06-18）。出荷ブロッカーでなく、IL 経路は元々 csc から PDB を得ていない＝脱却と独立。デバッガ体験の別タスク。
- [x] **E-5 C# 経路の出荷除去（2026-06-18）** ✅ — `ClrBackendPhase` は既定で **BIR のみ**出力（C# は `KOTLIN_CLR_EMIT_CS=1` で opt-in、CSharpCodegen を呼ばない）。MSBuild `<KotlinClrBackend>` 既定 `il`（il プレースホルダが csc 用に `StartupObject` 上書き）。`samples/ktproj`（StartupObject 付き）が既定 il で実機正＝**出荷経路に Kotlin 由来 .cs/csc 依存ゼロ**。`verify-all.sh` は C# をオラクルとして明示 `KotlinClrBackend=cs` で継続。`CSharpCodegen.kt` は repo 温存。逆 interop（.NET ホストが IL 出力アセンブリを **reflection 消費**）も `samples/il-revinterop` で実証。コンパイル時 `<Reference>` 消費（5.2）は下記 R-1 に 1.0 出荷タスクとして積み直し。

## 完了判定（このトラックの Done ＝ C# を捨てられた状態）✅ 2026-06-18 達成
- [x] 全サンプルが **IL 単独**で `dotnet build/run` 成功、`ilverify` clean、JVM 差分一致（verify-il 56 PASS / 55 VERIFY、verify-differential 25 MATCH）。
- [x] C# 生成は無効化してもビルドが通る（既定 BIR のみ・出荷経路に C#/csc 依存ゼロ、`samples/ktproj` 既定 il で緑）。
- **→ E トラック完了。** 詳細・各フェーズの設計は `docs/csharp-retirement-design.md`。残る磨き（5.2 コンパイル時 `<Reference>`）は下記 **R-1** に 1.0 出荷タスクとして移管。

## R. 逆 interop の磨き（1.0 出荷タスク・脱却ブロッカーではない）
> **⚠ `<Reference>` は方向で状況が真逆（混同注意）**:
> - **Forward（Kotlin → .NET）＝実装済 ✅**: `.ktproj` の `<Reference>`/`<PackageReference>`/`<ProjectReference>` で .NET アセンブリを参照すると、その型が Kotlin から使える（`ResolveReferences` が @(ReferencePath) を埋め、facadegen `--refs` が型を inject／C-2 import 駆動解決。`msbuild/KotlinClr.targets`、`samples/ktproj-ref`）。下記 C-1 参照。
> - **Reverse（.NET → Kotlin）＝R-1 で未実装 ❌**: C# 等が「Kotlin が emit した IL アセンブリ」を**コンパイル時 `<Reference>`** することは未。下記が R-1。
- [ ] **R-1 コンパイル時 `<Reference>` retargeting（M-L、REVERSE 方向 .NET→Kotlin のみ）**: C# 等が IL 出力アセンブリを**コンパイル時 `<Reference>`** で消費できるようにする（現状の reverse 経路は reflection-load のみ可＝`samples/il-revinterop`、または生成 C# ソースを `<Compile>`＝`samples/revinterop`）。
  - **根本原因**: ilemit は BCL を runtime reflection 型で解決するため、出力では **CoreLib の全型（Object/String/`List`/`Dictionary`/`Task`…）が単一の `System.Private.CoreLib` AssemblyRef を共有**。コンパイル時参照には型ごとに正しいコントラクトアセンブリ（Object/String/Task→System.Runtime、`List`/`Dictionary`→System.Collections、LINQ→System.Linq…）へ**per-ref 分離**が必要。
  - **不可と実証済の2案**（2026-06-18）: ① **MetadataLoadContext 型**で emit → MLC のジェネリック型/メソッドに**ユーザ TypeBuilder 型引数**を渡すと "not loaded by the MLC" 例外（lambda→`Func<UserT>`・closure・`List<UserT>`・コルーチン `Start<SM>` が全滅）。② **単一 AssemblyRef の PE in-place 書換**で CoreLib→System.Runtime → `Object`/`String` は通るが `List<T>` が `TypeLoadException`（System.Runtime は List を forward しない）。両方撤回。
  - **残る実装案**: 出力 PE の**メタデータ全面再構築**で TypeRef ごとの ResolutionScope を正しいコントラクト AssemblyRef に振り分け（型→コントラクトの対応は ref パックを引いて決定。Reflection.Emit を介さない純メタデータ変換なので TypeBuilder 制約を回避）。あるいは Reflection.Emit の参照アセンブリ対応 API を待つ。**(M-L, 出荷の磨き)**
  - 併せて **nullability 注釈 `[Nullable]`** の出力（任意）。
  - **受入**: C# プロジェクトが IL 出力アセンブリを `<Reference>` してコンパイル＋実行でき、`List`/`Dictionary`/コルーチンを使うアセンブリでも壊れない（全 IL サンプル緑を維持）。

## リスク / 緩和
- IL の stack 型規律・例外領域・generics 具体化・box 境界が難所。**緩和**: 移行期は C# 差分でバグを即検知、parity 後は JVM オラクル＋`ilverify` の二重ゲート。C# 経路は削除せず「壊れた時の参照実装」として温存可（出荷からは外すが repo には残す選択肢）。

---

# F. production ツーリング / 信頼性

- [x] **差分テストハーネス（JVM oracle）— 達成・実機 ✅**: `scripts/verify-differential.sh` が pure-Kotlin サンプル（16件）を kotlin/jvm（正解器）と kotlin/clr で実行し stdout 一致を検証。**ALL MATCH**。codegen＋stdlib 写像が実 Kotlin 意味論と一致することを実証。
  - **設計判断（ユーザ確定 2026-06-17）— primitive 文字列化は CLR ネイティブ**: Kotlin.NET プログラムは .NET プログラムなので、Boolean→`True`/`False`、Double→`4` 等、**ホスト（CLR）の慣習に揃える**（相互運用・逆方向 interop に一貫）。Kotlin の `true`/`4.0` は JVM/JS の慣習継承で言語の本質ではない、との判断。差分ハーネスは表記差（bool 大小・`.0`）を正規化してロジック一致を検証。
  - 残: corpus 拡張（pure サンプルを増やす）、CI 化。
- [ ] **診断品質**: 未対応構文・interop エラーをソース位置付き・読めるメッセージで。`-Xverify-ir` 相当の健全性ゲート常設。**(M)**
- [ ] 境界の null 正当性（プラットフォーム型 `T!` の扱い定義）。**(M)**
- [ ] 増分コンパイル。**(L)**
- [ ] 性能（コンパイル時間・生成コード）。**(L)**
- [~] 配布（基盤あり）: `dotnet new ktproj` テンプレート（`templates/` 存在）・MSBuild SDK / NuGet 化（`scripts/pack-dotkt.sh`＝DotKt.Sdk/Toolchain/Runtime/Templates をパック）は実装済。残: 相対パス依存の排除・self-contained コンパイラ・1.0 versioned release（現状 0.9.0 pre-1.0）。**(M–L)**
- [ ] VS / VS Code 体験（ビルド/実行統合。フル LSP は別スコープ）。**(M–L)**
- [x] CI ✅（`.github/workflows/verify.yml`＝verify-il + verify-differential + verify-all を push/PR で実行）。残: サンプル行列の継続拡張・ネット依存サンプル（Avalonia）のキャッシュ戦略。**(S–M)**
- [ ] **ライセンス / 帰属（出荷必須）**: 参考実装（`KotlinForCLR`、Apache-2.0）からの移植部分のライセンス遵守・NOTICE/帰属、kotlin-compiler-embeddable 等依存のライセンス確認、本体ライセンス確定。**(S)**
- [ ] **利用者ドキュメント**: README からの getting-started、`.ktproj` の書き方、.NET 型の取り込み方（C-2 で一本化した**単一の方法**を説明。使い分けは存在しない形に）、対応/非対応機能一覧。**(M)**
- [ ] **バージョン / サポート方針**: Kotlin 2.2.0 ピン留めの位置づけ、対応 .NET TFM、semver 方針を明文化。**(S)**

---

# 補足: 別プロダクト（コア外）
- Kotlin らしい UI DSL（Avalonia/WPF/WinUI ラッパ、lambda-with-receiver ベース）。**コアに入れない**。A-1 のスコープ関数/DSL 基盤が前提。— [[kotlin-net-is-pure-binding]]

---

## 進め方メモ
- **A と B は相互依存**（stdlib は拡張関数＋lambda-with-receiver で構成）。A-1（拡張関数・スコープ関数・配列・網羅 when・デフォルト引数）と B（コレクション/stdlib 写像）はセットで詰めると効率的。
- 横断で `verify-all.sh` 緑を維持、IL 追加分は C# 経路と差分一致を必須ゲート。
- 大物（B の方針、D の構造化並行性、C のジェネリック注入）は着手前に設計を `docs/` に固定。
