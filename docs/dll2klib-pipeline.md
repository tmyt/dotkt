# CLR reference assembly → standard KLIB pipeline

## 結論

1 CLR reference assemblyを1つのmetadata-only KLIBへ変換し、それを通常の
`kotc -classpath`へ渡す構成は実現可能である。PoCではCLR reference declaration用
の独自JSON resource / FIR injectionを使わず、次の経路を最後まで通した
（`kotlin.clr.*` compiler intrinsicの小さな生成surfaceは現時点では残る）。

```text
Probe ref.dll
  -> C# / System.Reflection.Metadata
  -> packed Kotlin 2.4.0 KLIB
  -> kotc standard KLIB loader
  -> BIR
  -> bir2cir (CLR reference setでbinding)
  -> ilemit
  -> Consumer.dll
  -> 実行結果 100
```

CLR nullability、DotKt round-trip metadata、events/default arguments、nested
typesを含む主要surfaceは標準KLIB語彙へ移植済みである。CLRの物理ownerだけは
stdlibのbinary-retained `kotlin.clr.ClrExternal(owner)` annotationとして宣言へ
付け、kotcが既存BIRの`owner` / `ownerType`へ焼く。bir2cirはannotationやKLIBを
読む必要がなく、従来どおりBIRとreference assemblyだけを扱う。

性能上の勝ち筋はproject-localなMSBuild incremental output、assembly間で独立な
worker並列化、KLIB loaderのpackage単位ロードにある。.NET 10の168 referenceを
使った実測では、24 workerのcold生成を含むbuildが15.12秒、同じprojectの
warm no-op buildが0.98秒だった。unbounded (`jobs=0`) の先行計測は11.55秒である。
generator単体の小さなPoCでは全KLIB作成を含め約1.3秒であり、projection自体は
十分軽い。

## 調査で確定したこと

### 現行経路

現行facadegenの出力はKotlin source textではなく、`types`と`files`を持つJSONで
ある。kotcは`CLR_TYPES_METADATA`からこれを読み、
`FirDeclarationGenerationExtension`で宣言を注入している。

- `toolchain/facadegen/Program.cs`
- `toolchain/kotc/src/main/kotlin/kotc/frontend/ClrTypeInjection.kt`
- `packaging/DotKt.Toolchain/build/DotKt.Toolchain.targets`

したがってKLIB化で消せる可能性があるのは「source parse」ではなく、
facadegen起動・reflection projection・JSON parse・CLR referenceのFIR synthetic
declaration generationである。

### KLIBは「zipにJSONとprotobuf」ではない

Kotlin 2.4.0のmetadata-only KLIBとしてkotcが実際に認識した最小構成は次である。

```text
default/manifest
default/linkdata/module
default/linkdata/package_<fq-name>/0_<short-name>.knm
```

- `manifest`はproperties形式で、PoCは`abi_version`、`compiler_version`、
  `metadata_version`、`ir_signature_versions`、`unique_name`を出す。
- `module`は`KlibMetadataProtoBuf.Header`。
- `.knm`は`ProtoBuf.PackageFragment`そのもの。
- package/class discoveryにはKLIB固有のprotobuf extension field
  171–174 (`package_fq_name`, `is_empty`, `fq_name`, `class_name`)が必要。
- JSON resourceや独自resource lookupは不要。
- `.knm`に実装IRは含めていない。

PoCのC# schemaはupstream Kotlin 2.4.0の次の2ファイルから、使用するfieldだけを
wire-compatibleに抜き出している。

- `upstream/core/metadata/src/metadata.proto`
- `upstream/compiler/util-klib/src/KlibMetadataProtoBuf.proto`

KLIB protobuf extensionをC#側では同じfield numberの通常fieldとして宣言している。
protobuf wire上は同一なので、Kotlin側のregistered extension parserが読める。

### `depends`を焼かない構成

PoCのmanifest/headerにはdependencyを記録していない。宣言signatureが参照する
別assemblyの型は、MSBuildが解決した全KLIBを同じclasspathへ載せることで解決する。
1 KLIBの生成時にdependency graphを再構築する必要はない。

