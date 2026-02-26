# CTRL 2 OSC (C2O)

A lightweight, GUI-driven application designed to seamlessly bridge the gap between physical hardware and digital environments. 

C2O reads real-time data from connected USB steering wheels, Bluetooth gamepads, and joysticks. It captures everything from continuous analog axes (pedals, throttles, analog sticks) to discrete button presses and D-pad movements. It translates and broadcasts these inputs over a local network using the Open Sound Control (OSC) protocol, ensuring low-latency communication without the need for heavy middleware. 

C2O was originally developed as a versatile solution for mapping physical simulation hardware to Massive Loop, and features **two-way OSC communication**, allowing remote software to send Force Feedback (FFB) commands back to the application to dynamically adjust steering wheel resistance in real-time.

Youtube Video Demo :
[![Massive Loop | OSC Vehicle Showcase](https://img.youtube.com/vi/9N-i8Vs3JMc/0.jpg)](https://www.youtube.com/watch?v=9N-i8Vs3JMc)

## Key Features

* **Universal Hardware Support:** Powered by a PySDL2 backend for direct, low-level access to device haptics and broad compatibility (XInput, DirectInput, etc.).
* **Dynamic Input Previews:** Real-time UI visualization that automatically adapts to your connected device (e.g., degree readouts for wheels vs. X/Y grid plots for gamepads).
* **Two-Way OSC (Hardware FFB):** Supports incoming OSC messages to control Centering Spring, Damper (Weight), and Static Friction on compatible steering wheels.
* **Input Profiles:** Save, load, and manage custom device layouts, deadzones, and sensitivities.
* **System Tray Integration:** Runs silently in the background with a system tray menu.

---

## Quick Start (Standalone Executable)

For the easiest setup, you can use the pre-compiled `.exe` file. No Python installation is required.

1. **Download the Release:** Grab the latest `C2O.exe` from the repository releases.
2. **Setup Assets:** Ensure the application icons are in the **same directory** as the `.exe`:
   * `steering-wheel-car_off.png`
   * `steering-wheel-car_on.png`
     *(If these files are missing, the app will safely fall back to basic colored squares).*
3. **Run:** Double-click `C2O.exe` to launch the application.
4. **Configure & Stream:** Select your input device, map your target IP/Ports in the Output Settings, and click **Start Streaming**.

---

## Running from Source (Python Setup)

If you prefer to run the script directly or want to modify the code, you will need Python 3.x.

### Dependencies

Install the required dependencies using `pip`:

    pip install pysdl2 python-osc Pillow pystray

* **`pysdl2`**: Handles underlying USB device polling, input event loops, and Force Feedback (haptics) drivers. 
* **`python-osc`**: Formats and transmits/receives the UDP network packets.
* **`Pillow`**: Processes the `.png` icons for the GUI and system tray.
* **`pystray`**: Manages the background system tray icon and menu.

### Execution

With your controller connected and your assets in the same folder, run the script from your terminal:

    python wheel_to_osc.py

---

## Localized Testing

If you want to verify that C2O is reading your controller and formatting the OSC packets correctly before integrating it with your target software, you can run a localized test:

1. **Target Localhost:** In the C2O Output Settings, set the **Target IP** to `127.0.0.1`.
2. **Set the Port:** Set the **Target Port (Send)** to `4041` (or your preferred test port).
3. **Use an OSC Monitor:** Download a free OSC monitoring tool (such as [Protokol](https://hexler.net/protokol)) or run a simple Python OSC listener script.
4. **Listen:** Configure your monitoring tool to listen on port `4041`.
5. **Test Inputs:** Click **Start Streaming** in C2O. Move your axes and press buttons on your controller; you should see the formatted OSC arrays arriving in your monitor in real-time.

---

## Input Profiles

You can save different device layouts and network parameters using the **Input Profile** manager at the top of the settings page. 

* Click **New Profile** to create a fresh setup for a different controller.
* Click **Save Settings** to write your current configuration to the local `config.json` file.
* Profiles load automatically the next time you boot the application.

---

## OSC Payload Formats

### 1. Output (Sending from C2O to your software)

When the stream is active, the application sends out messages in a multi-argument format. Only values that have changed since the last frame are broadcasted to save bandwidth.

* **Axes (Steering, Pedals, Joysticks):** `[Address] "axis" [Axis Index] [Float Value]`
  * *Example:* `/wheel/input axis 0 0.452`
* **Buttons (Shifters, Face Buttons):** `[Address] "button" [Mapped Button Index] [Int Value (0 or 1)]`
  * *Example:* `/wheel/input button 3 1`
* **Hats (D-Pads):** `[Address] "hat" [Hat Index] [X Int (-1, 0, 1)] [Y Int (-1, 0, 1)]`
  * *Example:* `/wheel/input hat 0 1 -1`

### 2. Input (Receiving FFB commands from your software)

If your connected device supports haptic feedback (like a Sim Racing Wheel), your game engine or remote application can send OSC floats to C2O to dynamically alter the wheel resistance.

* **Centering Spring:** `/ffb/spring [Float 0.0 - 100.0]`
  * Adjusts the overall stiffness and auto-centering force.
* **Damper:** `/ffb/damper [Float 0.0 - 100.0]`
  * Adjusts the dynamic weight and sluggishness of the wheel.
* **Friction:** `/ffb/friction [Float 0.0 - 100.0]`
  * Adjusts the static surface friction.

---

## Notes

* **Background Operation:** The app features a system tray icon. You can minimize the application and it will continue to stream data seamlessly in the background. Right-click the system tray icon to pause the stream, show the window, or quit the app.
* **Dashboard Output:** In the "Output Settings" tab, you can switch the output visualizer from a "Scrolling Log" (showing every packet sent) to an "In-Place Dashboard" (showing the current static state of all inputs) to reduce UI rendering load.
* **Windows Taskbar:** The script uses a `ctypes` AppUserModelID trick to separate the application icon from the default Python taskbar grouping on Windows.
