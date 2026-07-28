# CLR reference assembly → standard KLIB PoC

## 結論

1 CLR reference assemblyを1つのmetadata-only KLIBへ変換し、それを通常の
`kotc -classpath`へ渡す構成は実現可能である。PoCでは独自JSON resourceも
FIR declaration-generation extensionも使わず、次の経路を最後まで通した。

```text
Probe ref.dll
  -> C# / System.Reflection.Metadata
  -> packed Kotlin 2.4.0 KLIB
  -> kotc standard KLIB loader
  -> BIR
  -> bir2cir (CLR reference setでbinding)
  -> ilemit
  -> Consumer.dll
  -> 実行結果 44
```

ただし、これは現行facadegenをそのまま置換できる段階ではない。特にCLR nullability
attributes、DotKt round-trip metadata、events/default arguments、nested typesを
標準KLIB語彙へ移す必要がある。また、性能上の勝ち筋は
project-localなMSBuild incremental outputとKLIB loaderのpackage単位ロードにある。

## 調査で確定したこと

### 現行経路

現行facadegenの出力はKotlin source textではなく、`types`と`files`を持つJSONで
ある。kotcは`CLR_TYPES_METADATA`からこれを読み、
`FirDeclarationGenerationExtension`で宣言を注入している。

- `toolchain/facadegen/Program.cs`
- `toolchain/kotc/src/main/kotlin/kotc/frontend/ClrTypeInjection.kt`
- `packaging/DotKt.Toolchain/build/DotKt.Toolchain.targets`

したがってKLIB化で消せる可能性があるのは「source parse」ではなく、
facadegen起動・reflection projection・JSON parse・FIR synthetic declaration
generationである。

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
```

1プロセスで1 DLLだけを読み、1 KLIBだけを書く。metadataの読み取りには
`PEReader`、`MetadataReader`、`ISignatureTypeProvider`を使い、assemblyをload
しない。現在のPoC surfaceは次である。

- public top-level class/interface
- public/protected constructor
- public/protected instance method
- public/protected static method
- public/protected non-indexed property
- public/protected fieldのKotlin property射影
- public/protected static property/field
- enum entry
- primitive、class、array、基本的なgeneric type
- generic method declarationとgeneric parameter constraint
- classのbase type/interface
- CLR oblivious reference typeのKotlin flexible type (`T..T?`) 表現
- Kotlin 2.4.0 standard `IS_STATIC_FUNCTION` / `IS_STATIC_PROPERTY` flag

現在意図的に未実装なのは、events、indexers、nested types、delegates、
custom attributes、DotKt round-trip attributes、full nullability projectionなどである。

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
そのdispatch argumentを省略し、setter valueだけを渡すため、BIR emitterは
「class-owned property fake overrideで、供給引数がnon-dispatch parametersを
ちょうど満たす」というKotlin IR shapeからstatic accessを保持する。CLR propertyか
fieldかはここでも決めず、bir2cirがreference assemblyから解決する。

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

この分離によりkotcは「このKLIBがCLR由来か」を示すside tableや独自resourceを
必要としない。

## 実行方法

```bash
bash tests/special/dll2klib-poc/run.sh
```

testは別途facadegen metadataを生成せず、`CLR_TYPES_METADATA`も明示的にunsetする。
次を検証する。

1. reference assemblyを生成
2. 2回のKLIB出力がbyte-identical
3. standard KLIB layout
4. kotcがconstructor、instance/static method、instance/static property/fieldを解決
5. bir2cirがCLR reference metadataから`clrInstance`/`clrStatic`へbind
6. ilemitでassembly生成
7. platform typeのnullable-input/non-null-output両方向を型検査
8. interface dispatchとgeneric methodを含め、実行結果が`44`

## MSBuild接続案

`ReferencePathWithRefAssemblies`は
`FindReferenceAssembliesForReferences` targetのoutput itemである。したがって
生成targetは単なる`ResolveReferences`ではなく、これへ依存する。

概念上は次のbatchになる。

```xml
<Target Name="DotKtGenerateReferenceKlibs"
        DependsOnTargets="FindReferenceAssembliesForReferences"
        BeforeTargets="KotlinCompile"
        Inputs="%(_DotKtReferenceAssembly.FullPath);$(DotKtClr2Klib)"
        Outputs="%(_DotKtReferenceAssembly.KlibPath)">
  <ItemGroup>
    <_DotKtReferenceAssembly Include="@(ReferencePathWithRefAssemblies)">
      <KlibPath>$(BaseIntermediateOutputPath)dotkt-klib/%(Filename).klib</KlibPath>
    </_DotKtReferenceAssembly>
  </ItemGroup>
  <!-- Task batchingにより1 invocation = 1 DLL = 1 KLIB -->
  <Exec Command="dotnet &quot;$(DotKtClr2Klib)&quot;
                 &quot;%(_DotKtReferenceAssembly.FullPath)&quot;
                 &quot;%(_DotKtReferenceAssembly.KlibPath)&quot;" />
