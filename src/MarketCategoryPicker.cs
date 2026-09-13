using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;

namespace MabinogiBarter
{
    // Local navigation only. Stable node IDs distinguish an entire group from
    // a same-named leaf (accessories, totems and miscellaneous items).
    public sealed class MarketCategoryPicker : UserControl
    {
        readonly Dictionary<string, TreeViewItem> entries = new Dictionary<string, TreeViewItem>(StringComparer.Ordinal);
        bool updating;
        int navigationVersion;
        public TreeView Tree { get; private set; }
        public string SelectedId { get; private set; }
        public event EventHandler SelectionChanged;

        public MarketCategoryPicker()
        {
            SelectedId = MarketCategories.AllId;
            var frame = new Border { Background = AppTheme.Surface, BorderBrush = AppTheme.Brush("#DCE5DF"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(9), Padding = new Thickness(5) };
            var grid = new Grid(); grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); grid.RowDefinitions.Add(new RowDefinition()); frame.Child = grid; Content = frame;
            grid.Children.Add(new TextBlock { Text = "아이템 분류", FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = AppTheme.Brush("#202D35"), Margin = new Thickness(9, 8, 5, 10) });
            Tree = new TreeView { Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0), FontSize = 12, Foreground = AppTheme.Brush("#202D35"), HorizontalContentAlignment = HorizontalAlignment.Stretch };
            Tree.Resources["CategoryInk"] = AppTheme.Brush("#202D35"); Tree.Resources["CategoryAccent"] = AppTheme.Brush("#226C54");
            Tree.Resources["CategoryHover"] = AppTheme.Brush("#F4F6F5"); Tree.Resources["CategorySelected"] = AppTheme.Brush("#EAF3E9");
            Tree.Resources[typeof(TreeViewItem)] = (Style)XamlReader.Parse(ItemStyle);
            ScrollViewer.SetHorizontalScrollBarVisibility(Tree, ScrollBarVisibility.Disabled);
            ScrollViewer.SetVerticalScrollBarVisibility(Tree, ScrollBarVisibility.Auto);
            AutomationProperties.SetName(Tree, "시장 통계 아이템 분류"); Grid.SetRow(Tree, 1); grid.Children.Add(Tree);
            Tree.SelectedItemChanged += Changed;
            UpdateCategories(new string[0]);
        }

        public void UpdateCategories(IEnumerable<string> observed)
        {
            var names = (observed ?? new string[0]).ToList(); TreeViewItem previous;
            if (entries.TryGetValue(SelectedId, out previous)) { var node = (MarketCategoryNode)previous.Tag; if (!node.IsGroup && node.Id != MarketCategories.AllId) names.Add(node.Name); }
            var roots = MarketCategories.Build(names);
            var ids = Flatten(roots).Select(node => node.Id).ToList();
            if (ids.SequenceEqual(FlattenItems(Tree.Items).Select(item => ((MarketCategoryNode)item.Tag).Id))) return;
            var expanded = new HashSet<string>(entries.Where(pair => pair.Value.IsExpanded).Select(pair => pair.Key), StringComparer.Ordinal);
            var scroll = FindScroll(Tree); double offset = scroll == null ? 0 : scroll.VerticalOffset;
            updating = true;
            try {
                entries.Clear(); Tree.Items.Clear();
                foreach (var node in roots) Tree.Items.Add(Create(node, expanded));
                if (!Select(SelectedId)) Select(MarketCategories.AllId);
            } finally { updating = false; }
            int version = navigationVersion;
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(delegate { if (version != navigationVersion) return; var current = FindScroll(Tree); if (current != null) current.ScrollToVerticalOffset(offset); }));
        }

        TreeViewItem Create(MarketCategoryNode node, HashSet<string> expanded)
        {
            var item = new TreeViewItem { Header = node.Name, Tag = node, IsExpanded = expanded.Contains(node.Id), Cursor = Cursors.Hand, HorizontalContentAlignment = HorizontalAlignment.Stretch, ToolTip = node.Path };
            AutomationProperties.SetName(item, node.Path);
            if (node.IsGroup || node.Id == MarketCategories.AllId) item.FontWeight = FontWeights.SemiBold;
            entries.Add(node.Id, item);
            foreach (var child in node.Children) item.Items.Add(Create(child, expanded));
            return item;
        }

        public bool Select(string id)
        {
            TreeViewItem item; if (id == null || !entries.TryGetValue(id, out item)) return false;
            if (!updating) {
                navigationVersion++;
                var ancestor = ItemsControl.ItemsControlFromItemContainer(item) as TreeViewItem;
                while (ancestor != null) { ancestor.IsExpanded = true; ancestor = ItemsControl.ItemsControlFromItemContainer(ancestor) as TreeViewItem; }
                if (item.HasItems) item.IsExpanded = true;
            }
            SelectedId = id; item.IsSelected = true;
            if (!updating) item.BringIntoView();
            return true;
        }

        void Changed(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            var item = e.NewValue as TreeViewItem; if (item == null) return;
            SelectedId = ((MarketCategoryNode)item.Tag).Id;
            if (!updating) {
                navigationVersion++;
                // WPF selects the parent when a branch containing the selection
                // is collapsed. Respect that collapse instead of reopening it.
                bool collapsedAncestor = !item.IsExpanded && Contains(item, e.OldValue as TreeViewItem);
                if (item.HasItems && !collapsedAncestor) item.IsExpanded = true;
                if (SelectionChanged != null) SelectionChanged(this, EventArgs.Empty);
            }
            e.Handled = true;
        }
        static bool Contains(TreeViewItem parent, TreeViewItem child) { while (child != null) { child = ItemsControl.ItemsControlFromItemContainer(child) as TreeViewItem; if (Object.ReferenceEquals(parent, child)) return true; } return false; }
        static IEnumerable<MarketCategoryNode> Flatten(IEnumerable<MarketCategoryNode> nodes) { foreach (var node in nodes) { yield return node; foreach (var child in Flatten(node.Children)) yield return child; } }
        static IEnumerable<TreeViewItem> FlattenItems(ItemCollection items) { foreach (TreeViewItem item in items) { yield return item; foreach (var child in FlattenItems(item.Items)) yield return child; } }
        static ScrollViewer FindScroll(DependencyObject root) { var found = root as ScrollViewer; if (found != null) return found; for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) { var child = FindScroll(VisualTreeHelper.GetChild(root, i)); if (child != null) return child; } return null; }

        const string ItemStyle = @"
