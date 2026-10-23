namespace Nte.GuitarChart.Core;

/// <summary>Validation rules for the global position-switch virtual key binding.</summary>
public static class PositionSwitchKeyBinding
{
    public const int DefaultVirtualKey = 0x4A; // J
    private static readonly HashSet<int> GameNumpadScanCodes = [0x4F, 0x50, 0x51, 0x4B, 0x4C, 0x4D];

    public static bool IsAllowedVirtualKey(int virtualKey) =>
        virtualKey is >= 0x08 and <= 0xFE &&
        virtualKey is not (0x1B or 0x5B or 0x5C) &&
        virtualKey is not (0x10 or 0x11 or 0x12 or >= 0xA0 and <= 0xA5) &&
        virtualKey is not (>= 0x31 and <= 0x36) &&
        virtualKey is not (>= 0x61 and <= 0x66) &&
        virtualKey is not (0x52 or 0x54 or 0x77 or 0x78 or 0x7B);

    public static bool IsAllowedPhysicalKey(int virtualKey, int scanCode) =>
        IsAllowedVirtualKey(virtualKey) && !GameNumpadScanCodes.Contains(scanCode);
}
