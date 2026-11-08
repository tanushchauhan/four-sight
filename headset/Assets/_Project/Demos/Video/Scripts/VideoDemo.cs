using System.Collections;
using System.Collections.Generic;
using TMPro;
using CardboardXR.UI;
using UnityEngine;
using UnityEngine.Video;

namespace CardboardXR.Demos.Video
{
    // Watch 360 and VR180 videos, 2D or 3D. It opens on a library of what's on the phone (see
    // VideoLibrary); pick one and it plays around you. Pinch (or tap the screen) anywhere on the
    // video to bring up the controls, a little below where you're looking: play and pause, jump back
    // or ahead 10 s, seek by clicking the bar, the next video, the format (when a file's name doesn't
    // say it), and the library (the lobby is on the settings panel). Pinch on the video again to
    // put them away. They stay where they appeared rather than following the head, so they can be
    // looked at.
    // The demo runs in 3DoF with the AR session and hand tracking off; PinchInput's light pinch
    // detector runs, slowing down with no hand in view, leaving most of the heat to the video
    // decoder. Logs [Video].
    [RequireComponent(typeof(VideoPlayer))]
    public class VideoDemo : Demo
    {
        // The controls appear this far below where the head looks, in degrees, so they don't cover
        // what was being watched, but no lower than k_LowestPitch.
        const float k_ControlsBelowGaze = 20f;
        const float k_LowestPitch = -60f;
        const float k_PanelDistance = 1.5f;
        const float k_SkipSeconds = 10f;
        const int k_EntriesPerPage = 5;
        const float k_StatusSeconds = 5f;

        [SerializeField] VideoSphere m_Sphere;
        // The repository's placeholders. Any other videos in StreamingAssets (Tools/make_videos.sh copies
        // your own in for local builds) come first.
        [Tooltip("Videos in StreamingAssets, offered first, so there's something to watch without copying any.")]
        [SerializeField] string[] m_Bundled = { "Sample_360.mp4", "Sample_180_sbs.mp4" };

        CoreServices m_Core;
        VideoPlayer m_Player;
        RenderTexture m_Texture;
        List<VideoEntry> m_Library = new();
        int m_Current = -1;
        int m_Wanted = -1;
        Coroutine m_Download;
        int m_Dropped;
        float m_NextStatus;

        RectTransform m_Panel;
        RectTransform m_LibraryPage;
        RectTransform m_ControlsPage;
        TMP_Text m_Title;
        TMP_Text m_FormatLine;
        TMP_Text m_Time;
        M3Button m_Play;
        M3Slider m_Seek;
        TMP_Text m_PageLabel;
        M3Button m_PreviousPage;
        M3Button m_NextPage;
        TMP_Text m_EmptyMessage;
        readonly List<(M3Button item, TMP_Text supporting)> m_EntryButtons = new();
        int m_Page;
        bool m_ShowingLibrary;
        bool m_ControlsShown;

        public override void OnDemoEnter(CoreServices core)
        {
            m_Core = core;
            m_Player = GetComponent<VideoPlayer>();
            m_Player.playOnAwake = false;
            m_Player.source = VideoSource.Url;
            m_Player.renderMode = VideoRenderMode.RenderTexture;
            m_Player.isLooping = true;
            m_Player.skipOnDrop = true;
            m_Player.audioOutputMode = VideoAudioOutputMode.Direct;
            m_Player.prepareCompleted += OnPrepared;
            m_Player.errorReceived += OnError;
            m_Player.frameDropped += OnFrameDropped;
            m_Sphere.Clicked += OnVideoClicked;

            BuildPanel();
            Refresh();
            ShowLibrary();
        }

        public override void OnDemoExit(CoreServices core)
        {
            if (m_Download != null)
                StopCoroutine(m_Download);
            m_Player.prepareCompleted -= OnPrepared;
            m_Player.errorReceived -= OnError;
            m_Player.frameDropped -= OnFrameDropped;
            m_Sphere.Clicked -= OnVideoClicked;
            m_Player.Stop();
            m_Sphere.Show(null, default);
            ReleaseTexture();
        }

        void OnDestroy() => ReleaseTexture();

        void Update()
        {
            if (m_Core == null)
                return;
            if (m_ShowingLibrary)
                UpdateLibraryLabels();
            else if (m_ControlsShown)
                UpdateControls();

            if (m_Current >= 0 && m_Player.isPrepared && Time.unscaledTime >= m_NextStatus)
            {
                m_NextStatus = Time.unscaledTime + k_StatusSeconds;
                Debug.Log($"[Video] {(m_Player.isPlaying ? "Playing" : "Paused")} at {Clock(m_Player.time)} of {Clock(m_Player.length)}, " +
                          $"{m_Dropped} frames dropped in the last {k_StatusSeconds:F0} s");
                m_Dropped = 0;
            }
        }

