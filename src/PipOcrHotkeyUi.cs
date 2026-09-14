using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace MabinogiBarter
{
    public sealed partial class PipChecklistWindow
    {
        PipCaptureHotkey captureHotkey;
        bool updatingCaptureHotkey;
        public Expander CaptureHotkeyPanel { get; private set; }
        public CheckBox CaptureHotkeyToggle { get; private set; }
        public ComboBox CaptureHotkeyChoice { get; private set; }
        public TextBlock CaptureHotkeyStatus { get; private set; }
        public Button CaptureHotkeyRetry { get; private set; }
        public Action CaptureHotkeyChanged { get; set; }
        public bool CaptureHotkeyEnabled { get { return CaptureHotkeyToggle.IsChecked == true; } }
        public int CaptureHotkeyKey { get { return CaptureHotkeyChoice.SelectedIndex == 1 ? 0x23 : CaptureHotkeyChoice.SelectedIndex == 2 ? 0x2D : 0x24; } }

        void BuildCaptureHotkeyControls()
        {
            var body = new StackPanel { Margin = new Thickness(0, 7, 0, 0) };
            CaptureHotkeyToggle = new CheckBox { Content = "전체 촬영 단축키 사용", Foreground = Ink, FontSize = 12,
                VerticalContentAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 0, 7) };
            StyleReadyCheck(CaptureHotkeyToggle);
            body.Children.Add(CaptureHotkeyToggle);
            CaptureHotkeyChoice = new ComboBox { Margin = new Thickness(0, 0, 6, 0), MinWidth = 125 };
            foreach (string label in new[] { "Alt+Home", "Alt+End", "Alt+Insert" }) CaptureHotkeyChoice.Items.Add(label);
            LedgerControls.StyleCombo(CaptureHotkeyChoice, "촬영 키 선택"); CaptureHotkeyChoice.SelectedIndex = 0;
            AutomationProperties.SetName(CaptureHotkeyChoice, "전체 촬영 단축키 선택");
            var row = new Grid(); row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(CaptureHotkeyChoice);
            CaptureHotkeyRetry = SmallButton("다시 등록", delegate { if (captureHotkey != null) captureHotkey.Retry(); });
            Grid.SetColumn(CaptureHotkeyRetry, 1); row.Children.Add(CaptureHotkeyRetry); body.Children.Add(row);
            CaptureHotkeyStatus = Label("단축키 꺼짐", 11, Muted, false); CaptureHotkeyStatus.TextWrapping = TextWrapping.Wrap;
            CaptureHotkeyStatus.Margin = new Thickness(0, 6, 0, 0); body.Children.Add(CaptureHotkeyStatus);
            var help = Label("Alt를 누른 채 지정 키를 한 번 누르세요.\n마우스가 있는 모니터 전체를 즉시 촬영합니다.\n항상 위에 뜬 장부 창이 가린 부분은 읽지 않습니다.", 11, Muted, false);
            help.TextWrapping = TextWrapping.Wrap; help.Margin = new Thickness(0, 6, 0, 0); body.Children.Add(help);
            CaptureHotkeyPanel = new Expander { Content = body, Foreground = Ink, FontSize = 12,
                HorizontalContentAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 3, 0, 9) };
            captureHotkey = new PipCaptureHotkey(this, CaptureMonitorForOcr, delegate(string message) {
                CaptureHotkeyStatus.Text = message; UpdateCaptureHotkeyControls();
            });
            CaptureHotkeyToggle.Checked += delegate { ApplyCaptureHotkey(); };
            CaptureHotkeyToggle.Unchecked += delegate { ApplyCaptureHotkey(); };
            CaptureHotkeyChoice.SelectionChanged += delegate { ApplyCaptureHotkey(); };
            UpdateCaptureHotkeyControls();
        }
        public void ConfigureCaptureHotkey(bool enabled, int key)
        {
            updatingCaptureHotkey = true;
            try { CaptureHotkeyChoice.SelectedIndex = key == 0x23 ? 1 : key == 0x2D ? 2 : 0; CaptureHotkeyToggle.IsChecked = enabled; }
            finally { updatingCaptureHotkey = false; }
            ApplyCaptureHotkey();
        }
        void ApplyCaptureHotkey()
        {
            if (updatingCaptureHotkey || closed || captureHotkey == null) return;
            captureHotkey.Configure(CaptureHotkeyEnabled, CaptureHotkeyKey); UpdateCaptureHotkeyControls();
            var changed = CaptureHotkeyChanged; if (changed != null) changed();
        }
        void UpdateCaptureHotkeyControls()
        {
            if (CaptureHotkeyPanel == null) return;
            string state = !CaptureHotkeyEnabled ? "꺼짐" : captureHotkey != null && captureHotkey.IsRegistered ? "켜짐" : "확인 필요";
            CaptureHotkeyPanel.Header = "전체 촬영 · " + (CaptureHotkeyChoice.SelectedItem ?? "Alt+Home") + " · " + state;
            CaptureHotkeyToggle.IsEnabled = !OcrBusy; CaptureHotkeyChoice.IsEnabled = !OcrBusy;
            CaptureHotkeyRetry.IsEnabled = !OcrBusy && CaptureHotkeyEnabled;
        }
    }
}
