using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace MabinogiBarter
{
    // Short, interruptible render animations: input and calculations never wait for them.
    public static partial class AppMotion
    {
        sealed class MotionState
        {
            public ScaleTransform Scale = new ScaleTransform(1, 1);
            public TranslateTransform Offset = new TranslateTransform();
            public bool HoverCard;
        }
        sealed class AnimationTokens { public AnimationTokens() {} public readonly Dictionary<DependencyProperty, int> Values = new Dictionary<DependencyProperty, int>(); }
        static readonly ConditionalWeakTable<FrameworkElement, MotionState> states = new ConditionalWeakTable<FrameworkElement, MotionState>();
        static readonly ConditionalWeakTable<DependencyObject, AnimationTokens> tokens = new ConditionalWeakTable<DependencyObject, AnimationTokens>();
        static bool initialized;
        public static bool ReducedMotion { get; set; }
        public static bool Enabled { get { return !ReducedMotion && SystemParameters.ClientAreaAnimation; } }

        public static void Initialize()
        {
            if (initialized) return;
            initialized = true;
            EventManager.RegisterClassHandler(typeof(ButtonBase), UIElement.MouseEnterEvent, new MouseEventHandler(ButtonHover));
            EventManager.RegisterClassHandler(typeof(ButtonBase), UIElement.MouseLeaveEvent, new MouseEventHandler(ButtonLeave));
            EventManager.RegisterClassHandler(typeof(ButtonBase), UIElement.PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler(ButtonDown), true);
            EventManager.RegisterClassHandler(typeof(ButtonBase), UIElement.PreviewMouseLeftButtonUpEvent, new MouseButtonEventHandler(ButtonUp), true);
            EventManager.RegisterClassHandler(typeof(ButtonBase), UIElement.LostMouseCaptureEvent, new MouseEventHandler(ButtonRelease), true);
            EventManager.RegisterClassHandler(typeof(ButtonBase), UIElement.PreviewKeyDownEvent, new KeyEventHandler(ButtonKeyDown), true);
            EventManager.RegisterClassHandler(typeof(ButtonBase), UIElement.PreviewKeyUpEvent, new KeyEventHandler(ButtonKeyUp), true);
            EventManager.RegisterClassHandler(typeof(ButtonBase), Keyboard.LostKeyboardFocusEvent, new KeyboardFocusChangedEventHandler(ButtonFocusLost), true);
            EventManager.RegisterClassHandler(typeof(ButtonBase), ButtonBase.ClickEvent, new RoutedEventHandler(ButtonClick), true);
            EventManager.RegisterClassHandler(typeof(ToggleButton), ToggleButton.CheckedEvent, new RoutedEventHandler(ToggleChanged), true);
            EventManager.RegisterClassHandler(typeof(ToggleButton), ToggleButton.UncheckedEvent, new RoutedEventHandler(ToggleChanged), true);
            EventManager.RegisterClassHandler(typeof(Expander), Expander.ExpandedEvent, new RoutedEventHandler(ExpandChanged), true);
            EventManager.RegisterClassHandler(typeof(Expander), Expander.CollapsedEvent, new RoutedEventHandler(ExpandChanged), true);
            EventManager.RegisterClassHandler(typeof(ToolTip), ToolTip.OpenedEvent, new RoutedEventHandler(TooltipOpened), true);
            EventManager.RegisterClassHandler(typeof(ComboBox), Selector.SelectionChangedEvent, new SelectionChangedEventHandler(SelectionChanged), true);
            InitializeSequences();
        }

        static MotionState State(FrameworkElement element)
        {
            MotionState existing;
            if (states.TryGetValue(element, out existing)) return existing;
            var state = new MotionState();
            var transform = new TransformGroup();
            if (element.RenderTransform != null && element.RenderTransform != Transform.Identity) transform.Children.Add(element.RenderTransform);
            transform.Children.Add(state.Scale); transform.Children.Add(state.Offset);
            element.RenderTransformOrigin = new Point(.5, .5); element.RenderTransform = transform;
            states.Add(element, state);
            element.IsEnabledChanged += delegate { if (!element.IsEnabled) {
                Stop(state.Scale, ScaleTransform.ScaleXProperty, 1); Stop(state.Scale, ScaleTransform.ScaleYProperty, 1);
                Stop(state.Offset, TranslateTransform.YProperty, 0);
            } };
            element.Unloaded += delegate {
                Stop(state.Scale, ScaleTransform.ScaleXProperty, 1); Stop(state.Scale, ScaleTransform.ScaleYProperty, 1);
                Stop(state.Offset, TranslateTransform.YProperty, 0);
                // Release any fade clock without changing an element's original opacity.
                Cancel(element, UIElement.OpacityProperty);
            };
            return state;
        }
        static void Cancel(DependencyObject target, DependencyProperty property)
        {
            var stamp = tokens.GetOrCreateValue(target); int token; stamp.Values.TryGetValue(property, out token); stamp.Values[property] = token + 1;
            Begin(target, property, null);
        }
        static void Stop(DependencyObject target, DependencyProperty property, double value)
        {
            Cancel(target, property); target.SetCurrentValue(property, value);
        }
        static void Begin(DependencyObject target, DependencyProperty property, AnimationTimeline animation)
        {
            var element = target as UIElement;
            if (element != null) element.BeginAnimation(property, animation, HandoffBehavior.SnapshotAndReplace);
            else ((Animatable)target).BeginAnimation(property, animation, HandoffBehavior.SnapshotAndReplace);
        }
        static void Animate(DependencyObject target, DependencyProperty property, double from, double to, int milliseconds, bool setBase)
        {
            var stamp = tokens.GetOrCreateValue(target); int token; stamp.Values.TryGetValue(property, out token); token++; stamp.Values[property] = token;
            Begin(target, property, null);
            if (setBase) target.SetCurrentValue(property, to);
            if (!Enabled || Math.Abs(from - to) < .0001) return;
            var animation = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(milliseconds)) {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop
            };
            animation.Completed += delegate { int latest; if (stamp.Values.TryGetValue(property, out latest) && latest == token) Begin(target, property, null); };
            Begin(target, property, animation);
        }
        static void Scale(FrameworkElement element, double target, int milliseconds)
        {
            if (!element.IsLoaded || !element.IsEnabled) return;
            if (!Enabled) target = 1;
            var state = State(element);
            Animate(state.Scale, ScaleTransform.ScaleXProperty, state.Scale.ScaleX, target, milliseconds, true);
            Animate(state.Scale, ScaleTransform.ScaleYProperty, state.Scale.ScaleY, target, milliseconds, true);
        }
        static void Lift(FrameworkElement element, double target)
        {
            if (!Enabled) target = 0;
            var state = State(element);
            Animate(state.Offset, TranslateTransform.YProperty, state.Offset.Y, target, 170, true);
        }
        public static void Enter(FrameworkElement element)
        {
            Reveal(element);
        }
        public static void Feedback(FrameworkElement element)
        {
            if (element == null || !element.IsLoaded || !Enabled) return;
            State(element);
            double opacity = (double)element.GetAnimationBaseValue(UIElement.OpacityProperty);
            Animate(element, UIElement.OpacityProperty, opacity * .68, opacity, 170, false);
        }
        public static void SetText(TextBlock element, string text)
        {
            if (element.Text == text) return;
            element.Text = text; Feedback(element);
        }
        public static void HoverCard(FrameworkElement element)
        {
            var state = State(element); if (state.HoverCard) return; state.HoverCard = true;
            element.MouseEnter += delegate { if (element.IsEnabled) Lift(element, -2); };
            element.MouseLeave += delegate { Lift(element, 0); };
        }
        public static void WindowContent(Window window)
        {
            AttachWindowSequence(window);
        }
        static DependencyObject Parent(DependencyObject child)
        {
            if (child is Visual || child is System.Windows.Media.Media3D.Visual3D) return VisualTreeHelper.GetParent(child);
            var content = child as FrameworkContentElement; return content == null ? LogicalTreeHelper.GetParent(child) : content.Parent;
        }
        static bool OwnButton(object sender, object original)
        {
            for (var node = original as DependencyObject; node != null; node = Parent(node))
                if (node is ButtonBase) return Object.ReferenceEquals(sender, node);
            return Object.ReferenceEquals(sender, original);
        }
        static bool HasHoverParent(FrameworkElement element)
        {
            for (var node = Parent(element); node != null; node = Parent(node))
            {
                var parent = node as FrameworkElement; MotionState state;
                if (parent != null && states.TryGetValue(parent, out state) && state.HoverCard) return true;
            }
            return false;
        }
        static void ButtonHover(object sender, MouseEventArgs e)
        {
            var button = (ButtonBase)sender;
            if (button.IsEnabled && !HasHoverParent(button)) Lift(button, button.ActualHeight > 75 ? -2 : -1);
        }
        static void ButtonLeave(object sender, MouseEventArgs e) { Lift((ButtonBase)sender, 0); Scale((ButtonBase)sender, 1, 140); }
        static void ButtonDown(object sender, MouseButtonEventArgs e) { if (OwnButton(sender, e.OriginalSource)) Scale((ButtonBase)sender, .97, 80); }
        static void ButtonUp(object sender, MouseButtonEventArgs e) { if (OwnButton(sender, e.OriginalSource)) Scale((ButtonBase)sender, 1, 170); }
        static void ButtonRelease(object sender, MouseEventArgs e)
        {
            var button = (ButtonBase)sender; if (!button.IsPressed) Scale(button, 1, 170);
        }
        static void ButtonKeyDown(object sender, KeyEventArgs e)
        {
            if ((e.Key == Key.Space || e.Key == Key.Enter) && OwnButton(sender, e.OriginalSource)) Scale((ButtonBase)sender, .97, 80);
        }
        static void ButtonKeyUp(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Space || e.Key == Key.Enter) Scale((ButtonBase)sender, 1, 170);
        }
        static void ButtonFocusLost(object sender, KeyboardFocusChangedEventArgs e) { Scale((ButtonBase)sender, 1, 170); }
        static void ButtonClick(object sender, RoutedEventArgs e)
        {
            if (!OwnButton(sender, e.OriginalSource)) return;
            var button = (ButtonBase)sender; if (!button.IsLoaded || !button.IsEnabled || !Enabled) return;
            var state = State(button);
            double from = Math.Min(.975, state.Scale.ScaleX);
            Animate(state.Scale, ScaleTransform.ScaleXProperty, from, 1, 170, true);
            Animate(state.Scale, ScaleTransform.ScaleYProperty, from, 1, 170, true);
        }
        static void ToggleChanged(object sender, RoutedEventArgs e) { if (OwnButton(sender, e.OriginalSource)) Feedback((ToggleButton)sender); }
        static void ExpandChanged(object sender, RoutedEventArgs e)
        {
            var expander = (Expander)sender; if (!expander.IsLoaded || !Object.ReferenceEquals(e.OriginalSource, expander)) return;
            // Lazy child creation runs in the control's own Expanded handler first.
            expander.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(delegate {
                if (!expander.IsLoaded) return;
                if (expander.IsExpanded) Enter(expander.Content as FrameworkElement); else Feedback(expander);
            }));
        }
        static void TooltipOpened(object sender, RoutedEventArgs e) { Enter((ToolTip)sender); }
        static void SelectionChanged(object sender, SelectionChangedEventArgs e) { if (Object.ReferenceEquals(e.OriginalSource, sender)) Feedback((ComboBox)sender); }
    }
}
