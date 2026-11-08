using System;
using CardboardXR.Tracking;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
#if UNITY_EDITOR || UNITY_ANDROID
using UnityEngine.InputSystem.Android;
#endif

namespace CardboardXR.Stereo
{
    // 3DoF head tracking. By default it fuses every raw gyroscope and accelerometer sample with the
    // Cardboard SDK's Kalman filter (SensorFusionEkf), which keeps up with fast turns. Android's game
    // rotation vector, measured on a Pixel 9 Pro, under-reported fast turns by ~10% and added 20-28
    // degrees of roll before correcting itself; it stays selectable for comparison with the "tracker"
    // launch option (1). Recenter() turns the current heading into straight ahead; GazePointer calls it
    // when the screen is held. The pose is predicted ahead to cover the time until the frame reaches
    // the display, and refreshed again just before rendering.
    //
    // On Android the fusion reads the sensors natively (NativeImu) at their full rate with hardware
    // timestamps, like the SDK. The Input System's sensors, which arrive at 50 Hz stamped with their
    // arrival time, are the fallback and can be forced with the "nativeImu" launch option (0).
    [DefaultExecutionOrder(-100)]
    public class GyroHeadTracker : MonoBehaviour
    {
        public enum Source
        {
            SensorFusion = 0,
            GameRotationVector = 1,
        }

        // The Input System reports Android acceleration in g with the sign flipped; the fusion wants
        // m/s^2 that point up when the phone is still.
        const double k_StandardGravity = 9.80665;
        const float k_StatsIntervalSeconds = 5f;
        // Turn rate above which the [Tracking] log compares native and Input System gyroscope axes.
        const float k_AxisCheckRadiansPerSecond = 1f;

        [SerializeField] Source m_Source = Source.SensorFusion;
        [Tooltip("Read the sensors natively at their full rate with hardware timestamps, where supported.")]
        [SerializeField] bool m_NativeSensors = true;
        [SerializeField] float m_SamplingFrequency = 400f;
        [Tooltip("How far ahead to predict head rotation, roughly the sensor-to-display latency.")]
        [SerializeField, Range(0f, 0.1f)] float m_PredictionSeconds = 0.03f;

        // Gyroscope-rate rotation history, about 1.3 s, for matching ARCore poses to the rotation at
        // their capture time.
        const int k_HistoryLength = 512;

        readonly SensorFusionEkf m_Fusion = new();
        readonly NativeImu.Sample[] m_NativeSamples = new NativeImu.Sample[256];
        readonly long[] m_HistoryTimes = new long[k_HistoryLength];
        readonly Rotation[] m_HistoryRotations = new Rotation[k_HistoryLength];
        int m_HistoryCount;
        int m_HistoryNext;
        UnityEngine.InputSystem.Gyroscope m_Gyro;
        Accelerometer m_Accelerometer;
        AttitudeSensor m_Attitude;
        Quaternion m_Recenter = Quaternion.identity;
        bool m_UseNative;
        bool m_NativeRunning;
        bool m_RecenterWhenAligned;
        // The screen orientation native sensor samples are rotated into, fixed at start.
        ScreenOrientation m_Orientation = ScreenOrientation.LandscapeLeft;
        ScreenOrientation m_LastReportedOrientation;

        // Sample statistics for the [Tracking] log.
        int m_GyroSamples;
        int m_AccelerometerSamples;
        int m_StaleGyroSamples;
        double m_LastGyroTime;
        double m_MinGyroInterval = double.MaxValue;
        double m_MaxGyroInterval;
        long m_LastNativeGyroNs;
        long m_MaxSampleAgeNs;
        Vector3 m_LastNativeGyro;
        double m_AxisAgreement;
        int m_AxisChecks;
        float m_StatsStart;

        // Whether this component moves its transform. HeadPoseFusion turns it off in 6DoF and drives
        // the head itself.
        public bool DrivesTransform { get; set; } = true;

        // Now, on the clock every timestamp this tracker records is on: the native sensors' clock
        // when they run, and the Input System's otherwise. Callers comparing times against the
        // tracker's must use this rather than NativeImu.NowNs, which is 0 where there are no
        // native sensors, as on iOS.
        public long NowNs => m_NativeRunning ? NativeImu.NowNs() : (long)(InputState.currentTime * 1e9);

        public float PredictionSeconds => m_PredictionSeconds;

