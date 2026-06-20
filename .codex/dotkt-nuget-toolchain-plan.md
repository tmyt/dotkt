# dotkt.toolchain NuGet 化計画

## 目的

`dotkt.toolchain` を NuGet package として配布し、利用者が Gradle やリポジトリ checkout に依存せず、通常の .NET SDK project と同じ感覚で Kotlin-to-CLR project を `dotnet run` できる状態にする。

目標 UX:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="dotkt.toolchain" Version="0.1.0" PrivateAssets="all" />
  </ItemGroup>
</Project>
```

```bash
dotnet run
```

通常利用では `<Import>`, `<StartupObject>`, backend 選択、Gradle bootstrap、手書き facade 指定を不要にする。

## 命名と商標方針

- package/brand は `dotkt` とする。
- toolchain package は `dotkt.toolchain` とする。
- `Kotlin` を package ID や製品名に含めない。
- 説明文では `Kotlin(R)` を対象言語として参照する。
- README / NuGet description には非提携 disclaimer を置く。

例:

```text
dotkt is an experimental compiler toolchain for running Kotlin(R) programs on .NET CLR.
Kotlin is a trademark of the Kotlin Foundation. dotkt is not affiliated with or endorsed by JetBrains, Google, or the Kotlin Foundation.
```

## Package 構成

初版は単一 package にまとめる。

```text
dotkt.toolchain.nupkg
  buildTransitive/
    dotkt.toolchain.props
    dotkt.toolchain.targets
  tools/
    compiler/
      compiler.jar
      lib/*.jar
    ilemit/
      ilemit.dll
      ilemit.deps.json
      ilemit.runtimeconfig.json
      ...
    facadegen/
      facadegen.dll
      facadegen.deps.json
      facadegen.runtimeconfig.json
      ...
  runtime/
    kotlin/
      kotlin-stdlib-2.2.0.jar
```

Notes:

- `buildTransitive` を使い、利用側 project で `<Import>` を書かせない。
- Gradle は package 作成時だけ使う。利用者 build では一切呼ばない。
- `ilemit` / `facadegen` は package 作成時に framework-dependent publish 済みの成果物を入れる。
- Kotlin stdlib jar は package に同梱し、利用者環境の Gradle cache に依存しない。
- JRE/JDK は package に同梱しない。必要な Java runtime は利用者環境で用意してもらう。
- compiler は NuGet 内の jar を `java -jar` または明示 classpath で起動し、Gradle installDist の launcher script には依存しない。
- third-party dependency の license / notice を package に同梱する。

## Runtime / Tool 配布方針

`ilemit` と `facadegen` は framework-dependent publish とする。

理由:

- この package は MSBuild / .NET SDK から使われるため、利用者環境には .NET runtime がある前提でよい。
- self-contained publish は package size が大きくなりすぎる。
- `dotkt.toolchain` の初版では配布サイズと単純さを優先する。

Java runtime は同梱しない。

理由:

- compiler は Kotlin/JVM compiler embeddable に依存するため Java runtime が必要だが、JRE/JDK まで同梱すると NuGet package が過大になる。
- Java runtime は OS/package manager/CI image で用意する方が自然。
- package 側は Java が見つからない場合に明確な診断を出す。

前提:

- 利用者は .NET SDK 10 を持っている。
- 利用者は Java runtime を持っている。
- 利用者は Gradle を持っている必要はない。

## Compiler 起動方針

NuGet 版では Gradle installDist の launcher script に依存しない。

方針:

- package 内に compiler 実行用 jar を置く。
- MSBuild target は `java -jar "$(DotKtCompilerJar)" ...` のように起動する。
- もし Kotlin compiler embeddable など依存 jar を外部 lib として置く必要がある場合は、`java -cp "<compiler.jar>;lib/*" clrc.MainKt ...` にする。
- Linux/macOS の executable bit や Windows `.bat` launcher の差異を避ける。

Open implementation detail:

- `compiler.jar` を fat jar にするか、thin jar + `lib/*` classpath にするか。
- まずは配布の確実性を優先し、`java -jar` で起動できる形を目指す。

## Third-party notices

NuGet package に Kotlin stdlib jar と Kotlin compiler embeddable 由来の artifacts を同梱するため、license / notice を明示的に含める。

必須:

- `LICENSE` または package 自体の license expression / file
- `THIRD-PARTY-NOTICES.md`
- Kotlin stdlib / Kotlin compiler embeddable / Gradle 経由で bundle される依存 jar の license attribution

NuGet metadata でも license 情報を設定する。

## Public MSBuild API

公開面はできるだけ小さくする。

通常不要:

- `DotKtBackend`: 公開しない。NuGet 版は IL backend 固定。
- `StartupObject`: 通常不要。自動検出する。
- `<Import>`: 不要。NuGet `buildTransitive` が行う。
- C# backend: NuGet 版には含めない。公開 API にも escape hatch にも出さない。

許容する escape hatch:

```xml
<PropertyGroup>
  <DotKtStartupObject>MainKt</DotKtStartupObject>
  <DotKtDisableAutoImports>true</DotKtDisableAutoImports>
  <DotKtCompilerArgs>...</DotKtCompilerArgs>
</PropertyGroup>

<ItemGroup>
  <DotKtImport Include="System.Text.StringBuilder" />
</ItemGroup>
```

互換目的で短期的に alias してもよい内部/旧名:

- `KotlinClrType` -> `DotKtImport`
- `KotlinClrFacade` -> deprecated / internal fallback

含めないもの:

- `KotlinClrBackend`
- `KOTLIN_CLR_EMIT_CS`
- C# backend artifacts
- generated `.cs` 経路

C# backend はリポジトリ開発用の過去の参照実装として扱い、`dotkt.toolchain` の配布物からは忘れる。

## StartupObject 自動化

利用者に `AppKt` などの file class 名を書かせない。

短期実装:

- `@(KotlinCompile)` を scan して `fun main(` を探す。
- `main` が 1 つなら、その source file 名から `App.kt` -> `AppKt` を推定する。
- `main` が複数なら明示的な `<DotKtStartupObject>` を要求する。
- `<DotKtStartupObject>` が指定された場合はそれを優先する。

最終実装:

- compiler / BIR 側が entry point 情報を出す。
- `ilemit` が BIR の entry point 情報から `SetEntryPoint` する。
- MSBuild 側の source scan は不要にする。

## .NET 型 import / facade 指定の廃止

`<KotlinClrFacade>` / `<KotlinClrType>` は内部事情が漏れているため、公開 API から消す。

段階:

1. `<DotKtImport Include="System.Text.StringBuilder" />` に統合する。
   - 利用者は「CLR 型を import する」だけを書く。
   - 内部で FIR injection / facade generation のどちらを使うかを toolchain が選ぶ。
2. `.kt` source の `import System.Text.StringBuilder` を MSBuild target で scan し、自動で `DotKtImport` 相当を生成する。
   - 初期対応は explicit type import のみに限定する。
   - wildcard import, alias import, nested type, 同名型は後回し。
3. 最終的には frontend に CLR symbol provider を入れ、参照 assembly からオンデマンド解決する。

初版の方針:

- auto import scan は初版スコープに入れる。
- 初期対応は explicit type import のみに限定する。
- 逃げ道として `<DotKtImport>` は残す。
- `Facade` という単語は公開 docs から消す。

初版で対応する import:

```kotlin
import System.Text.StringBuilder
import System.Collections.ObjectModel.ObservableCollection
```

初版で対応しない import:

```kotlin
import System.Text.*
import System.Text.StringBuilder as Sb
import Some.Namespace.Outer.Inner
```

unsupported な import は無理に解決しない。必要なら `<DotKtImport Include="..." />` を使う。

## Build/pack pipeline

package 作成時:

1. `./gradlew :compiler:installDist`
2. `dotnet publish tools/ilemit -c Release -o artifacts/tools/ilemit`
3. `dotnet publish tools/facadegen -c Release -o artifacts/tools/facadegen`
4. `kotlin-stdlib-2.2.0.jar` を固定位置へコピー
5. `dotkt.toolchain.nupkg` を作成
6. clean な一時 directory で local NuGet source から restore
7. Gradle なし / repo checkout なしで `dotnet run` smoke test

利用者 build で禁止:

- `gradlew`
- `dotnet build tools/ilemit`
- `dotnet build tools/facadegen`
- `~/.gradle/caches` 探索
- repo-local relative import

## Smoke Test 受け入れ条件

clean environment に近い一時 directory で:

```bash
mkdir /tmp/dotkt-smoke
cd /tmp/dotkt-smoke
dotnet new console
# csproj を dotkt project に変更し、Program.cs を消して App.kt を置く
dotnet add package dotkt.toolchain --source /path/to/local-nuget
dotnet run
```

期待:

- `dotnet restore` が NuGet package を取得する。
- `dotnet run` が Kotlin source を compile して CIL assembly を生成する。
- Gradle が PATH に無くても成功する。
- repository checkout が無くても成功する。
- `bin/` / `obj/` 以外に利用者 project を汚さない。

最小 `App.kt`:

```kotlin
fun main() {
    println("hello from dotkt")
}
```

interop smoke:

```kotlin
import System.Text.StringBuilder

fun main() {
    val sb = StringBuilder()
    sb.Append("hello")
    println(sb.ToString())
}
```

## タスク一覧

### Phase 1: Package skeleton

- [ ] `packaging/dotkt.toolchain/` など package 作成用 directory を決める。
- [ ] `buildTransitive/dotkt.toolchain.props` を用意する。
- [ ] `buildTransitive/dotkt.toolchain.targets` を用意する。
- [ ] package root 解決を `$(MSBuildThisFileDirectory)` 起点に変更する。
- [ ] tool paths を package 内 `tools/` / `runtime/` に向ける。
- [ ] package metadata に trademark disclaimer を入れる。
- [ ] package ID を `dotkt.toolchain` に固定する。
- [ ] `THIRD-PARTY-NOTICES.md` を package に含める。

### Phase 2: Remove consumer-time bootstrap

- [ ] NuGet 版 targets から `gradlew :compiler:installDist` を消す。
- [ ] NuGet 版 targets から `dotnet build tools/ilemit` を消す。
- [ ] NuGet 版 targets から `dotnet build tools/facadegen` を消す。
- [ ] compiler jar が無ければ明確な package corruption error にする。
- [ ] compiler を launcher script ではなく `java -jar` または explicit classpath で起動する。
- [ ] `KotlinStdlib` を package 同梱 jar に固定する。

### Phase 3: Public API cleanup

- [ ] `DotKt*` property/item names を導入する。
- [ ] `DotKtBackend` は公開しない。内部は IL 固定にする。
- [ ] C# backend は NuGet package に含めない。
- [ ] C# backend 用 property / env var / docs を NuGet 公開面に出さない。
- [ ] `<DotKtImport>` を導入する。
- [ ] 旧 `<KotlinClrType>` / `<KotlinClrFacade>` は互換 alias または deprecated warning にする。

### Phase 4: StartupObject removal

- [ ] `<DotKtStartupObject>` を導入する。
- [ ] 指定が無い場合に `fun main` を scan する。
- [ ] `main` が 1 件なら file class 名を自動推定する。
- [ ] `main` が 0 件で `OutputType=Exe` なら明確に error にする。
- [ ] `main` が複数なら `<DotKtStartupObject>` を要求する。
- [ ] 将来 compiler/BIR entry point metadata に置き換える TODO を残す。

### Phase 5: Auto CLR imports

- [ ] `.kt` source の explicit import を scan する target/tool を作る。
- [ ] `System.Text.StringBuilder` 形式の type import を抽出する。
- [ ] 抽出結果と `<DotKtImport>` を merge する。
- [ ] `facadegen --meta --refs ...` へ渡す。
- [ ] wildcard / alias / nested type は初版では自動解決しない。
- [ ] auto import scan で拾えない型の逃げ道として `<DotKtImport>` を維持する。
- [ ] docs では `<DotKtImport>` を逃げ道として説明する。

### Phase 6: Pack automation

- [ ] compiler jar と必要依存 jar を artifact directory にコピーする。
- [ ] `ilemit` を framework-dependent publish して artifact directory にコピーする。
- [ ] `facadegen` を framework-dependent publish して artifact directory にコピーする。
- [ ] Kotlin stdlib jar を artifact directory にコピーする。
- [ ] `THIRD-PARTY-NOTICES.md` を生成または手動更新して artifact directory に含める。
- [ ] `.nupkg` を作成する script / CI job を作る。
- [ ] local NuGet source から restore する smoke test を作る。
- [ ] smoke test で Gradle/repo checkout 非依存を検証する。

### Phase 7: Templates

- [ ] `dotkt.templates` を別 package として設計する。
- [ ] `dotnet new dotkt -n Hello` を提供する。
- [ ] template は `dotkt.toolchain` PackageReference だけを含む。
- [ ] sample `App.kt` は `fun main()` のみで動くようにする。

## Open questions

- [x] tools は framework-dependent publish で十分か、self-contained にするか。
  - 決定: framework-dependent publish とする。MSBuild できる環境には .NET runtime がある前提でよい。
- [x] package に JRE/JDK は含めない方針でよいか。
  - 決定: 含めない。package size を抑え、Java runtime は利用者環境で用意してもらう。
- [x] compiler launcher script の cross-platform path / executable bit を NuGet でどう扱うか。
  - 決定: launcher script には依存しない。`java -jar` を第一候補にし、必要なら explicit classpath で `clrc.MainKt` を起動する。
- [x] `kotlin-stdlib-2.2.0.jar` の再配布ライセンス表記を package にどう含めるか。
  - 決定: `THIRD-PARTY-NOTICES.md` を必須で同梱する。
- [x] package ID の casing は `dotkt.toolchain` で固定するか。
  - 決定: `dotkt.toolchain` で固定する。
- [x] 初版で auto import scan まで入れるか、まず `<DotKtImport>` のみにするか。
  - 決定: auto import scan は初版スコープに入れる。対象は explicit type import のみ。`<DotKtImport>` は逃げ道として残す。
- [x] NuGet package に C# backend artifacts を含めるか、完全に repo 開発用に限定するか。
  - 決定: NuGet package には含めない。C# backend は過去の開発用 backend であり、`dotkt.toolchain` は IL backend のみを配布する。

## 初版 Definition of Done

- [ ] 利用者 project には `PackageReference Include="dotkt.toolchain"` だけが必要。
- [ ] `<Import>` 不要。
- [ ] `<StartupObject>` 不要。
- [ ] backend 選択不要。
- [ ] C# backend が NuGet package に含まれていない。
- [ ] Gradle 不要。
- [ ] repo checkout 不要。
- [ ] `fun main()` だけの `App.kt` が `dotnet run` で動く。
- [ ] explicit import の BCL interop smoke が auto import scan だけで動く。
- [ ] auto import scan で拾えない型は `<DotKtImport>` で明示できる。
- [ ] package 内に必要な toolchain artifacts が全て含まれる。
- [ ] package description に Kotlin trademark disclaimer がある。
