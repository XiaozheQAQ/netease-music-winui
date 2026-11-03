using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Text;
using Windows.Foundation;
using winuitest.Controls;
using winuitest.Models;

namespace winuitest.Views.Controls;

/// <summary>
/// Native glyph masks reveal a feathered white sweep inside the glyph, rather than
/// fading a whole Run. Widths apportion the visual sweep within the original YRC
/// segment; its upstream start/duration are the only timing information used.
/// </summary>
internal sealed class KaraokeText : Panel
{
    private readonly List<Segment> _segments = new();
    private double _fontSize;
    private double _lastTime;
    private bool _active;
    private bool _motion;

    public KaraokeText(IReadOnlyList<LyricWord> words, double fontSize)
    {
        _fontSize = fontSize;
        IsHitTestVisible = false;
        foreach (var word in words)
        {
            var segment = new Segment(word);
            foreach (var fragment in Fragments(word.Text))
            {
                var glyph = new GlyphFragment(fragment, fontSize);
                segment.Fragments.Add(glyph);
                Children.Add(glyph);
            }
            _segments.Add(segment);
        }
    }

    public double FontSize
    {
        get => _fontSize;
        set
        {
            if (Math.Abs(value - _fontSize) < 0.01) return;
            _fontSize = value;
            foreach (var segment in _segments)
                foreach (var fragment in segment.Fragments) fragment.FontSize = value;
            InvalidateMeasure();
        }
    }

    public void StopMotion()
    {
        foreach (var fragment in _segments.SelectMany(segment => segment.Fragments)) fragment.StopMotion();
    }

