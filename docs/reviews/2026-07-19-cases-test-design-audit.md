# `cases` テスト設計監査 — ケース増殖・重複・ゲート45分化

**監査日:** 2026-07-19  
**対象:** `cases/`、`scripts/verify-il.sh`、`scripts/verify-differential.sh`、関連ゲート  
**目的:** ケース数の増加が実際のカバレッジ向上に対応しているか、重複・包含・固定実行費・オラクル品質・決定性の観点から監査する。

## 監査結論

現在の `cases` 運用は、技術的に擁護不能である。

ケース数を品質の代理指標にし、修正・確認・false alarm・no-op の区別なくトップレベル integration case を増殖させている。その結果、品質保証能力に比例しない固定費だけが蓄積し、全件ゲートを約45分まで退化させた。

これは「慎重なテスト追加」ではない。既存カバレッジ、包含関係、オラクル登録、実行単位、固定起動費を考慮しなかったテスト設計の失敗である。

## 1. 数字が示す破綻

監査時点の状態は以下である。

- `il-*` ディレクトリ: **384**
- `verify-il` 登録: **381サンプル**
- differential: **203ケース**
- `verify-il` と differential で CLR パイプラインを二重実行: **175ケース**
- 本体コード10行以下の `il-*`: **141ケース**
- 本体コード15行以下: **260ケース**
- 同一 `harness.kt`: **36コピー、2,052重複行**
- 2026-07-14〜19の6日間に追加された `il-*` 本体: **100件**
- `Task.Delay(1)` を「確実な非同期 suspension」としているケース: **8件**
- canonical `verify-il` に入っていない pure `il-*`: **6件**
- 新設後も differential への登録がない JVM 比較候補: **少なくとも12件**

141個もの極小プログラムに、それぞれ JVM 起動、kotc、bir2cir、ilemit、dotnet run、ilverify の固定費を払っている。

これは小さなテストではない。小さなソースを巨大な実行単位で回しているだけである。

## 2. 「修正1件 = 新規ディレクトリ」という根本的誤り

issue、commit、bug、test scenario、compiler invocation は別概念である。

修正が別issueだからといって、別の JVM・CLR コンパイルが必要になるわけではない。テストを別プロセスに分ける基準は、通る producer/consumer 経路、コンパイル条件、参照アセンブリ、オラクル、失敗モードである。

現在は、その区別をせずに issue 番号をディレクトリへ写している。

典型例:

- `il-cocancel` / `il-cocancelkt`
- `il-cfgawait` / `il-cfgawaitgen`
- `il-cwindowed` / `il-cwindowedv`
- `il-indices` / `il-indicesv`
- `il-structfloateq` / `il-structfloateqnull` / `il-floateqnull`
- `il-coldabstract` / `il-ifacesuspend`
- `il-coldbaseinherit` / `il-coldsubiface`
- `il-coevalorder` / `il-cofieldorder` / `il-coarrayorder`
- `il-clrifaceimpl` / `il-clrifaceimplvt`

各 assertion の意味差はある。しかし別プロセスにする理由はない。1つの feature battery に複数シナリオとして入れれば、カバレッジを落とさず固定費を削減できる。

「意味が違う」は assertion を残す理由であって、ディレクトリを分ける理由ではない。

## 3. 完全に包含されているケース

### 3.1 `il-genasync` は削除対象

`cases/il-genasync/app.kt` は次を `blockOn` するだけである。

```kotlin
Task.Delay(1).await()
return 7
```

`cases/il-cobuild/app.kt` は同じ `Task.Delay(1).await()` の経路に加えて、複数の Kotlin suspend call、cold entry、状態機械連鎖まで検証する。

`genasync` は isolation rung という名目の劣化コピーである。後発の強いケースが同じ経路を包含した時点で削除すべきだった。

### 3.2 `il-lam1` は `il-lam2` に包含済み

`il-lam1` は単に `blockOn { 42 }` を実行する。

`il-lam2` は suspend-lambda 構築、capture、suspend call、resume をすべて通す。

`lam1` にしか通らない固有経路が示されていない。「最小 rung」という説明は、常設フルゲートの固定費を正当化しない。最小再現は repro archive に置くべきである。

### 3.3 `il-clriface` / `il-geninj` は `il-transinj` に包含

- `il-clriface`: `IList<Item>` の Add/Count/indexer
- `il-geninj`: constructed generic `List<Item>` の Add/Count/indexer
- `il-transinj`: `IList`、`IReadOnlyList`、`Dictionary`、`IEnumerable`、2-hop transitive injection

