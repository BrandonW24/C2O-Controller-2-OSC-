using ML.SDK;
using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;

public class HoveringSpaceJet : MonoBehaviour
{
    // Constants
    private const float ENGINE_IDLE_THRUST = 0.1f;
    private const float ENGINE_MAX_THRUST = 1f;

    // Engine state
    public enum EngineState
    {
        Off,
        Startup,
        Idle,
        Acceleration
    }

    public class JetEngine
    {
        public float thrust = 0;
        public EngineState state = EngineState.Off;
        public float startTime = 0;

        public void CalculateCurrentEngineState(float throttleInput)
        {
            if (state == EngineState.Off || state == EngineState.Startup)
            {
                thrust = 0;
                if (state == EngineState.Startup && Time.time - startTime > 3)
                {
                    state = EngineState.Idle;
                }
                return;
            }

            float targetThrust = Mathf.Lerp(ENGINE_IDLE_THRUST, ENGINE_MAX_THRUST, Mathf.Abs(throttleInput));
            thrust = Mathf.MoveTowards(thrust, targetThrust, 0.5f * Time.deltaTime);

            if (throttleInput <= 0.05f)
            {
                state = EngineState.Idle;
            }
            else
            {
                state = EngineState.Acceleration;
            }

            thrust = Mathf.Clamp(thrust, ENGINE_IDLE_THRUST, ENGINE_MAX_THRUST);
        }
    }

    public JetEngine jetEngine = new JetEngine();

    // Serialized fields
    public Rigidbody Jet_RB;
    public GameObject throttleControl;
    public GameObject joystickControl;
    public GameObject speedDial;
    public GameObject thrustDial;
    public Transform centerOfMass;
    public MLStation station;

    [Header("Control Settings")]
    public float throttleSensitivity = 0.1f;
    public float pitchSensitivity = 0.5f;
    public float yawSensitivity = 0.8f;
    public float rollSensitivity = 0.4f;
    public float maxTorque = 3000f;
    public float stabilityForce = 1500f;
    public float stabilitySpeed = 3f;
    public bool useJoystickControl = true;

    [Header("Throttle Settings")]
    public float throttleReturnSpeed = 3f;
    public float throttleDeadzone = 0.15f;

    [Header("Space Flight Settings (Zero-G)")]
    [Tooltip("Automatically counteracts any scene gravity so the ship doesn't fall")]
    public bool counteractGravity = true;
    [Tooltip("How strongly the ship brakes to a halt when no input is given")]
    public float inertialDampening = 2.5f;

    [Header("Thrust Settings")]
    public float thrustPrecision = 1.2f;
    public float thrustAlignmentSpeed = 5f;
    private Vector3 currentThrustDirection;

    [Header("Camera Steering Settings")]
    public float shipTurnSpeed = 1.5f;
    public float pitchResponseSpeed = 1.2f;
    public float yawResponseSpeed = 0.8f;
    public float maxPitchAngle = 45f;
    public float maxRollAngle = 30f;
    public float rollDamping = 0.6f;
    public float pitchLookInfluence = 0.7f;
    public GameObject ThirdPersonCamera;
    private bool isInThirdPerson = false;
    private Vector3 originalStationPosition;
    private Quaternion originalStationRotation;
    private Transform originalParent;
    private float toggleCooldown = 0.5f;
    private float lastToggleTime = 0f;

    [Header("Lateral Movement Settings")]
    public float lateralMovementSpeed = 15f;
    public float lateralMovementDamping = 2.5f;
    private float lateralMovementInput = 0f;

    [Header("Vertical Movement Settings")]
    public float verticalMoveSpeed = 15f;
    public float verticalDamping = 2.5f;
    private float verticalMovementInput = 0f;

    [Header("OSC Control & Telemetry")]
    public bool enableOSC = false;
    public string oscAddressPattern = "/spacejet";
    public bool sendMotionTelemetry = true;

    [Tooltip("Boosts hardware inputs to make the yoke and sliders more responsive against ship drag")]
    public float oscInputMultiplier = 3.0f;

    [Tooltip("Map hardware axes indices to flight controls")]
    public int oscAxisRoll = 0;
    public int oscAxisPitch = 1;
    public int oscAxisThrottle = 2;
    public int oscAxisYaw = 3;
    public int oscAxisLateral = 4;
    public int oscAxisVertical = 5;

    [Tooltip("Map hardware button indices to jet actions")]
    public int oscBtnPrimaryFire = 0;
    public int oscBtnSecondaryFire = 1;
    public int oscBtnToggleCamera = 2;
    public int oscBtnReset = 16;

    private OSC osc = new OSC();
    private bool isOscBound = false;

    // OSC Input States
    private float oscPitch, oscRoll, oscYaw, oscThrottle, oscLateral, oscVertical;
    private bool oscPrimaryFirePressed;
    private bool oscSecondaryFirePressed;

