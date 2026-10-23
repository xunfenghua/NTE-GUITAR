using MidiAutoPlayer.Guitar;
using MidiAutoPlayer.Models;
using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Targets;
using Nefarius.ViGEm.Client.Targets.Xbox360;

namespace MidiAutoPlayer.Input;

/// <summary>
/// Sends guitar controls through an emulated Xbox 360 controller. Each public
/// input method submits immediately; timing and releases belong to the caller's
/// action timeline rather than an internal timer or wait loop.
/// </summary>
public sealed class VirtualGamepadController : IDisposable
{
    private readonly object _sync = new();
    private ViGEmClient? _client;
    private IXbox360Controller? _controller;
    private readonly bool[] _heldStrings = new bool[7];
    private byte _activeSlot;
    private ushort _activeSlotGeneration;

    public string? LastError { get; private set; }

    private static readonly (short X, short Y)[] SlotVectors =
    [
        (0, 0),
        (0, 32767),
        (28377, 16383),
        (28377, -16383),
        (0, -32767),
        (-28377, -16383),
        (-28377, 16383)
    ];

    public bool EnsureConnected()
    {
        lock (_sync)
        {
            if (_controller != null) return true;
            try
            {
                _client ??= new ViGEmClient();
                _controller = _client.CreateXbox360Controller();
                _controller.AutoSubmitReport = false;
                _controller.Connect();
                _controller.ResetReport();
                _controller.SubmitReport();
                _activeSlot = 0;
                _activeSlotGeneration = 0;
                Array.Clear(_heldStrings);
                LastError = null;
                return true;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                try { _client?.Dispose(); } catch { }
                _client = null;
                _controller = null;
                _activeSlot = 0;
                _activeSlotGeneration = 0;
                Array.Clear(_heldStrings);
                return false;
            }
        }
    }

    public bool SelectSlot(int slot, ushort generation = 0)
    {
        if (slot is < 1 or > 6) return false;
        lock (_sync)
        {
            if (!EnsureConnected() || _controller == null) return false;
            ReleaseDpadConflictsInternal();
            var (x, y) = SlotVectors[slot];
            _controller.SetAxisValue(Xbox360Axis.LeftThumbX, x);
            _controller.SetAxisValue(Xbox360Axis.LeftThumbY, y);
            _activeSlot = (byte)slot;
            _activeSlotGeneration = generation;
            _controller.SubmitReport();
            return true;
        }
    }

    public bool NeutralizeSlot(byte expectedSlot = 0, ushort expectedGeneration = 0)
    {
        lock (_sync)
        {
            if (!EnsureConnected() || _controller == null) return false;
            if (expectedGeneration != 0 && _activeSlotGeneration != expectedGeneration) return true;
            if (expectedSlot != 0 && _activeSlot != 0 && _activeSlot != expectedSlot) return true;

            _controller.SetAxisValue(Xbox360Axis.LeftThumbX, 0);
            _controller.SetAxisValue(Xbox360Axis.LeftThumbY, 0);
            _activeSlot = 0;
            _activeSlotGeneration = 0;
            _controller.SubmitReport();
            return true;
        }
    }

    public bool SendPageNav(int direction, bool isDown)
    {
        lock (_sync)
        {
            if (!EnsureConnected() || _controller == null) return false;
            if (isDown) ClearStringsAndLeftStickInternal();
            _controller.SetButtonState(
                direction >= 0 ? Xbox360Button.RightShoulder : Xbox360Button.LeftShoulder,
                isDown);
            _controller.SubmitReport();
            return true;
        }
    }

    public bool SendModeSwitch(bool isDown)
    {
        lock (_sync)
        {
            if (!EnsureConnected() || _controller == null) return false;
            if (isDown) ClearStringsAndLeftStickInternal();
            _controller.SetSliderValue(Xbox360Slider.RightTrigger, isDown ? (byte)255 : (byte)0);
            _controller.SubmitReport();
            return true;
        }
    }