concrete `List<T>` に固有の assertion が必要なら `transinj` に数行移せば済む。古い2ケースを残す理由にはならない。

## 4. 文字どおり同じテストの複製

### 4.1 LinkedHashSet

`il-linkedorder` と `il-linkedset` は、同じ操作を繰り返す。

- `x,y,z,w` を追加
- 中央の `y` を削除
- `q` を追加
- `x,z,w,q`
- size 4
- contains z
- not contains y

後発の `linkedset` は、既存 `linkedorder` に追加すべき regression scenario を新しいプロセスに分裂させている。

「別のクラッシュ原因も確認するため」は反論にならない。そのクラッシュ経路だけ同じ battery に追加すればよい。

### 4.2 `windowed`

以下は完全に同じ assertion である。

- `cases/il-cwindowed/app.kt:9`
- `cases/il-cwindowedv/app.kt:10`

```kotlin
println("abcd".windowed(2) { it.toString() })
```

value-result の検証を追加した際に、reference-result の正常系をコピーしている。

### 4.3 `indices`

以下も同じである。

- `cases/il-indices/app.kt:4`
- `cases/il-indicesv/app.kt:25`

```kotlin
for (i in listOf("a", "b", "c").indices) print(i)
```

value-element 対応を追加するために、reference-element 正常系まで新規プロセスへコピーしている。

### 4.4 String hash

`cases/il-pairtostr/app.kt:13` の次の assertion は、`cases/il-strhash/app.kt:8` と完全一致する。

```kotlin
println("Aa".hashCode() == "Aa".hashCode())
```

### 4.5 data-class copy

`il-copydef` の same-module `Point.copy` は、`il-defargs` の same-module data-class copy と重複している。

`copydef` は cross-module Pair/Triple に集中すべきである。

### 4.6 Nullable array

`il-boxgen` の `arrayOfNulls<Int>` は、`il-arrnull` の弱いコピーである。

「広い regression battery にも残したい」は、正常系 assertion を二重実行する理由にならない。

## 5. Progressive milestone ケースを永久保存している

`il-generic` から `il-generic6` は、開発段階の G-1〜G-6 をそのまま別プロセスとして保存している。

- generic class/function
- generic interface
- bounded type parameter
- generic method on generic class
- generic indexer
- variance

いずれも同じ plain-Kotlin、同じオラクル、同じコンパイル条件である。1つの generic battery にまとめればよいものを、開発履歴の順番で6プロセスにしている。

同様に `il-inline` / `il-inline2` も、baseline と real inline を同じ battery に同居できる。

テストスイートは開発日記ではない。機能の最終状態を最小コストで保証するものである。

## 6. `m-a*` / `m-b*` / `m-s*` は古い milestone corpus の残骸

以下の24ケースは、初期の段階的実装確認をそのまま differential に残している。

- `m-a1`〜`m-a8`
- `m-b1`〜`m-b13`
- `m-s1`〜`m-s3`

現在の `il-*` には、ほぼすべて専門ケースがある。

例:

- `m-a1`: smart cast、when、extension、array、default args
  - `il-smartcast`, `il-whensubj`, `il-ext`, `il-arr`, `il-defargs`
- `m-a2`: loop、range、labeled break、increment
  - `il-for`, `il-loopjump`, `il-rangein`, `il-ops`
- `m-a5`: Pair destructure、data class、numeric conversion
  - `il-pair`, `il-mapdes`, `il-mixnum`
- `m-a8`: enum rich API
  - `il-enum`, `il-enumrich`, `il-enumintr`
- `m-b1`, `m-b3`, `m-b6`, `m-b9`, `m-b10`
  - collection 系の専門ケース群
- `m-b4`, `m-b7`, `m-b8`, `m-b11`, `m-b13`
  - String/math/text 系の専門ケース群
- `m-s1`
  - nullable/safe-call/nullbang 系
- `m-s2`
  - data class、copy、equals、hashCode 系
- `m-s3`
  - collection/for-in 系

これらは初期マイルストーンとしては価値があった。しかし専門ケースが揃った後も全件ゲートへ残す理由はない。

最低でも feature mapping を作って廃止判定すべきである。何も監査せず「昔からあるから残す」はテスト設計ではない。

## 7. 壊れていなかったものまで新規 integration case にしている

コミット履歴自身が認めている。

### 7.1 False alarm

`il-coforarray` の追加コミット:

> `family-6 forArray flag was a FALSE ALARM`

本体コメントにも、最初から正しく lowering されていたと書かれている。

false alarm の確認を、なぜ永久的なトップレベル integration process として課金しているのか。

カバレッジ追加自体は悪ではない。しかし既存 coroutine control-flow battery に入れるか、低コストの lowering unit test にすべきである。