        // Subtracted from every gyroscope sample before the sensor fusion, on both sensor paths, in
        // rad/s in the frame of the samples the fusion is fed. HeadPoseFusion learns it from ARCore
        // or ARKit with GyroDriftEstimator, which measures it in that same frame, so no conversion
        // is needed on either path. The estimator only settles if this is applied: it adds each
        // window's leftover drift to the bias.
        public Vector3d ExternalGyroscopeBias { get; set; }

        // Linear acceleration (gravity removed) for each accelerometer sample, in m/s^2 in the
        // tracker's gravity-aligned space with y up, stamped on NowNs's clock.
        public event Action<long, Vector3> LinearAccelerationSampled;

        void OnEnable()
        {
            m_Source = (Source)LaunchOptions.GetInt("tracker", (int)m_Source);
            m_PredictionSeconds = LaunchOptions.GetInt("predictionMs", Mathf.RoundToInt(m_PredictionSeconds * 1000f)) / 1000f;
            m_UseNative = m_Source == Source.SensorFusion && NativeImu.Supported &&
                          LaunchOptions.GetInt("nativeImu", m_NativeSensors ? 1 : 0) != 0;
            // Cardboard runs in landscape. Unity has been seen switching Screen.orientation to
            // Portrait while the phone was tilted, which would rotate the tracking axes, so they
            // follow the orientation at start instead.
            m_Orientation = Screen.orientation == ScreenOrientation.LandscapeRight
                ? ScreenOrientation.LandscapeRight
                : ScreenOrientation.LandscapeLeft;
            m_LastReportedOrientation = Screen.orientation;

            // The Input System's sensors rely on it rotating their data into screen space.
            if (!InputSystem.settings.compensateForScreenOrientation)
                Debug.LogWarning("[Stereo] compensateForScreenOrientation is off, so head tracking will be rotated.");

            m_Gyro = UnityEngine.InputSystem.Gyroscope.current;
            m_Accelerometer = Accelerometer.current;
            m_Attitude = FindAttitudeSensor();
            if (m_UseNative && !StartNativeImu())
                m_UseNative = false;
            var ready = m_Source == Source.GameRotationVector
                ? m_Attitude != null
                : m_UseNative || (m_Gyro != null && m_Accelerometer != null);
            if (!ready)
            {
                Debug.LogWarning($"[Stereo] Sensors for {m_Source} not found; head tracking is off.");
                return;
            }

            // The gyroscope also drives prediction for the game rotation vector, and checks the
            // native sensors' axes.
            EnableSensor(m_Gyro);
            if (m_Source == Source.GameRotationVector)
            {
                EnableSensor(m_Attitude);
            }
            else if (!m_UseNative)
            {
                EnableSensor(m_Accelerometer);
                InputSystem.onEvent += OnInputEvent;
            }

            Application.onBeforeRender += UpdatePose;
            m_StatsStart = Time.unscaledTime;
            var sensors = m_Source == Source.GameRotationVector ? "" : m_UseNative ? ", native sensors" : ", Input System sensors";
            Debug.Log($"[Stereo] Head tracking: {m_Source}{sensors}, predicting {m_PredictionSeconds * 1000f:F0} ms, " +
                      $"screen {m_Orientation}");
        }

        void OnDisable()
        {
            Application.onBeforeRender -= UpdatePose;
            InputSystem.onEvent -= OnInputEvent;
            StopNativeImu();
            DisableSensor(m_Gyro);
            DisableSensor(m_Accelerometer);
            DisableSensor(m_Attitude);
        }

        // The native sensors keep running in the background otherwise.
        void OnApplicationPause(bool paused)
        {
            if (!m_UseNative || !isActiveAndEnabled)
                return;
            if (paused)
                StopNativeImu();
            else
                StartNativeImu();
        }

        bool StartNativeImu()
        {
            if (m_NativeRunning)
                return true;
            var status = NativeImu.Start(Application.identifier);
            if (status != 0)
            {
                Debug.LogWarning($"[Stereo] Native sensors failed to start ({status}); using the Input System's.");
                return false;
            }

            // The fusion has no limit on the time between samples, so it would integrate the whole
            // gap as one rotation. Start over, and face ahead once it knows where down is.
            m_Fusion.Reset();
            m_RecenterWhenAligned = true;
            m_LastNativeGyroNs = 0;
            m_HistoryCount = 0;
            m_NativeRunning = true;
            Debug.Log($"[Stereo] Native sensors: {NativeImu.Describe()}");
            return true;
        }

