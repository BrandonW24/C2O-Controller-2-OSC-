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
    public GameObject CarObject;

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

    // --- Network Scanner Variables ---
    private const string EVENT_SCAN = "ScanEvent";
    private EventToken tokenScanEnvironment;

    // --- Network Reset Variables ---
    private const string EVENT_RESET_CAR = "ResetCarEvent";
    private EventToken tokenResetCar;
    private Vector3 initialCarPosition;
    private Quaternion initialCarRotation;
    private const string EVENT_UPRIGHT_CAR = "UprightCarEvent";
    private EventToken tokenUprightCar;

    // --- State Dictionaries for the Dashboard ---
    private Dictionary<int, float> axisStates = new Dictionary<int, float>();
    private Dictionary<int, int> buttonStates = new Dictionary<int, int>();
    private Dictionary<int, string> hatStates = new Dictionary<int, string>();

    public TextMeshPro Score;
    private int currentScore = 0;

    public ParticleSystem ScoredParticleSystem;

    public GameObject terrainmanager_object;
    private MarsTerrainManager terrainmanager_Script;

    public CarDriver_CSharp car_scriptReference;
    public void AddScore(int points)
    {
        currentScore += points;
        UpdateScoreDisplay();
        Log("Score increased by " + points + "!");
    }

    private void UpdateScoreDisplay()
    {
        if (Score != null)
        {
            ScoredParticleSystem.Play();
            Score.text = "Score: " + currentScore;
        }
    }


    void Start()
    {
        Log("\nOSC Wheel Manager started");
        osc.TryBindAddressPattern(oscAddressPattern, OnOSCMessage);

        if (colorchangeobject != null)
            colorRenderer = colorchangeobject.GetComponent<Renderer>();

        // Store the original position of the car
        if (CarObject != null)
        {
            initialCarPosition = CarObject.transform.position;
            initialCarRotation = CarObject.transform.rotation;
        }

        // Register the synchronized network event
        tokenResetCar = this.AddEventHandler(EVENT_RESET_CAR, OnCarResetNetwork);
        tokenScanEnvironment = this.AddEventHandler(EVENT_SCAN, OnCarScanNetwork);

        terrainmanager_Script = terrainmanager_object.GetComponent(typeof(MarsTerrainManager)) as MarsTerrainManager;
        car_scriptReference = CarObject.GetComponent(typeof(CarDriver_CSharp)) as CarDriver_CSharp;
        tokenUprightCar = this.AddEventHandler(EVENT_UPRIGHT_CAR, OnCarUprightNetwork);

        UpdateDashboard(); // Initial draw
    }


    private void OnCarUprightNetwork(object[] args)
    {
        if (CarObject != null)
        {
            // Reset the physical momentum so the car drops cleanly
            Rigidbody carRb = CarObject.GetComponent<Rigidbody>();
            if (carRb != null)
            {
                carRb.velocity = Vector3.zero;
                carRb.angularVelocity = Vector3.zero;
            }

            // Keep the current X and Z, but boost the Y slightly and flatten the rotation
            Vector3 currentPos = CarObject.transform.position;
            currentPos.y += 2.0f; // Drop from 2 units above its current spot

            // Flatten the rotation (Zero out X and Z roll/pitch, keep the Y yaw so it faces the same way)
            Quaternion currentRot = CarObject.transform.rotation;
            Vector3 eulerRotation = currentRot.eulerAngles;
            Quaternion flatRotation = Quaternion.Euler(0, eulerRotation.y, 0);

            CarObject.transform.position = currentPos;
            CarObject.transform.rotation = flatRotation;

            Log("Car uprighted in place.");
        }
    }


    private void OnCarScanNetwork(object[] args)
    {
        Log("Car attempting to scan");
        // Non-generic grab of the Terrain Manager to trigger the visual scan response
        
        if (terrainmanager_Script != null)
        {
            terrainmanager_Script.TriggerScan();
        }
    }

    private void OnCarResetNetwork(object[] args)
    {
        if (CarObject != null)
        {
            // Reset the physical momentum so the car doesn't go flying after resetting
            Rigidbody carRb = CarObject.GetComponent<Rigidbody>();
            if (carRb != null)
            {
                carRb.velocity = Vector3.zero;
                carRb.angularVelocity = Vector3.zero;
            }

            // Move the car back to the starting point
            CarObject.transform.position = initialCarPosition;
            CarObject.transform.rotation = initialCarRotation;

            Log("Car reset to original position.");
        }
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
            /*
            if (isPressed == 1 && colorRenderer != null && inputIndex < buttonColors.Length)
            {
                colorRenderer.material.color = buttonColors[inputIndex];
            }
            */

            if (inputIndex == 16 && isPressed == 1)
            {
                Log("Reset button pressed. Broadcasting reset event...");
                // Broadcast to all clients to reset the car
                this.InvokeNetwork(EVENT_RESET_CAR, EventTarget.All, null);
            }

            if(inputIndex == 10 && isPressed == 1)
            {
                Log("Scanner button pressed. Broadcasting reset event...");
                this.InvokeNetwork(EVENT_SCAN, EventTarget.All, null);
            }

            if (inputIndex == 0 && isPressed == 1)
            {
                Log("Skip Mars Fact button pressed. Broadcasting event...");
                if(car_scriptReference.CurrentMarsFact != null)
                {
                   car_scriptReference.CurrentMarsFact.SetActive(false);
                }
            }

            if (inputIndex == 1 && isPressed == 1)
            {
                Log("Upright button pressed. Broadcasting upright event...");
                this.InvokeNetwork(EVENT_UPRIGHT_CAR, EventTarget.All, null);
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