        void Refresh()
        {
            m_Library = VideoLibrary.Find(m_Bundled, out var bundled);
            m_Page = 0;
            Debug.Log($"[Video] {m_Library.Count} videos: {bundled} bundled, the rest from {VideoLibrary.VideoFolder}");
        }

        // Plays an entry, downloading it first if it's a Drive video not yet in the cache.
        void Play(int index)
        {
            if (index < 0 || index >= m_Library.Count)
                return;
            var entry = m_Library[index];
            m_Wanted = index;
            if (entry.NeedsDownload)
            {
                if (m_Download == null)
                    m_Download = StartCoroutine(DownloadThenPlay(entry, index));
                return;
            }

            m_Player.Stop();
            m_Sphere.Show(null, default);
            m_Current = index;
            m_Player.url = entry.Url;
            m_Player.Prepare();
            m_Title.text = entry.Title;
            entry.Status = "Loading";
            Debug.Log($"[Video] Opening {entry.Title} ({entry.Format})");
        }

        IEnumerator DownloadThenPlay(VideoEntry entry, int index)
        {
            yield return DriveDownloader.Download(entry, null);
            m_Download = null;
            // Only if it's still the one wanted: something else may have been picked meanwhile.
            if (entry.Url != null && m_Wanted == index)
                Play(index);
        }

        void OnPrepared(VideoPlayer player)
        {
            var entry = m_Library[m_Current];
            var width = (int)player.width;
            var height = (int)player.height;
            if (m_Texture == null || m_Texture.width != width || m_Texture.height != height)
            {
                ReleaseTexture();
                m_Texture = new RenderTexture(width, height, 0) { name = "Video" };
            }
            player.targetTexture = m_Texture;
            m_Sphere.Show(m_Texture, entry.Format);
            player.Play();
            entry.Status = null;
            m_Dropped = 0;
            m_NextStatus = Time.unscaledTime + k_StatusSeconds;
            Debug.Log($"[Video] Playing {entry.Title}: {width}x{height} at {player.frameRate:F0} fps, {Clock(player.length)}, " +
                      $"{entry.Format}, {player.audioTrackCount} audio tracks");
            HidePanel();
        }

        void OnError(VideoPlayer player, string message)
        {
            if (m_Current >= 0)
                m_Library[m_Current].Status = $"Couldn't play: {message}";
            Debug.LogWarning($"[Video] Couldn't play {(m_Current >= 0 ? m_Library[m_Current].Title : player.url)}: {message}");
            m_Current = -1;
            m_Sphere.Show(null, default);
            ShowLibrary();
        }

        void OnFrameDropped(VideoPlayer player) => m_Dropped++;

        void TogglePlay()
        {
            if (m_Current < 0 || !m_Player.isPrepared)
                return;
            if (m_Player.isPlaying)
                m_Player.Pause();
            else
                m_Player.Play();
            Debug.Log($"[Video] {(m_Player.isPlaying ? "Resumed" : "Paused")} at {Clock(m_Player.time)}");
        }

        void Skip(float seconds) => SeekTo(m_Player.time + seconds);

        void SeekTo(double seconds)
        {
            if (m_Current < 0 || !m_Player.isPrepared || !m_Player.canSetTime)
                return;
            // The player moves there a moment later, so log the target: reading its time back here
            // still gives where it was.
            var target = System.Math.Clamp(seconds, 0.0, System.Math.Max(0.0, m_Player.length - 0.5));
            Debug.Log($"[Video] Seek from {Clock(m_Player.time)} to {Clock(target)}");
            m_Player.time = target;
        }

        void Next()
        {
            if (m_Library.Count > 0)
                Play((Mathf.Max(m_Current, 0) + 1) % m_Library.Count);
        }

        void CycleFormat()
        {
            if (m_Current < 0)
                return;
            var entry = m_Library[m_Current];
            entry.Format = entry.Format.Next();
            if (m_Player.isPrepared)
                m_Sphere.Show(m_Texture, entry.Format);
            Debug.Log($"[Video] Format of {entry.Title} set to {entry.Format}");
        }