<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='{x:Type TreeViewItem}'>
 <Setter Property='Foreground' Value='{DynamicResource CategoryInk}'/><Setter Property='HorizontalContentAlignment' Value='Stretch'/><Setter Property='FontWeight' Value='Normal'/><Setter Property='FocusVisualStyle' Value='{x:Null}'/>
 <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='{x:Type TreeViewItem}'>
  <Grid><Grid.RowDefinitions><RowDefinition Height='Auto'/><RowDefinition Height='Auto'/></Grid.RowDefinitions>
   <Border x:Name='Row' Background='Transparent' BorderBrush='Transparent' BorderThickness='1' CornerRadius='6' MinHeight='31' Margin='0,1'>
    <Grid><Grid.ColumnDefinitions><ColumnDefinition Width='21'/><ColumnDefinition Width='*'/></Grid.ColumnDefinitions>
     <ToggleButton x:Name='Expander' Focusable='False' IsChecked='{Binding IsExpanded, RelativeSource={RelativeSource TemplatedParent}, Mode=TwoWay}'>
      <ToggleButton.Template><ControlTemplate TargetType='{x:Type ToggleButton}'><Border Background='Transparent'><Path x:Name='Arrow' Data='M 0 0 L 4 4 L 0 8' Stroke='{DynamicResource CategoryInk}' StrokeThickness='1.4' HorizontalAlignment='Center' VerticalAlignment='Center' RenderTransformOrigin='0.5,0.5'/></Border><ControlTemplate.Triggers><Trigger Property='IsChecked' Value='True'><Setter TargetName='Arrow' Property='RenderTransform'><Setter.Value><RotateTransform Angle='90'/></Setter.Value></Setter></Trigger></ControlTemplate.Triggers></ControlTemplate></ToggleButton.Template>
     </ToggleButton>
     <ContentPresenter x:Name='PART_Header' Grid.Column='1' ContentSource='Header' VerticalAlignment='Center' Margin='2,5,5,5'/>
    </Grid>
   </Border>
   <ItemsPresenter x:Name='Children' Grid.Row='1' Margin='11,0,0,0'/>
  </Grid>
  <ControlTemplate.Triggers>
   <Trigger Property='HasItems' Value='False'><Setter TargetName='Expander' Property='Visibility' Value='Hidden'/></Trigger>
   <Trigger Property='IsExpanded' Value='False'><Setter TargetName='Children' Property='Visibility' Value='Collapsed'/></Trigger>
   <Trigger SourceName='Row' Property='IsMouseOver' Value='True'><Setter TargetName='Row' Property='Background' Value='{DynamicResource CategoryHover}'/></Trigger>
   <Trigger Property='IsSelected' Value='True'><Setter TargetName='Row' Property='Background' Value='{DynamicResource CategorySelected}'/><Setter TargetName='Row' Property='BorderBrush' Value='{DynamicResource CategoryAccent}'/><Setter Property='Foreground' Value='{DynamicResource CategoryAccent}'/></Trigger>
   <Trigger Property='IsKeyboardFocused' Value='True'><Setter TargetName='Row' Property='BorderBrush' Value='{DynamicResource CategoryAccent}'/></Trigger>
  </ControlTemplate.Triggers>
 </ControlTemplate></Setter.Value></Setter>
</Style>";
    }
}
