using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using BlastDesigner.App.Services;
using BlastDesigner.Domain.Model;

namespace BlastDesigner.App.Views;

/// <summary>
/// The tables above the products in Available resources: families, kinds, groups and types. Records are added, edited,
/// deleted logically (a parent with its children, after a warning) and restored one at a time, on the copies of the window.
/// </summary>
public sealed class CatalogueEditor : Grid
{
    /// <summary>The tables in the order of their tabs, after the products.</summary>
    public enum Table { Types, Kinds, Families, Groups }

    private static readonly IBrush Warn = new SolidColorBrush(Color.FromUInt32(0xFFF0C13A));
    private static readonly IBrush WarnSoft = new SolidColorBrush(Color.FromUInt32(0x1AF0C13A));

    private readonly ProductCatalogue _tables;
    private readonly List<Product> _products;
    private readonly StackPanel _list = new() { Spacing = 2 };
    private readonly StackPanel _props = new() { Spacing = 14, Margin = new Thickness(18, 16) };
    private readonly Border _confirm = new() { IsVisible = false, Name = "tableConfirm" };
    private readonly CheckBox _showDeleted = new() { Name = "tableShowDeleted" };
    private readonly Button _delete, _restore, _up, _down;
    /// <summary>The row of the kinds no group lists, at the end of the groups: worked out, not a record of the table.</summary>
    private readonly CatalogueGroup _unassigned = new() { Id = "unassigned", Name = "<unassigned>" };
    private Table _shown;
    private ICatalogueRecord? _selected;

    public CatalogueEditor(ProductCatalogue tables, List<Product> products)
    {
        _tables = tables;
        _products = products;
        ColumnDefinitions = new ColumnDefinitions("360,*");
        _showDeleted.Content = L("Show deleted");
        _showDeleted.IsCheckedChanged += (_, _) => Fill();
        var add = Tool("plus", L("Add"), Add, "tableAdd");
        _delete = Tool("trash", null, AskDelete, "tableDelete");
        ToolTip.SetTip(_delete, L("Delete"));
        _restore = Tool("undo", L("Restore"), Restore, "tableRestore");
        _up = Tool("arrowup", null, () => Move(-1), "tableUp");
        _down = Tool("arrowdown", null, () => Move(1), "tableDown");
        var bottom = new StackPanel { Spacing = 8, Margin = new Thickness(14, 10, 14, 14), Children = { _confirm, Ui.Row(6, add, _delete, _restore, _up, _down), _showDeleted } };
        var left = new DockPanel();
        DockPanel.SetDock(bottom, Dock.Bottom);
        left.Children.Add(bottom);
        left.Children.Add(new Border { BorderBrush = Look.Line, BorderThickness = new Thickness(0, 0, 0, 1), Child = new ScrollViewer { Content = new Border { Padding = new Thickness(8, 6), Child = _list } } });
        var right = new Border { Background = Look.Panel, BorderBrush = Look.Line, BorderThickness = new Thickness(1, 0, 0, 0), Child = new ScrollViewer { Content = _props } };
        SetColumn(right, 1);
        Children.Add(left);
        Children.Add(right);
    }

    public Table Shown
    {
        get => _shown;
        set { _shown = value; _selected = null; _confirm.IsVisible = false; Fill(); }
    }

    /// <summary>The record selected in the list.</summary>
    public ICatalogueRecord? Selected => _selected;

    private static string L(string english) => Loc.Ui(english);

