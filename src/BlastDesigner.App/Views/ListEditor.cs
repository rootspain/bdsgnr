using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using BlastDesigner.App.Services;

namespace BlastDesigner.App.Views;

/// <summary>Master/detail editor for a list: items on the left, a property grid for the selected item on the right.</summary>
public sealed class ListEditor<T> : Grid where T : class
{
    private readonly ListBox _list = new() { MinWidth = 200 };
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
        ColumnDefinitions = new ColumnDefinitions("220,8,*");
        RowDefinitions = new RowDefinitions("*,Auto");

        var buttons = new WrapPanel { Margin = new Thickness(0, 4) };
        if (create != null) buttons.Children.Add(Btn("Add", () => Insert(create())));
        if (clone != null) buttons.Children.Add(Btn("Duplicate", () => { if (Selected is { } s) Insert(clone(s)); }));
        buttons.Children.Add(Btn("Delete", () =>
        {
            if (Selected is not { } s) return;
            var i = Items.IndexOf(s);
            Items.Remove(s);
            Reload(Math.Min(i, Items.Count - 1));
            Changed?.Invoke();
        }));
        buttons.Children.Add(Btn("▲", () => Move(-1)));
        buttons.Children.Add(Btn("▼", () => Move(1)));

        SetColumn(_list, 0);
        SetRow(buttons, 1);
        var scroll = new ScrollViewer { Content = _grid };
        SetColumn(scroll, 2);
        SetRowSpan(scroll, 2);
        Children.Add(_list);
        Children.Add(buttons);
        Children.Add(scroll);

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

    private static Button Btn(string text, Action a)
    {
        var b = new Button { Content = Loc.T(text), Margin = new Thickness(0, 0, 4, 4) };
        b.Click += (_, _) => a();
        return b;
    }

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