    public bool SetStrum(MouseButton strumButton, bool isDown)
    {
        lock (_sync)
        {
            if (!EnsureConnected() || _controller == null) return false;
            short value = !isDown
                ? (short)0
                : strumButton == MouseButton.Left ? (short)32767 : (short)-32768;
            _controller.SetAxisValue(Xbox360Axis.RightThumbX, 0);
            _controller.SetAxisValue(Xbox360Axis.RightThumbY, value);
            _controller.SubmitReport();
            return true;
        }
    }

    public bool SendStrum(MouseButton strumButton) => SetStrum(strumButton, true);

    public bool SetStringsBatch(ReadOnlySpan<(byte StringNo, bool IsDown)> changes)
    {
        lock (_sync)
        {
            if (!EnsureConnected() || _controller == null) return false;

            // Face buttons can be submitted together. The D-pad is exclusive;
            // its planner emits one direction per event. If a stale direction
            // remains, submit a neutral report before the recovery press.
            foreach (var (stringNo, isDown) in changes)
            {
                if (stringNo is < 1 or > 6) continue;
                if (stringNo <= 4 && isDown) ReleaseDpadConflictsInternal(stringNo);
                var button = ResolveStringButton(stringNo);
                if (button is not Xbox360Button resolved) continue;
                _controller.SetButtonState(resolved, isDown);
                _heldStrings[stringNo] = isDown;
            }

            _controller.SubmitReport();
            return true;
        }
    }

    public bool SetStringState(byte stringNo, bool isDown)
    {
        Span<(byte StringNo, bool IsDown)> one = [(stringNo, isDown)];
        return SetStringsBatch(one);
    }

    private void ReleaseDpadConflictsInternal(byte exceptString = 0)
    {
        if (_controller == null) return;
        bool changed = false;
        for (byte stringNo = 1; stringNo <= 4; stringNo++)
        {
            if (stringNo == exceptString || !_heldStrings[stringNo]) continue;
            if (ResolveStringButton(stringNo) is Xbox360Button button)
                _controller.SetButtonState(button, false);
            _heldStrings[stringNo] = false;
            changed = true;
        }
        if (changed) _controller.SubmitReport();
    }

    private void ClearStringsAndLeftStickInternal()
    {
        if (_controller == null) return;
        for (byte stringNo = 1; stringNo <= 6; stringNo++)
        {
            if (!_heldStrings[stringNo]) continue;
            if (ResolveStringButton(stringNo) is Xbox360Button button)
                _controller.SetButtonState(button, false);
            _heldStrings[stringNo] = false;
        }
        _controller.SetAxisValue(Xbox360Axis.LeftThumbX, 0);
        _controller.SetAxisValue(Xbox360Axis.LeftThumbY, 0);
        _activeSlot = 0;
        _activeSlotGeneration = 0;
    }

    private static Xbox360Button? ResolveStringButton(byte stringNo) => stringNo switch
    {
        6 => Xbox360Button.X,
        5 => Xbox360Button.A,
        4 => Xbox360Button.Up,
        3 => Xbox360Button.Right,
        2 => Xbox360Button.Left,
        1 => Xbox360Button.Down,
        _ => null
    };

    public void ResetAll()
    {
        lock (_sync)
        {
            _activeSlot = 0;
            _activeSlotGeneration = 0;
            Array.Clear(_heldStrings);
            if (_controller == null) return;
            try
            {
                _controller.ResetReport();
                _controller.SubmitReport();
            }
            catch (Exception ex) { LastError = ex.Message; }
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            try
            {
                if (_controller != null)
                {
                    _controller.ResetReport();
                    _controller.SubmitReport();
                    _controller.Disconnect();
                }
            }
            catch { }
            try { _client?.Dispose(); } catch { }
            _controller = null;
            _client = null;
            _activeSlot = 0;
            _activeSlotGeneration = 0;
            Array.Clear(_heldStrings);
        }
    }
}
