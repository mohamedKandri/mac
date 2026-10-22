using System.Windows;
using MacShell.Native;
using MacShell.Themes;

namespace MacShell.Bar;

// The dock's Trash tile: Recycle Bin state, open on click, recycle dropped files (dock step 5).
public partial class DockWindow
{
    // ---------------- Trash (dock step 5) ----------------

    private void UpdateTrash()
    {
        _binFull = !RecycleBin.IsEmpty();
        TrashIcon.Data = (System.Windows.Media.Geometry)FindResource(_binFull ? "Icon.TrashFull" : "Icon.Trash");
        TrashIcon.FillData = _binFull ? (System.Windows.Media.Geometry)FindResource("Icon.TrashPaper") : null;
        if (_hovering) LoadMagnifier();   // keep the magnified copy in sync
    }

    private void OnTrashClick(object sender, RoutedEventArgs e) => RecycleBin.Open();

    // Accept only files/folders from Explorer (or any app that drags files); show that the drop will land.
    private void OnTrashDragOver(object sender, DragEventArgs e)
    {
        bool files = e.Data.GetDataPresent(DataFormats.FileDrop);
        e.Effects = files ? DragDropEffects.Move : DragDropEffects.None;
        SetTrashHighlight(files);
        e.Handled = true;
    }

    private void OnTrashDragLeave(object sender, DragEventArgs e) => SetTrashHighlight(false);

    private void OnTrashDrop(object sender, DragEventArgs e)
    {
        SetTrashHighlight(false);
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0) return;
        RecycleBin.Recycle(paths,
            failed: error => Dispatcher.InvokeAsync(() => _notify("Couldn't move to the Recycle Bin", error)),
            done: () => Dispatcher.InvokeAsync(UpdateTrash));
        e.Handled = true;
    }

    private void SetTrashHighlight(bool on)
    {
        TrashTile.SetResourceReference(System.Windows.Controls.Border.BorderBrushProperty, on ? ThemeKeys.Selection : ThemeKeys.Hairline);
        TrashTile.BorderThickness = new Thickness(on ? 2 : 1);
    }
}
