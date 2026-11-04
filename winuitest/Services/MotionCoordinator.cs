using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.UI.ViewManagement;

namespace winuitest.Services;

/// <summary>
/// Owns short UI visibility transitions. Call on the window dispatcher and do not
/// animate the same element's opacity/translation from another owner.
/// </summary>
public sealed class MotionCoordinator : IDisposable
{
    private readonly UISettings _settings;
    private readonly DispatcherQueue _dispatcher;
    private readonly Dictionary<UIElement, MotionState> _states = new();
    private readonly Dictionary<ButtonBase, PressState> _pressButtons = new();
    private readonly Dictionary<uint, PressState> _pressedPointers = new();
    private UIElement? _pressRoot;
    private PointerEventHandler? _pressHandler;
    private PointerEventHandler? _releaseHandler;
    private bool _disposed;
    private bool _subscribed;

    public MotionCoordinator(UISettings settings)
    {
        _settings = settings;
        _dispatcher = DispatcherQueue.GetForCurrentThread()
            ?? throw new InvalidOperationException("MotionCoordinator must be created on the window dispatcher.");
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
        {
            _settings.AnimationsEnabledChanged += MotionPreferenceChanged;
            _subscribed = true;
        }
    }

    public bool AnimationsEnabled => !_disposed && _settings.AnimationsEnabled;

    /// <summary>
    /// Adds quiet pointer feedback to app buttons without replacing their native
    /// hover/focus/pressed states or handling the input event itself.
    /// </summary>
    public void AttachPressFeedback(UIElement root)
    {
        VerifyDispatcher();
        if (_disposed || ReferenceEquals(_pressRoot, root)) return;
        DetachPressFeedback();
        _pressRoot = root;
        _pressHandler = PressRoot_PointerPressed;
        _releaseHandler = PressRoot_PointerReleased;
        root.AddHandler(UIElement.PointerPressedEvent, _pressHandler, handledEventsToo: true);
        root.AddHandler(UIElement.PointerReleasedEvent, _releaseHandler, handledEventsToo: true);
        root.AddHandler(UIElement.PointerCanceledEvent, _releaseHandler, handledEventsToo: true);
        root.AddHandler(UIElement.PointerCaptureLostEvent, _releaseHandler, handledEventsToo: true);
        if (root is FrameworkElement framework) framework.Unloaded += PressRoot_Unloaded;
    }

    private void PressRoot_PointerPressed(object sender, PointerRoutedEventArgs args)
    {
        if (!AnimationsEnabled || _pressRoot is null) return;
        var point = args.GetCurrentPoint(_pressRoot);
        if (!point.Properties.IsLeftButtonPressed) return;
        var button = FindPressButton(args.OriginalSource as DependencyObject);
        if (button is null || !button.IsEnabled) return;
        var pointerId = args.Pointer.PointerId;
        ReleasePointer(pointerId);
        if (!_pressButtons.TryGetValue(button, out var state))
        {
            var visual = ElementCompositionPreview.GetElementVisual(button);
            state = new PressState(visual);
            _pressButtons.Add(button, state);
            button.Unloaded += PressButton_Unloaded;
        }
        _pressedPointers[pointerId] = state;
        state.Pointers.Add(pointerId);
        state.Visual.CenterPoint = new Vector3((float)button.ActualWidth / 2, (float)button.ActualHeight / 2, state.OriginalCenterPoint.Z);
        AnimatePress(state, pressed: true);
    }

    private ButtonBase? FindPressButton(DependencyObject? source)
    {
        ButtonBase? candidate = null;
        for (var current = source; current is not null && !ReferenceEquals(current, _pressRoot);)
        {
            // Do not animate framework-internal repeat/clear/dropdown buttons or
            // change the feel of scrolling, text editing and navigation controls.
            if (current is Slider or ScrollBar or NavigationViewItem or TextBox or PasswordBox or ComboBox)
                return null;
            candidate ??= current as ButtonBase;
            current = current is UIElement ? VisualTreeHelper.GetParent(current) : null;
        }
        return candidate;
    }

    private void PressRoot_PointerReleased(object sender, PointerRoutedEventArgs args) => ReleasePointer(args.Pointer.PointerId);

    private void ReleasePointer(uint pointerId)
    {
        if (!_pressedPointers.Remove(pointerId, out var state)) return;
        state.Pointers.Remove(pointerId);
        if (state.Pointers.Count == 0) AnimatePress(state, pressed: false);
    }

