using Avalonia;
using Avalonia.Controls;

namespace WoWBoxer.Layouts.App.Views;

/// <summary>Discord, YouTube and website icon links (status bar: small icons; About: with labels).</summary>
public partial class CommunityBar : UserControl
{
    public static readonly StyledProperty<double> IconSizeProperty = AvaloniaProperty.Register<CommunityBar, double>(nameof(IconSize), 15.0);
    public static readonly StyledProperty<bool> ShowLabelsProperty = AvaloniaProperty.Register<CommunityBar, bool>(nameof(ShowLabels));

    public CommunityBar() => InitializeComponent();

    public double IconSize { get => GetValue(IconSizeProperty); set => SetValue(IconSizeProperty, value); }
    public bool ShowLabels { get => GetValue(ShowLabelsProperty); set => SetValue(ShowLabelsProperty, value); }
}