    private static Button Tool(string icon, string? text, Action click, string name)
    {
        var content = text == null ? Look.Icon(icon, 16, Look.TextSoft) : Ui.Row(6, Look.Icon(icon, 16, Look.TextSoft), new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
        var b = new Button { Content = content, Height = 32, MinWidth = 32, Padding = new Thickness(text == null ? 8 : 12, 0), CornerRadius = new CornerRadius(6), BorderBrush = Look.LineStrong, BorderThickness = new Thickness(1), Background = Look.Ribbon, VerticalContentAlignment = VerticalAlignment.Center, HorizontalContentAlignment = HorizontalAlignment.Center, Name = name };
        b.Click += (_, _) => Ui.Safe(click);
        return b;
    }

    private IEnumerable<ICatalogueRecord> Records => _shown switch
    {
        Table.Families => _tables.Families.OrderBy(f => KindOrder(f.KindId)).ThenBy(f => f.Name),
        Table.Kinds => _tables.Kinds.OrderBy(k => k.Order),
        Table.Groups => _tables.Groups.OrderBy(g => g.Order).Append(_unassigned),
        _ => _tables.Types.OrderBy(t => t.Order)
    };

    private int KindOrder(string id) => _tables.Kinds.FirstOrDefault(k => k.Id == id)?.Order ?? int.MaxValue;
    private string KindName(string id) => _tables.Kinds.FirstOrDefault(k => k.Id == id) is { } k ? Loc.T(k.Name) : "";

    private static string NameOf(ICatalogueRecord r) => r switch
    {
        CatalogueFamily f => f.Name,
        CatalogueKind k => Loc.T(k.Name),
        CatalogueGroup g => L(g.Name),
        CatalogueType t => Loc.T(t.Name),
        _ => ""
    };

    private bool CanRestore(ICatalogueRecord r) => r switch
    {
        CatalogueKind k => _tables.CanRestore(k),
        CatalogueFamily f => _tables.CanRestore(f),
        _ => true
    };

    private void Fill()
    {
        var show = _showDeleted.IsChecked == true;
        var records = Records.Where(r => show || !r.Deleted).ToList();
        if (_selected == null || !records.Contains(_selected)) _selected = records.FirstOrDefault();
        _list.Children.Clear();
        foreach (var r in records)
        {
            var on = r == _selected;
            var note = r is CatalogueFamily f ? KindName(f.KindId) : r == _unassigned ? _tables.UnassignedKinds.Count().ToString() : "";
            var row = new Button
            {
                Height = 34,
                Padding = new Thickness(10, 0),
                CornerRadius = new CornerRadius(6),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Center,
                Background = on ? Look.AccentSoft : Brushes.Transparent,
                BorderBrush = on ? Look.Accent : Brushes.Transparent,
                BorderThickness = new Thickness(1),
                Opacity = r.Deleted ? 0.55 : 1,
                Tag = "row:" + r.Id,
                Content = new DockPanel
                {
                    Children =
                    {
                        new TextBlock { [DockPanel.DockProperty] = Dock.Right, Text = r.Deleted ? L("deleted") : note, FontSize = 11, Foreground = Look.Faint, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) },
                        new TextBlock { Text = NameOf(r), Foreground = on ? Look.Text : Look.TextSoft, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis }
                    }
                }
            };
            row.Classes.Add("flat");
            row.Click += (_, _) => { _selected = r; _confirm.IsVisible = false; Fill(); };
            _list.Children.Add(row);
        }
        _delete.IsEnabled = _selected is { Deleted: false } && _selected != _unassigned;
        _restore.IsVisible = _selected is { Deleted: true };
        _restore.IsEnabled = _selected != null && CanRestore(_selected);
        ToolTip.SetTip(_restore, _restore.IsEnabled ? null : L("Restore the parent record first"));
        _up.IsVisible = _down.IsVisible = _shown != Table.Families;
        _up.IsEnabled = _down.IsEnabled = _selected != _unassigned;
        FillProps();
    }

    private static Control Labeled(string label, Control field) => new StackPanel
    {
        Spacing = 4,
        Children = { new TextBlock { Text = L(label), FontSize = 12, Foreground = Look.Muted }, field }
    };

