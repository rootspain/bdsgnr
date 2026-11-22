using BlastDesigner.Domain;
using BlastDesigner.Domain.Geometry;
using BlastDesigner.Domain.Model;
using BlastDesigner.Engine;
using BlastDesigner.IO;
using Xunit;

namespace BlastDesigner.Tests;

/// <summary>The units are a setting of the program, shared: the tests that change them run alone.</summary>
[CollectionDefinition("Measure", DisableParallelization = true)]
public class MeasureCollection { }

/// <summary>The units are a setting of the program: these tests set them and put them back.</summary>
[Collection("Measure")]
public class MeasureTests
{
    private static T With<T>(UnitSystem system, AngleReference angle, Func<T> body)
    {
        var (s, a) = (Measure.System, Measure.Angle);
        Measure.System = system;
        Measure.Angle = angle;
        try { return body(); }
        finally { (Measure.System, Measure.Angle) = (s, a); }
    }

    [Fact]
    public void Metric_ShowsWhatIsKept_TheCoreLoadInGramsPerMetre()
    {
        With(UnitSystem.Metric, AngleReference.FromVertical, () =>
        {
            foreach (var q in Enum.GetValues<Quantity>().Where(q => q is not (Quantity.CordLoad or Quantity.SmallMass)))
                Assert.Equal(12.5, Measure.Show(q, 12.5), 9);
            Assert.Equal(10, Measure.Show(Quantity.CordLoad, 0.010), 9);
            Assert.Equal(400, Measure.Show(Quantity.SmallMass, 0.4), 9);
            Assert.Equal(("m", "kg/m³", "mm/s"), (Measure.Symbol(Quantity.Length), Measure.Symbol(Quantity.PowderFactor), Measure.Symbol(Quantity.Ppv)));
            return 0;
        });
    }

    [Fact]
    public void Imperial_ConvertsEachQuantity_AndBack()
    {
        With(UnitSystem.Imperial, AngleReference.FromVertical, () =>
        {
            Assert.Equal(32.8084, Measure.Show(Quantity.Length, 10), 4);
            Assert.Equal(4.0157, Measure.Show(Quantity.Diameter, 102), 4);
            Assert.Equal(2.2046, Measure.Show(Quantity.Mass, 1), 4);
            Assert.Equal(14.11, Measure.Show(Quantity.SmallMass, 0.4), 2);
            Assert.Equal(1.6856, Measure.Show(Quantity.PowderFactor, 1), 4);
            Assert.Equal(1.3080, Measure.Show(Quantity.Volume, 1), 4);
            Assert.Equal(10.7639, Measure.Show(Quantity.Area, 1), 4);
            Assert.Equal(0.6720, Measure.Show(Quantity.LinearCharge, 1), 4);
            Assert.Equal(47.04, Measure.Show(Quantity.CordLoad, 0.010), 2);
            Assert.Equal(2.2934, Measure.Show(Quantity.RockDensity, 2.721), 3);
            Assert.Equal(1.2, Measure.Show(Quantity.ExplosiveDensity, 1.2), 9);
            Assert.Equal(1, Measure.Show(Quantity.Ppv, 25.4), 9);
            Assert.Equal(0.1524, Measure.Show(Quantity.DelayPerLength, 0.5), 9);
            Assert.Equal(212, Measure.Show(Quantity.Temperature, 100), 9);
            Assert.Equal(("ft", "lb/yd³", "in/s", "°F"), (Measure.Symbol(Quantity.Length), Measure.Symbol(Quantity.PowderFactor), Measure.Symbol(Quantity.Ppv), Measure.Symbol(Quantity.Temperature)));
            foreach (var q in Enum.GetValues<Quantity>())
                Assert.Equal(7.25, Measure.Store(q, Measure.Show(q, 7.25)), 9);
            return 0;
        });
    }

    [Fact]
    public void TheHoleAngle_FromHorizontal_Is90LessIt_WhateverTheUnits()
    {
        foreach (var system in Enum.GetValues<UnitSystem>())
            With(system, AngleReference.FromHorizontal, () =>
            {
                Assert.Equal(90, Measure.Show(Quantity.HoleAngle, 0), 9);
                Assert.Equal(75, Measure.Show(Quantity.HoleAngle, 15), 9);
                // a hole up from the horizontal
                Assert.Equal(-30, Measure.Show(Quantity.HoleAngle, 120), 9);
                Assert.Equal(15, Measure.Store(Quantity.HoleAngle, 75), 9);
                Assert.Equal("75", Measure.Text(Quantity.HoleAngle, 15, culture: System.Globalization.CultureInfo.InvariantCulture));
                return 0;
            });
        With(UnitSystem.Imperial, AngleReference.FromVertical, () => { Assert.Equal(15, Measure.Show(Quantity.HoleAngle, 15), 9); return 0; });
    }