        void ReleaseTexture()
        {
            if (m_Texture == null)
                return;
            if (m_Player != null && m_Player.targetTexture == m_Texture)
                m_Player.targetTexture = null;
            m_Texture.Release();
            Destroy(m_Texture);
            m_Texture = null;
        }

        // Where the head is looking, in degrees above (+) or below (-) the horizon.
        float HeadPitch => Mathf.Asin(Mathf.Clamp(m_Core.Head.forward.y, -1f, 1f)) * Mathf.Rad2Deg;

        // A tap on the video toggles the controls. It also closes the library, back to the video.
        void OnVideoClicked()
        {
            if (m_ShowingLibrary || m_ControlsShown)
                HidePanel();
            else
                ShowControls();
        }

        void ShowLibrary()
        {
            m_ShowingLibrary = true;
            m_ControlsShown = false;
            m_LibraryPage.gameObject.SetActive(true);
            m_ControlsPage.gameObject.SetActive(false);
            PlacePanel(-12f, k_LibrarySize);
            UpdateLibraryLabels();
        }

        void ShowControls()
        {
            m_ShowingLibrary = false;
            m_ControlsShown = true;
            m_LibraryPage.gameObject.SetActive(false);
            m_ControlsPage.gameObject.SetActive(true);
            PlacePanel(Mathf.Max(HeadPitch - k_ControlsBelowGaze, k_LowestPitch), k_ControlsSize);
            UpdateControls();
        }

        void HidePanel()
        {
            m_ShowingLibrary = false;
            m_ControlsShown = false;
            M3Fade.Hide(m_Panel.gameObject);
        }

        // In front of where the head faces, at a pitch, turned to face it. It stays there until it's
        // hidden and shown again.
        void PlacePanel(float pitch, Vector2 size)
        {
            WorldUi.PlaceInFront(m_Panel, m_Core.Head, k_PanelDistance, pitch);
            m_Panel.sizeDelta = size;
            M3Fade.Show(m_Panel.gameObject);
        }

        void UpdateControls()
        {
            var ready = m_Current >= 0 && m_Player.isPrepared;
            if (m_Current >= 0)
            {
                var entry = m_Library[m_Current];
                m_Title.text = entry.Title;
                m_FormatLine.text = entry.Status ?? entry.Format.ToString();
            }
            m_Play.Icon = ready && m_Player.isPlaying ? "pause" : "play_arrow";
            m_Time.text = ready ? $"{Clock(m_Player.time)} / {Clock(m_Player.length)}" : "";
            m_Seek.Value = ready && m_Player.length > 0 ? (float)(m_Player.time / m_Player.length) : 0f;
        }

        void UpdateLibraryLabels()
        {
            var pages = Mathf.Max(1, (m_Library.Count + k_EntriesPerPage - 1) / k_EntriesPerPage);
            m_Page = Mathf.Clamp(m_Page, 0, pages - 1);
            m_PageLabel.text = $"{m_Page + 1} of {pages}";
            m_PreviousPage.Interactable = m_Page > 0;
            m_NextPage.Interactable = m_Page < pages - 1;
            m_EmptyMessage.gameObject.SetActive(m_Library.Count == 0);
            for (var i = 0; i < m_EntryButtons.Count; i++)
            {
                var index = m_Page * k_EntriesPerPage + i;
                var (item, supporting) = m_EntryButtons[i];
                item.gameObject.SetActive(index < m_Library.Count);
                if (index >= m_Library.Count)
                    continue;
                var entry = m_Library[index];
                item.Text = entry.Title;
                item.Icon = entry.NeedsDownload ? "cloud_download" : "movie";
                supporting.text = entry.Status ?? (entry.NeedsDownload ? "Google Drive: pinch to download" : entry.Format.ToString());
                if (item.Selected != (index == m_Current))
                    item.Selected = index == m_Current;
            }
        }

        // Both pages share one Material panel, k_PanelDistance away, sized to the page showing. Sizes in dp.
        static readonly Vector2 k_LibrarySize = new(600f, 536f);
        static readonly Vector2 k_ControlsSize = new(600f, 352f);

