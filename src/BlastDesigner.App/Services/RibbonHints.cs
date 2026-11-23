namespace BlastDesigner.App.Services;

/// <summary>
/// What each ribbon button does, for its tooltip (doc/ribbon-tooltips.md): the description, then, muted, what it works on.
/// The key is the label of the button; the texts are plain keys of ru.tsv (Loc.T).
/// </summary>
public static class RibbonHints
{
    // the scopes many buttons share
    public const string Ask = "With holes selected, asks: only those or all.";
    public const string SelectedOrAll = "The selected holes, or all of them when none is selected.";
    public const string Selected = "Works on the selected holes.";
    public const string Tool = "A tool: works by clicks on the drawing; Esc ends it.";

    private static readonly Dictionary<string, (string What, string? Scope)> Hints = new()
    {
        // File
        ["New"] = ("Starts a new blast project.", null),
        ["Open"] = ("Opens a saved project.", null),
        ["Save"] = ("Saves the project to its file.", null),
        ["Recent"] = ("The projects opened lately; the tip of each shows its folder and size.", null),
        ["Save as…"] = ("Saves the project to a new file; the work then goes on in that file.", null),
        ["Close"] = ("Closes the open project.", null),
        ["Save as template"] = ("Keeps this project as a template to start new projects from.", null),
        ["Manage templates"] = ("The list of saved templates.", null),
        ["Import"] = ("Brings in data of other formats: text, DXF/DWG, Surpac STR, drill plan XML, OBJ and others.", null),
        ["Merge with file…"] = ("Adds the objects of another project file to this one.", null),
        ["Copy image"] = ("Puts a picture of the current view on the clipboard.", null),
        ["Blast properties"] = ("Name, site, date and the other data of the blast.", null),
        ["Design data"] = ("The design values new holes and patterns take: burden, spacing, diameter, bench height and others.", null),
        ["Site coordinate system"] = ("The coordinate system of the site and how the project coordinates relate to it.", null),

        // Topography
        ["Laser wizard"] = ("Step by step entry of a face survey from a laser.", null),
        ["Survey points"] = ("Puts survey points.", Tool),
        ["Survey stations"] = ("The survey stations of the project.", null),
        ["Bore track"] = ("Draws the actual track of a hole by its points.", Tool),
        ["Grade pegs"] = ("Puts grade pegs: marks of the design floor level.", Tool),
        ["Monitoring points"] = ("Puts the points where vibration and airblast are measured.", Tool),
        ["MWD data"] = ("Imports a file of drilling data (MWD) and joins it to the holes.", null),
        ["Point cloud (LAS)"] = ("Imports a LIDAR point cloud from LAS files.", null),
        ["Build"] = ("The surface commands: build from the selected objects, contours, intersections, simplify and others.", null),
        ["Surface tool"] = ("Click points; a surface is triangulated through them.", Tool),
        ["Clip to polygon"] = ("Cuts the selected surface to the selected polygon.", null),
        ["Contours"] = ("Draws the contour lines of a surface at a set interval.", null),
        ["Recognize (AI)"] = ("Finds the crests, toes and other bench lines in the survey.", null),
        ["Convert"] = ("Turns the selected objects into others: points, polylines, polygons, dummy holes and back.", null),
        ["Move / copy"] = ("Moves or copies the selected objects by a set offset.", null),
        ["Transform coordinates"] = ("Shifts, turns and scales the selected objects, e.g. from one coordinate system to another.", null),
        ["Expand polygon"] = ("Grows or shrinks the selected polygon by a set distance.", null),
        ["Clip lines"] = ("Cuts the selected polylines: only their parts inside the polygon stay.", null),
        ["Offset"] = ("Draws a polyline parallel to the selected one at a set distance.", null),
        ["Midpoints"] = ("Puts a point in the middle of each selected segment.", null),
        ["Centre of mass"] = ("Puts a point at the centre of mass of each polygon.", null),
        ["Extend to intersection"] = ("Lengthens a line up to where it meets another.", null),
        ["Clearance zone"] = ("Marks a clearance zone round objects at a set distance.", null),

        // Layout
        ["Hole"] = ("Puts holes one by one.", Tool),
        ["Row"] = ("Draws a row: the holes go along the line at the design spacing.", Tool),
        ["Pattern"] = ("Draws a pattern: rows at the design burden and spacing.", Tool),
        ["Baseline"] = ("Draws the baseline the patterns are laid out from.", Tool),
        ["Pattern from polygon"] = ("Fills a polygon with a pattern at the design burden and spacing.", Tool),
        ["Curved layout"] = ("Rows along the selected curved polyline, at a set burden and spacing.", null),
        ["At points"] = ("A hole at each selected point.", null),
        ["Along line"] = ("Holes along the selected polyline at a set spacing.", null),
        ["Between lines"] = ("Rows of holes between two selected polylines.", null),
        ["From polylines"] = ("A hole from each selected polyline: the collar at its upper end, the toe at the other.", null),
        ["To survey points"] = ("Moves the selected holes to the nearest selected points, within a set distance.", null),
        ["Delete outside polygon"] = ("Deletes the holes outside the selected polygon.", null),
        ["Even out spacing"] = ("Evens out the spacing in the rows: the first and the last hole of a row stay, the holes between them are moved to equal steps on the line between those two; the collar heights stay.",
            "The rows of the selected holes, or every row when nothing is selected."),
        ["Rows automatically"] = ("Builds the rows from the holes by the blast direction; the old rows are replaced.", null),
        ["Row from holes"] = ("Joins the selected holes into a row (two at least).", null),
        ["Detach from row"] = ("Takes the selected holes out of their rows.", null),
        ["Front row profile"] = ("The burden of the front row to the face along its length.", null),
        ["Batter"] = ("Builds the toe line from the selected crest line by the face angle.", null),
        ["Section"] = ("A vertical section along a drawn line: holes, charges and surfaces.", null),
        ["Renumber"] = ("Gives new labels in order: a prefix and the first number.", SelectedOrAll),
        ["Duplicate labels"] = ("Gives new labels to the holes that share one.", null),
        ["Duplicate coordinates"] = ("Finds the holes standing at one place, within a tolerance; can delete the second of each pair.", null),
        ["Holes → dummy"] = ("The selected holes become dummy holes: not drilled, points for ties only.", null),
        ["Dummy → holes"] = ("The selected dummy holes become real holes.", null),
        ["Markout"] = ("The markout table: coordinates to set out the holes on the bench.", null),
        ["Distance at depth"] = ("Click two holes: the distance between them, also at depth.", Tool),
        ["Reset design tracks"] = ("Removes the bore tracks: the holes go straight from the collar to the toe again.", Ask),
        ["Collars onto surface"] = ("Puts the collars on a chosen surface; the toes or the lengths stay, as you choose.", SelectedOrAll),
        ["Lengths to grade"] = ("Sets the hole lengths so that the toes reach a chosen grade surface.", SelectedOrAll),

        // Loading
        ["Load"] = ("Charges holes by clicks.", Tool),
        ["By rules"] = ("Charges the holes by the loading rules; asks before overwriting the holes loaded by hand.", Ask),
        ["Initiation only"] = ("Applies the initiation rules only: the detonators and boosters; the charges stay.", null),
        ["Empty"] = ("Removes the charges from the holes.", Ask),
        ["Release for loading"] = ("Marks the holes as released for loading.", Selected),
        ["Loading rules"] = ("The rules that choose the charge column of a hole by its type, length, water and others.", null),
        ["Initiation rules"] = ("The rules that choose the detonators and boosters of the holes.", null),
        ["Products"] = ("Explosives, detonators, connectors and the other products of the blast.", null),
        ["Hole and material types"] = ("The hole types and the material types of the rock.", null),
        ["Edit column"] = ("Edits the charge column of a hole; with several selected, the same column is fitted to the length of each.", null),
        ["In-hole detonators"] = ("Sets the in-hole delays of holes by clicks.", Tool),
        ["Join decks"] = ("Joins neighbouring decks of the same product into one.", Ask),
        ["Loading factor"] = ("Sets a factor of the explosive mass (1 = as designed).", Ask),
        ["Strata intercepts"] = ("Where the holes cross the strata surfaces: a table of depths and points.", SelectedOrAll),
        ["Material from strata"] = ("Sets the material type of each hole by the stratum at its toe.", SelectedOrAll),
        ["Clear actuals"] = ("Clears the dipped lengths, as-drilled collars, bore tracks and MWD logs.", SelectedOrAll),
        ["Mark drilled"] = ("Marks the holes as drilled.", Selected),
        ["Design tolerances"] = ("How far the actual holes may be from the design.", null),
        ["Tolerance check"] = ("The holes whose actual position or length is out of tolerance.", null),

        // Initiation
        ["Ties"] = ("Connects holes with surface delays: click the hole from, then the hole to; dragging works too.", Tool),
        ["Initiation point"] = ("Puts the point where the blast is initiated.", Tool),
        ["Dummy hole"] = ("Puts dummy holes: points for ties that are not drilled.", Tool),
        ["Initiation catalogue"] = ("The palette of connectors and delays to tie with.", null),
        ["Automatic tie-up"] = ("Plans the tie-up with surface connectors from the initiation point: variants with their cost and checks to choose from.", SelectedOrAll),
        ["Connect in order"] = ("Ties the selected holes into one chain, each to its nearest neighbour.", null),
        ["Flip ties"] = ("Turns the direction of the selected ties.", null),
        ["Delete ties"] = ("Deletes the ties of the selected holes, or the whole tie-up.", null),
        ["Time by click"] = ("Sets the time of one hole at a time (electronic detonators).", Tool),
        ["Along row"] = ("The clicked holes take increasing times at a set step.", Tool),
        ["Timing line"] = ("Draws the front of the blast; the hole times follow it.", SelectedOrAll),
        ["Opening point"] = ("Puts the point the electronic blast opens from; the times spread from it.", SelectedOrAll),
        ["Shift time…"] = ("Adds a time to the holes or takes it away.", null),
        ["Scale intervals…"] = ("Changes the intervals between the hole times.", null),
        ["Constant step…"] = ("Sets a constant time step between the selected holes.", null),
        ["Between horizons"] = ("Copies the hole times from one horizon of the blast to another.", null),
        ["Re-apply"] = ("Calculates the hole times anew from the ties and settings.", null),
        ["Deprogram"] = ("Clears the programmed times of the electronic detonators.", Ask),
        ["Pyrotechnic → electronic"] = ("Replaces the pyrotechnic detonators with the electronic one of the products.", SelectedOrAll),
        ["Clear exceptions"] = ("Clears the logging exceptions of the holes.", Ask),
        ["Electronic settings"] = ("The settings of electronic timing.", null),
        ["Timing overlap"] = ("The holes firing too close in time.", null),
        ["Logging report"] = ("The report of the logged electronic detonators.", null),

        // Analysis
        ["Visualization"] = ("Plays the firing of the blast in time.", null),
        ["Dynamics analysis"] = ("The direction and relief of the blast: how the rock moves by the timing.", null),
        ["Time envelope"] = ("The charge firing in each time window.", null),
        ["Charge vs time"] = ("The charge per delay along the time of the blast.", null),
        ["Clear analysis"] = ("Removes the analysis colours and marks from the drawing.", null),
        ["Bullseye"] = ("One hole in plan: its deviation from the design.", null),
        ["Exceptions"] = ("The holes with logging exceptions.", null),
        ["Front row"] = ("The front row against the face: the burden hole by hole.", null),
        ["Row check"] = ("Spacing and lengths in each row: the mean, the least and the most.", null),
        ["Burden check"] = ("The burden between the rows across the pattern.", null),
        ["Horizontal slice"] = ("A plan cut at a set level through the holes and charges.", null),
        ["Fly through"] = ("Flies the camera through the blast in 3D.", null),
        ["Quantities"] = ("Holes, metres drilled, explosive, volume and powder factor.", null),
        ["Dashboard"] = ("The main figures of the blast on one page.", null),
        ["Volume between surfaces"] = ("The volume between two surfaces within a polygon.", null),
        ["Volume overview"] = ("The volumes of the blast at a glance.", null),
        ["Costs"] = ("The cost of the blast by the prices of the products.", null),
        ["Prediction"] = ("The predicted size of the blasted rock (Kuz-Ram).", null),
        ["Inputs"] = ("The rock data of the fragmentation prediction.", null),
        ["Fragmentation target"] = ("Finds the pattern and charge that give the size wanted; the project changes only on Apply.", null),
        ["Exclusion zone"] = ("The danger zone of the blast by flyrock.", null),
        ["Vibration"] = ("The predicted vibration at the monitoring points.", null),
        ["Vibration map"] = ("A map of the predicted vibration round the blast.", null),
        ["Airblast map"] = ("A map of the predicted airblast round the blast.", null),
        ["Flyrock"] = ("The predicted flyrock distance.", null),
        ["Scale law"] = ("The site law of vibration by distance and charge.", null),
        ["Vibration records"] = ("Analysis of measured vibration records.", null),

        // Output
        ["Reports"] = ("Prints or saves the reports of the blast.", null),
        ["Print plan"] = ("Prints the plan of the blast to scale.", null),
        ["Report designer"] = ("Sets up the look and content of the reports.", null),
        ["IREDES"] = ("The drill plan in IREDES for drill rigs.", null),
        ["Drill plan (XML)"] = ("The drill plan as an XML file.", null),
        ["Surpac STR"] = ("The holes and lines as a Surpac string file.", null),
        ["Load sheets (CSV)"] = ("The charge sheets of the holes for the loading crew.", null),
        ["Deck table (CSV)"] = ("The table of all charges, deck by deck.", null),
        ["Firing data"] = ("The hole times and charges for vibration monitoring.", null),
        ["Export"] = ("Saves the project data in other formats.", null),
        ["DXF"] = ("The drawing as a DXF file for CAD.", null)
    };

    /// <summary>The description and the scope of a button, untranslated; null for a button without one.</summary>
    public static (string What, string? Scope)? For(string label) => Hints.TryGetValue(label, out var h) ? h : null;

    /// <summary>Every label described: the smoke test checks they are all buttons of the ribbon, and translated.</summary>
    public static IEnumerable<string> Labels => Hints.Keys;
    public static IEnumerable<string> Texts => Hints.Values.SelectMany(h => h.Scope == null ? new[] { h.What } : new[] { h.What, h.Scope }).Distinct();
}
