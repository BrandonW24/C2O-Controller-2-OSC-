* # CTRL 2 OSC (C2O) - V3.3 C# Edition

  A lightweight, GUI-driven application designed to seamlessly bridge the gap between physical hardware and digital environments. 

  C2O reads real-time data from connected USB steering wheels, Bluetooth gamepads, joysticks, and keyboards. It captures everything from continuous analog axes (pedals, throttles, analog sticks) to discrete button presses, global keystrokes, and D-pad movements. It translates and broadcasts these inputs over a local network using the Open Sound Control (OSC) protocol, ensuring low-latency communication without the need for heavy middleware. 

  Originally developed as a versatile solution for mapping physical simulation hardware to Massive Loop, C2O V3.3 has been completely rebuilt in C# / WPF for native Windows performance. It now features advanced two-way OSC communication for dynamic Force Feedback (FFB) and a dedicated Motion Platform Forwarding engine for Sim Racing and Flight Sim rigs.

  Youtube Video Demo:

  [![Massive Loop | OSC Vehicle Showcase](https://img.youtube.com/vi/vynb3MnTXRs/0.jpg)]([Massive Loop | Mars Rover Experience](https://www.youtube.com/watch?v=vynb3MnTXRs))

  ## Key Features

  * **Native C# Performance:** Rebuilt from the ground up in .NET/WPF, utilizing a dedicated multi-threaded high-speed loop purely for hardware polling via SDL2 and OSC broadcasting.
  * **Multi-Device & Keyboard Support:** Capture inputs from multiple different hardware devices simultaneously, or map global keyboard keystrokes and combinations directly to OSC outputs.
  * **Motion Platform Forwarding (New in V3.3):** Route incoming 6-DoF OSC telemetry directly to your motion platform's UDP port. Includes Per-Axis High-Pass Washout Filters, Gain/Smoothing adjustments, SimTools String translation, and a Safety Heartbeat timeout to auto-zero your rig on disconnect.
  * **Expanded Hardware FFB (Two-Way OSC):** Supports incoming OSC messages to control Constant Force, Centering Spring, Damper (Weight), Static Friction, and standard Gamepad Rumble. Includes a built-in FFB Tester and Signal Clipping Monitor.
  * **Advanced Axis Tuning:** Fine-tune your controls on a per-axis basis with adjustable Deadzones, Sensitivity multipliers, Non-linear Curves, and Exponential Moving Average (EMA) Smoothing.
  * **Built-in Incoming OSC Monitor:** Easily debug and read incoming telemetry or FFB signals natively inside the app via a scrolling terminal or in-place dashboard.
  * **Profile Management:** Save, clone, export, and import your configurations as `.json` files to easily share setups or swap between different simulation rigs.

  ---

  ## Quick Start (Standalone Executable)

  For the easiest setup, use the pre-compiled `.exe` file.

  1. **Download the Release:** Grab the latest C2O release from the repository.
  2. **Setup Dependencies:** Ensure `SDL2.dll` is located in the **same directory** as your executable so the application can read USB hardware.
  3. **Run:** Double-click the `.exe` to launch the application.
  4. **Configure & Stream:** Select your input device(s), map your target IP/Ports in the Output Settings, and click **Start Streaming**.

  ---

  ## Building from Source (C# / .NET)

  If you wish to modify the code or build it yourself, you will need Visual Studio and the .NET SDK. Python is no longer required.

  ### Dependencies

  Ensure your project has the following dependencies configured (usually via NuGet):
  * **`CoreOSC`**: Handles formatting and transmitting/receiving UDP network packets.
  * **`SDL2-CS`** (or raw SDL2 bindings): Handles underlying USB device polling, input event loops, and Force Feedback (haptics) drivers.

  ### Execution

  1. Open the `.sln` or `.csproj` file in Visual Studio.
  2. Ensure the required `steering-wheel-car_on.png` and `steering-wheel-car_off.png` assets are set as Embedded Resources or are present in the build directory.
  3. Build and run the solution.

  ---

  ## Localized Testing

  If you want to verify that C2O is formatting the OSC packets correctly before integrating it with your target software:

  1. **Target Localhost:** In the Output Settings, set the **Target IP** to `127.0.0.1`.
  2. **Set the Port:** Set the **Target Port (Send)** to `4041` (or your preferred test port).
  3. **Use an OSC Monitor:** Download a free OSC monitoring tool (such as [Protokol](https://hexler.net/protokol)).
  4. **Listen:** Configure your monitoring tool to listen on your chosen port.
  5. **Test Inputs:** Click **Start Streaming** in C2O. Move your axes or press buttons; you should see the formatted OSC arrays arriving in your monitor.

  *(Note: You can also use C2O's built-in "Incoming OSC Monitor" tab to test signals being sent back to the app on port `4042`.)*

  ---

  ## OSC Payload Formats

  ### 1. Output (Sending from C2O to your software)

  Only values that have changed since the last frame are broadcasted to save bandwidth. Custom OSC addresses mapped in the UI will replace the `[Address]` field; otherwise, it defaults to your Base OSC Address.

  * **Axes (Steering, Pedals):** `[Address] "axis" [Mapped ID] [Float Value]`
  * **Buttons (Shifters, Face Buttons):** `[Address] "button" [Mapped ID] [Int Value (0/1)]`
  * **Hats (D-Pads):** `[Address] "hat" [Mapped ID] [Int X] [Int Y]`
  * **Keyboard (Global Keys):** `[Address] "keyboard" [Mapped ID] [Int Value (0/1)]`

  ### 2. Input (Receiving FFB & Rumble commands)

  Send these commands to C2O's configured Listen Port (default `4042`) to trigger haptics on supported hardware.

  * `/ffb/force [Float 0-100]`: Adjusts constant Cartesian pull
  * `/ffb/spring [Float 0-100]`: Adjusts centering resistance
  * `/ffb/damper [Float 0-100]`: Adjusts dynamic wheel weight
  * `/ffb/friction [Float 0-100]`: Adjusts static friction
  * `/ffb/rumble [Float 0-100]`: Triggers standard gamepad rumble

  ### 3. Motion Telemetry (6-DoF Forwarding)

  Send your rig's telemetry to C2O. It will process the signals (applying your configured Washout filters and Gain) and forward them to your motion platform hardware

  * `/motion/pitch [Float]`: Forward/Backward Tilt
  * `/motion/roll [Float]`: Left/Right Tilt
  * `/motion/yaw [Float]`: Left/Right Rotation
  * `/motion/surge [Float]`: Forward/Backward Acceleration
  * `/motion/sway [Float]`: Left/Right Acceleration
  * `/motion/heave [Float]`: Up/Down Acceleration

  ---

  ## Notes

  * **Motion Kill Switch:** If your motion platform behaves unexpectedly, hit the red "MOTION KILL SWITCH" in the Motion tab to instantly send a zeroed payload to your rig and halt movement
  * **Dashboard Output:** In both the Output and Incoming Monitor tabs, you can switch the visualizer from a "Scrolling Log" (showing every packet) to an "In-Place Dashboard" (showing the current static state) to drastically reduce UI rendering load during gameplay