    // OSC Motion Telemetry States
    private Vector3 lastLocalVelocity = Vector3.zero;
    private float lastYawAngle = 0f;
    private float lastPitchTele, lastRollTele, lastYawTele, lastSurge, lastSway, lastHeave;
    private const float MOTION_THRESHOLD = 0.005f;

    [Header("Audio")]
    public AudioSource engineStartupAudio;
    public AudioSource engineIdleAudio;
    public AudioSource engineAccelerationAudio;
    public AudioSource hoverAudio; // Now used for internal inertial dampener sounds
    public AudioClip[] ShipSounds;

    [Header("UI")]
    public TextMeshPro debugText;
    public GameObject localControlIndicator;
    public Transform syncObject;
    public TextMeshPro weaponStatusText;

    [Header("Physics Settings")]
    public float jetMass = 1500f;
    public float thrustForceMultiplier = 25f;

    [Header("Weapons Settings")]
    public GameObject LeftWeapon;
    public GameObject RightWeapon;
    public Transform Left_WeaponShootPoint;
    public Transform Right_WeaponShootPoint;
    public GameObject Weapon_Projectile;
    public GameObject MuzzleFireEffect;
    public float weaponRange = 100f;
    public float weaponProjectileSpeed = 50f;
    private Transform playerCamera;

    public Transform SecondaryWeaponSystemShootPoint;
    public GameObject SecondaryWeaponSystem_Projectile;
    public GameObject SecondaryWeaponSystem_MuzzleFireEffect;
    public Transform SecondaryWeaponSystemShootPoint_2;

    // Primary Weapon System
    public float primaryWeaponCooldown = 0.2f;
    private float lastPrimaryFireTime;

    // Secondary Weapon System
    public float secondaryWeaponCooldown = 1.0f;
    private float lastSecondaryFireTime;

    [Header("Weapon Constraints")]
    public float maxHorizontalAngle = 45f;
    public float maxVerticalAngle = 30f;
    public float weaponTurnSpeed = 5f;

    [Header("Health Settings")]
    public int ArmorPoints = 1500;
    public int LeftWing_ArmorPoints = 500;
    public int RightWing_ArmorPoints = 500;
    public int MainEngine_ArmorPoints = 350;

    // Internal variables
    private float throttle = 0;
    private float pitch = 0;
    private float yaw = 0;
    private float roll = 0;
    private bool underLocalPlayerControl = false;
    public MLPlayer currentPilot;
    private float maxThrustForce;

    // Network & Reset Variables
    private const string EVENT_SHIP_SHOOT = "ShipShootEvent";
    private EventToken tokenShipShoot;

    private const string EVENT_RESET_JET = "ResetJetEvent";
    private EventToken tokenResetJet;
    private Vector3 initialJetPosition;
    private Quaternion initialJetRotation;

    void Start()
    {
        Debug.Log("Start SpaceJet");
        InitializePhysics();

        // Store starting transforms for the reset logic
        initialJetPosition = transform.position;
        initialJetRotation = transform.rotation;

        tokenShipShoot = this.AddEventHandler(EVENT_SHIP_SHOOT, OnShipShootEvent);
        tokenResetJet = this.AddEventHandler(EVENT_RESET_JET, OnResetJetEvent);

        station.OnPlayerSeated.AddListener(OnPlayerEnterStation);
        station.OnPlayerLeft.AddListener(OnPlayerLeftStation);

        // Bind OSC
        if (enableOSC)
        {
            isOscBound = osc.TryBindAddressPattern(oscAddressPattern, OnOSCMessage);
            if (isOscBound) Debug.Log($"SpaceJet OSC Successfully Bound to: {oscAddressPattern}");
            else Debug.LogError($"SpaceJet OSC Failed to Bind to: {oscAddressPattern}");
        }

        if (station.IsOccupied)
        {
            jetEngine.state = EngineState.Idle;
        }

        if (station != null)
        {
            originalStationPosition = station.transform.localPosition;
            originalStationRotation = station.transform.localRotation;
            originalParent = station.transform.parent;
        }

        engineIdleAudio.Play();
        engineAccelerationAudio.Play();
        if (hoverAudio) hoverAudio.Play();
    }

    private void OnResetJetEvent(object[] args)
    {
        if (Jet_RB != null)
        {
            // Kill all momentum and snap back to the start
            Jet_RB.velocity = Vector3.zero;
            Jet_RB.angularVelocity = Vector3.zero;
            transform.position = initialJetPosition;
            transform.rotation = initialJetRotation;
            Debug.Log("SpaceJet Reset to Initial Position");
        }
    }

