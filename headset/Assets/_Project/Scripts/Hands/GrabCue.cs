using CardboardXR.Interaction;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

namespace CardboardXR.Hands
{
    // Shows on an object while the hand could grab it, near or along its ray: a pinch then takes it,
    // where otherwise the pinch clicks whatever the head is looking at. In passthrough the camera
    // image is flat, so how far the hand is from an object can't be seen; reaching for a cube on the
    // iPhone, pinches landed just short of it and clicked the table beside it instead. This says when
    // a pinch will grab. It follows the object's own hover, which is exactly what HandInteractorRig
    // asks before letting a pinch grab.
    [RequireComponent(typeof(XRBaseInteractable), typeof(GazeHighlight))]
    public class GrabCue : MonoBehaviour
    {
        XRBaseInteractable m_Interactable;
        GazeHighlight m_Highlight;

        void Awake()
        {
            m_Interactable = GetComponent<XRBaseInteractable>();
            m_Highlight = GetComponent<GazeHighlight>();
        }

        // Polled rather than following hover events, so the order XRI raises them in can't leave it
        // showing after the hand has gone.
        void LateUpdate() => m_Highlight.GrabReady = m_Interactable.isHovered;

        void OnDisable()
        {
            if (m_Highlight != null)
                m_Highlight.GrabReady = false;
        }
    }
}
