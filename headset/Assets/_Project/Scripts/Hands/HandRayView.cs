using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.XR.Interaction.Toolkit.Interactors;
using UnityEngine.XR.Interaction.Toolkit.Interactors.Visuals;

namespace CardboardXR.Hands
{
    // A dot on whatever the hand's ray could pick up, and nothing the rest of the time: the ray only
    // grabs, so a cursor that followed it everywhere would be clutter beside the gaze reticle, which is
    // what aims at UI and surfaces. It's white, and yellow while pinching. The ray runs out from the
    // eyes through the fingertips, so it's seen end-on and there's no line worth drawing.
    public class HandRayView : MonoBehaviour
    {
        // URP's main color property.
        static readonly int k_BaseColor = Shader.PropertyToID("_BaseColor");
        static readonly Color k_Ready = Color.white;
        static readonly Color k_Pinching = new(1f, 0.85f, 0.2f);
        // After the transparent queue, which holds the room boundary's grid, so the grid, which
        // ignores depth, never covers the dot.
        const int k_RenderQueue = 3150;

        [SerializeField] HandInteractorRig m_Rig;
        [SerializeField] NearFarInteractor m_Interactor;
        [SerializeField] Material m_Material;
        [Tooltip("The dot's size as an angle, in degrees, so it looks the same at any distance.")]
        [SerializeField] float m_CursorDegrees = 0.8f;

        Renderer m_Cursor;
        MaterialPropertyBlock m_Block;

        void Start()
        {
            var cursor = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            cursor.name = "Hand Ray Cursor";
            Destroy(cursor.GetComponent<Collider>());
            cursor.transform.SetParent(transform, false);
            m_Cursor = cursor.GetComponent<Renderer>();
            // Set here rather than on the saved material, where URP resets the queue.
            m_Cursor.sharedMaterial = new Material(m_Material) { renderQueue = k_RenderQueue };
            m_Cursor.shadowCastingMode = ShadowCastingMode.Off;
            m_Cursor.receiveShadows = false;
            m_Block = new MaterialPropertyBlock();
        }

        void LateUpdate()
        {
            // Only shown on something the hand could take, so it reads as "this is what a pinch gets".
            var shown = m_Rig.IsActive && m_Rig.HasGrabTarget;
            m_Cursor.enabled = shown;
            if (!shown)
                return;

            var end = m_Interactor.TryGetCurveEndPoint(out var point, snapToSelectedAttachIfAvailable: true);
            if (end is EndPointType.None or EndPointType.EmptyCastHit)
                return;

            var dot = m_Cursor.transform;
            dot.position = point;
            var distance = Vector3.Distance(m_Interactor.transform.position, point);
            dot.localScale = Vector3.one * (2f * distance * Mathf.Tan(m_CursorDegrees * Mathf.Deg2Rad / 2f));
            m_Block.SetColor(k_BaseColor, m_Rig.IsPinching ? k_Pinching : k_Ready);
            m_Cursor.SetPropertyBlock(m_Block);
        }
    }
}