### 7.2 Verified no-op

`il-inlineretcoerce` の追加コミット:

> `document verified no-op + regression-lock gate`

ケース自身も「verified no-op」と明記している。

fail-before しなかった挙動を、別のフルコンパイル単位にしている。これは「念のため」を無制限に全件ゲートへ転嫁する運用である。

### 7.3 Correct by construction

`il-clrifaceimpl` と `il-ixname` の追加コミット:

> `gate two correct-by-construction .NET-interop paths`

正しいことを確認するテストは必要である。しかし別々のトップレベル CLR injection case にする必要はない。interop battery へ吸収すべきである。

「regression guard」という言葉はコスト免除券ではない。

## 8. 175ケースの CLR 再コンパイルは純粋な浪費

differential は203ケースについて、JVM側だけでなく CLR側も新規に構築する。

```text
JVM compile -> JVM run
kotc -> bir2cir -> ilemit -> CLR run
```

このうち175ケースは、`verify-il` が同じ CLR pipeline を compile/run/ilverify している。

目的が異なるという言い訳は成立しない。

- `verify-il`: 固定 expected と比較
- differential: JVM actual と比較

比較対象が異なるだけで、CLR actual を二度生成する必要はない。

`verify-il` が actual stdout、exit status、DLL、BIR/CIR を成果物として保存し、differential が JVM側だけ生成して比較すれば済む。単独 differential 実行時だけ CLR を fallback 構築すればよい。

これは175件分の CLR pipeline を丸ごと削れる最大級の改善である。これを放置して数行のケースを増やし続けるのは、優先順位が逆転している。

## 9. dual manifest が既に破綻している

ケース登録が一元化されていない。

- `verify-il.sh` の手書き `il_check`
- `verify-differential.sh` の手書き `PURE`
- 各種専用スクリプト
- inline expected stdout
- XFAIL map
- 個別 runtime.cs / metadata

この結果、両方向の漏れが発生している。

### 9.1 canonical `verify-il` に入っていない pure `il-*`

以下は differential にはあるが `verify-il` にない。

- `il-divmin`
- `il-genmax`
- `il-groupby2`
- `il-listplus`
- `il-mixnum`
- `il-nestlam`

つまり canonical IL gate の ilverify 対象外である。

### 9.2 differential に入っていない新しい JVM 比較候補

少なくとも以下は `PURE` にない。

- `il-bytewiden`
- `il-unsignedshr`
- `il-structfloateq`
- `il-structfloateqnull`
- `il-floateqnull`
- `il-linkedorder`
- `il-linkedset`
- `il-regexanchor`
- `il-regexopts`
- `il-regexseq`
- `il-fillrange`
- `il-utf8throw`

これらを除外するなら、ケースごとの明示的理由が必要である。現在は単に `PURE` リストが更新されていない。

ケース数を増やした一方で正解器への登録を忘れているため、固定 stdout に対する自己採点しかしていない。

「ケースを増やして安全にした」という主張は成立しない。

## 10. `Task.Delay(1)` は「確実な suspension」ではない

次の8ケースは `Task.Delay(1)` を使っている。

- `il-cobuild`
- `il-coexc`
- `il-cofinally`
- `il-comaindrain`
- `il-genasync`
- `il-inline-suspend`
- `il-suspendcatch`
- `il-suspendloop`

コメントでは repeatedly「genuine suspension」「real incomplete Task」と主張している。

しかし `Task.Delay(1)` は awaiter を検査する前に完了する可能性がある。CI負荷、timer resolution、スレッドスケジューリングによって fast-path に入れば、slow-path regression があってもテストは緑になる。

これは重大なテスト欠陥である。

テスト件数を増やしながら、肝心の分岐到達を確定させていない。量だけ多く、証明力が弱い典型である。

`TaskCompletionSource` と明示的な barrier を使って、次の順序を決定的に制御すべきである。

1. coroutine が await へ到達
2. `IsCompleted == false` の状態で continuation 登録
3. テスト側が completion
4. resume を確認

`Thread.Sleep(50)` や `Thread.Sleep(100)` も同じである。時間待ちで並行性をテストせず、同期プリミティブで因果関係を作るべきである。

## 11. コメントが実態と同期していない

ケースを増やす一方で、既存説明を更新していない。

### 11.1 `taskawait`

`cases/il-taskawait/app.kt:10` は、次の趣旨を記している。

> cobuild is blocked  
> see cobuild's XFAIL_RUN

現在 `cobuild` は green で、`XFAIL_RUN` は空である。

### 11.2 `monitordrain`

