using TMPro;
using UnityEngine;

namespace CardboardXR.UI
{
    // The fonts the Material UI is set in, as TextMesh Pro font assets: Roboto in two weights and
    // Material Icons (Round). Kept in Resources so UI built at runtime in any scene finds them; the
    // editor's MaterialAssets step makes it.
    public class M3Assets : ScriptableObject
    {
        public const string k_ResourcePath = "M3Assets";

        [SerializeField] TMP_FontAsset m_Regular;
        [SerializeField] TMP_FontAsset m_Medium;
        [SerializeField] TMP_FontAsset m_Icons;
        [SerializeField] Sprite m_Round;

        static M3Assets s_Instance;

        public static M3Assets Instance => s_Instance != null ? s_Instance : s_Instance = Resources.Load<M3Assets>(k_ResourcePath);

        public TMP_FontAsset Regular => m_Regular;
        public TMP_FontAsset Medium => m_Medium;
        public TMP_FontAsset Icons => m_Icons;
        // M3Shapes' rounded sprite, saved so UI built in the editor keeps it.
        public Sprite Round => m_Round;

        public void Set(TMP_FontAsset regular, TMP_FontAsset medium, TMP_FontAsset icons, Sprite round)
        {
            m_Regular = regular;
            m_Medium = medium;
            m_Icons = icons;
            m_Round = round;
        }
    }
}
