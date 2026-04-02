namespace C2O.Serialization
{
    public class MotionAxisConfig
    {
        public float Gain { get; set; } = 1.0f;
        public float Smooth { get; set; } = 0.0f;
        public bool WashoutEnabled { get; set; } = false;
        public float WashoutStrength { get; set; } = 0.99f;
    }
}
