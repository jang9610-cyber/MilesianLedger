using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MabinogiBarter
{
    // All images are local assets. Rendering never downloads an icon or calls an API.
    public sealed class ItemIconEntry
    {
        public string name { get; set; }
        public string file { get; set; }
        public string status { get; set; }
        public List<string> aliases { get; set; }
        public string sourcePage { get; set; }
    }
    public sealed class ItemIconManifest
    {
        public string source { get; set; }
        public string sourceUrl { get; set; }
        public List<ItemIconEntry> items { get; set; }
    }
    public sealed class ItemIconCatalog
    {
        readonly string directory;
        readonly Dictionary<string, ItemIconEntry> names = new Dictionary<string, ItemIconEntry>(StringComparer.Ordinal);
        readonly Dictionary<string, BitmapSource> images = new Dictionary<string, BitmapSource>(StringComparer.Ordinal);
        public int Count { get; private set; }
        public string SourceName { get; private set; }
        public string SourceUrl { get; private set; }

        public ItemIconCatalog(string assetDirectory)
        {
            directory = Path.GetFullPath(assetDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            try
            {
                var manifest = new JavaScriptSerializer().Deserialize<ItemIconManifest>(File.ReadAllText(Path.Combine(directory, "manifest.json"), Encoding.UTF8));
                if (manifest == null) return;
                SourceName = manifest.source; SourceUrl = manifest.sourceUrl;
                foreach (var item in manifest.items ?? new List<ItemIconEntry>())
                {
                    if (item.status != "ready" || String.IsNullOrEmpty(item.name) || String.IsNullOrEmpty(item.file)) continue;
                    string path = Path.GetFullPath(Path.Combine(directory, item.file));
                    if (!path.StartsWith(directory, StringComparison.OrdinalIgnoreCase) || !String.Equals(Path.GetExtension(path), ".png", StringComparison.OrdinalIgnoreCase)) continue;
                    names[item.name] = item;
                    foreach (var alias in item.aliases ?? new List<string>()) if (!String.IsNullOrEmpty(alias)) names[alias] = item;
                    Count++;
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
        }

        public string GetSourcePage(string name)
        {
            ItemIconEntry item;
            return name != null && names.TryGetValue(name, out item) ? item.sourcePage : null;
        }

        public BitmapSource Get(string name)
        {
            ItemIconEntry item;
            if (String.IsNullOrEmpty(name) || !names.TryGetValue(name, out item)) return null;
            BitmapSource result;
            if (images.TryGetValue(item.name, out result)) return result;
            result = null;
            try
            {
                using (var stream = File.OpenRead(Path.Combine(directory, item.file)))
                {
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze();
                    result = bitmap;
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (NotSupportedException) { }
            catch (FileFormatException) { }
            images[item.name] = result;
            return result;
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
            var missing = expected.Where(n => Get(n) == null).ToList();
            if (missing.Count > 0) throw new Exception("Missing item icons: " + String.Join(", ", missing));
            if (Count != 145) throw new Exception("Expected 145 packaged item icons, got " + Count);
            return "PASS icons: 145 local PNGs decoded; all " + expected.Count + " catalogue and recursive ingredient names and aliases resolved without network access.";
        }
    }

    public sealed partial class MainWindow
    {
        readonly ItemIconCatalog itemIcons = new ItemIconCatalog(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "assets", "item-icons"));

        FrameworkElement ItemLabel(string name, TextBlock label, double size)
        {
            var row = new Grid { VerticalAlignment = VerticalAlignment.Center };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(size + 9) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var source = itemIcons.Get(name);
            var frame = new Border { Width = size, Height = size, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left };
            if (source != null)
            {
                double scale = Math.Min(1, size / Math.Max(source.PixelWidth, source.PixelHeight));
                var image = new Image { Source = source, Width = source.PixelWidth * scale, Height = source.PixelHeight * scale, Stretch = Stretch.Uniform, ToolTip = name };
                AutomationProperties.SetName(image, name + " 아이콘");
                RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.NearestNeighbor);
                frame.Child = image;
            }
            else
            {
                frame.Child = T("·", 18, Muted, false);
                frame.ToolTip = "아이콘 파일 없음: " + name;
            }
            row.Children.Add(frame); Grid.SetColumn(label, 1); row.Children.Add(label);
            var help = AcquisitionHelp(name); Grid.SetColumn(help, 2); row.Children.Add(help);
            return row;
        }

        public string RunIconChecks() { return itemIcons.Verify(catalog); }
    }
}