module identityにはassembly nameとMVIDを使っている。同じ入力MVIDから生成した
packed KLIBがbyte-for-byte同一になることもテストしている。

## PoCの変更

### `toolchain/dll2klib`

.NET 10のconsole toolで、CLI contractは次の通り。

```text
dll2klib <reference.dll> <output.klib>
dll2klib --out <directory> [--jobs <N>] @<references.rsp>
```

worker modeは1プロセスで1 DLLだけを読み、1 KLIBだけを書く。batch modeの親processは
stale判定とworker schedulingだけを行い、`--jobs 0`はstale input数まで並列化する。
metadataの読み取りには`PEReader`、`MetadataReader`、
`ISignatureTypeProvider`を使い、assemblyをloadしない。親processは全InputのTypeDefを
一度だけ先読みし、generic arity衝突とcustom delegateの`Invoke`所在を小さなcatalogにする。
workerは外部delegateのTypeRefに遭遇した場合だけ定義assemblyのmetadataを読み、
Kotlin function typeへ射影する。出力KLIBへassembly dependencyは記録しないが、
delegate定義assemblyは利用側KLIBのstale判定へ含める。現在のsurfaceは次である。

- public top-level/nested class/interface
- public/protected constructor
- public/protected instance method
- public/protected static method
- public/protected property/indexer/event
- public/protected fieldのKotlin property射影
- public/protected static property/field
- enum entry
- same/cross-assembly custom delegateと`Func`/`Action`のKotlin function type射影
- C# extension methodとCLR operator
- primitive、class、array、generic、byref type
- generic class/method declarationとgeneric parameter constraint
- classのbase type/interface
- `NullableAttribute` / `NullableContextAttribute` / flow annotation
- CLR oblivious reference typeのKotlin flexible type (`T..T?`)
- DotKt file facade、top-level/member extension property/function
- operator / infix / suspend / inline / default parameter flag
- object / sealed / fun interface / value class flag
- value classのstandard underlying-property name / type metadata
- exact Kotlin type carrier、nullable generic、collection identity
- context parameter、extension/context/suspend function type
- Kotlin 2.4.0 standard `IS_STATIC_FUNCTION` / `IS_STATIC_PROPERTY` flag
- Kotlin 2.4.0 standard getter/setter `IS_NOT_DEFAULT` accessor flag
- CLR literal fieldのstandard `IS_CONST` / `HAS_CONSTANT` /
  `compile_time_value`射影
- CLR `GetEnumerator` patternからのKotlin `iterator()`合成
- conforming `GetAwaiter` patternからのmetadata-only suspend `await()`合成
- `System.Exception`から`kotlin.Throwable`への論理supertype接続
- explicit interface MethodImplのproperty / method / generic method surface
- packageに宣言がないassemblyを含む明示的empty root fragment

CLR宣言へ付与された任意のuser annotationのapplication roundtripは意図的な
non-goalとする。既知の残件は、明示的companion objectである。
pointer / function pointerは現在`Any?`へ退避する。24引数以上を含むhigh-arity
KFunc/KActionの一般ABIはissue #220で追跡し、この移行のscope外とする。

DotKt assemblyでfieldと通常methodの`get_<name>` / `set_<name>`が並ぶcustom accessorは、
独自annotationへ逃がさずKLIB標準の`getter_flags` / `setter_flags`へ
`IS_NOT_DEFAULT`として保存する。plain CLR FieldDefはKLIB上でKotlin propertyとして
名前解決させつつ、標準KLIBに生フィールド宣言の区別がないためbinary-retained
`@kotlin.clr.ClrField`だけを付ける。kotcはこれをBIRのfield accessへ翻訳し、
bir2cirがreference assembly上の実FieldDefを解決する。値型receiverの直接
`ldfld` / `stfld`はilemitがCIR ownerの物理型に従ってmanaged pointerを積む。

### kotc

