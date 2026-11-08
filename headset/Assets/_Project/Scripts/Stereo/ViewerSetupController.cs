using CardboardXR.UI;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Events;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;
using UnityEngine.XR.ARFoundation;

namespace CardboardXR.Stereo
{
    // Lets the user pick their viewer by scanning the QR code printed on it, like the Cardboard apps'
    // "switch viewer" flow, and remembers it between launches. A button at the top right, where
    // Cardboard apps put their settings gear, opens a full-screen camera view. The stereo view is
    // paused while scanning, and the AR session and camera are switched on for it, then put back as
    // they were: off, unless ArPoseSource is tracking with them.
    public class ViewerSetupController : MonoBehaviour
    {
        const string k_SavedViewerKey = "CardboardXR.ViewerUrl";
        const string k_ScanPrompt = "Point the camera at the QR code on your viewer.";
        const float k_RetryDelaySeconds = 2.5f;
        const float k_ToastSeconds = 3f;

        [SerializeField] StereoRig m_Rig;
        [SerializeField] ARSession m_Session;
        [SerializeField] ARCameraManager m_CameraManager;
        [SerializeField] ViewerQrScanner m_Scanner;

        // The corner's settings button, and the menu it opens.
        GameObject m_SettingsButton;
        GameObject m_Menu;
        float m_MenuCloseTime;
        M3Button m_View;
        bool m_ShownMono;
        GameObject m_ScanPanel;
        RawImage m_Preview;
        AspectRatioFitter m_PreviewFitter;
        TMP_Text m_Status;
        TMP_Text m_Toast;
        GameObject m_Snackbar;
        float m_ToastHideTime;
        bool m_Resolving;
        bool m_SessionWasEnabled;
        bool m_CameraWasEnabled;

        void Awake()
        {
            EnsureEventSystem();
            BuildUi();
            ShowScanPanel(false);
            LoadSavedViewer();
        }

        void OnEnable() => m_Scanner.CodeFound += OnCodeFound;

        void OnDisable() => m_Scanner.CodeFound -= OnCodeFound;

        void Update()
        {
            if (m_ScanPanel.activeSelf && m_Scanner.Preview != null)
            {
                m_Preview.texture = m_Scanner.Preview;
                m_PreviewFitter.aspectRatio = m_Scanner.Preview.width / (float)m_Scanner.Preview.height;
            }

            if (M3Fade.IsShown(m_Snackbar) && Time.unscaledTime > m_ToastHideTime)
                M3Fade.Hide(m_Snackbar);
            if (M3Fade.IsShown(m_Menu) && Time.unscaledTime > m_MenuCloseTime)
                M3Fade.Hide(m_Menu);

            // The rig reads the "mono" launch option after this UI is built, and the view can change
            // from elsewhere, so the label follows it rather than only the button's own presses.
            if (m_ShownMono != m_Rig.IsMono)
                UpdateViewLabel();
        }

        void LoadSavedViewer()
        {
            var url = PlayerPrefs.GetString(k_SavedViewerKey, "");
            if (url.Length == 0)
                return;

            if (ViewerParamsCodec.TryDecodeUrl(url, out var viewer))
            {
                m_Rig.Viewer = viewer;
                Debug.Log($"[Viewer] Using saved viewer {viewer.vendor} {viewer.model}");
            }
            else
            {
                PlayerPrefs.DeleteKey(k_SavedViewerKey);
            }
        }

        void SetScanning(bool scanning)
        {
            if (scanning == m_ScanPanel.activeSelf)
                return;
            if (scanning)
            {
                m_SessionWasEnabled = m_Session.enabled;
                m_CameraWasEnabled = m_CameraManager.enabled;
            }

            ShowScanPanel(scanning);
            m_Session.enabled = scanning || m_SessionWasEnabled;
            m_CameraManager.enabled = scanning || m_CameraWasEnabled;
            m_Scanner.enabled = scanning;
        }

        void ToggleView()
        {
            M3Fade.Hide(m_Menu);
            m_Rig.ToggleMono();
            UpdateViewLabel();
        }

        void ToggleMenu()
        {
            M3Fade.SetShown(m_Menu, !M3Fade.IsShown(m_Menu));
            m_MenuCloseTime = Time.unscaledTime + k_MenuSeconds;
        }

        void ChooseOpenSettings()
        {
            M3Fade.Hide(m_Menu);
            SettingsPanelHub.Open();
        }

        void ChooseSetViewer()
        {
            M3Fade.Hide(m_Menu);
            SetScanning(true);
        }

