using ML.SDK;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using TMPro;
using System.Text;

public class MLWheelOSC : MonoBehaviour
{
    [Header("Shared OSC Settings")]
    private OSC osc = new OSC();
    public TextMeshPro debugText;

    [Header("Server Settings (Receiving)")]
    public string oscAddressPattern;
    public bool isServerRunning;
    public bool isBound;

    public bool sendInputs;

    [Header("Client Settings (Sending)")]
    public string clientAddress = "127.0.0.1";
    public bool isClientRunning;

    [Header("Objects")]
    public GameObject wheel;
    public GameObject colorchangeobject;

    [Header("Wheel Visuals")]
    public float maxSteeringAngle = 180f;
    [Tooltip("Colors mapped to button indices (Index 0 = Button 0, etc.)")]
    public Color[] buttonColors = new Color[] { Color.red, Color.green, Color.blue, Color.yellow };

    [Header("Logging Settings")]
    public int maxLogLines = 15;
    private Queue<string> logQueue = new Queue<string>();

    private Renderer colorRenderer;

    [Header("Internal Tracking")]
    private float currentSteeringAngle = 0f;
    public float currentOSCSteeringValue = 0f; //Exposes the -1 steering wheel turning axis 1 value to the car
    public float currentOSCGasValue = 0f;   //Exposes Axis 1 (Gas)
    public float currentOSCBrakeValue = 0f; //Exposes Axis 3 (Brake)

    // --- State Dictionaries for the Dashboard ---
    private Dictionary<int, float> axisStates = new Dictionary<int, float>();
    private Dictionary<int, int> buttonStates = new Dictionary<int, int>();
    private Dictionary<int, string> hatStates = new Dictionary<int, string>();

    void Start()
    {
        Log("\nOSC Wheel Manager started");
        osc.TryBindAddressPattern(oscAddressPattern, OnOSCMessage);

        if (colorchangeobject != null)
            colorRenderer = colorchangeobject.GetComponent<Renderer>();

        UpdateDashboard(); // Initial draw
    }

    public void Log(string msg)
    {
        if (debugText == null) return;
        logQueue.Enqueue(msg);
        while (logQueue.Count > maxLogLines) logQueue.Dequeue();
        debugText.text = string.Join("\n", logQueue);
    }

    public void OnOSCMessage(object[] args)
    {
        if (args.Length < 3) return;

        string inputType = args[0].ToString();
        int inputIndex = int.Parse(args[1].ToString());
        string inputValue = args[2].ToString();

        // --- AXIS INPUTS ---
        if (inputType == "axis")
        {
            // Parse and store the value for the dashboard FIRST
            float axisValue = float.Parse(inputValue);
            axisStates[inputIndex] = axisValue;

            // Then route the specific logic based on the index
            if (inputIndex == 0)
            {
                currentOSCSteeringValue = axisValue; // Store it for the CarDriver script

                if (wheel != null)
                {
                    // 1. Calculate the new target angle and the delta (difference) needed to get there
                    float targetAngle = axisValue * maxSteeringAngle;
                    float deltaAngle = targetAngle - currentSteeringAngle;

                    // 2. Find the true visual center of the wheel's mesh, ignoring its pivot
                    Renderer wheelRenderer = wheel.GetComponent<Renderer>();
                    if (wheelRenderer != null)
                    {
                        Vector3 trueCenter = wheelRenderer.bounds.center;

                        // 3. Rotate around that calculated center point. 
                        // Using wheel.transform.forward assumes the wheel rotates like a clock face on its local Z axis.
                        wheel.transform.RotateAround(trueCenter, wheel.transform.forward, deltaAngle);

                        // 4. Store the current angle for the next incoming OSC message
                        currentSteeringAngle = targetAngle;
                    }
                }
            }
            else if (inputIndex == 1)
            {
                currentOSCGasValue = axisValue;
            }
            else if (inputIndex == 3) // BRAKE PEDAL
            {
                currentOSCBrakeValue = axisValue;
            }
        }

        // --- BUTTON COLOR CHANGE ---
        if (inputType == "button")
        {
            int isPressed = int.Parse(inputValue);
            buttonStates[inputIndex] = isPressed; // Store in state dictionary

            // Only trigger visual change on button press (1)
            if (isPressed == 1 && colorRenderer != null && inputIndex < buttonColors.Length)
            {
                colorRenderer.material.color = buttonColors[inputIndex];
            }
        }

        // Handle D-Pad / Hats
        else if (inputType == "hat" && args.Length >= 4)
        {
            //   Log($"[OSC Wheel] HAT {inputIndex} changed to: X:{args[2]}, Y:{args[3]}");
            hatStates[inputIndex] = $"X: {args[2]} | Y: {args[3]}";
        }

        UpdateDashboard();
    }