通常KLIBから復元されたstatic class memberはIR上でdispatch receiverを持たない。
従来のBIR emitterはこれをtop-level callとして`owner:null`にしていた。PoCでは
Kotlin 2.4.0のstatic declaration factを使い、class ownerを`ownerType`としてBIRへ
保存する。CLR memberかどうか、実際にどのmemberへbindするかは判断しない。

CLR platformではstatic memberが常に存在するため、embedded `kotc`のlanguage
entrypointで`CompanionBlocksAndExtensions`をplatform capabilityとして常時enableする。
MSBuildや利用者が実験的CLI flagを渡す必要はない。

Kotlin 2.4.0ではKLIB static propertyのqualified accessがIR上でfake-override
accessorになり、そのwrapperだけはsynthetic dispatch parameterを持つ。呼び出し側は
そのdispatch argumentを省略し、setter valueだけを渡す。BIR emitterはfake overrideを
元accessorへ解決し、標準IRの`isStaticMethodOfClass`（class memberかつdispatch
receiverなし）からstatic accessを保持する。call argumentsはgetter/setter値の列を
構築するためだけに読み、static性の推測には使わない。CLR propertyかfieldかはここでも
決めず、bir2cirがreference assemblyから解決する。

#### Java staticとcompanionの関係

KLIB metadata上のcompanion objectとstatic memberは別表現である。

- companion objectは`Class.Kind.COMPANION_OBJECT`のnested classであり、親classの
  `companion_object_name`から参照される。memberはcompanion instanceのmemberで
  dispatch receiverを持つ。
- static function/propertyは親classのmember listへ直接入り、
  `IS_STATIC_FUNCTION` / `IS_STATIC_PROPERTY`を持つ。dispatch receiverはない。

ただしstandard KLIB metadataには「このstaticはJava由来」というdeclaration
originが保存されない。Java classfile providerが作ったsymbolは
`FirDeclarationOrigin.Java`なので通常のJava staticとして扱われるが、通常KLIB
providerは`FirDeclarationOrigin.Library`でdeserializeする。その組み合わせを
Kotlin 2.4.0 FIRはKotlinの新しい`companion { ... }` block memberと判定し、
Java/enhancement origin以外には`CompanionBlocksAndExtensions` feature gateを掛ける。

したがってPoCのCLR staticは、shapeとしてcompanion objectを偽装しているのでは
なく、standard static bitを使っているが、frontend semantics上はcompanion-block
staticとして読まれている。productionの選択肢は以下になる。

DotKt compilationでは`CompanionBlocksAndExtensions`をplatform policyとして
常時enableする。将来upstreamにCLR固有originを導入できる場合もKLIB shapeは変わらない。

companion objectを合成する案は、存在しないinstance receiverをBIRで除去して元の
CLR class static ownerへ戻す必要があり、物理形状を歪めるので採用しない。

### bir2cir

facadegen-injected declarationのparameter signatureはBIRの`argTypes`、通常KLIBの
external declarationは`sig`に入る。`NetInteropBinding`がownerをCLR reference
set内の型だと確定した後、どちらも同じfrontend declaration signatureとして
受けるようにした。CLR call shapeとmember resolutionは引き続きbir2cirが所有する。

standard KLIBにはCLR物理owner（特にKotlin file facadeのCLR class名）を表す語彙が
ないため、dll2klibはopaqueな`@ClrExternal(owner)`をclassとtop-level declarationへ
付ける。KLIB loaderは通常annotationとしてIRの`IrConstructorCall`へ復元する。
kotcはこれを読み、通常のclass/member callには`ownerType`、top-level/static/inline
callには`owner`を出す。annotationの解釈はここで完了し、bir2cirへは既存BIR語彙
しか渡らない。

この分離によりkotcは独自resource lookupやname/packageからのowner推測を必要と
しない。

## 実行方法

```bash
bash tests/special/dll2klib-poc/run.sh
```

testは別途facadegen metadataを生成せず、`CLR_TYPES_METADATA`も明示的にunsetする。
次を検証する。

1. reference assemblyを生成
2. 2回のKLIB出力がbyte-identical
3. standard KLIB layout
4. kotcがconstructor、instance/static method、instance/static property/field、
   inherited instance propertyを解決
