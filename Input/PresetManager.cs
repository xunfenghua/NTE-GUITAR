using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using MidiAutoPlayer.Library;
using MidiAutoPlayer.Models;

namespace MidiAutoPlayer.Input;

public static class PresetManager
{
    public static string PresetsDirectory
    {
        get
        {
            string newPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "NTE-GUITAR", "presets");
            string oldPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MidiAutoPlayer", "presets");

            if (!Directory.Exists(newPath) && Directory.Exists(oldPath))
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(newPath)!);
                    Directory.Move(oldPath, newPath);
                }
                catch { }
            }
            return newPath;
        }
    }

    public const string DefaultPresetName = "默认36键";

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern int GetKeyNameText(int lParam, StringBuilder lpString, int nSize);

    [DllImport("user32.dll")]
    public static extern uint MapVirtualKey(uint uCode, uint uMapType);

    public static readonly (string Note, byte Midi, string Key, ushort ScanCode, bool Ext)[] DefaultMappingData =
    [
        ("E3", 52, "A", 0x1E, false),
        ("F3", 53, "S", 0x1F, false),
        ("F#3", 54, "D", 0x20, false),
        ("G3", 55, "F", 0x21, false),
        ("G#3", 56, "G", 0x22, false),
        ("A3", 57, "H", 0x23, false),
        ("A#3", 58, "J", 0x24, false),
        ("B3", 59, "K", 0x25, false),
        ("C4", 60, "L", 0x26, false),
        ("C#4", 61, ";", 0x27, false),
        ("D4", 62, "'", 0x28, false),
        ("D#4", 63, "Enter", 0x1C, false),
        ("E4", 64, "Q", 0x10, false),
        ("F4", 65, "W", 0x11, false),
        ("F#4", 66, "E", 0x12, false),
        ("G4", 67, "R", 0x13, false),
        ("G#4", 68, "T", 0x14, false),
        ("A4", 69, "Y", 0x15, false),
        ("A#4", 70, "U", 0x16, false),
        ("B4", 71, "I", 0x17, false),
        ("C5", 72, "O", 0x18, false),
        ("C#5", 73, "P", 0x19, false),
        ("D5", 74, "[", 0x1A, false),
        ("D#5", 75, "]", 0x1B, false),
        ("E5", 76, "1", 0x02, false),
        ("F5", 77, "2", 0x03, false),
        ("F#5", 78, "3", 0x04, false),
        ("G5", 79, "4", 0x05, false),
        ("G#5", 80, "5", 0x06, false),
        ("A5", 81, "6", 0x07, false),
        ("A#5", 82, "7", 0x08, false),
        ("B5", 83, "8", 0x09, false),
        ("C6", 84, "9", 0x0A, false),
        ("C#6", 85, "0", 0x0B, false),
        ("D6", 86, "-", 0x0C, false),
        ("D#6", 87, "=", 0x0D, false),
    ];

    public static KeyPreset CreateDefaultPreset()
    {
        var preset = new KeyPreset
        {
            Name = DefaultPresetName,
            Version = 1,
            Keys = new List<KeyMapping>(36)
        };

        for (byte i = 0; i < DefaultMappingData.Length; i++)
        {
            var item = DefaultMappingData[i];
            preset.Keys.Add(new KeyMapping
            {
                NoteIndex = i,
                NoteName = item.Note,
                MidiNumber = item.Midi,
                KeyName = item.Key,
                ScanCode = item.ScanCode,
                Extended = item.Ext
            });
        }

        return preset;
    }

    public static List<string> GetAvailablePresets()
    {
        Directory.CreateDirectory(PresetsDirectory);
        var list = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { DefaultPresetName };

        foreach (var file in Directory.GetFiles(PresetsDirectory, "*.json"))
        {
            string name = Path.GetFileNameWithoutExtension(file);
            list.Add(name);
        }

        return list.OrderBy(n => n == DefaultPresetName ? " " : n).ToList();
    }

    public static KeyPreset LoadPreset(string name)
    {
        Directory.CreateDirectory(PresetsDirectory);
        if (string.IsNullOrWhiteSpace(name) || name == DefaultPresetName)
        {
            string defaultFile = Path.Combine(PresetsDirectory, "default.json");
            if (File.Exists(defaultFile))
            {
                try
                {
                    string json = File.ReadAllText(defaultFile);
                    var p = JsonSerializer.Deserialize<KeyPreset>(json, SongSerializer.JsonOptions);
                    if (p != null && p.Keys.Count == 36)
                    {
                        p.Name = DefaultPresetName;
                        return p;
                    }
                }
                catch { }
            }
            return CreateDefaultPreset();
        }

        string customFile = Path.Combine(PresetsDirectory, $"{name}.json");
        if (File.Exists(customFile))
        {
            try
            {
                string json = File.ReadAllText(customFile);
                var p = JsonSerializer.Deserialize<KeyPreset>(json, SongSerializer.JsonOptions);
                if (p != null && p.Keys.Count == 36)
                {
                    p.Name = name;
                    return p;
                }
            }
            catch { }
        }

        return CreateDefaultPreset();
    }

    public static void SavePreset(KeyPreset preset)
    {
        Directory.CreateDirectory(PresetsDirectory);
        string filename = preset.Name == DefaultPresetName ? "default.json" : $"{preset.Name}.json";
        string targetPath = Path.Combine(PresetsDirectory, filename);
        string tmpPath = targetPath + ".tmp";

        string json = JsonSerializer.Serialize(preset, SongSerializer.JsonOptions);
        File.WriteAllText(tmpPath, json);
        File.Move(tmpPath, targetPath, overwrite: true);
    }

    public static string GetKeyDisplay(ushort scanCode, bool extended = false)
    {
        // Check standard well-known scan codes for accurate symbol display
        switch (scanCode)
        {
            case 0x1E: return "A";
            case 0x1F: return "S";
            case 0x20: return "D";
            case 0x21: return "F";
            case 0x22: return "G";
            case 0x23: return "H";
            case 0x24: return "J";
            case 0x25: return "K";
            case 0x26: return "L";
            case 0x27: return ";";
            case 0x28: return "'";
            case 0x1C: return extended ? "NumEnter" : "Enter";
            case 0x10: return "Q";
            case 0x11: return "W";
            case 0x12: return "E";
            case 0x13: return "R";
            case 0x14: return "T";
            case 0x15: return "Y";
            case 0x16: return "U";
            case 0x17: return "I";
            case 0x18: return "O";
            case 0x19: return "P";
            case 0x1A: return "[";
            case 0x1B: return "]";
            case 0x02: return "1";
            case 0x03: return "2";
            case 0x04: return "3";
            case 0x05: return "4";
            case 0x06: return "5";
            case 0x07: return "6";
            case 0x08: return "7";
            case 0x09: return "8";
            case 0x0A: return "9";
            case 0x0B: return "0";
            case 0x0C: return "-";
            case 0x0D: return "=";
            case 0x2C: return "Z";
            case 0x2D: return "X";
            case 0x2E: return "C";
            case 0x2F: return "V";
            case 0x30: return "B";
            case 0x31: return "N";
            case 0x32: return "M";
            case 0x33: return ",";
            case 0x34: return ".";
            case 0x35: return "/";
            case 0x29: return "`";
            case 0x2B: return "\\";
            case 0x39: return "Space";
            case 0x0F: return "Tab";
            case 0x3A: return "CapsLock";
            case 0x2A: return "LShift";
            case 0x36: return "RShift";
            case 0x1D: return extended ? "RCtrl" : "LCtrl";
            case 0x38: return extended ? "RAlt" : "LAlt";
            case 0x01: return "Esc";
            case 0x0E: return "Backspace";
        }

        // Query Windows API GetKeyNameText
        int lParam = (scanCode << 16) | (extended ? (1 << 24) : 0);
        var sb = new StringBuilder(64);
        if (GetKeyNameText(lParam, sb, sb.Capacity) > 0)
        {
            string name = sb.ToString();
            if (!string.IsNullOrWhiteSpace(name))
                return name;
        }

        return $"0x{scanCode:X2}";
    }
}
