using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace MabinogiBarter
{
    public sealed class AcquisitionSource
    {
        public string title { get; set; }
        public string url { get; set; }
    }
    public sealed class AcquisitionMethod
    {
        public string kind { get; set; }
        public string text { get; set; }
        public List<AcquisitionSource> sources { get; set; }
    }
    public sealed class AcquisitionItem
    {
        public string name { get; set; }
        public List<string> aliases { get; set; }
        public List<AcquisitionMethod> methods { get; set; }
        public List<string> notes { get; set; }
        public List<string> sourceNotes { get; set; }
        public string checkedAt { get; set; }
    }
    public sealed class AcquisitionDocument
    {
        public List<AcquisitionItem> items { get; set; }
    }
    public sealed class AcquisitionCatalog
    {
        readonly Dictionary<string, AcquisitionItem> names = new Dictionary<string, AcquisitionItem>(StringComparer.Ordinal);
        public readonly List<AcquisitionItem> Items = new List<AcquisitionItem>();
        public AcquisitionCatalog(string path)
        {
            try
            {
                var document = new JavaScriptSerializer { MaxJsonLength = 4 * 1024 * 1024 }.Deserialize<AcquisitionDocument>(File.ReadAllText(path, Encoding.UTF8));
                if (document == null || document.items == null) return;
                foreach (var item in document.items)
                {
                    if (item == null || String.IsNullOrWhiteSpace(item.name)) continue;
                    Items.Add(item); names[item.name] = item;
                    foreach (var alias in item.aliases ?? new List<string>()) if (!String.IsNullOrWhiteSpace(alias)) names[alias] = item;
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
        }
        public AcquisitionItem Get(string name)
        {
            AcquisitionItem result;
            return name != null && names.TryGetValue(name, out result) ? result : null;
        }
        public static bool IsWebLink(string url)
        {
            Uri uri;
            return Uri.TryCreate(url, UriKind.Absolute, out uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);
        }
        public string Verify(Catalog catalog)
        {
            var expected = catalog.Trades.Select(t => t.Name).Concat(catalog.Trades.SelectMany(t => t.Groups).Select(g => g.Name))
                .Concat(catalog.Trades.SelectMany(t => t.Groups).SelectMany(g => g.Lines).SelectMany(l => new[] { l.Name, l.AlternateName }.Concat(l.Components ?? new List<string>())))
                .Where(n => !String.IsNullOrEmpty(n)).Distinct().ToList();
            var planner = new ProcurementPlanner(catalog);
            for (int i = 0; i < expected.Count; i++)
                foreach (var ingredient in planner.GetRecipes(expected[i]).SelectMany(r => r.Ingredients))
                    if (!expected.Contains(ingredient.Name)) expected.Add(ingredient.Name);
            foreach (string name in expected)
            {
                var item = Get(name);
                if (item == null || item.methods == null || item.methods.Count == 0) throw new Exception("Missing acquisition help: " + name);
                foreach (var method in item.methods)
                    if (String.IsNullOrWhiteSpace(method.text) || method.sources == null || method.sources.Count == 0 || method.sources.Any(s => s == null || !IsWebLink(s.url)))
                        throw new Exception("Missing acquisition evidence: " + name);
            }
            if (Items.Count < 145) throw new Exception("Expected acquisition help for the original and added procurement materials.");
            foreach (var potion in new[] { "생명력 500 포션", "마나 500 포션", "스태미나 500 포션", "마리오네트 500 포션" })
                if (Get(potion).methods.Any(m => m.kind == "제작")) throw new Exception("Purchased potion must not show a crafting recipe: " + potion);
            return "PASS acquisition: " + Items.Count + " entries cover all " + expected.Count + " names and aliases including recursive crafting; every method has a web source; 500 potions remain purchase-only.";
        }
    }

    public sealed partial class MainWindow
    {
        readonly AcquisitionCatalog acquisition = new AcquisitionCatalog(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", "item-acquisition.json"));

        Button AcquisitionHelp(string name)
        {
            // A local lookup only. Hovering, focusing and opening the details cannot
            // cause a request; a source URL opens only after an explicit link click.
            var help = Btn("?", delegate { }, false);
            help.Width = 19; help.Height = 19; help.FontSize = 11; help.Padding = new Thickness(0);
            help.Content = new TextBlock { Text = "?", FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = Green,
                TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            help.Margin = new Thickness(6, 0, 8, 0); help.Foreground = Green; help.Background = B("#EFF5F1");
            help.BorderBrush = B("#CADDD2"); help.HorizontalAlignment = HorizontalAlignment.Right;
            help.Tag = "acquisition:" + name;
            AutomationProperties.SetName(help, name + " 획득 방법");
            var tooltip = new ToolTip { Placement = PlacementMode.MousePoint, HorizontalOffset = 12, VerticalOffset = 14,
                Background = AppTheme.Surface, BorderBrush = B("#CBDCD2"), BorderThickness = new Thickness(1), Padding = new Thickness(18), MaxWidth = 440,
                FontFamily = FontFamily, Foreground = Ink, UseLayoutRounding = true };
            tooltip.Opened += delegate { tooltip.Content = AcquisitionBody(name, false); };
            help.ToolTip = tooltip;
            ToolTipService.SetInitialShowDelay(help, 200); ToolTipService.SetShowDuration(help, 120000);
            ToolTipService.SetBetweenShowDelay(help, 0); ToolTipService.SetShowOnDisabled(help, true);
            help.Click += delegate(object sender, RoutedEventArgs e) { e.Handled = true; tooltip.IsOpen = false; ShowAcquisition(name); };
            return help;
        }

        FrameworkElement AcquisitionBody(string name, bool isDetail)
        {
            var panel = new StackPanel { MaxWidth = isDetail ? 470 : 390 };
            var item = acquisition.Get(name);
            var heading = new Grid { Margin = new Thickness(0, 0, 0, 13) };
            heading.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(45) }); heading.ColumnDefinitions.Add(new ColumnDefinition());
            var source = itemIcons.Get(name);
            if (source != null)
            {
                var picture = new Image { Source = source, Width = 32, Height = 32, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left };
                RenderOptions.SetBitmapScalingMode(picture, BitmapScalingMode.NearestNeighbor); heading.Children.Add(picture);
            }
            var titleText = new StackPanel();
            titleText.Children.Add(T(item == null ? name : item.name, 17, Ink, true));
            titleText.Children.Add(T("획득 · 제작 · 구매 안내", 10, Muted, false)); Grid.SetColumn(titleText, 1); heading.Children.Add(titleText); panel.Children.Add(heading);
            if (item == null)
            {
                panel.Children.Add(T("획득 정보 파일을 찾지 못했습니다. 실행 파일 옆의 data 폴더를 확인해 주세요.", 12, Muted, false));
                return panel;
            }
            foreach (var method in item.methods ?? new List<AcquisitionMethod>())
            {
                var badge = T(method.kind, 11, Green, true); badge.Margin = new Thickness(0, 3, 0, 5); panel.Children.Add(badge);
                var description = T(method.text, 12, Ink, false); description.LineHeight = 20; description.Margin = new Thickness(0, 0, 0, 10); panel.Children.Add(description);
            }
            var trade = catalog.Trades.FirstOrDefault(t => t.Name == name);
            if (trade != null)
            {
                var recipe = T("현재 계산기 기준 · 교역품 1개 교환\n" + String.Join("\n", trade.Groups.Select(g => g.Name + " " + Calculator.FormatQuantity(g.PerTrade) + "개")), 11, Ink, false);
                recipe.LineHeight = 20; recipe.Margin = new Thickness(0, 3, 0, 9); panel.Children.Add(recipe);
                panel.Children.Add(T("교역소의 품목·교환 조건은 시기에 따라 변경될 수 있습니다.", 10, Muted, false));
            }
            foreach (var note in item.notes ?? new List<string>())
            {
                var text = T(note, 10, Muted, false); text.LineHeight = 18; text.Margin = new Thickness(0, 5, 0, 0); panel.Children.Add(text);
            }
            if (!isDetail) { var hint = T("?를 클릭하면 상세 내용을 볼 수 있습니다.", 10, Green, false); hint.Margin = new Thickness(0, 12, 0, 0); panel.Children.Add(hint); }
            var scroll = new ScrollViewer { Content = panel, Width = isDetail ? 478 : 398, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                MaxHeight = Math.Max(160, Math.Min(isDetail ? 580 : 510, SystemParameters.WorkArea.Height - (isDetail ? 180 : 140))), Padding = new Thickness(0, 0, 8, 0) };
            return scroll;
        }

        Window AcquisitionDialog(string name)
        {
            var dialog = new Window { Owner = this, Title = name + " · 획득 방법", Width = 555, SizeToContent = SizeToContent.Height,
                MaxHeight = Math.Min(740, SystemParameters.WorkArea.Height - 40), ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Background = AppTheme.Surface, FontFamily = FontFamily, UseLayoutRounding = true };
            var contentPanel = new StackPanel { Margin = new Thickness(23) };
            contentPanel.Children.Add(AcquisitionBody(name, true));
            var close = Btn("닫기", delegate { dialog.Close(); }, false); close.HorizontalAlignment = HorizontalAlignment.Right; close.Margin = new Thickness(0, 15, 0, 0); close.IsCancel = true;
            contentPanel.Children.Add(close); dialog.Content = contentPanel; AppMotion.WindowContent(dialog);
            return dialog;
        }
        void ShowAcquisition(string name) { AcquisitionDialog(name).ShowDialog(); }
        void OpenAcquisitionSource(string url)
        {
            if (!AcquisitionCatalog.IsWebLink(url)) return;
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch { footerMessage.Text = "출처 페이지를 열지 못했습니다. 인터넷 연결과 기본 브라우저 설정을 확인하세요."; }
        }

        public string RunAcquisitionChecks(Func<int> requests, string directory)
        {
            int before = requests();
            string result = acquisition.Verify(catalog);
            foreach (var item in acquisition.Items)
            {
                var button = AcquisitionHelp(item.name);
                if (!(button.ToolTip is ToolTip) || AutomationProperties.GetName(button) != item.name + " 획득 방법") throw new Exception("Missing acquisition affordance: " + item.name);
            }
            string longest = acquisition.Items.OrderByDescending(i => String.Join("", i.methods.Select(m => m.text)) .Length + String.Join("", i.notes ?? new List<string>()).Length).First().name;
            foreach (var sample in new[] { "설탕", "매듭끈", "스태미나 500 포션", "펫 놀이 세트", "고운 모래", longest }.Distinct())
            {
                var button = AcquisitionHelp(sample);
                var tip = (ToolTip)button.ToolTip; tip.PlacementTarget = this; tip.IsOpen = true;
                Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(delegate {}));
                if (!(tip.Content is FrameworkElement)) throw new Exception("Hover did not populate acquisition help: " + sample);
                tip.UpdateLayout();
                var scroll = tip.Content as ScrollViewer;
                if (scroll != null && ((FrameworkElement)scroll.Content).ActualWidth > scroll.ViewportWidth + 1) throw new Exception("Acquisition help overflows horizontally: " + sample);
                var bitmap = new RenderTargetBitmap((int)Math.Ceiling(tip.ActualWidth), (int)Math.Ceiling(tip.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(tip); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using (var file = File.Create(Path.Combine(directory, "acquisition-" + sample + ".png"))) encoder.Save(file);
                tip.IsOpen = false;
            }
            var dialog = AcquisitionDialog("설탕"); dialog.WindowStartupLocation = WindowStartupLocation.Manual; dialog.Left = -18000; dialog.Top = -18000; dialog.ShowInTaskbar = false;
            dialog.Show(); dialog.UpdateLayout();
            if (AuctionTestChildren<Button>(dialog).Any(b => AutomationProperties.GetName(b).StartsWith("출처 열기:", StringComparison.Ordinal))) throw new Exception("Acquisition detail must keep its source links in the central source window.");
            dialog.Close();
            if (requests() != before) throw new Exception("Acquisition hover or details caused an auction request.");
            return result + " Hover panels, aliases and source-free acquisition details verified with zero auction requests.";
        }
    }
}