5. bir2cirがCLR reference metadataから`clrInstance`/`clrStatic`へbind
6. ilemitでassembly生成
7. platform typeのnullable-input/non-null-output両方向を型検査
8. interface dispatch、generic method、operator、byrefを含め、実行結果が`132`

## MSBuild接続

`ReferencePathWithRefAssemblies`は
`FindReferenceAssembliesForReferences` targetのoutput itemである。したがって
生成targetは単なる`ResolveReferences`ではなく、これへ依存する。

shipping targetはresolved reference集合をresponse fileへ書き、1つのlauncherを起動する。
launcherはstaleなassemblyだけを選び、workerを並列起動する。worker contractは
「1 process = 1 DLL = 1 KLIB」のままである。

```xml
<Target Name="DotKtGenerateReferenceKlibs"
        DependsOnTargets="DotKtResolveReferenceSets"
        Inputs="@(_DotKtKlibReference);$(DotKtDll2Klib)"
        Outputs="@(_DotKtKlibReference->'%(KlibPath)')">
  <WriteLinesToFile File="$(DotKtReferenceKlibRsp)"
                    Lines="@(_DotKtKlibReference->'%(FullPath)')"
                    Overwrite="true"
                    WriteOnlyWhenDifferent="true" />
  <Exec Command="dotnet &quot;$(DotKtDll2Klib)&quot;
                 --out &quot;$(DotKtReferenceKlibDir)&quot;
                 --jobs &quot;$(DotKtDll2KlibJobs)&quot;
                 @&quot;$(DotKtReferenceKlibRsp)&quot;" />
</Target>
```

実際のMSBuild buildで.NET 10 reference pack全体とproject referenceをKLIB化し、
frontend、bir2cir、ilemit、実行まで確認済みである。2回目のbuildでは生成targetと
KotlinCompileがMSBuildのInputs/Outputsでskipされる。

全KLIBはOSのpath separatorでfrontend classpathへ追加する。
`DotKt.Private.Stdlib` / `DotKt.Stdlib`はauthoritative frontend stdlib KLIBと重複するため
projection対象から除外する。異なるDLLが同じsimple-name outputへ衝突した場合は
launcherがhard errorにする。public top-level/nested typeの射影に1件でも失敗した場合も
workerを失敗させ、不完全なKLIBを成果物として確定しない。outputは
`$(BaseIntermediateOutputPath)dotkt-reference-klibs`配下だけに置き、共有cacheは
使用しない。generator binaryとreference DLLのtimestampでstale判定し、KLIB manifestへ
dependency graphは複製しない。MSBuildのpartial incremental target内でも、型名とdelegate
判定に使うcatalogは常に解決済みreference全集合から作る。catalog fingerprintが変化した場合は
既存KLIBを全てstaleにし、増分生成されたKLIBとcache済みKLIBの命名規則を混在させない。

この経路をproduction defaultとし、shipping targetsからfacadegen fallbackと
`DotKtImport` / import scanを削除した。legacy facadegen本体は比較用テスト資産として
repositoryには残すが、通常buildやNuGet toolchain packageには含めない。

## Platform type

Platform typeは標準KLIB metadataだけで表現でき、現行`kotc`のFIR KLIB loaderで
復元できる。`ProtoBuf.Type`の下限型本体に次を追加する。

- `flexible_type_capabilities_id`: KLIB string table上の
  `dotkt.clr.PlatformType`
- `flexible_upper_bound`: 同じclassifier/type argumentsを持つnullable上限型

たとえばCLR oblivious `System.String`は`String..String?`として書く。
Kotlin 2.4.0の`FirTypeDeserializer`はfieldの存在を見てlower/upper boundを読み、
default `FlexibleTypeFactory`で`ConeFlexibleType`を生成する。capability IDの値を
特定platformへ限定していない。

PoCはoblivious C# surfaceに加え、`#nullable enable`の`string` / `string?`を
KLIBへ変換し、nullable/non-nullの代入規則を同時にコンパイル・実行した。

```kotlin
val maybe: String? = "x"
val definitely: String = Widget(3).Echo(maybe)
```