    void OnShipShootEvent(object[] args)
    {
        if (args == null || args.Length == 0 || args[0] == null) return;

        int weaponID = (int)args[0];

        switch (weaponID)
        {
            case 1: FirePrimaryWeapon(); break;
            case 2: FireSecondaryWeapon(); break;
        }
    }

    private void FirePrimaryWeapon()
    {
        FireSingleWeapon(Left_WeaponShootPoint, Weapon_Projectile, MuzzleFireEffect);
        FireSingleWeapon(Right_WeaponShootPoint, Weapon_Projectile, MuzzleFireEffect);
    }

    private void FireSecondaryWeapon()
    {
        FireSingleWeapon(SecondaryWeaponSystemShootPoint, SecondaryWeaponSystem_Projectile, SecondaryWeaponSystem_MuzzleFireEffect);
        FireSingleWeapon(SecondaryWeaponSystemShootPoint_2, SecondaryWeaponSystem_Projectile, SecondaryWeaponSystem_MuzzleFireEffect);
    }

    private void FireSingleWeapon(Transform shootPoint, GameObject projectilePrefab, GameObject muzzleEffect)
    {
        if (muzzleEffect != null)
        {
            GameObject flash = Instantiate(muzzleEffect, shootPoint.position, shootPoint.rotation);
            flash.transform.parent = shootPoint;
            Destroy(flash, 0.5f);
        }

        GameObject projectile = Instantiate(projectilePrefab, shootPoint.position, shootPoint.rotation);
        Rigidbody rb = projectile.GetComponent<Rigidbody>();
        if (rb != null)
        {
            rb.velocity = Jet_RB.velocity + (shootPoint.forward * weaponProjectileSpeed);
        }
        Destroy(projectile, 3f);
    }

    // --- OSC MESSAGE HANDLER ---
    public void OnOSCMessage(object[] args)
    {
        if (!enableOSC || !underLocalPlayerControl || args.Length < 3) return;

        string inputType = args[0].ToString();
        int inputIndex = int.Parse(args[1].ToString());
        string inputValue = args[2].ToString();

        if (inputType == "axis")
        {
            float axisValue = float.Parse(inputValue);

            if (inputIndex == oscAxisPitch) oscPitch = axisValue;
            else if (inputIndex == oscAxisRoll) oscRoll = axisValue;
            else if (inputIndex == oscAxisYaw) oscYaw = axisValue;
            else if (inputIndex == oscAxisLateral) oscLateral = axisValue;
            else if (inputIndex == oscAxisVertical) oscVertical = axisValue;
            else if (inputIndex == oscAxisThrottle)
            {
                // Normalizes standard -1 to 1 hardware throttle into 0 to 1 range
                oscThrottle = (axisValue + 1f) / 2f;
            }
        }
        else if (inputType == "button")
        {
            int isPressed = int.Parse(inputValue);

            if (inputIndex == oscBtnPrimaryFire) oscPrimaryFirePressed = (isPressed == 1);
            if (inputIndex == oscBtnSecondaryFire) oscSecondaryFirePressed = (isPressed == 1);
            if (inputIndex == oscBtnToggleCamera && isPressed == 1) ToggleThirdPersonView();

            if (inputIndex == oscBtnReset && isPressed == 1)
            {
                this.InvokeNetwork(EVENT_RESET_JET, EventTarget.All, null);
            }
        }
    }

    private IEnumerator FindPlayerCamera()
    {
        while (playerCamera == null)
        {
            GameObject cameraObject = GameObject.FindGameObjectWithTag("MainCamera");
            if (cameraObject != null)
            {
                playerCamera = cameraObject.transform;
            }
            yield return new WaitForSeconds(0.5f);
        }
    }

    private void HandleWeaponAiming()
    {
        if (playerCamera == null) return;

        Vector3 aimDirection = playerCamera.forward;
        Ray aimRay = new Ray(playerCamera.position, aimDirection);
        Vector3 targetPoint = aimRay.GetPoint(weaponRange);

        ApplyConstrainedWeaponAim(LeftWeapon.transform, targetPoint);
        ApplyConstrainedWeaponAim(RightWeapon.transform, targetPoint);
        ApplyConstrainedWeaponAim(SecondaryWeaponSystemShootPoint_2, targetPoint);
        ApplyConstrainedWeaponAim(SecondaryWeaponSystemShootPoint, targetPoint);
    }

