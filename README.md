# ConfigReplace

プロファイルに登録したフォルダーを配置先へ上書きする、Windows向けの小型ツールです。

## 配布

[ConfigReplace.zipをダウンロード](https://github.com/rin0420/ConfigReplace/releases/download/v1.3.0/ConfigReplace.zip)

## 使い方

1. ［新規］を押す
2. プロファイル名と配置先を指定する
3. 配置するフォルダーをエクスプローラーからドロップする
4. ［保存］後、内容を確認して上書きする

プロファイル名は作成時・編集時に変更できます。プロファイル内のフォルダー名は重複できません。

ドラッグ＆ドロップしたフォルダーは、［保存］時にEXEと同じ階層の`Profiles\[プロファイル名]\[フォルダー名]`へコピーされます。以後の上書きはこの保存内容を使うため、元フォルダーには依存しません。

［上書き実行］は、プロファイルに登録されたフォルダーの内容を配置先へコピーします。同じ相対パスのファイルは上書きされますが、配置先にしかないファイルやフォルダーは削除されません。履歴・バックアップ・詳細な差分表示は作成しません。

## 動作環境

- Windows 10 / 11
- .NET 10
- Windows Forms

## ビルド

```powershell
dotnet build .\ConfigReplace.slnx -c Release
dotnet publish .\src\ConfigReplace.App\ConfigReplace.App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o .\dist\ConfigReplace
dotnet test .\ConfigReplace.slnx -c Release
```