    [Fact]
    public void Texts_TheirMetricUnits_InTheUnitsOfTheProgram()
    {
        With(UnitSystem.Metric, AngleReference.FromVertical, () =>
        {
            // metric: nothing changes
            Assert.Equal("Burden, m", MeasureText.Caption("Burden, m"));
            Assert.Equal("12.5 m", MeasureText.InText("12.5 m"));
            return 0;
        });
        With(UnitSystem.Imperial, AngleReference.FromVertical, () =>
        {
            // captions: after a comma, in brackets, after a word; Russian too; a unit of no length (ms) stays
            Assert.Equal("Burden, ft", MeasureText.Caption("Burden, m"));
            Assert.Equal("ЛНС, ft", MeasureText.Caption("ЛНС, м"));
            Assert.Equal("Spacing (ft)", MeasureText.Caption("Spacing (m)"));
            Assert.Equal("Volume yd³", MeasureText.Caption("Volume m³"));
            Assert.Equal("Powder factor, lb/yd³", MeasureText.Caption("Powder factor, kg/m³"));
            Assert.Equal("Delay, ms", MeasureText.Caption("Delay, ms"));
            Assert.Equal("Hole", MeasureText.Caption("Hole"));
            // formats: the number before the unit converted, both of a product, others left
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var (f, a) = MeasureText.Formatted("Drilled: {0:0.0} m, {1} holes, {2:0} ms", new object?[] { 10.0, 5, 42.0 });
            Assert.Equal("Drilled: 32.8 ft, 5 holes, 42 ms", string.Format(inv, f, a));
            (f, a) = MeasureText.Formatted("{0:0.00} × {1:0.00} m", new object?[] { 4.0, 5.0 });
            Assert.Equal("13.12 × 16.40 ft", string.Format(inv, f, a));
            (f, _) = MeasureText.Formatted("{0} m", new object?[] { "abc" });
            Assert.Equal("{0} m", f);
            // written texts: the decimals kept, a comma too; kg/t is no length
            Assert.Equal("Stemming 9.8 ft, mass 22.0 lb", MeasureText.InText("Stemming 3.0 m, mass 10.0 kg"));
            Assert.Equal("Забойка 9,8 ft", MeasureText.InText("Забойка 3,0 м"));
            Assert.Equal("0.5 kg/t", MeasureText.InText("0.5 kg/t"));
            Assert.Equal("1.686 lb/yd³", MeasureText.InText("1.000 kg/m³"));
            return 0;
        });
    }

    [Fact]
    public void BlockSizes_AreInches_InTheImperialSystem()
    {
        With(UnitSystem.Imperial, AngleReference.FromVertical, () =>
        {
            Assert.Equal(10, SizeUnits.FromCm(25.4, SizeUnit.Mm), 9);
            Assert.Equal(25.4, SizeUnits.ToCm(10, SizeUnit.Cm), 9);
            Assert.Equal("in", SizeUnits.Symbol(SizeUnit.Mm));
            return 0;
        });
        With(UnitSystem.Metric, AngleReference.FromVertical, () => { Assert.Equal(254, SizeUnits.FromCm(25.4, SizeUnit.Mm), 9); return 0; });
    }

    [Fact]
    public void ATextFile_InFeet_WithADip_IsReadInMetres_FromVertical()
    {
        var lines = new[] { "Hole,X,Y,Z,Dip,Bearing,Length,Diameter", "1,1000,2000,500,-75,90,40,5" };
        var fmt = With(UnitSystem.Imperial, AngleReference.FromVertical, () => TextImportFormat.Guess(lines));
        // a file of a user of feet; a dip column counts from the horizontal
        Assert.Equal((UnitSystem.Imperial, AngleReference.FromHorizontal), (fmt.Units, fmt.Angles));
        Assert.Equal(ImportField.Angle, fmt.Columns[4]);
        var h = Assert.IsType<Hole>(Assert.Single(TextFiles.Import(lines, fmt, new DesignData()).Entities));
        Assert.Equal(304.8, h.Collar.X, 6);
        Assert.Equal(152.4, h.Collar.Z, 6);
        Assert.Equal(15, h.Angle, 9);
        Assert.Equal(12.192, h.Length, 6);
        Assert.Equal(127, h.Diameter, 6);
        // a template from before the fields: metres, from vertical
        var old = new TextImportFormat { Columns = fmt.Columns, SkipLines = 1 };
        var m = Assert.IsType<Hole>(Assert.Single(TextFiles.Import(lines, old, new DesignData()).Entities));
        Assert.Equal((1000.0, 75.0), (m.Collar.X, Math.Abs(m.Angle)));
    }

