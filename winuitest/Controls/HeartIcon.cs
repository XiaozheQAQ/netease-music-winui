using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using ShapePath = Microsoft.UI.Xaml.Shapes.Path;

namespace winuitest.Controls;

/// <summary>The existing Fluent filled heart centered by its visible outline, without a font baseline.</summary>
public sealed class HeartIcon : UserControl
{
    private readonly ShapePath _shape;

    public HeartIcon()
    {
        // Same U+F47D outline as the bundled FluentSystemIcons-Filled.ttf (MIT;
        // see Assets/Fonts/PROVENANCE.txt). Its 500-em glyph has a loose glyf bbox.
        // Exact quadratic extrema: x=49.75..450.25, font y=75..425. Centering the
        // actual 400.5×350 outline avoids both that padding and OS/2 line metrics.
        // Coordinates below flip the font's Y axis; Path measures the real curves.
        var figure = new PathFigure { StartPoint = new Point(235, 33), IsClosed = true, IsFilled = true };
        AddCurve(figure, 220, 18, 200, 9.5);
        AddCurve(figure, 180, 1, 158.5, 1);
        AddCurve(figure, 137, 1, 117, 9.5);
        AddCurve(figure, 97, 18, 82, 33);
        AddCurve(figure, 61, 54, 53.5, 82);
        AddCurve(figure, 46, 110, 53.5, 138.5);
        AddCurve(figure, 61, 167, 82, 188);
        figure.Segments.Add(new LineSegment { Point = new Point(239, 346) });
        AddCurve(figure, 244, 351, 251, 351);
        AddCurve(figure, 258, 351, 262, 346);
        figure.Segments.Add(new LineSegment { Point = new Point(419, 188) });
        AddCurve(figure, 439, 167, 446.5, 139);
        AddCurve(figure, 454, 111, 446.5, 83);
        AddCurve(figure, 439, 55, 418, 34);
        AddCurve(figure, 403, 18, 383, 10);
        AddCurve(figure, 363, 2, 341.5, 1.5);
        AddCurve(figure, 320, 1, 300, 9.5);
        AddCurve(figure, 280, 18, 265, 33);
        figure.Segments.Add(new LineSegment { Point = new Point(250, 48) });
        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        _shape = new ShapePath { Data = geometry, Stretch = Stretch.Fill, UseLayoutRounding = false };
        _shape.SetBinding(ShapePath.FillProperty, new Binding { Source = this, Path = new PropertyPath(nameof(Foreground)) });
        Content = _shape;
        IsTabStop = false;
        UseLayoutRounding = false;
        HorizontalContentAlignment = HorizontalAlignment.Center;
        VerticalContentAlignment = VerticalAlignment.Center;
        RegisterPropertyChangedCallback(FontSizeProperty, (_, _) => UpdateSize());
        FontSize = 20;
        UpdateSize();
    }

    private void UpdateSize()
    {
        _shape.Width = FontSize * (400.5 / 500);
        _shape.Height = FontSize * (350.0 / 500);
    }

    private static void AddCurve(PathFigure figure, double x, double y, double endX, double endY) =>
        figure.Segments.Add(new QuadraticBezierSegment { Point1 = new Point(x, y), Point2 = new Point(endX, endY) });
}
