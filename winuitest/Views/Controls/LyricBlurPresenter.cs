using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using System;
using System.Numerics;

namespace winuitest.Views.Controls;

/// <summary>Live, GPU-filtered text, with an immediate native-text path while browsing.</summary>
internal sealed class LyricBlurPresenter : Grid
{
    private const float CapturePadding = 10;
    private readonly FrameworkElement _content;
    private readonly Grid _sourceHost;
    private Visual? _sourceHostVisual;
    private CompositionVisualSurface? _surface;
    private CompositionSurfaceBrush? _surfaceBrush;
    private CompositionEffectBrush? _effectBrush;
    private SpriteVisual? _output;
    private float _targetRadius = float.NaN;

    public LyricBlurPresenter(FrameworkElement content)
    {
        _content = content;
        _sourceHost = new Grid();
        _sourceHost.Children.Add(content);
        Children.Add(_sourceHost);
        Unloaded += (_, _) => ReleaseResources();
    }

    public bool HasResources => _output is not null;

    public void UpdateBlur(CompositionEffectFactory factory, float radius, bool animate, double visibleTop, double visibleBottom)
    {
        if (!IsLoaded || _content.ActualWidth <= 0 || _content.ActualHeight <= 0) return;
        if (radius <= 0 && _output is null) return;
        var compositor = ElementCompositionPreview.GetElementVisual(this).Compositor;
        if (_output is null)
        {
            _sourceHostVisual = ElementCompositionPreview.GetElementVisual(_sourceHost);
            _surface = compositor.CreateVisualSurface();
            // Capture starts below the hidden parent. Its opacity is excluded
            // from the captured subtree, while all live karaoke children remain.
            // The output is a sibling of the source, so no feedback loop exists.
            _surface.SourceVisual = ElementCompositionPreview.GetElementVisual(_content);
            _surfaceBrush = compositor.CreateSurfaceBrush(_surface);
            _surfaceBrush.Stretch = CompositionStretch.None;
            _surfaceBrush.HorizontalAlignmentRatio = 0;
            _surfaceBrush.VerticalAlignmentRatio = 0;
            _effectBrush = factory.CreateBrush();
            _effectBrush.SetSourceParameter("Text", _surfaceBrush);
            _effectBrush.Properties.InsertScalar("LyricBlur.BlurAmount", 0);
            _output = compositor.CreateSpriteVisual();
            _output.Brush = _effectBrush;
            ElementCompositionPreview.SetElementChildVisual(this, _output);
            _targetRadius = 0;
        }

        // A very long paragraph only captures the part around the viewport.
        // Padding preserves soft glyph edges without creating full-song textures.
        var top = Math.Clamp(visibleTop, 0, _content.ActualHeight);
        var bottom = Math.Clamp(visibleBottom, top, _content.ActualHeight);
        var offset = new Vector2(-CapturePadding, (float)top - CapturePadding);
        var size = new Vector2((float)_content.ActualWidth + CapturePadding * 2, (float)Math.Max(1, bottom - top) + CapturePadding * 2);
        if (_surface!.SourceOffset != offset) _surface.SourceOffset = offset;
        if (_surface.SourceSize != size) _surface.SourceSize = size;
        _output.Size = size;
        _output.Offset = new Vector3(offset, 0);
        _sourceHostVisual!.Opacity = 0;
        _output.IsVisible = true;

        if (animate && Math.Abs(_targetRadius - radius) < 0.001f) return;
        _targetRadius = radius;
        if (!animate)
        {
            _effectBrush!.StopAnimation("LyricBlur.BlurAmount");
            _effectBrush.Properties.InsertScalar("LyricBlur.BlurAmount", radius);
            return;
        }
        using var animation = compositor.CreateScalarKeyFrameAnimation();
        using var easing = compositor.CreateCubicBezierEasingFunction(new Vector2(0.2f, 0), new Vector2(0.2f, 1));
        animation.Duration = TimeSpan.FromMilliseconds(radius > 0 ? 380 : 260);
        animation.InsertExpressionKeyFrame(0, "this.StartingValue");
        animation.InsertKeyFrame(1, radius, easing);
        _effectBrush!.StartAnimation("LyricBlur.BlurAmount", animation);
    }

    public void ShowClearImmediately()
    {
        if (_output is not null) _output.IsVisible = false;
        if (_sourceHostVisual is not null) _sourceHostVisual.Opacity = 1;
        if (_effectBrush is not null)
        {
            _effectBrush.StopAnimation("LyricBlur.BlurAmount");
            _effectBrush.Properties.InsertScalar("LyricBlur.BlurAmount", 0);
        }
        _targetRadius = 0;
    }

    public void ReleaseResources()
    {
        ShowClearImmediately();
        if (_output is not null) ElementCompositionPreview.SetElementChildVisual(this, null);
        if (_surface is not null) _surface.SourceVisual = null;
        _output?.Dispose();
        _effectBrush?.Dispose();
        _surfaceBrush?.Dispose();
        _surface?.Dispose();
        _output = null;
        _effectBrush = null;
        _surfaceBrush = null;
        _surface = null;
        _sourceHostVisual = null;
    }
}
