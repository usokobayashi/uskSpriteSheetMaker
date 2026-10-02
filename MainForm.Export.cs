using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SpriteSheetMaker
{
    // PNG / TGA / GIF の書き出し。シート全体を1枚のビットマップにせず、帯（数行ぶん）ごとに作って
    // ファイルへ流し込むので、GDI+ の約1GBの上限を超える大きさでも書き出せる。
    // 画像はその帯に必要になったときに1枚ずつ読み込み、読み込んだものは上限つきで使い回す。
    // 実行は別スレッドで行い、進み具合を状態欄に出す。Esc で中止できる。
    public sealed partial class MainForm
    {
        private const long ExportImageCacheBytes = 256L * 1024 * 1024;

        private CancellationTokenSource exportCancellation;
        private Task exportWorker;
        private bool exportBusy;
        private int lastExportProgressTick;

        // 書き出しの設定を、その時点の画面の状態からまとめて控える。書き出し中に画面を操作しても影響しない。
        private sealed class ExportSnapshot
        {
            public List<LoadFolderRequest> Request;
            public int Divisor;
            public int Columns;
            public int StartCell;
            public int EndCell;
            public int Fps;
            public bool ConvertBlack;
            public bool Numbers;
            public SpriteColorBlendMode BlendMode;
            public Color AdjustmentColor;
            public int AdjustmentStrength;
        }

        private ExportSnapshot CaptureExportSnapshot()
        {
            return new ExportSnapshot
            {
                Request = CaptureLoadRequest(),
                Divisor = GetScaleDivisor(),
                Columns = Math.Max(1, (int)columnsBox.Value),
                StartCell = (int)startCellBox.Value,
                EndCell = (int)endCellBox.Value,
                Fps = Math.Max(1, (int)fpsBox.Value),
                ConvertBlack = blackTransparencyCheckBox.Checked,
                Numbers = gridNumberCheckBox.Checked,
                BlendMode = colorBlendMode,
                AdjustmentColor = spriteAdjustmentColor,
                AdjustmentStrength = spriteAdjustmentStrength
            };
        }

        private SheetLayout BuildExportLayout(ExportSnapshot snapshot)
        {
            return BuildLayout(imagePipeline.LoadSizes(snapshot.Request, snapshot.Divisor), snapshot.Columns);
        }

        private static Bitmap LoadPlacementBitmap(FramePlacement placement, ExportSnapshot snapshot, CancellationToken cancellationToken)
        {
            return SpriteImagePipeline.LoadOne(placement.Source.Path, snapshot.Divisor, snapshot.ConvertBlack,
                snapshot.BlendMode, snapshot.AdjustmentColor, snapshot.AdjustmentStrength, cancellationToken);
        }

        // シートの y0 から height 行ぶんを描いた帯（幅はシート全体）。
        private static Bitmap RenderSheetBand(SheetLayout layout, ExportSnapshot snapshot, BitmapLruCache cache,
            int y0, int height, CancellationToken cancellationToken)
        {
            var band = new Bitmap(layout.Width, height, PixelFormat.Format32bppArgb);
            try
            {
                using (Graphics g = Graphics.FromImage(band))
                {
                    g.Clear(Color.Transparent);
                    g.InterpolationMode = InterpolationMode.NearestNeighbor;
                    g.PixelOffsetMode = PixelOffsetMode.Half;
                    g.CompositingMode = CompositingMode.SourceOver;
                    g.TranslateTransform(0, -y0);

                    int y1 = y0 + height;
                    for (int i = 0; i < layout.Placements.Count; i++)
                    {
                        FramePlacement placement = layout.Placements[i];
                        if (placement.Rect.Bottom <= y0 || placement.Rect.Top >= y1) continue;
                        cancellationToken.ThrowIfCancellationRequested();
                        FramePlacement current = placement;
                        Bitmap bitmap = placement.Bitmap ?? cache.Get(i, () => LoadPlacementBitmap(current, snapshot, cancellationToken));
                        g.DrawImageUnscaled(bitmap, placement.Rect.X, placement.Rect.Y);
                    }

                    if (snapshot.Numbers)
                        DrawGridNumbers(g, layout.Cells.Where(c => c.Rect.Bottom > y0 && c.Rect.Top < y1));
                }
                return band;
            }
            catch
            {
                band.Dispose();
                throw;
            }
        }

        // 別スレッドから呼べる。sheet 全体（PNG/TGA）を path へ書き出す。
        private void ExportSheetFile(ImageOutputFormat format, string path, ExportSnapshot snapshot,
            Action<double> progress, CancellationToken cancellationToken)
        {
            SheetLayout layout = BuildExportLayout(snapshot);
            if (format == ImageOutputFormat.Tga && (layout.Width > ushort.MaxValue || layout.Height > ushort.MaxValue))
                throw new InvalidOperationException(Loc.T("error.tgaTooLarge", layout.Width, layout.Height));

            using (var cache = new BitmapLruCache(ExportImageCacheBytes))
            {
                SheetBandRenderer render = (y0, h) => RenderSheetBand(layout, snapshot, cache, y0, h, cancellationToken);
                if (format == ImageOutputFormat.Png)
                    PngStreamWriter.Save(path, layout.Width, layout.Height, render, progress, cancellationToken);
                else
                    TgaStreamWriter.Save(path, layout.Width, layout.Height, render, progress, cancellationToken);
            }
        }

        // 別スレッドから呼べる。開始〜終了セルのコマをGIF/WebPアニメーションへ書き出す。
        // 両形式ともコマの集め方・描き方は同じで、最後に呼ぶエンコーダーだけが違う。
        private void ExportAnimationFile(ImageOutputFormat format, string path, ExportSnapshot snapshot,
            Action<double> progress, CancellationToken cancellationToken)
        {
            SheetLayout layout = BuildExportLayout(snapshot);
            var selected = layout.Placements
                .Where(p => p.CellNumber >= snapshot.StartCell && p.CellNumber <= snapshot.EndCell)
                .ToList();
            if (selected.Count == 0)
                throw new InvalidOperationException(Loc.T("message.noAnimationFrames", format == ImageOutputFormat.WebP ? "WebP" : "GIF"));

            // 各フレームは同じ大きさである必要があるため、最大サイズに揃える。
            int frameWidth = selected.Max(p => p.CellRect.Width);
            int frameHeight = selected.Max(p => p.CellRect.Height);
            // GIFは規格上65,535px、WebPは16,384pxまで。
            if (format == ImageOutputFormat.Gif && (frameWidth > ushort.MaxValue || frameHeight > ushort.MaxValue))
                throw new InvalidOperationException(Loc.T("error.gifTooLarge", frameWidth, frameHeight));
            if (format == ImageOutputFormat.WebP && (frameWidth > WebPWriter.MaxDimension || frameHeight > WebPWriter.MaxDimension))
                throw new InvalidOperationException(Loc.T("error.webpTooLarge", frameWidth, frameHeight));

            Func<int, Bitmap> getFrame = index =>
            {
                FramePlacement placement = selected[index];
                var frame = new Bitmap(frameWidth, frameHeight, PixelFormat.Format32bppArgb);
                try
                {
                    using (Bitmap source = LoadPlacementBitmap(placement, snapshot, cancellationToken))
                    using (Graphics g = Graphics.FromImage(frame))
                    {
                        g.Clear(Color.Transparent);
                        g.InterpolationMode = InterpolationMode.NearestNeighbor;
                        g.PixelOffsetMode = PixelOffsetMode.Half;
                        g.DrawImageUnscaled(source, 0, 0);
                        if (snapshot.Numbers)
                        {
                            DrawGridNumbers(g, new List<LayoutCell>
                            {
                                new LayoutCell { Rect = new Rectangle(0, 0, frame.Width, frame.Height), CellNumber = placement.CellNumber }
                            });
                        }
                    }
                    return frame;
                }
                catch
                {
                    frame.Dispose();
                    throw;
                }
            };

            int delayMs = Math.Max(1, 1000 / snapshot.Fps);
            if (format == ImageOutputFormat.WebP)
                WebPWriter.SaveAnimatedWebP(path, frameWidth, frameHeight, selected.Count, getFrame, true, delayMs, progress, cancellationToken);
            else
                GifWriter.SaveAnimatedGif(path, frameWidth, frameHeight, selected.Count, getFrame, true, delayMs, progress, cancellationToken);
        }

        private void ExportGifFile(string path, ExportSnapshot snapshot, Action<double> progress, CancellationToken cancellationToken)
        {
            ExportAnimationFile(ImageOutputFormat.Gif, path, snapshot, progress, cancellationToken);
        }

        private void ExportWebPFile(string path, ExportSnapshot snapshot, Action<double> progress, CancellationToken cancellationToken)
        {
            ExportAnimationFile(ImageOutputFormat.WebP, path, snapshot, progress, cancellationToken);
        }

        // 画面の状態そのままで書き出す（同期）。テストやスクリプトから使う。
        private void ExportToFile(ImageOutputFormat format, string path)
        {
            ExportSnapshot snapshot = CaptureExportSnapshot();
            if (format == ImageOutputFormat.Gif || format == ImageOutputFormat.WebP)
                ExportAnimationFile(format, path, snapshot, null, CancellationToken.None);
            else
                ExportSheetFile(format, path, snapshot, null, CancellationToken.None);
        }

        private void ExportGif(string path)
        {
            ExportToFile(ImageOutputFormat.Gif, path);
            statusLabel.Text = Loc.T("message.exported", "GIF", Path.GetFileName(path));
        }

        private void ExportWebP(string path)
        {
            ExportToFile(ImageOutputFormat.WebP, path);
            statusLabel.Text = Loc.T("message.exported", "WebP", Path.GetFileName(path));
        }

        // シート全体を1枚のビットマップとして作る。小さなシート（テストや確認用）向けで、
        // 書き出し自体は帯ごとの経路（ExportSheetFile）を使う。
        private Bitmap BuildSpriteSheet()
        {
            ExportSnapshot snapshot = CaptureExportSnapshot();
            SheetLayout layout = BuildExportLayout(snapshot);
            if ((long)layout.Width * layout.Height > 268435456L || layout.Width > 32767 || layout.Height > 32767)
                throw new InvalidOperationException(Loc.T("error.layoutTooLarge", layout.Width, layout.Height));
            using (var cache = new BitmapLruCache(ExportImageCacheBytes))
                return RenderSheetBand(layout, snapshot, cache, 0, layout.Height, CancellationToken.None);
        }

        //--------------
        // 画面まわり（別スレッド実行・進み具合・中止）
        //--------------
        private void Export(ImageOutputFormat format)
        {
            if (GetAllItems().Count == 0)
            {
                ShowDarkNotice(Loc.T("dialog.noImagesTitle"), Loc.T("message.noImages"));
                return;
            }
            if (exportBusy) return;

            string path;
            using (var dialog = new SaveFileDialog())
            {
                if (format == ImageOutputFormat.Png)
                {
                    dialog.Filter = Loc.T("filter.formatImage", "PNG") + " (*.png)|*.png";
                    dialog.DefaultExt = "png";
                    dialog.FileName = "spritesheet.png";
                }
                else if (format == ImageOutputFormat.Tga)
                {
                    dialog.Filter = Loc.T("filter.formatImage", "TGA") + " (*.tga)|*.tga";
                    dialog.DefaultExt = "tga";
                    dialog.FileName = "spritesheet.tga";
                }
                else if (format == ImageOutputFormat.Gif)
                {
                    dialog.Filter = Loc.T("filter.formatImage", "GIF") + " (*.gif)|*.gif";
                    dialog.DefaultExt = "gif";
                    dialog.FileName = "animation.gif";
                }
                else
                {
                    dialog.Filter = Loc.T("filter.animatedWebP") + " (*.webp)|*.webp";
                    dialog.DefaultExt = "webp";
                    dialog.FileName = "animation.webp";
                }

                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                path = dialog.FileName;
            }

            RunExport(format, path);
        }

        private async void RunExport(ImageOutputFormat format, string path)
        {
            string label = format == ImageOutputFormat.WebP ? "WebP" : format.ToString().ToUpperInvariant();
            ExportSnapshot snapshot = CaptureExportSnapshot();
            exportCancellation = new CancellationTokenSource();
            CancellationToken token = exportCancellation.Token;
            SetExportBusy(true);
            StartExportBar();
            statusLabel.Text = Loc.T("message.exporting", label, 0);

            bool canceled = false;
            Exception failure = null;
            try
            {
                Action<double> progress = fraction => ReportExportProgress(label, fraction);
                exportWorker = Task.Run(() =>
                {
                    if (format == ImageOutputFormat.Gif || format == ImageOutputFormat.WebP)
                        ExportAnimationFile(format, path, snapshot, progress, token);
                    else
                        ExportSheetFile(format, path, snapshot, progress, token);
                });
                await exportWorker;
            }
            catch (OperationCanceledException)
            {
                canceled = true;
            }
            catch (Exception ex)
            {
                failure = ex;
            }

            // 継続がUIスレッドで走らない環境（メッセージループ外のテストなど）でも安全なよう、画面の更新はここでまとめて行う。
            if (closing || IsDisposed) return;
            RunOnUiThread(() => FinishExport(label, path, canceled, failure));
        }

        private void FinishExport(string label, string path, bool canceled, Exception failure)
        {
            exportWorker = null;
            CancellationTokenSource source = exportCancellation;
            exportCancellation = null;
            if (source != null) source.Dispose();
            SetExportBusy(false);
            ReleaseDirectoriesAfterExport();   // 書き出し中に切り替えたプロジェクトの、古い展開先をここで消す
            FinishExportBar(!canceled && failure == null);
            if (canceled)
            {
                statusLabel.Text = Loc.T("message.exportCanceled");
            }
            else if (failure != null)
            {
                statusLabel.Text = Loc.T("message.exportFailed");
                ShowDarkNotice(Loc.T("dialog.exportError"), failure.Message);
            }
            else
            {
                statusLabel.Text = Loc.T("message.exported", label, Path.GetFileName(path));
            }
        }

        private void ReportExportProgress(string label, double fraction)
        {
            int now = Environment.TickCount;
            if (fraction < 1.0 && unchecked(now - lastExportProgressTick) < 100) return;
            lastExportProgressTick = now;
            int percent = (int)Math.Floor(Math.Max(0.0, Math.Min(1.0, fraction)) * 100);
            TryBeginInvoke(() =>
            {
                if (!exportBusy || closing) return;
                statusLabel.Text = Loc.T("message.exporting", label, percent);
                exportBarTarget = (float)Math.Max(exportBarTarget, Math.Min(1.0, fraction));
            });
        }

        //--------------
        // 書き出しの進捗バー（ステータスバーの上端に細く描く）
        //--------------
        private enum ExportBarState { Hidden, Running, Done }
        private const int ExportBarHeight = 3;
        private const int ExportBarDoneMilliseconds = 450;   // 終わったときに緑に光ってから消えるまで
        private ExportBarState exportBarState;
        private float exportBarTarget;        // 実際の進み具合（0〜1）
        private float exportBarShown;         // 描いている長さ（目標へなめらかに近づける）
        private readonly System.Diagnostics.Stopwatch exportBarDoneClock = new System.Diagnostics.Stopwatch();
        private System.Windows.Forms.Timer exportBarTimer;

        private void StartExportBar()
        {
            exportBarState = ExportBarState.Running;
            exportBarTarget = 0;
            exportBarShown = 0;
            if (exportBarTimer == null)
            {
                exportBarTimer = new System.Windows.Forms.Timer { Interval = 15 };
                exportBarTimer.Tick += (s, e) => StepExportBar();
            }
            exportBarTimer.Start();
            InvalidateExportBar();
        }

        // 成功なら最後まで伸ばして緑に光らせ、中止・失敗ならすぐ消す。
        private void FinishExportBar(bool succeeded)
        {
            if (!succeeded || !UiMotion.Enabled)
            {
                exportBarState = ExportBarState.Hidden;
                if (exportBarTimer != null) exportBarTimer.Stop();
                InvalidateExportBar();
                return;
            }
            exportBarTarget = 1f;
            exportBarState = ExportBarState.Done;
            exportBarDoneClock.Reset();
        }

        private void StepExportBar()
        {
            float step = (exportBarTarget - exportBarShown) * 0.22f;
            exportBarShown = Math.Abs(step) < 0.002f ? exportBarTarget : exportBarShown + step;
            if (exportBarState == ExportBarState.Done)
            {
                if (exportBarShown >= 0.999f && !exportBarDoneClock.IsRunning) exportBarDoneClock.Start();
                if (exportBarDoneClock.ElapsedMilliseconds >= ExportBarDoneMilliseconds)
                {
                    exportBarState = ExportBarState.Hidden;
                    exportBarTimer.Stop();
                }
            }
            InvalidateExportBar();
        }

        private void InvalidateExportBar()
        {
            if (bottomStatusStrip.IsHandleCreated) bottomStatusStrip.Invalidate(new Rectangle(0, 0, bottomStatusStrip.Width, ExportBarHeight + 1));
        }

        private void PaintExportBar(Graphics g)
        {
            if (exportBarState == ExportBarState.Hidden) return;
            int width = (int)Math.Round(bottomStatusStrip.Width * Math.Max(0f, Math.Min(1f, exportBarShown)));
            if (width <= 0) return;
            Color color = accentColor;
            if (exportBarState == ExportBarState.Done && exportBarDoneClock.IsRunning)
            {
                // 緑に光ってから薄くなって消える。
                float t = Math.Min(1f, exportBarDoneClock.ElapsedMilliseconds / (float)ExportBarDoneMilliseconds);
                Color green = Color.FromArgb(90, 210, 130);
                color = t < 0.4f ? UiMotion.Mix(accentColor, green, t / 0.4f) : Color.FromArgb((int)Math.Round(255 * (1f - (t - 0.4f) / 0.6f)), green);
            }
            using (var brush = new SolidBrush(color))
                g.FillRectangle(brush, 0, 0, width, ExportBarHeight);
        }

        private void SetExportBusy(bool busy)
        {
            exportBusy = busy;
            bool canExport = !busy && GetAllItems().Count > 0 && loadingDepth == 0;
            exportPngButton.Enabled = canExport;
            exportTgaButton.Enabled = canExport;
            exportGifButton.Enabled = canExport;
            exportWebPButton.Enabled = canExport;
        }

        private void CancelExport()
        {
            CancellationTokenSource source = exportCancellation;
            if (source != null)
            {
                try { source.Cancel(); } catch (ObjectDisposedException) { }
            }
        }

        // フォームを閉じるときは書き出しを止め、書きかけのファイルを片付ける時間だけ待つ。
        private void StopExportForClose()
        {
            CancelExport();
            Task worker = exportWorker;
            if (worker == null) return;
            try { worker.Wait(3000); }
            catch (AggregateException) { }
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape && exportBusy)
            {
                CancelExport();
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }
    }
}