    private void ApplyConstrainedWeaponAim(Transform weapon, Vector3 targetPoint)
    {
        Vector3 localTargetDir = weapon.parent.InverseTransformPoint(targetPoint).normalized;

        float horizontalAngle = Mathf.Atan2(localTargetDir.x, localTargetDir.z) * Mathf.Rad2Deg;
        float verticalAngle = Mathf.Asin(localTargetDir.y) * Mathf.Rad2Deg;

        horizontalAngle = Mathf.Clamp(horizontalAngle, -maxHorizontalAngle, maxHorizontalAngle);
        verticalAngle = Mathf.Clamp(verticalAngle, -maxVerticalAngle, maxVerticalAngle);

        Vector3 constrainedDir = new Vector3(
            Mathf.Sin(horizontalAngle * Mathf.Deg2Rad),
            Mathf.Sin(verticalAngle * Mathf.Deg2Rad),
            Mathf.Cos(horizontalAngle * Mathf.Deg2Rad)
        ).normalized;

        Vector3 worldTarget = weapon.parent.TransformPoint(constrainedDir * weaponRange);
        Quaternion targetRotation = Quaternion.LookRotation(worldTarget - weapon.position);
        weapon.rotation = Quaternion.Slerp(weapon.rotation, targetRotation, weaponTurnSpeed * Time.deltaTime);
    }

    private void HandleCameraSteering()
    {
        if (playerCamera == null || enableOSC) return; // Disable camera steering if OSC overrides

        Vector3 cameraFlatForward = Vector3.ProjectOnPlane(playerCamera.forward, Vector3.up).normalized;
        Vector3 cameraUpDown = Vector3.ProjectOnPlane(playerCamera.forward, transform.right).normalized;

        Quaternion targetYawRotation = Quaternion.LookRotation(cameraFlatForward, Vector3.up);
        float pitchAngle = Vector3.SignedAngle(cameraFlatForward, cameraUpDown, transform.right) * pitchLookInfluence;
        pitchAngle = Mathf.Clamp(pitchAngle, -maxPitchAngle, maxPitchAngle);

        Quaternion targetRotation = targetYawRotation * Quaternion.Euler(pitchAngle, 0, 0);
        ApplyConstrainedShipRotation(targetRotation);
    }

    private void ApplyConstrainedShipRotation(Quaternion targetRotation)
    {
        Quaternion localCurrent = Quaternion.Inverse(transform.rotation) * Jet_RB.rotation;
        Quaternion localTarget = Quaternion.Inverse(transform.rotation) * targetRotation;

        Vector3 currentEuler = localCurrent.eulerAngles;
        Vector3 targetEuler = localTarget.eulerAngles;

        currentEuler.x = NormalizeAngle(currentEuler.x);
        currentEuler.y = NormalizeAngle(currentEuler.y);
        currentEuler.z = NormalizeAngle(currentEuler.z);
        targetEuler.x = NormalizeAngle(targetEuler.x);
        targetEuler.y = NormalizeAngle(targetEuler.y);
        targetEuler.z = NormalizeAngle(targetEuler.z);

        float smoothedPitch = Mathf.LerpAngle(currentEuler.x, Mathf.Clamp(targetEuler.x, -maxPitchAngle, maxPitchAngle), pitchResponseSpeed * Time.fixedDeltaTime);
        float smoothedYaw = Mathf.LerpAngle(currentEuler.y, targetEuler.y, yawResponseSpeed * Time.fixedDeltaTime);
        float smoothedRoll = Mathf.LerpAngle(currentEuler.z, Mathf.Clamp(targetEuler.z, -maxRollAngle, maxRollAngle) * rollDamping, shipTurnSpeed * Time.fixedDeltaTime);

        Quaternion smoothedRotation = transform.rotation * Quaternion.Euler(smoothedPitch, smoothedYaw, smoothedRoll);
        Jet_RB.MoveRotation(smoothedRotation);
    }

    private float NormalizeAngle(float angle)
    {
        while (angle > 180) angle -= 360;
        while (angle < -180) angle += 360;
        return angle;
    }

    private void HandleWeaponFiring()
    {
        var input = station.GetStationInput();

        bool firePrimary = (enableOSC && oscPrimaryFirePressed) || (input.LeftTrigger > 0.5f);
        bool fireSecondary = (enableOSC && oscSecondaryFirePressed) || (input.RightTrigger > 0.5f);

        if (firePrimary && Time.time > lastPrimaryFireTime + primaryWeaponCooldown)
        {
            lastPrimaryFireTime = Time.time;
            FireWeapon(1);
        }

        if (fireSecondary && Time.time > lastSecondaryFireTime + secondaryWeaponCooldown)
        {
            lastSecondaryFireTime = Time.time;
            FireWeapon(2);
        }
    }

    private void InitializePhysics()
    {
        Jet_RB.mass = jetMass;
        maxThrustForce = jetMass * thrustForceMultiplier;
        Jet_RB.centerOfMass = centerOfMass.localPosition;
    }

    private void Update()
    {
        HandleControlState();
        UpdateInstruments();
        UpdateWeaponStatus();

        if (underLocalPlayerControl)
        {
            HandleWeaponAiming();
            HandleWeaponFiring();
            HandleCameraSteering();
        }
    }