    private void AnimatePress(PressState state, bool pressed)
    {
        // LeaveCurrentValue makes a quick release or re-press reverse smoothly.
        state.Visual.StopAnimation("Scale");
        if (!AnimationsEnabled)
        {
            state.Visual.Scale = state.OriginalScale;
            return;
        }
        using var animation = state.Visual.Compositor.CreateVector3KeyFrameAnimation();
        animation.Duration = TimeSpan.FromMilliseconds(pressed ? 120 : 240);
        animation.StopBehavior = AnimationStopBehavior.LeaveCurrentValue;
        animation.InsertExpressionKeyFrame(0, "this.StartingValue");
        var scale = pressed ? new Vector3(state.OriginalScale.X * 0.975f, state.OriginalScale.Y * 0.975f, state.OriginalScale.Z) : state.OriginalScale;
        var easing = state.Visual.Compositor.CreateCubicBezierEasingFunction(new Vector2(0.2f, 0), new Vector2(0.2f, 1));
        animation.InsertKeyFrame(1, scale, easing);
        state.Visual.StartAnimation("Scale", animation);
    }

    private void PressButton_Unloaded(object sender, RoutedEventArgs args)
    {
        if (sender is not ButtonBase button || !_pressButtons.Remove(button, out var state)) return;
        button.Unloaded -= PressButton_Unloaded;
        foreach (var id in state.Pointers) _pressedPointers.Remove(id);
        ResetPress(state);
    }

    private void PressRoot_Unloaded(object sender, RoutedEventArgs args) => ResetPressFeedback();

    private static void ResetPress(PressState state)
    {
        state.Pointers.Clear();
        state.Visual.StopAnimation("Scale");
        state.Visual.Scale = state.OriginalScale;
        state.Visual.CenterPoint = state.OriginalCenterPoint;
    }

    private void ResetPressFeedback()
    {
        _pressedPointers.Clear();
        foreach (var state in _pressButtons.Values) ResetPress(state);
    }

    private void DetachPressFeedback()
    {
        if (_pressRoot is not null)
        {
            _pressRoot.RemoveHandler(UIElement.PointerPressedEvent, _pressHandler!);
            _pressRoot.RemoveHandler(UIElement.PointerReleasedEvent, _releaseHandler!);
            _pressRoot.RemoveHandler(UIElement.PointerCanceledEvent, _releaseHandler!);
            _pressRoot.RemoveHandler(UIElement.PointerCaptureLostEvent, _releaseHandler!);
            if (_pressRoot is FrameworkElement framework) framework.Unloaded -= PressRoot_Unloaded;
        }
        ResetPressFeedback();
        foreach (var button in _pressButtons.Keys) button.Unloaded -= PressButton_Unloaded;
        _pressButtons.Clear();
        _pressRoot = null;
        _pressHandler = null;
        _releaseHandler = null;
    }

    /// <summary>Returns false when a newer transition supersedes this request.</summary>
    public Task<bool> TransitionAsync(UIElement element, bool visible, double offset = 12, int durationMs = 340) =>
        TransitionAsync(element, visible, new Vector3(0, (float)offset, 0), durationMs);

    public Task<bool> TransitionAsync(UIElement element, bool visible, Vector3 offset, int durationMs = 340) =>
        TransitionCore(element, visible, offset, durationMs, reveal: false);

    /// <summary>Reveals newly changed content even when its page container is already visible.</summary>
    public Task<bool> RevealAsync(UIElement element, double offset = 12, int durationMs = 340) =>
        TransitionCore(element, true, new Vector3(0, (float)offset, 0), durationMs, reveal: true);

    /// <summary>Moves an opaque sheet from below the window, retaining its position on reversal.</summary>
    public Task<bool> SlideAsync(UIElement element, bool visible, double distance, int durationMs = 560) =>
        TransitionCore(element, visible, new Vector3(0, (float)distance, 0), durationMs, reveal: false, fade: false);

    public void Show(UIElement element, double offset = 12, int durationMs = 340) =>
        _ = TransitionAsync(element, true, offset, durationMs);

    public Task<bool> HideAsync(UIElement element, double offset = 8, int durationMs = 300) =>
        TransitionAsync(element, false, offset, durationMs);

