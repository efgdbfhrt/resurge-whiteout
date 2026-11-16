using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace ResurgeLauncherCSharp;

public sealed class SidebarTab : TabItem
{
    private static bool Motion => (System.Windows.Media.RenderCapability.Tier >> 16) > 0;

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        UpdatePointer(IsMouseOver, animate: false);
        UpdateSelection(IsSelected, animate: false);
    }

    protected override void OnMouseEnter(MouseEventArgs e)
    {
        base.OnMouseEnter(e);
        UpdatePointer(true, animate: true);
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        UpdatePointer(false, animate: true);
    }

    protected override void OnSelected(RoutedEventArgs e)
    {
        base.OnSelected(e);
        UpdateSelection(true, animate: true);
    }

    protected override void OnUnselected(RoutedEventArgs e)
    {
        base.OnUnselected(e);
        UpdateSelection(false, animate: true);
    }

    private void UpdatePointer(bool over, bool animate) => VisualStateManager.GoToState(
        this, over ? "PointerOver" : "PointerRest", animate && Motion);

    private void UpdateSelection(bool selected, bool animate) => VisualStateManager.GoToState(
        this, selected ? "ActiveTab" : "InactiveTab", animate && Motion);
}