    private void HandleControlState()
    {
        if (station.IsOccupied)
        {
            var player = station.GetPlayer();
            if (player.IsLocal)
            {
                Jet_RB.drag = 1.5f;
                Jet_RB.angularDrag = 3.5f;
                underLocalPlayerControl = true;

                var input = station.GetStationInput();

                if (enableOSC) HandleOSCInput();
                else if (MassiveLoopClient.IsInDesktopMode) HandleDesktopModeInput(input);
                else HandleVRModeInput(input);
            }
            else
            {
                underLocalPlayerControl = false;
            }
        }
        else
        {
            underLocalPlayerControl = false;
            // High drag when empty so it doesn't drift away eternally
            Jet_RB.drag = 5f;
            Jet_RB.angularDrag = 5f;
        }

        if (underLocalPlayerControl)
        {
            syncObject.localPosition = new Vector3(throttle, pitch, yaw);
            syncObject.localRotation = Quaternion.Euler(pitch * 45f, yaw * 45f, roll * 45f);
        }
        else
        {
            var localPos = syncObject.localPosition;
            throttle = localPos.x;
            pitch = localPos.y;
            yaw = localPos.z;

            var localRot = syncObject.localRotation.eulerAngles;
            roll = localRot.z / 45f;
        }
    }

    private void FixedUpdate()
    {
        if (underLocalPlayerControl)
        {
            ApplySpaceStabilization();
            ApplyThrust();
            ApplyRotation();
            ApplyStabilization();
            ApplyLateralMovement();
            ApplyVerticalMovement();

            if (enableOSC && sendMotionTelemetry)
            {
                ProcessAndSendMotionTelemetry();
            }
        }

        jetEngine.CalculateCurrentEngineState(throttle);
        HandleEngineAudioEffects();
    }

    // --- OSC TELEMETRY PROCESSING ---
    private void ProcessAndSendMotionTelemetry()
    {
        Vector3 localVelocity = transform.InverseTransformDirection(Jet_RB.velocity);
        Vector3 acceleration = (localVelocity - lastLocalVelocity) / Time.fixedDeltaTime;
        lastLocalVelocity = localVelocity;

        float surge = Mathf.Clamp(acceleration.z / 20f, -1f, 1f);
        float sway = Mathf.Clamp(acceleration.x / 20f, -1f, 1f);
        float heave = Mathf.Clamp(acceleration.y / 20f, -1f, 1f);

        float pitchAngle = transform.eulerAngles.x;
        if (pitchAngle > 180) pitchAngle -= 360;
        float pitchTele = Mathf.Clamp(pitchAngle / 45f, -1f, 1f);

        float rollAngle = transform.eulerAngles.z;
        if (rollAngle > 180) rollAngle -= 360;
        float rollTele = Mathf.Clamp(rollAngle / 45f, -1f, 1f);

        float yawAngle = transform.eulerAngles.y;
        float yawRate = (yawAngle - lastYawAngle) / Time.fixedDeltaTime;
        if (yawRate > 180) yawRate -= 360;
        if (yawRate < -180) yawRate += 360;
        lastYawAngle = yawAngle;
        float yawTele = Mathf.Clamp(yawRate / 90f, -1f, 1f);

        if (Mathf.Abs(pitchTele - lastPitchTele) > MOTION_THRESHOLD) { osc.SendMessage("/motion/pitch", pitchTele); lastPitchTele = pitchTele; }
        if (Mathf.Abs(rollTele - lastRollTele) > MOTION_THRESHOLD) { osc.SendMessage("/motion/roll", rollTele); lastRollTele = rollTele; }
        if (Mathf.Abs(yawTele - lastYawTele) > MOTION_THRESHOLD) { osc.SendMessage("/motion/yaw", yawTele); lastYawTele = yawTele; }
        if (Mathf.Abs(surge - lastSurge) > MOTION_THRESHOLD) { osc.SendMessage("/motion/surge", surge); lastSurge = surge; }
        if (Mathf.Abs(sway - lastSway) > MOTION_THRESHOLD) { osc.SendMessage("/motion/sway", sway); lastSway = sway; }
        if (Mathf.Abs(heave - lastHeave) > MOTION_THRESHOLD) { osc.SendMessage("/motion/heave", heave); lastHeave = heave; }
    }