    public void Update(double timestamp, bool active, bool motion)
    {
        var wasActive = _active;
        _lastTime = timestamp;
        _active = active;
        _motion = motion;
        if (!active)
        {
            if (wasActive)
                foreach (var fragment in _segments.SelectMany(segment => segment.Fragments)) fragment.Reset(motion);
            return;
        }
        foreach (var segment in _segments)
        {
            var progress = active ? Math.Clamp((timestamp - segment.Word.StartMs) / Math.Max(1d, segment.Word.DurationMs), 0, 1) : 0;
            var totalWidth = segment.Fragments.Sum(fragment => fragment.DesiredSize.Width);
            var revealed = progress * totalWidth;
            var offset = 0d;
            foreach (var fragment in segment.Fragments)
            {
                var width = fragment.DesiredSize.Width;
                var localProgress = width > 0 ? Math.Clamp((revealed - offset) / width, 0, 1) : progress;
                fragment.Update(localProgress, active && motion);
                offset += width;
            }
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsFinite(availableSize.Width) ? Math.Max(1, availableSize.Width) : 100000;
        ExpandOversizedWords(width);
        var x = 0d;
        var y = 0d;
        var rowHeight = 0d;
        var desiredWidth = 0d;
        foreach (var fragment in Children.Cast<GlyphFragment>())
        {
            fragment.Measure(new Size(width, double.PositiveInfinity));
            var size = fragment.DesiredSize;
            if (x > 0 && x + size.Width > width + 0.5)
            {
                desiredWidth = Math.Max(desiredWidth, x);
                x = 0;
                y += rowHeight;
                rowHeight = 0;
            }
            x += size.Width;
            rowHeight = Math.Max(rowHeight, size.Height);
        }
        return new Size(Math.Min(width, Math.Max(desiredWidth, x)), y + rowHeight);
    }

    private void ExpandOversizedWords(double width)
    {
        foreach (var segment in _segments)
        {
            for (var index = 0; index < segment.Fragments.Count; index++)
            {
                var fragment = segment.Fragments[index];
                // Keep shaping for complex scripts; exceptionally long Latin words
                // can wrap at grapheme boundaries without sweeping two rows at once.
                if (fragment.SourceText.Length < 2 || fragment.SourceText.Any(character => character >= 0x0400)) continue;
                fragment.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                if (fragment.DesiredSize.Width <= width) continue;
                var elements = new List<string>();
                var iterator = StringInfo.GetTextElementEnumerator(fragment.SourceText);
                while (iterator.MoveNext()) elements.Add(iterator.GetTextElement());
                if (elements.Count < 2) continue;
                var childIndex = Children.IndexOf(fragment);
                Children.RemoveAt(childIndex);
                segment.Fragments.RemoveAt(index);
                foreach (var element in elements)
                {
                    var replacement = new GlyphFragment(element, _fontSize);
                    Children.Insert(childIndex++, replacement);
                    segment.Fragments.Insert(index++, replacement);
                }
                index--;
            }
        }
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var x = 0d;
        var y = 0d;
        var rowHeight = 0d;
        foreach (var fragment in Children.Cast<GlyphFragment>())
        {
            var size = fragment.DesiredSize;
            if (x > 0 && x + size.Width > finalSize.Width + 0.5)
            {
                x = 0;
                y += rowHeight;
                rowHeight = 0;
            }
            fragment.Arrange(new Rect(x, y, size.Width, size.Height));
            x += size.Width;
            rowHeight = Math.Max(rowHeight, size.Height);
        }
        Update(_lastTime, _active, _motion);
        return finalSize;
    }

    private static IEnumerable<string> Fragments(string text)
    {
        // Keep combining marks, surrogate pairs, emoji sequences and normal Latin
        // words intact. CJK text naturally wraps at complete Unicode text elements.
        var iterator = StringInfo.GetTextElementEnumerator(text);
        var word = new StringBuilder();
        while (iterator.MoveNext())
        {
            var element = iterator.GetTextElement();
            var codepoint = char.ConvertToUtf32(element, 0);
            var separate = codepoint >= 0x2E80 || string.IsNullOrWhiteSpace(element);
            if (separate)
            {
                if (word.Length > 0) { yield return word.ToString(); word.Clear(); }
                yield return element;
            }
            else word.Append(element);
        }
        if (word.Length > 0) yield return word.ToString();
    }

    private sealed class Segment
    {
        public Segment(LyricWord word) => Word = word;
        public LyricWord Word { get; }
        public List<GlyphFragment> Fragments { get; } = new();
    }

    private sealed class GlyphFragment : Grid
    {
        private readonly TextBlock _text;
        private readonly TranslateTransform _lift = new();
        private SpriteVisual? _reveal;
        private CompositionMaskBrush? _mask;
        private CompositionLinearGradientBrush? _gradient;
        private CompositionColorGradientStop? _solidEdge;
        private CompositionColorGradientStop? _softEdge;
        private CompositionColorGradientStop? _endEdge;
        private double _progress;
        private bool _motion;
        private bool _fading;

        public GlyphFragment(string text, double fontSize)
        {
            SourceText = text;
            _text = new TextBlock
            {
                Text = text.Replace(' ', '\u00a0'),
                FontSize = fontSize,
                FontFamily = LyricTypography.PrimaryFamily,
                FontWeight = Microsoft.UI.Text.FontWeights.Black,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 139, 139, 143)),
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = false,
                IsHitTestVisible = false
            };
            Children.Add(_text);
            RenderTransform = _lift;
            IsHitTestVisible = false;
            SizeChanged += (_, _) => RefreshMask();
            Loaded += (_, _) => Update(_progress, _motion);
            Unloaded += (_, _) => ReleaseMask();
        }

        public string SourceText { get; }
        public double FontSize { set { _text.FontSize = value; InvalidateMeasure(); } }

