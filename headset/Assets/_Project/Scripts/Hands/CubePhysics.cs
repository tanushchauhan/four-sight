using CardboardXR.Stereo;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

namespace CardboardXR.Hands
{
    // Real physics for a placed object, except while it's held. Free, it falls, slides, bounces off
    // other objects and lands on the room's detected surfaces (their colliders). Held, XRI moves it
    // as a kinematic body (MovementType.Kinematic), which still knocks free objects aside. Let go by
    // opening the fingers and it's thrown with the hand's velocity (XRI's throw on detach).
    //   Losing the hand isn't letting go: when tracking blipped at the camera's edge, an earlier
    //   version dropped what was being carried, which felt bad. So a release XRI cancels (the hand
    //   lost) freezes the object where it is, kinematic, until it's grabbed again or a moving object
    //   knocks into it.
    //   Once free and at rest it's anchored again where it lies (AnchorPlacer.Move), so it stays put
    //   in the room as tracking corrects itself. One that falls well below the head (off the edge of a
    //   surface, or through a gap) is removed. Goes on the object's prefab, with an XRGrabInteractable
    //   and a Rigidbody. Logs [Hands].
    [RequireComponent(typeof(XRGrabInteractable), typeof(Rigidbody))]
    public class CubePhysics : MonoBehaviour
    {
        // Farther than this from the fingertips, in meters, and the ray took it rather than the hand.
        const float k_NearReach = 0.1f;
        // Slower than this, in meters per second, for k_RestSeconds, and it's at rest.
        const float k_RestSpeed = 0.03f;
        const float k_RestSeconds = 0.5f;
        // Below the head by this much, in meters, and it's gone.
        const float k_LostDepth = 3f;
        // Throws faster than this, in meters per second, are slowed to it: a tracking jump at the
        // release can read as a very fast hand.
        public const float k_MaxThrowSpeed = 6f;
        // A knock this fast frees a frozen object, in meters per second.
        const float k_FreeingSpeed = 0.2f;

        static AnchorPlacer s_Placer;
        static HeadPoseFusion s_Head;

        XRGrabInteractable m_Grab;
        Rigidbody m_Body;
        Vector3 m_PickedUpAt;
        bool m_Frozen;
        // Free and not yet anchored where it came to rest.
        bool m_Settling;
        float m_StillFor;
        float m_ThrownAt = float.NegativeInfinity;

        void Awake()
        {
            m_Grab = GetComponent<XRGrabInteractable>();
            m_Body = GetComponent<Rigidbody>();
        }

        void OnEnable()
        {
            m_Grab.selectEntered.AddListener(OnSelectEntered);
            m_Grab.selectExited.AddListener(OnSelectExited);
            // Placed: it drops onto the surface and is anchored there once it's still.
            m_Settling = true;
        }

        void OnDisable()
        {
            m_Grab.selectEntered.RemoveListener(OnSelectEntered);
            m_Grab.selectExited.RemoveListener(OnSelectExited);
        }

        void OnSelectEntered(SelectEnterEventArgs args)
        {
            m_PickedUpAt = transform.position;
            m_Frozen = false;
            m_Settling = false;
            // How far away it is says whether the hand or the ray took it: the interactor's own near or
            // far region can still read near when the select arrives.
            var distance = Vector3.Distance(args.interactorObject.transform.position, transform.position);
            Debug.Log($"[Hands] Picked up {name} {(distance > k_NearReach ? "with the ray" : "by hand")}, {distance:F2} m from the fingertips");
        }

        void OnSelectExited(SelectExitEventArgs args)
        {
            var moved = Vector3.Distance(m_PickedUpAt, transform.position);
            if (args.isCanceled)
            {
                Freeze();
                Debug.Log($"[Hands] Lost the hand holding {name}, {moved:F2} m from where it was picked up: it stays where it was");
                Anchor();
                return;
            }
            // Free: XRI put back the body as it was before the grab, which may have been frozen.
            m_Body.isKinematic = false;
            m_Body.useGravity = true;
            m_Settling = true;
            m_StillFor = 0f;
            m_ThrownAt = Time.time;
            Debug.Log($"[Hands] Let go of {name} {moved:F2} m from where it was picked up");
        }

        void Freeze()
        {
            m_Frozen = true;
            m_Settling = false;
            m_Body.isKinematic = true;
            m_Body.useGravity = false;
        }

        void FixedUpdate()
        {
            if (m_Grab.isSelected || m_Body.isKinematic)
                return;
            // XRI applies the throw just after the release: keep it within reason for a moment.
            if (Time.time - m_ThrownAt < 0.2f && m_Body.linearVelocity.magnitude > k_MaxThrowSpeed)
                m_Body.linearVelocity = m_Body.linearVelocity.normalized * k_MaxThrowSpeed;
        }

        void Update()
        {
            if (m_Grab.isSelected || m_Frozen)
                return;
            if (s_Head == null)
                s_Head = FindAnyObjectByType<HeadPoseFusion>();
            if (s_Head != null && transform.position.y < s_Head.transform.position.y - k_LostDepth)
            {
                Debug.Log($"[Hands] {name} fell out of the room; removing it");
                Remove();
                return;
            }
            if (!m_Settling)
                return;
            var still = m_Body.IsSleeping() || m_Body.linearVelocity.magnitude < k_RestSpeed;
            m_StillFor = still ? m_StillFor + Time.deltaTime : 0f;
            if (m_StillFor < k_RestSeconds)
                return;
            m_Settling = false;
            Anchor();
        }

        // Knocked by a moving object: frozen ones come free; resting ones settle again after.
        void OnCollisionEnter(Collision collision)
        {
            if (collision.relativeVelocity.magnitude < k_FreeingSpeed || m_Grab.isSelected)
                return;
            if (m_Frozen)
            {
                m_Frozen = false;
                m_Body.isKinematic = false;
                m_Body.useGravity = true;
                Debug.Log($"[Hands] {name} knocked free");
            }
            m_Settling = true;
            m_StillFor = 0f;
        }

        void Anchor()
        {
            if (s_Placer == null)
                s_Placer = FindAnyObjectByType<AnchorPlacer>();
            if (s_Placer != null)
                s_Placer.Move(gameObject, new Pose(transform.position, transform.rotation));
        }

        void Remove()
        {
            if (s_Placer == null)
                s_Placer = FindAnyObjectByType<AnchorPlacer>();
            var anchor = GetComponentInParent<ARAnchor>();
            if (s_Placer != null && anchor != null)
                s_Placer.Remove(anchor);
            else
                gameObject.SetActive(false);
        }
    }
}
