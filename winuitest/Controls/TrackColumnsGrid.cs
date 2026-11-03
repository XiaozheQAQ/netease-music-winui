using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;

namespace winuitest.Controls;

public enum TrackField { Cover, Title, Album, Duration, Favorite, More }

/// <summary>One column definition for track headings, songs and loading placeholders.</summary>
public sealed class TrackColumnsGrid : Grid
{
    public static readonly DependencyProperty FieldProperty = DependencyProperty.RegisterAttached(
        "Field", typeof(TrackField), typeof(TrackColumnsGrid), new PropertyMetadata(TrackField.Cover));

    public static TrackField GetField(DependencyObject element) => (TrackField)element.GetValue(FieldProperty);
    public static void SetField(DependencyObject element, TrackField value) => element.SetValue(FieldProperty, value);

    private bool? _albumVisible;

    public TrackColumnsGrid()
    {
        Padding = new Thickness(12, 0, 12, 0);
        HorizontalAlignment = HorizontalAlignment.Stretch;
        SetColumns(true);
        Loaded += (_, _) => UpdateLayoutColumns();
        SizeChanged += (_, _) => UpdateLayoutColumns();
    }

    private void UpdateLayoutColumns()
    {
        var showAlbum = ActualWidth >= 650;
        if (_albumVisible != showAlbum) SetColumns(showAlbum);
        foreach (var child in Children)
        {
            if (child is not FrameworkElement element) continue;
            var field = GetField(element);
            SetColumn(element, !showAlbum && field > TrackField.Album ? (int)field - 1 : (int)field);
            if (field == TrackField.Album) element.Visibility = showAlbum ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void SetColumns(bool showAlbum)
    {
        _albumVisible = showAlbum;
        ColumnSpacing = showAlbum ? 16 : 12;
        ColumnDefinitions.Clear();
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
        if (showAlbum) ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
    }
}
