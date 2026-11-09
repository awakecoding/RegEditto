using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;

namespace RegistryEditor.Presentation.Views;

public sealed class InlineName : Grid
{
    private readonly TextBlock _label;
    private readonly TextBox _editor = new() { IsVisible = false, MinHeight = 0, Padding = new Thickness(1, 0),
        BorderThickness = new Thickness(1), VerticalContentAlignment = VerticalAlignment.Center };
    private TaskCompletionSource<string?>? _pending;
    public bool Editing => _pending is not null;
    public InlineName(string name, object item)
    {
        Tag = item;
        _label = new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center, TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis };
        Children.Add(_label); Children.Add(_editor);
        _editor.KeyDown += (_, e) => {
            if (e.Key is Key.Enter or Key.Escape) {
                Complete(e.Key == Key.Enter ? _editor.Text : null);
                e.Handled = true;
            }
        };
        _editor.LostFocus += (_, _) => Complete(_editor.Text);
        DetachedFromVisualTree += (_, _) => Complete(null);
    }
    public Task<string?> Edit()
    {
        if (_pending is not null) return _pending.Task;
        _pending = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var task = _pending.Task;
        _label.IsVisible = false; _editor.IsVisible = true;
        _editor.Text = _label.Text;
        _editor.Focus(); _editor.SelectAll();
        return task;
    }
    private void Complete(string? name)
    {
        var pending = _pending;
        _pending = null;
        _editor.IsVisible = false; _label.IsVisible = true;
        pending?.TrySetResult(name);
    }
}