        public void Update(double progress, bool motion)
        {
            _progress = Math.Clamp(progress, 0, 1);
            _motion = motion;
            if (_fading && _reveal is not null) { _reveal.StopAnimation("Opacity"); _fading = false; }
            // A small deterministic lift follows the sung portion. Pause freezes it;
            // seek replaces its phase directly, so there is no queued bounce to finish.
            _lift.Y = motion ? -2.25 * Math.Sin(Math.PI * _progress) : 0;
            if (_progress <= 0)
            {
                if (_reveal is not null) _reveal.Opacity = 0;
                return;
            }
            if (!IsLoaded || ActualWidth <= 0 || ActualHeight <= 0) return;
            if (_reveal is null) CreateMask();
            if (_reveal is null) return;
            _reveal.Opacity = 1;
            if (_progress >= 1)
            {
                _solidEdge!.Offset = 1;
                _softEdge!.Offset = 1;
                _softEdge.Color = Colors.White;
                _endEdge!.Color = Colors.White;
            }
            else
            {
                // The soft edge is about one sixth of a Chinese glyph, independent
                // of whether the upstream word spans one character or a whole word.
                var feather = Math.Clamp(_text.FontSize * 0.17 / ActualWidth, 0.025, 0.22);
                _solidEdge!.Offset = (float)Math.Clamp(_progress - feather, 0, 1);
                _softEdge!.Offset = (float)Math.Clamp(_progress + feather, 0, 1);
                _softEdge.Color = Colors.Transparent;
                _endEdge!.Color = Colors.Transparent;
            }
        }

        public void StopMotion()
        {
            _lift.Y = 0;
            if (_fading && _reveal is not null)
            {
                _reveal.StopAnimation("Opacity");
                _reveal.Opacity = 0;
                _fading = false;
            }
        }

        public void Reset(bool animate)
        {
            _progress = 0;
            _lift.Y = 0;
            if (_reveal is null) return;
            if (animate)
            {
                var fade = _reveal.Compositor.CreateScalarKeyFrameAnimation();
                fade.Duration = TimeSpan.FromMilliseconds(320);
                fade.InsertExpressionKeyFrame(0, "this.StartingValue");
                fade.InsertKeyFrame(1, 0);
                _reveal.StartAnimation("Opacity", fade);
                _fading = true;
            }
            else
            {
                _reveal.StopAnimation("Opacity");
                _reveal.Opacity = 0;
                _fading = false;
            }
        }

        private void CreateMask()
        {
            var compositor = ElementCompositionPreview.GetElementVisual(this).Compositor;
            _gradient = compositor.CreateLinearGradientBrush();
            _gradient.MappingMode = CompositionMappingMode.Relative;
            _gradient.StartPoint = Vector2.Zero;
            _gradient.EndPoint = new Vector2(1, 0);
            _gradient.ColorStops.Add(compositor.CreateColorGradientStop(0, Colors.White));
            _solidEdge = compositor.CreateColorGradientStop(0, Colors.White);
            _softEdge = compositor.CreateColorGradientStop(0, Colors.Transparent);
            _endEdge = compositor.CreateColorGradientStop(1, Colors.Transparent);
            _gradient.ColorStops.Add(_solidEdge);
            _gradient.ColorStops.Add(_softEdge);
            _gradient.ColorStops.Add(_endEdge);
            _mask = compositor.CreateMaskBrush();
            _mask.Source = _gradient;
            _mask.Mask = _text.GetAlphaMask();
            _reveal = compositor.CreateSpriteVisual();
            _reveal.Brush = _mask;
            _reveal.Size = new Vector2((float)ActualWidth, (float)ActualHeight);
            ElementCompositionPreview.SetElementChildVisual(this, _reveal);
        }

        private void RefreshMask()
        {
            if (_reveal is not null)
            {
                _reveal.Size = new Vector2((float)ActualWidth, (float)ActualHeight);
                _mask!.Mask = _text.GetAlphaMask();
            }
            Update(_progress, _motion);
        }

        private void ReleaseMask()
        {
            ElementCompositionPreview.SetElementChildVisual(this, null);
            _reveal?.Dispose();
            _mask?.Dispose();
            _gradient?.Dispose();
            _solidEdge?.Dispose();
            _softEdge?.Dispose();
            _endEdge?.Dispose();
            _reveal = null;
            _mask = null;
            _gradient = null;
            _solidEdge = _softEdge = _endEdge = null;
        }
    }
}
