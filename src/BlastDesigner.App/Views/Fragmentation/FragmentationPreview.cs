using Avalonia.Controls;
using Avalonia.Media;
using BlastDesigner.App.Services;
using BlastDesigner.Domain.Model;
using BlastDesigner.Engine;

namespace BlastDesigner.App.Views;

/// <summary>The prediction of a blast with fragmentation inputs: X50, X80, oversize and fines in four columns (sizes in the unit of the inputs).</summary>
public sealed class FragmentationPreview : Grid
{
    public FragmentationPreview()
    {
        ColumnDefinitions = new ColumnDefinitions("*,*,*,*");
        ColumnSpacing = 10;
    }

    /// <summary>X50, n, X80 and oversize (the target window) in place of X50, X80, oversize and fines.</summary>
    public bool WithUniformity { get; set; }

    /// <summary>The last prediction shown; null before the first, or when the holes are not loaded.</summary>
    public Fragmentation.Result? Result { get; private set; }

    /// <summary>The prediction of the blast with <paramref name="draft"/> in place of its inputs (the project is left as it is).</summary>
    public void Show(BlastProject project, FragmentationModel draft)
    {
        Children.Clear();
        var saved = project.Fragmentation;
        Fragmentation.Result r;
        try { project.Fragmentation = draft; r = Fragmentation.Compute(project); }
        finally { project.Fragmentation = saved; }
        Show(r, SizeUnits.Of(draft));
    }

    /// <summary>A prediction worked out by the caller.</summary>
    public void Show(Fragmentation.Result r, SizeUnit unit)
    {
        Children.Clear();
        Result = r.X50Cm > 0 ? r : null;
        if (r.X50Cm <= 0)
        {
            Children.Add(new TextBlock { Text = FragmentationUi.L("Load the holes to see the prediction: it needs the powder factor and the charge per hole."), Foreground = Look.Muted, TextWrapping = TextWrapping.Wrap, [Grid.ColumnSpanProperty] = 4 });
            return;
        }
        string Size(double cm) => Loc.F(FragmentationUi.L("{0} " + SizeUnits.Symbol(unit)), FragmentationUi.N(SizeUnits.FromCm(cm, unit), SizeUnits.Format(unit)));
        var cells = WithUniformity
            ? new (string Label, string Value, bool Warn)[]
            {
                ("X50", Size(r.X50Cm), false),
                ("n", FragmentationUi.N(r.Uniformity, "0.00"), r.Uniformity < 1),
                ("X80", Size(r.X80Cm), false),
                (FragmentationUi.L("Oversize"), FragmentationUi.N(r.OversizePct, "0.0") + " %", r.OversizePct > 10)
            }
            : new (string Label, string Value, bool Warn)[]
            {
                ("X50", Size(r.X50Cm), false),
                ("X80", Size(r.X80Cm), false),
                (FragmentationUi.L("Oversize"), FragmentationUi.N(r.OversizePct, "0.0") + " %", r.OversizePct > 10),
                (FragmentationUi.L("Fines"), FragmentationUi.N(r.FinesPct, "0.0") + " %", false)
            };
        for (int i = 0; i < cells.Length; i++)
        {
            var cell = new StackPanel
            {
                Spacing = 2,
                Children =
                {
                    new TextBlock { Text = cells[i].Label, FontSize = 11.5, Foreground = Look.Faint },
                    new TextBlock { Text = cells[i].Value, FontSize = 18, FontWeight = FontWeight.SemiBold, FontFamily = Look.Mono, Foreground = cells[i].Warn ? Look.Accent : Look.Text, Name = "preview" + i }
                }
            };
            SetColumn(cell, i);
            Children.Add(cell);
        }
    }
}
