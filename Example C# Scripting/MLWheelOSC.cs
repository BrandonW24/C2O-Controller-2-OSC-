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

 //   [Header("Telemetry Output Settings")]
 //   [Tooltip("Enable sending 6-DoF pitch, roll, yaw, surge, sway, heave data out to C2O.")]
    public bool sendMotionTelemetry = true;
 //   [Tooltip("Enable sending Force Feedback (FFB) spring, damper, and rumble data out to C2O.")]
    public bool sendFFBTelemetry = true;

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

 //   public GameObject terrainmanager_object;
  //  private MarsTerrainManager terrainmanager_Script;

    public CarDriver_CSharp car_scriptReference;

    // --- Turn Signal Variables ---
    [Header("Turn Signals")]
    public float blinkRate = 0.5f;
    private bool isLeftTurnSignalActive = false;
    private bool isRightTurnSignalActive = false;
    private Coroutine leftSignalCoroutine;
    private Coroutine rightSignalCoroutine;
    public Light Left_turnsignal;
    public Light Right_turnsignal;
    public GameObject LeftTurnIndicator;
    public GameObject RightTurnIndicator;


    /// <summary>
    /// Sends the local OSC address pattern to C2O so it can update its Global Base Address automatically.
    /// Called by CarDriver_CSharp when the local player sits down.
    /// </summary>
    public void NotifyC2OOfAddress()
    {
        osc.SendMessage("/c2o/setBaseAddress", oscAddressPattern);
        Log($"Notified C2O of base address: {oscAddressPattern}");
    }

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


    private void ToggleRightTurnSignal()
    {
        if (Right_turnsignal == null) return;

        isRightTurnSignalActive = !isRightTurnSignalActive;
        Log("Right turn signal toggled: " + isRightTurnSignalActive);

        if (isRightTurnSignalActive)
        {
            // Cancel the left signal if it happens to be on
            if (isLeftTurnSignalActive) ToggleLeftTurnSignal();

            rightSignalCoroutine = StartCoroutine(BlinkLight(Right_turnsignal, RightTurnIndicator));
        }
        else
        {
            if (rightSignalCoroutine != null) StopCoroutine(rightSignalCoroutine);
            Right_turnsignal.enabled = false; // Force it off when cancelled
            RightTurnIndicator.SetActive(false);

        }
    }

    private void ToggleLeftTurnSignal()
    {
        if (Left_turnsignal == null) return;

        isLeftTurnSignalActive = !isLeftTurnSignalActive;
        Log("Left turn signal toggled: " + isLeftTurnSignalActive);

        if (isLeftTurnSignalActive)
        {
            // Cancel the right signal if it happens to be on
            if (isRightTurnSignalActive) ToggleRightTurnSignal();

            leftSignalCoroutine = StartCoroutine(BlinkLight(Left_turnsignal, LeftTurnIndicator));
        }
        else
        {
            if (leftSignalCoroutine != null) StopCoroutine(leftSignalCoroutine);
            Left_turnsignal.enabled = false; // Force it off when cancelled
            LeftTurnIndicator.SetActive(false);
        }
    }

    private IEnumerator BlinkLight(Light targetLight, GameObject indicator)
    {
        while (true)
        {
            if (targetLight != null)
            {
                targetLight.enabled = !targetLight.enabled;

                if(indicator != null) indicator.SetActive(targetLight.enabled);

            }
            yield return new WaitForSeconds(blinkRate);
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

    //    terrainmanager_Script = terrainmanager_object.GetComponent(typeof(MarsTerrainManager)) as MarsTerrainManager;
        car_scriptReference = CarObject.GetComponent(typeof(CarDriver_CSharp)) as CarDriver_CSharp;
        tokenUprightCar = this.AddEventHandler(EVENT_UPRIGHT_CAR, OnCarUprightNetwork);
        isClientRunning = true;
        UpdateDashboard(); // Initial draw
    }


    private void OnCarUprightNetwork(object[] args)
    {
        if (CarObject != null)
        {
            Rigidbody carRb = CarObject.GetComponent<Rigidbody>();
            if (carRb != null)
            {
                carRb.velocity = Vector3.zero;
                carRb.angularVelocity = Vector3.zero;
            }

            Vector3 currentPos = CarObject.transform.position;
            currentPos.y += 2.0f;

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
      //  if (terrainmanager_Script != null)
    //    {
      //      terrainmanager_Script.TriggerScan();
       // }
    }

    private void OnCarResetNetwork(object[] args)
    {
        if (CarObject != null)
        {
            Rigidbody carRb = CarObject.GetComponent<Rigidbody>();
            if (carRb != null)
            {
                carRb.velocity = Vector3.zero;
                carRb.angularVelocity = Vector3.zero;
            }

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
            float axisValue = float.Parse(inputValue);
            axisStates[inputIndex] = axisValue;

            if (inputIndex == 0)
            {
                currentOSCSteeringValue = axisValue;

                if (wheel != null)
                {
                    float targetAngle = axisValue * maxSteeringAngle;
                    float deltaAngle = targetAngle - currentSteeringAngle;

                    Renderer wheelRenderer = wheel.GetComponent<Renderer>();
                    if (wheelRenderer != null)
                    {
                        Vector3 trueCenter = wheelRenderer.bounds.center;
                        wheel.transform.RotateAround(trueCenter, wheel.transform.forward, deltaAngle);
                        currentSteeringAngle = targetAngle;
                    }
                }
            }
            else if (inputIndex == 1)
            {
                currentOSCGasValue = axisValue;
            }
            else if (inputIndex == 3)
            {
                currentOSCBrakeValue = axisValue;
            }
        }

        // --- BUTTON INPUTS ---
        if (inputType == "button")
        {
            int isPressed = int.Parse(inputValue);
            buttonStates[inputIndex] = isPressed;

            if (inputIndex == 16 && isPressed == 1)
            {
                Log("Reset button pressed. Broadcasting reset event...");
                this.InvokeNetwork(EVENT_RESET_CAR, EventTarget.All, null);
            }

            if (inputIndex == 10 && isPressed == 1)
            {
                Log("Scanner button pressed. Broadcasting reset event...");
                this.InvokeNetwork(EVENT_SCAN, EventTarget.All, null);
            }

            if (inputIndex == 0 && isPressed == 1)
            {
                Log("Skip Mars Fact button pressed. Broadcasting event...");
                if (car_scriptReference.CurrentMarsFact != null)
                {
                    car_scriptReference.CurrentMarsFact.SetActive(false);
                }
            }

            if (inputIndex == 1 && isPressed == 1)
            {
                Log("Upright button pressed. Broadcasting upright event...");
                this.InvokeNetwork(EVENT_UPRIGHT_CAR, EventTarget.All, null);
            }

            // --- Turn Signals ---
            if (inputIndex == 4 && isPressed == 1)
            {
                ToggleRightTurnSignal();
            }

            if (inputIndex == 5 && isPressed == 1)
            {
                ToggleLeftTurnSignal();
            }
        }

        else if (inputType == "hat" && args.Length >= 4)
        {
            hatStates[inputIndex] = $"X: {args[2]} | Y: {args[3]}";
        }

        //disabled to help performance
      //  UpdateDashboard();
    }

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
     //   HandleServerLogic();
    //    HandleClientLogic();
    }

    private void HandleServerLogic()
    {
        if (osc.IsServerRunning && !isServerRunning)
        {
            Log("\n (SERVER) Started running.");
            isServerRunning = true;
        }

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

        if (isServerRunning && !isBound)
        {
            Log($"\n (SERVER) Attempting to bind OSC handler to: {oscAddressPattern}");
            Log($" osc.TryBindAddressPattern(oscAddressPattern, OnOSCMessage) : {osc.TryBindAddressPattern(oscAddressPattern, OnOSCMessage)}");
            Log($"\n osc.HasAddress(oscAddressPattern) : {osc.HasAddress(oscAddressPattern)} Bound to : {oscAddressPattern}");
            isBound = true;
        }
    }

    private void HandleClientLogic()
    {
        if (osc.IsClientRunning && !isClientRunning)
        {
            Log("\n (CLIENT) Started running. Ready to send.");
            isClientRunning = true;
        }

        if (!osc.IsClientRunning && isClientRunning)
        {
            Log("\n (CLIENT) Stopped running.");
            isClientRunning = false;
        }
    }

    public void SendOutgoingMessage(string message)
    {
        if (isClientRunning)
        {
            // osc.SendMessage(clientAddress, message);
            Log($"\n (CLIENT) Sent message: {message}");
        }
    }

    /// <summary>
    /// Formats and sends standard OSC Float data (used for Motion/FFB to C2O)
    /// </summary>
    public void SendOSCFloat(string address, float value)
    {
     //   if (isClientRunning)
     //   {
            osc.SendMessage(address, value);
     //   }
    }
}