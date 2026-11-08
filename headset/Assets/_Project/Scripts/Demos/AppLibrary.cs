using System.Collections.Generic;
using CardboardXR.Hands;
using CardboardXR.Stereo;
using CardboardXR.UI;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace CardboardXR.Demos
{
    // The app library, like visionOS's or Quest's: a grid of app icons and names above the settings
    // panel, which its Apps button shows and hides. The lobby comes first, then each demo in the
    // catalog; pick one and the panel closes and that app opens, from wherever you are, so there's no
    // need to go back to the lobby between demos (DemoManager restores what one demo switched on
    // before applying the next's needs). The app running now is marked with a dot under its name.
    // Looking at an app shows its one-line tagline under the grid. Entering the lobby opens the
    // settings panel with the library showing (Open, from LobbyFlow), since the lobby is only a room
    // to start from. Part of the settings panel, so it follows the panel and
    // closes with it. Tiles are built at runtime from the catalog, so a new demo needs only a catalog
    // entry. A page holds k_Rows rows, so the library stays as short however many demos there are; with
    // more apps than that, arrows either side of the grid turn the page, dots under it say which page
    // it is, and it opens on the page with the app that's running. The "libraryRows" launch option
    // makes pages shorter, to try paging with few demos. Lives on an object that's always active (the
    // Demos object), and shows and hides m_Root. Logs [Demos].
    public class AppLibrary : MonoBehaviour
    {
        // In dp: each app's cell, its icon tile, and the gaps between cells and around the grid.
        public const float k_CellWidth = 128f;
        // Kept short, so it sits at eye level with the settings panel close below it.
        public const float k_CellHeight = 124f;
        const float k_IconSize = 72f;
        const float k_IconRadius = 18f;
        const float k_Gap = 8f;
        const float k_Padding = 16f;
        const float k_TaglineHeight = 28f;
        public const int k_Columns = 3;
        // Rows a page: two keep the library short enough to sit at eye level with the settings panel
        // close below it. A third row would push the panel 5 degrees further down, and each row after
        // that 5 more.
        public const int k_Rows = 2;
        // When there's more than a page: an arrow either side of the grid, and a row of page dots
        // between the grid and the tagline.
        const float k_ArrowSize = M3Theme.k_Target;
        const float k_DotsHeight = 14f;
        const float k_DotSize = 6f;
        const float k_DotSpacing = 12f;
        const string k_Prompt = "Look at an app to see what it is";
        // Picks this soon after the library appears are ignored: it opens where the user is looking,
        // and the pinch that opened it (or a flicker of it) landed on a tile 0.6 s later, reopening the
        // demo just left.
        const float k_PickGuardSeconds = 0.8f;

        [SerializeField] DemoManager m_Demos;
        [SerializeField] SettingsSummoner m_Settings;
        [Tooltip("The settings panel's Apps button, shown selected while the library shows.")]
        [SerializeField] M3Button m_AppsButton;
        [Tooltip("The library's surface, under the settings panel, shown and hidden.")]
        [SerializeField] RectTransform m_Root;
        [Tooltip("Where the tiles go.")]
        [SerializeField] RectTransform m_Grid;
        [SerializeField] TMP_Text m_Tagline;
        [Header("The lobby, which isn't in the catalog")]
        [SerializeField] string m_LobbyTitle = "Lobby";
        [SerializeField] string m_LobbyTagline = "A quiet room to start from";
        [SerializeField] string m_LobbyIcon = "home";
        [SerializeField] Color m_LobbyTint = new(0.29f, 0.36f, 0.54f);

        readonly List<(DemoCatalog.Entry entry, GameObject running)> m_Tiles = new();
        readonly List<GameObject> m_Cells = new();
        readonly List<Image> m_Dots = new();
        M3Button m_Previous;
        M3Button m_Next;
        int m_Rows = k_Rows;
        int m_Page;
        int m_Pages = 1;
        float m_ShownAt = float.NegativeInfinity;
        bool m_Built;

        public bool IsShown => m_Root != null && M3Fade.IsShown(m_Root.gameObject);
        // Which page shows, from 0, and how many there are.
        public int Page => m_Page;
        public int PageCount => m_Pages;

        public static int PerPage(int rows) => k_Columns * Mathf.Max(1, rows);

        // How many pages this many apps take, at rows a page.
        public static int Pages(int apps, int rows = k_Rows) => Mathf.Max(1, (apps + PerPage(rows) - 1) / PerPage(rows));

        // The page an app is on, by its place in the library (the lobby is 0).
        public static int PageOf(int index, int rows = k_Rows) => Mathf.Max(0, index) / PerPage(rows);

        // The library's size in dp for this many apps: the grid, as many rows as a page holds at most,
        // and the tagline under it; with more than a page, the arrows beside the grid and the page
        // dots under it too.
        public static Vector2 Size(int apps, int rows = k_Rows)
        {
            var paged = Pages(apps, rows) > 1;
            var shown = paged ? Mathf.Max(1, rows) : Mathf.Max(1, (apps + k_Columns - 1) / k_Columns);
            var width = k_Columns * k_CellWidth + (k_Columns - 1) * k_Gap + 2f * k_Padding;
            var height = shown * k_CellHeight + (shown - 1) * k_Gap + k_Gap + k_TaglineHeight + 2f * k_Padding;
            if (paged)
            {
                width += 2f * (k_ArrowSize + k_Gap);
                height += k_DotsHeight;
            }
            return new Vector2(width, height);
        }

        // Where an app's cell goes, its middle from the library's middle, in dp: the same place on its
        // own page as the app that many pages before it.
        public static Vector2 CellPosition(int index, int apps, int rows = k_Rows)
        {
            var size = Size(apps, rows);
            var slot = index % PerPage(rows);
            var column = slot % k_Columns;
            var row = slot / k_Columns;
            var left = -size.x / 2f + k_Padding + (Pages(apps, rows) > 1 ? k_ArrowSize + k_Gap : 0f);
            var x = left + k_CellWidth / 2f + column * (k_CellWidth + k_Gap);
            var y = size.y / 2f - k_Padding - k_CellHeight / 2f - row * (k_CellHeight + k_Gap);
            return new Vector2(x, y);
        }

        // The tagline's middle: at the bottom, under the grid (and the page dots).
        public static Vector2 TaglinePosition(Vector2 size) => new(0f, -size.y / 2f + k_Padding + k_TaglineHeight / 2f);

        void Awake()
        {
            m_Rows = Mathf.Clamp(LaunchOptions.GetInt("libraryRows", k_Rows), 1, 4);
            if (m_Settings != null)
                m_Settings.OpenChanged += OnSettingsOpenChanged;
            Layout();
        }

        // Sizes the surface and places the tagline for the catalog's apps at this many rows a page,
        // which the build only knows the default of. Before the panel first opens, so the settings
        // panel is placed below the library's real height.
        void Layout()
        {
            if (m_Demos == null || m_Demos.Catalog == null || m_Root == null)
                return;
            var size = Size(m_Demos.Catalog.Demos.Count + 1, m_Rows);
            m_Root.sizeDelta = size;
            if (m_Grid != null)
                m_Grid.sizeDelta = size;
            if (m_Tagline != null)
            {
                m_Tagline.rectTransform.anchoredPosition = TaglinePosition(size);
                m_Tagline.rectTransform.sizeDelta = new Vector2(size.x - 2f * k_Padding, k_TaglineHeight);
            }
        }

        void OnDestroy()
        {
            if (m_Settings != null)
                m_Settings.OpenChanged -= OnSettingsOpenChanged;
        }

        // The Apps button.
        public void Toggle() => Show(!IsShown);

        public void Show(bool show)
        {
            if (show)
                Build();
            if (show && !IsShown)
            {
                m_ShownAt = Time.unscaledTime;
                ShowPage(PageOf(RunningIndex(), m_Rows));
            }
            M3Fade.SetShown(m_Root.gameObject, show);
            if (m_AppsButton != null)
                m_AppsButton.Selected = show;
            if (show)
            {
                m_Tagline.text = k_Prompt;
                MarkRunning();
            }
        }

        // The settings panel, opened in front of the user, with the library showing: how the lobby
        // greets the user (LobbyFlow).
        public void Open()
        {
            if (m_Settings != null)
                m_Settings.Open();
            Show(true);
        }

        // Closed with the panel, so it opens next time as the panel alone.
        void OnSettingsOpenChanged(bool open)
        {
            if (!open && IsShown)
                Show(false);
        }

        void Pick(DemoCatalog.Entry entry)
        {
            var title = entry != null ? entry.Title : m_LobbyTitle;
            if (Time.unscaledTime - m_ShownAt < k_PickGuardSeconds)
            {
                Debug.Log($"[Demos] Ignored a pick of {title} {Time.unscaledTime - m_ShownAt:F2} s after the library appeared");
                return;
            }
            if (m_Settings != null)
                m_Settings.Close();
            if (entry == m_Demos.Current)
            {
                Debug.Log($"[Demos] Picked {title} in the app library: already running");
                return;
            }
            Debug.Log($"[Demos] Picked {title} in the app library");
            if (entry == null)
                m_Demos.ExitToLobby();
            else
                m_Demos.Enter(entry);
        }

        void MarkRunning()
        {
            foreach (var (entry, running) in m_Tiles)
                running.SetActive(entry == m_Demos.Current);
        }

        // The running app's place in the library: 0 for the lobby, then the demos in catalog order.
        int RunningIndex()
        {
            var current = m_Demos != null ? m_Demos.Current : null;
            return current == null || m_Demos.Catalog == null ? 0 : m_Demos.Catalog.IndexOf(current.Id) + 1;
        }

        // The lobby's tile, then one per demo, and the arrows and page dots if they take more than a
        // page; once.
        public void Build()
        {
            if (m_Built || m_Demos == null || m_Demos.Catalog == null)
                return;
            m_Built = true;
            var demos = m_Demos.Catalog.Demos;
            var apps = demos.Count + 1;
            m_Pages = Pages(apps, m_Rows);
            Tile(0, apps, null, m_LobbyTitle, m_LobbyTagline, m_LobbyIcon, m_LobbyTint);
            for (var i = 0; i < demos.Count; i++)
                Tile(i + 1, apps, demos[i], demos[i].Title, demos[i].Tagline, demos[i].Icon, demos[i].Tint);
            if (m_Pages > 1)
            {
                BuildPaging(apps);
                Debug.Log($"[Demos] App library: {apps} apps on {m_Pages} pages of {PerPage(m_Rows)}");
            }
            ShowPage(0);
        }

        // The arrows, round and tonal, either side of the grid at its middle height, and a dot per page
        // centered between the grid and the tagline.
        void BuildPaging(int apps)
        {
            var size = Size(apps, m_Rows);
            var gridHeight = m_Rows * k_CellHeight + (m_Rows - 1) * k_Gap;
            var y = size.y / 2f - k_Padding - gridHeight / 2f;
            var x = size.x / 2f - k_Padding - k_ArrowSize / 2f;
            m_Previous = M3.IconButton("Previous Apps", m_Grid, "chevron_left", M3Button.Variant.Tonal, new Vector2(-x, y), () => Turn(-1));
            m_Next = M3.IconButton("Next Apps", m_Grid, "chevron_right", M3Button.Variant.Tonal, new Vector2(x, y), () => Turn(1));
            var dotsY = -size.y / 2f + k_Padding + k_TaglineHeight + k_DotsHeight / 2f;
            for (var page = 0; page < m_Pages; page++)
            {
                var dotX = (page - (m_Pages - 1) / 2f) * k_DotSpacing;
                m_Dots.Add(M3.Surface($"Page {page + 1}", m_Grid, new Vector2(dotX, dotsY), Vector2.one * k_DotSize, M3Theme.Outline,
                    M3Theme.k_ShapeFull));
            }
        }

        // An arrow: the page before or after, stopping at the first and last.
        void Turn(int step)
        {
            var page = Mathf.Clamp(m_Page + step, 0, m_Pages - 1);
            if (page == m_Page)
                return;
            ShowPage(page);
            Debug.Log($"[Demos] App library page {m_Page + 1} of {m_Pages}");
        }

        // Shows that page's tiles only, marks its dot, and dims an arrow that has no page to go to.
        void ShowPage(int page)
        {
            m_Page = Mathf.Clamp(page, 0, m_Pages - 1);
            var perPage = PerPage(m_Rows);
            for (var i = 0; i < m_Cells.Count; i++)
                m_Cells[i].SetActive(i / perPage == m_Page);
            for (var i = 0; i < m_Dots.Count; i++)
                m_Dots[i].color = i == m_Page ? M3Theme.Primary : M3Theme.Outline;
            if (m_Previous != null)
                m_Previous.Interactable = m_Page > 0;
            if (m_Next != null)
                m_Next.Interactable = m_Page < m_Pages - 1;
            // A tile on the page just left may have been the one looked at.
            if (m_Tagline != null)
                m_Tagline.text = k_Prompt;
        }

        // One app: an icon on a colored rounded square over its name, the whole cell the target, with
        // a state layer behind it for the gaze, and a dot under the name while it's running.
        void Tile(int index, int apps, DemoCatalog.Entry entry, string title, string tagline, string icon, Color tint)
        {
            var cell = M3.Group($"App {title}", m_Grid, CellPosition(index, apps, m_Rows), new Vector2(k_CellWidth, k_CellHeight));
            m_Cells.Add(cell.gameObject);
            var state = M3.Surface("State Layer", cell, Vector2.zero, cell.sizeDelta, Color.clear, M3Theme.k_ShapeLarge,
                raycastTarget: true);
            var button = cell.gameObject.AddComponent<Button>();
            button.transition = Selectable.Transition.None;
            button.targetGraphic = state;
            button.onClick.AddListener(() => Pick(entry));
            cell.gameObject.AddComponent<M3StateLayer>().Initialize(state, button, M3Theme.OnSurface);

            var tile = M3.Surface("Icon Tile", cell, new Vector2(0f, 16f), new Vector2(k_IconSize, k_IconSize), tint, k_IconRadius);
            M3.Icon("Icon", tile.rectTransform, icon, 40f, Vector2.zero, Color.white);
            var name = M3.Text("Name", cell, title, M3Theme.Type.LabelLarge, new Vector2(0f, -36f), new Vector2(k_CellWidth - 8f, 20f));
            name.textWrappingMode = TextWrappingModes.NoWrap;
            var running = M3.Surface("Running", cell, new Vector2(0f, -53f), new Vector2(6f, 6f), M3Theme.Primary, M3Theme.k_ShapeFull)
                .gameObject;
            running.SetActive(false);
            cell.gameObject.AddComponent<AppTileGaze>().Initialize(m_Tagline, string.IsNullOrEmpty(tagline) ? title : tagline, k_Prompt);
            m_Tiles.Add((entry, running));
        }
    }
}
