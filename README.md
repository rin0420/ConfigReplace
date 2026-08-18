# ConfigReplace

フォルダー構成をプロファイル単位で切り替える、Windows向けの小型ツールです。

## 使い方

1. ［新規］を押す
2. 配置先を指定する
3. 配置するフォルダーをエクスプローラーからドロップする
4. ［保存］後、プレビューして切り替える

取り込んだフォルダーは`Profiles`内へ保存され、元フォルダーには依存しません。

## 動作環境

- Windows 10 / 11
- .NET 10
- Windows Forms

## ビルド

```powershell
dotnet test .\ConfigReplace.slnx -c Release
dotnet publish .\ConfigReplace.App\ConfigReplace.App.csproj -c Release -r win-x64 --self-contained true -o .\publish
```
