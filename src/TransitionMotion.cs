using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace MabinogiBarter
{
    public static partial class AppMotion
    {
        sealed class ClockTicket
        {
            public DependencyObject Target;
            public DependencyProperty Property;
            public int Token;
            public void Release()
            {
                AnimationTokens stamp; int current;
                if (tokens.TryGetValue(Target, out stamp) && stamp.Values.TryGetValue(Property, out current) && current == Token) Cancel(Target, Property);
            }
        }
        sealed class SequenceState
        {
            public readonly List<ClockTicket> Clocks = new List<ClockTicket>();
            public TransitionAdorner Ghost;
            public int Revision;
            public DispatcherTimer Cleanup;
        }
        sealed class WindowSequenceState { public bool Attached, Closing; }
        sealed class Snapshot
        {
            public BitmapSource Image;
            public Rect Bounds;
        }
        static readonly ConditionalWeakTable<FrameworkElement, SequenceState> sequences = new ConditionalWeakTable<FrameworkElement, SequenceState>();
        static readonly ConditionalWeakTable<Window, WindowSequenceState> windowSequences = new ConditionalWeakTable<Window, WindowSequenceState>();

        // This decorative layer never receives input and never postpones a state change or Window.Close.
        internal sealed class TransitionAdorner : Adorner
        {
            readonly BitmapSource image;
            readonly Rect bounds;
            readonly ScaleTransform shrink;
            readonly TranslateTransform drift = new TranslateTransform();
            AdornerLayer layer;
            public TransitionAdorner(FrameworkElement scope, BitmapSource bitmap, Rect rectangle, AdornerLayer ownerLayer) : base(scope)
            {
                image = bitmap; bounds = rectangle; layer = ownerLayer;
                IsHitTestVisible = false; Focusable = false;
                shrink = new ScaleTransform(1, 1, bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);
                scope.Unloaded += ScopeUnloaded;
            }
            void ScopeUnloaded(object sender, RoutedEventArgs e) { Remove(); }
            protected override void OnRender(DrawingContext context)
            {
                // AdornerLayer owns this element's RenderTransform for coordinate placement.
                context.PushTransform(shrink); context.PushTransform(drift);
                context.DrawImage(image, bounds); context.Pop(); context.Pop();
            }
            public void Fade()
            {
                var animation = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(105)) { FillBehavior = FillBehavior.Stop, EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn } };
                Opacity = 0;
                animation.Completed += delegate { Remove(); };
                BeginAnimation(OpacityProperty, animation, HandoffBehavior.SnapshotAndReplace);
                shrink.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, .992, animation.Duration));
                shrink.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, .992, animation.Duration));
                drift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, -4, animation.Duration));
            }
            public void Remove()
            {
                if (layer == null) return;
                var previous = layer; layer = null;
                ((FrameworkElement)AdornedElement).Unloaded -= ScopeUnloaded;
                BeginAnimation(OpacityProperty, null);
                shrink.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                shrink.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                drift.BeginAnimation(TranslateTransform.YProperty, null);
                previous.Remove(this);
            }
        }

        static SequenceState Sequence(FrameworkElement scope)
        {
            SequenceState sequence;
            if (sequences.TryGetValue(scope, out sequence)) return sequence;
            sequence = new SequenceState(); sequences.Add(scope, sequence);
            scope.Unloaded += delegate { CancelSequence(scope); };
            return sequence;
        }
        static void CancelSequence(FrameworkElement scope)
        {
            SequenceState sequence;
            if (!sequences.TryGetValue(scope, out sequence)) return;
            sequence.Revision++;
            if (sequence.Cleanup != null) { sequence.Cleanup.Stop(); sequence.Cleanup = null; }
            foreach (var clock in sequence.Clocks) clock.Release();
            sequence.Clocks.Clear();
            if (sequence.Ghost != null) { sequence.Ghost.Remove(); sequence.Ghost = null; }
        }
        static void CancelChildSequences(FrameworkElement scope)
        {
            CancelSequence(scope);
            foreach (var child in LogicalChildren(scope)) CancelChildSequences(child);
        }

        public static void Transition(FrameworkElement scope, Action change)
        {
            if (change == null) throw new ArgumentNullException("change");
            if (scope == null || !Enabled || !scope.IsLoaded || !scope.IsVisible)
            {
                if (scope != null) CancelChildSequences(scope);
                change(); return;
            }
            var snapshot = Capture(scope);
            CancelChildSequences(scope);
            var sequence = Sequence(scope);
            sequence.Ghost = ShowGhost(scope, snapshot);
            try
            {
                // The new state, controls, focus rules and scroll restoration are synchronous.
                change();
                Stage(scope, sequence.Ghost == null ? 0 : 85, false);
            }
            catch { CancelSequence(scope); throw; }
        }
        public static void Reveal(FrameworkElement scope)
        {
            if (scope == null) return;
            CancelChildSequences(scope);
            if (!Enabled) return;
            Stage(scope, 0, true);
        }
        static void Stage(FrameworkElement scope, int initialDelay, bool allowDeferred)
        {
            if (!scope.IsLoaded)
            {
                if (!allowDeferred) return;
                var pending = Sequence(scope); int revision = ++pending.Revision;
                RoutedEventHandler loaded = null;
                loaded = delegate {
                    scope.Loaded -= loaded;
                    if (revision == pending.Revision && Enabled) Stage(scope, initialDelay, false);
                };
                scope.Loaded += loaded;
                return;
            }
            if (!scope.IsVisible) return;
            scope.UpdateLayout();
            var units = new List<FrameworkElement>(); CollectUnits(scope, true, 0, units);
            if (units.Count == 0) units.Add(scope);
            units = units.Distinct().OrderBy(e => Position(e, scope).Y).ThenBy(e => Position(e, scope).X).ToList();
            var sequence = Sequence(scope); int currentRevision = ++sequence.Revision;
            Action finish = delegate {
                if (sequence.Revision != currentRevision) return;
                if (sequence.Cleanup != null) { sequence.Cleanup.Stop(); sequence.Cleanup = null; }
                foreach (var clock in sequence.Clocks) clock.Release();
                sequence.Clocks.Clear();
                if (sequence.Ghost != null) { sequence.Ghost.Remove(); sequence.Ghost = null; }
            };
            for (int i = 0; i < units.Count; i++)
            {
                var unit = units[i]; var motion = State(unit);
                int delay = initialDelay + Math.Min(i, 9) * 24;
                double opacity = (double)unit.GetAnimationBaseValue(UIElement.OpacityProperty);
                sequence.Clocks.Add(Arrival(unit, UIElement.OpacityProperty, 0, opacity, delay, 230, false));
                sequence.Clocks.Add(Arrival(motion.Offset, TranslateTransform.YProperty, 13, 0, delay, 260, true));
                sequence.Clocks.Add(Arrival(motion.Scale, ScaleTransform.ScaleXProperty, .985, 1, delay, 260, true));
                bool last = i == units.Count - 1;
                sequence.Clocks.Add(Arrival(motion.Scale, ScaleTransform.ScaleYProperty, .985, 1, delay, 260, true, last ? finish : null));
            }
            // Hover/press feedback may replace the final unit's clock. Cleanup must still run.
            sequence.Cleanup = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(initialDelay + Math.Min(units.Count - 1, 9) * 24 + 280) };
            sequence.Cleanup.Tick += delegate { finish(); };
            sequence.Cleanup.Start();
        }
        static ClockTicket Arrival(DependencyObject target, DependencyProperty property, double from, double to, int delay, int duration, bool setBase, Action completed = null)
        {
            Cancel(target, property);
            if (setBase) target.SetCurrentValue(property, to);
            var stamp = tokens.GetOrCreateValue(target); int token = stamp.Values[property];
            var frames = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
            // A zero-time keyframe holds hidden geometry during the stagger delay.
            frames.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            if (delay > 0) frames.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(delay))));
            frames.KeyFrames.Add(new EasingDoubleKeyFrame(to, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(delay + duration)), new CubicEase { EasingMode = EasingMode.EaseOut }));
            frames.Completed += delegate {
                int current;
                if (stamp.Values.TryGetValue(property, out current) && current == token) Begin(target, property, null);
                if (completed != null) completed();
            };
            Begin(target, property, frames);
            return new ClockTicket { Target = target, Property = property, Token = token };
        }

        static IEnumerable<FrameworkElement> LogicalChildren(FrameworkElement element)
        {
            var panel = element as Panel; if (panel != null) return panel.Children.OfType<FrameworkElement>();
            var presenter = element as ItemsPresenter;
            if (presenter != null) return Enumerable.Range(0, VisualTreeHelper.GetChildrenCount(presenter)).Select(i => VisualTreeHelper.GetChild(presenter, i)).OfType<FrameworkElement>();
            var decorator = element as Decorator; if (decorator != null) return decorator.Child == null ? Enumerable.Empty<FrameworkElement>() : new[] { decorator.Child }.OfType<FrameworkElement>();
            var viewbox = element as Viewbox; if (viewbox != null) return viewbox.Child == null ? Enumerable.Empty<FrameworkElement>() : new[] { viewbox.Child }.OfType<FrameworkElement>();
            var content = element as ContentControl; if (content != null && !(element is System.Windows.Controls.Primitives.ButtonBase)) return new[] { content.Content }.OfType<FrameworkElement>();
            return Enumerable.Empty<FrameworkElement>();
        }
        static Point Position(FrameworkElement element, FrameworkElement scope)
        {
            try { return element.TranslatePoint(new Point(), scope); } catch (InvalidOperationException) { return new Point(); }
        }
        static void CollectUnits(FrameworkElement element, bool unwrap, int depth, List<FrameworkElement> units)
        {
            if (element.Visibility != Visibility.Visible || !element.IsVisible || element.ActualWidth < 1 || element.ActualHeight < 1 || VisibleBounds(element).IsEmpty) return;
            var children = LogicalChildren(element).Where(c => c.Visibility == Visibility.Visible).ToList();
            bool structural = element is Panel || element is ScrollViewer || element is Viewbox;
            if (depth > 12 || children.Count == 0 || (!unwrap && !structural) || element is Expander)
            { units.Add(element); return; }
            if (!unwrap && element is Panel && children.Count > 1 && element.ActualHeight <= 95)
            { units.Add(element); return; }
            foreach (var child in children) CollectUnits(child, children.Count == 1, depth + 1, units);
        }
        static Rect VisibleBounds(FrameworkElement scope)
        {
            Rect bounds = new Rect(0, 0, Math.Max(0, scope.ActualWidth), Math.Max(0, scope.ActualHeight));
            for (DependencyObject node = Parent(scope); node != null; node = Parent(node))
            {
                var ancestor = node as FrameworkElement;
                if (ancestor == null || (!ancestor.ClipToBounds && !(ancestor is ScrollContentPresenter) && !(ancestor is Window))) continue;
                try
                {
                    var inverse = scope.TransformToAncestor(ancestor).Inverse;
                    if (inverse != null) bounds.Intersect(inverse.TransformBounds(new Rect(0, 0, Math.Max(0, ancestor.ActualWidth), Math.Max(0, ancestor.ActualHeight))));
                }
                catch (InvalidOperationException) { }
                if (bounds.IsEmpty) break;
            }
            return bounds;
        }
        static Snapshot Capture(FrameworkElement scope)
        {
            if (!scope.IsLoaded || !scope.IsVisible) return null;
            var bounds = VisibleBounds(scope); if (bounds.IsEmpty || bounds.Width < 1 || bounds.Height < 1) return null;
            try
            {
                double scale = Math.Min(1, 1500 / Math.Max(bounds.Width, bounds.Height));
                var bitmap = new RenderTargetBitmap(Math.Max(1, (int)Math.Ceiling(bounds.Width * scale)), Math.Max(1, (int)Math.Ceiling(bounds.Height * scale)), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
                var visual = new DrawingVisual();
                using (var drawing = visual.RenderOpen())
                {
                    var brush = new VisualBrush(scope) { Viewbox = bounds, ViewboxUnits = BrushMappingMode.Absolute, Stretch = Stretch.Fill };
                    drawing.DrawRectangle(brush, null, new Rect(0, 0, bounds.Width, bounds.Height));
                }
                bitmap.Render(visual); bitmap.Freeze();
                return new Snapshot { Image = bitmap, Bounds = bounds };
            }
            catch (InvalidOperationException) { return null; }
            catch (ArgumentException) { return null; }
        }
        static TransitionAdorner ShowGhost(FrameworkElement scope, Snapshot snapshot)
        {
            if (snapshot == null || !Enabled || !scope.IsLoaded) return null;
            var layer = AdornerLayer.GetAdornerLayer(scope); if (layer == null) return null;
            var ghost = new TransitionAdorner(scope, snapshot.Image, snapshot.Bounds, layer);
            layer.Add(ghost); ghost.Fade(); return ghost;
        }
        static void AttachWindowSequence(Window window)
        {
            WindowSequenceState state;
            if (!windowSequences.TryGetValue(window, out state)) { state = new WindowSequenceState(); windowSequences.Add(window, state); }
            if (state.Attached) return; state.Attached = true;
            Snapshot closing = null; Point corner = new Point(); Window owner = null;
            window.Loaded += delegate { Reveal(window.Content as FrameworkElement); };
            window.Closing += delegate(object sender, CancelEventArgs e) {
                state.Closing = true; closing = null; owner = window.Owner;
                var surface = window.Content as FrameworkElement;
                if (!e.Cancel && Enabled && owner != null && window.IsActive && surface != null && owner.IsVisible && owner.WindowState != WindowState.Minimized)
                {
                    WindowSequenceState ownerState;
                    if (!windowSequences.TryGetValue(owner, out ownerState) || !ownerState.Closing)
                    {
                        closing = Capture(surface);
                        if (closing != null) corner = surface.PointToScreen(closing.Bounds.TopLeft);
                    }
                }
                // A later Closing handler may cancel. Never leave a cancelled close marked as shutdown.
                window.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(delegate { if (window.IsVisible) { state.Closing = false; closing = null; } }));
            };
            window.Closed += delegate {
                var snapshot = closing; closing = null; var destination = owner;
                if (snapshot == null || destination == null || window.Dispatcher.HasShutdownStarted) return;
                window.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(delegate {
                    WindowSequenceState ownerState;
                    if (!Enabled || !destination.IsVisible || destination.WindowState == WindowState.Minimized || (windowSequences.TryGetValue(destination, out ownerState) && ownerState.Closing)) return;
                    var surface = destination.Content as FrameworkElement; if (surface == null || !surface.IsLoaded) return;
                    var local = surface.PointFromScreen(corner);
                    snapshot.Bounds = new Rect(local, snapshot.Bounds.Size);
                    ShowGhost(surface, snapshot);
                }));
            };
        }

        sealed class DisclosureState { public Snapshot Pending; }
        static readonly ConditionalWeakTable<Expander, DisclosureState> disclosures = new ConditionalWeakTable<Expander, DisclosureState>();
        static readonly ConditionalWeakTable<ComboBox, object> dropdowns = new ConditionalWeakTable<ComboBox, object>();
        static void InitializeSequences()
        {
            EventManager.RegisterClassHandler(typeof(Expander), UIElement.PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler(BeforeDisclosureMouse), true);
            EventManager.RegisterClassHandler(typeof(Expander), UIElement.PreviewMouseLeftButtonUpEvent, new MouseButtonEventHandler(AfterDisclosureMouse), true);
            EventManager.RegisterClassHandler(typeof(Expander), UIElement.LostMouseCaptureEvent, new MouseEventHandler(AfterDisclosureCapture), true);
            EventManager.RegisterClassHandler(typeof(Expander), UIElement.PreviewKeyDownEvent, new KeyEventHandler(BeforeDisclosureKey), true);
            EventManager.RegisterClassHandler(typeof(Expander), UIElement.PreviewKeyUpEvent, new KeyEventHandler(AfterDisclosureKey), true);
            EventManager.RegisterClassHandler(typeof(Expander), Expander.CollapsedEvent, new RoutedEventHandler(DisclosureCollapsed), true);
        }
        static void CaptureDisclosure(Expander expander, object source)
        {
            if (!Enabled || !expander.IsExpanded) return;
            ButtonBase button = null;
            for (var node = source as DependencyObject; node != null; node = Parent(node))
                if (node is ButtonBase) { button = (ButtonBase)node; break; }
            if (button == null || !Object.ReferenceEquals(button.TemplatedParent, expander)) return;
            DisclosureState state;
            if (!disclosures.TryGetValue(expander, out state))
            {
                state = new DisclosureState(); disclosures.Add(expander, state);
                expander.Unloaded += delegate { state.Pending = null; };
            }
            state.Pending = Capture(expander);
        }
        static void ClearDisclosureLater(Expander expander)
        {
            expander.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(delegate {
                DisclosureState state; if (disclosures.TryGetValue(expander, out state)) state.Pending = null;
            }));
        }
        static void BeforeDisclosureMouse(object sender, MouseButtonEventArgs e) { CaptureDisclosure((Expander)sender, e.OriginalSource); }
        static void AfterDisclosureMouse(object sender, MouseButtonEventArgs e) { ClearDisclosureLater((Expander)sender); }
        static void AfterDisclosureCapture(object sender, MouseEventArgs e) { ClearDisclosureLater((Expander)sender); }
        static void BeforeDisclosureKey(object sender, KeyEventArgs e) { if (e.Key == Key.Space || e.Key == Key.Enter) CaptureDisclosure((Expander)sender, e.OriginalSource); }
        static void AfterDisclosureKey(object sender, KeyEventArgs e) { ClearDisclosureLater((Expander)sender); }
        static void DisclosureCollapsed(object sender, RoutedEventArgs e)
        {
            var expander = (Expander)sender; DisclosureState state;
            if (!Object.ReferenceEquals(expander, e.OriginalSource) || !disclosures.TryGetValue(expander, out state)) return;
            var snapshot = state.Pending; state.Pending = null;
            CancelSequence(expander); Sequence(expander).Ghost = ShowGhost(expander, snapshot);
        }
        public static void Dropdown(ComboBox combo)
        {
            object marker;
            if (combo == null || dropdowns.TryGetValue(combo, out marker)) return;
            dropdowns.Add(combo, new object());
            combo.DropDownOpened += delegate {
                var popup = combo.Template.FindName("PART_Popup", combo) as Popup;
                if (popup == null) return;
                popup.PopupAnimation = PopupAnimation.None;
                // Popup HWND visibility settles after DropDownOpened and its Loaded notifications.
                combo.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(delegate {
                    if (combo.IsLoaded && combo.IsDropDownOpen && popup.IsOpen) Reveal(popup.Child as FrameworkElement);
                }));
            };
        }
    }
}