</Target>
```

production化では以下も必要である。

- `@(ReferencePathWithRefAssemblies)`の全KLIBをOSのpath separatorで
  `$(DotKtStdlib)`へ追加し、kotc classpathにする。
- `DotKt.Private.Stdlib`は既存frontend stdlib KLIBと重複するため除外する。
- assembly simple name衝突を検出するか、MVIDをoutput/cache keyへ含める。
- generator binaryを`Inputs`へ含め、input timestampと合わせて再生成を判定する。
- outputは`$(IntermediateOutputPath)`配下だけに置き、共有cacheは作らない。
- reference setはKLIB manifestへdependsとして複製せず、そのbuild invocationの
  classpathをMSBuildのresolved setから作る。

shipping targetsへの切り替えはまだ行っていない。未実装surfaceを持つPoC writerを
全projectへ有効化すると、JSON経路で現在扱えている宣言をsilentに落とすためである。

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

PoCは`#nullable disable`相当のC# method
`string Echo(string value)`をKLIBへ変換し、次を同時にコンパイル・実行した。

```kotlin
val maybe: String? = "x"
val definitely: String = Widget(3).Echo(maybe)
```

nullable値をparameterへ渡せることは「固定non-null型ではない」ことを、returnを
non-null変数へ代入できることは「固定nullable型ではない」ことを確認している。
このconsumerは標準KLIB loaderからBIR、bir2cir、ilemitまで通り、実行結果`44`を
得た。

以前懸念していた`NullFlexibleTypeDeserializer`はdescriptor-based KLIB
deserializationの経路であり、現在の`kotc`が使うFIR KLIB providerの経路ではない。
descriptor APIを利用する周辺toolが将来必要なら別途互換性確認が要るが、frontend
compile pathのblockerではない。

残る作業は「KLIBにplatform typeを載せられるか」ではなく、ECMA-335の
`NullableAttribute` / `NullableContextAttribute`とDotKt metadataから各type-useを
non-null、nullable、obliviousのどれにするかをfacadegenと同等に復元することである。

## 未解決事項

### 1. DotKt Kotlin vocabularyのround-trip

DotKt固有resourceは不要である。必要な意味は既にCLR metadata内の
`AssemblyMetadata("DotKt.Compiler", "metadata-v1")`と、compiler-generatedな
`Kotlin*Attribute`群に焼かれている。新writerがそれを
`System.Reflection.Metadata`で読み、標準KLIBのflags、types、annotations、
top-level package declarationsへ直接投影すればよい。

ただし現PoCはまだこのportをしていない。facadegenにある少なくとも次の規則が移植
対象になる。

- Kotlin file class → package-level function/property
- operator / infix / suspend / inline
- object / sealed / fun interface / readonly
- exact Kotlin type carrier、nullable generic、collection identity
- extension/context/function type
- default parameter carrier
- Kotlin名とCLR arity family

これは「dotkt metadataをKLIB resourceとして残す」のではなく、CLR metadataから
標準KLIB declarationへ変換する作業である。

### 2. CLR surface coverage

properties、fields、events、indexers、operators、delegates、enums、byref、
pointers、generic constraints、nested type namingを一般則として実装する必要がある。
特にgeneric constraintsを落としてはいけないというproject principleをproduction
writerにも適用する。

standard KLIBのstatic flagsは利用できたが、language featureの安定性とstatic
propertyも検証が必要である。

### 3. Kotlin metadata format compatibility

KLIB metadata schemaはKotlin compiler内部formatである。C# writerはKotlin 2.4.0
wire schemaとversionへ明示的にpinされる。compiler更新時には次をcompatibility
testにする。

- upstream proto field/extension number
- manifest ABI/metadata version
- old generated KLIBをnew kotcが読めるか
- new generated KLIBをsupported kotcが読めるか

## 性能仮説とbenchmark

現行facadegenはimport-drivenであり、sourceで使う型を中心にprojectionする。対して
素朴な「毎clean buildで全reference assemblyを1プロセスずつKLIB化」は、
framework reference数だけprocess startupと全metadata scanが増えるため、cold build
では悪化し得る。

期待できる構成は次である。

- output: `$(IntermediateOutputPath)`配下のassembly単位packed KLIB
- MSBuild `Inputs`/`Outputs` timestamp判定でup-to-dateなKLIBを再利用
- stale outputだけassembly単位に生成し、必要ならMSBuildで並列化
- kotcは必要なpackage fragmentだけをstandard KLIB providerから読む

比較すべきcaseは最低でも次の4つ。

1. empty cacheのclean build
2. warm cacheのclean project build
3. Kotlin source 1ファイル変更
4. reference DLL 1個だけ変更

各caseでwall timeに加え、facadegen/clr2klib process time、kotc frontend time、
peak RSS、読んだDLL/KLIB数とbytesを取る。PoCは形式とlayeringの実現性を証明したが、
「コンパイルが速い」はこのbenchmarkを行うまで未確定である。

## 判定

技術的実現性は **あり**。特に「標準KLIBだけでkotcがCLR宣言を解決し、そのownerを
bir2cirでreference assemblyへbindできる」点はE2Eで確認済みである。

production移行の順序は次が安全である。

1. converterのsurfaceをfacadegen parityまで増やす。
2. 同じDLLについてJSON injectorとKLIBのresolved FIR/BIR surfaceを差分比較する。
3. CLR/DotKt nullability attributeのprojectionをfacadegen parityへ揃える。
4. project-local outputと`ReferencePathWithRefAssemblies` batchingを入れる。
5. opt-in dual pathでbenchmarkする。
6. parityと性能を確認後にfacadegen JSON経路を外す。