    // --- NEW SPACE FLIGHT LOGIC ---
    private void ApplySpaceStabilization()
    {
        // 1. Anti-Gravity: keeps the ship from falling if there is scene gravity
        if (counteractGravity)
        {
            Jet_RB.AddForce(-Physics.gravity * jetMass, ForceMode.Force);
        }

        // 2. Space Hover (Inertial Dampening)
        // Automatically stops the ship from drifting eternally when no inputs are given
        bool noThrust = jetEngine.thrust < 0.05f;
        bool noDirectionalInput = Mathf.Abs(lateralMovementInput) < 0.05f && Mathf.Abs(verticalMovementInput) < 0.05f;

        if (noThrust && noDirectionalInput)
        {
            Vector3 dampingForce = -Jet_RB.velocity * inertialDampening * jetMass;
            Jet_RB.AddForce(dampingForce, ForceMode.Force);

            if (hoverAudio)
            {
                float speedNorm = Mathf.Clamp01(Jet_RB.velocity.magnitude / 20f);
                hoverAudio.volume = Mathf.Lerp(0.1f, 0.6f, speedNorm);
                hoverAudio.pitch = Mathf.Lerp(0.8f, 1.2f, speedNorm);
            }
        }
        else
        {
            if (hoverAudio)
            {
                hoverAudio.volume = Mathf.Lerp(hoverAudio.volume, 0.2f, Time.fixedDeltaTime);
                hoverAudio.pitch = Mathf.Lerp(hoverAudio.pitch, 1.0f, Time.fixedDeltaTime);
            }
        }
    }

    private void ApplyLateralMovement()
    {
        if (Mathf.Abs(lateralMovementInput) > 0.05f)
        {
            Vector3 lateralForce = transform.right * lateralMovementInput * lateralMovementSpeed * jetMass;
            Jet_RB.AddForce(lateralForce, ForceMode.Force);
        }

        // Apply slight resistance to prevent infinite lateral sliding
        Vector3 lateralVelocity = Vector3.Project(Jet_RB.velocity, transform.right);
        Jet_RB.AddForce(-lateralVelocity * lateralMovementDamping * jetMass, ForceMode.Force);
    }

    private void ApplyVerticalMovement()
    {
        if (Mathf.Abs(verticalMovementInput) > 0.05f)
        {
            Vector3 verticalForce = transform.up * verticalMovementInput * verticalMoveSpeed * jetMass;
            Jet_RB.AddForce(verticalForce, ForceMode.Force);
        }

        // Apply slight resistance to prevent infinite vertical drifting
        Vector3 verticalVelocity = Vector3.Project(Jet_RB.velocity, transform.up);
        Jet_RB.AddForce(-verticalVelocity * verticalDamping * jetMass, ForceMode.Force);
    }

    private void ApplyThrust()
    {
        if (jetEngine.state != EngineState.Off)
        {
            Vector3 targetDirection = transform.forward;
            currentThrustDirection = Vector3.Slerp(currentThrustDirection, targetDirection, thrustAlignmentSpeed * Time.fixedDeltaTime).normalized;

            float thrustForce = jetEngine.thrust * maxThrustForce;
            Jet_RB.AddForce(currentThrustDirection * thrustForce, ForceMode.Force);
        }
    }

    private void ApplyRotation()
    {
        Jet_RB.AddTorque(transform.right * pitch * maxTorque * pitchSensitivity * Time.fixedDeltaTime);
        Jet_RB.AddTorque(transform.up * yaw * maxTorque * yawSensitivity * Time.fixedDeltaTime);
        Jet_RB.AddTorque(-transform.forward * roll * maxTorque * rollSensitivity * Time.fixedDeltaTime);
    }

    private void ApplyStabilization()
    {
        // Keeps the ship leveled internally based on its upward orientation
        if (Mathf.Abs(pitch) < 0.1f && Mathf.Abs(roll) < 0.1f)
        {
            Vector3 targetUp = Vector3.Dot(transform.up, Vector3.up) > 0.3f ? Vector3.up : -Vector3.up;

            Vector3 predictedUp = Quaternion.AngleAxis(
                Jet_RB.angularVelocity.magnitude * Mathf.Rad2Deg * stabilitySpeed / stabilityForce,
                Jet_RB.angularVelocity
            ) * transform.up;

            Vector3 torqueVector = Vector3.Cross(predictedUp, targetUp);
            Jet_RB.AddTorque(torqueVector * stabilityForce * Time.fixedDeltaTime);
        }
    }

    private void UpdateWeaponStatus()
    {
        string primaryStatus = Time.time > lastPrimaryFireTime + primaryWeaponCooldown ?
            "<color=#4CAF50>READY</color>" : $"<color=#FF5252>COOLING {(Time.time - lastPrimaryFireTime) / primaryWeaponCooldown:P0}</color>";

        string secondaryStatus = Time.time > lastSecondaryFireTime + secondaryWeaponCooldown ?
            "<color=#4CAF50>READY</color>" : $"<color=#FF5252>COOLING {(Time.time - lastSecondaryFireTime) / secondaryWeaponCooldown:P0}</color>";

        weaponStatusText.text =
            "<size=16><b><color=#64B5F6>WEAPON SYSTEMS & HULL INTEGRITY</color></b></size>\n" +
            $"\n<color=#FFD740>Primary Blasters:</color> {primaryStatus}" +
            $"\n<color=#FFD740>Secondary Missiles:</color> {secondaryStatus}" +
            $"\n<color=#9E9E9E>Last Fire:</color> <color=#BDBDBD>{Time.time - Mathf.Max(lastPrimaryFireTime, lastSecondaryFireTime):F1}s</color>" +
            $"\n\n<color=#FF7043>HULL STATUS:</color>" +
            $"\n<color=#AED581>• Main Armor:</color> <color=#E57373>{ArmorPoints}</color>" +
            $"\n<color=#AED581>• Left Wing:</color> <color=#E57373>{LeftWing_ArmorPoints}</color>" +
            $"\n<color=#AED581>• Right Wing:</color> <color=#E57373>{RightWing_ArmorPoints}</color>" +
            $"\n<color=#AED581>• Main Engine:</color> <color=#E57373>{MainEngine_ArmorPoints}</color>";
    }

