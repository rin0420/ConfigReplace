# ConfigReplace

フォルダー構成をプロファイル単位で切り替える、Windows向けの小型ツールです。

## 配布

[ConfigReplace.zipをダウンロード](https://github.com/rin0420/ConfigReplace/releases/download/v1.2.0/ConfigReplace.zip)

## 使い方

1. ［新規］を押す
2. プロファイル名と配置先を指定する
3. 配置するフォルダーをエクスプローラーからドロップする
4. ［保存］後、プレビューして切り替える

プロファイル名は作成時・編集時に変更できます。名前を変更しても保存済みスナップショットはProfiles内で引き継がれます。

ドラッグ＆ドロップ時はパスだけを登録するため、大きなフォルダーでも画面操作をすぐに続けられます。［保存］を押すと、EXEと同じ階層の`Profiles\[プロファイル名]`へ一度だけコピーされ、以後の切替はその保存内容を配置先へ展開します。元フォルダーには依存しません。

［切替履歴・復元］で履歴を選択し、［ファイル差分...］を押すと、履歴作成時のバックアップと現在の配置先をWinMergeのように比較できます。フォルダーを選び、追加・削除・変更されたファイルを選択すると左右に内容を表示します。履歴行をダブルクリックして開くこともできます。

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