        // The button says which view it would switch to, not which one it's in.
        void UpdateViewLabel()
        {
            m_ShownMono = m_Rig.IsMono;
            if (m_View != null)
            {
                m_View.Text = m_ShownMono ? "Viewer view" : "Phone view";
                m_View.Icon = m_ShownMono ? "view_agenda" : "smartphone";
            }
        }

        void ShowScanPanel(bool show)
        {
            m_ScanPanel.SetActive(show);
            m_SettingsButton.SetActive(!show);
            M3Fade.Hide(m_Menu);
            m_Rig.enabled = !show;
            m_Status.text = k_ScanPrompt;
        }

        async void OnCodeFound(string text)
        {
            if (m_Resolving)
                return;

            m_Resolving = true;
            m_Scanner.enabled = false;
            m_Status.text = "Reading viewer...";
            Debug.Log($"[Viewer] Scanned: {text}");

            var (status, url) = await ViewerUrlResolver.ResolveAsync(text);
            m_Resolving = false;
            // Cancelled while the link was being followed.
            if (!m_ScanPanel.activeSelf)
                return;

            if (status == ViewerUrlResolver.Status.Ok && ViewerParamsCodec.TryDecodeUrl(url, out var viewer))
            {
                PlayerPrefs.SetString(k_SavedViewerKey, url);
                PlayerPrefs.Save();
                m_Rig.Viewer = viewer;
                SetScanning(false);
                ShowToast($"Viewer: {viewer.vendor} {viewer.model}");
                Debug.Log($"[Viewer] Switched to {viewer.vendor} {viewer.model} from {url}");
                return;
            }

            m_Status.text = status == ViewerUrlResolver.Status.ConnectionError
                ? "Couldn't follow the code's link. Check the connection and try again."
                : "That doesn't look like a Cardboard viewer code. Trying again...";
            await Awaitable.WaitForSecondsAsync(k_RetryDelaySeconds);
            if (m_ScanPanel.activeSelf)
            {
                m_Status.text = k_ScanPrompt;
                m_Scanner.enabled = true;
            }
        }

        void UseCardboardV1()
        {
            PlayerPrefs.DeleteKey(k_SavedViewerKey);
            PlayerPrefs.Save();
            m_Rig.Viewer = ViewerParams.CardboardV1;
            SetScanning(false);
            ShowToast("Viewer: Cardboard v1");
        }

        void ShowToast(string message)
        {
            m_Toast.text = message;
            M3Fade.Show(m_Snackbar);
            m_ToastHideTime = Time.unscaledTime + k_ToastSeconds;
        }

        static void EnsureEventSystem()
        {
            if (EventSystem.current == null)
                new GameObject("EventSystem", typeof(EventSystem), typeof(InputSystemUIInputModule));
        }