        void BuildPanel()
        {
            m_Panel = WorldUi.Panel("Video Panel", transform, k_LibrarySize, k_PanelDistance);

            // The library: a list of videos, a page at a time.
            m_LibraryPage = WorldUi.CreateGroup("Library", m_Panel);
            M3.Text("Header", m_LibraryPage, "Videos", M3Theme.Type.TitleLarge, new Vector2(-60f, 220f), new Vector2(432f, 32f),
                alignment: TextAlignmentOptions.Left);
            M3.IconButton("Refresh", m_LibraryPage, "refresh", M3Button.Variant.Text, new Vector2(248f, 220f), () =>
            {
                Refresh();
                UpdateLibraryLabels();
            });
            for (var i = 0; i < k_EntriesPerPage; i++)
            {
                var slot = i;
                m_EntryButtons.Add(M3.ListItem($"Entry {i}", m_LibraryPage, "movie", new Vector2(0f, 152f - i * 68f), 552f,
                    () => Play(m_Page * k_EntriesPerPage + slot)));
            }
            var folder = Application.platform == RuntimePlatform.IPhonePlayer
                ? "On a Mac, open the iPhone in Finder, then Files > CardboardXR > Videos, and drag videos in."
                : $"Copy videos with: adb push video.mp4 {VideoLibrary.VideoFolder}";
            m_EmptyMessage = M3.Text("Empty", m_LibraryPage, "No videos yet.\n\n" + folder +
                "\n\nName tags set the format: _360, _180, _sbs, _tb. For Google Drive, list share links in videos.txt in that folder.",
                M3Theme.Type.BodyMedium, new Vector2(0f, 16f), new Vector2(520f, 300f), M3Theme.OnSurfaceVariant);
            m_PreviousPage = M3.IconButton("Previous page", m_LibraryPage, "chevron_left", M3Button.Variant.Text, new Vector2(-84f, -212f),
                () => Page(-1));
            m_PageLabel = M3.Text("Page", m_LibraryPage, "", M3Theme.Type.BodyMedium, new Vector2(0f, -212f), new Vector2(100f, 24f),
                M3Theme.OnSurfaceVariant);
            m_NextPage = M3.IconButton("Next page", m_LibraryPage, "chevron_right", M3Button.Variant.Text, new Vector2(84f, -212f),
                () => Page(1));

            // The controls: what's playing, a seek slider, transport, then the rest.
            m_ControlsPage = WorldUi.CreateGroup("Controls", m_Panel);
            m_Title = M3.Text("Title", m_ControlsPage, "", M3Theme.Type.TitleMedium, new Vector2(0f, 140f), new Vector2(552f, 24f));
            m_Title.textWrappingMode = TextWrappingModes.NoWrap;
            m_FormatLine = M3.Text("Format", m_ControlsPage, "", M3Theme.Type.BodyMedium, new Vector2(0f, 116f), new Vector2(552f, 20f),
                M3Theme.OnSurfaceVariant);
            m_Seek = M3.Slider("Seek", m_ControlsPage, new Vector2(0f, 76f), 552f);
            m_Seek.Clicked += fraction =>
            {
                if (m_Current >= 0 && m_Player.isPrepared)
                    SeekTo(fraction * m_Player.length);
            };
            m_Time = M3.Text("Time", m_ControlsPage, "", M3Theme.Type.BodyMedium, new Vector2(0f, 46f), new Vector2(552f, 20f),
                M3Theme.OnSurfaceVariant);

            M3.IconButton("Back 10 s", m_ControlsPage, "replay_10", M3Button.Variant.Tonal, new Vector2(-100f, -20f),
                () => Skip(-k_SkipSeconds));
            m_Play = M3.IconButton("Play", m_ControlsPage, "play_arrow", M3Button.Variant.Filled, new Vector2(0f, -20f), TogglePlay, 80f);
            M3.IconButton("Ahead 10 s", m_ControlsPage, "forward_10", M3Button.Variant.Tonal, new Vector2(100f, -20f),
                () => Skip(k_SkipSeconds));

            const float width = 168f;
            M3.Button("Library", m_ControlsPage, "Library", M3Button.Variant.Tonal, new Vector2(-176f, -128f), ShowLibrary, "video_library",
                width);
            M3.Button("Next video", m_ControlsPage, "Next", M3Button.Variant.Tonal, new Vector2(0f, -128f), Next, "skip_next", width);
            M3.Button("Format", m_ControlsPage, "Format", M3Button.Variant.Tonal, new Vector2(176f, -128f), CycleFormat, "aspect_ratio",
                width);
        }

        void Page(int step)
        {
            m_Page += step;
            UpdateLibraryLabels();
        }

        static string Clock(double seconds)
        {
            var total = (int)System.Math.Max(0.0, seconds);
            return total >= 3600 ? $"{total / 3600}:{total / 60 % 60:00}:{total % 60:00}" : $"{total / 60}:{total % 60:00}";
        }
    }
}
