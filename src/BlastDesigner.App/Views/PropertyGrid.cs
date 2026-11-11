using System.Globalization;
using System.Reflection;
using System.Text.Json.Serialization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using BlastDesigner.App.Services;
using BlastDesigner.Domain.Geometry;

namespace BlastDesigner.App.Views;

/// <summary>A group of fields of a <see cref="PropertyGrid"/>, its properties in the order given.</summary>
public sealed record PropertySection(string Title, params string[] Names);

/// <summary>
/// Reflection-based editor for the simple properties of an object (numbers, text, flags, enums, dates, colours, points):
/// a row for each, or with <see cref="Sections"/> folding groups of captioned fields, two to a row.
/// </summary>
public sealed class PropertyGrid : Grid
{
    private object? _target;

    public PropertyGrid()
    {
        ColumnDefinitions = new ColumnDefinitions("2*,3*");
        Margin = new Thickness(4);
    }

    public HashSet<string> Exclude { get; set; } = new();
    public HashSet<string> Changed { get; } = new();
    /// <summary>Properties whose values differ between the edited objects; their editors start empty ("mixed").</summary>
    public HashSet<string> Mixed { get; set; } = new();
    /// <summary>For [Flags] properties: bits that differ between the edited objects.</summary>
    public Dictionary<string, long> MixedBits { get; set; } = new();
    /// <summary>For [Flags] properties with mixed bits: the bits the user actually changed.</summary>
    public Dictionary<string, long> FlagEdits { get; } = new();
    /// <summary>For points (<see cref="Vec3"/>): the axes X, Y, Z that differ between the edited objects.</summary>
    public Dictionary<string, bool[]> MixedAxes { get; set; } = new();
    /// <summary>For points with mixed axes: the axes the user actually typed.</summary>
    public Dictionary<string, bool[]> AxisEdits { get; } = new();

    /// <summary>Lays the fields out in these groups; the last one also takes the properties no group names.</summary>
    public IReadOnlyList<PropertySection>? Sections { get; set; }
    /// <summary>Titles of the groups folded; shared with the next grid so a fold outlasts the selection.</summary>
    public HashSet<string> Collapsed { get; set; } = new();
    /// <summary>The titles of the groups fold them, as in the narrow panel; a window shows its groups whole.</summary>
    public bool Foldable { get; set; } = true;
    /// <summary>Short texts stand two to a row, as numbers do; in the narrow panel each takes the whole row.</summary>
    public bool TextInColumns { get; set; }
    /// <summary>Properties that take the whole row of their group.</summary>
    public HashSet<string> WideFields { get; } = new();
    /// <summary>Enum properties shown as a row of chips, one for each value, instead of a list.</summary>
    public HashSet<string> Chips { get; } = new();
    /// <summary>A control of the caller's in the cell after a property; it moves to each new form of the grid.</summary>
    public Dictionary<string, Control> Beside { get; } = new();
    /// <summary>The two columns of the fields of a section, with the gap between them.</summary>
    public string FieldColumns { get; set; } = "*,8,*";
    /// <summary>Unit shown in the field of a property, by property name.</summary>
    public Dictionary<string, string> Units { get; } = new();

    private string MixedText => Sections != null ? Loc.T("(mixed)").Trim('(', ')') : Loc.T("(mixed)");

    /// <summary>Prepares the grid for editing several objects: the first one is the template, differing values show as mixed.</summary>
    public void SetTargets(object template, IReadOnlyList<object> objects)
    {
        Mixed = FindMixed(objects);
        MixedBits = FindMixedBits(objects);
        MixedAxes = FindMixedAxes(objects);
        SetTarget(template);
    }

