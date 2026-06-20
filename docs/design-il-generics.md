# 設計: ユーザ定義ジェネリクスの IL 化（generic TypeBuilder）

**状態: 純粋な generics 機構は完遂 ✅**（G-1 generic class/fun、G-2 generic interface、G-3 境界型パラメータ、G-4 generic-on-generic メソッド、G-5 generic indexer、G-6 宣言箇所変性）。`samples/il-generic{,2,3,4,5,6}` 実機正＋ilverify clean、全 48 IL サンプル緑。設計は設計検証エージェント（Codex 役）で .NET 10 Reflection.Emit の 8 論点を事前検証済み（下記「検証結果」）。**残るは interop インフラ依存のみ**: .NET 基底ジェネリック継承（.NET 基底継承の IL 化が前提・E-1）、generic .NET 型の FIR 直接注入（C トラック）。**学んだ追加の落とし穴**: (a) メソッド型パラメータ置換は un-baked builder の `DeclaringMethod`/`GenericParameterPosition` 反射ではなく**参照同一性**（gp builder→type arg）で行う、(b) ジェネリック param 値は `IsValueType` だけでなく `IsGenericParameter` でも box が要る（`NeedsBoxToRef`）が、`isinst` 結果（`x as? T`）は既に ref なので再 box しない、(c) **変性は参照型引数のみ**（CLR 規則、`Source<Int>`→`Source<Any>` は不可＝reified generics の帰結）。

## 検証結果（設計エージェント, .NET 10）
1. `DefineGenericParameters` は Pass1（`SetParent`/`AddInterface` より前）が**順序として load-bearing** ✅
2. メソッド型パラメータが型パラメータを shadow＝正しい ✅
3. generic method は段階 define（`DefineMethod(name,attrs)`→`DefineGenericParameters`→`SetParameters`/`SetReturnType`）✅
4. 構築済みユーザ generic のメンバは**静的 `TypeBuilder.GetMethod/GetField/GetConstructor` 必須**（`MakeGenericType` 結果の `.GetX` は persisted builder で `NotSupportedException`）。第2引数は**開いた定義の Builder** ✅
5. generic method 呼出は `MakeGenericMethod`（bake 前に emit 可）。**bake 前の `.MetadataToken` 参照は厳禁**（Builder オブジェクトで emit）✅
6. generic interface 実装は `AddInterfaceImplementation(constructed)`→`DefineMethodOverride(impl, TypeBuilder.GetMethod(constructedIface, openMethod))`（.NET 9 P5 で spurious validation 撤廃済）✅
7. **CreateType 順序ルール**: generic iface/base の open 定義を、それを構築して継承/実装する型より先に bake（`Ordered()` に反映）⚠採用
8. PersistedAssemblyBuilder 固有バグは .NET 10 GA で全 fix。**最重要＝generic フィールド load/store（`C<int>::F where F:T`）の silent PE 破損**が .NET 10 で修正済＝この shape を ilverify で重点検証 ⚠採用（il-generic で緑確認）

## 位置づけ（原設計）
**位置づけ**: Track E-1「ジェネリクス」の中核。現状 `birType` は型パラメータ `T` を `object` に**消去**しており、ユーザ定義の generic class / generic fun / generic interface を IL で*定義*できない（A-3 レジスタ「開いた `Iterator<T>`」「generic method `T M<T>()`」の根）。本設計でこれを閉じる。[[design-first-on-hard-features]] に従い着手前に固定。

## スコープ（段階）

- **G-1**: generic class（型パラメータをフィールド/ctor/メソッドで使用）＋ top-level generic fun。`Box<T>` を構築し ctor/メソッドを呼ぶ。generic method を型引数付きで呼ぶ。
- **G-2**: generic interface ＋ それを実装する class（→「開いた `Iterator<T>`」を閉じる）。境界 `<T : Comparable<T>>`。

各段で実機実行正＋`ilverify` clean を必須ゲート。

## BIR エンコーディング（追加分・後方互換）

| 構文位置 | 追加 JSON | 例 |
|---|---|---|
| 型宣言 | `"typeParams": ["T"]`（省略=非ジェネリック） | `Box<T>` |
| メソッド宣言 | `"typeParams": ["T"]` | `fun <T> id` |
| 型パラメータ参照 | 型文字列 `gp:<name>` | `T` → `gp:T` |
| 構築済みユーザ generic 型 | `@Name[arg,...]`（既存 `@Name` を括弧拡張） | `Box<int>` → `@Box[int]` |
| generic method 呼出 | `callStatic`/`callInstance` に `"typeArgs": ["int"]` | `id<Int>(x)` |

`gp:T` の解決は**文脈依存**: メソッド自身の型パラメータ → 型の型パラメータ の順（shadowing 対応）。

## ilemit 側の変更

### TypeInfo
`GenericTypeParameterBuilder[] TypeParams` ＋ `Dictionary<string,GenericTypeParameterBuilder> TypeParamMap` を追加。

### 文脈フィールド
`_curTypeParams` / `_curMethodParams`（name→GenericTypeParameterBuilder）。Pass 3（宣言）/Pass 4（本体）で各 ti・各 method の前後にセット/クリア。

### Pass 1
型に `typeParams` があれば `DefineType` 直後に `tb.DefineGenericParameters(names)`。返り値を TypeParamMap へ。

### MapType
- `gp:T` → `_curMethodParams[T] ?? _curTypeParams[T]`。
- `@Name[args]` → `_types[Name].TB.MakeGenericType(args.map(MapType))`（構築済みジェネリック）。

### DeclareMethod（generic method）
一発の `DefineMethod(name,attrs,ret,params)` は不可（署名が自分の型パラメータを参照）。段階形:
```
var mb = tb.DefineMethod(name, attrs);
var gps = mb.DefineGenericParameters(names);   // _curMethodParams にセット
mb.SetParameters(paramTypes);   // MapType が gp:T を gps へ解決
mb.SetReturnType(retType);
```

### メンバ解決（最難関 — 構築済みジェネリック TypeBuilder）
構築済み `Box<int>`（TypeBuilderInstantiation、未 bake）に対しては通常の `.GetMethod/.GetConstructor/.GetField` が使えず、静的ヘルパが必須:
- `TypeBuilder.GetConstructor(constructed, ctorBuilderOnOpenDef)`
- `TypeBuilder.GetMethod(constructed, methodBuilderOnOpenDef)`
- `TypeBuilder.GetField(constructed, fieldBuilderOnOpenDef)`

第2引数は**開いた定義側の Builder**でなければならない。`new`/`callInstance`/`field` の各ハンドラで「owner が構築済みジェネリックか」を判定し分岐する。**※この部分は設計検証エージェントの結論を反映してから確定（PersistedAssemblyBuilder での GetX 静的ヘルパ可否・generic interface の DefineMethodOverride 引数・CreateType 順序）。**

### generic method 呼出
MethodBuilder（generic method definition）→ `mb.MakeGenericMethod(typeArgs)` して `call`/`callvirt`。

## リスク / 検証ゲート
- PersistedAssemblyBuilder（新 persisted emitter）での generic 周りの既知バグ＝検証エージェントで洗い出し。
- 各段で `ilverify` clean を必須。生成 PE が corrupt なら GenerateMetadata/save 時に落ちるか BadImageFormat。
- 既存 25 pure＋dedicated サンプルの回帰ゼロ維持（`gp:` 化で erasure 依存が壊れないこと）。
