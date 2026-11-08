using System.Collections.Generic;
using CardboardXR.Hands;
using CardboardXR.Interaction;
using CardboardXR.UI;
using TMPro;
using UnityEngine;

namespace CardboardXR.Demos.Rush
{
    // Orb Rush: orbs fly in and you hit them with your hand. Press Start facing the way you want to
    // play: that direction becomes the track, fixed in the room. Each orb arrives at a ringed spot
    // within arm's reach and inside the camera's view (see RushRules), and counts as hit if the palm
    // or the index or middle fingertip touches it on the way in; near the ring is a Perfect. One
    // that gets past is a miss and ends the combo. A round is RushRules.k_RoundSeconds, getting
    // faster. The best score is kept on the phone. Pinches during a round are swallowed, so hitting
    // can't click anything, except on the Pause button above the track; between rounds they work
    // the menus as usual. Paused, the game's clock stops and the orbs hide; the pause menu resumes,
    // restarts or quits the round (the lobby is on the settings panel). Hit tests use the hand's
    // joints as HandTracker predicts them for this frame, which makes up for most of its delay.
    // The track's position eases after the head during a round (k_FollowSeconds), while its facing
    // stays put, so orbs keep arriving within reach however the player shifts, and however head
    // tracking re-anchors: a room-fixed track was carried out of reach by one (see Follow). On Resume
    // it's set down again where the player is and faces. Runs in 6DoF VR. Logs [Rush].
    public class RushDemo : Demo, IPinchHandler
    {
        const string k_BestKey = "OrbRushBest";
        const int k_MiddleTip = 12;
        // How long the track takes to catch up with the head, most of the way: slow enough that
        // leaning in to hit still reaches farther, quick enough that a shifted stance doesn't carry
        // the orbs out of reach.
        const float k_FollowSeconds = 0.5f;
        // Where the menus go, ahead of the head, and the score line, along the track; in meters.
        const float k_MenuDistance = 1.6f;
        const float k_HudDistance = 2.4f;
        static readonly Color[] k_OrbColors =
        {
            new(0.2f, 0.9f, 1f),
            new(1f, 0.35f, 0.8f),
            new(1f, 0.8f, 0.2f),
            new(0.45f, 1f, 0.45f),
        };

        [SerializeField] Material m_OrbMaterial;
        [SerializeField] Material m_HaloMaterial;
        [SerializeField] Material m_RailMaterial;

        enum State
        {
            Menu,
            Playing,
            Paused,
            Results,
        }

        CoreServices m_Core;
        State m_State;
        Transform m_Track;
        Material m_RingMaterial;
        readonly List<RushOrb> m_Orbs = new();
        readonly List<Vector3> m_HandPoints = new();
        System.Random m_Random;
        Vector2 m_LastSpot;
        float m_RoundStart;
        float m_NextSpawn;
        // The game's own time, which stops while paused: orbs and the round run on it.
        float m_Clock;
        float m_NextFollowLog;
        int m_Spawned;

        int m_Score;
        int m_Combo;
        int m_BestCombo;
        int m_Perfects;
        int m_Goods;
        int m_Misses;
        int m_Best;

        RectTransform m_Menu;
        TMP_Text m_MenuTitle;
        TMP_Text m_MenuBody;
        M3Button m_Start;
        RectTransform m_Hud;
        TMP_Text m_HudText;
        TMP_Text m_Callout;
        float m_CalloutUntil;
        RectTransform m_PauseMenu;
        TMP_Text m_PauseBody;

        AudioSource m_Audio;
        AudioClip m_PerfectSound;
        AudioClip m_GoodSound;
        AudioClip m_MissSound;

        public override void OnDemoEnter(CoreServices core)
        {
            m_Core = core;
            core.Placer.ObjectPrefab = null;
            PinchRouter.Handler = this;
            if (core.Settings != null)
                core.Settings.OpenChanged += OnSettingsOpenChanged;
            m_Best = PlayerPrefs.GetInt(k_BestKey, 0);
            m_RingMaterial = new Material(Shader.Find("CardboardXR/Reticle")) { name = "Landing Ring" };
            m_RingMaterial.SetFloat("_Inner", 0.8f);
            m_RingMaterial.SetFloat("_Outer", 0.95f);
            m_RingMaterial.SetFloat("_Outline", 0f);
            m_RingMaterial.SetColor("_OutlineColor", Color.clear);
            BuildSounds();
            BuildMenu();
            BuildHud();
            BuildPauseMenu();
            ShowMenu();
            Debug.Log($"[Rush] Ready; best score {m_Best}");
        }

