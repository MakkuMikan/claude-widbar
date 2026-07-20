# System Widget for WidBar

Windows のタスクバーに CPU、RAM、GPU、VRAM、Claude Code、Codex のローカル使用状況を表示する [WidBar](https://github.com/andelby/widbar) ウィジェットです。

このディレクトリは **ソースコードのみ**を公開する想定です。MSIXバイナリ、証明書、認証情報、利用ログは含みません。

## 表示とデータの扱い

すべての計測はPC内で行い、ウィジェット自身はデータを外部送信しません。

| 項目 | 取得元 | 補足 |
| --- | --- | --- |
| CPU / RAM | WindowsシステムAPI | 1秒ごとに更新 |
| GPU / VRAM | Windows PDH / DXGI | 対応ドライバーが必要 |
| Claude Code | `~/.claude/projects` のローカルJSONL | ローカルログからの推定。公式利用枠ではありません |
| Codex | `~/.codex/sessions` のローカルJSONL | Codexが記録した `rate_limits` を表示 |

Claude Code と Codex のログにアクセスできない、または該当データがない場合は `--` を表示します。

## 必要環境

- Windows 11（x64 または ARM64）
- [WidBar](https://github.com/andelby/widbar)
- Visual Studio 2022（Desktop development with .NET、Windows App SDK とWindows SDK）
- .NET 8 SDK

## ローカルでビルド・登録する

1. このディレクトリを取得し、`SystemWidget.WidBar.sln` を Visual Studio 2022 で開きます。
2. `Debug | x64` を選択し、`SystemWidget.WidBar (Package)` をビルドします。
3. 開発用のルーズレイアウトを登録します。管理者PowerShellは不要ですが、Windowsの開発者モードが必要になる場合があります。

```powershell
Add-AppxPackage -Register ".\SystemWidget.WidBar (Package)\bin\x64\Debug\AppxManifest.xml"
```

4. WidBar を起動し、カタログから **System Widget** をタスクバーに追加します。

Visual StudioでのDeploy、またはMSIXをローカル配布する場合は、Windowsが信頼する署名が必要です。ソースをGitHubで公開するだけなら署名やMicrosoft Store登録は不要です。開発用の自己署名証明書の扱いは、Microsoftの[MSIX署名ガイド](https://learn.microsoft.com/windows/msix/package/create-certificate-package-signing)を参照してください。

## 開発時の検証

パッケージプロジェクトをビルドした後、WidBarを再起動して値を実画面で確認してください。ウィジェット実行中に `Rebuild` すると出力ファイルがロックされるため、その場合はWidBarを終了してからビルドし、完了後に起動し直します。GPU/VRAMや利用量の修正では、既存のローカル収集値とWidBar表示値を同時点で照合します。

```powershell
& "C:\Program Files\Microsoft Visual Studio\2022\Community\Msbuild\Current\Bin\amd64\MSBuild.exe" `
  "SystemWidget.WidBar (Package)\SystemWidget.WidBar (Package).wapproj" `
  /p:Configuration=Debug /p:Platform=x64 /restore
```

パッケージプロジェクトは、WidBar SDKが生成する `plugin.json` をビルドごとにパッケージの `Public/` へ同期します。IDや表示名を変更する場合は、手編集した `plugin.json` を置かず、`SystemWidget.WidBar.ExtensionApp.csproj` の `WidBarPlugin*` プロパティを変更してください。

## プライバシーとセキュリティ

- 認証トークンや資格情報を送信・保存しません。
- Claude Code / Codex のローカルログは読み取り専用です。
- ローカルビルドで生成されるMSIX、証明書、`bin/`、`obj/` はGit管理に含めません。

## ライセンス

本ソースは [GPL-3.0-only](LICENSE) です。直接依存するNuGetパッケージのライセンスは [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) を参照してください。

## 現在の配布方針

GitHubでのソース公開を対象としています。署名済みMSIXの公開配布、Microsoft Store掲載、商用コード署名証明書の取得は、このリリースには含めません。