    private void FireWeapon(int weaponID)
    {
        this.InvokeNetwork(EVENT_SHIP_SHOOT, EventTarget.All, null, weaponID);
    }

    private void UpdateInstruments()
    {
        float speed = Jet_RB.velocity.magnitude;
        Vector3 localVelocity = transform.InverseTransformDirection(Jet_RB.velocity);

        speedDial.transform.localRotation = Quaternion.Euler(0, Mathf.Lerp(-135, 135, Mathf.InverseLerp(0, 100, speed)), 0);
        thrustDial.transform.localRotation = Quaternion.Euler(0, Mathf.Lerp(-135, 135, Mathf.InverseLerp(0, ENGINE_MAX_THRUST, jetEngine.thrust)), 0);

        string debugString = $"<size=16><b><color=#64B5F6>SPACE FLIGHT SYSTEMS</color></b></size>\n";

        debugString += $"\n<color=#FFD740>PROPULSION</color>\n";
        debugString += $"<color=#81C784>State:</color> <color=#E6EE9C>{jetEngine.state}</color>\n";
        debugString += $"<color=#81C784>Thrust:</color> <color=#E6EE9C>{jetEngine.thrust * 100:F0}%</color> ";
        debugString += $"<color=#81C784>Speed:</color> <color=#E6EE9C>{speed:F1} m/s</color>\n";

        debugString += $"\n<color=#FFD740>ANTI-GRAVITY & DAMPENERS</color>\n";
        debugString += $"<color=#81C784>Anti-Grav Core:</color> <color=#E6EE9C>{(counteractGravity ? "ACTIVE" : "OFF")}</color>\n";
        debugString += $"<color=#81C784>Inertial Dampening:</color> <color=#E6EE9C>{inertialDampening} Ns/m</color>\n";

        debugString += $"\n<color=#FFD740>MOVEMENT VECTORS</color>\n";
        debugString += $"<color=#81C784>Lateral:</color> <color=#E6EE9C>{localVelocity.x:F1} m/s</color> ";
        debugString += $"<color=#81C784>Forward:</color> <color=#E6EE9C>{localVelocity.z:F1} m/s</color>\n";
        debugString += $"<color=#81C784>Vertical:</color> <color=#E6EE9C>{localVelocity.y:F1} m/s</color> ";
        debugString += $"<color=#81C784>Drag:</color> <color=#E6EE9C>{Jet_RB.drag:F2}</color>\n";

        debugString += $"\n<color=#FFD740>CONTROL INPUTS {(enableOSC ? "(OSC OVERRIDE)" : "")}</color>\n";
        debugString += $"<color=#81C784>Throttle:</color> <color=#E6EE9C>{throttle * 100:F0}%</color> ";
        debugString += $"<color=#81C784>Pitch:</color> <color=#E6EE9C>{pitch * 100:F0}%</color>\n";
        debugString += $"<color=#81C784>Yaw:</color> <color=#E6EE9C>{yaw * 100:F0}%</color> ";
        debugString += $"<color=#81C784>Roll:</color> <color=#E6EE9C>{roll * 100:F0}%</color>\n";

        debugText.text = debugString;
        localControlIndicator.SetActive(underLocalPlayerControl);
    }

    private void OnPlayerEnterStation()
    {
        foreach (AudioClip clip in ShipSounds)
        {
            engineStartupAudio.PlayOneShot(clip);
        }

        jetEngine.state = EngineState.Startup;
        jetEngine.startTime = Time.time;
        currentPilot = station.GetPlayer();
        if (currentPilot != null && currentPilot.IsLocal)
        {
            StartCoroutine(FindPlayerCamera());
        }
    }

    private void OnPlayerLeftStation()
    {
        throttle = 0;
        pitch = 0;
        yaw = 0;
        roll = 0;
        verticalMovementInput = 0;
        lateralMovementInput = 0;

        jetEngine.state = EngineState.Off;
        currentPilot = null;
    }