    [Fact]
    public void TheHoleTable_InFeet_NamesItsUnits_AndADipIsReadBack()
    {
        var p = BlastProject.CreateNew();
        p.Add(new Hole { Label = "1", Collar = new Vec3(304.8, 0, 30.48), Angle = 15, Length = 3.048, Diameter = 127 });
        var csv = With(UnitSystem.Imperial, AngleReference.FromHorizontal, () => TextFiles.ExportHoles(p, p.Holes));
        var rows = csv.Split('\n');
        Assert.StartsWith("Label,CollarX ft,CollarY ft,CollarZ ft,ToeX ft", rows[0]);
        Assert.Contains(",Dip,Bearing,Length ft,Diameter in,", rows[0]);
        Assert.StartsWith("1,1000,0,100,", rows[1]);
        Assert.Contains(",75,0,10,5,", rows[1]);
        // read back as written: the same hole
        var fmt = With(UnitSystem.Imperial, AngleReference.FromHorizontal, () => TextImportFormat.Guess(rows));
        // the columns named with their unit are not guessed by name: mapped as the user would
        fmt.Columns = new() { ImportField.Label, ImportField.CollarX, ImportField.CollarY, ImportField.CollarZ, ImportField.Ignore, ImportField.Ignore, ImportField.Ignore, ImportField.Angle, ImportField.Bearing, ImportField.Length, ImportField.Diameter };
        var back = Assert.IsType<Hole>(Assert.Single(TextFiles.Import(rows.Where(r => r.Length > 0).ToList(), fmt, new DesignData()).Entities));
        Assert.Equal((304.8, 15.0, 127.0), (Math.Round(back.Collar.X, 6), Math.Round(back.Angle, 6), Math.Round(back.Diameter, 6)));
        // metric and from vertical: as before
        var metric = With(UnitSystem.Metric, AngleReference.FromVertical, () => TextFiles.ExportHoles(p, p.Holes));
        Assert.StartsWith("Label,CollarX,CollarY,CollarZ,ToeX,ToeY,ToeZ,Angle,Bearing,Length,Diameter,Subdrill,", metric);
    }

    [Fact]
    public void ACalculatedValue_ShowsInTheUnitsOfTheProgram()
    {
        With(UnitSystem.Imperial, AngleReference.FromVertical, () =>
        {
            Assert.Equal((1.308, "yd³"), (Math.Round(CalcService.InUnits(1, "m3").Value, 3), CalcService.InUnits(1, "m3").Unit));
            Assert.Equal((32.808, "ft"), (Math.Round(CalcService.InUnits(10, "m").Value, 3), CalcService.InUnits(10, "m").Unit));
            // cm is a length too: in inches through mm
            Assert.Equal((3.937, "in"), (Math.Round(CalcService.InUnits(10, "cm").Value, 3), CalcService.InUnits(10, "cm").Unit));
            Assert.Equal((1.686, "lb/yd³"), (Math.Round(CalcService.InUnits(1, "kg/m3").Value, 3), CalcService.InUnits(1, "kg/m3").Unit));
            // energy has no imperial unit here; a number no unit
            Assert.Equal((5.0, "MJ"), CalcService.InUnits(5, "MJ"));
            Assert.Equal((5.0, "1"), CalcService.InUnits(5, "1"));
            return 0;
        });
        With(UnitSystem.Metric, AngleReference.FromVertical, () => { Assert.Equal((10.0, "m"), CalcService.InUnits(10, "m")); return 0; });
    }

    [Fact]
    public void ADrawingInFeet_IsTakenToMetres()
    {
        var items = new List<(string, Entity)>
        {
            ("0", new PolylineEntity { Points = { new Vec3(10, 20, 30), new Vec3(20, 20, 30) } }),
            ("0", new CircleEntity { Center = new Vec3(10, 0, 0), Radius = 10 }),
            ("0", new TextEntity { Position = new Vec3(0, 10, 0), Height = 1 }),
        };
        CadFiles.ToMetres(items, 0.3048);
        Assert.Equal(3.048, ((PolylineEntity)items[0].Item2).Points[0].X, 9);
        Assert.Equal(9.144, ((PolylineEntity)items[0].Item2).Points[1].Z, 9);
        Assert.Equal(3.048, ((CircleEntity)items[1].Item2).Center.X, 9);
        Assert.Equal(3.048, ((CircleEntity)items[1].Item2).Radius, 9);
        Assert.Equal(0.3048, ((TextEntity)items[2].Item2).Height, 9);
    }
}