        public override void OnDemoExit(CoreServices core)
        {
            if (ReferenceEquals(PinchRouter.Handler, this))
                PinchRouter.Clear();
            if (core.Settings != null)
                core.Settings.OpenChanged -= OnSettingsOpenChanged;
            // Out of the round first: the settings panel's Lobby can leave mid-round, and Update would
            // otherwise run once more on the cleared track before the scene unloads.
            if (m_State == State.Playing || m_State == State.Paused)
                Debug.Log($"[Rush] Left mid-round at {m_Clock - m_RoundStart:F0} s, score {m_Score}");
            m_State = State.Menu;
            ClearTrack();
            if (m_RingMaterial != null)
                Destroy(m_RingMaterial);
        }

        // During a round, pinches are part of hitting and click nothing, except on the Pause button.
        public bool TryBeginPinch(GameObject gazeTarget, PinchSource source) =>
            source == PinchSource.Hand && m_State == State.Playing && (gazeTarget == null || !gazeTarget.transform.IsChildOf(m_Hud));

        // Opening the settings panel mid-round pauses it; the pause menu then resumes it when the user
        // is ready, rather than orbs arriving the moment the panel closes.
        void OnSettingsOpenChanged(bool open)
        {
            if (open && m_State == State.Playing)
            {
                Debug.Log("[Rush] Settings opened mid-round");
                Pause();
            }
        }

        public void EndPinch() { }

        void Update()
        {
            if (m_Core == null)
                return;
            if (m_State == State.Menu || m_State == State.Results)
                UpdateStartButton();
            if (m_State != State.Playing)
                return;

            m_Clock += Time.deltaTime;
            Follow();
            var elapsed = m_Clock - m_RoundStart;
            var progress = elapsed / RushRules.k_RoundSeconds;
            if (elapsed < RushRules.k_RoundSeconds && m_Clock >= m_NextSpawn)
            {
                Spawn(progress);
                m_NextSpawn = m_Clock + RushRules.Interval(progress);
            }

            CheckOrbs();
            UpdateHud(elapsed);

            if (elapsed >= RushRules.k_RoundSeconds && m_Orbs.Count == 0)
                EndRound();
        }

        void StartRound()
        {
            ClearTrack();
            var head = m_Core.Head;
            var flat = Vector3.ProjectOnPlane(head.forward, Vector3.up);
            if (flat.sqrMagnitude < 1e-4f)
                flat = Vector3.forward;
            // The track lives in the room's space, so it stays put as tracking corrects itself.
            m_Track = new GameObject("Rush Track").transform;
            m_Track.SetParent(m_Core.Boundary.transform, false);
            m_Track.SetPositionAndRotation(head.position, Quaternion.LookRotation(flat.normalized, Vector3.up));
            BuildRails();

            m_Random = new System.Random();
            m_LastSpot = Vector2.zero;
            m_Score = m_Combo = m_BestCombo = m_Perfects = m_Goods = m_Misses = m_Spawned = 0;
            m_RoundStart = m_Clock;
            // A moment to get ready before the first orb.
            m_NextSpawn = m_Clock + 1.5f;
            m_State = State.Playing;
            M3Fade.Hide(m_Menu.gameObject);
            M3Fade.Hide(m_PauseMenu.gameObject);
            M3Fade.Show(m_Hud.gameObject);
            m_Hud.SetParent(m_Track, false);
            m_Hud.SetLocalPositionAndRotation(new Vector3(0f, 0.45f, k_HudDistance), Quaternion.identity);
            Debug.Log("[Rush] Round started");
        }

