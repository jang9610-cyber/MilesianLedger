using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MabinogiBarter
{
    public sealed partial class MainWindow
    {
        Window sourcesDialog;

        void ShowSources()
        {
            if (sourcesDialog != null) { sourcesDialog.Activate(); return; }
            sourcesDialog = BuildSourcesDialog(); sourcesDialog.Show();
        }

        Window BuildSourcesDialog()
        {
            var dialog = new Window { Owner = this, Title = "출처", Width = 840, Height = 780, MinWidth = 660, MinHeight = 560,
                MaxHeight = Math.Max(560, SystemParameters.WorkArea.Height - 30), WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Background = BackgroundColor, Foreground = Ink, FontFamily = FontFamily, FontSize = 12, UseLayoutRounding = true };
            var layout = new Grid { Margin = new Thickness(24) };
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); layout.RowDefinitions.Add(new RowDefinition()); layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var heading = new StackPanel { Margin = new Thickness(0, 0, 0, 18) }; heading.Children.Add(T("출처", 24, Ink, true));
            var intro = T("밀레시안 장부에 사용한 자료와 원문 링크를 한곳에 모았습니다.", 12, Muted, false); intro.Margin = new Thickness(0, 7, 0, 0); heading.Children.Add(intro); layout.Children.Add(heading);
            var body = new StackPanel();
            var credits = new UniformGrid { Columns = 2, Margin = new Thickness(0, 0, -10, 4) };
            credits.Children.Add(SourceCreditCard("게임 자료 권리", "마비노기", "게임 명칭·캐릭터·아이템 이미지의 권리는\nNEXON 등 각 권리자에게 있습니다.", null));
            credits.Children.Add(SourceCreditCard("경매장 시세", "NEXON Open API", "Data based on NEXON Open API", "https://openapi.nexon.com/ko/game/mabinogi/?id=33"));
            credits.Children.Add(SourceCreditCard("아이콘", itemIcons.SourceName ?? "아이콘 자료", "게임 이미지의 권리는 원권리자에게 있습니다.\n개별 이미지 원문은 아래에서 확인할 수 있습니다.", itemIcons.SourceUrl));
            credits.Children.Add(SourceCreditCard("판매·운송 기준", "마비교역", "판매 평균가·적재량·교역 설정\n자료 확인 " + tradePlanningData.CheckedAt, tradePlanningData.SourceUrl));
            body.Children.Add(credits);
            var sortingNote = T("재료 정렬은 아래 아이템별 설명과 제작 정보를 참고해 앱에서 정한 분류입니다. 장작·허브처럼 여러 제작에 쓰이는 재료도 한 분류에 모으며, 게임이나 경매장의 공식 분류와는 다를 수 있습니다.", 11, Muted, false);
            sortingNote.Margin = new Thickness(0, 4, 0, 16); body.Children.Add(sortingNote);
            var details = new StackPanel(); details.Children.Add(T("아이템별 출처", 15, Ink, true));
            var searchLabel = T("아이템 이름으로 검색", 11, Muted, false); searchLabel.Margin = new Thickness(0, 13, 0, 6); details.Children.Add(searchLabel);
            var search = new TextBox { Height = 36, FontSize = 12, Padding = new Thickness(11, 0, 11, 0), VerticalContentAlignment = VerticalAlignment.Center, Foreground = Ink, Background = AppTheme.Surface, BorderBrush = Line };
            StyleStationPresetName(search); AutomationProperties.SetName(search, "출처 아이템 검색");
            var searchField = new Grid(); searchField.Children.Add(search);
            var placeholder = T("예: 설탕, 매듭끈, 실리엔", 12, Muted, false); placeholder.Margin = new Thickness(12, 0, 12, 0); placeholder.IsHitTestVisible = false; searchField.Children.Add(placeholder); details.Children.Add(searchField);
            var selector = new ComboBox { DisplayMemberPath = "name", Margin = new Thickness(0, 8, 0, 0) }; StyleStationCombo(selector, "아이템을 선택하세요"); AutomationProperties.SetName(selector, "출처 아이템 선택"); details.Children.Add(selector);
            var count = T("", 10, Muted, false); count.Margin = new Thickness(0, 7, 0, 12); details.Children.Add(count);
            var references = new StackPanel(); details.Children.Add(references);
            bool filtering = false, hasShownItem = false;
            string shownItemName = null;
            Action showItem = delegate {
                if (filtering) return;
                var item = selector.SelectedItem as AcquisitionItem;
                string itemName = item == null ? null : item.name;
                if (hasShownItem && String.Equals(shownItemName, itemName, StringComparison.Ordinal)) return;
                AppMotion.Transition(references, delegate {
                    references.Children.Clear(); shownItemName = itemName; hasShownItem = true;
                    if (item == null) { references.Children.Add(T("일치하는 아이템이 없습니다.", 12, Muted, false)); return; }
                    references.Children.Add(T(item.name + " · 자료 확인 " + item.checkedAt, 11, Ink, true));
                    if (ItemCategories.IsKnown(item.name)) references.Children.Add(T("정렬 분류 · " + ItemCategories.GetGroup(item.name), 11, Green, true));
                    foreach (string note in item.sourceNotes ?? new List<string>()) { var noteText = T(note, 11, Muted, false); noteText.Margin = new Thickness(0, 8, 0, 0); references.Children.Add(noteText); }
                    var links = (item.methods ?? new List<AcquisitionMethod>()).SelectMany(m => m.sources ?? new List<AcquisitionSource>()).Where(s => s != null && AcquisitionCatalog.IsWebLink(s.url)).GroupBy(s => s.url).Select(g => g.First()).ToList();
                    foreach (var evidence in links) references.Children.Add(SourceLink(evidence.title, evidence.url));
                    string iconPage = itemIcons.GetSourcePage(item.name);
                    if (AcquisitionCatalog.IsWebLink(iconPage) && !links.Any(s => s.url == iconPage)) references.Children.Add(SourceLink("아이콘 원문 · " + item.name, iconPage));
                });
            };
            selector.SelectionChanged += delegate { showItem(); };
            Action filter = delegate {
                string query = (search.Text ?? "").Trim(); var previous = selector.SelectedItem as AcquisitionItem;
                var matches = acquisition.Items.Where(i => i.name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 || (i.aliases ?? new List<string>()).Any(a => a.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)).OrderBy(i => i.name).ToList();
                filtering = true;
                try { selector.ItemsSource = matches; selector.SelectedItem = matches.Contains(previous) ? previous : matches.FirstOrDefault(); selector.IsEnabled = matches.Count > 0; }
                finally { filtering = false; }
                count.Text = "아이템 " + matches.Count + "종 · 링크를 누르면 원문 페이지가 열립니다."; showItem();
            };
            search.TextChanged += delegate { placeholder.Visibility = search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed; filter(); }; filter();
            var itemCard = Box(details, AppTheme.Surface, 10, new Thickness(18)); itemCard.BorderBrush = Line; itemCard.BorderThickness = new Thickness(1); body.Children.Add(itemCard);
            var scroll = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new Thickness(0, 0, 8, 0) }; Grid.SetRow(scroll, 1); layout.Children.Add(scroll);
            var close = Btn("닫기", delegate { dialog.Close(); }, false); close.HorizontalAlignment = HorizontalAlignment.Right; close.Margin = new Thickness(0, 15, 0, 0); Grid.SetRow(close, 2); layout.Children.Add(close);
            dialog.Content = layout; AttachProcurementWindowLifecycle(dialog);
            dialog.Closed += delegate { if (sourcesDialog == dialog) sourcesDialog = null; };
            return dialog;
        }

        FrameworkElement SourceCreditCard(string category, string name, string description, string url)
        {
            var panel = new StackPanel(); panel.Children.Add(T(category, 10, Muted, false));
            var title = T(name, 13, Ink, true); title.Margin = new Thickness(0, 6, 0, 7); panel.Children.Add(title);
            var text = T(description, 11, Muted, false); text.LineHeight = 18; panel.Children.Add(text);
            if (AcquisitionCatalog.IsWebLink(url)) panel.Children.Add(SourceLink("원문 보기", url));
            var card = Box(panel, AppTheme.Surface, 10, new Thickness(16)); card.BorderBrush = Line; card.BorderThickness = new Thickness(1); card.Margin = new Thickness(0, 0, 10, 10); return card;
        }

        Button SourceLink(string title, string url)
        {
            var link = Btn("", delegate { OpenAcquisitionSource(url); }, false); link.Content = T(title + " ↗", 11, Green, false);
            link.ToolTip = url; link.Tag = url; link.Margin = new Thickness(0, 8, 0, 0); link.Padding = new Thickness(10, 7, 10, 7); link.HorizontalContentAlignment = HorizontalAlignment.Stretch;
            AutomationProperties.SetName(link, "출처 열기: " + title); return link;
        }

        public string RunSourcesUiChecks(Func<int> requests, string directory)
        {
            if (!auctionHasInjectedService || !String.Equals(Path.GetFullPath(Path.GetDirectoryName(store.FilePath)), Path.GetFullPath(directory), StringComparison.OrdinalIgnoreCase)) throw new Exception("Source UI checks require an offline temporary store.");
            int before = requests(); string savedState = new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(state);
            var ordinary = String.Join(" ", AuctionTestChildren<TextBlock>(shell).Select(t => t.Text));
            if (ordinary.Contains("교역하자") || ordinary.Contains("라바뉴") || ordinary.Contains("Data based on") || ordinary.Contains("알반 에일레르")) throw new Exception("Source credits or season remain on the main screen.");
            if (AuctionTestChildren<Button>(shell).Count(b => AutomationProperties.GetName(b) == "출처 모아보기") != 1) throw new Exception("One central source entry is required.");
            var dialog = BuildSourcesDialog(); dialog.WindowStartupLocation = WindowStartupLocation.Manual; dialog.Left = -18000; dialog.Top = -18000; dialog.ShowInTaskbar = false;
            try
            {
                dialog.Show(); PumpProcurementInteractionLayout(); dialog.UpdateLayout();
                var text = String.Join(" ", AuctionTestChildren<TextBlock>(dialog).Select(t => t.Text));
                if (!text.Contains("게임 자료 권리") || !text.Contains("각 권리자") || !text.Contains("NEXON Open API") || !text.Contains("라바뉴") || !text.Contains("마비교역")) throw new Exception("Central source credits or rights notice are incomplete.");
                if (text.Contains("교역하자") || text.Contains(".xlsx") || text.Contains("원본 교환표") || text.Contains("알반 에일레르")) throw new Exception("Retired spreadsheet credits or season remain in sources.");
                var search = AuctionTestChildren<TextBox>(dialog).Single(t => AutomationProperties.GetName(t) == "출처 아이템 검색");
                var selector = AuctionTestChildren<ComboBox>(dialog).Single();
                if (selector.Items.Count != acquisition.Items.Count) throw new Exception("Not all acquisition items are available in sources.");
                search.Text = "설탕"; dialog.UpdateLayout();
                if (selector.Items.Count != 1 || ((AcquisitionItem)selector.SelectedItem).name != "설탕") throw new Exception("Source item search failed.");
                var expected = acquisition.Get("설탕").methods.SelectMany(m => m.sources).Select(s => s.url).Distinct();
                var actual = AuctionTestChildren<Button>(dialog).Select(b => b.Tag as string).ToList();
                if (expected.Any(url => !actual.Contains(url)) || !actual.Contains(itemIcons.GetSourcePage("설탕"))) throw new Exception("Item evidence or icon source was lost.");
                search.Text = "없는아이템검증"; if (selector.SelectedItem != null || selector.IsEnabled) throw new Exception("Source empty search left a stale item.");
                search.Text = "설탕"; dialog.UpdateLayout();
                var bitmap = new RenderTargetBitmap((int)Math.Ceiling(dialog.ActualWidth), (int)Math.Ceiling(dialog.ActualHeight), 96, 96, PixelFormats.Pbgra32); bitmap.Render(dialog);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using (var file = File.Create(Path.Combine(directory, "sources.png"))) encoder.Save(file);
                foreach (var item in acquisition.Items)
                {
                    var help = AcquisitionBody(item.name, true); help.Measure(new Size(500, 700)); help.Arrange(new Rect(0, 0, 500, 700)); help.UpdateLayout();
                    var detailText = String.Join(" ", AuctionTestChildren<TextBlock>(help).Select(t => t.Text));
                    if (!detailText.Contains(item.name)) throw new Exception("Acquisition text was not rendered for source inspection: " + item.name);
                    if (detailText.Contains("출처") || detailText.Contains("위키") || AuctionTestChildren<Button>(help).Any(b => AutomationProperties.GetName(b).StartsWith("출처 열기:", StringComparison.Ordinal))) throw new Exception("An item detail still displays scattered source credits: " + item.name);
                }
                if (requests() != before || new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(state) != savedState) throw new Exception("Reading sources changed preparation state or requested auction data.");
                return "PASS sources UI: one entry, game rights and three source credits; searchable item evidence and icon links, empty recovery, no spreadsheet credit or season label, no state changes or HTTP.";
            }
            finally { dialog.Close(); }
        }
    }
}
