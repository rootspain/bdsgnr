using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using BlastDesigner.App.Services;

namespace BlastDesigner.App.Views;

/// <summary>Master/detail editor for a list: items on the left, a property grid for the selected item on the right.</summary>
public sealed class ListEditor<T> : Grid where T : class
{
    private readonly ListBox _list = new() { Background = Brushes.Transparent, Margin = new Thickness(6) };
    private readonly StackPanel _detail = new() { Spacing = 16, Margin = new Thickness(16, 14) };
    private readonly PropertyGrid _grid;
    private readonly Func<T, string> _display;
    private readonly Func<T>? _create;
    private readonly Func<T, T>? _clone;

    public ListEditor(List<T> items, Func<T, string> display, Func<T>? create, Func<T, T>? clone, PropertyGrid? grid = null)
    {
        Items = items;
        _display = display;
        _create = create;
        _clone = clone;
        _grid = grid ?? new PropertyGrid();
        if (_grid.Sections == null) FormLayouts.Apply(_grid, typeof(T));
        ColumnDefinitions = new ColumnDefinitions("290,*");

        var buttons = new WrapPanel { Margin = new Thickness(10, 8, 6, 6) };
        void Add(Button b) { b.Margin = new Thickness(0, 0, 4, 4); buttons.Children.Add(b); }
        if (create != null)
        {
            var add = FormKit.Tool("plus", Loc.Ui("Add"), () => Insert(create()));
            add.Name = "listAdd";
            Add(add);
        }
        if (clone != null) Add(FormKit.Tool("dup", null, () => { if (Selected is { } s) Insert(clone(s)); }, Loc.Ui("Duplicate")));
        Add(FormKit.Tool("trash", null, () =>
        {
            if (Selected is not { } s) return;
            var i = Items.IndexOf(s);
            Items.Remove(s);
            Reload(Math.Min(i, Items.Count - 1));
            Changed?.Invoke();
        }, Loc.Ui("Delete")));
        // narrow, for the four small buttons to stay on one line under a narrow list
        void Arrow(string icon, int step)
        {
            var b = FormKit.Tool(icon, null, () => Move(step));
            b.MinWidth = 0;
            b.Padding = new Thickness(3, 0);
            Add(b);
        }
        Arrow("arrowup", -1);
        Arrow("arrowdown", 1);

        DockPanel.SetDock(buttons, Dock.Bottom);
        _detail.Children.Add(_grid);
        var right = new Border { Background = Look.Panel, BorderBrush = Look.Line, BorderThickness = new Thickness(1, 0, 0, 0), Child = new ScrollViewer { Content = _detail } };
        SetColumn(right, 1);
        Children.Add(new DockPanel { Children = { buttons, _list } });
        Children.Add(right);

        _list.SelectionChanged += (_, _) =>
        {
            _grid.SetTarget(Selected);
            SelectionChanged?.Invoke(Selected);
        };
        _grid.PropertyEdited += _ =>
        {
            var i = _list.SelectedIndex;
            if (i >= 0 && _list.ItemsSource is List<string> names && i < names.Count && Selected is { } s && names[i] != _display(s)) Reload(i);
            Changed?.Invoke();
        };
        Reload(Items.Count > 0 ? 0 : -1);
    }

    public List<T> Items { get; }
    public PropertyGrid Grid => _grid;
    public T? Selected => _list.SelectedIndex >= 0 && _list.SelectedIndex < Items.Count ? Items[_list.SelectedIndex] : null;
    public event Action<T?>? SelectionChanged;
    public event Action? Changed;

    /// <summary>Width of the list; the fields of the selected item take the rest.</summary>
    public double ListWidth { set => ColumnDefinitions[0].Width = new GridLength(value); }

    /// <summary>Something more about the selected item, under its fields.</summary>
    public void AddBelow(Control c) => _detail.Children.Add(c);

    private void Insert(T item)
    {
        var i = _list.SelectedIndex >= 0 ? _list.SelectedIndex + 1 : Items.Count;
        Items.Insert(i, item);
        Reload(i);
        Changed?.Invoke();
    }

    private void Move(int delta)
    {
        var i = _list.SelectedIndex;
        var j = i + delta;
        if (i < 0 || j < 0 || j >= Items.Count) return;
        (Items[i], Items[j]) = (Items[j], Items[i]);
        Reload(j);
        Changed?.Invoke();
    }

    public void Reload(int select = -1)
    {
        _list.ItemsSource = Items.Select(_display).ToList();
        _list.SelectedIndex = select;
        _hovered = -1;
    }

    private int _hovered = -1;

    /// <summary>Index shown with the hover mark, -1 when none.</summary>
    public int Hovered => _hovered;

    /// <summary>
    /// Marks a row as hovered from outside the list (for example from a drawing): an amber outline that is distinct from
    /// the selection fill. -1 clears the mark.
    /// </summary>
    public void Hover(int index)
    {
        if (index == _hovered) return;
        if (_list.ContainerFromIndex(_hovered) is ListBoxItem old) { old.BorderThickness = new Thickness(0); old.BorderBrush = null; }
        _hovered = index >= 0 && index < Items.Count ? index : -1;
        if (_list.ContainerFromIndex(_hovered) is ListBoxItem item)
        {
            item.BorderBrush = Look.Accent;
            item.BorderThickness = new Thickness(1.5);
            item.BringIntoView();
        }
    }

    /// <summary>Selects a row by index (as a click in the list would).</summary>
    public void Select(int index)
    {
        if (index >= 0 && index < Items.Count) _list.SelectedIndex = index;
    }
}
