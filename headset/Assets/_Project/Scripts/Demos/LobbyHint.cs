using CardboardXR.UI;
using UnityEngine;

namespace CardboardXR.Demos
{
    // The lobby's one line of guidance, low in front of where the user starts: how to bring back the
    // settings panel and its app library, which open by themselves on entering the lobby (AppLibrary)
    // but can be closed. The lobby is otherwise just a room to start from.
    public class LobbyHint : MonoBehaviour
    {
        [Tooltip("How far the hint is from the user, in meters.")]
        [SerializeField] float m_Distance = M3Theme.k_PanelDistance;
        [Tooltip("Where it is, in degrees above (+) or below (-) eye level.")]
        [SerializeField] float m_Pitch = -21f;

        void Start()
        {
            var hint = M3.Canvas("Apps Hint", transform, new Vector2(560f, 32f), m_Distance);
            var direction = Quaternion.Euler(-m_Pitch, 0f, 0f) * Vector3.forward;
            hint.SetPositionAndRotation(direction * m_Distance, Quaternion.LookRotation(direction, Vector3.up));
            M3.Icon("Icon", hint, "apps", 20f, new Vector2(-230f, 0f), M3Theme.OnSurfaceVariant);
            M3.Text("Text", hint, "Look at your palm, then pinch, for apps and settings", M3Theme.Type.BodyLarge, new Vector2(16f, 0f),
                new Vector2(460f, 32f), M3Theme.OnSurfaceVariant);
        }
    }
}