    private void ToggleThirdPersonView()
    {
        if (station == null || ThirdPersonCamera == null) return;
        if (Time.time - lastToggleTime < toggleCooldown) return;

        lastToggleTime = Time.time;
        isInThirdPerson = !isInThirdPerson;

        if (isInThirdPerson)
        {
            originalStationPosition = station.transform.localPosition;
            originalStationRotation = station.transform.localRotation;

            station.transform.SetParent(ThirdPersonCamera.transform, false);
            station.transform.localPosition = Vector3.zero;
            station.transform.localRotation = Quaternion.identity;
        }
        else
        {
            station.transform.SetParent(originalParent, false);
            station.transform.localPosition = originalStationPosition;
            station.transform.localRotation = originalStationRotation;
        }
    }

    // --- OSC FLIGHT HANDLER ---
    private void HandleOSCInput()
    {
        throttle = Mathf.Clamp(oscThrottle, 0, 1);

        pitch = oscPitch * pitchSensitivity * oscInputMultiplier;
        roll = oscRoll * rollSensitivity * oscInputMultiplier;
        yaw = oscYaw * yawSensitivity * oscInputMultiplier;

        lateralMovementInput = oscLateral * oscInputMultiplier;
        verticalMovementInput = oscVertical * oscInputMultiplier;
    }

    private void HandleDesktopModeInput(UserStationInput input)
    {
        if (Mathf.Abs(input.KeyboardMove.y) > 0.1f)
        {
            throttle += input.KeyboardMove.y * throttleSensitivity;
            throttle = Mathf.Clamp(throttle, 0, 1);
        }
        else
        {
            throttle = Mathf.Lerp(throttle, 0, throttleReturnSpeed * Time.deltaTime);
        }

        if (input.LeftSprint)
        {
            yaw = input.KeyboardMove.x * yawSensitivity;
            lateralMovementInput = 0;

            pitch = 0;
            if (input.Jump) pitch += pitchSensitivity;
            if (input.Crouch) pitch -= pitchSensitivity;
        }
        else
        {
            lateralMovementInput = input.KeyboardMove.x;
            yaw = 0;

            verticalMovementInput = 0f;
            if (input.Jump) verticalMovementInput += 1f;
            if (input.Crouch) verticalMovementInput -= 1f;
        }

        roll = 0;

        if (input.RightSprint)
        {
            ToggleThirdPersonView();
        }

        pitch = Mathf.Lerp(pitch, 0, Time.deltaTime * 2f);
        yaw = Mathf.Lerp(yaw, 0, Time.deltaTime * 3f);
        roll = Mathf.Lerp(roll, 0, Time.deltaTime * 2f);
    }

    private void HandleVRModeInput(UserStationInput input)
    {
        if (Mathf.Abs(input.LeftControl.y) > 0.1f)
        {
            throttle += input.LeftControl.y * throttleSensitivity;
            throttle = Mathf.Clamp(throttle, 0, 1);
        }
        else
        {
            throttle = Mathf.Lerp(throttle, 0, throttleReturnSpeed * Time.deltaTime);
        }

        lateralMovementInput = input.LeftControl.x;
        verticalMovementInput = input.RightControl.y;

        pitch = input.RightControl.y * pitchSensitivity;
        yaw = input.RightControl.x * yawSensitivity;

        roll = 0;
        if (input.LeftGrab > 0.5f) roll -= rollSensitivity;
        if (input.RightGrab > 0.5f) roll += rollSensitivity;

        pitch = Mathf.Lerp(pitch, 0, Time.deltaTime * 2f);
        yaw = Mathf.Lerp(yaw, 0, Time.deltaTime * 3f);
        roll = Mathf.Lerp(roll, 0, Time.deltaTime * 2f);
        lateralMovementInput = Mathf.Lerp(lateralMovementInput, 0, Time.deltaTime * 3f);
    }

    private void HandleEngineAudioEffects()
    {
        if (jetEngine.state == EngineState.Off)
        {
            engineAccelerationAudio.volume = 0;
            engineIdleAudio.volume = 0;
        }
        else if (jetEngine.state == EngineState.Startup)
        {
            engineAccelerationAudio.volume = 0;
            engineIdleAudio.volume = Mathf.Lerp(0, 1, (Time.time - jetEngine.startTime) / 3f);
        }
        else
        {
            float thrustFactor = Mathf.InverseLerp(ENGINE_IDLE_THRUST, ENGINE_MAX_THRUST, jetEngine.thrust);
            engineAccelerationAudio.volume = thrustFactor;
            engineAccelerationAudio.pitch = Mathf.Lerp(0.8f, 1.2f, thrustFactor);
            engineIdleAudio.volume = 1 - thrustFactor;
            engineIdleAudio.pitch = Mathf.Lerp(0.9f, 1.1f, thrustFactor * 0.5f);
        }
    }
}