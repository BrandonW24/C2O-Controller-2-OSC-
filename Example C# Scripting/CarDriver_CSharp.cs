using ML.SDK;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class CarDriver_CSharp : MonoBehaviour
{
    // Constants
    private const float NEAR_ZERO = 0.01f;
    private const float ENGINE_IDLE_RPM = 800f;
    private const float ENGINE_MAX_RPM = 8000f;

    // Gearbox values (now local variables instead of a class)
    private float[] gearRatios = { 3f, 3f, 2f, 1.5f, 1f, 0.5f };
    private float gearFactor = 3.75f;
    private float diffRatio = 0.65f;
    private int currentGear = 2;
    private const int MIN_GEAR_INDEX = 2;
    private const int MAX_GEAR_INDEX = 6;
    private const int REVERSE_GEAR_INDEX = 1;

    // Engine state
    public enum EngineState
    {
        Off,
        Startup,
        Idle,
        Acceleration
    }

    
    public class CarEngine
    {
        public float rpm = 0;
        public EngineState state = EngineState.Off;
        public float startTime = 0;

        public void CalculateCurrentEngineState(float throttle, float speed, WheelCollider wheelBL, CarDriver_CSharp car)
        {
            if (state == EngineState.Off || state == EngineState.Startup)
            {
                rpm = 0;
                if (state == EngineState.Startup && Time.time - startTime > 3)
                {
                    state = EngineState.Idle;
                }
                return;
            }

            float wheelRPM = (speed * 60) / (2 * Mathf.PI * wheelBL.radius);
            wheelRPM = Mathf.Abs(wheelRPM) * ((car.gearFactor * car.gearRatios[car.currentGear - 1]) + car.diffRatio);

            if (wheelRPM > ENGINE_MAX_RPM * 0.7f)
            {
                if (car.currentGear < MAX_GEAR_INDEX)
                {
                    car.currentGear++;
                }
            }
            if (wheelRPM < ENGINE_MAX_RPM * 0.5f)
            {
                if (car.currentGear > MIN_GEAR_INDEX)
                {
                    car.currentGear--;
                }
            }

            float targetRPM = wheelRPM + Mathf.Lerp(0, ENGINE_MAX_RPM / 16, Mathf.Abs(throttle));
            rpm = Mathf.MoveTowards(rpm, targetRPM, 100);

            if (throttle <= 0.05f)
            {
                state = EngineState.Idle;
            }
            else
            {
                state = EngineState.Acceleration;
            }

            rpm = Mathf.Clamp(rpm, ENGINE_IDLE_RPM, ENGINE_MAX_RPM);
        }

        public float GetEngineTorque(float engineRPM)
        {
            float rpm = Mathf.Clamp(engineRPM / ENGINE_MAX_RPM, 0, 1);
            return Mathf.Clamp(Mathf.Sin(Mathf.Sqrt(rpm) * Mathf.PI) * 0.58f + Mathf.Sin(rpm * rpm * Mathf.PI) * 0.58f + 0.095f, 0, 1);
        }
    }

    /* Newer, seems to create lag?
    public class CarEngine
    {
        public float rpm = 0;
        public EngineState state = EngineState.Off;
        public float startTime = 0;

        // Add a cooldown timer to prevent frame-perfect oscillation
        private float lastShiftTime = 0f;
        private const float SHIFT_COOLDOWN = 1.5f; // 0.5 seconds between gear shifts

        public void CalculateCurrentEngineState(float throttle, float speed, WheelCollider wheelBL, CarDriver_CSharp car)
        {
            if (state == EngineState.Off || state == EngineState.Startup)
            {
                rpm = 0;
                if (state == EngineState.Startup && Time.time - startTime > 3)
                {
                    state = EngineState.Idle;
                }
                return;
            }

            float wheelRPM = (speed * 60) / (2 * Mathf.PI * wheelBL.radius);
            wheelRPM = Mathf.Abs(wheelRPM) * ((car.gearFactor * car.gearRatios[car.currentGear - 1]) + car.diffRatio);

            // Widen the thresholds: Up-shift at 85%, Down-shift at 40%
            // and check against the shift cooldown timer
            if (Time.time - lastShiftTime > SHIFT_COOLDOWN)
            {
                if (wheelRPM > ENGINE_MAX_RPM * 0.85f) // Increased from 0.7f
                {
                    if (car.currentGear < MAX_GEAR_INDEX)
                    {
                        car.currentGear++;
                        lastShiftTime = Time.time;
                    }
                }
                else if (wheelRPM < ENGINE_MAX_RPM * 0.40f) // Decreased from 0.5f
                {
                    if (car.currentGear > MIN_GEAR_INDEX)
                    {
                        car.currentGear--;
                        lastShiftTime = Time.time;
                    }
                }
            }

            float targetRPM = wheelRPM + Mathf.Lerp(0, ENGINE_MAX_RPM / 16, Mathf.Abs(throttle));
            rpm = Mathf.MoveTowards(rpm, targetRPM, 100);

            if (throttle <= 0.05f)
            {
                state = EngineState.Idle;
            }
            else
            {
                state = EngineState.Acceleration;
            }

            rpm = Mathf.Clamp(rpm, ENGINE_IDLE_RPM, ENGINE_MAX_RPM);
        }

        public float GetEngineTorque(float engineRPM)
        {
            float rpm = Mathf.Clamp(engineRPM / ENGINE_MAX_RPM, 0, 1);
            return Mathf.Clamp(Mathf.Sin(Mathf.Sqrt(rpm) * Mathf.PI) * 0.58f + Mathf.Sin(rpm * rpm * Mathf.PI) * 0.58f + 0.095f, 0, 1);
        }
    }
    */

    public CarEngine carEngine = new CarEngine();

    // Serialized fields
    public WheelCollider wheelFR;
    public WheelCollider wheelFL;
    public WheelCollider wheelBR;
    public WheelCollider wheelBL;

    public Rigidbody Car_RB;
    public GameObject steeringWheel;
    public GameObject speedDial;
    public GameObject engineRPMDial;
    public GameObject localControlIndicator;
    public Transform syncObject;
    public Transform centerOfMass;
    public MLStation station;

    public float THROTTLE_FACTOR = 0.1f;
    public float STEERING_FACTOR = 0.1f;
    public float MOTOR_MAX_TORQUE = 2000f;
    public float STEERING_ANGLE_MAX = 30f;
    public float MAX_BREAK_TORQUE = 1000f;
    public bool useAckerman = true;
    public bool isAllWheelDrive = true;
    [Range(0f, 1f)]
    public float awdFrontBias = 0.4f; // 40% front, 60% rear is a standard performance split
    [Range(0f, 1f)]
    public float steeringPowerReduction = 0.5f; // Reduce front power by up to 50% at full steering lock
    public float handBreakTorque = 1000f;
    public bool useCurvedSteeringInVR = true;

    public bool isAllWheelSteering = false;
    [Tooltip("Negative values turn opposite to front (tight turns). Positive values turn same way (crab walk).")]
    [Range(-1f, 1f)]
    public float rearSteeringRatio = -0.5f; // -0.5 means rear wheels turn half as much, in the opposite direction

    public GameObject minimapMarker;

    public AudioSource engineStartupAudio;
    public AudioSource engineIdleAudio;
    public AudioSource engineAccelerationAudio;
    public AudioSource engineDriveAudio;

    public AudioSource windAudio;
    public AudioClip impact;

    public Text gearIndicator;
    public Text debugText;

    public GameObject directionIndicator;
    public TextMeshPro nextGateText; // Optional text display
    private int currentTargetGate = 1;

    // Internal variables
    private int direction = 1;
    private float throttle = 0;
    private float steering = 0;
    private float breaks = 0;
    private float speed = 0;
    private float speedSMA = 0;
    private int speedSMASamplesSize = 10;
    private float[] previousSpeeds = new float[10];
    private float wheelBase = 0;
    private float track = 0;
    private float turningRadius = 0;
    private bool underLocalPlayerControl = false;
    private Vector3 lastPost = Vector3.zero;
    public MLPlayer currentDriver;
    public SkinnedMeshRenderer Decal_1;
    public MeshRenderer Decal_2;
    public GameObject WindObject;

    public GameObject RacingManagerOBJ;
    //   public RacingGameManager RacingGameManagerReference;

    public bool useSteeringWheelforVR;
    public GameObject steeringwheel_VR;
    public MLWheel steeringWheel_script;

    public bool useOSCWheelforInput;
    public MLWheelOSC oscWheelScript; // NEW: Reference to the OSC script
    public GameObject OSCWheelobject;
    public string ownerName;

    [Header("Custom Gravity")]
    public bool useCustomGravity = true;
    [Tooltip("1.0 is Earth gravity (9.81 m/s^2). Mars is approximately 0.38.")]
    [Range(0f, 3f)]
    public float gravityMultiplier = 0.38f;

    // NEW: Reference to hold the currently active Mars Fact UI
    public GameObject CurrentMarsFact;


    // NEW Motion Telemetry Tracking
    private Vector3 lastLocalVelocity = Vector3.zero;
    private float lastYawAngle = 0f;
    private WheelController[] wheelControllers; // Cache to read slip data
    private WheelController wcFR_Script;
    private WheelController wcFL_Script;
    private WheelController wcBR_Script;
    private WheelController wcBL_Script;

    // NEW: Delta Threshold Tracking to prevent network flooding
    private float lastPitch, lastRoll, lastYaw, lastSurge, lastSway, lastHeave;
    private float lastSpring, lastDamper, lastRumble;
    private const float MOTION_THRESHOLD = 0.005f; // 0.5% change required
    private const float FFB_THRESHOLD = 0.5f;      // 0.5 unit change required

    // New audio telemetry for buttkicker support experimental
    //*********************************************************************
    /*
    [Header("Tactile/Audio Telemetry")]
    public bool sendAudioTelemetry = true;
    private float[] audioSamples = new float[64]; // Small buffer for low-latency RMS calculation
    private float lastAudioEngine, lastAudioWind;

    private float GetAudioRMS(AudioSource source)
    {
        if (source == null || !source.isPlaying) return 0f;

        // Grab a small block of samples from the active audio source
        source.GetOutputData(audioSamples, 0);
        float sum = 0;
        for (int i = 0; i < audioSamples.Length; i++)
        {
            sum += audioSamples[i] * audioSamples[i];
        }
        return Mathf.Sqrt(sum / audioSamples.Length);
    }
    */
    //*********************************************************************

    private void Start()
    {
        Debug.Log("Checking if car is alive...");
        wheelBase = Vector3.Distance(wheelBL.transform.position, wheelFR.transform.position);
        track = Vector3.Distance(wheelFL.transform.position, wheelFR.transform.position);
        float betaAngleRadian = (180 - (STEERING_ANGLE_MAX + 90)) * Mathf.Deg2Rad;
        turningRadius = (Mathf.Abs(Mathf.Tan(betaAngleRadian) * wheelBase)) + (track / 2);

        Car_RB.centerOfMass = centerOfMass.localPosition;

        // Cache the wheel controllers using non-generic GetComponent
        if (wheelFR != null) wcFR_Script = wheelFR.gameObject.GetComponent(typeof(WheelController)) as WheelController;
        if (wheelFL != null) wcFL_Script = wheelFL.gameObject.GetComponent(typeof(WheelController)) as WheelController;
        if (wheelBR != null) wcBR_Script = wheelBR.gameObject.GetComponent(typeof(WheelController)) as WheelController;
        if (wheelBL != null) wcBL_Script = wheelBL.gameObject.GetComponent(typeof(WheelController)) as WheelController;

        if (useCustomGravity && Car_RB != null)
        {
            Car_RB.useGravity = false;
        }

        if (useSteeringWheelforVR == true)
        {
            steeringWheel_script = steeringwheel_VR.GetComponent(typeof(MLWheel)) as MLWheel;
        }

        // debugText.text += " attempting to get oscwheel script ";
        if (OSCWheelobject != null)
        {
            oscWheelScript = OSCWheelobject.GetComponent(typeof(MLWheelOSC)) as MLWheelOSC;


        }
        // debugText.text += $" oscwheel script : {oscWheelScript}";


        //  RacingGameManagerReference = RacingManagerOBJ.GetComponent(typeof(RacingGameManager)) as RacingGameManager;


        for (int i = 0; i < speedSMASamplesSize; i++)
        {
            previousSpeeds[i] = 0;
        }

        lastPost = transform.position;

        station.OnPlayerSeated.AddListener(OnPlayerEnterStation);
        station.OnPlayerLeft.AddListener(OnPlayerLeftStation);

        if (station.IsOccupied)
        {
            carEngine.state = EngineState.Idle;
        }

        engineIdleAudio.Play();
        engineAccelerationAudio.Play();
        engineDriveAudio.Play();
        windAudio.Play();
    }

    private void Update()
    {
        if (station.IsOccupied)
        {
            var player = station.GetPlayer();
            if (player.IsLocal)
            {
                Car_RB.drag = 0;
                Car_RB.angularDrag = 0;

                underLocalPlayerControl = true;
                var input = station.GetStationInput();
                if (direction == 0)
                {
                    direction = 1;
                }

                if (MassiveLoopClient.IsInDesktopMode)
                {
                    HandleDesktopModeInput(input);
                }
                else
                {
                    HandleVRModeInput(input);
                }
            }
            else
            {
                underLocalPlayerControl = false;
            }
        }
        else
        {
            underLocalPlayerControl = false;
            Car_RB.velocity = Vector3.zero;
            Car_RB.angularVelocity = Vector3.zero;
            Car_RB.drag = 15;
            Car_RB.angularDrag = 30;
        }

        if (underLocalPlayerControl)
        {
            syncObject.localPosition = new Vector3(throttle, steering, direction);
        }
        else
        {
            var localPos = syncObject.localPosition;
            throttle = localPos.x;
            steering = localPos.y;
            direction = (int)localPos.z;
        }

        steeringWheel.transform.localRotation = Quaternion.Euler(-steering * STEERING_ANGLE_MAX * 4, -90, 0);
        speedDial.transform.localRotation = Quaternion.Euler(0, Mathf.LerpUnclamped(-42, 71, Mathf.Abs(speedSMA / 100) * 2.23694f), 0);
        engineRPMDial.transform.localRotation = Quaternion.Euler(0, Mathf.Lerp(-135, 135, Mathf.InverseLerp(0, ENGINE_MAX_RPM, carEngine.rpm)), 0);

        gearIndicator.text = direction < 0 ? "R" : $"D{currentGear - 1}";
        //        UpdateDirectionIndicator();


    }

    /*

    private void UpdateDirectionIndicator()
    {
        if (station.IsOccupied && RacingGameManagerReference != null)
        {
            // Get player progress from GameManager
            string playerName = station.GetPlayer().NickName;
            int lastGate = RacingGameManagerReference.GetPlayerLastGate(playerName);
            currentTargetGate = (lastGate % RacingGameManagerReference.TotalGates) + 1;

            // Point toward next gate
            if (directionIndicator != null)
            {
                Transform targetGate = RacingGameManagerReference.GetGateTransform(currentTargetGate);
                if (targetGate != null)
                {
                    directionIndicator.transform.LookAt(targetGate);
                    // Keep the arrow horizontal (only rotate on Y axis)
                    directionIndicator.transform.rotation = Quaternion.Euler(
                        0,
                        directionIndicator.transform.eulerAngles.y,
                        0
                    );
                }
            }

            // Update text if available
            if (nextGateText != null)
            {
                nextGateText.text = $"Gate: {currentTargetGate}";
            }
        }
    }
    */

    private void FixedUpdate()
    {
        // Add custom gravity continuous force
        if (useCustomGravity && Car_RB != null)
        {
            Car_RB.AddForce(Physics.gravity * gravityMultiplier, ForceMode.Acceleration);
        }

        if (underLocalPlayerControl)
        {
            speed = (wheelFL.rpm * wheelFL.radius * Mathf.PI * 2) * 0.06f;

            // --- OSC OUTBOUND TELEMETRY (MOTION & FFB) ---
            if (oscWheelScript != null && oscWheelScript.isClientRunning)
            {
                Vector3 localVelocity = transform.InverseTransformDirection(Car_RB.velocity);
                Vector3 acceleration = (localVelocity - lastLocalVelocity) / Time.fixedDeltaTime;
                lastLocalVelocity = localVelocity;

                if (oscWheelScript.sendMotionTelemetry)
                {
                    float surge = Mathf.Clamp(acceleration.z / 20f, -1f, 1f);
                    float sway = Mathf.Clamp(acceleration.x / 20f, -1f, 1f);
                    float heave = Mathf.Clamp(acceleration.y / 20f, -1f, 1f);

                    float pitchAngle = transform.eulerAngles.x;
                    if (pitchAngle > 180) pitchAngle -= 360;
                    float pitch = Mathf.Clamp(pitchAngle / 45f, -1f, 1f);

                    float rollAngle = transform.eulerAngles.z;
                    if (rollAngle > 180) rollAngle -= 360;
                    float roll = Mathf.Clamp(rollAngle / 45f, -1f, 1f);

                    float yawAngle = transform.eulerAngles.y;
                    float yawRate = (yawAngle - lastYawAngle) / Time.fixedDeltaTime;
                    if (yawRate > 180) yawRate -= 360;
                    if (yawRate < -180) yawRate += 360;
                    lastYawAngle = yawAngle;
                    float yaw = Mathf.Clamp(yawRate / 90f, -1f, 1f);

                    // DELTA FILTER: Only send if the value changed enough to matter
                    if (Mathf.Abs(pitch - lastPitch) > MOTION_THRESHOLD) { oscWheelScript.SendOSCFloat("/motion/pitch", pitch); lastPitch = pitch; }
                    if (Mathf.Abs(roll - lastRoll) > MOTION_THRESHOLD) { oscWheelScript.SendOSCFloat("/motion/roll", roll); lastRoll = roll; }
                    if (Mathf.Abs(yaw - lastYaw) > MOTION_THRESHOLD) { oscWheelScript.SendOSCFloat("/motion/yaw", yaw); lastYaw = yaw; }
                    if (Mathf.Abs(surge - lastSurge) > MOTION_THRESHOLD) { oscWheelScript.SendOSCFloat("/motion/surge", surge); lastSurge = surge; }
                    if (Mathf.Abs(sway - lastSway) > MOTION_THRESHOLD) { oscWheelScript.SendOSCFloat("/motion/sway", sway); lastSway = sway; }
                    if (Mathf.Abs(heave - lastHeave) > MOTION_THRESHOLD) { oscWheelScript.SendOSCFloat("/motion/heave", heave); lastHeave = heave; }
                }

                if (oscWheelScript.sendFFBTelemetry)
                {
                    float speedNorm = Mathf.Clamp01(Mathf.Abs(speedSMA) / 30f);

                    float springForce = speedNorm * 100f;
                    float damperForce = speedNorm * 50f;

                    float engineRumble = Mathf.InverseLerp(ENGINE_IDLE_RPM, ENGINE_MAX_RPM, carEngine.rpm) * 25f;

                    float maxSlip = 0f;
                    if (wcFR_Script != null && wcFR_Script.currentSlip > maxSlip) maxSlip = wcFR_Script.currentSlip;
                    if (wcFL_Script != null && wcFL_Script.currentSlip > maxSlip) maxSlip = wcFL_Script.currentSlip;
                    if (wcBR_Script != null && wcBR_Script.currentSlip > maxSlip) maxSlip = wcBR_Script.currentSlip;
                    if (wcBL_Script != null && wcBL_Script.currentSlip > maxSlip) maxSlip = wcBL_Script.currentSlip;

                    float slipRumble = Mathf.Clamp01(maxSlip) * 100f;
                    float finalRumble = Mathf.Max(engineRumble, slipRumble);

                    // DELTA FILTER: Only send if the FFB changes by at least 0.5 units
                    if (Mathf.Abs(springForce - lastSpring) > FFB_THRESHOLD) { oscWheelScript.SendOSCFloat("/ffb/spring", springForce); lastSpring = springForce; }
                    if (Mathf.Abs(damperForce - lastDamper) > FFB_THRESHOLD) { oscWheelScript.SendOSCFloat("/ffb/damper", damperForce); lastDamper = damperForce; }
                    if (Mathf.Abs(finalRumble - lastRumble) > FFB_THRESHOLD) { oscWheelScript.SendOSCFloat("/ffb/rumble", finalRumble); lastRumble = finalRumble; }
                }


                // NEW: Tactile Audio Envelope Telemetry
                /* Experimental
                if (sendAudioTelemetry)
                {
                    // Combine drive and acceleration envelopes for the engine
                    float audioEngine = Mathf.Clamp01(GetAudioRMS(engineDriveAudio) + GetAudioRMS(engineAccelerationAudio));
                    float audioWind = Mathf.Clamp01(GetAudioRMS(windAudio));

                    // DELTA FILTER: Only send if the envelope changes enough
                    if (Mathf.Abs(audioEngine - lastAudioEngine) > MOTION_THRESHOLD)
                    {
                        oscWheelScript.SendOSCFloat("/audio/engine", audioEngine);
                        lastAudioEngine = audioEngine;
                    }
                    if (Mathf.Abs(audioWind - lastAudioWind) > MOTION_THRESHOLD)
                    {
                        oscWheelScript.SendOSCFloat("/audio/wind", audioWind);
                        lastAudioWind = audioWind;
                    }
                }
                */
            }
        }
        else
        {
            speed = Vector3.Distance(lastPost, transform.position) / Time.fixedDeltaTime;
            lastPost = transform.position;
        }

        previousSpeeds[Time.frameCount % speedSMASamplesSize] = speed;

        float samplesSum = 0;
        foreach (var value in previousSpeeds)
        {
            samplesSum += value;
        }

        speedSMA = samplesSum / speedSMASamplesSize;

        localControlIndicator.SetActive(underLocalPlayerControl);

        /* disabled to help performance
        if (oscWheelScript != null)
        {
            debugText.text = $"Engine RPM: {carEngine.rpm}\nCurrent Gear: {currentGear}\nThrottle: {throttle:F3}\nDirection: {direction}\nEngine State: {carEngine.state}\n OSC Steering Value : {oscWheelScript.currentOSCSteeringValue}\n OSC gas value {oscWheelScript.currentOSCGasValue}\n OSC brake value : {oscWheelScript.currentOSCBrakeValue}  ";
        }
        else
        {
            debugText.text = $"Engine RPM: {carEngine.rpm}\nCurrent Gear: {currentGear}\nThrottle: {throttle:F3}\nDirection: {direction}\nEngine State: {carEngine.state}\n ";
        }
        */

        if (underLocalPlayerControl)
        {
            if (speedSMA > 1 && ((direction > 0 && speedSMA < 0) || (direction < 0 && speedSMA > 0)))
            {
                direction = -direction;
            }

            if (breaks < NEAR_ZERO)
            {
                wheelFL.brakeTorque = 0;
                wheelFR.brakeTorque = 0;
                wheelBL.brakeTorque = 0;
                wheelBR.brakeTorque = 0;

                if ((throttle > 0 && direction > 0) || (throttle < 0 && direction < 0))
                {
                    float rpmRelativeT = carEngine.GetEngineTorque(carEngine.rpm);

                    float divisor = isAllWheelDrive ? 4f : 2f;
                    float wheelTorque = throttle * rpmRelativeT * MOTOR_MAX_TORQUE * (gearFactor * gearRatios[currentGear - 1]) / divisor;

                    wheelBL.motorTorque = wheelTorque;
                    wheelBR.motorTorque = wheelTorque;

                    if (isAllWheelDrive)
                    {
                        wheelFL.motorTorque = wheelTorque;
                        wheelFR.motorTorque = wheelTorque;
                    }
                    else
                    {
                        wheelFL.motorTorque = 0;
                        wheelFR.motorTorque = 0;
                    }
                }
            }
            else
            {
                float breaking = breaks * MAX_BREAK_TORQUE + speedSMA * 2;
                wheelFL.brakeTorque = breaking;
                wheelFR.brakeTorque = breaking;
                wheelBL.brakeTorque = breaking / 2;
                wheelBR.brakeTorque = breaking / 2;
            }
        }
        else
        {
            wheelBL.brakeTorque = 0;
            wheelBR.brakeTorque = 0;
            wheelFL.brakeTorque = 0;
            wheelFR.brakeTorque = 0;

            wheelBL.motorTorque = 0;
            wheelBR.motorTorque = 0;
            wheelFL.motorTorque = 0;
            wheelFR.motorTorque = 0;
        }

        if (useAckerman)
        {
            float ackAngleLeft = 0;
            float ackAngleRight = 0;

            float halfTrack = track / 2;
            if (steering > 0)
            {
                ackAngleLeft = Mathf.Rad2Deg * Mathf.Atan(wheelBase / (turningRadius + halfTrack)) * steering / 2;
                ackAngleRight = Mathf.Rad2Deg * Mathf.Atan(wheelBase / (turningRadius - halfTrack)) * steering / 2;
            }
            else if (steering < 0)
            {
                ackAngleLeft = Mathf.Rad2Deg * Mathf.Atan(wheelBase / (turningRadius - halfTrack)) * steering / 2;
                ackAngleRight = Mathf.Rad2Deg * Mathf.Atan(wheelBase / (turningRadius + halfTrack)) * steering / 2;
            }

            wheelFR.steerAngle = ackAngleRight;
            wheelFL.steerAngle = ackAngleLeft;

            if (isAllWheelSteering)
            {
                wheelBR.steerAngle = ackAngleRight * rearSteeringRatio;
                wheelBL.steerAngle = ackAngleLeft * rearSteeringRatio;
            }
            else
            {
                wheelBR.steerAngle = 0;
                wheelBL.steerAngle = 0;
            }
        }
        else
        {
            float frontSteer = steering * STEERING_ANGLE_MAX;
            wheelFR.steerAngle = frontSteer;
            wheelFL.steerAngle = frontSteer;

            if (isAllWheelSteering)
            {
                wheelBR.steerAngle = frontSteer * rearSteeringRatio;
                wheelBL.steerAngle = frontSteer * rearSteeringRatio;
            }
            else
            {
                wheelBR.steerAngle = 0;
                wheelBL.steerAngle = 0;
            }
        }

        if (underLocalPlayerControl)
        {
            carEngine.CalculateCurrentEngineState(throttle, 2 * Mathf.PI * wheelBL.radius * (wheelBL.rpm / 60), wheelBL, this);
        }
        else
        {
            carEngine.CalculateCurrentEngineState(throttle, speedSMA, wheelBL, this);
        }

        HandleEngineAudioEffects();
        HandleOtherAudioEffects();
    }

    private void OnCollisionEnter(Collision collision)
    {
        if (collision != null)
        {
            float impactMag = collision.impulse.magnitude / 10f;

            /*
            var contact = collision.GetContact(0);
            if (contact != null)
            {
                AudioSource.PlayClipAtPoint(impact, contact.point, impactMag);
            }*/

            // NEW: Send instant impact spike for tactile feedback experimental
            /*
            if (sendAudioTelemetry && oscWheelScript != null && oscWheelScript.isClientRunning)
            {
                oscWheelScript.SendOSCFloat("/audio/impact", Mathf.Clamp01(impactMag));
            }
            */
        }
    }

    private void OnPlayerEnterStation()
    {
        engineStartupAudio.Play();
        carEngine.state = EngineState.Startup;
        carEngine.startTime = Time.time;

        Car_RB.drag = 0;
        Car_RB.angularDrag = 0;

        currentDriver = station.GetPlayer();
        Debug.Log($"Current driver : {currentDriver}");
        ownerName = currentDriver.NickName;

        // Load thumbnail with callback
        currentDriver.LoadPlayerThumbnail((texture) =>
        {
            if (texture != null)
            {
                Decal_1.material.mainTexture = texture;
                if (Decal_2 != null)
                {

                    Decal_2.material.mainTexture = texture;
                }
            }
            else
            {
                Debug.LogWarning("Failed to load player thumbnail");
            }
        });
    }

    private void OnPlayerLeftStation()
    {
        throttle = 0;
        wheelBL.brakeTorque = handBreakTorque;
        wheelBR.brakeTorque = handBreakTorque;
        wheelFL.brakeTorque = handBreakTorque;
        wheelFR.brakeTorque = handBreakTorque;

        breaks = 1;
        carEngine.state = EngineState.Off;

        Car_RB.velocity = Vector3.zero;
        Car_RB.angularVelocity = Vector3.zero;
        currentDriver = null;
    }

    private void HandleDesktopModeInput(UserStationInput input)
    {
        // --- 1. THROTTLE & BRAKES ---
        if (useOSCWheelforInput && oscWheelScript != null && oscWheelScript.sendInputs)
        {
            // Normalize -1 to 1 hardware inputs into a 0 to 1 range
            float normalizedGas = (oscWheelScript.currentOSCGasValue + 1f) / 2f;
            float normalizedBrake = (oscWheelScript.currentOSCBrakeValue + 1f) / 2f;

            // Subtract brake from gas to create a perfect -1 to 1 throttle value
            throttle = normalizedGas - normalizedBrake;

            // We MUST apply the VR-style direction and braking logic here so the pedals can reverse the car!
            if (direction > 0)
            {
                if (throttle > NEAR_ZERO)
                {
                    breaks = 0;
                }
                else
                {
                    breaks = Mathf.Abs(throttle);
                    throttle = 0;

                    if (speedSMA < 1)
                    {
                        direction = -direction;
                    }
                }
            }
            else if (direction < 0)
            {
                if (throttle > NEAR_ZERO)
                {
                    breaks = Mathf.Abs(throttle);
                    throttle = 0;

                    if (speedSMA > -1)
                    {
                        direction = -direction;
                    }
                }
                else
                {
                    if (throttle < -0.8f)
                    {
                        throttle = -0.8f;
                    }

                    breaks = 0;
                }
            }
        }
        else
        {
            // Existing Keyboard Throttle Logic
            if (input.KeyboardMove.y > 0.5f)
            {
                throttle += THROTTLE_FACTOR;
                throttle = Mathf.Clamp(throttle, -1, 1);

                if (direction > 0)
                {
                    if (throttle > 0)
                    {
                        breaks = 0;
                    }
                }
                else
                {
                    if (throttle > 0)
                    {
                        breaks += THROTTLE_FACTOR;
                    }

                    breaks = Mathf.Clamp(breaks, 0, 1);

                    if (speedSMA < NEAR_ZERO)
                    {
                        throttle = 0;
                        direction = -direction;
                    }
                }
            }
            else if (input.KeyboardMove.y < -0.5f)
            {
                if (direction > 0)
                {
                    throttle -= THROTTLE_FACTOR * 2;
                    if (throttle < 0)
                    {
                        breaks += THROTTLE_FACTOR;
                    }

                    throttle = Mathf.Clamp(throttle, -1, 1);
                    breaks = Mathf.Clamp(breaks, 0, 1);

                    if (speedSMA < 1)
                    {
                        throttle = 0;
                        direction = -direction;
                    }
                }
                else
                {
                    throttle -= THROTTLE_FACTOR;
                    if (throttle < 0)
                    {
                        breaks = 0;
                    }

                    throttle = Mathf.Clamp(throttle, -1, -0.8f);
                }
            }
            else
            {
                throttle = Mathf.MoveTowards(throttle, 0, THROTTLE_FACTOR);
            }
        }

        // --- 2. STEERING ---
        if (useOSCWheelforInput && oscWheelScript != null && oscWheelScript.sendInputs)
        {
            // Directly apply the clamped OSC value
            steering = Mathf.Clamp(oscWheelScript.currentOSCSteeringValue, -1f, 1f);
        }
        else
        {
            // Existing Keyboard Steering Logic
            if (input.KeyboardMove.x > 0.5f)
            {
                steering += STEERING_FACTOR;
            }
            else if (input.KeyboardMove.x < -0.5f)
            {
                steering -= STEERING_FACTOR;
            }
            else
            {
                steering = Mathf.MoveTowards(steering, 0, STEERING_FACTOR / 5);
            }
            steering = Mathf.Clamp(steering, -1, 1);
        }

        if (useSteeringWheelforVR && steeringWheel_script != null)
        {
            float targetAngle = steering * 90f;
            Quaternion targetRot = Quaternion.Euler(0, 0, targetAngle);
            steeringWheel_script.wheelVisual.localRotation = targetRot;
        }

        if (input.Jump)
        {
            wheelBL.brakeTorque = handBreakTorque;
            wheelBR.brakeTorque = handBreakTorque;
        }
        else
        {
            wheelBL.brakeTorque = 0;
            wheelBR.brakeTorque = 0;
        }
    }

    private void HandleVRModeInput(UserStationInput input)
    {
        // 1. Check for OSC Override FIRST
        if (useOSCWheelforInput && oscWheelScript != null && oscWheelScript.sendInputs)
        {
            // --- OSC Throttle & Brake ---
            // Normalize -1 to 1 hardware inputs into a 0 to 1 range
            float normalizedGas = (oscWheelScript.currentOSCGasValue + 1f) / 2f;
            float normalizedBrake = (oscWheelScript.currentOSCBrakeValue + 1f) / 2f;
            throttle = normalizedGas - normalizedBrake;

            // --- OSC Steering ---
            // Direct assignment. We remove Mathf.MoveTowards here to stop the jittering 
            // and give 1:1 responsiveness with the hardware wheel.
            steering = Mathf.Clamp(oscWheelScript.currentOSCSteeringValue, -1f, 1f);

            // Visually sync the VR steering wheel prop to the OSC input so it matches the real world
            if (useSteeringWheelforVR && steeringWheel_script != null)
            {
                float targetAngle = steering * 90f; // Adjust the 90f multiplier based on your wheel's max rotation
                steeringWheel_script.wheelVisual.localRotation = Quaternion.Euler(0, 0, targetAngle);
            }
        }
        else
        {
            // 2. Standard VR Input Fallback (Controllers or Virtual Wheel)
            throttle = input.RightTrigger - input.LeftTrigger;

            float steeringTarget = 0;

            if (useSteeringWheelforVR && steeringWheel_script != null)
            {
                // Calculate rotation from the interactable VR wheel
                Quaternion localRot = Quaternion.Inverse(steeringWheel_script.pivotPoint.rotation) * steeringWheel_script.wheelVisual.rotation;
                float wheelAngle = localRot.eulerAngles.z;
                if (wheelAngle > 180) wheelAngle -= 360;
                steeringTarget = Mathf.Clamp(wheelAngle / 90f, -1f, 1f);
            }
            else if (useCurvedSteeringInVR)
            {
                steeringTarget = Mathf.Tan(input.RightControl.x / 0.685f) * 0.113f;
            }
            else
            {
                steeringTarget = input.RightControl.x;
            }

            // MoveTowards is perfectly fine here to smooth out VR thumbsticks and hand tracking
            steering = Mathf.MoveTowards(steering, steeringTarget, 1.5f * Time.deltaTime);
        }

        // 3. Universal Direction & Braking Logic (Applies to both OSC and VR inputs)
        if (direction > 0)
        {
            if (throttle > NEAR_ZERO)
            {
                breaks = 0;
            }
            else
            {
                breaks = Mathf.Abs(throttle);
                throttle = 0;

                if (speedSMA < 1)
                {
                    direction = -direction;
                }
            }
        }
        else if (direction < 0)
        {
            if (throttle > NEAR_ZERO)
            {
                breaks = Mathf.Abs(throttle);
                throttle = 0;

                if (speedSMA > -1)
                {
                    direction = -direction;
                }
            }
            else
            {
                if (throttle < -0.8f)
                {
                    throttle = -0.8f;
                }

                breaks = 0;
            }
        }
    }

    private void HandleEngineAudioEffects()
    {
        if (carEngine.state == EngineState.Off)
        {
            engineAccelerationAudio.volume = 0;
            engineDriveAudio.volume = 0;
            engineIdleAudio.volume = 0;
        }
        else if (carEngine.state == EngineState.Startup)
        {
            engineAccelerationAudio.volume = 0;
            engineDriveAudio.volume = 0;
            engineIdleAudio.volume = 0;
        }
        else
        {
            float trueThrottle = 0;
            if (direction >= 0 && throttle >= 0)
            {
                trueThrottle = Mathf.Clamp(throttle, 0, 1);
            }
            else if (direction < 0 && throttle < 0)
            {
                trueThrottle = Mathf.Clamp(Mathf.Abs(throttle), 0, 1);
            }

            float rpmFactor = Mathf.InverseLerp(ENGINE_IDLE_RPM, ENGINE_MAX_RPM, carEngine.rpm);

            engineAccelerationAudio.volume = trueThrottle;
            engineAccelerationAudio.pitch = Mathf.Lerp(0.8f, 1.5f, rpmFactor);

            engineIdleAudio.pitch = Mathf.Lerp(1, 1.8f, rpmFactor + throttle);
            engineIdleAudio.volume = Mathf.Lerp(1, 0, rpmFactor);

            engineDriveAudio.pitch = Mathf.Lerp(0.5f, 1.5f, rpmFactor);
            engineDriveAudio.volume = Mathf.Lerp(0.2f, 1, rpmFactor);
        }
    }

    private void HandleOtherAudioEffects()
    {
        float speedFactor = Mathf.InverseLerp(0, 50, speedSMA);
        windAudio.volume = Mathf.Lerp(0, 1, speedFactor);
        windAudio.pitch = Mathf.Lerp(1, 1.3f, speedFactor);
    }
}