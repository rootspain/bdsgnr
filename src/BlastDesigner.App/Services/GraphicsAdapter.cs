using Avalonia.Platform;
using BlastDesigner.IO;

namespace BlastDesigner.App.Services;

/// <summary>
/// Keeps the program on the video card it started on. The drawing device is made again when Windows takes it away, as when
/// a laptop with two video cards hands the screen to the other card on going full screen; the first card in the list is
/// then that other one, and the program would move over to it, with the screen flickering on the way. The card is told
/// by its identifier (LUID), which stays while the computer runs.
/// </summary>
public static class GraphicsAdapter
{
    private static byte[]? _kept;

    /// <summary>For <c>Win32PlatformOptions.GraphicsAdapterSelectionCallback</c>: the index of the card to draw on.</summary>
    public static int Pick(IReadOnlyList<PlatformGraphicsDeviceAdapterDescription> adapters)
    {
        var i = Choose(adapters.Select(a => a.DeviceLuid).ToList(), _kept);
        if (i >= 0 && i < adapters.Count)
        {
            var moved = _kept != null && !Same(_kept, adapters[i].DeviceLuid);
            _kept ??= adapters[i].DeviceLuid;
            Log.Info($"Video card: {adapters[i].Description}{(moved ? " (the card the program started on is gone)" : "")}; of {string.Join(", ", adapters.Select(a => a.Description))}");
        }
        return i;
    }

    /// <summary>
    /// The card to draw on among <paramref name="luids"/>: the one <paramref name="kept"/> names while it is there, otherwise
    /// the first, which is the one Windows puts first.
    /// </summary>
    public static int Choose(IReadOnlyList<byte[]?> luids, byte[]? kept)
    {
        if (luids.Count == 0) return 0;
        if (kept == null) return 0;
        for (int i = 0; i < luids.Count; i++)
            if (luids[i] is { } l && Same(l, kept)) return i;
        return 0;
    }

    private static bool Same(byte[]? a, byte[]? b) => a != null && b != null && a.AsSpan().SequenceEqual(b);
}
