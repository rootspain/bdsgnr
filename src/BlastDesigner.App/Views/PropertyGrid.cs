using System.Globalization;
using System.Reflection;
using System.Text.Json.Serialization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using BlastDesigner.App.Services;
using BlastDesigner.Domain.Geometry;

namespace BlastDesigner.App.Views;

/// <summary>Reflection-based editor for the simple properties of an object (numbers, text, flags, enums, dates, colours, points).</summary>
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

    private static string MixedText => Loc.T("(mixed)");

    /// <summary>Prepares the grid for editing several objects: the first one is the template, differing values show as mixed.</summary>
    public void SetTargets(object template, IReadOnlyList<object> objects)
    {
        Mixed = FindMixed(objects);
        MixedBits = FindMixedBits(objects);
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

    /// <summary>Copies the edited properties to a target; flag properties with mixed bits only change the bits that were edited.</summary>
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
            else p.SetValue(target, p.GetValue(template));
        }
    }
    public bool ReadOnly { get; set; }
    /// <summary>Named choices for Guid? properties (e.g. products), keyed by property name.</summary>
    public Dictionary<string, IReadOnlyList<(Guid Id, string Name)>> Choices { get; } = new();
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
            editor.Margin = new Thickness(0, 2);
            editor.IsEnabled = !ReadOnly && p.SetMethod?.IsPublic == true;
            Children.Add(label);
            Children.Add(editor);
            row++;
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

        if (type == typeof(string))
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
            tb.LostFocus += (_, _) =>
            {
                var text = (tb.Text ?? "").Trim().Replace(',', '.');
                if (mixed && text.Length == 0 && !Changed.Contains(p.Name)) return;
                if (text.Length == 0 && under != null) { Set(p, null); return; }
                if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) { tb.Text = Format(p.GetValue(target)); return; }
                object v = isInt ? (int)Math.Round(d) : d;
                if (!Equals(p.GetValue(target), v) || mixed) Set(p, v);
            };
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
            var boxes = new[] { v.X, v.Y, v.Z }.Select(c => new TextBox { Text = mixed ? "" : c.ToString("0.###", CultureInfo.InvariantCulture), Watermark = mixed ? MixedText : null, MinWidth = 60 }).ToArray();
            foreach (var b in boxes)
                b.LostFocus += (_, _) =>
                {
                    var parsed = boxes.Select(x => double.TryParse((x.Text ?? "").Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : double.NaN).ToArray();
                    if (parsed.Any(double.IsNaN)) return;
                    var nv = new Vec3(parsed[0], parsed[1], parsed[2]);
                    if (!nv.Equals(p.GetValue(target))) Set(p, nv);
                };
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,4,*,4,*") };
            for (int i = 0; i < 3; i++)
            {
                Grid.SetColumn(boxes[i], i * 2);
                ToolTip.SetTip(boxes[i], new[] { "X", "Y", "Z" }[i]);
                g.Children.Add(boxes[i]);
            }
            return g;
        }
        if ((under ?? type) == typeof(Guid) && Choices.TryGetValue(p.Name, out var choices))
        {
            var items = new List<(Guid? Id, string Name)> { (null, Loc.T("(none)")) };
            items.AddRange(choices.Select(c => ((Guid?)c.Id, c.Name)));
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
