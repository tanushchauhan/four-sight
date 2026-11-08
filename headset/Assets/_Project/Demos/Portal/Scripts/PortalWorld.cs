using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace CardboardXR.Demos.Portal
{
    // A doorway standing on the floor, with a night world beyond it. Built in code when placed. Its
    // "World" child is turned so +z runs from the doorway into the world; the player starts on the
    // -z side, in their room, which passthrough shows.
    //   Outside, a box of depth mask (CardboardXR/DepthMask) wraps the world's front half with a
    //   hole where the door is, so the world shows only through the doorway and the room around it.
    //   Inside, after walking through the frame, the box goes, the world's back half appears around
    //   the player, and a mask in the doorway shows the room through the door behind them.
    // Only a crossing through the frame changes sides (Crossing); walking around it doesn't.
    // When placed it rises out of the floor (Rise): the frame grows up from the sill, overshooting a
    // little, and the view into the world opens with it, under a mask that comes down from the top of
    // the doorway as fast as the frame goes up. Close sinks it back into the floor the same way,
    // before it's taken down.
    // Everything is unlit, and has no colliders, so the gaze passes through. Logs [Portal].
    public class PortalWorld : MonoBehaviour
    {
        public const float k_DoorWidth = 0.9f;
        public const float k_DoorHeight = 2.05f;
        // How far outside the opening a crossing still counts, for a head that clips the frame.
        public const float k_Margin = 0.1f;
        const float k_DomeRadius = 18f;
        // The mask box's half width, height and depth: around everything in the world's front half.
        const float k_BoxHalfWidth = 24f;
        const float k_BoxHeight = 20f;
        const float k_BoxDepth = 29f;
        static readonly int k_BaseColor = Shader.PropertyToID("_BaseColor");
        const float k_RiseSeconds = 1.6f;
        const float k_SinkSeconds = 1.3f;

        static readonly Color[] k_CrystalColors =
        {
            new(0.3f, 0.85f, 1f),
            new(0.75f, 0.4f, 1f),
            new(1f, 0.45f, 0.75f),
            new(0.4f, 1f, 0.7f),
        };

        [SerializeField] Material m_Sky;
        [SerializeField] Material m_Ground;
        [SerializeField] Material m_Glow;
        [SerializeField] Material m_Mask;

        Transform m_World;
        Transform m_Frame;
        GameObject m_BackHalf;
        GameObject m_OutsideMask;
        GameObject m_InsideMask;
        Vector3 m_LastHead;
        bool m_HasLastHead;
        MaterialPropertyBlock m_Block;
        Coroutine m_Animating;

        public bool IsInside { get; private set; }

        // True when the player steps in, false when they step back out.
        public event Action<bool> Crossed;

        void Start()
        {
            m_Block = new MaterialPropertyBlock();
            BuildFrame();
            m_World = new GameObject("World").transform;
            m_World.SetParent(transform, false);
            // The portal was placed facing the player, so the world lies behind it.
            m_World.localRotation = Quaternion.Euler(0f, 180f, 0f);
            BuildWorld();
            SetInside(false, announce: false);
            m_Animating = StartCoroutine(Animate(k_RiseSeconds, BackOut, null));
        }

        // Sinks back into the floor, then calls closed: for taking it down.
        public void Close(Action closed)
        {
            if (m_Animating != null)
                StopCoroutine(m_Animating);
            // From however high it is now.
            var from = m_Frame != null ? Mathf.Clamp01(m_Frame.localScale.y) : 1f;
            m_Animating = StartCoroutine(Animate(k_SinkSeconds, t => from * (1f - t * t * t), closed));
        }

        // Up out of the floor or back into it: the frame's height, and the open part of the doorway,
        // follow height(t) as t goes from 0 to 1 over seconds; then done, if given.
        IEnumerator Animate(float seconds, Func<float, float> height, Action done)
        {
            var cover = Quad("Door Cover", m_World, Vector3.zero, Vector2.one, Quaternion.identity, m_Mask);
            for (var t = 0f; ; t += Time.deltaTime)
            {
                var progress = Mathf.Clamp01(t / seconds);
                var h = height(progress);
                m_Frame.localScale = new Vector3(1f, Mathf.Max(0.001f, h), 1f);
                // The opening follows the frame but never past the top of the doorway.
                var open = Mathf.Clamp01(h) * k_DoorHeight;
                var covered = k_DoorHeight - open;
                cover.localPosition = new Vector3(0f, open + covered / 2f, 0f);
                cover.localScale = new Vector3(k_DoorWidth, Mathf.Max(0.001f, covered), 1f);
                if (progress >= 1f)
                    break;
                yield return null;
            }
            // Open: the cover goes. Closed: it stays, so nothing shows until the portal is taken down.
            if (height(1f) >= 1f)
                Destroy(cover.gameObject);
            m_Animating = null;
            done?.Invoke();
        }

        // Eases out past the end and settles back: 0 to 1 with a small overshoot.
        static float BackOut(float t)
        {
            const float overshoot = 1.4f;
            var u = t - 1f;
            return 1f + u * u * ((overshoot + 1f) * u + overshoot);
        }

        void Update()
        {
            var core = CoreServices.Instance;
            if (core == null || m_World == null)
                return;
            var head = m_World.InverseTransformPoint(core.Head.position);
            if (m_HasLastHead)
            {
                var crossing = Crossing(m_LastHead, head, k_DoorWidth / 2f, k_DoorHeight);
                if (crossing > 0 && !IsInside)
                    SetInside(true, announce: true);
                else if (crossing < 0 && IsInside)
                    SetInside(false, announce: true);
            }
            m_LastHead = head;
            m_HasLastHead = true;
        }

        // +1 if the head went from the room side (z < 0) to the world side through the doorway, -1
        // back out through it, 0 otherwise. Positions are in the world's space, whose z runs through
        // the door, x across it and y up from the floor.
        public static int Crossing(Vector3 before, Vector3 after, float halfWidth, float height)
        {
            var inward = before.z < 0f && after.z >= 0f;
            var outward = before.z >= 0f && after.z < 0f;
            if (!inward && !outward)
                return 0;
            var t = Mathf.Abs(after.z - before.z) > 1e-6f ? -before.z / (after.z - before.z) : 0f;
            var at = Vector3.Lerp(before, after, t);
            var inFrame = Mathf.Abs(at.x) <= halfWidth + k_Margin && at.y >= -k_Margin && at.y <= height + k_Margin;
            if (!inFrame)
                return 0;
            return inward ? 1 : -1;
        }

        void SetInside(bool inside, bool announce)
        {
            IsInside = inside;
            m_BackHalf.SetActive(inside);
            m_OutsideMask.SetActive(!inside);
            m_InsideMask.SetActive(inside);
            if (!announce)
                return;
            Debug.Log(inside ? "[Portal] Stepped through into the world" : "[Portal] Stepped back out into the room");
            Crossed?.Invoke(inside);
        }

        // The door frame: two posts and a lintel, glowing, and a line across the sill, under one parent
        // at the floor that Rise scales up.
        void BuildFrame()
        {
            m_Frame = new GameObject("Frame").transform;
            m_Frame.SetParent(transform, false);
            var frameColor = new Color(0.45f, 0.8f, 1f);
            const float post = 0.08f;
            var half = k_DoorWidth / 2f + post / 2f;
            Box("Left Post", m_Frame, new Vector3(-half, k_DoorHeight / 2f, 0f), new Vector3(post, k_DoorHeight, post), m_Glow, frameColor);
            Box("Right Post", m_Frame, new Vector3(half, k_DoorHeight / 2f, 0f), new Vector3(post, k_DoorHeight, post), m_Glow, frameColor);
            Box("Lintel", m_Frame, new Vector3(0f, k_DoorHeight + post / 2f, 0f), new Vector3(k_DoorWidth + 2f * post, post, post),
                m_Glow, frameColor);
            Box("Sill", m_Frame, new Vector3(0f, 0.005f, 0f), new Vector3(k_DoorWidth, 0.01f, 0.04f), m_Glow, frameColor * 0.7f);
        }

        void BuildWorld()
        {
            var front = new GameObject("Front Half").transform;
            front.SetParent(m_World, false);
            m_BackHalf = new GameObject("Back Half");
            m_BackHalf.transform.SetParent(m_World, false);
            var back = m_BackHalf.transform;

            // Sky, split at the doorway's plane: the front half is what shows through the door.
            Dome("Sky Front", front, -90f, 90f);
            Dome("Sky Back", back, 90f, 270f);
            Ground("Ground Front", front, 0f, k_BoxDepth - 1f);
            Ground("Ground Back", back, -(k_BoxDepth - 1f), 0f);

            Sphere("Moon", front, new Vector3(-6f, 11f, 15f), 2.4f, new Color(1f, 0.95f, 0.75f));
            // A lit path from the door into the world.
            for (var i = 0; i < 8; i++)
                Box($"Path {i}", front, new Vector3(0f, 0.01f, 0.7f + i * 0.8f), new Vector3(0.5f, 0.02f, 0.35f), m_Glow,
                    new Color(0.3f, 0.6f, 1f) * Mathf.Lerp(0.9f, 0.35f, i / 7f));

            var random = new System.Random(11);
            for (var i = 0; i < 16; i++)
            {
                // Crystals scattered around the path, clear of it, on both sides of the door.
                var side = i % 2 == 0 ? -1f : 1f;
                var x = side * Mathf.Lerp(1.5f, 12f, (float)random.NextDouble());
                var z = Mathf.Lerp(-14f, 22f, (float)random.NextDouble());
                if (Mathf.Abs(z) < 1.5f)
                    z += 3f;
                var height = Mathf.Lerp(0.8f, 4.5f, (float)random.NextDouble());
                var crystal = Box($"Crystal {i}", z >= 0f ? front : back, new Vector3(x, height / 2f, z),
                    new Vector3(0.35f, height, 0.35f), m_Glow, k_CrystalColors[i % k_CrystalColors.Length]);
                crystal.localRotation = Quaternion.Euler(0f, 45f + i * 20f, (float)random.NextDouble() * 12f - 6f);
            }

            m_OutsideMask = BuildOutsideMask();
            m_InsideMask = Quad("Inside Mask", m_World, new Vector3(0f, k_DoorHeight / 2f, 0f), new Vector2(k_DoorWidth, k_DoorHeight),
                Quaternion.identity, m_Mask).gameObject;
        }

        // The box around the world's front half, open only where the door is.
        GameObject BuildOutsideMask()
        {
            var mask = new GameObject("Outside Mask").transform;
            mask.SetParent(m_World, false);
            const float bottom = -0.5f;
            var middleY = bottom + k_BoxHeight / 2f;
            var halfDoor = k_DoorWidth / 2f;
            Quad("Far", mask, new Vector3(0f, middleY, k_BoxDepth), new Vector2(2f * k_BoxHalfWidth, k_BoxHeight), Quaternion.identity, m_Mask);
            Quad("Left", mask, new Vector3(-k_BoxHalfWidth, middleY, k_BoxDepth / 2f), new Vector2(k_BoxDepth, k_BoxHeight),
                Quaternion.Euler(0f, 90f, 0f), m_Mask);
            Quad("Right", mask, new Vector3(k_BoxHalfWidth, middleY, k_BoxDepth / 2f), new Vector2(k_BoxDepth, k_BoxHeight),
                Quaternion.Euler(0f, 90f, 0f), m_Mask);
            Quad("Top", mask, new Vector3(0f, bottom + k_BoxHeight, k_BoxDepth / 2f), new Vector2(2f * k_BoxHalfWidth, k_BoxDepth),
                Quaternion.Euler(90f, 0f, 0f), m_Mask);
            Quad("Bottom", mask, new Vector3(0f, bottom, k_BoxDepth / 2f), new Vector2(2f * k_BoxHalfWidth, k_BoxDepth),
                Quaternion.Euler(90f, 0f, 0f), m_Mask);
            // The front, in four pieces around the doorway.
            var sideWidth = k_BoxHalfWidth - halfDoor;
            Quad("Front Left", mask, new Vector3(-halfDoor - sideWidth / 2f, middleY, 0f), new Vector2(sideWidth, k_BoxHeight),
                Quaternion.identity, m_Mask);
            Quad("Front Right", mask, new Vector3(halfDoor + sideWidth / 2f, middleY, 0f), new Vector2(sideWidth, k_BoxHeight),
                Quaternion.identity, m_Mask);
            var aboveHeight = bottom + k_BoxHeight - k_DoorHeight;
            Quad("Above Door", mask, new Vector3(0f, k_DoorHeight + aboveHeight / 2f, 0f), new Vector2(k_DoorWidth, aboveHeight),
                Quaternion.identity, m_Mask);
            Quad("Below Door", mask, new Vector3(0f, bottom / 2f, 0f), new Vector2(k_DoorWidth, -bottom), Quaternion.identity, m_Mask);
            return mask.gameObject;
        }

        void Dome(string name, Transform parent, float fromDegrees, float toDegrees)
        {
            const int rings = 16;
            const int segments = 32;
            var vertices = new List<Vector3>();
            var uvs = new List<Vector2>();
            var triangles = new List<int>();
            for (var r = 0; r <= rings; r++)
            {
                var latitude = Mathf.PI / 2f * r / rings;
                for (var s = 0; s <= segments; s++)
                {
                    var longitude = Mathf.Lerp(fromDegrees, toDegrees, s / (float)segments) * Mathf.Deg2Rad;
                    vertices.Add(new Vector3(Mathf.Cos(latitude) * Mathf.Sin(longitude), Mathf.Sin(latitude),
                        Mathf.Cos(latitude) * Mathf.Cos(longitude)) * k_DomeRadius);
                    uvs.Add(new Vector2(Mathf.Lerp(fromDegrees, toDegrees, s / (float)segments) / 180f, r / (float)rings));
                    if (r == 0 || s == 0)
                        continue;
                    var a = (r - 1) * (segments + 1) + s - 1;
                    var b = r * (segments + 1) + s - 1;
                    triangles.AddRange(new[] { a, b, a + 1, a + 1, b, b + 1 });
                }
            }
            var mesh = new Mesh { name = name };
            mesh.SetVertices(vertices);
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateBounds();
            var dome = new GameObject(name, typeof(MeshFilter), typeof(MeshRenderer));
            dome.transform.SetParent(parent, false);
            dome.GetComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = dome.GetComponent<MeshRenderer>();
            renderer.sharedMaterial = m_Sky;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        }

        void Ground(string name, Transform parent, float fromZ, float toZ)
        {
            var width = 2f * (k_BoxHalfWidth - 1f);
            Quad(name, parent, new Vector3(0f, 0.002f, (fromZ + toZ) / 2f), new Vector2(width, toZ - fromZ), Quaternion.Euler(90f, 0f, 0f),
                m_Ground);
        }

        Transform Box(string name, Transform parent, Vector3 position, Vector3 size, Material material, Color color) =>
            Primitive(PrimitiveType.Cube, name, parent, position, size, Quaternion.identity, material, color);

        void Sphere(string name, Transform parent, Vector3 position, float diameter, Color color) =>
            Primitive(PrimitiveType.Sphere, name, parent, position, Vector3.one * diameter, Quaternion.identity, m_Glow, color);

        Transform Quad(string name, Transform parent, Vector3 position, Vector2 size, Quaternion rotation, Material material) =>
            Primitive(PrimitiveType.Quad, name, parent, position, new Vector3(size.x, size.y, 1f), rotation, material, null);

        Transform Primitive(PrimitiveType type, string name, Transform parent, Vector3 position, Vector3 scale, Quaternion rotation,
            Material material, Color? color)
        {
            var primitive = GameObject.CreatePrimitive(type);
            primitive.name = name;
            Destroy(primitive.GetComponent<Collider>());
            primitive.transform.SetParent(parent, false);
            primitive.transform.SetLocalPositionAndRotation(position, rotation);
            primitive.transform.localScale = scale;
            var renderer = primitive.GetComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            if (color.HasValue)
            {
                m_Block.SetColor(k_BaseColor, color.Value);
                renderer.SetPropertyBlock(m_Block);
            }
            return primitive.transform;
        }
    }
}
