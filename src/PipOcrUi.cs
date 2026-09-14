using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MabinogiBarter
{
    public sealed partial class PipChecklistWindow
    {
        CancellationTokenSource ocrOperation;
        bool ocrMode, ocrBusy, ocrMatching, ocrWholeScreen;
        int ocrRevision, ocrMatchRevision;
        string ocrAppliedText = "";
        List<OcrMarketMatch> ocrMatches = new List<OcrMarketMatch>();
        readonly Dictionary<string, string> ocrChoices = new Dictionary<string, string>(StringComparer.Ordinal);

        public Button CaptureButton { get; private set; }
        public Button OcrApplyButton { get; private set; }
        public Button OcrClearButton { get; private set; }
        public TextBox OcrLinesInput { get; private set; }
        public Expander OcrEditor { get; private set; }
        public bool OcrBusy { get { return ocrBusy || ocrMatching; } }
        public bool OcrMode { get { return ocrMode; } }
        public int OcrItemCount { get { return VisibleOcrMatches().Count; } }
        // Production services are local-only. Tests replace both to avoid reading
        // the user's screen, requiring an OCR language pack or touching clipboard.
        public Func<Window, CancellationToken, Task<BitmapSource>> CaptureRegionAsync { get; set; }
        public Func<Window, CancellationToken, Task<BitmapSource>> CaptureMonitorAsync { get; set; }
        public Func<BitmapSource, CancellationToken, Task<LocalOcrResult>> RecognizeImageAsync { get; set; }

        void BuildOcrControls()
        {
            CaptureRegionAsync = ScreenRegionCapture.CaptureAsync;
            CaptureMonitorAsync = ScreenRegionCapture.CaptureMonitorAsync;
            RecognizeImageAsync = LocalOcrEngine.RecognizeAsync;
            CaptureButton = SmallButton("영역 촬영", CaptureItems);
            CaptureButton.Height = 32; CaptureButton.Padding = new Thickness(7, 0, 7, 0);
            CaptureButton.ToolTip = "마우스로 화면 영역을 지정해 여러 아이템을 한 번에 검색합니다. Esc·오른쪽 클릭으로 취소하며, 이미지와 글자는 이 PC에서만 처리합니다.";
            AutomationProperties.SetName(CaptureButton, "PIP 영역 촬영 OCR 다중 검색");
            var editor = new StackPanel { Margin = new Thickness(0, 7, 0, 0) };
            OcrLinesInput = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 96, MaxLength = 16000,
                FontSize = 13, Padding = new Thickness(8), VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            LedgerControls.StyleTextInput(OcrLinesInput);
            OcrLinesInput.ToolTip = "아이템 이름을 한 줄에 하나씩 수정하세요. 없는 품목도 남겨 두며, 같은 이름은 합칩니다.";
            AutomationProperties.SetName(OcrLinesInput, "인식한 아이템 이름 여러 줄 수정"); editor.Children.Add(OcrLinesInput);
            OcrApplyButton = SmallButton("수정한 이름 검색", ApplyEditedOcr);
            OcrApplyButton.Margin = new Thickness(0, 7, 0, 0); editor.Children.Add(OcrApplyButton);
            OcrEditor = new Expander { Header = "인식한 이름 수정", Content = editor, Foreground = Ink,
                FontSize = 12, Margin = new Thickness(0, 0, 0, 8), HorizontalContentAlignment = HorizontalAlignment.Stretch };
            OcrClearButton = SmallButton("일반 검색", delegate { LeaveOcrResults(); RenderMarketSearch(true); SearchInput.Focus(); });
            OcrClearButton.ToolTip = "촬영 검색 결과를 지우고 이름 검색으로 돌아갑니다.";
            BuildCaptureHotkeyControls();
        }

        async void CaptureItems()
        {
            await CaptureItemsAsync(CaptureRegionAsync, false);
        }
        public async void CaptureMonitorForOcr()
        {
            // Registered hotkeys work while the game keeps keyboard focus.
            // A disabled/hidden PIP must not capture behind a modal or on close.
            if (closed || !IsVisible || WindowState == WindowState.Minimized || !marketSearchRoot.IsEnabled || !PipWindowBehavior.IsNativeEnabled(this)) return;
            await CaptureItemsAsync(CaptureMonitorAsync, true);
        }
        async Task CaptureItemsAsync(Func<Window, CancellationToken, Task<BitmapSource>> captureOperation, bool wholeScreen)
        {
            if (closed || OcrBusy || searchBusy || captureOperation == null || RecognizeImageAsync == null) return;
            int revision = ++ocrRevision;
            var pending = CancellationTokenSource.CreateLinkedTokenSource(searchLifetime.Token); ocrOperation = pending;
            ocrBusy = true;
            try {
                // Full-screen capture freezes the current Alt-held frame before
                // changing any PIP content or scheduling asynchronous work.
                var captureTask = wholeScreen ? captureOperation(this, pending.Token) : null;
                UpdateOcrControls(); searchDelay.Stop();
                if (!wholeScreen) CloseAcquisitionHelp();
                SearchMessage(wholeScreen ? "전체 화면의 아이템 이름을 읽는 중…" : "아이템 이름이 보이는 영역을 드래그하세요. Esc로 취소합니다.", false);
                BitmapSource capture = await (captureTask ?? captureOperation(this, pending.Token));
                if (!OcrCurrent(revision, pending)) return;
                if (capture == null) { SearchMessage("촬영을 취소했습니다. 이전 검색 결과를 유지합니다.", false); return; }
                if (wholeScreen) SelectedTab = 3;
                SearchMessage(wholeScreen ? "전체 화면의 글자를 읽는 중…" : "선택한 영역의 글자를 읽는 중…", false);
                var result = await RecognizeImageAsync(capture, pending.Token);
                capture = null;
                if (!OcrCurrent(revision, pending)) return;
                string text = result == null || result.Lines == null ? "" : String.Join("\n", result.Lines);
                if (String.IsNullOrWhiteSpace(text)) {
                    SearchMessage("글자를 읽지 못했습니다. 아이템 이름이 크게 보이도록 영역을 좁혀 다시 촬영하세요.", true); return;
                }
                OcrLinesInput.Text = text.Length > 16000 ? text.Substring(0, 16000) : text;
                ocrAppliedText = OcrLinesInput.Text; ocrMode = true; ocrWholeScreen = wholeScreen;
                ocrMatches.Clear(); ocrChoices.Clear(); OcrEditor.IsExpanded = false;
                SearchMessage("", false);
                await ResolveOcrTextAsync(true);
            } catch (OperationCanceledException) {
                if (OcrCurrent(revision, pending)) SearchMessage("촬영·인식을 취소했습니다.", false);
            } catch (Exception error) {
                if (OcrCurrent(revision, pending)) SearchMessage(error is InvalidOperationException || error is NotSupportedException
                    ? error.Message : "화면의 글자를 읽지 못했습니다. 창 모드에서 다시 촬영하거나 이름을 직접 입력하세요.", true);
            } finally {
                if (!closed && revision == ocrRevision) { ocrBusy = false; ocrOperation = null; UpdateOcrControls(); }
                pending.Dispose();
            }
        }

        bool OcrCurrent(int revision, CancellationTokenSource operation)
        {
            return !closed && revision == ocrRevision && Object.ReferenceEquals(ocrOperation, operation) && !operation.IsCancellationRequested;
        }
        async void ApplyEditedOcr()
        {
            if (closed || OcrBusy) return;
            if (String.IsNullOrWhiteSpace(OcrLinesInput.Text)) { SearchMessage("검색할 아이템 이름을 한 줄에 하나씩 입력하세요.", true); return; }
            ocrAppliedText = OcrLinesInput.Text; ocrMatches.Clear(); ocrChoices.Clear(); OcrEditor.IsExpanded = false;
            await ResolveOcrTextAsync(true);
        }
        async Task ResolveOcrTextAsync(bool resetScroll)
        {
            if (closed || !ocrMode) return;
            int revision = ++ocrMatchRevision;
            var data = searchSnapshot; string text = ocrAppliedText; bool prioritizeItems = ocrWholeScreen;
            ocrMatching = true; UpdateOcrControls();
            RenderMarketSearch(resetScroll);
            try {
                var matches = await Task.Run(() => new OcrMarketMatcher(data).Resolve(text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries), prioritizeItems), searchLifetime.Token);
                if (closed || !ocrMode || revision != ocrMatchRevision || !Object.ReferenceEquals(data, searchSnapshot)) return;
                ocrMatches = matches; ocrMatching = false;
                RenderMarketSearch(resetScroll);
            } catch (OperationCanceledException) { }
            catch {
                if (!closed && revision == ocrMatchRevision) {
                    ocrMatching = false; RenderMarketSearch(resetScroll);
                    SearchMessage("인식한 이름을 검색하지 못했습니다. 이름을 수정한 뒤 다시 검색하세요.", true);
                }
            } finally {
                if (!closed && revision == ocrMatchRevision) { ocrMatching = false; UpdateOcrControls(); }
            }
        }
        void UpdateOcrControls()
        {
            if (CaptureButton == null) return;
            CaptureButton.IsEnabled = !closed && !OcrBusy && !searchBusy;
            CaptureButton.Content = ocrBusy ? "인식 중…" : "영역 촬영";
            SearchInput.IsEnabled = !OcrBusy;
            SearchRefreshButton.IsEnabled = !closed && !searchBusy && !OcrBusy && refreshSearchData != null;
            OcrApplyButton.IsEnabled = !OcrBusy; OcrLinesInput.IsReadOnly = OcrBusy;
            OcrClearButton.IsEnabled = !OcrBusy;
            UpdateCaptureHotkeyControls();
        }
        void InvalidateOcrMatches()
        {
            ++ocrMatchRevision; ocrMatching = false; ocrMatches.Clear(); ocrChoices.Clear();
        }
        void LeaveOcrResults()
        {
            ++ocrRevision; ++ocrMatchRevision;
            if (ocrOperation != null) ocrOperation.Cancel();
            ocrOperation = null; ocrBusy = false; ocrMatching = false; ocrMode = false;
            ocrAppliedText = ""; ocrMatches.Clear(); ocrChoices.Clear();
            OcrLinesInput.Clear(); OcrEditor.IsExpanded = false; SearchMessage("", false); UpdateOcrControls();
        }
        void CloseOcr()
        {
            ++ocrRevision; ++ocrMatchRevision;
            if (ocrOperation != null) ocrOperation.Cancel();
            ocrMode = false; ocrBusy = false; ocrMatching = false;
            ocrOperation = null; ocrMatches.Clear(); ocrChoices.Clear(); ocrAppliedText = "";
            OcrLinesInput.Clear(); CaptureRegionAsync = null; CaptureMonitorAsync = null; RecognizeImageAsync = null;
            if (captureHotkey != null) captureHotkey.Dispose();
        }

        MarketSearchEntry SelectedOcrItem(OcrMarketMatch match)
        {
            string name;
            if (ocrChoices.TryGetValue(match.Text, out name)) return match.Candidates.FirstOrDefault(item => item.Name == name);
            return match.Exact && match.Candidates.Count == 1 ? match.Candidates[0] : null;
        }
        List<OcrMarketMatch> VisibleOcrMatches()
        {
            var result = new List<OcrMarketMatch>(); var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var match in ocrMatches) {
                var item = SelectedOcrItem(match);
                string key = (item == null ? "raw:" : "item:") + KoreanNameSearch.Normalize(item == null ? match.Text : item.Name);
                if (seen.Add(key)) result.Add(match);
            }
            return result;
        }
        bool RenderOcrResults()
        {
            if (!ocrMode) return false;
            var header = new Grid { Margin = new Thickness(0, 4, 0, 8) };
            header.ColumnDefinitions.Add(new ColumnDefinition()); header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var previousParent = OcrClearButton.Parent as Panel;
            if (previousParent != null) previousParent.Children.Remove(OcrClearButton);
            var visible = VisibleOcrMatches(); int known = visible.Count(match => SelectedOcrItem(match) != null);
            var summary = Label("촬영 검색 · " + visible.Count + "종\n이름 확인 " + known + " · 확인 필요 " + (visible.Count - known), 12, Ink, true);
            summary.TextWrapping = TextWrapping.Wrap; header.Children.Add(summary);
            Grid.SetColumn(OcrClearButton, 1); header.Children.Add(OcrClearButton); SearchResultsPanel.Children.Add(header);
            SearchResultsPanel.Children.Add(OcrEditor);
            if (searchIndex == null) AddSearchNote("저장된 시세가 없습니다. 시세 받기를 누르면 인식한 이름을 공통 데이터에서 다시 찾습니다.");
            if (ocrMatches.Count == 0) AddSearchNote(ocrMatching ? "인식한 이름을 공통 시세에서 찾는 중…" : "검색할 이름이 없습니다. 인식한 글자를 확인해 주세요.");
            var unknown = new StackPanel(); int unknownCount = 0;
            foreach (var match in visible) {
                if (ocrWholeScreen && match.Candidates.Count == 0) { unknown.Children.Add(CreateOcrResult(match)); ++unknownCount; }
                else SearchResultsPanel.Children.Add(CreateOcrResult(match));
            }
            if (unknownCount > 0) SearchResultsPanel.Children.Add(new Expander {
                Header = "이름 미확인 " + unknownCount + "종 · 펼치기", Content = unknown, Foreground = Ink, FontSize = 12,
                HorizontalContentAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 3, 0, 9),
                ToolTip = "아이템이 아닌 화면 문구도 포함될 수 있습니다. 거래 불가 품목으로 확정한 목록은 아닙니다."
            });
            AddSearchNote("개당 참고 최저가 · 같은 이름은 한 번만 표시\n시세 미확인은 거래 불가 판정이 아닙니다.\n한 번에 최대 100종까지 표시합니다.");
            return true;
        }
        Border CreateOcrResult(OcrMarketMatch match)
        {
            var entry = SelectedOcrItem(match);
            bool unnamed = entry != null && entry.IsEnchantScroll && !entry.EnchantNameKnown;
            bool priced = entry != null && !unnamed && entry.HasListing && entry.UnitPrice.HasValue;
            var body = new StackPanel();
            var top = new Grid(); top.ColumnDefinitions.Add(new ColumnDefinition()); top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var name = Label(entry == null ? match.Text : entry.Name, 14, Ink, true); name.TextWrapping = TextWrapping.Wrap;
            name.ToolTip = "인식: " + match.Text; top.Children.Add(name);
            var edit = SmallButton("수정", delegate {
                OcrEditor.IsExpanded = true; OcrLinesInput.Focus();
                int start = OcrLinesInput.Text.IndexOf(match.Text, StringComparison.Ordinal);
                int length = match.Text.Length;
                if (start < 0 && !String.IsNullOrEmpty(match.OriginalText)) {
                    start = OcrLinesInput.Text.IndexOf(match.OriginalText, StringComparison.Ordinal); length = match.OriginalText.Length;
                }
                if (start >= 0) OcrLinesInput.Select(start, length);
                OcrEditor.BringIntoView();
            });
            edit.FontSize = 10; edit.Padding = new Thickness(5, 3, 5, 3); edit.Margin = new Thickness(5, 0, 0, 0);
            Grid.SetColumn(edit, 1); top.Children.Add(edit); body.Children.Add(top);
            string price = priced ? Decimal.Truncate(entry.UnitPrice.Value).ToString("#,0", CultureInfo.CurrentCulture) + " G"
                : entry == null && match.Candidates.Count > 0 ? "이름 확인 필요"
                : unnamed ? "인챈트 이름 미확인"
                : entry != null && entry.FetchedUtc.HasValue && entry.ListingCount == 0 ? "수집 당시 매물 없음" : "시세 미확인";
            var amount = Label(price, 17, priced ? Green : Muted, true); amount.Margin = new Thickness(0, 5, 0, 1); body.Children.Add(amount);
            string detail = priced ? "개당 최저가" + (!entry.PriceComparable ? " · 옵션별 가격 차이 있음" : "")
                : entry == null && match.Candidates.Count > 0 ? "아래 후보에서 정확한 품목을 선택하세요."
                : unnamed ? "인챈트 이름과 스크롤 종류를 확인하세요."
                : "거래 불가·매물 없음·이름 오인식 가능";
            var note = Label(detail, 11, Muted, false); note.TextWrapping = TextWrapping.Wrap; body.Children.Add(note);
            if (entry == null && match.Candidates.Count > 0) {
                var choices = new StackPanel();
                foreach (var candidate in match.Candidates) {
                    var item = candidate;
                    var choose = SmallButton("", delegate { ocrChoices[match.Text] = item.Name; RenderMarketSearch(false); });
                    var label = Label(item.Name, 12, Ink, false); label.TextWrapping = TextWrapping.Wrap; choose.Content = label;
                    choose.Margin = new Thickness(0, 4, 0, 0); choose.HorizontalContentAlignment = HorizontalAlignment.Stretch;
                    AutomationProperties.SetName(choose, item.Name + " OCR 후보 선택"); choices.Children.Add(choose);
                }
                body.Children.Add(new Expander { Header = "이름 후보 " + match.Candidates.Count + "개", Content = choices, FontSize = 12,
                    Foreground = Ink, Margin = new Thickness(0, 6, 0, 0), HorizontalContentAlignment = HorizontalAlignment.Stretch });
            }
            var card = new Border { Child = body, Padding = new Thickness(10), Margin = new Thickness(0, 0, 0, 7),
                Background = AppTheme.Surface, BorderBrush = priced ? Line : Paint("#B99555"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(9) };
            AutomationProperties.SetName(card, match.Text + " 촬영 검색 결과"); return card;
        }
    }
}
