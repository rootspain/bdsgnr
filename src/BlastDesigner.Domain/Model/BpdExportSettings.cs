namespace BlastDesigner.Domain.Model;

/// <summary>
/// The names of the blast in a Davey Tronic plan (BPD), kept with the project for its next export: FULL_NAME and
/// DISPLAYED_NAME as last written. Null in a project not exported yet (and in files from before): the export window takes
/// the number or the name of the blast then.
/// </summary>
public sealed class BpdExportSettings
{
    public string FullName { get; set; } = string.Empty;
    public string DisplayedName { get; set; } = string.Empty;
}