    /// <summary>Names of simple properties whose values are not the same on all objects.</summary>
    public static HashSet<string> FindMixed(IReadOnlyList<object> objects)
    {
        var result = new HashSet<string>();
        if (objects.Count < 2) return result;
        foreach (var p in objects[0].GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (p.GetIndexParameters().Length > 0 || p.GetMethod == null) continue;
            var t = Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType;
            if (!(t.IsPrimitive || t.IsEnum || t == typeof(string) || t == typeof(Vec3) || t == typeof(DateTime) || t == typeof(Guid))) continue;
            if (t.IsEnum && t.GetCustomAttribute<FlagsAttribute>() != null) continue;
            var first = p.GetValue(objects[0]);
            if (objects.Skip(1).Any(o => !Equals(p.GetValue(o), first))) result.Add(p.Name);
        }
        return result;
    }

    public static Dictionary<string, long> FindMixedBits(IReadOnlyList<object> objects)
    {
        var result = new Dictionary<string, long>();
        if (objects.Count < 2) return result;
        foreach (var p in objects[0].GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!p.PropertyType.IsEnum || p.PropertyType.GetCustomAttribute<FlagsAttribute>() == null) continue;
            long and = -1, or = 0;
            foreach (var o in objects) { var v = Convert.ToInt64(p.GetValue(o)); and &= v; or |= v; }
            if ((and ^ or) != 0) result[p.Name] = and ^ or;
        }
        return result;
    }

    public static Dictionary<string, bool[]> FindMixedAxes(IReadOnlyList<object> objects)
    {
        var result = new Dictionary<string, bool[]>();
        if (objects.Count < 2) return result;
        foreach (var p in objects[0].GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (p.PropertyType != typeof(Vec3) || p.GetIndexParameters().Length > 0 || p.GetMethod == null) continue;
            var first = (Vec3)p.GetValue(objects[0])!;
            var axes = new bool[3];
            foreach (var o in objects.Skip(1))
            {
                var v = (Vec3)p.GetValue(o)!;
                axes[0] |= v.X != first.X; axes[1] |= v.Y != first.Y; axes[2] |= v.Z != first.Z;
            }
            if (axes.Any(a => a)) result[p.Name] = axes;
        }
        return result;
    }

    /// <summary>Copies the edited properties to a target; flag properties with mixed bits only change the bits that were edited,
    /// points with mixed axes only the axes typed.</summary>
    public void ApplyTo(object template, object target)
    {
        foreach (var n in Changed)
        {
            var p = template.GetType().GetProperty(n);
            if (p?.SetMethod == null || !p.DeclaringType!.IsInstanceOfType(target)) continue;
            if (FlagEdits.TryGetValue(n, out var bits))
            {
                var now = Convert.ToInt64(p.GetValue(target));
                var want = Convert.ToInt64(p.GetValue(template));
                p.SetValue(target, Enum.ToObject(p.PropertyType, (now & ~bits) | (want & bits)));
            }
            else if (AxisEdits.TryGetValue(n, out var axes) && MixedAxes.TryGetValue(n, out var mixedAxes))
            {
                var now = (Vec3)p.GetValue(target)!;
                var want = (Vec3)p.GetValue(template)!;
                // an axis the objects share is the same everywhere, so it is copied as it stands
                bool Take(int i) => axes[i] || !mixedAxes[i];
                p.SetValue(target, new Vec3(Take(0) ? want.X : now.X, Take(1) ? want.Y : now.Y, Take(2) ? want.Z : now.Z));
            }
            else p.SetValue(target, p.GetValue(template));
        }
    }
    public bool ReadOnly { get; set; }
    /// <summary>Named choices for id properties (e.g. products), keyed by property name.</summary>
    public Dictionary<string, IReadOnlyList<(string Id, string Name)>> Choices { get; } = new();
    /// <summary>Colour swatch (ARGB) shown next to each value of an enum type in its list, e.g. the colour of each kind of surface.</summary>
    public Dictionary<Type, Func<object, uint>> EnumColors { get; } = new();
    public event Action<string>? PropertyEdited;

    public object? Target => _target;

    public void SetTarget(object? target)
    {
        _target = target;
        Changed.Clear();
        Children.Clear();
        RowDefinitions.Clear();
        if (target == null) return;
        if (Sections != null) { BuildSections(target); return; }
        int row = 0;
        foreach (var p in target.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (Exclude.Contains(p.Name) || p.GetIndexParameters().Length > 0) continue;
            if (p.GetCustomAttribute<JsonIgnoreAttribute>() != null && p.SetMethod == null) { AddReadOnly(p, target, ref row); continue; }
            var editor = CreateEditor(p, target);
            if (editor == null) continue;
            RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var label = new TextBlock { Text = Loc.Property(p.Name), VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 8, 2) };
            SetRow(label, row);
            SetRow(editor, row);
            SetColumn(editor, 1);
            editor.Name = "prop" + p.Name;
            editor.Margin = new Thickness(0, 2);
            editor.IsEnabled = !ReadOnly && p.SetMethod?.IsPublic == true;
            Children.Add(label);
            Children.Add(editor);
            row++;
        }
    }

    private void BuildSections(object target)
    {
        Margin = default;
        var cells = new List<(string Name, Control Cell, bool Wide)>();
        foreach (var p in target.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            if (!Exclude.Contains(p.Name) && p.GetIndexParameters().Length == 0 && SectionCell(p, target) is var (cell, wide)) cells.Add((p.Name, cell, wide));
        var body = new StackPanel { Spacing = 12 };
        for (int i = 0; i < Sections!.Count; i++)
        {
            var names = Sections[i].Names;
            var listed = names.SelectMany(n => cells.Where(c => c.Name == n));
            if (i == Sections.Count - 1) listed = listed.Concat(cells.Where(c => !Sections.Any(s => s.Names.Contains(c.Name))));
            var fields = listed.SelectMany(c => Beside.TryGetValue(c.Name, out var extra) ? new[] { (c.Cell, c.Wide), (extra, false) } : new[] { (c.Cell, c.Wide) }).ToList();
            if (fields.Count > 0) body.Children.Add(Section(Sections[i].Title, fields));
        }
        SetColumnSpan(body, 2);
        Children.Add(body);
    }

    /// <summary>A group: its title folds it, under it the fields two to a row, a wide one on a row of its own.</summary>
    private Control Section(string title, List<(Control Cell, bool Wide)> fields)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions(FieldColumns), RowSpacing = 8, IsVisible = !Collapsed.Contains(title) };
        int row = 0, column = 0;
        foreach (var (cell, wide) in fields)
        {
            if (wide && column == 1) { row++; column = 0; }
            while (g.RowDefinitions.Count <= row) g.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            SetRow(cell, row);
            SetColumn(cell, column * 2);
            if (wide) SetColumnSpan(cell, 3);
            (cell.Parent as Panel)?.Children.Remove(cell);
            g.Children.Add(cell);
            if (wide || column == 1) { row++; column = 0; }
            else column = 1;
        }
        if (!Foldable) return title.Length == 0 ? g : FormKit.Section(Loc.Ui(title), g);
        var chevron = new ContentControl { Content = Look.Icon(g.IsVisible ? "up" : "down", 13, Look.Faint), [DockPanel.DockProperty] = Dock.Right, VerticalAlignment = VerticalAlignment.Center };
        var head = new Button
        {
            Name = "section" + title,
            Height = 24,
            Padding = new Thickness(0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Content = new DockPanel
            {
                Children =
                {
                    chevron,
                    new TextBlock { Text = Loc.Ui(title).ToUpper(CultureInfo.CurrentCulture), FontSize = 11, FontWeight = FontWeight.SemiBold, LetterSpacing = 0.6, Foreground = Look.Blue, VerticalAlignment = VerticalAlignment.Center }
                }
            }
        };
        head.Classes.Add("flat");
        head.Click += (_, _) =>
        {
            g.IsVisible = !g.IsVisible;
            chevron.Content = Look.Icon(g.IsVisible ? "up" : "down", 13, Look.Faint);
            if (g.IsVisible) Collapsed.Remove(title);
            else Collapsed.Add(title);
        };
        return new StackPanel { Spacing = 6, Children = { head, g } };
    }

    /// <summary>The field of a property in a group, its caption above it (beside a switch), and whether it takes a whole row.</summary>
    private (Control Cell, bool Wide)? SectionCell(PropertyInfo p, object target)
    {
        var caption = Loc.Property(p.Name);
        Units.TryGetValue(p.Name, out var unit);
        if (p.GetCustomAttribute<JsonIgnoreAttribute>() != null && p.SetMethod == null)
        {
            var t = Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType;
            if (!(t.IsPrimitive || t == typeof(string) || t == typeof(Vec3) || t.IsEnum)) return null;
            var v = p.GetValue(target);
            return (Captioned(caption, new TextBlock { Text = v is double d ? d.ToString("0.###") : v?.ToString() ?? "", Foreground = Look.Muted }), false);
        }
        var editor = CreateEditor(p, target);
        if (editor == null) return null;
        editor.Name = "prop" + p.Name;
        editor.IsEnabled = !ReadOnly && p.SetMethod?.IsPublic == true;
        if (editor is ToggleSwitch or CheckBox)
        {
            editor.VerticalAlignment = VerticalAlignment.Center;
            editor[DockPanel.DockProperty] = Dock.Right;
            return (new DockPanel { MinHeight = 30, Children = { editor, new TextBlock { Text = caption, Foreground = Look.TextSoft, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap } } }, true);
        }
        Compact(editor);
        if (TextInColumns && p.PropertyType == typeof(string) && editor is TextBox text) text.ClearValue(TextBox.FontFamilyProperty);
        var point = p.PropertyType == typeof(Vec3);
        if (point) caption += " X · Y · Z";
        if (unit != null && (point || editor is not TextBox)) caption += ", " + unit;
        else if (unit != null && editor is TextBox box)
            box.InnerRightContent = new TextBlock { Text = unit, FontSize = 11, Foreground = Look.Faint, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        return (Captioned(caption, editor), point || WideFields.Contains(p.Name) || (p.PropertyType == typeof(string) && (!TextInColumns || editor is TextBox { AcceptsReturn: true })) || (editor is StackPanel && !Chips.Contains(p.Name)));
    }

    private static Control Captioned(string caption, Control field) => new StackPanel
    {
        Spacing = 4,
        Children = { new TextBlock { Text = caption, FontSize = 12, Foreground = Look.Muted, TextWrapping = TextWrapping.Wrap }, field }
    };

    /// <summary>A field made to fit a narrow panel, in the look of the input forms.</summary>
    private static void Compact(Control editor)
    {
        editor.MinWidth = 0;
        editor.Margin = default;
        switch (editor)
        {
            case TextBox box:
                box.Height = box.AcceptsReturn ? double.NaN : 30;
                box.MinHeight = box.AcceptsReturn ? 64 : 30;
                box.Padding = new Thickness(8, 4);
                box.FontFamily = Look.Mono;
                box.Background = Look.Field;
                box.BorderBrush = Look.LineStrong;
                break;
            case ComboBox combo:
                combo.MinHeight = 30;
                combo.HorizontalAlignment = HorizontalAlignment.Stretch;
                break;
            case CalendarDatePicker date:
                date.MinHeight = 30;
                date.HorizontalAlignment = HorizontalAlignment.Stretch;
                break;
            case Panel parts:
                foreach (var part in parts.Children) Compact(part);
                break;
        }
    }

    private void AddReadOnly(PropertyInfo p, object target, ref int row)
    {
        var t = Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType;
        if (!(t.IsPrimitive || t == typeof(string) || t == typeof(Vec3) || t.IsEnum)) return;
        var v = p.GetValue(target);
        RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        var label = new TextBlock { Text = Loc.Property(p.Name), Foreground = Look.Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 8, 2) };
        var value = new TextBlock { Text = v is double d ? d.ToString("0.###") : v?.ToString() ?? "", Foreground = Look.Muted, Margin = new Thickness(0, 2) };
        SetRow(label, row); SetRow(value, row); SetColumn(value, 1);
        Children.Add(label);
        Children.Add(value);
        row++;
    }

    private void Set(PropertyInfo p, object? value)
    {
        if (_target == null) return;
        try
        {
            p.SetValue(_target, value);
            Changed.Add(p.Name);
            PropertyEdited?.Invoke(p.Name);
        }
        catch
        {
            // Invalid input is ignored; the editor keeps showing what the user typed.
        }
    }

    private Control? CreateEditor(PropertyInfo p, object target)
    {
        if (p.SetMethod == null || !p.SetMethod.IsPublic) return null;
        var type = p.PropertyType;
        var under = Nullable.GetUnderlyingType(type);
        var value = p.GetValue(target);
        var mixed = Mixed.Contains(p.Name);

        if (type == typeof(string) && !Choices.ContainsKey(p.Name))
        {
            var tb = new TextBox { Text = mixed ? "" : (string?)value ?? "", Watermark = mixed ? MixedText : null, AcceptsReturn = p.Name is "Description" or "Comment" or "Comments" or "Notes", TextWrapping = TextWrapping.Wrap };
            tb.LostFocus += (_, _) =>
            {
                if (mixed && string.IsNullOrEmpty(tb.Text) && !Changed.Contains(p.Name)) return;
                if (tb.Text != (string?)p.GetValue(target) || mixed) Set(p, tb.Text ?? "");
            };
            return tb;
        }
        if (type == typeof(double) || under == typeof(double) || type == typeof(int) || under == typeof(int))
        {
            var isInt = (under ?? type) == typeof(int);
            var tb = new TextBox { Text = mixed ? "" : Format(value), Watermark = mixed ? MixedText : under != null ? "—" : null };
            // the text taken: an edit can take the box away, which also takes its focus, and that is not a second edit
            string? taken = null;
            bool Commit()
            {
                if (tb.Text == taken) return false;
                var text = (tb.Text ?? "").Trim().Replace(',', '.');
                if (mixed && text.Length == 0 && !Changed.Contains(p.Name)) return false;
                taken = tb.Text;
                if (text.Length == 0 && under != null) { Set(p, null); return true; }
                if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) { taken = null; tb.Text = Format(p.GetValue(target)); return false; }
                // boxed apart: one ?: of int and double is a double, and an int property does not take it
                object v = isInt ? (int)Math.Round(d) : (object)d;
                if (Equals(p.GetValue(target), v) && !mixed) return false;
                Set(p, v);
                return true;
            }
            tb.LostFocus += (_, _) => Commit();
            // Enter takes the number; with nothing to take it goes on to the window (the default button of a dialog)
            tb.KeyDown += (_, e) => { if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.None && Commit()) e.Handled = true; };
            return tb;
        }
        if (type == typeof(uint) && p.Name.Contains("Color"))
        {
            var u = (uint)(value ?? 0u);
            var swatch = new Border { Width = 22, Height = 18, Background = new SolidColorBrush(Color.FromUInt32(u)), BorderBrush = Look.LineStrong, BorderThickness = new Thickness(1) };
            var tb = new TextBox { Text = $"#{u:X8}", Width = 110 };
            tb.LostFocus += (_, _) =>
            {
                var t = (tb.Text ?? "").Trim().TrimStart('#');
                if (t.Length == 6) t = "FF" + t;
                if (uint.TryParse(t, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var c))
                {
                swatch.Background = new SolidColorBrush(Color.FromUInt32(c));
                Set(p, c);
                }
            };
            return new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { swatch, tb } };
        }
        if (type == typeof(bool) && Sections != null)
        {
            var sw = new ToggleSwitch { IsChecked = mixed ? null : (bool?)value, IsThreeState = mixed, OnContent = "", OffContent = "", MinWidth = 0 };
            if (mixed) ToolTip.SetTip(sw, MixedText);
            sw.IsCheckedChanged += (_, _) => { if (sw.IsChecked != null) Set(p, sw.IsChecked == true); };
            return sw;
        }
        if (type == typeof(bool) || under == typeof(bool))
        {
            var cb = new CheckBox { IsChecked = mixed ? null : (bool?)value, IsThreeState = under != null || mixed };
            if (mixed) ToolTip.SetTip(cb, MixedText);
            cb.IsCheckedChanged += (_, _) =>
            {
                if (under == null && cb.IsChecked == null) return;
                Set(p, under != null ? cb.IsChecked : cb.IsChecked == true);
            };
            return cb;
        }
        var enumType = under ?? type;
        if (enumType.IsEnum)
        {
            if (enumType.GetCustomAttribute<FlagsAttribute>() != null)
            {
                var panel = new StackPanel();
                var current = Convert.ToInt64(value ?? 0);
                foreach (var v in Enum.GetValues(enumType))
                {
                var bits = Convert.ToInt64(v);
                if (bits == 0) continue;
                var bitMixed = MixedBits.TryGetValue(p.Name, out var mb) && (mb & bits) != 0;
                var cb = new CheckBox { Content = Loc.Enum(v), IsChecked = bitMixed ? null : (current & bits) == bits, IsThreeState = bitMixed, Margin = new Thickness(0, 0, 8, 0) };
                cb.IsCheckedChanged += (_, _) =>
                {
                    if (cb.IsChecked == null) return;
                    var now = Convert.ToInt64(p.GetValue(target) ?? 0);
                    now = cb.IsChecked == true ? now | bits : now & ~bits;
                    if (MixedBits.ContainsKey(p.Name)) { FlagEdits.TryGetValue(p.Name, out var fe); FlagEdits[p.Name] = fe | bits; }
                    Set(p, Enum.ToObject(enumType, now));
                };
                panel.Children.Add(cb);
                }
                return panel;
            }
            var values = Enum.GetValues(enumType).Cast<object>().ToList();
            if (Chips.Contains(p.Name))
            {
                var chips = new List<Button>();
                foreach (var v in values)
                {
                var chip = Ui.Chip(Loc.Enum(v), () =>
                {
                    Set(p, v);
                    for (int i = 0; i < chips.Count; i++) Ui.SetChip(chips[i], values[i].Equals(v));
                }, v.Equals(value));
                chip.Name = p.Name + v;
                chips.Add(chip);
                }
                return Ui.Row(4, chips.Cast<Control>().ToArray());
            }
            System.Collections.IEnumerable items = values.Select(Loc.Enum).ToList();
            if (EnumColors.TryGetValue(enumType, out var colorOf))
                items = values.Select(v => (Control)Ui.Row(8,
                new Border { Width = 12, Height = 12, CornerRadius = new CornerRadius(3), Background = new SolidColorBrush(Color.FromUInt32(colorOf(v))), BorderBrush = Look.LineStrong, BorderThickness = new Thickness(1), VerticalAlignment = VerticalAlignment.Center },
                new TextBlock { Text = Loc.Enum(v), VerticalAlignment = VerticalAlignment.Center })).ToList();
            var combo = new ComboBox { ItemsSource = items, SelectedIndex = value == null || mixed ? -1 : values.IndexOf(value), PlaceholderText = mixed ? MixedText : null, MinWidth = 160, HorizontalContentAlignment = HorizontalAlignment.Left };
            combo.SelectionChanged += (_, _) => { if (combo.SelectedIndex >= 0) Set(p, values[combo.SelectedIndex]); };
            return combo;
        }
        if (type == typeof(DateTime) || under == typeof(DateTime))
        {
            var dp = new CalendarDatePicker { SelectedDate = (DateTime?)value };
            dp.SelectedDateChanged += (_, _) => Set(p, under != null ? dp.SelectedDate : dp.SelectedDate ?? DateTime.Now);
            return dp;
        }
        if (type == typeof(Vec3))
        {
            var v = (Vec3)value!;
            // with several objects each axis is mixed on its own: an empty one keeps every object's value, a typed one is applied
            var axisMixed = mixed ? MixedAxes.GetValueOrDefault(p.Name) ?? new[] { true, true, true } : new bool[3];
            var boxes = new[] { v.X, v.Y, v.Z }.Select((c, i) => new TextBox { Text = axisMixed[i] ? "" : c.ToString("0.###", CultureInfo.InvariantCulture), Watermark = axisMixed[i] ? MixedText : null, MinWidth = 60 }).ToArray();
            bool Commit()
            {
                var was = (Vec3)p.GetValue(target)!;
                var old = new[] { was.X, was.Y, was.Z };
                var parsed = new double[3];
                var typed = new bool[3];
                for (int i = 0; i < 3; i++)
                {
                    var text = (boxes[i].Text ?? "").Trim();
                    if (axisMixed[i] && text.Length == 0) { parsed[i] = old[i]; continue; }
                    if (!double.TryParse(text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out parsed[i])) return false;
                    typed[i] = axisMixed[i];
                }
                var nv = new Vec3(parsed[0], parsed[1], parsed[2]);
                var edits = AxisEdits.GetValueOrDefault(p.Name);
                var newAxis = typed.Where((t, i) => t && edits?[i] != true).Any();
                if (nv.Equals(was) && !newAxis) return false;
                if (mixed) AxisEdits[p.Name] = Enumerable.Range(0, 3).Select(i => typed[i] || edits?[i] == true).ToArray();
                Set(p, nv);
                return true;
            }
            foreach (var b in boxes)
            {
                b.LostFocus += (_, _) => Commit();
                b.KeyDown += (_, e) => { if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.None && Commit()) e.Handled = true; };
            }
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,4,*,4,*") };
            for (int i = 0; i < 3; i++)
            {
                Grid.SetColumn(boxes[i], i * 2);
                ToolTip.SetTip(boxes[i], new[] { "X", "Y", "Z" }[i]);
                g.Children.Add(boxes[i]);
            }
            return g;
        }
        if (type == typeof(string) && Choices.TryGetValue(p.Name, out var choices))
        {
            var items = new List<(string? Id, string Name)> { (null, Loc.T("(none)")) };
            items.AddRange(choices.Select(c => ((string?)c.Id, c.Name)));
            var combo = new ComboBox { ItemsSource = items.Select(i => i.Name).ToList(), SelectedIndex = mixed ? -1 : Math.Max(0, items.FindIndex(i => Equals(i.Id, value))), PlaceholderText = mixed ? MixedText : null, MinWidth = 200 };
            combo.SelectionChanged += (_, _) => { if (combo.SelectedIndex >= 0) Set(p, items[combo.SelectedIndex].Id); };
            return combo;
        }
        return null;
    }

    private static string Format(object? v) => v switch
    {
        null => "",
        double d => d.ToString("0.####", CultureInfo.InvariantCulture),
        int i => i.ToString(CultureInfo.InvariantCulture),
        _ => v.ToString() ?? ""
    };

    /// <summary>Copies the listed properties from one object to another of the same type.</summary>
    public static void CopyProperties(object from, object to, IEnumerable<string> names)
    {
        foreach (var n in names)
        {
            var p = from.GetType().GetProperty(n);
            if (p?.SetMethod != null && to.GetType().IsAssignableFrom(from.GetType())) p.SetValue(to, p.GetValue(from));
        }
    }
}
