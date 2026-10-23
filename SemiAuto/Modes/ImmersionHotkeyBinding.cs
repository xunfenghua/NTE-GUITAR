using Nte.GuitarChart.Core;

namespace MidiAutoPlayer.SemiAuto.Modes;

/// <summary>Validation and labels for the single-key global immersion toggle.</summary>
public static class ImmersionHotkeyBinding
{
    public const int DefaultVirtualKey = 0x49; // I

    public static bool IsSupportedVirtualKey(int virtualKey) =>
        virtualKey is >= 0x30 and <= 0x39
        or >= 0x41 and <= 0x5A
        or >= 0x70 and <= 0x87
        or 0xBA or 0xBB or 0xBC or 0xBD or 0xBE or 0xBF or 0xC0
        or 0xDB or 0xDC or 0xDD or 0xDE or 0xE2
        or >= 0x6A and <= 0x6F;

    public static bool IsAllowed(int virtualKey, int globalHotkeyVk, IReadOnlyList<PhysicalKeyBinding> laneKeys,
        PhysicalKeyBinding positionKey)
    {
        if (!IsSupportedVirtualKey(virtualKey)) return false;
        if (virtualKey is >= 0x31 and <= 0x36 or 0x4A) return false; // Default six lanes and position-switch key
        if (virtualKey is 0x52 or 0x54 or 0x77 or 0x78 or 0x7B) return false; // R/T, F8, F9, F12
        if (virtualKey == globalHotkeyVk || virtualKey == positionKey.VirtualKey) return false;
        return laneKeys.All(key => key.VirtualKey != virtualKey);
    }

    public static string FormatLabel(int virtualKey) => virtualKey switch
    {
        >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A => ((char)virtualKey).ToString(),
        >= 0x70 and <= 0x87 => $"F{virtualKey - 0x6F}",
        0xBA => ";",
        0xBB => "=",
        0xBC => ",",
        0xBD => "-",
        0xBE => ".",
        0xBF => "/",
        0xC0 => "`",
        0xDB => "[",
        0xDC => "\\",
        0xDD => "]",
        0xDE => "'",
        0xE2 => "\\ (102)",
        0x6A => "Num *",
        0x6B => "Num +",
        0x6C => "Num ,",
        0x6D => "Num -",
        0x6E => "Num .",
        0x6F => "Num /",
        _ => $"VK 0x{virtualKey:X2}"
    };
}