    private Task<bool> TransitionCore(UIElement element, bool visible, Vector3 offset, int durationMs, bool reveal, bool fade = true)
    {
        VerifyDispatcher();
        if (_disposed) return Task.FromResult(false);
        ArgumentNullException.ThrowIfNull(element);
        var state = GetState(element);
        if (state.Completion is not null && state.TargetVisible == visible && !reveal)
            return state.Completion.Task;

        var wasMoving = state.Completion is not null;
        var wasHidden = element.Visibility != Visibility.Visible;
        if (!wasMoving && state.IsSettled && !wasHidden && state.TargetVisible && visible && !reveal)
            return Task.FromResult(true);

        Interrupt(state);
        var generation = ++state.Generation;
        state.TargetVisible = visible;
        if (!AnimationsEnabled || (!visible && wasHidden))
        {
            Settle(element, state, visible);
            return Task.FromResult(true);
        }

        if (visible)
        {
            // Retargeting an in-flight fade must retain its compositor value. A
            // genuinely hidden/new page gets the one intentional entrance offset.
            if (wasHidden || (reveal && !wasMoving))
            {
                state.Visual.Opacity = fade ? 0 : 1;
                if (state.HasTranslation) state.Visual.Properties.InsertVector3("Translation", offset);
            }
            element.Visibility = Visibility.Visible;
            element.IsHitTestVisible = state.OriginalHitTesting;
        }
        else element.IsHitTestVisible = false;

        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        state.Completion = completion;
        var compositor = state.Visual.Compositor;
        var easing = compositor.CreateCubicBezierEasingFunction(new Vector2(0.2f, 0), new Vector2(0.15f, 1));
        var duration = TimeSpan.FromMilliseconds(Math.Clamp(durationMs, 120, 900));
        using var opacity = compositor.CreateScalarKeyFrameAnimation();
        opacity.Duration = duration;
        opacity.StopBehavior = AnimationStopBehavior.LeaveCurrentValue;
        opacity.InsertExpressionKeyFrame(0, "this.StartingValue");
        opacity.InsertKeyFrame(1, visible ? 1 : 0, easing);

        var batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        state.Batch = batch;
        void CompleteTransition()
        {
            if (_disposed || state.Generation != generation || !ReferenceEquals(state.Batch, batch)
                || !ReferenceEquals(state.Completion, completion)) return;
            ReleaseBatch(state);
            StopVisual(state);
            Settle(element, state, visible);
            state.Completion = null;
            completion.TrySetResult(true);
        }
        state.CompletedHandler = (_, _) => CompleteTransition();
        batch.Completed += state.CompletedHandler;
        try
        {
            if (fade) state.Visual.StartAnimation("Opacity", opacity);
            else state.Visual.Opacity = 1;
            if (state.HasTranslation)
            {
                using var movement = compositor.CreateVector3KeyFrameAnimation();
                movement.Duration = duration;
                movement.StopBehavior = AnimationStopBehavior.LeaveCurrentValue;
                movement.InsertExpressionKeyFrame(0, "this.StartingValue");
                movement.InsertKeyFrame(1, visible ? Vector3.Zero : offset, easing);
                state.Visual.StartAnimation("Translation", movement);
            }

            // An occluded/minimized window may stop presenting compositor
            // frames and never deliver Batch.Completed. Settle visibility on
            // the UI dispatcher after a grace period, including hit testing,
            // so a closing sheet cannot remain over the underlying page.
            var fallback = _dispatcher.CreateTimer();
            fallback.IsRepeating = false;
            fallback.Interval = duration + TimeSpan.FromMilliseconds(250);
            state.FallbackTimer = fallback;
            state.FallbackHandler = (_, _) => CompleteTransition();
            fallback.Tick += state.FallbackHandler;
            fallback.Start();
        }
        catch
        {
            // A detached visual should not leave the UI invisible or its caller
            // awaiting a compositor completion that can no longer arrive.
            CompleteTransition();
        }
        finally
        {
            if (ReferenceEquals(state.Batch, batch)) batch.End();
        }
        return completion.Task;
    }

    /// <summary>
    /// Stops current work. With no visibility argument the current compositor
    /// value is retained, ready for the next transition to continue from it.
    /// </summary>
    public void Cancel(UIElement element, bool? visible = null)
    {
        VerifyDispatcher();
        if (!_states.TryGetValue(element, out var state))
        {
            if (visible.HasValue) element.Visibility = visible.Value ? Visibility.Visible : Visibility.Collapsed;
            return;
        }
        Interrupt(state);
        if (visible.HasValue)
        {
            state.TargetVisible = visible.Value;
            Settle(element, state, visible.Value);
        }
    }

