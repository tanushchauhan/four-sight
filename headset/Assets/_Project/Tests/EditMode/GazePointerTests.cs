using System.Collections.Generic;
using CardboardXR.Interaction;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;

namespace CardboardXR.Stereo.Tests
{
    // The pointer sits at the origin looking down +z.
    public class GazePointerTests
    {
        readonly List<GameObject> m_Created = new();

        [TearDown]
        public void TearDown()
        {
            foreach (var created in m_Created)
            {
                if (created != null)
                    Object.DestroyImmediate(created);
            }
            m_Created.Clear();
        }

        [Test]
        public void Collider_GetsEnterDownUpClickExit()
        {
            var pointer = CreatePointer();
            var cube = CreateCube(new Vector3(0f, 0f, 2f));
            var recorder = cube.AddComponent<PointerEventRecorder>();

            pointer.UpdateGaze();
            Assert.AreSame(cube, pointer.Target);
            Assert.AreEqual(1.5f, pointer.TargetDistance, 1e-4f, "The cube's near face is 1.5 m away.");

            pointer.TriggerDown();
            pointer.TriggerUp();
            MoveTo(cube, new Vector3(0f, 5f, 2f));
            pointer.UpdateGaze();

            Assert.IsNull(pointer.Target);
            Assert.AreEqual("enter down up click exit", recorder.ToString());
        }

        [Test]
        public void LookingAwayBeforeRelease_DoesNotClick()
        {
            var pointer = CreatePointer();
            var cube = CreateCube(new Vector3(0f, 0f, 2f));
            var recorder = cube.AddComponent<PointerEventRecorder>();

            pointer.UpdateGaze();
            pointer.TriggerDown();
            MoveTo(cube, new Vector3(0f, 5f, 2f));
            pointer.UpdateGaze();
            pointer.TriggerUp();

            Assert.AreEqual("enter down exit up", recorder.ToString());
        }

        [Test]
        public void WorldSpaceButton_IsClickedOverItsBackground()
        {
            var pointer = CreatePointer();
            var canvas = CreateWorldCanvas(new Vector3(0f, 0f, 2f));
            CreateGraphic<Image>(canvas, "Background", new Vector2(1000f, 1000f));
            var button = CreateGraphic<Image>(canvas, "Button", new Vector2(200f, 100f)).gameObject.AddComponent<Button>();
            var clicks = 0;
            button.onClick.AddListener(() => clicks++);

            pointer.UpdateGaze();
            Assert.AreSame(button.gameObject, pointer.Target, "The button is drawn over the background.");
            Assert.AreEqual(2f, pointer.TargetDistance, 1e-4f);

            pointer.TriggerDown();
            pointer.TriggerUp();
            Assert.AreEqual(1, clicks);
        }

        [Test]
        public void NearestTargetWins()
        {
            var pointer = CreatePointer();
            CreateCube(new Vector3(0f, 0f, 4f));
            var canvas = CreateWorldCanvas(new Vector3(0f, 0f, 2f));
            var panel = CreateGraphic<Image>(canvas, "Panel", new Vector2(500f, 500f));

            pointer.UpdateGaze();
            Assert.AreSame(panel.gameObject, pointer.Target);

            var nearCube = CreateCube(new Vector3(0f, 0f, 1f));
            pointer.UpdateGaze();
            Assert.AreSame(nearCube, pointer.Target);
        }

        [Test]
        public void RealSurfaces_DoNotHideGraphicsBehindThem()
        {
            const int realSurfaceLayer = 30;
            var pointer = CreatePointer();
            pointer.RealSurfaceMask = 1 << realSurfaceLayer;
            var surface = CreateCube(new Vector3(0f, 0f, 1f));
            surface.layer = realSurfaceLayer;
            var canvas = CreateWorldCanvas(new Vector3(0f, 0f, 2f));
            var panel = CreateGraphic<Image>(canvas, "Panel", new Vector2(500f, 500f));

            pointer.UpdateGaze();
            Assert.AreSame(panel.gameObject, pointer.Target);

            canvas.gameObject.SetActive(false);
            pointer.UpdateGaze();
            Assert.AreSame(surface, pointer.Target, "With nothing behind it, the surface is the target.");
        }