        // The phone-screen UI, for use with the phone in hand: Material tonal buttons at the top right,
        // the QR scanner's screen, and a snackbar for messages. Laid out in dp inside a frame scaled to
        // the screen (k_DpOnScreen reference pixels each), so it's sized as Material sizes phone UI.
        void BuildUi()
        {
            var canvasObject = new GameObject("Viewer Setup UI", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            canvasObject.transform.SetParent(transform, false);
            var canvas = canvasObject.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 100;
            var scaler = canvasObject.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 1f;
            var root = (RectTransform)canvasObject.transform;

            // One small settings button in the corner, clear of the alignment marker at the bottom
            // center and covering as little of the view as it can: with the phone in a viewer it's
            // all that's left on screen. It opens a menu below it with Set viewer and the switch between
            // the viewer's two eye views and one view filling the screen. Both are here rather than on
            // the gaze panel because they're pressed with the phone in hand, out of the viewer, where a
            // world-space panel can't be reached.
            var corner = Frame("Corner", root, new Vector2(1f, 1f), new Vector2(-12f, -12f), new Vector2(40f, 40f));
            m_SettingsButton = M3.IconButton("Settings", corner, "settings", M3Button.Variant.Tonal, Vector2.zero, ToggleMenu, 40f).gameObject;
            var menuFrame = Frame("Menu Frame", root, new Vector2(1f, 1f), new Vector2(-12f, -60f), new Vector2(200f, 188f));
            var menu = M3.Surface("Menu", menuFrame, Vector2.zero, new Vector2(200f, 188f), M3Theme.SurfaceContainer, M3Theme.k_ShapeSmall);
            M3.Button("Set viewer", menu.rectTransform, "Set viewer", M3Button.Variant.Text, new Vector2(0f, 60f), ChooseSetViewer,
                "qr_code_scanner", 184f);
            m_View = M3.Button("View", menu.rectTransform, "Phone view", M3Button.Variant.Text, new Vector2(0f, 0f), ToggleView,
                "smartphone", 184f);
            // The settings panel is summoned by looking at a palm and pinching; this is for when there's
            // no hand to do it with.
            M3.Button("Open settings", menu.rectTransform, "Open settings", M3Button.Variant.Text, new Vector2(0f, -60f),
                ChooseOpenSettings, "settings", 184f);
            m_Menu = menuFrame.gameObject;
            m_Menu.AddComponent<M3Fade>();
            m_Menu.SetActive(false);
            UpdateViewLabel();

            m_ScanPanel = new GameObject("Scan Panel", typeof(Image));
            m_ScanPanel.transform.SetParent(root, false);
            Stretch((RectTransform)m_ScanPanel.transform);
            m_ScanPanel.GetComponent<Image>().color = M3Theme.Surface;

            var previewObject = new GameObject("Camera Preview", typeof(RawImage), typeof(AspectRatioFitter));
            previewObject.transform.SetParent(m_ScanPanel.transform, false);
            Stretch((RectTransform)previewObject.transform);
            m_Preview = previewObject.GetComponent<RawImage>();
            m_Preview.raycastTarget = false;
            m_PreviewFitter = previewObject.GetComponent<AspectRatioFitter>();
            m_PreviewFitter.aspectMode = AspectRatioFitter.AspectMode.FitInParent;

            var scanRect = (RectTransform)m_ScanPanel.transform;
            var top = Frame("Top", scanRect, new Vector2(0.5f, 1f), new Vector2(0f, -16f), new Vector2(600f, 64f));
            var statusBar = M3.Surface("Status Bar", top, Vector2.zero, new Vector2(600f, 64f), M3Theme.SurfaceContainerHigh,
                M3Theme.k_ShapeLarge);
            m_Status = M3.Text("Status", statusBar.rectTransform, "", M3Theme.Type.BodyLarge, Vector2.zero, new Vector2(568f, 56f));
            var actions = Frame("Actions", scanRect, new Vector2(0.5f, 0f), new Vector2(0f, 16f), new Vector2(420f, 56f));
            M3.Button("Cancel", actions, "Cancel", M3Button.Variant.Tonal, new Vector2(-110f, 0f), () => SetScanning(false), "close", 180f);
            M3.Button("Use Cardboard v1", actions, "Use Cardboard v1", M3Button.Variant.Filled, new Vector2(100f, 0f), UseCardboardV1,
                null, 210f);

            // A Material snackbar: inverse colors, low on the screen.
            var bottom = Frame("Snackbar Frame", root, new Vector2(0.5f, 0f), new Vector2(0f, 72f), new Vector2(560f, 56f));
            var snackbar = M3.Surface("Snackbar", bottom, Vector2.zero, new Vector2(560f, 56f), M3Theme.InverseSurface, 4f);
            m_Toast = M3.Text("Message", snackbar.rectTransform, "", M3Theme.Type.BodyMedium, Vector2.zero, new Vector2(528f, 48f),
                M3Theme.InverseOnSurface, TextAlignmentOptions.Left);
            m_Snackbar = snackbar.gameObject;
            m_Snackbar.AddComponent<M3Fade>();
            m_Snackbar.SetActive(false);
        }

        // How long the menu stays open without a choice, in seconds.
        const float k_MenuSeconds = 6f;

        // Reference pixels per dp: the canvas is 1080 tall, about 412 dp on a phone held sideways.
        const float k_DpOnScreen = 2.6f;

        // A frame laid out in dp, anchored and pivoted at a point of its parent, its own size in dp.
        static RectTransform Frame(string name, RectTransform parent, Vector2 anchor, Vector2 offsetDp, Vector2 sizeDp)
        {
            var frame = new GameObject(name, typeof(RectTransform));
            frame.transform.SetParent(parent, false);
            var rect = (RectTransform)frame.transform;
            rect.anchorMin = rect.anchorMax = rect.pivot = anchor;
            rect.sizeDelta = sizeDp;
            rect.localScale = Vector3.one * k_DpOnScreen;
            rect.anchoredPosition = offsetDp * k_DpOnScreen;
            return rect;
        }

        static void Stretch(RectTransform rect)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
        }
    }
}
