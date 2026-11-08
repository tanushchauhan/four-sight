using CardboardXR.Demos.Video;
using NUnit.Framework;
using UnityEngine;

namespace CardboardXR.Demos.Video.Tests
{
    public class VideoDemoTests
    {
        [TestCase("beach.mp4", Projection.Full360, StereoLayout.Mono)]
        [TestCase("beach_360.mp4", Projection.Full360, StereoLayout.Mono)]
        [TestCase("Concert_360_TB.mov", Projection.Full360, StereoLayout.TopBottom)]
        [TestCase("hike-VR180.mp4", Projection.Half180, StereoLayout.SideBySide)]
        [TestCase("hike 180 sbs.mp4", Projection.Half180, StereoLayout.SideBySide)]
        [TestCase("talk_180_mono.mp4", Projection.Half180, StereoLayout.Mono)]
        [TestCase("dive_360_3dh.mp4", Projection.Full360, StereoLayout.SideBySide)]
        [TestCase("dive_ou.mp4", Projection.Full360, StereoLayout.TopBottom)]
        // Tags only count as whole words.
        [TestCase("tbilisi_1800.mp4", Projection.Full360, StereoLayout.Mono)]
        [TestCase(null, Projection.Full360, StereoLayout.Mono)]
        public void FormatComesFromTheFileName(string name, Projection projection, StereoLayout layout)
        {
            Assert.AreEqual(new VideoFormat(projection, layout), VideoFormat.FromFileName(name));
        }

        [Test]
        public void EachEyeSeesItsOwnPartOfTheFrame()
        {
            var sideBySide = new VideoFormat(Projection.Half180, StereoLayout.SideBySide);
            Assert.AreEqual(new Rect(0f, 0f, 0.5f, 1f), sideBySide.EyeRect(0));
            Assert.AreEqual(new Rect(0.5f, 0f, 0.5f, 1f), sideBySide.EyeRect(1));
            // The left eye's picture is the top one, and texture coordinates start at the bottom.
            var topBottom = new VideoFormat(Projection.Full360, StereoLayout.TopBottom);
            Assert.AreEqual(new Rect(0f, 0.5f, 1f, 0.5f), topBottom.EyeRect(0));
            Assert.AreEqual(new Rect(0f, 0f, 1f, 0.5f), topBottom.EyeRect(1));
            Assert.AreEqual(new Rect(0f, 0f, 1f, 1f), VideoFormat.Mono360.EyeRect(1));
        }

        [Test]
        public void FormatButtonVisitsEveryFormatAndComesBack()
        {
            var format = VideoFormat.Mono360;
            for (var i = 0; i < VideoFormat.All.Length; i++)
                format = format.Next();
            Assert.AreEqual(VideoFormat.Mono360, format);
        }

        [TestCase("https://drive.google.com/file/d/1AbCdEfGhIjKlMnOp_qr-ST/view?usp=sharing", "1AbCdEfGhIjKlMnOp_qr-ST")]
        [TestCase("https://drive.google.com/open?id=1AbCdEfGhIjKlMnOp_qr-ST", "1AbCdEfGhIjKlMnOp_qr-ST")]
        [TestCase("https://drive.google.com/uc?export=download&id=1AbCdEfGhIjKlMnOp_qr-ST", "1AbCdEfGhIjKlMnOp_qr-ST")]
        [TestCase("https://drive.usercontent.google.com/download?id=1AbCdEfGhIjKlMnOp_qr-ST&export=download", "1AbCdEfGhIjKlMnOp_qr-ST")]
        public void DriveLinksGiveTheFileId(string url, string id)
        {
            Assert.IsTrue(DriveLink.TryGetFileId(url, out var found));
            Assert.AreEqual(id, found);
        }

        [TestCase("https://www.youtube.com/watch?v=abcdefghijk")]
        [TestCase("https://drive.google.com/drive/folders")]
        [TestCase("")]
        public void OtherLinksAreNotDriveFiles(string url)
        {
            Assert.IsFalse(DriveLink.TryGetFileId(url, out _));
        }