    /// <summary>
    /// Builds an in-place dashboard string from the current state dictionaries.
    /// </summary>
    private void UpdateDashboard()
    {
        if (debugText == null) return;

        StringBuilder sb = new StringBuilder();
        sb.AppendLine("--- OSC Input Dashboard ---");
        sb.AppendLine($"Bound Addr: {oscAddressPattern}");
        sb.AppendLine("---------------------------");

        if (axisStates.Count > 0)
        {
            sb.AppendLine("\n[ Axes ]");
            foreach (var kvp in axisStates)
            {
                // Inject friendly names for your known axes
                string axisName = $"Axis {kvp.Key}";
                if (kvp.Key == 0) axisName += " (Steering)";
                else if (kvp.Key == 1) axisName += " (Gas)";
                else if (kvp.Key == 3) axisName += " (Brake)";

                sb.AppendLine($"{axisName,-18}: {kvp.Value:F3}");
            }
        }

        if (buttonStates.Count > 0)
        {
            sb.AppendLine("\n[ Buttons ]");
            foreach (var kvp in buttonStates)
            {
                string state = kvp.Value == 1 ? "<color=green>PRESSED</color>" : "Released";
                sb.AppendLine($"Btn {kvp.Key,-2}: {state}");
            }
        }

        if (hatStates.Count > 0)
        {
            sb.AppendLine("\n[ D-Pad / Hats ]");
            foreach (var kvp in hatStates)
            {
                sb.AppendLine($"Hat {kvp.Key,-2}: {kvp.Value}");
            }
        }

        debugText.text = sb.ToString();
    }

    void Update()
    {
        // Process server (receiving) and client (sending) states independently
    //    HandleServerLogic();
     //   HandleClientLogic();
    }

    /// <summary>
    /// Handles binding and unbinding of receiving addresses
    /// </summary>
    private void HandleServerLogic()
    {
        // Check if server just turned ON
        if (osc.IsServerRunning && !isServerRunning)
        {
            Log("\n (SERVER) Started running.");
            isServerRunning = true;
        }

        // Check if server just turned OFF
        if (!osc.IsServerRunning && isServerRunning)
        {
            Log("\n (SERVER) Stopped running. Unbinding...");
            if (isBound)
            {
                osc.TryUnBindAddressPattern(oscAddressPattern);
                isBound = false;
                Log($"\n (SERVER) Stopped running. Unbound from {oscAddressPattern}");

            }
            isServerRunning = false;
        }

        // If the server is running but we haven't bound the address yet, try to bind it
        if (isServerRunning && !isBound)
        {
            Log($"\n (SERVER) Attempting to bind OSC handler to: {oscAddressPattern}");
          //  osc.TryBindAddressPattern(oscAddressPattern, OnOSCMessage);
            Log($" osc.TryBindAddressPattern(oscAddressPattern, OnOSCMessage) : {osc.TryBindAddressPattern(oscAddressPattern, OnOSCMessage)}");
            Log($"\n osc.HasAddress(oscAddressPattern) : {osc.HasAddress(oscAddressPattern)} Bound to : {oscAddressPattern}");
            isBound = true;
        }
    }

    /// <summary>
    /// Handles the connection state for sending messages outward
    /// </summary>
    private void HandleClientLogic()
    {
        // Check if client just turned ON
        if (osc.IsClientRunning && !isClientRunning)
        {
            Log("\n (CLIENT) Started running. Ready to send.");
            isClientRunning = true;
            // osc.SendMessage(clientAddress, "testing 123");
        }

        // Check if client just turned OFF
        if (!osc.IsClientRunning && isClientRunning)
        {
            Log("\n (CLIENT) Stopped running.");
            isClientRunning = false;
        }
    }

    /// <summary>
    /// Public helper method to allow other scripts to send OSC messages easily
    /// </summary>
    public void SendOutgoingMessage(string message)
    {
        if (isClientRunning)
        {
            //if you want to send an outgoing message : 
            //it is possible too.
            // osc.SendMessage(clientAddress, message);
            Log($"\n (CLIENT) Sent message: {message}");
        }
        else
        {
            Log("\n (CLIENT ERROR) Attempted to send message, but client is not running.");
        }
    }
}