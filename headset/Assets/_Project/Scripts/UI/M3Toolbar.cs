using UnityEngine;
using UnityEngine.UI;

namespace CardboardXR.UI
{
    // Lays out an orbiter toolbar's items in a row, in groups with a divider between them, from only
    // the items showing: hide one and the rest close up and the toolbar narrows, with no gap where it
    // was. A group with nothing showing takes its divider with it. Resizes the toolbar's surface and
    // any full-width lines on it (such as a status line) to match. Lays out when built and whenever an
    // item is shown or hidden.
    [ExecuteAlways]
    public class M3Toolbar : MonoBehaviour
    {
        [SerializeField] RectTransform[] m_Items = new RectTransform[0];
        [Tooltip("Each item's group, in the same order; items of a group sit together.")]
        [SerializeField] int[] m_Groups = new int[0];
        [Tooltip("One per group, drawn before it when an earlier group is showing; the first is unused.")]
        [SerializeField] Image[] m_Dividers = new Image[0];
        [Tooltip("Stretched to the toolbar's inner width, such as a status line above the items.")]
        [SerializeField] RectTransform[] m_FullWidth = new RectTransform[0];
        [SerializeField] float m_ItemWidth = 92f;
        [SerializeField] float m_Padding = 12f;
        [SerializeField] float m_DividerGap = 16f;
        [SerializeField] float m_ItemY;

        bool[] m_Shown;

        public const float k_DividerHeight = 40f;

        public void Initialize(RectTransform[] items, int[] groups, Image[] dividers, RectTransform[] fullWidth, float itemWidth,
            float padding, float itemY)
        {
            m_Items = items;
            m_Groups = groups;
            m_Dividers = dividers;
            m_FullWidth = fullWidth;
            m_ItemWidth = itemWidth;
            m_Padding = padding;
            m_ItemY = itemY;
            Layout();
        }

        void OnEnable() => Layout();

        void LateUpdate()
        {
            if (Changed())
                Layout();
        }

        bool Changed()
        {
            if (m_Shown == null || m_Shown.Length != m_Items.Length)
                return true;
            for (var i = 0; i < m_Items.Length; i++)
            {
                if (m_Items[i] != null && m_Items[i].gameObject.activeSelf != m_Shown[i])
                    return true;
            }
            return false;
        }

        // Where each showing item and divider goes, left to right, and how wide the row is. Pure, for
        // testing: shown says which items show, groups their groups. Returns the row's width; item and
        // divider x positions are filled in (NaN for those hidden), measured from the row's left.
        public static float Arrange(bool[] shown, int[] groups, int groupCount, float itemWidth, float dividerGap,
            float[] itemX, float[] dividerX)
        {
            for (var i = 0; i < itemX.Length; i++)
                itemX[i] = float.NaN;
            for (var g = 0; g < dividerX.Length; g++)
                dividerX[g] = float.NaN;
            var x = 0f;
            var anyBefore = false;
            for (var g = 0; g < groupCount; g++)
            {
                var groupHasItems = false;
                for (var i = 0; i < shown.Length; i++)
                    groupHasItems |= shown[i] && groups[i] == g;
                if (!groupHasItems)
                    continue;
                if (anyBefore && g < dividerX.Length)
                {
                    dividerX[g] = x + dividerGap / 2f;
                    x += dividerGap;
                }
                for (var i = 0; i < shown.Length; i++)
                {
                    if (!shown[i] || groups[i] != g)
                        continue;
                    itemX[i] = x + itemWidth / 2f;
                    x += itemWidth;
                }
                anyBefore = true;
            }
            return x;
        }

        public void Layout()
        {
            if (m_Items == null || m_Groups == null || m_Items.Length != m_Groups.Length)
                return;
            m_Shown = new bool[m_Items.Length];
            var groupCount = 0;
            for (var i = 0; i < m_Items.Length; i++)
            {
                m_Shown[i] = m_Items[i] != null && m_Items[i].gameObject.activeSelf;
                groupCount = Mathf.Max(groupCount, m_Groups[i] + 1);
            }
            var itemX = new float[m_Items.Length];
            var dividerX = new float[m_Dividers.Length];
            var width = Arrange(m_Shown, m_Groups, groupCount, m_ItemWidth, m_DividerGap, itemX, dividerX);

            var rect = (RectTransform)transform;
            rect.sizeDelta = new Vector2(width + 2f * m_Padding, rect.sizeDelta.y);
            var left = -width / 2f;
            for (var i = 0; i < m_Items.Length; i++)
            {
                if (m_Shown[i])
                    m_Items[i].anchoredPosition = new Vector2(left + itemX[i], m_ItemY);
            }
            for (var g = 0; g < m_Dividers.Length; g++)
            {
                if (m_Dividers[g] == null)
                    continue;
                var show = !float.IsNaN(dividerX[g]);
                m_Dividers[g].gameObject.SetActive(show);
                if (show)
                    m_Dividers[g].rectTransform.anchoredPosition = new Vector2(left + dividerX[g], m_ItemY + 8f);
            }
            foreach (var line in m_FullWidth)
            {
                if (line != null)
                    line.sizeDelta = new Vector2(width, line.sizeDelta.y);
            }
        }
    }
}