        void StopNativeImu()
        {
            if (!m_NativeRunning)
                return;
            NativeImu.Stop();
            m_NativeRunning = false;
        }

        void EnableSensor(Sensor sensor)
        {
            if (sensor == null)
                return;
            InputSystem.EnableDevice(sensor);
            sensor.samplingFrequency = m_SamplingFrequency;
        }

        static void DisableSensor(Sensor sensor)
        {
            if (sensor != null && sensor.enabled)
                InputSystem.DisableDevice(sensor);
        }

        void Update()
        {
            UpdatePose();
            CheckNativeAxes();
            ReportOrientationChanges();
            LogStats();
        }

        void ReportOrientationChanges()
        {
            if (Screen.orientation == m_LastReportedOrientation)
                return;
            m_LastReportedOrientation = Screen.orientation;
            Debug.LogWarning($"[Stereo] Unity reports screen orientation {Screen.orientation}; tracking stays {m_Orientation}.");
        }

        // Turns the current heading into straight ahead. Only yaw is removed, so the horizon stays
        // level.
        public void Recenter()
        {
            if (TryGetScreenInWorld(out var forward, out var up))
                m_Recenter = Quaternion.Euler(0f, -Quaternion.LookRotation(forward, up).eulerAngles.y, 0f);
        }

        void UpdatePose()
        {
            if (!DrivesTransform || !TryGetScreenInWorld(out var forward, out var up))
                return;
            if (m_RecenterWhenAligned)
            {
                m_RecenterWhenAligned = false;
                Recenter();
            }
            transform.localRotation = m_Recenter * Quaternion.LookRotation(forward, up);
        }

        // The head rotation predicted for the display, in the tracker's own gravity-aligned space,
        // without recentering.
        public bool TryGetRotation(out Quaternion rotation)
        {
            var found = TryGetScreenInWorld(out var forward, out var up);
            rotation = found ? Quaternion.LookRotation(forward, up) : Quaternion.identity;
            return found;
        }

        // The rotation at an earlier time on NowNs's clock, like TryGetRotation, from the latest
        // gyroscope sample at or before it.
        public bool TryGetRotationAt(long timeNs, out Quaternion rotation) => TryGetRotationAt(timeNs, out rotation, out _);

        // Also gives the vertical in the gyroscope's frame at that time.
        public bool TryGetRotationAt(long timeNs, out Quaternion rotation, out Vector3d upInSensor)
        {
            for (var i = 1; i <= m_HistoryCount; i++)
            {
                var index = (m_HistoryNext - i + k_HistoryLength) % k_HistoryLength;
                if (m_HistoryTimes[index] > timeNs)
                    continue;
                var sensorFromStart = m_HistoryRotations[index];
                rotation = HeadRotation(sensorFromStart.Inverse);
                // Start space has z up.
                upInSensor = sensorFromStart * new Vector3d(0, 0, 1);
                return true;
            }

            rotation = Quaternion.identity;
            upInSensor = default;
            return false;
        }

        // Feeds every Input System gyroscope and accelerometer sample to the fusion, not just the
        // latest per frame.
        void OnInputEvent(InputEventPtr eventPtr, InputDevice device)
        {
            if (!eventPtr.IsA<StateEvent>() && !eventPtr.IsA<DeltaStateEvent>())
                return;

            // Event times share InputState.currentTime's clock, which prediction uses.
            var timeNs = (long)(eventPtr.time * 1e9);
            // Both branches mirror DrainNativeSamples. Until iOS ran 6DoF this path only ever served
            // 3DoF, and it skipped the rotation history and the linear acceleration that 6DoF needs:
            // with no history, every AR pose failed to match a rotation and was thrown away. It also
            // skipped the learned bias, so the drift estimator, whose loop that closes, wound up.
            if (device == m_Gyro && m_Gyro.angularVelocity.ReadValueFromEvent(eventPtr, out var angularVelocity))
            {
                RecordGyroSample(eventPtr.time);
                var rate = ToVector3d(angularVelocity) - ExternalGyroscopeBias;
                m_Fusion.ProcessGyroscopeSample(new SensorSample(timeNs, timeNs, rate));
                RecordRotation(timeNs);
            }
            else if (device == m_Accelerometer && m_Accelerometer.acceleration.ReadValueFromEvent(eventPtr, out var acceleration))
            {
                m_AccelerometerSamples++;
                var data = ToVector3d(acceleration) * -k_StandardGravity;
                m_Fusion.ProcessAccelerometerSample(new SensorSample(timeNs, timeNs, data));
                ReportLinearAcceleration(timeNs, data);
            }
        }