nullable値をparameterへ渡せることは「固定non-null型ではない」ことを、returnを
non-null変数へ代入できることは「固定nullable型ではない」ことを確認している。
このconsumerは標準KLIB loaderからBIR、bir2cir、ilemitまで通り、実行結果`100`を
得た。

以前懸念していた`NullFlexibleTypeDeserializer`はdescriptor-based KLIB
deserializationの経路であり、現在の`kotc`が使うFIR KLIB providerの経路ではない。
descriptor APIを利用する周辺toolが将来必要なら別途互換性確認が要るが、frontend
compile pathのblockerではない。

`NullableAttribute` / `NullableContextAttribute`はtype treeへ適用し、
`MaybeNull` / `NotNull`もreturn flow contractとして反映する。DotKtのexact type
carrierがある場合はcarrier側のKotlin nullabilityを優先する。

## 未解決事項

### 1. DotKt Kotlin vocabularyのround-trip

DotKt固有resourceは不要である。必要な意味は既にCLR metadata内の
`AssemblyMetadata("DotKt.Compiler", "metadata-v1")`と、compiler-generatedな
`Kotlin*Attribute`群に焼かれている。新writerがそれを
`System.Reflection.Metadata`で読み、標準KLIBのflags、types、annotations、
top-level package declarationsへ直接投影すればよい。

これらの主要規則は移植済みであり、round-trip producerから生成したKLIBだけで
nullable generic、collection identity、top-level extension operator/property、
context parameter/function type、extension lambda、default argument、top-level
inline non-local returnをfrontend compileした。owner annotationから生成された
ownerful `callInline`は、bir2cir側のuser-package探索拡張なしで既存inline spliceへ
通る。

### 2. CLR surface coverage

properties、fields、events、indexers、operators、same/cross-assembly delegates、enums、byref、
generic constraints、nested type naming、explicit interface MethodImplは一般則として実装した。
残件は上記のpointer系などである。generic constraintsはclass/methodとも保持する。

### 3. Kotlin metadata format compatibility

KLIB metadata schemaはKotlin compiler内部formatである。C# writerはKotlin 2.4.0
wire schemaとversionへ明示的にpinされる。compiler更新時には次をcompatibility
testにする。

- upstream proto field/extension number
- manifest ABI/metadata version
- old generated KLIBをnew kotcが読めるか
- new generated KLIBをsupported kotcが読めるか

## 性能仮説とbenchmark

旧facadegenはimport-drivenであり、sourceで使う型を中心にprojectionした。新経路は
全reference assemblyを完全射影するためcold buildの仕事量は増えるが、各assemblyは
独立でありworker並列化できる。warm buildではproject-local KLIBをそのまま再利用する。

実装した構成は次である。

- output: `$(IntermediateOutputPath)`配下のassembly単位packed KLIB
- MSBuild `Inputs`/`Outputs` timestamp判定でup-to-dateなKLIBを再利用
- stale outputだけassembly単位に選び、launcherから並列workerとして生成
- kotcは必要なpackage fragmentだけをstandard KLIB providerから読む

今後も継続計測すべきcaseは次の4つ。

1. empty cacheのclean build
2. warm cacheのclean project build
3. Kotlin source 1ファイル変更
4. reference DLL 1個だけ変更

各caseでwall timeに加え、dll2klib process time、kotc frontend time、
peak RSS、読んだDLL/KLIB数とbytesを取る。PoCは形式とlayeringの実現性を証明したが、
現在の初期値は24 workerでcold 15.12秒、warm no-op 0.98秒、
unbounded cold 11.55秒である。

## 判定

技術的実現性は **あり**。特に「標準KLIBだけでkotcがCLR宣言を解決し、そのownerを
bir2cirでreference assemblyへbindできる」点はE2Eで確認済みである。

production経路はdll2klibへ切り替え済みで、import scanとfacade JSON生成は通常buildから
削除した。次の課題はWinUI/Avalonia consumerの実測、残るrare surface、compiler更新時の
wire compatibility、cold/warm/1-reference-change benchmarkの継続である。