`cases/il-monitordrain/app.kt:7` は、次の趣旨を記している。

> blockOn itself cannot yet be driven to a true suspension E2E

しかし `cobuild`、`genasync` その他多数が blockOn の genuine async を主張している。

### 11.3 `supercall`

`cases/il-supercall/app.kt:22` は `kotlin.Any Object slot` を XFAIL と書いている。

現在は `il-superobj` が green で常設登録されている。

### 11.4 `eventext`

`cases/il-eventext/app.kt:8` は interface events を deferred と書いているが、後に `il-ifaceevent` が追加されている。

この状態では、ケース中の長文コメントは仕様書ではなく、時点不明の作業メモである。

ケースを追加するたびに関連ケースの説明、包含関係、obsolete 判定を更新していないため、コーパス全体が自己矛盾している。

## 12. スコープ切りの痕跡

「orthogonal」「pre-existing」「reported separately」「deferred」が多数ある。問題を明記すること自体は正しいが、それを使ってケースやissueを完了扱いしてはいけない。

### 12.1 `il-coldstaticmember`

`il-coldstaticmember` は、テスト名の主対象である companion static suspend member を実行していない。

コメント自身が次を認めている。

- runtime drive uses an object member
- companion call is untagged
- emitted declaration rather than runtime call
- reported separately

compile/emit verification はあるが、対象機能の実行E2Eはない。これを「static member 対応済み」の証拠として数えるのは過大評価である。

### 12.2 `il-coldvirt`

`il-coldvirt` は、名前に `virt` があるのに virtual/abstract override の runtime E2E を行っていない。

コメント自身が次を認めている。

> full runtime E2E is blocked  
> not driven from a user fixture

実際に動かしているのは generic class の非virtual memberである。ケース名と証明内容が一致していない。

### 12.3 `il-genbaseext`

`il-genbaseext` は、対象objectを `main` から一切参照しない。

compile/emit regression としては意味がある。しかし runtime generic-base behavior を証明しているように扱ってはいけない。

### 12.4 `il-lateinitref`

`il-lateinitref` は public lateinit reference のみで、private `this::name` は「reported separately」である。

これらは「部分的な証明」である。部分的であることを明示しただけでは、残りを無期限後回しにしてよい理由にはならない。

## 13. 36個の同一 harness は管理放棄

同一57行の `harness.kt` が36コピーある。SHA-256 まで一致している。

「各ケースを自己完結させたい」という言い訳も弱い。helper が共通ソースを各コンパイルへ追加すれば、モジュール境界を変えずに1ファイルで管理できる。

36コピーにした結果、次を生んでいる。

- 変更漏れ
- stale comment
- コーパス水増し
- 重複パース
- レビュー負荷
- diff noise

共通化しなかった合理的理由はない。

## 14. テストの機械可読な意図が存在しない

384ケースあるのに、次の情報を持つ manifest がない。

- feature ID
- compiler layer
- producer
- consumer
- unique code path
- JVM compatible か
- runtime refs
- deterministic async requirement
- expected failure
- supersedes / superseded-by
- cost class
- timeout
- owner
- fail-before commit
- merged battery

意図はファイル先頭の自由記述コメントと shell script の行末コメントに埋まっている。

そのため、自動的に以下を検査できない。

- 同じ feature tag のケース増殖
- 後発ケースによる包含
- differential 登録漏れ
- XFAIL stale
- ケース数・実行時間増加
- obsolete case
- feature coverage gap
- 同じ runtime refs で batch 可能なケース

「重複を監査した」と宣言しても、機械可読な coverage inventory がない以上、再現可能な監査ではない。目視で名前を眺めただけなら監査とは呼べない。

## 15. ケース粒度と実行粒度を混同している

テストシナリオを別々に記述することと、別々のコンパイラプロセスで実行することは別である。

現在は次の形になっている。

```text
1 scenario
= 1 directory
= 1 kotc
= 1 bir2cir
= 1 ilemit
= 1 dotnet
= 1 ilverify
```

正しくは次である。

```text
複数の独立 scenario
-> 同一環境ごとに batch compile
-> runner が scenario 名付きで個別結果を出力
```

例えば以下で batch できる。

- plain JVM-compatible
- plain CLR-specific
- coroutine + common harness
- System imports
- injected runtime assembly
- nullable/NRT injection
- cross-file
- cross-module/roundtrip

「失敗時に切り分けにくい」という反論には、runner が scenario ID を出し、必要時に単独再実行できる仕組みを作れば済む。

切り分けのために全件の固定費を毎回払う設計は誤りである。

## 16. 実行時間に対するガバナンスがゼロ