        // Feeds the fusion every native sample buffered since the last call, in the screen's frame.
        void DrainNativeSamples()
        {
            int count;
            do
            {
                count = NativeImu.Read(m_NativeSamples);
                for (var i = 0; i < count; i++)
                {
                    var sample = m_NativeSamples[i];
                    var data = PhoneToScreen(new Vector3d(sample.x, sample.y, sample.z));
                    if (sample.type == NativeImu.Gyroscope)
                        data -= ExternalGyroscopeBias;
                    var fusionSample = new SensorSample(sample.timestampNs, sample.timestampNs, data);
                    if (sample.type == NativeImu.Gyroscope)
                    {
                        RecordGyroSample(sample.timestampNs * 1e-9);
                        m_LastNativeGyroNs = sample.timestampNs;
                        m_LastNativeGyro = ToVector3(data);
                        m_Fusion.ProcessGyroscopeSample(fusionSample);
                        RecordRotation(sample.timestampNs);
                    }
                    else
                    {
                        m_AccelerometerSamples++;
                        m_Fusion.ProcessAccelerometerSample(fusionSample);
                        ReportLinearAcceleration(sample.timestampNs, data);
                    }
                }
            } while (count == m_NativeSamples.Length);
        }

        // The screen's back (the view direction) and its up, in Unity world space.
        bool TryGetScreenInWorld(out Vector3 forward, out Vector3 up)
        {
            forward = up = default;
            if (m_Source == Source.SensorFusion)
            {
                if (m_NativeRunning)
                    DrainNativeSamples();
                var nowNs = NowNs;
                if (m_NativeRunning && m_LastNativeGyroNs != 0)
                    m_MaxSampleAgeNs = Math.Max(m_MaxSampleAgeNs, nowNs - m_LastNativeGyroNs);

                if (!m_Fusion.IsAlignedWithGravity)
                    return false;
                // Inverting sensor-from-start gives start-from-screen.
                ScreenInWorld(m_Fusion.PredictRotation(nowNs + (long)(m_PredictionSeconds * 1e9)).Inverse, out forward, out up);
                return true;
            }

            if (m_Attitude == null || !m_Attitude.enabled)
                return false;
            var attitude = PredictAttitude(m_Attitude.attitude.ReadValue());
            forward = AndroidWorldToUnity(attitude * Vector3.back);
            up = AndroidWorldToUnity(attitude * Vector3.up);
            return true;
        }

        // Carries the game rotation vector forward by the gyroscope's current angular velocity. Both
        // are in screen space, and a rotation in the phone's own frame composes on the right.
        Quaternion PredictAttitude(Quaternion attitude)
        {
            if (m_Gyro == null || !m_Gyro.enabled || m_PredictionSeconds <= 0f)
                return attitude;

            var angularVelocity = m_Gyro.angularVelocity.ReadValue();
            var speed = angularVelocity.magnitude;
            if (speed < 1e-4f)
                return attitude;
            return attitude * Quaternion.AngleAxis(speed * m_PredictionSeconds * Mathf.Rad2Deg, angularVelocity / speed);
        }

        // The screen's view direction and up in Unity world axes. Start space has z up, like
        // Android's world.
        static void ScreenInWorld(Rotation startFromScreen, out Vector3 forward, out Vector3 up)
        {
            forward = AndroidWorldToUnity(ToVector3(startFromScreen * new Vector3d(0, 0, -1)));
            up = AndroidWorldToUnity(ToVector3(startFromScreen * new Vector3d(0, 1, 0)));
        }

        static Quaternion HeadRotation(Rotation startFromScreen)
        {
            ScreenInWorld(startFromScreen, out var forward, out var up);
            return Quaternion.LookRotation(forward, up);
        }

        void RecordRotation(long timeNs)
        {
            if (!m_Fusion.IsAlignedWithGravity)
                return;
            m_HistoryTimes[m_HistoryNext] = timeNs;
            m_HistoryRotations[m_HistoryNext] = m_Fusion.SensorFromStart;
            m_HistoryNext = (m_HistoryNext + 1) % k_HistoryLength;
            m_HistoryCount = Math.Min(m_HistoryCount + 1, k_HistoryLength);
        }