        void Spawn(float progress)
        {
            var spot = RushRules.NextSpot(m_Random, m_LastSpot);
            m_LastSpot = spot;
            var speed = RushRules.Speed(progress);
            var arrival = new Vector3(spot.x, -RushRules.k_Drop + spot.y, RushRules.k_ArrivalDistance);
            var travel = (RushRules.k_SpawnDistance - RushRules.k_ArrivalDistance) / speed;
            var arrivalTime = m_Clock + travel;

            var orb = new GameObject($"Orb {++m_Spawned}");
            orb.transform.SetParent(m_Track, false);
            var core = CreateSphere("Core", orb.transform, m_OrbMaterial, RushRules.k_OrbRadius * 2f);
            var halo = CreateSphere("Halo", orb.transform, m_HaloMaterial, RushRules.k_OrbRadius * 3f);
            var ring = GameObject.CreatePrimitive(PrimitiveType.Quad);
            ring.name = "Landing Ring";
            Destroy(ring.GetComponent<Collider>());
            var ringRenderer = ring.GetComponent<MeshRenderer>();
            ringRenderer.sharedMaterial = m_RingMaterial;
            ringRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            ring.transform.SetParent(m_Track, false);

            var rushOrb = orb.AddComponent<RushOrb>();
            rushOrb.Launch(() => m_Clock, arrival, arrivalTime, speed, k_OrbColors[m_Spawned % k_OrbColors.Length], core, halo,
                ringRenderer);
            m_Orbs.Add(rushOrb);
        }

        static Renderer CreateSphere(string name, Transform parent, Material material, float diameter)
        {
            var sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            sphere.name = name;
            Destroy(sphere.GetComponent<Collider>());
            sphere.transform.SetParent(parent, false);
            sphere.transform.localScale = Vector3.one * diameter;
            var renderer = sphere.GetComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            return renderer;
        }

        // Hits and misses. Distances along the track are from where the eyes are now, so leaning in
        // or stepping forward counts.
        void CheckOrbs()
        {
            var tracker = m_Core.HandTracker;
            m_HandPoints.Clear();
            if (tracker != null && tracker.isActiveAndEnabled && tracker.IsTracking)
            {
                var joints = tracker.Joints;
                var palm = Vector3.zero;
                foreach (var id in HandGeometry.Palm)
                    palm += joints[id];
                m_HandPoints.Add(m_Track.InverseTransformPoint(palm / HandGeometry.Palm.Length));
                m_HandPoints.Add(m_Track.InverseTransformPoint(joints[HandGeometry.IndexTip]));
                m_HandPoints.Add(m_Track.InverseTransformPoint(joints[k_MiddleTip]));
            }
            var eyes = m_Track.InverseTransformPoint(m_Core.Head.position).z;

            for (var i = m_Orbs.Count - 1; i >= 0; i--)
            {
                var orb = m_Orbs[i];
                if (orb == null || orb.IsDone)
                {
                    m_Orbs.RemoveAt(i);
                    continue;
                }
                if (!orb.IsActive)
                {
                    // A missed orb flies on; it's dropped once well behind.
                    if (orb.LocalPosition.z - eyes < -1f)
                        Destroy(orb.gameObject);
                    continue;
                }

                var distance = orb.LocalPosition.z - eyes;
                if (m_HandPoints.Count > 0 && RushRules.Touches(orb.LocalPosition, m_HandPoints))
                {
                    var judgement = RushRules.Judge(distance);
                    if (judgement != Judgement.None)
                    {
                        Score(judgement, distance);
                        orb.Hit();
                        continue;
                    }
                }
                if (distance < -RushRules.k_MissBehind)
                {
                    m_Misses++;
                    m_Combo = 0;
                    orb.Miss();
                    Play(m_MissSound, 0.5f);
                    Callout("Miss", M3Theme.OnSurfaceVariant);
                }
            }
        }

        void Score(Judgement judgement, float distance)
        {
            m_Score += RushRules.Points(judgement, m_Combo);
            m_Combo++;
            m_BestCombo = Mathf.Max(m_BestCombo, m_Combo);
            if (judgement == Judgement.Perfect)
            {
                m_Perfects++;
                Play(m_PerfectSound, 0.8f);
                Callout("Perfect", M3Theme.Primary);
            }
            else
            {
                m_Goods++;
                Play(m_GoodSound, 0.7f);
                Callout("Good", new Color(1f, 0.9f, 0.4f));
            }
            if (m_Perfects + m_Goods <= 3 || (m_Perfects + m_Goods) % 10 == 0)
                Debug.Log($"[Rush] {judgement} at {distance * 100f:F0} cm; combo {m_Combo}, score {m_Score}");
        }