        [Test]
        public void DownloadNameComesFromTheHeader()
        {
            Assert.AreEqual("beach_360.mp4", DriveLink.FileNameFromDisposition("attachment; filename=\"beach_360.mp4\""));
            Assert.AreEqual("hike 180 sbs.mp4",
                DriveLink.FileNameFromDisposition("attachment; filename=\"hike_180_sbs.mp4\"; filename*=UTF-8''hike%20180%20sbs.mp4"));
            Assert.IsNull(DriveLink.FileNameFromDisposition(null));
        }

        // Your own bundled videos first, then the placeholders; only what's there.
        [Test]
        public void BundledVideosAreThoseThere()
        {
            var present = new[] { "Sample_360.mp4", "Zebra.mov", "readme.txt", "niagara.MP4" };
            CollectionAssert.AreEqual(new[] { "niagara.MP4", "Zebra.mov", "Sample_360.mp4" },
                VideoLibrary.OrderBundled(present, new[] { "Sample_360.mp4", "Sample_180_sbs.mp4" }));
            CollectionAssert.IsEmpty(VideoLibrary.OrderBundled(new string[0], new[] { "Sample_360.mp4" }));
        }

        [Test]
        public void ListSkipsCommentsAndOtherLinks()
        {
            var entries = VideoLibrary.ParseList(new[]
            {
                "# my videos",
                "",
                "https://drive.google.com/file/d/1AbCdEfGhIjKlMnOp_qr-ST/view  Canyon_180_sbs",
                "https://example.com/video.mp4",
                "https://drive.google.com/open?id=1ZyXwVuTsRqPoNmLk",
            });
            UnityEngine.TestTools.LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex("Not a Google Drive link"));
            Assert.AreEqual(2, entries.Count);
            Assert.AreEqual("Canyon_180_sbs", entries[0].Title);
            Assert.AreEqual(new VideoFormat(Projection.Half180, StereoLayout.SideBySide), entries[0].Format);
            Assert.IsTrue(entries[0].NeedsDownload);
            Assert.AreEqual("1ZyXwVuTsRqPoNmLk", entries[1].DriveId);
        }

        [Test]
        public void AheadIsTheMiddleOfTheFrame()
        {
            Assert.AreEqual(new Vector2(0.5f, 0.5f), VideoSphere.Uv(0f, 0f, Projection.Full360));
            Assert.AreEqual(new Vector2(0.5f, 0.5f), VideoSphere.Uv(0f, 0f, Projection.Half180));
            AssertClose(Vector3.forward, VideoSphere.Direction(0f, 0f));
        }

        [Test]
        public void RightIsRightOfMiddle()
        {
            var right = Mathf.PI / 2f;
            AssertClose(Vector3.right, VideoSphere.Direction(right, 0f));
            Assert.AreEqual(0.75f, VideoSphere.Uv(right, 0f, Projection.Full360).x, 1e-5f);
            // A 180 video's right edge is straight out to the right.
            Assert.AreEqual(1f, VideoSphere.Uv(right, 0f, Projection.Half180).x, 1e-5f);
            Assert.AreEqual(1f, VideoSphere.Uv(0f, Mathf.PI / 2f, Projection.Full360).y, 1e-5f);
        }

        [Test]
        public void SphereFacesInward()
        {
            var mesh = VideoSphere.BuildMesh(Projection.Full360, 10f);
            try
            {
                var vertices = mesh.vertices;
                var triangles = mesh.triangles;
                for (var i = 0; i < triangles.Length; i += 3)
                {
                    var a = vertices[triangles[i]];
                    var b = vertices[triangles[i + 1]];
                    var c = vertices[triangles[i + 2]];
                    var normal = Vector3.Cross(b - a, c - a);
                    // Degenerate triangles at the poles have no direction.
                    if (normal.sqrMagnitude < 1e-10f)
                        continue;
                    // Unity's face normal, (b - a) x (c - a), points toward whoever sees the front, so
                    // for the viewer at the center it points inward.
                    Assert.Less(Vector3.Dot(normal, (a + b + c) / 3f), 0f, $"triangle {i / 3}");
                }
            }
            finally
            {
                Object.DestroyImmediate(mesh);
            }
        }

        static void AssertClose(Vector3 expected, Vector3 actual) =>
            Assert.Less(Vector3.Distance(expected, actual), 1e-5f, $"{actual} isn't {expected}");
    }
}
