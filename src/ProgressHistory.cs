using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;

namespace MabinogiBarter
{
    public sealed class ProgressCheckpoint
    {
        public DateTime CreatedUtc { get; set; }
        public string Reason { get; set; }
        public ProgressState State { get; set; }
        public override string ToString() { return CreatedUtc.ToLocalTime().ToString("MM/dd HH:mm:ss") + "  ·  " + Reason; }
    }

    public sealed class ProgressHistory
    {
        readonly string path;
        readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 };
        public ProgressHistory(string file) { path = file; }
        public List<ProgressCheckpoint> Load()
        {
            if (!File.Exists(path)) return new List<ProgressCheckpoint>();
            var entries = json.Deserialize<List<ProgressCheckpoint>>(File.ReadAllText(path));
            if (entries == null || entries.Any(e => e == null || e.State == null)) throw new InvalidDataException("복원 기록을 읽지 못했습니다. 기존 파일은 유지됩니다.");
            return entries.Take(20).ToList();
        }
        public void Save(ProgressState state, string reason)
        {
            var entries = Load();
            entries.Insert(0, new ProgressCheckpoint { CreatedUtc = DateTime.UtcNow, Reason = reason, State = StateStore.Copy(state) });
            entries = entries.Take(20).ToList();
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string pending = path + ".tmp";
            File.WriteAllText(pending, json.Serialize(entries), new System.Text.UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(pending, path, path + ".bak"); else File.Move(pending, path);
        }
    }

    public sealed partial class MainWindow
    {
        ProgressHistory History { get { return new ProgressHistory(Path.Combine(Path.GetDirectoryName(store.FilePath), "progress-history.json")); } }
        bool SaveProgressCheckpoint(string reason)
        {
            try {
                var copy = StateStore.Copy(state); ProcurementPresets.SaveCurrent(copy, procurementPlanner);
                History.Save(copy, reason); return true;
            } catch (Exception ex) { footerMessage.Text = "복원 지점을 저장하지 못해 변경을 중단했습니다. " + ex.Message; return false; }
        }

        bool ApplyProgressCheckpoint(ProgressCheckpoint checkpoint)
        {
            try {
                var restored = StateStore.Copy(checkpoint.State);
                restored.Normalize(catalog); restored.TradeSettings.Normalize(tradePlanningData);
                ProcurementPresets.SaveCurrent(restored, procurementPlanner);
                if (!SaveProgressCheckpoint("상태 복원 전")) return false;
                // Commit successfully before changing any live views.
                store.Save(restored);
                delayedRefresh.Stop(); state = restored; undo = null; footerActions.Children.Clear();
                if (procurementDialog != null) procurementDialog.Close();
                RenderAll();
                footerMessage.Text = checkpoint.CreatedUtc.ToLocalTime().ToString("MM/dd HH:mm") + "의 진행 상태로 복원했습니다. 복원 직전 상태도 기록에 남겼습니다.";
                return true;
            } catch (Exception ex) { footerMessage.Text = "진행 상태를 복원하지 못했습니다. " + ex.Message; return false; }
        }

        void ShowProgressHistory()
        {
            var dialog = new Window { Owner = this, Title = "진행 상태 복원 · 밀레시안 장부", Width = 600, Height = 550,
                WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize,
                Background = BackgroundColor, FontFamily = FontFamily, Icon = Icon };
            var body = new Grid { Margin = new Thickness(24) };
            body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            body.RowDefinitions.Add(new RowDefinition());
            body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var intro = new StackPanel(); intro.Children.Add(T("진행 상태 복원", 22, Ink, true));
            var hint = T("리셋·프리셋 적용 전 상태를 자동 보관합니다. 최근 20개까지 앱을 다시 열어도 남습니다.", 12, Muted, false);
            hint.Margin = new Thickness(0, 10, 0, 14); intro.Children.Add(hint); body.Children.Add(intro);
            var list = new ListBox { BorderBrush = Line, BorderThickness = new Thickness(1), Padding = new Thickness(8), FontSize = 12 };
            Grid.SetRow(list, 1); body.Children.Add(list);
            var bottom = new StackPanel { Margin = new Thickness(0, 14, 0, 0) };
            var detailText = T("복원할 시점을 선택하세요.", 12, Muted, false); bottom.Children.Add(detailText);
            var buttons = new WrapPanel { Margin = new Thickness(0, 16, 0, 0) };
            Action reload = delegate {
                try { var entries = History.Load(); list.ItemsSource = entries; detailText.Text = entries.Count == 0 ? "아직 복원 기록이 없습니다. 현재 상태를 저장할 수 있습니다." : "복원할 시점을 선택하세요."; }
                catch (Exception ex) { detailText.Text = ex.Message; }
            };
            buttons.Children.Add(Btn("현재 상태 저장", delegate { if (SaveProgressCheckpoint("직접 저장")) reload(); else detailText.Text = footerMessage.Text; }, false));
            buttons.Children.Add(Btn("닫기", dialog.Close, false));
            var restore = Btn("선택한 상태로 복원", delegate {
                var checkpoint = list.SelectedItem as ProgressCheckpoint; if (checkpoint == null) return;
                if (MessageBox.Show(dialog, "품목 선택·수량·구비 체크·준비 방식·프리셋·운송 설정을 선택한 시점으로 되돌립니다.\n\n현재 상태도 복원 기록에 보관됩니다. 계속할까요?", "진행 상태 복원", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
                if (ApplyProgressCheckpoint(checkpoint)) dialog.Close(); else detailText.Text = footerMessage.Text;
            }, true);
            restore.IsEnabled = false; buttons.Children.Add(restore);
            list.SelectionChanged += delegate {
                var checkpoint = list.SelectedItem as ProgressCheckpoint; restore.IsEnabled = checkpoint != null;
                if (checkpoint != null) detailText.Text = checkpoint + "\n선택 교역품 " + catalog.Trades.Count(t => Calculator.EffectiveTarget(checkpoint.State, t) > 0) + "종 · 구비 체크 " + (checkpoint.State.ProcurementReady == null ? 0 : checkpoint.State.ProcurementReady.Count) + "건";
            };
            bottom.Children.Add(buttons); Grid.SetRow(bottom, 2); body.Children.Add(bottom);
            dialog.Content = body; AppMotion.WindowContent(dialog); reload(); dialog.ShowDialog();
        }
    }
}