        void EndRound()
        {
            m_State = State.Results;
            var hits = m_Perfects + m_Goods;
            var total = hits + m_Misses;
            var accuracy = total > 0 ? hits * 100f / total : 0f;
            var newBest = m_Score > m_Best;
            if (newBest)
            {
                m_Best = m_Score;
                PlayerPrefs.SetInt(k_BestKey, m_Best);
                PlayerPrefs.Save();
            }
            Debug.Log($"[Rush] Round over: score {m_Score}{(newBest ? " (new best)" : "")}, {m_Perfects} perfect, {m_Goods} good, " +
                      $"{m_Misses} missed of {total}, accuracy {accuracy:F0}%, best combo {m_BestCombo}");
            M3Fade.Hide(m_Hud.gameObject);
            m_MenuTitle.text = newBest ? "New best!" : "Round over";
            m_MenuBody.text = $"Score {m_Score}   (best {m_Best})\n" +
                              $"{m_Perfects} perfect, {m_Goods} good, {m_Misses} missed\n" +
                              $"Accuracy {accuracy:F0}%   Best combo {m_BestCombo}";
            m_Start.Text = "Play again";
            PlaceMenu();
        }

        void Pause()
        {
            if (m_State != State.Playing)
                return;
            m_State = State.Paused;
            m_Track.gameObject.SetActive(false);
            m_PauseBody.text = $"Score {m_Score}, {Mathf.CeilToInt(Mathf.Max(0f, RushRules.k_RoundSeconds - (m_Clock - m_RoundStart)))} s left";
            PlaceInFront(m_PauseMenu);
            Debug.Log($"[Rush] Paused at {m_Clock - m_RoundStart:F0} s, score {m_Score}");
        }

        void Resume()
        {
            if (m_State != State.Paused)
                return;
            m_State = State.Playing;
            M3Fade.Hide(m_PauseMenu.gameObject);
            // Where the player is and faces now: they may have turned to the pause menu, and tracking
            // may have re-anchored meanwhile.
            var head = m_Core.Head;
            var moved = Vector3.Distance(m_Track.position, head.position);
            var flat = Vector3.ProjectOnPlane(head.forward, Vector3.up);
            var facing = flat.sqrMagnitude > 1e-4f ? Quaternion.LookRotation(flat.normalized, Vector3.up) : m_Track.rotation;
            var turned = Quaternion.Angle(m_Track.rotation, facing);
            m_Track.SetPositionAndRotation(head.position, facing);
            m_Track.gameObject.SetActive(true);
            Debug.Log($"[Rush] Resumed; the track moved {moved * 100f:F0} cm and turned {turned:F0} deg to where the player is");
        }

        // Eases the track's origin after the head, keeping its facing.
        void Follow()
        {
            var target = m_Core.Head.position;
            var gap = Vector3.Distance(m_Track.position, target);
            m_Track.position = Vector3.Lerp(m_Track.position, target, 1f - Mathf.Exp(-Time.deltaTime / k_FollowSeconds));
            // A sudden gap is the head tracking re-anchoring, worth knowing about.
            if (gap > 0.2f && Time.time > m_NextFollowLog)
            {
                m_NextFollowLog = Time.time + 1f;
                Debug.Log($"[Rush] The track is catching up with the head from {gap * 100f:F0} cm away");
            }
        }

        void Restart()
        {
            Debug.Log($"[Rush] Restarted at {m_Clock - m_RoundStart:F0} s, score {m_Score}");
            StartRound();
        }

        // Back to the Orb Rush menu, not counting the round.
        void QuitRound()
        {
            Debug.Log($"[Rush] Quit at {m_Clock - m_RoundStart:F0} s, score {m_Score}");
            M3Fade.Hide(m_PauseMenu.gameObject);
            M3Fade.Hide(m_Hud.gameObject);
            ClearTrack();
            ShowMenu();
        }

        // A Material dialog: Resume first, as the filled button, then Restart and Quit round.
        void BuildPauseMenu()
        {
            m_PauseMenu = Dialog("Rush Pause", new Vector2(440f, 200f), "Paused", out m_PauseBody);
            DialogActions(m_PauseMenu, -48f,
                ("Quit round", M3Button.Variant.Text, null, QuitRound),
                ("Restart", M3Button.Variant.Tonal, "restart_alt", Restart),
                ("Resume", M3Button.Variant.Filled, "play_arrow", Resume));
            m_PauseMenu.gameObject.SetActive(false);
        }

