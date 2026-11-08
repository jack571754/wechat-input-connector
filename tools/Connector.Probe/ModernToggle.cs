using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using HAlign = System.Windows.HorizontalAlignment;
using UserControl = System.Windows.Controls.UserControl;
using Color = System.Windows.Media.Color;
using Cursors = System.Windows.Input.Cursors;

namespace Connector.Probe;

internal sealed class ModernToggle : UserControl
{
    private readonly Border _track;
    private readonly Border _thumb;
    private bool _isChecked;

    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (_isChecked != value)
            {
                _isChecked = value;
                UpdateVisual();
                IsCheckedChanged?.Invoke(this, _isChecked);
            }
        }
    }

    public event EventHandler<bool>? IsCheckedChanged;

    private static readonly SolidColorBrush GreenTrack = new(Color.FromRgb(7, 193, 96));
    private static readonly SolidColorBrush GrayTrack = new(Color.FromRgb(203, 213, 225));
    private static readonly SolidColorBrush ThumbBrush = new(Colors.White);

    public ModernToggle(bool initial = false)
    {
        _isChecked = initial;
        Width = 44;
        Height = 24;
        Cursor = Cursors.Hand;

        _track = new Border
        {
            CornerRadius = new CornerRadius(12),
            Background = _isChecked ? GreenTrack : GrayTrack,
            Width = 44,
            Height = 24
        };

        _thumb = new Border
        {
            Width = 18,
            Height = 18,
            CornerRadius = new CornerRadius(9),
            Background = ThumbBrush,
            HorizontalAlignment = _isChecked ? HAlign.Right : HAlign.Left,
            Margin = new Thickness(3),
            Effect = new DropShadowEffect { BlurRadius = 4, Opacity = 0.25, ShadowDepth = 1, Color = Colors.Black }
        };

        _track.Child = _thumb;
        Content = _track;

        MouseLeftButtonUp += (_, _) => IsChecked = !IsChecked;
    }

    private void UpdateVisual()
    {
        _track.Background = _isChecked ? GreenTrack : GrayTrack;
        _thumb.HorizontalAlignment = _isChecked ? HAlign.Right : HAlign.Left;
    }
}