        [Test]
        public void RealSurfaces_HideOnlyUnclickableObjectsBehindThem()
        {
            const int realSurfaceLayer = 30;
            var pointer = CreatePointer();
            pointer.RealSurfaceMask = 1 << realSurfaceLayer;
            var surface = CreateCube(new Vector3(0f, 0f, 1f));
            surface.layer = realSurfaceLayer;
            CreateCube(new Vector3(0f, 0f, 3f));

            pointer.UpdateGaze();
            Assert.AreSame(surface, pointer.Target, "An object that can't be clicked, like a virtual wall, stays hidden.");

            var clickable = CreateCube(new Vector3(0f, 0f, 2f));
            clickable.AddComponent<PointerEventRecorder>();
            pointer.UpdateGaze();
            Assert.AreSame(clickable, pointer.Target);
        }

        // The virtual room's floor lined up with the detected one: whichever is a hair nearer, the
        // real surface is pointed at, every frame, rather than whichever rounding favours.
        [Test]
        public void VirtualSurfaceLevelWithARealOne_LetsTheRealOneWin()
        {
            const int realSurfaceLayer = 30;
            var pointer = CreatePointer();
            pointer.RealSurfaceMask = 1 << realSurfaceLayer;
            var real = CreateCube(new Vector3(0f, 0f, 2f));
            real.layer = realSurfaceLayer;
            var virtualFloor = CreateCube(new Vector3(0f, 0f, 1.99f));

            pointer.UpdateGaze();
            Assert.AreSame(real, pointer.Target, "1 cm nearer is level.");

            MoveTo(virtualFloor, new Vector3(0f, 0f, 2.03f));
            pointer.UpdateGaze();
            Assert.AreSame(real, pointer.Target);

            MoveTo(virtualFloor, new Vector3(0f, 0f, 1.5f));
            pointer.UpdateGaze();
            Assert.AreSame(virtualFloor, pointer.Target, "A virtual surface clearly nearer still hides the real one.");
        }

        [Test]
        public void RayOverride_PointsInsteadOfTheHead()
        {
            var pointer = CreatePointer();
            var ahead = CreateCube(new Vector3(0f, 0f, 2f));
            var aside = CreateCube(new Vector3(3f, 0f, 2f));

            pointer.UpdateGaze();
            Assert.AreSame(ahead, pointer.Target);

            pointer.RayOverride = new Ray(new Vector3(3f, 0f, 0f), Vector3.forward);
            pointer.UpdateGaze();
            Assert.AreSame(aside, pointer.Target);

            pointer.RayOverride = null;
            pointer.UpdateGaze();
            Assert.AreSame(ahead, pointer.Target);
        }

        [Test]
        public void GraphicsOutsideTheRayOrFacingAway_AreMissed()
        {
            var pointer = CreatePointer();
            var canvas = CreateWorldCanvas(new Vector3(0f, 0f, 2f));
            var offToTheSide = CreateGraphic<Image>(canvas, "Off to the side", new Vector2(100f, 100f));
            ((RectTransform)offToTheSide.transform).anchoredPosition = new Vector2(300f, 0f);

            pointer.UpdateGaze();
            Assert.IsNull(pointer.Target);

            var facingAway = CreateWorldCanvas(new Vector3(0f, 0f, 3f));
            facingAway.transform.rotation = Quaternion.Euler(0f, 180f, 0f);
            CreateGraphic<Image>(facingAway, "Facing away", new Vector2(500f, 500f));
            pointer.UpdateGaze();
            Assert.IsNull(pointer.Target);
        }

        GazePointer CreatePointer() => Track(new GameObject("Head")).AddComponent<GazePointer>();

        GameObject CreateCube(Vector3 position)
        {
            var cube = Track(GameObject.CreatePrimitive(PrimitiveType.Cube));
            MoveTo(cube, position);
            return cube;
        }

        static void MoveTo(GameObject target, Vector3 position)
        {
            target.transform.position = position;
            Physics.SyncTransforms();
        }

        // One canvas unit is a millimeter.
        Canvas CreateWorldCanvas(Vector3 position)
        {
            var canvasObject = Track(new GameObject("Canvas", typeof(RectTransform)));
            var canvas = canvasObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvasObject.AddComponent<GazeCanvas>();
            var rect = (RectTransform)canvasObject.transform;
            rect.sizeDelta = new Vector2(1000f, 1000f);
            rect.localScale = Vector3.one * 0.001f;
            rect.position = position;
            return canvas;
        }

        static T CreateGraphic<T>(Canvas canvas, string name, Vector2 size) where T : Graphic
        {
            var graphicObject = new GameObject(name, typeof(RectTransform));
            graphicObject.transform.SetParent(canvas.transform, false);
            ((RectTransform)graphicObject.transform).sizeDelta = size;
            return graphicObject.AddComponent<T>();
        }

        GameObject Track(GameObject created)
        {
            m_Created.Add(created);
            return created;
        }
    }
}