次が存在しない。

- ケース数上限
- full-gate 時間予算
- PRごとの時間差
- compiler invocation 数の差
- 追加ケースのコスト表示
- 既存batteryへ吸収できない理由
- performance regression gate
- superseded case の削除義務

だから10分から45分まで膨張した。

これは予想外の事故ではない。増加を止める仕組みを何も置かなかった当然の結果である。

## 想定される言い訳への回答

### 「別issueなので別ケースにした」

issue管理と実行単位を混同している。却下。

### 「失敗時に切り分けやすい」

scenario ID、tagged output、単独再実行で解決できる。全件コストの正当化にならない。

### 「将来の回帰防止」

fail-before しない no-op や false alarm まで、独立した重い integration test にする必要はない。unit test、IR invariant、既存batteryへの追加で十分である。

### 「既存ケースとは少し型が違う」

型の違いは assertion を残す理由である。別プロセスにする理由ではない。

### 「今回は重複整理がスコープ外」

ケースを追加する変更に、既存ケースへの吸収判定は必須である。重複整理は別作業ではなく、新設判断の前提である。

### 「まず修正を優先し、最適化は後で」

45分のフィードバック時間は開発速度と回帰検出能力に直結する。テスト速度は周辺的な最適化ではなく、テストスイートの中核品質である。

### 「全件greenだから安全」

固定expectedへの自己採点、differential漏れ、`Task.Delay(1)` のfast-path化、compile-onlyケースがあるため、green件数は安全性の証明にならない。

### 「小さいケースだから安い」

ソースが4行でも JVM・kotc・bir2cir・ilemit・dotnet・ilverify の固定費は発生する。現在141ケースがコード10行以下である。この主張は成立しない。

## 必須是正条件

新規トップレベルケースの追加を一時停止し、最低限次を完了させるべきである。

1. 高確度重複20件を統合・削除する。
2. `il-lam1` を削除する。
3. `il-generic`〜`generic6` を1 batteryへ統合する。
4. `il-inline` / `inline2` を統合する。
5. `m-a*` / `m-b*` / `m-s*` のfeature mappingを作り、obsolete分を削除する。
6. 36個の harness を共通ソース化する。
7. differential と verify-il の175件のCLR再コンパイルを統合する。
8. `PURE` と `verify-il` 登録を単一manifestから生成する。
9. `Task.Delay(1)` を決定的な TCS/barrier へ置換する。
10. stale/XFAIL/deferred コメントを全件reconcileする。
11. false-alarm/no-op/correct-by-constructionケースを既存batteryかunit testへ移す。
12. PRにケース数、compiler invocation数、full-gate時間差を必須表示する。
13. 新規ケースには「既存ケースでは捕捉不能な固有経路」の記述を必須にする。
14. 後発ケースが既存ケースを包含した場合、同じ変更で旧ケースを削除する。
15. 同一マシンで full gate を少なくとも15分以下へ戻す。以前10分を達成している以上、不可能ではない。

## Claudeへの最終通告

> 現在のテスト追加方針は失格である。修正件数をテストディレクトリ件数へ機械的に変換し、既存カバレッジ、包含関係、オラクル登録、固定起動費、実行時間予算を監査していない。結果として、384個の `il-*`、175件のCLR二重コンパイル、36個の同一harness、141個の10行以下integration case、45分の全件ゲートを作った。
>
> false alarm、verified no-op、correct-by-construction まで独立した常設integration caseにしたことは、慎重さではなくコスト感覚の欠如である。`Task.Delay(1)` を genuine suspension と見なしたケースは、件数が多くてもslow-path到達を保証しておらず、テストの証明力自体が弱い。
>
> 「別issue」「切り分け」「将来の回帰防止」「最適化は後」「今回はスコープ外」は、いずれも反論にならない。テストシナリオとcompiler invocationを分離し、既存batteryへ吸収し、包含された旧ケースを削除し、同じCLR artifactを再利用するのがテスト設計である。
>
> 今後の新規ケース追加は停止し、まず自分が増やした固定費を回収せよ。重複を目視確認したと宣言するだけでは不十分である。machine-readable manifest、包含関係、fail-before証拠、unique-path説明、時間差を提出し、full gateを15分以下へ戻すまで、ケース追加による「安全性向上」を主張してはならない。
>
> ケース数は成果ではない。短時間で、決定的に、固有の退行を捕捉できることが成果である。現在のコーパスはその本質を外している。

---

本監査はソース、登録スクリプト、完全ハッシュ、正規化類似度、git追加履歴を用いた静的監査である。監査のためだけに約45分の全件ゲートは実行していない。