    private void FillProps()
    {
        _props.Children.Clear();
        if (_selected is not { } r) return;
        if (r == _unassigned)
        {
            var kinds = _tables.UnassignedKinds.Select(k => Loc.T(k.Name)).ToList();
            _props.Children.Add(new TextBlock { Text = L(kinds.Count > 0 ? "Kinds no group lists; a group takes them with its boxes" : "Every kind is in a group"), Foreground = Look.Muted, TextWrapping = TextWrapping.Wrap });
            foreach (var k in kinds) _props.Children.Add(new TextBlock { Text = k, Foreground = Look.Text });
            return;
        }
        // the name as the list shows it: a built-in name in the language of the program
        var name = new TextBox { Name = "tableName", Text = NameOf(r), Height = 32, MinHeight = 32, Padding = new Thickness(8, 5), Background = Look.Field, BorderBrush = Look.LineStrong };
        name.LostFocus += (_, _) =>
        {
            var v = (name.Text ?? "").Trim();
            if (v.Length == 0 || v == NameOf(r)) return;
            switch (r)
            {
                case CatalogueFamily f: f.Name = v; break;
                case CatalogueKind k: k.Name = v; break;
                case CatalogueGroup g: g.Name = v; break;
                case CatalogueType t: t.Name = v; break;
            }
            Fill();
        };
        _props.Children.Add(Labeled("Name", name));
        switch (r)
        {
            case CatalogueFamily family: _props.Children.Add(Labeled("Product kind", FamilyKind(family))); break;
            case CatalogueKind kind: _props.Children.Add(Labeled("How the program calculates it", Behaviour(kind))); break;
            case CatalogueGroup group: _props.Children.Add(Labeled("Kinds", KindChecks(group.KindIds))); break;
            case CatalogueType type:
                var own = new CheckBox { Content = L("Own delay instead of a product"), IsChecked = type.UserDelay, Name = "tableUserDelay" };
                own.IsCheckedChanged += (_, _) => type.UserDelay = own.IsChecked == true;
                _props.Children.Add(own);
                _props.Children.Add(Labeled("Kinds", KindChecks(type.KindIds)));
                break;
        }
    }

