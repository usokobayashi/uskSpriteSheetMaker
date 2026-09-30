# uskSpriteSheetMaker

連番画像からスプライトシート（PNG / TGA）やアニメーションGIFを作成するWindows用ツールです。キャラクターの移動・ジャンプや、エフェクトの動きをプレビューできます。

**English:** A Windows tool for creating sprite sheets (PNG / TGA) and animated GIFs from image sequences, with character and effect previews. [Download](../../releases)

## ダウンロード・使い方

1. [Releases](../../releases) から `uskSpriteSheetMaker.exe` をダウンロードします。インストールは不要です。
2. 起動したウィンドウに、画像またはフォルダをドラッグ＆ドロップします。
3. 右上の PNG / TGA / GIF ボタンで書き出します。

| 1. 起動 | 2. 画像を追加 |
| --- | --- |
| ![起動直後の画面](docs/images/guide/01_start.png) | ![画像を追加した画面](docs/images/guide/02_dropped.png) |

| 3. 書き出し | 出力例 |
| --- | --- |
| ![書き出し完了の画面](docs/images/guide/04_exported.png) | ![スプライトシートの出力例](docs/images/guide/05_result.png) |

## 主な機能

### シート作成・書き出し

- PNG / JPG / BMP画像を読み込み、ファイル名順に配置します（`frame2` → `frame10` の順）。フォルダごとに新しい行から始まります。
- 横セル数と縮小率（等倍・1/2・1/4・1/8）を指定できます。画像サイズが異なる場合は、列ごとの最大幅・行ごとの最大高さに揃えます。
- 画像の並べ替え、フォルダ間の移動、黒背景の透過、色の調整（乗算・加算）に対応しています。
- セル番号とグリッドを表示します。書き出す画像に番号を付けるかどうかも選べます。
- PNG / TGAはシート全体、GIFは開始〜終了セルのアニメーションを書き出します。書き出しは `Esc` で中止できます。

### アニメーションのプレビュー

| モード | 内容 |
| --- | --- |
| デフォルト | 開始〜終了セルとFPSを指定して再生 |
| キャラクター | 待機・移動・ジャンプ・攻撃にセル範囲を割り当て、キー操作で確認 |
| エフェクト | 方向・速度・FPSを指定して再生 |

キャラクターの初期操作は `A` / `D`（左右）、`W` / `S`（上下）、`Space`（ジャンプ）、`G` / `R`（攻撃1・2）です。キーは変更できます。移動速度・ジャンプ力・重力・接地位置を調整でき、地形や足場を使った確認にも対応しています。

### 保存・メモ・言語

- 設定・画像・メモを `.smproj` ファイルにまとめて保存し、別のPCへ渡せます。[Releases](../../releases) にはサンプルプロジェクトもあります。
- シートの外側を右クリックすると、メモを追加できます。
- 設定タブで日本語 / English / 简体中文 / Bahasa Indonesiaを切り替えられます。

日本語以外はAIによる翻訳で、母語話者による確認は未実施です。翻訳の修正は [Issues](../../issues) またはPull Requestで受け付けています（対象：`Localization/*.lang`）。

## 主な操作

| 操作 | 入力 |
| --- | --- |
| 元に戻す / やり直し | `Ctrl+Z` / `Ctrl+Y` |
| 保存 / 開く / 名前を付けて保存 | `Ctrl+S` / `Ctrl+O` / `Ctrl+Shift+S` |
| 削除 | `Delete` |
| 順序を上下 | `Ctrl+↑` / `Ctrl+↓` |
| 再生・停止 | `Space`（デフォルト・エフェクト） |
| シートの拡大縮小 / 移動 | ホイール / 左または中ボタンでドラッグ |
| 全体表示 | `F` / `0` / ダブルクリック |
| セルの選択 / 範囲選択 | `Shift`＋クリック / `Shift`＋ドラッグ |
| 直前の選択から範囲を追加 | `Ctrl`＋`Shift`＋クリック |
| 書き出し・割り当ての中止 | `Esc` |

セルを状態に割り当てるには、状態遷移タブでセル範囲を選択し、右クリック →「割り当て」→ 対象の状態の行をクリックします。

## 動作環境・注意点

- Windows 10 / 11、.NET Framework 4.8が必要です。Windows 10での動作確認は未実施です。
- 実行ファイルは未署名のため、WindowsのSmartScreenが警告を表示する場合があります。
- TGAは書き出し専用です。TGA・GIFの幅と高さは、それぞれ最大65,535 pxです。
- GIFは256色の固定パレットを使用します。半透明は透明または不透明になります。
- 大きな画像の処理には時間とメモリが必要です。読み込み上限を超える画像は、縮小してから追加してください。
- 横セル数を変更すると、状態に割り当てたセル番号はそのままで、対応する画像が変わります。変更後に割り当てを確認してください。
- マウスによる範囲選択、割り当て中のクリック、日本語入力（IME）中のショートカット、高DPI環境での表示は、実機での確認が十分ではありません。

アプリは通信を行わず、元の画像ファイルも変更しません。

設定は `%LOCALAPPDATA%\uskSpriteSheetMaker\settings.ini`、エラーログは同じフォルダの `error.log` に保存します。

## ビルド・テスト

.NET Framework 4.8の開発者パックが必要です。

```powershell
dotnet build SpriteSheetMaker.csproj -c Release
dotnet build Tests/SpriteSheetMakerTests.csproj -c Release
.\Tests\bin\x86\Release\SpriteSheetMakerTests.exe
```

開発履歴は [docs/DEVELOPMENT_HISTORY.md](docs/DEVELOPMENT_HISTORY.md)、翻訳ファイルの書式は [Localization/README.md](Localization/README.md) を参照してください。

## ライセンス

- ソースコード：[MIT License](LICENSE)
- 同梱フォント（LINE Seed JP）：[SIL Open Font License 1.1](docs/third_party_licenses/LINE_Seed_JP_OFL.txt)
