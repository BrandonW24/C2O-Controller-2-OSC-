using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace C2O.Serialization
{
    public class ProfileData
    {
        public string Ip { get; set; } = "127.0.0.1";
        public string Port { get; set; } = "4041";
        public string ListenIp { get; set; } = "127.0.0.1";
        public string ListenPort { get; set; } = "4042";
        public string BaseAddress { get; set; } = "/wheel/input";

        // FFB Configs
        public bool FfbEnabled { get; set; } = true;
        public float FfbGain { get; set; } = 1.0f;

        // Motion configs
        public bool MotionEnabled { get; set; } = false;
        public string MotionIp { get; set; } = "127.0.0.1";
        public string MotionPort { get; set; } = "20777";
        public string MotionProtocol { get; set; } = "Raw OSC";

        public bool SafetyEnabled { get; set; } = true;
        public int SafetyTimeoutMs { get; set; } = 500;

        public Dictionary<string, MotionAxisConfig> MotionAxes { get; set; } = new Dictionary<string, MotionAxisConfig>();

        public Dictionary<int, AxisConfig> Axes { get; set; } = new Dictionary<int, AxisConfig>();
        public Dictionary<int, ButtonConfig> Buttons { get; set; } = new Dictionary<int, ButtonConfig>();
        public Dictionary<int, HatConfig> Hats { get; set; } = new Dictionary<int, HatConfig>();
        public Dictionary<int, KeyConfig> Keys { get; set; } = new Dictionary<int, KeyConfig>();
    }
}