    /// <summary>The kind of a family; its products go with it.</summary>
    private Control FamilyKind(CatalogueFamily family)
    {
        var kinds = _tables.Kinds.Where(k => !k.Deleted || k.Id == family.KindId).OrderBy(k => k.Order).ToList();
        var combo = new ComboBox { ItemsSource = kinds.Select(k => Loc.T(k.Name)).ToList(), SelectedIndex = kinds.FindIndex(k => k.Id == family.KindId), HorizontalAlignment = HorizontalAlignment.Stretch, Height = 32, Name = "tableKind" };
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedIndex < 0 || kinds[combo.SelectedIndex].Id == family.KindId) return;
            var kind = kinds[combo.SelectedIndex];
            family.KindId = kind.Id;
            foreach (var p in _products.Where(p => p.FamilyId == family.Id))
            {
                p.SetKind(kind);
                p.FamilyId = family.Id;
            }
            Fill();
        };
        return combo;
    }

    /// <summary>What a behaviour does in the calculations: the fixed list of the program, not the records of the table of kinds.</summary>
    private static string BehaviourText(ProductKind behaviour) => L(behaviour switch
    {
        ProductKind.BulkExplosive => "Explosive: mass by volume",
        ProductKind.PackagedExplosive => "Explosive: mass by cartridges",
        ProductKind.Stemming => "Column filler: mass by volume, counts as stemming",
        ProductKind.GasBag => "In the column: no mass, counted in pieces",
        ProductKind.Booster => "Primer: counted in pieces",
        ProductKind.DownholeDetonator => "In-hole delay",
        ProductKind.SurfaceConnector => "Delay on the tie",
        ProductKind.ElectronicDetonator => "Programmed delay, tie without a delay",
        ProductKind.DetonatingCord => "Delay from length and velocity of detonation",
        ProductKind.CordRelay => "Delay on the tie, on cord",
        ProductKind.ElectricDetonator => "In-hole delay, tie without a delay",
        ProductKind.HarnessWire => "Tie without a delay",
        _ => "No rules of its own"
    });

    /// <summary>What the calculations do with the products of a behaviour.</summary>
    private static string BehaviourNote(ProductKind behaviour) => L(behaviour switch
    {
        ProductKind.BulkExplosive => "Charge of the hole: the mass comes from the density and the volume of the deck, with the loading factor. It counts in the explosive of the hole and the blast and in the predictions; a deck of it needs a primer and is checked against water.",
        ProductKind.PackagedExplosive => "Charge of the hole: the mass comes from the cartridges that fit the deck, by their mass and length. It counts in the explosive of the hole and the blast and in the predictions; a deck of it needs a primer and is checked against water.",
        ProductKind.Stemming => "A deck of it counts as stemming: in the stemming length and the minimum stemming check. Its mass comes from the density and the volume and stays out of the explosive; quantities are in kilograms.",
        ProductKind.GasBag => "A deck of it takes up its place in the column and has no mass. Quantities count it in pieces.",
        ProductKind.Booster => "Offered as the booster of a primer. Quantities count it in pieces.",
        ProductKind.DownholeDetonator => "Offered as the detonator of a primer: its delay is added to the firing time of the hole. Quantities count it in pieces.",
        ProductKind.SurfaceConnector => "Used for ties: the delay of the product is the delay of the tie, so it sets the times of the holes down the line.",
        ProductKind.ElectronicDetonator => "Offered as the detonator of a primer: a hole with it counts as electronic and its time is programmed. On a tie it adds no delay.",
        ProductKind.DetonatingCord => "Used for ties: the delay of the tie comes from its length and the velocity of detonation of the product.",
        ProductKind.CordRelay => "Used for ties: the delay of the product is the delay of the tie, as with a surface connector.",
        ProductKind.ElectricDetonator => "Offered as the detonator of a primer: its delay is added to the firing time of the hole. On a tie it adds no delay.",
        ProductKind.HarnessWire => "Used for ties that only carry the signal: a tie with it adds no delay.",
        _ => "No calculation of its own: it is not explosive or stemming, quantities count it in pieces, and on a tie its delay is used."
    });

    private static Control Behaviour(CatalogueKind kind)
    {
        var values = Enum.GetValues<ProductKind>();
        var combo = new ComboBox { ItemsSource = values.Select(BehaviourText).ToList(), SelectedIndex = Array.IndexOf(values, kind.Behaviour), HorizontalAlignment = HorizontalAlignment.Stretch, Height = 32, Name = "tableBehaviour" };
        var note = new TextBlock { Name = "tableBehaviourNote", Text = BehaviourNote(kind.Behaviour), FontSize = 12, Foreground = Look.Muted, TextWrapping = TextWrapping.Wrap };
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedIndex < 0) return;
            kind.Behaviour = values[combo.SelectedIndex];
            note.Text = BehaviourNote(kind.Behaviour);
        };
        return new StackPanel { Spacing = 8, Children = { combo, note } };
    }

    private Control KindChecks(List<string> ids)
    {
        var s = new StackPanel { Spacing = 2 };
        foreach (var k in _tables.Kinds.Where(k => !k.Deleted).OrderBy(k => k.Order))
        {
            var id = k.Id;
            var box = new CheckBox { Content = Loc.T(k.Name), IsChecked = ids.Contains(id), Tag = "kind:" + id };
            box.IsCheckedChanged += (_, _) => { if (box.IsChecked == true) { if (!ids.Contains(id)) ids.Add(id); } else ids.Remove(id); };
            s.Children.Add(box);
        }
        return s;
    }

    private void Add()
    {
        var order = Records.Count(r => r != _unassigned);
        ICatalogueRecord r;
        switch (_shown)
        {
            case Table.Families:
                if (_tables.Kinds.Where(k => !k.Deleted).MinBy(k => k.Order) is not { } kind) return;
                var family = new CatalogueFamily { Name = L("New family"), KindId = kind.Id };
                _tables.Families.Add(family);
                r = family;
                break;
            case Table.Kinds:
                var k2 = new CatalogueKind { Name = L("New kind"), Behaviour = ProductKind.Other, Order = order };
                _tables.Kinds.Add(k2);
                r = k2;
                break;
            case Table.Groups:
                var g = new CatalogueGroup { Name = L("New group"), Order = order };
                _tables.Groups.Add(g);
                r = g;
                break;
            default:
                var t = new CatalogueType { Name = L("New type"), Order = order };
                _tables.Types.Add(t);
                r = t;
                break;
        }
        r.IsNew = true;
        _selected = r;
        Fill();
    }

    /// <summary>
    /// A record added just now and tied to nothing goes for good. Otherwise it is deleted logically: at once when nothing
    /// goes with it, after saying what goes when something does.
    /// </summary>
    private void AskDelete()
    {
        if (_selected is not { } r) return;
        if (_tables.CanRemove(r, _products))
        {
            _tables.Remove(r);
            _selected = null;
            _confirm.IsVisible = false;
            Fill();
            return;
        }
        var (kinds, families, products) = r switch
        {
            CatalogueType t => _tables.Dependents(t, _products),
            CatalogueKind k when _tables.Dependents(k, _products) is var (f, p) => (0, f, p),
            CatalogueFamily f => (0, 0, _tables.Dependents(f, _products)),
            _ => (0, 0, 0)
        };
        if (kinds + families + products == 0) { Delete(r); return; }
        var yes = new Button { Content = L("Delete"), Height = 28, Padding = new Thickness(12, 0), CornerRadius = new CornerRadius(6), Background = new SolidColorBrush(Color.FromUInt32(0xFFE5484D)), Foreground = Brushes.White, BorderThickness = new Thickness(0), VerticalContentAlignment = VerticalAlignment.Center, Tag = "confirmTableDelete" };
        var no = new Button { Content = Loc.T("Cancel"), Height = 28, Padding = new Thickness(12, 0), CornerRadius = new CornerRadius(6), Background = Brushes.Transparent, BorderBrush = Look.LineStrong, BorderThickness = new Thickness(1), VerticalContentAlignment = VerticalAlignment.Center };
        yes.Click += (_, _) => Delete(r);
        no.Click += (_, _) => _confirm.IsVisible = false;
        _confirm.Background = WarnSoft;
        _confirm.BorderBrush = Warn;
        _confirm.BorderThickness = new Thickness(1);
        _confirm.CornerRadius = new CornerRadius(8);
        _confirm.Padding = new Thickness(10, 8);
        _confirm.Child = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                Ui.Row(8, Look.Icon("warn", 15, Warn), new TextBlock { Text = Loc.F("Delete \"{0}\"?", NameOf(r)), FontWeight = FontWeight.SemiBold, Foreground = Look.Text, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap }),
                new TextBlock { Text = Loc.F(L("Also deleted: kinds {0}, families {1}, products {2}"), kinds, families, products), FontSize = 12, Foreground = Look.TextSoft, TextWrapping = TextWrapping.Wrap },
                Ui.Row(6, yes, no)
            }
        };
        _confirm.IsVisible = true;
    }

    private void Delete(ICatalogueRecord r)
    {
        switch (r)
        {
            case CatalogueType t: _tables.Delete(t, _products); break;
            case CatalogueKind k: _tables.Delete(k, _products); break;
            case CatalogueGroup g: _tables.Delete(g); break;
            case CatalogueFamily f: _tables.Delete(f, _products); break;
        }
        _confirm.IsVisible = false;
        Fill();
    }

    /// <summary>Brings back the selected record only, not what was deleted with it.</summary>
    private void Restore()
    {
        if (_selected is not { } r || !CanRestore(r)) return;
        r.Deleted = false;
        Fill();
    }

    private void Move(int dir)
    {
        var records = Records.Where(r => !r.Deleted && r != _unassigned).ToList();
        if (_selected == null || records.IndexOf(_selected) is var i && (i < 0 || i + dir < 0 || i + dir >= records.Count)) return;
        (records[i], records[i + dir]) = (records[i + dir], records[i]);
        for (int n = 0; n < records.Count; n++)
            switch (records[n])
            {
                case CatalogueKind k: k.Order = n; break;
                case CatalogueGroup g: g.Order = n; break;
                case CatalogueType t: t.Order = n; break;
            }
        Fill();
    }
}
