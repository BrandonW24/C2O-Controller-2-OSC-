namespace C2O.Serialization
{
    public class DeviceInfo
    {
        public IntPtr Joystick { get; set; }
        public IntPtr Haptic { get; set; }

        // Multi-Effect FFB Array Management
        public Dictionary<ushort, int> EffectIds { get; set; } = new Dictionary<ushort, int>();
        public bool IsRumbleInitialized { get; set; } = false;

        public string Name { get; set; } = "";
        public int NumAxes { get; set; }
        public int NumButtons { get; set; }
        public int NumHats { get; set; }
        public int AxisOffset { get; set; }
        public int ButtonOffset { get; set; }
        public int HatOffset { get; set; }
    }
}