    private MotionState GetState(UIElement element)
    {
        if (_states.TryGetValue(element, out var existing)) return existing;
        var hasTranslation = element.Translation == Vector3.Zero;
        if (hasTranslation) ElementCompositionPreview.SetIsTranslationEnabled(element, true);
        var visual = ElementCompositionPreview.GetElementVisual(element);
        // Initialise exactly once, before starting/stopping Translation. Preserve
        // XAML-supplied translation (for example a shadow's Z) by using fade only.
        if (hasTranslation) visual.Properties.InsertVector3("Translation", Vector3.Zero);
        var state = new MotionState(visual, hasTranslation, element.IsHitTestVisible)
        {
            TargetVisible = element.Visibility == Visibility.Visible
        };
        _states.Add(element, state);
        return state;
    }

    private static void Interrupt(MotionState state, bool completed = false)
    {
        state.Generation++;
        ReleaseBatch(state);
        StopVisual(state);
        state.Completion?.TrySetResult(completed);
        state.Completion = null;
        state.IsSettled = false;
    }

    private static void StopVisual(MotionState state)
    {
        state.Visual.StopAnimation("Opacity");
        if (state.HasTranslation) state.Visual.StopAnimation("Translation");
    }

    private static void Settle(UIElement element, MotionState state, bool visible)
    {
        element.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        element.IsHitTestVisible = state.OriginalHitTesting;
        state.Visual.Opacity = 1;
        if (state.HasTranslation) state.Visual.Properties.InsertVector3("Translation", Vector3.Zero);
        state.IsSettled = true;
    }

    private static void ReleaseBatch(MotionState state)
    {
        if (state.FallbackTimer is { } fallback)
        {
            fallback.Stop();
            if (state.FallbackHandler is not null) fallback.Tick -= state.FallbackHandler;
            state.FallbackTimer = null;
        }
        state.FallbackHandler = null;
        var batch = state.Batch;
        if (batch is null) return;
        if (state.CompletedHandler is not null) batch.Completed -= state.CompletedHandler;
        state.CompletedHandler = null;
        state.Batch = null;
        if (!batch.IsEnded) batch.End();
        batch.Dispose();
    }

    private void MotionPreferenceChanged(UISettings sender, UISettingsAnimationsEnabledChangedEventArgs args) =>
        _dispatcher.TryEnqueue(() =>
        {
            if (_disposed || _settings.AnimationsEnabled) return;
            ResetPressFeedback();
            foreach (var pair in _states)
            {
                Interrupt(pair.Value, completed: true);
                Settle(pair.Key, pair.Value, pair.Value.TargetVisible);
            }
        });

    public void Dispose()
    {
        VerifyDispatcher();
        if (_disposed) return;
        _disposed = true;
        if (_subscribed && OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
            _settings.AnimationsEnabledChanged -= MotionPreferenceChanged;
        _subscribed = false;
        DetachPressFeedback();
        foreach (var pair in _states)
        {
            Interrupt(pair.Value);
            Settle(pair.Key, pair.Value, pair.Value.TargetVisible);
        }
        _states.Clear();
    }

    private void VerifyDispatcher()
    {
        if (!_dispatcher.HasThreadAccess) throw new InvalidOperationException("UI motion must run on the window dispatcher.");
    }

    private sealed class MotionState
    {
        public MotionState(Visual visual, bool hasTranslation, bool originalHitTesting)
        {
            Visual = visual;
            HasTranslation = hasTranslation;
            OriginalHitTesting = originalHitTesting;
        }
        public Visual Visual { get; }
        public bool HasTranslation { get; }
        public bool OriginalHitTesting { get; }
        public int Generation { get; set; }
        public bool TargetVisible { get; set; }
        public bool IsSettled { get; set; } = true;
        public TaskCompletionSource<bool>? Completion { get; set; }
        public CompositionScopedBatch? Batch { get; set; }
        public TypedEventHandler<object, CompositionBatchCompletedEventArgs>? CompletedHandler { get; set; }
        public DispatcherQueueTimer? FallbackTimer { get; set; }
        public TypedEventHandler<DispatcherQueueTimer, object>? FallbackHandler { get; set; }
    }

    private sealed class PressState
    {
        public PressState(Visual visual)
        {
            Visual = visual;
            OriginalScale = visual.Scale;
            OriginalCenterPoint = visual.CenterPoint;
        }
        public Visual Visual { get; }
        public Vector3 OriginalScale { get; }
        public Vector3 OriginalCenterPoint { get; }
        public HashSet<uint> Pointers { get; } = new();
    }
}
