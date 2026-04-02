using System.Runtime.InteropServices;

namespace C2O.VJoy
{
    /// <summary>
    /// A bridge to send motion telemetry to vJoy.
    /// </summary>
    internal class VJoyMotionBridge : IDisposable
    {
        [DllImport("vJoyInterface.dll")]
        private static extern bool vJoyEnabled();


        [DllImport("vJoyInterface.dll")]
        private static extern int GetVJDStatus(uint rID);

        [DllImport("vJoyInterface.dll")]
        private static extern bool AcquireVJD(uint rID);

        [DllImport("vJoyInterface.dll")]
        private static extern bool SetAxis(int value, uint rID, uint axis);

        [DllImport("vJoyInterface.dll")]
        private static extern void RelinquishVJD(uint rID);

        const uint HID_USAGE_X = 0x30;
        const uint HID_USAGE_Y = 0x31;
        const uint HID_USAGE_Z = 0x32;
        const uint HID_USAGE_RX = 0x33;
        const uint HID_USAGE_RY = 0x34;

        public uint deviceID = 1;

        bool acuired;

        /// <summary>
        /// Is vJoy enabled in the system
        /// </summary>
        /// <returns>True if vJoy is enabled</returns>
        public static bool VJoyEnabled()
        {
            return vJoyEnabled();
        }

        /// <summary>
        /// Start vJoy Communication
        /// </summary>
        /// <returns>True if connection was successful</returns>
        public bool Begin()
        {
            if (!vJoyEnabled())
            {
                Console.WriteLine($"vJoy not enabled");
                return false;
            }

            if (!AcquireVJD(deviceID))
            {
                Console.WriteLine("Failed to acquire vJoy device");
                return false;
            }

            acuired = true;

            Console.WriteLine("vJoy connected");
            return true;
        }

        int Normalize(float value)
        {
            if (value < -1f)
            {
                value = -1f;
            }
            if (value > 1f)
            {
                value = 1f;
            }

            return (int)((value + 1f) * 0.5f * 32767);
        }


        /// <summary>
        /// The motion data to axes
        /// </summary>
        /// <param name="roll">The Roll Value [-1,1]</param>
        /// <param name="pitch">The Pitch Value [-1,1]</param>
        /// <param name="heave">The Heave Value [-1,1]</param>
        /// <param name="sway">The Sway Value [-1,1]</param>
        /// <param name="surge">The Surge Value [-1,1]</param>
        public void SendAxes(float roll, float pitch, float heave, float sway, float surge)
        {
            SetAxis(Normalize(roll), deviceID, HID_USAGE_X);
            SetAxis(Normalize(pitch), deviceID, HID_USAGE_Y);
            SetAxis(Normalize(heave), deviceID, HID_USAGE_Z);
            SetAxis(Normalize(sway), deviceID, HID_USAGE_RX);
            SetAxis(Normalize(surge), deviceID, HID_USAGE_RY);
        }

        /// <summary>
        /// Finish the communication
        /// </summary>
        public void End()
        {
            if (acuired)
            {
                SetAxis(Normalize(0), deviceID, HID_USAGE_X);
                SetAxis(Normalize(0), deviceID, HID_USAGE_Y);
                SetAxis(Normalize(0), deviceID, HID_USAGE_Z);
                SetAxis(Normalize(0), deviceID, HID_USAGE_RX);
                SetAxis(Normalize(0), deviceID, HID_USAGE_RY);
                acuired = false;
                RelinquishVJD(deviceID);
            }
        }

        /// <summary>
        /// IDisposable
        /// </summary>
        public void Dispose()
        {
            End();
        }
    }
}