        // The accelerometer measures gravity as an upward acceleration; remove it in start space,
        // where z is up.
        void ReportLinearAcceleration(long timeNs, Vector3d screenAcceleration)
        {
            if (LinearAccelerationSampled == null || !m_Fusion.IsAlignedWithGravity)
                return;
            var inStart = m_Fusion.SensorFromStart.Inverse * screenAcceleration;
            var linear = inStart - new Vector3d(0, 0, k_StandardGravity);
            LinearAccelerationSampled(timeNs, AndroidWorldToUnity(ToVector3(linear)));
        }

        // Rotates phone-frame sensor data into the screen's frame, as the Input System's
        // CompensateDirection processor does for its sensors.
        Vector3d PhoneToScreen(Vector3d v) => m_Orientation == ScreenOrientation.LandscapeRight
            ? new Vector3d(v.y, -v.x, v.z)
            : new Vector3d(-v.y, v.x, v.z);

        // During fast turns, compares the native gyroscope's direction with the Input System's,
        // which is already in screen space. Near 1 means PhoneToScreen matches.
        void CheckNativeAxes()
        {
            if (!m_NativeRunning || m_Gyro == null || !m_Gyro.enabled)
                return;
            var reference = m_Gyro.angularVelocity.ReadValue();
            if (reference.magnitude < k_AxisCheckRadiansPerSecond || m_LastNativeGyro.magnitude < k_AxisCheckRadiansPerSecond)
                return;
            m_AxisAgreement += Vector3.Dot(reference.normalized, m_LastNativeGyro.normalized);
            m_AxisChecks++;
        }

        void RecordGyroSample(double time)
        {
            m_GyroSamples++;
            if (m_LastGyroTime > 0)
            {
                var interval = time - m_LastGyroTime;
                if (interval <= 0)
                {
                    m_StaleGyroSamples++;
                }
                else
                {
                    m_MinGyroInterval = Math.Min(m_MinGyroInterval, interval);
                    m_MaxGyroInterval = Math.Max(m_MaxGyroInterval, interval);
                }
            }
            m_LastGyroTime = time;
        }

        // Sample rates and timing every few seconds with the [Tracking] tag. Samples whose time isn't
        // newer than the previous one are dropped by the fusion, so they would lose rotation. With
        // native sensors it adds how old the newest gyroscope sample got when a pose was read, and
        // the average agreement of gyroscope axes during fast turns.
        void LogStats()
        {
            var seconds = Time.unscaledTime - m_StatsStart;
            if (m_Source != Source.SensorFusion || seconds < k_StatsIntervalSeconds)
                return;

            var native = !m_NativeRunning ? "" :
                $", newest gyro up to {m_MaxSampleAgeNs * 1e-6:F1} ms old, {NativeImu.Dropped()} dropped, " +
                (m_AxisChecks > 0 ? $"axes agree {m_AxisAgreement / m_AxisChecks:F2} over {m_AxisChecks} fast frames" : "no fast turns");
            Debug.Log($"[Tracking] gyro {m_GyroSamples / seconds:F0} Hz (interval {m_MinGyroInterval * 1000:F2}-" +
                      $"{m_MaxGyroInterval * 1000:F2} ms, {m_StaleGyroSamples} not newer), " +
                      $"accel {m_AccelerometerSamples / seconds:F0} Hz, bias {m_Fusion.GyroscopeBias}{native}");
            m_GyroSamples = m_AccelerometerSamples = m_StaleGyroSamples = 0;
            m_MinGyroInterval = double.MaxValue;
            m_MaxGyroInterval = 0;
            m_MaxSampleAgeNs = 0;
            m_AxisAgreement = 0;
            m_AxisChecks = 0;
            m_StatsStart = Time.unscaledTime;
        }

        // Prefers Android's game rotation vector, which ignores the magnetometer.
        static AttitudeSensor FindAttitudeSensor()
        {
#if UNITY_EDITOR || UNITY_ANDROID
            if (AndroidGameRotationVector.current != null)
                return AndroidGameRotationVector.current;
#endif
            return AttitudeSensor.current;
        }

        static Vector3d ToVector3d(Vector3 v) => new(v.x, v.y, v.z);

        static Vector3 ToVector3(Vector3d v) => new((float)v.x, (float)v.y, (float)v.z);

        // Android world (east, north, up) to Unity world (x east, y up, z north).
        static Vector3 AndroidWorldToUnity(Vector3 v) => new(v.x, v.z, v.y);
    }
}
