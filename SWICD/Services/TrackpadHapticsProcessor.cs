using neptune_hidapi.net;
using SWICD.Config;
using System;

namespace SWICD.Services
{
    /// <summary>
    /// Recreates the SteamOS style trackpad haptics: a light tick every time the
    /// finger travels a set distance across a pad, and a firmer click when the
    /// pad is pressed down or released.
    /// </summary>
    internal class TrackpadHapticsProcessor
    {
        // Haptic positions as used by the 0x8F haptic pulse report.
        private const byte RightPad = 0;
        private const byte LeftPad = 1;

        // Off time between repeats. Only one pulse is sent, so this mostly pads the report.
        private const ushort PulsePeriod = 1000;
        private const ushort PulseCount = 1;
        private const int ClickStrengthMultiplier = 3;
        private const int ReleaseStrengthMultiplier = 2;

        // Keeps a fast swipe from flooding the controller with feature reports.
        private static readonly TimeSpan MinPulseInterval = TimeSpan.FromMilliseconds(12);

        private class PadState
        {
            public byte Position;
            public bool Touched;
            public bool Pressed;
            public int AnchorX;
            public int AnchorY;
            public DateTime LastPulse = DateTime.MinValue;
        }

        private readonly PadState _left = new PadState() { Position = LeftPad };
        private readonly PadState _right = new PadState() { Position = RightPad };
        private readonly object _lock = new object();

        public void ProcessInput(ControllerConfig config, NeptuneControllerInputState state, NeptuneController controller, bool leftRumbling, bool rightRumbling)
        {
            var settings = config.ProfileSettings;

            lock (_lock)
            {
                ProcessPad(_left, settings, controller, leftRumbling,
                    state.ButtonState[NeptuneControllerButton.BtnLPadTouch],
                    state.ButtonState[NeptuneControllerButton.BtnLPadPress],
                    state.AxesState[NeptuneControllerAxis.LeftPadX],
                    state.AxesState[NeptuneControllerAxis.LeftPadY]);

                ProcessPad(_right, settings, controller, rightRumbling,
                    state.ButtonState[NeptuneControllerButton.BtnRPadTouch],
                    state.ButtonState[NeptuneControllerButton.BtnRPadPress],
                    state.AxesState[NeptuneControllerAxis.RightPadX],
                    state.AxesState[NeptuneControllerAxis.RightPadY]);
            }
        }

        private void ProcessPad(PadState pad, ProfileSettings settings, NeptuneController controller, bool rumbling, bool touched, bool pressed, int x, int y)
        {
            bool pressEdge = pressed && !pad.Pressed;
            bool releaseEdge = !pressed && pad.Pressed;
            pad.Pressed = pressed;

            if (!touched && !pressed)
            {
                pad.Touched = false;
                return;
            }

            if (!pad.Touched)
            {
                // Finger just landed, start measuring travel from here.
                pad.Touched = true;
                pad.AnchorX = x;
                pad.AnchorY = y;
            }

            // Game rumble on this side takes priority over trackpad ticks.
            if (!settings.TrackpadHapticsEnabled || rumbling || settings.TrackpadHapticsStrength == 0)
                return;

            if (pressEdge)
            {
                Pulse(pad, controller, settings.TrackpadHapticsStrength * ClickStrengthMultiplier, force: true);
                return;
            }

            if (releaseEdge)
            {
                Pulse(pad, controller, settings.TrackpadHapticsStrength * ReleaseStrengthMultiplier, force: true);
                return;
            }

            // Measure straight line distance from the last tick rather than summing
            // every small step, so sensor jitter on a resting finger never adds up to a tick.
            long dx = x - pad.AnchorX;
            long dy = y - pad.AnchorY;
            long tickDistance = Math.Max(1, (int)settings.TrackpadHapticsTickDistance);

            if (dx * dx + dy * dy >= tickDistance * tickDistance && Pulse(pad, controller, settings.TrackpadHapticsStrength, force: false))
            {
                pad.AnchorX = x;
                pad.AnchorY = y;
            }
        }

        private bool Pulse(PadState pad, NeptuneController controller, int strength, bool force)
        {
            var now = DateTime.UtcNow;
            if (!force && now - pad.LastPulse < MinPulseInterval)
                return false;

            pad.LastPulse = now;
            _ = controller.SetHaptic(pad.Position, (ushort)Math.Min(strength, ushort.MaxValue), PulsePeriod, PulseCount);
            return true;
        }
    }
}