        // A Material dialog k_MenuDistance away: the surface container high, 28 dp corners, a headline
        // and a line or more of body text under it. Sizes in dp.
        RectTransform Dialog(string name, Vector2 size, string headline, out TMP_Text body)
        {
            const float padding = 24f;
            var dialog = WorldUi.Panel(name, transform, size, k_MenuDistance, M3Theme.SurfaceContainerHigh, M3Theme.k_ShapeDialog);
            var top = size.y / 2f - padding;
            var width = size.x - 2f * padding;
            M3.Text("Headline", dialog, headline, M3Theme.Type.HeadlineSmall, new Vector2(0f, top - 16f), new Vector2(width, 32f),
                alignment: TextAlignmentOptions.Left);
            var bodyHeight = size.y - 2f * padding - 32f - 16f - 24f - M3Theme.k_Target;
            body = M3.Text("Body", dialog, "", M3Theme.Type.BodyMedium, new Vector2(0f, top - 32f - 16f - bodyHeight / 2f),
                new Vector2(width, bodyHeight), M3Theme.OnSurfaceVariant, TextAlignmentOptions.TopLeft);
            return dialog;
        }

        // A dialog's buttons in a row from the right, the last given rightmost, 8 dp apart.
        static M3Button[] DialogActions(RectTransform dialog, float y,
            params (string label, M3Button.Variant variant, string icon, UnityEngine.Events.UnityAction onClick)[] actions)
        {
            var buttons = new M3Button[actions.Length];
            var right = dialog.sizeDelta.x / 2f - 24f;
            for (var i = actions.Length - 1; i >= 0; i--)
            {
                var (label, variant, icon, onClick) = actions[i];
                var width = Mathf.Max(M3.ButtonWidth(label, icon), 96f);
                buttons[i] = M3.Button(label, dialog, label, variant, new Vector2(right - width / 2f, y), onClick, icon, width);
                right -= width + 8f;
            }
            return buttons;
        }

        void ClearTrack()
        {
            foreach (var orb in m_Orbs)
            {
                if (orb != null)
                    Destroy(orb.gameObject);
            }
            m_Orbs.Clear();
            if (m_Hud != null)
                m_Hud.SetParent(transform, false);
            if (m_Track != null)
                Destroy(m_Track.gameObject);
            m_Track = null;
        }

        // Two faint rails from the arrival spots out to where the orbs start, so the track reads as
        // a direction in the dark.
        void BuildRails()
        {
            for (var side = -1; side <= 1; side += 2)
            {
                var rail = GameObject.CreatePrimitive(PrimitiveType.Cube);
                rail.name = "Rail";
                Destroy(rail.GetComponent<Collider>());
                rail.transform.SetParent(m_Track, false);
                var length = RushRules.k_SpawnDistance;
                rail.transform.localPosition = new Vector3(side * (RushRules.k_Across + 0.1f), -RushRules.k_Drop - RushRules.k_Down - 0.05f,
                    RushRules.k_ArrivalDistance + length / 2f);
                rail.transform.localScale = new Vector3(0.008f, 0.008f, length);
                var renderer = rail.GetComponent<MeshRenderer>();
                renderer.sharedMaterial = m_RailMaterial;
                renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            }
        }

        void ShowMenu()
        {
            m_State = State.Menu;
            m_MenuTitle.text = "Orb Rush";
            m_MenuBody.text = "Orbs fly toward you: hit them with your hand as they reach the rings.\n" +
                              "Face the way you want to play, then press Start.\n" +
                              $"Best score {m_Best}";
            m_Start.Text = "Start";
            PlaceMenu();
        }

        void UpdateStartButton()
        {
            // Hands don't run while the floor is being found, so a round can't start yet.
            var ready = m_Core.HandTracker == null || !m_Core.HandTracker.IsPaused;
            if (m_Start.Interactable != ready)
            {
                m_Start.Interactable = ready;
                m_Start.Text = ready ? (m_State == State.Results ? "Play again" : "Start") : "Scanning…";
            }
        }

        // The start and results dialog. While the floor is being found, Start waits, saying so.
        void BuildMenu()
        {
            m_Menu = Dialog("Rush Menu", new Vector2(480f, 268f), "Orb Rush", out m_MenuBody);
            m_MenuTitle = m_Menu.Find("Headline").GetComponent<TMP_Text>();
            // At the right.
            const float width = 180f;
            m_Start = M3.Button("Start", m_Menu, "Start", M3Button.Variant.Filled, new Vector2(240f - 24f - width / 2f, -82f), StartRound,
                width: width);
        }

