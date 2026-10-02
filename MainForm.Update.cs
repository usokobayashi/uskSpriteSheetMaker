//==================================================
// MainForm.Update
// 新しいバージョンの通知。起動の少しあと（1日1回まで）に GitHub Releases を確認し、
// タイトルバー右端のチップと、右下の通知カード（そのバージョンで1回だけ）で知らせる。
// 通信できないときは何も出さない。更新そのものは利用者が Releases のページから行う。
//==================================================

using System;
using System.Diagnostics;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SpriteSheetMaker
{
    public sealed partial class MainForm
    {
        private const int UpdateCheckDelayMilliseconds = 3000;   // 起動直後の描画・読み込みを優先する
        private const int UpdateNotesMaxWidth = 420;             // 変更点の折り返し幅（論理px）

        private readonly RoundedCheckBox updateCheckBox = new RoundedCheckBox();
        private RoundedButton updateChip;
        private RoundedPanel updateCard;
        private ReleaseInfo availableUpdate;   // 現在のバージョンより新しいバージョン。なければ null
        private const int CardRiseMilliseconds = 220;
        private const int CardSinkMilliseconds = 160;
        private const int CardRisePixels = 28;
        private const int ChipGlowMilliseconds = 900;
        private Timer updateCardMotion;
        private Timer updateChipGlow;

        //--------------
        // InitializeUpdateSupport
        //--------------
        private void InitializeUpdateSupport()
        {
            BindText(updateCheckBox, "check.updates");
            updateCheckBox.AutoSize = true;
            updateCheckBox.ForeColor = lightText;
            updateCheckBox.BackColor = Color.Transparent;
            updateCheckBox.Margin = new Padding(10, 4, 10, 4);
            updateCheckBox.Checked = UpdateChecker.IsEnabled;
            updateCheckBox.CheckedChanged += (s, e) =>
            {
                UpdateChecker.IsEnabled = updateCheckBox.Checked;
                if (!updateCheckBox.Checked) ShowUpdateNotice(null, false);
            };
            localizedBindings.Add(RefreshUpdateTexts);

            Shown += (s, e) =>
            {
                var timer = new Timer { Interval = UpdateCheckDelayMilliseconds };
                timer.Tick += (ts, te) =>
                {
                    timer.Stop();
                    timer.Dispose();
                    BeginUpdateCheck();
                };
                timer.Start();
            };
            Resize += (s, e) => PlaceUpdateCard();
        }

        // タイトルバー右端（ウィンドウ操作ボタンの左）のチップ。新しいバージョンがあるときだけ見える。
        private void BuildUpdateTitleControls(Panel bar, Control caption)
        {
            updateChip = new RoundedButton
            {
                CornerRadius = RadiusMd,
                BackColor = accentColor,
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = UiFont.Create(9.5f, FontStyle.Regular, GraphicsUnit.Point),
                Cursor = Cursors.Hand,
                Height = 32,
                TabStop = false,
                Visible = false
            };
            updateChip.FlatAppearance.BorderSize = 0;
            updateChip.FlatAppearance.MouseOverBackColor = accentHoverColor;
            updateChip.FlatAppearance.MouseDownBackColor = accentPressedColor;
            updateChip.Click += (s, e) => OpenUpdatePage();
            bar.Controls.Add(updateChip);

            Action place = () =>
            {
                updateChip.Left = caption.Left - updateChip.Width - 16;
                updateChip.Top = (bar.Height - updateChip.Height) / 2;
            };
            bar.Resize += (s, e) => place();
            caption.LocationChanged += (s, e) => place();
            updateChip.SizeChanged += (s, e) => place();
            place();
        }

        //--------------
        // 確認
        //--------------
        private void BeginUpdateCheck()
        {
            if (IsDisposed || !UpdateChecker.IsEnabled) return;
            if (!UpdateChecker.ShouldCheckNow(DateTime.UtcNow))
            {
                // 今日はもう確認済み。前回見つけた新しいバージョンがあればチップだけ出す。
                ShowUpdateNotice(UpdateChecker.CachedNewerRelease(), false);
                return;
            }
            Task<ReleaseInfo> check = UpdateChecker.CheckAsync();
            check.ContinueWith(t =>
            {
                ReleaseInfo latest = t.Status == TaskStatus.RanToCompletion ? t.Result : null;
                if (latest == null) return;   // 通信できない・応答がおかしいときは何も出さない
                TryBeginInvoke(() =>
                {
                    if (!UpdateChecker.IsEnabled) return;
                    UpdateChecker.RememberCheck(DateTime.UtcNow, latest);
                    bool newer = UpdateChecker.IsNewer(latest.Version, AppInfo.Version);
                    ShowUpdateNotice(newer ? latest : null, newer && UpdateChecker.ShouldShowCard(latest));
                    if (newer) UpdateChecker.MarkCardShown(latest);
                });
            }, TaskScheduler.Default);
        }

        // release が null ならチップとカードを隠す。showCard のときだけ通知カードを出す。
        internal void ShowUpdateNotice(ReleaseInfo release, bool showCard)
        {
            availableUpdate = release;
            if (updateChip != null)
            {
                bool appearing = release != null && !updateChip.Visible;
                updateChip.Visible = release != null;
                RefreshUpdateTexts();
                if (appearing) GlowUpdateChip();
            }
            CloseUpdateCard();
            if (release != null && showCard) OpenUpdateCard(release);
        }

        private void RefreshUpdateTexts()
        {
            if (updateChip == null || availableUpdate == null) return;
            updateChip.Text = Loc.T("update.chip", "v" + availableUpdate.Version);
            updateChip.Width = TextRenderer.MeasureText(updateChip.Text, updateChip.Font).Width + 32;
            if (updateCard != null) OpenUpdateCard(availableUpdate);   // 言語が変わったら作り直す
        }

        // チップが現れたときに1回だけ、明るくなってから元の色へ戻る（光り続けると作業の邪魔になるため1回だけ）。
        private void GlowUpdateChip()
        {
            UiMotion.Stop(ref updateChipGlow);
            Color glow = Color.FromArgb(160, 152, 255);
            updateChipGlow = UiMotion.Animate(ChipGlowMilliseconds, t =>
            {
                if (updateChip == null || updateChip.IsDisposed) return;
                float pulse = (float)Math.Sin(Math.PI * t);
                updateChip.BackColor = UiMotion.Mix(accentColor, glow, pulse);
                updateChip.Invalidate();
            }, () => { if (updateChip != null && !updateChip.IsDisposed) updateChip.BackColor = accentColor; });
        }

        private void OpenUpdatePage()
        {
            string url = availableUpdate != null && UpdateChecker.IsTrustedReleaseUrl(availableUpdate.PageUrl)
                ? availableUpdate.PageUrl : UpdateChecker.ReleasesPage;
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception || ex is InvalidOperationException)
            {
                statusLabel.Text = url;
            }
        }

        //--------------
        // 通知カード
        //--------------
        private void OpenUpdateCard(ReleaseInfo release)
        {
            CloseUpdateCard();
            int notesWidth = LogicalToDeviceUnits(UpdateNotesMaxWidth);

            var layout = new TableLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 2,
                BackColor = Color.Transparent,
                Margin = Padding.Empty,
                Padding = Padding.Empty
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            var title = new Label
            {
                AutoSize = true,
                MaximumSize = new Size(notesWidth, 0),
                Text = Loc.T("update.cardTitle", "v" + release.Version),
                ForeColor = lightText,
                BackColor = Color.Transparent,
                Font = UiFont.Create(10.5f, FontStyle.Bold, GraphicsUnit.Point),
                Margin = new Padding(0, 4, 12, 4)
            };
            var close = new RoundedButton
            {
                Text = "×",
                AccessibleName = Loc.T("update.close"),
                CornerRadius = RadiusSm,
                BackColor = panelElevated,
                ForeColor = mutedText,
                FlatStyle = FlatStyle.Flat,
                Size = new Size(LogicalToDeviceUnits(28), LogicalToDeviceUnits(28)),
                Margin = Padding.Empty,
                TabStop = false,
                Cursor = Cursors.Hand
            };
            close.FlatAppearance.BorderSize = 0;
            close.FlatAppearance.MouseOverBackColor = treeSelectionColor;
            close.Click += (s, e) => SinkUpdateCard();

            var current = MakeUpdateCardLabel(Loc.T("update.current", "v" + AppInfo.Version), mutedText, notesWidth);
            layout.Controls.Add(title, 0, 0);
            layout.Controls.Add(close, 1, 0);
            layout.Controls.Add(current, 0, 1);
            layout.SetColumnSpan(current, 2);
            int row = 2;
            if (!string.IsNullOrEmpty(release.Notes))
            {
                Label heading = MakeUpdateCardLabel(Loc.T("update.changes"), lightText, notesWidth);
                heading.Margin = new Padding(0, 10, 0, 2);
                Label notes = MakeUpdateCardLabel(release.Notes, mutedText, notesWidth);
                layout.Controls.Add(heading, 0, row);
                layout.SetColumnSpan(heading, 2);
                layout.Controls.Add(notes, 0, row + 1);
                layout.SetColumnSpan(notes, 2);
                row += 2;
            }

            var buttons = new FlowLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                WrapContents = true,
                MaximumSize = new Size(notesWidth, 0),
                BackColor = Color.Transparent,
                Margin = new Padding(0, 12, 0, 0),
                Padding = Padding.Empty
            };
            RoundedButton open = MakeUpdateCardButton(Loc.T("update.open"), true);
            open.Click += (s, e) => { OpenUpdatePage(); SinkUpdateCard(); };
            RoundedButton later = MakeUpdateCardButton(Loc.T("update.later"), false);
            later.Click += (s, e) => SinkUpdateCard();
            buttons.Controls.Add(open);
            buttons.Controls.Add(later);
            layout.Controls.Add(buttons, 0, row);
            layout.SetColumnSpan(buttons, 2);

            updateCard = new RoundedPanel
            {
                CornerRadius = RadiusLg,
                BackColor = panelElevated,
                BorderColor = inputBorder,
                BackdropColor = darkBack,
                Padding = new Padding(LogicalToDeviceUnits(16), LogicalToDeviceUnits(12), LogicalToDeviceUnits(12), LogicalToDeviceUnits(14))
            };
            updateCard.Controls.Add(layout);
            layout.Location = new Point(updateCard.Padding.Left, updateCard.Padding.Top);
            Size preferred = layout.GetPreferredSize(Size.Empty);
            // 大きさが決まったら1列目を残り幅いっぱいにして、× をカードの右上へ寄せる。
            layout.AutoSize = false;
            layout.ColumnStyles[0] = new ColumnStyle(SizeType.Percent, 100f);
            layout.Size = preferred;
            updateCard.Size = new Size(preferred.Width + updateCard.Padding.Horizontal, preferred.Height + updateCard.Padding.Vertical);
            Controls.Add(updateCard);
            updateCard.BringToFront();
            PlaceUpdateCard();

            // 下から浮き上がって出る。
            RoundedPanel card = updateCard;
            int finalTop = card.Top;
            UiMotion.Stop(ref updateCardMotion);
            updateCardMotion = UiMotion.Animate(CardRiseMilliseconds, t =>
            {
                if (!card.IsDisposed) card.Top = finalTop + (int)Math.Round(CardRisePixels * (1f - t));
            });
        }

        // ボタンで閉じたときは、沈みながら画面の下へ消える。
        private void SinkUpdateCard()
        {
            RoundedPanel card = updateCard;
            if (card == null) return;
            updateCard = null;   // 以降は「閉じた」扱い（同じカードを二度閉じない）
            UiMotion.Stop(ref updateCardMotion);
            int startTop = card.Top;
            int distance = Math.Max(CardRisePixels, ClientSize.Height - startTop);
            UiMotion.Animate(CardSinkMilliseconds, t =>   // 閉じる動きは他の処理で止めない（止めるとカードが残る）
            {
                if (!card.IsDisposed) card.Top = startTop + (int)Math.Round(distance * t);
            }, () =>
            {
                Controls.Remove(card);
                card.Dispose();
            });
        }

        private Label MakeUpdateCardLabel(string text, Color color, int maxWidth)
        {
            return new Label
            {
                AutoSize = true,
                MaximumSize = new Size(maxWidth, 0),
                Text = text,
                ForeColor = color,
                BackColor = Color.Transparent,
                Font = UiFont.Create(9.5f, FontStyle.Regular, GraphicsUnit.Point),
                UseMnemonic = false,
                Margin = new Padding(0, 2, 0, 2)
            };
        }

        private RoundedButton MakeUpdateCardButton(string text, bool primary)
        {
            var button = new RoundedButton
            {
                Text = text,
                CornerRadius = RadiusMd,
                BackColor = primary ? accentColor : darkBack,
                BorderColor = primary ? Color.Empty : inputBorder,
                ForeColor = primary ? Color.White : lightText,
                FlatStyle = FlatStyle.Flat,
                Font = UiFont.Create(9.5f, FontStyle.Regular, GraphicsUnit.Point),
                Cursor = Cursors.Hand,
                Margin = new Padding(0, 0, 8, 0),
                TabStop = false
            };
            button.FlatAppearance.BorderSize = 0;
            button.FlatAppearance.MouseOverBackColor = primary ? accentHoverColor : treeSelectionColor;
            Size textSize = TextRenderer.MeasureText(text, button.Font);
            button.Size = new Size(textSize.Width + LogicalToDeviceUnits(28), Math.Max(LogicalToDeviceUnits(32), textSize.Height + LogicalToDeviceUnits(12)));
            return button;
        }

        private void PlaceUpdateCard()
        {
            if (updateCard == null) return;
            int margin = LogicalToDeviceUnits(16);
            int bottom = ClientSize.Height - margin - (bottomStatusStrip.Visible ? bottomStatusStrip.Height : 0);
            updateCard.Location = new Point(Math.Max(0, ClientSize.Width - updateCard.Width - margin), Math.Max(0, bottom - updateCard.Height));
        }

        private void CloseUpdateCard()
        {
            UiMotion.Stop(ref updateCardMotion);
            if (updateCard == null) return;
            RoundedPanel card = updateCard;
            updateCard = null;
            Controls.Remove(card);
            card.Dispose();
        }
    }
}
