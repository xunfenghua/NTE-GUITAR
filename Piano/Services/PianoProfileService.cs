using MidiAutoPlayer.Models;
using MidiAutoPlayer.Piano.Models;

namespace MidiAutoPlayer.Piano.Services;

public sealed class PianoProfileService
{
    private readonly AppConfig _config;
    private readonly Action _saveConfig;

    public PianoProfileService(AppConfig config, Action saveConfig)
    {
        _config = config;
        _saveConfig = saveConfig;
    }

    public object GetState()
    {
        EnsureDefaultProfiles();
        var active = _config.PianoProfiles.First(profile => profile.Id == _config.ActivePianoProfileId);
        return new
        {
            activeProfileId = active.Id,
            activeProfile = active,
            profiles = _config.PianoProfiles
        };
    }

    public IReadOnlyList<PianoLayoutProfile> GetProfiles()
    {
        EnsureDefaultProfiles();
        return _config.PianoProfiles;
    }

    public PianoLayoutProfile GetActiveProfile()
    {
        EnsureDefaultProfiles();
        return _config.PianoProfiles.First(profile => profile.Id == _config.ActivePianoProfileId);
    }

    public PianoLayoutProfile GetProfile(string profileId)
    {
        EnsureDefaultProfiles();
        return _config.PianoProfiles.FirstOrDefault(profile => profile.Id == profileId)
            ?? throw new InvalidOperationException("找不到对应的游戏钢琴布局配置。");
    }

    public PianoLayoutProfile SetActiveLayout(PianoLayoutKind kind)
    {
        EnsureDefaultProfiles();
        string defaultId = kind switch
        {
            PianoLayoutKind.SevenKeyScale => "game-piano-seven",
            PianoLayoutKind.TwelveKeyChromatic => "game-piano-twelve",
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        var profile = _config.PianoProfiles.FirstOrDefault(item => item.Id == defaultId);
        if (profile is null)
        {
            profile = PianoLayoutProfile.CreateUncalibrated(kind);
            _config.PianoProfiles.Add(profile);
        }
        _config.ActivePianoProfileId = profile.Id;
        _saveConfig();
        return profile;
    }

    public void SaveProfile(PianoLayoutProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var errors = profile.ValidateStructure();
        if (errors.Count > 0)
            throw new InvalidOperationException(string.Join("\n", errors));

        EnsureDefaultProfiles();
        int index = _config.PianoProfiles.FindIndex(item => item.Id == profile.Id);
        if (index < 0) _config.PianoProfiles.Add(profile);
        else _config.PianoProfiles[index] = profile;
        _config.ActivePianoProfileId = profile.Id;
        _saveConfig();
    }

    public PianoLayoutProfile UpdateScale(string profileId, string displayName, int tonicMidiPitch, IReadOnlyList<int> intervals)
    {
        var profile = GetProfile(profileId);
        if (profile.Kind != PianoLayoutKind.SevenKeyScale || profile.ScaleProfile is null)
            throw new InvalidOperationException("只有七键调式布局可以修改主音和调式。");
        if (string.IsNullOrWhiteSpace(displayName) || displayName.Length > 80)
            throw new ArgumentException("调式名称不能为空，且不能超过 80 个字符。", nameof(displayName));
        if (tonicMidiPitch is < PianoLayoutProfile.DefaultMidiMin or > PianoLayoutProfile.DefaultMidiMax)
            throw new ArgumentOutOfRangeException(nameof(tonicMidiPitch), "主音 MIDI 音高必须在 0..127。");

        profile.ScaleProfile.DisplayName = displayName.Trim();
        profile.ScaleProfile.TonicMidiPitchAtMiddleRow = tonicMidiPitch;
        profile.ScaleProfile.Intervals = intervals.ToList();
        var errors = profile.ValidateStructure();
        if (errors.Count > 0) throw new InvalidOperationException(string.Join("\n", errors));
        SaveProfile(profile);
        return profile;
    }

    private void EnsureDefaultProfiles()
    {
        _config.PianoProfiles ??= new List<PianoLayoutProfile>();
        bool changed = false;
        if (!_config.PianoProfiles.Any(profile => profile.Kind == PianoLayoutKind.TwelveKeyChromatic))
        {
            _config.PianoProfiles.Insert(0, PianoLayoutProfile.CreateUncalibrated(PianoLayoutKind.TwelveKeyChromatic));
            changed = true;
        }
        if (!_config.PianoProfiles.Any(profile => profile.Kind == PianoLayoutKind.SevenKeyScale))
        {
            _config.PianoProfiles.Add(PianoLayoutProfile.CreateUncalibrated(PianoLayoutKind.SevenKeyScale));
            changed = true;
        }
        foreach (var kind in new[] { PianoLayoutKind.SevenKeyScale, PianoLayoutKind.TwelveKeyChromatic })
        {
            var existing = _config.PianoProfiles.First(profile => profile.Kind == kind);
            var defaults = PianoLayoutProfile.CreateUncalibrated(kind);
            bool sameBindings = existing.Bindings.Count == defaults.Bindings.Count &&
                defaults.Bindings.All(expected => existing.Bindings.Any(actual =>
                    actual.Row == expected.Row && actual.Position == expected.Position &&
                    actual.DisplayKey == expected.DisplayKey && actual.ScanCode == expected.ScanCode &&
                    actual.Extended == expected.Extended && actual.BaseMidiPitch == expected.BaseMidiPitch &&
                    actual.IsVerified == expected.IsVerified));
            bool sameModifiers = kind == PianoLayoutKind.SevenKeyScale ||
                (existing.ModifierProfile?.Shift.ScanCode == defaults.ModifierProfile?.Shift.ScanCode &&
                 existing.ModifierProfile?.Shift.SemitoneDelta == defaults.ModifierProfile?.Shift.SemitoneDelta &&
                 existing.ModifierProfile?.Shift.IsVerified == defaults.ModifierProfile?.Shift.IsVerified &&
                 existing.ModifierProfile?.Control.ScanCode == defaults.ModifierProfile?.Control.ScanCode &&
                 existing.ModifierProfile?.Control.SemitoneDelta == defaults.ModifierProfile?.Control.SemitoneDelta &&
                 existing.ModifierProfile?.Control.IsVerified == defaults.ModifierProfile?.Control.IsVerified);
            if (!sameBindings || !sameModifiers || existing.Id != defaults.Id)
            {
                if (kind == PianoLayoutKind.SevenKeyScale && existing.ScaleProfile is not null)
                    defaults.ScaleProfile = existing.ScaleProfile;
                _config.PianoProfiles[_config.PianoProfiles.IndexOf(existing)] = defaults;
                changed = true;
            }
        }
        PianoLayoutProfile? active = _config.PianoProfiles.FirstOrDefault(profile => profile.Id == _config.ActivePianoProfileId);
        if (active is null)
        {
            _config.ActivePianoProfileId = _config.PianoProfiles.First(profile => profile.Kind == PianoLayoutKind.TwelveKeyChromatic).Id;
            changed = true;
        }
        if (changed) _saveConfig();
    }
}
