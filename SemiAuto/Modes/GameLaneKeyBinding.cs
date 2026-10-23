namespace Nte.GuitarChart.Core;

/// <summary>A keyboard key identified by both virtual-key and hardware scan code.</summary>
public sealed class PhysicalKeyBinding
{
    public int VirtualKey { get; set; }
    public int ScanCode { get; set; }

    public PhysicalKeyBinding() { }

    public PhysicalKeyBinding(int virtualKey, int scanCode)
    {
        VirtualKey = virtualKey;
        ScanCode = scanCode;
    }
}

/// <summary>Defaults, validation and physical-key matching for the six playable lanes.</summary>
public static class GameLaneKeyBinding
{
    private static readonly PhysicalKeyBinding[] Defaults =
    [
        new(0x31, 0x02), new(0x32, 0x03), new(0x33, 0x04),
        new(0x34, 0x05), new(0x35, 0x06), new(0x36, 0x07)
    ];

    public static IReadOnlyList<PhysicalKeyBinding> CreateDefaultGameLanes() =>
        Defaults.Select(key => new PhysicalKeyBinding(key.VirtualKey, key.ScanCode)).ToArray();

    public static bool NormalizeConfig(MidiAutoPlayer.Models.AppConfig config)
    {
        if (config.SemiAutoGameLaneKeyBindings is null || config.SemiAutoGameLaneKeyBindings.Count != 6
            || config.SemiAutoGameLaneKeyBindings.Any(key => key is null))
        {
            config.SemiAutoGameLaneKeyBindings = CreateDefaultGameLanes().ToList();
            config.SemiAutoPositionSwitchVk = 0x4A;
            config.SemiAutoPositionSwitchScanCode = 0x24;
            return true;
        }

        var allKeys = config.SemiAutoGameLaneKeyBindings
            .Append(new PhysicalKeyBinding(config.SemiAutoPositionSwitchVk, config.SemiAutoPositionSwitchScanCode))
            .ToArray();
        bool gameKeysValid = config.SemiAutoGameLaneKeyBindings.All(k => IsAllowedPhysicalKey(k.VirtualKey, k.ScanCode, config.GlobalHotkeyVk));
        bool positionKeyValid = PositionSwitchKeyBinding.IsAllowedPhysicalKey(config.SemiAutoPositionSwitchVk, config.SemiAutoPositionSwitchScanCode)
            && config.SemiAutoPositionSwitchVk != config.GlobalHotkeyVk;
        if (!gameKeysValid || !positionKeyValid || !AreUnique(allKeys))
        {
            config.SemiAutoGameLaneKeyBindings = CreateDefaultGameLanes().ToList();
            config.SemiAutoPositionSwitchVk = 0x4A;
            config.SemiAutoPositionSwitchScanCode = 0x24;
            return true;
        }
        return false;
    }

    public static bool IsAllowedPhysicalKey(int virtualKey, int scanCode, int globalHotkeyVk = 0x77)
    {
        if (virtualKey is < 0x08 or > 0xFE || virtualKey == globalHotkeyVk) return false;
        if (virtualKey is 0x1B or 0x5B or 0x5C or 0x10 or 0x11 or 0x12 or >= 0xA0 and <= 0xA5) return false;
        if (virtualKey is 0x52 or 0x54 or 0x77 or 0x78 or 0x7B) return false;
        return true;
    }

    public static bool AreUnique(IReadOnlyList<PhysicalKeyBinding> bindings)
    {
        for (int i = 0; i < bindings.Count; i++)
        for (int j = i + 1; j < bindings.Count; j++)
        {
            var a = bindings[i];
            var b = bindings[j];
            if (a.ScanCode > 0 && b.ScanCode > 0)
            {
                if (a.ScanCode == b.ScanCode) return false;
            }
            else if (a.VirtualKey == b.VirtualKey)
            {
                return false;
            }
        }
        return true;
    }

    public static int MapPhysicalKeyToGameLane(int virtualKey, int scanCode, IReadOnlyList<PhysicalKeyBinding> bindings)
    {
        for (int i = 0; i < Math.Min(6, bindings.Count); i++)
        {
            var binding = bindings[i];
            if (scanCode > 0 && binding.ScanCode > 0 && scanCode == binding.ScanCode) return i + 1;
            if ((scanCode == 0 || binding.ScanCode == 0) && virtualKey != 0 && virtualKey == binding.VirtualKey) return i + 1;
        }
        return 0;
    }

    public static string FormatLabel(int virtualKey, int scanCode = 0)
    {
        return scanCode switch
        {
            0x4F => "Num 1", 0x50 => "Num 2", 0x51 => "Num 3",
            0x4B => "Num 4", 0x4C => "Num 5", 0x4D => "Num 6",
            _ when virtualKey is >= 0x30 and <= 0x39 => ((char)virtualKey).ToString(),
            _ when virtualKey is >= 0x41 and <= 0x5A => ((char)virtualKey).ToString(),
            _ when virtualKey is >= 0x70 and <= 0x87 => $"F{virtualKey - 0x6F}",
            0x01 => "鼠标左键", 0x02 => "鼠标右键", 0x04 => "鼠标中键",
            _ => $"VK 0x{virtualKey:X2}"
        };
    }
}
