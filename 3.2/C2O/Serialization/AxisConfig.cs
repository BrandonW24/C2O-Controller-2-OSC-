namespace C2O.Serialization
{
    public class AxisConfig
    {
        public string CustomName { get; set; } = "";
        public int OscId { get; set; }
        public string CustomAddress { get; set; } = "";
        public bool IsInverted { get; set; } = false;
        public bool IsActive { get; set; } = true;
        public float Sensitivity { get; set; } = 1.0f;
        public float Deadzone { get; set; } = 0.0f;
        public float Curve { get; set; } = 1.0f;
        public float Smooth { get; set; } = 0.0f;
    }
}