        // The score line over the track, and the one thing a pinch clicks during a round: Pause. No
        // surface, so it doesn't hide the orbs; dp sized for its distance on the track.
        void BuildHud()
        {
            m_Hud = M3.Canvas("Rush HUD", transform, new Vector2(520f, 120f), k_HudDistance);
            m_Hud.gameObject.AddComponent<GazeCanvas>();
            m_HudText = M3.Text("Score", m_Hud, "", M3Theme.Type.HeadlineSmall, new Vector2(-36f, 28f), new Vector2(440f, 36f),
                alignment: TextAlignmentOptions.Left);
            m_HudText.textWrappingMode = TextWrappingModes.NoWrap;
            M3.IconButton("Pause", m_Hud, "pause", M3Button.Variant.Tonal, new Vector2(232f, 28f), Pause);
            m_Callout = M3.Text("Callout", m_Hud, "", M3Theme.Type.HeadlineSmall, new Vector2(0f, -32f), new Vector2(500f, 36f));
            m_Hud.gameObject.SetActive(false);
        }

        void UpdateHud(float elapsed)
        {
            var left = Mathf.Max(0f, RushRules.k_RoundSeconds - elapsed);
            var multiplier = RushRules.Multiplier(m_Combo);
            m_HudText.text = $"{m_Score}    Combo {m_Combo}{(multiplier > 1 ? $" ×{multiplier}" : "")}    0:{Mathf.CeilToInt(left):00}";
            if (Time.time > m_CalloutUntil && m_Callout.text.Length > 0)
                m_Callout.text = "";
        }

        void Callout(string text, Color color)
        {
            m_Callout.text = text;
            m_Callout.color = color;
            m_CalloutUntil = Time.time + 0.5f;
        }

        void PlaceMenu() => PlaceInFront(m_Menu);

        // Ahead of the head, a little below the eyes, turned to face it.
        void PlaceInFront(RectTransform panel)
        {
            var head = m_Core.Head;
            var flat = Vector3.ProjectOnPlane(head.forward, Vector3.up);
            var yaw = flat.sqrMagnitude > 1e-4f ? Quaternion.LookRotation(flat.normalized) : Quaternion.identity;
            var position = head.position + yaw * Quaternion.Euler(8f, 0f, 0f) * Vector3.forward * k_MenuDistance;
            panel.SetPositionAndRotation(position, Quaternion.LookRotation(position - head.position, Vector3.up));
            M3Fade.Show(panel.gameObject);
        }

        void BuildSounds()
        {
            m_Audio = gameObject.AddComponent<AudioSource>();
            m_Audio.playOnAwake = false;
            m_Audio.spatialBlend = 0f;
            m_PerfectSound = RushSounds.Pop("Perfect", 1320f, 660f);
            m_GoodSound = RushSounds.Pop("Good", 880f, 520f);
            m_MissSound = RushSounds.Thud("Miss");
        }

        void Play(AudioClip clip, float volume)
        {
            if (clip != null)
                m_Audio.PlayOneShot(clip, volume);
        }
    }

    // Short sounds made in code, so nothing needs to be downloaded or bundled.
    public static class RushSounds
    {
        const int k_Rate = 44100;

        // A bright pop: a sine sweeping down from one pitch to another, with a quick decay.
        public static AudioClip Pop(string name, float fromHz, float toHz)
        {
            const float seconds = 0.12f;
            var samples = new float[(int)(k_Rate * seconds)];
            var phase = 0.0;
            for (var i = 0; i < samples.Length; i++)
            {
                var t = i / (float)samples.Length;
                phase += 2.0 * System.Math.PI * Mathf.Lerp(fromHz, toHz, t) / k_Rate;
                samples[i] = (float)System.Math.Sin(phase) * Mathf.Exp(-5f * t) * Mathf.Min(1f, i / 60f);
            }
            var clip = AudioClip.Create(name, samples.Length, 1, k_Rate, false);
            clip.SetData(samples, 0);
            return clip;
        }

        // A dull low thud with a little noise.
        public static AudioClip Thud(string name)
        {
            const float seconds = 0.2f;
            var samples = new float[(int)(k_Rate * seconds)];
            var random = new System.Random(7);
            for (var i = 0; i < samples.Length; i++)
            {
                var t = i / (float)samples.Length;
                var tone = Mathf.Sin(2f * Mathf.PI * Mathf.Lerp(140f, 70f, t) * i / k_Rate);
                var noise = (float)(random.NextDouble() * 2.0 - 1.0) * 0.25f;
                samples[i] = (tone + noise) * Mathf.Exp(-9f * t) * 0.8f;
            }
            var clip = AudioClip.Create(name, samples.Length, 1, k_Rate, false);
            clip.SetData(samples, 0);
            return clip;
        }
    }
}
