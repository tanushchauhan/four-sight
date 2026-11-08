using System;
using System.Linq;
using NUnit.Framework;

namespace CardboardXR.Stereo.Tests
{
    public class ViewerParamsCodecTests
    {
        // The Cardboard SDK's encoded Cardboard v1 parameters, from
        // sdk/qrcode/cardboard_v1/cardboard_v1.cc (Apache License 2.0).
        static readonly byte[] k_CardboardV1Bytes =
        {
            0xa, 0xc, 0x47, 0x6f, 0x6f, 0x67, 0x6c, 0x65, 0x2c, 0x20, 0x49,
            0x6e, 0x63, 0x2e, 0x12, 0xc, 0x43, 0x61, 0x72, 0x64, 0x62, 0x6f,
            0x61, 0x72, 0x64, 0x20, 0x76, 0x31, 0x1d, 0x31, 0x8, 0x2c, 0x3d,
            0x25, 0x8f, 0xc2, 0x75, 0x3d, 0x2a, 0x10, 0x0, 0x0, 0x20, 0x42,
            0x0, 0x0, 0x20, 0x42, 0x0, 0x0, 0x20, 0x42, 0x0, 0x0, 0x20,
            0x42, 0x35, 0x29, 0x5c, 0xf, 0x3d, 0x3a, 0x8, 0xc1, 0xca, 0xe1,
            0x3e, 0x77, 0xbe, 0x1f, 0x3e, 0x58, 0x0, 0x60, 0x1,
        };

        static string ConfigParam => Convert.ToBase64String(k_CardboardV1Bytes)
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

        static void AssertMatchesCardboardV1(ViewerParams viewer)
        {
            var expected = ViewerParams.CardboardV1;
            Assert.AreEqual(expected.vendor, viewer.vendor);
            Assert.AreEqual(expected.model, viewer.model);
            Assert.AreEqual(expected.screenToLensDistance, viewer.screenToLensDistance, 1e-6f);
            Assert.AreEqual(expected.interLensDistance, viewer.interLensDistance, 1e-6f);
            CollectionAssert.AreEqual(expected.leftEyeFieldOfViewAngles, viewer.leftEyeFieldOfViewAngles);
            Assert.AreEqual(expected.verticalAlignment, viewer.verticalAlignment);
            Assert.AreEqual(expected.trayToLensDistance, viewer.trayToLensDistance, 1e-6f);
            Assert.That(viewer.distortionCoefficients,
                Is.EqualTo(expected.distortionCoefficients).Within(1e-6f));
            Assert.AreEqual(expected.primaryButton, viewer.primaryButton);
        }

        [Test]
        public void DecodesCardboardV1Bytes()
        {
            Assert.IsTrue(ViewerParamsCodec.TryDecode(k_CardboardV1Bytes, out var viewer));
            AssertMatchesCardboardV1(viewer);
        }

        [TestCase("https://google.com/cardboard/cfg?p=")]
        [TestCase("http://google.com/cardboard/cfg?p=")]
        [TestCase("google.com/cardboard/cfg?p=")]
        [TestCase("https://www.google.com/cardboard/cfg?p=")]
        public void DecodesConfigUrls(string prefix)
        {
            var url = prefix + ConfigParam;
            Assert.IsTrue(ViewerParamsCodec.IsViewerUrl(url));
            Assert.IsTrue(ViewerParamsCodec.TryDecodeUrl(url, out var viewer));
            AssertMatchesCardboardV1(viewer);
        }

        [TestCase("https://g.co/cardboard")]
        [TestCase("http://g.co/cardboard")]
        [TestCase("g.co/cardboard")]
        public void OriginalCardboardCodeMeansV1(string url)
        {
            Assert.IsTrue(ViewerParamsCodec.IsViewerUrl(url));
            Assert.IsTrue(ViewerParamsCodec.TryDecodeUrl(url, out var viewer));
            AssertMatchesCardboardV1(viewer);
        }

        [TestCase("https://goo.gl/abc123")]
        [TestCase("https://example.com/cardboard/cfg?p=")]
        [TestCase("https://google.com/cardboard/other?p=")]
        public void OtherUrlsNeedResolvingFirst(string prefix)
        {
            var url = prefix.EndsWith("p=") ? prefix + ConfigParam : prefix;
            Assert.IsFalse(ViewerParamsCodec.IsViewerUrl(url));
            Assert.IsFalse(ViewerParamsCodec.TryDecodeUrl(url, out _));
        }

        // A real Google Cardboard (2015) viewer's code, reached from goo.gl/KS2FEm. It also carries
        // field 10, which the protobuf definition doesn't list, so the decoder has to skip it.
        [Test]
        public void DecodesScannedCardboard2015Url()
        {
            const string url = "https://google.com/cardboard/cfg?p=" +
                "CgZHb29nbGUSCUNhcmRib2FyZB0J-SA9JQHegj0qEAAAcEIAAHBCAABwQgAAcEJYADUpXA89OghX8as-YrENP1AAYAM";

            Assert.IsTrue(ViewerParamsCodec.TryDecodeUrl(url, out var viewer));
            Assert.AreEqual("Google", viewer.vendor);
            Assert.AreEqual("Cardboard", viewer.model);
            Assert.AreEqual(0.0393f, viewer.screenToLensDistance, 1e-4f);
            Assert.AreEqual(0.0639f, viewer.interLensDistance, 1e-4f);
            CollectionAssert.AreEqual(new[] { 60f, 60f, 60f, 60f }, viewer.leftEyeFieldOfViewAngles);
            Assert.AreEqual(ViewerParams.VerticalAlignment.Bottom, viewer.verticalAlignment);
            Assert.AreEqual(0.035f, viewer.trayToLensDistance, 1e-4f);
            Assert.That(viewer.distortionCoefficients, Is.EqualTo(new[] { 0.3358f, 0.5535f }).Within(1e-4f));
            Assert.AreEqual(ViewerParams.ButtonType.IndirectTouch, viewer.primaryButton);
        }

        [Test]
        public void RejectsTruncatedData()
        {
            var truncated = k_CardboardV1Bytes.Take(k_CardboardV1Bytes.Length - 20).ToArray();
            Assert.IsFalse(ViewerParamsCodec.TryDecode(truncated, out var viewer));
            Assert.IsNull(viewer);
        }

        // An unknown field whose length, 2^32 - 6, wraps to -6 as an int: skipping it used to move back
        // to the start of the data, so a crafted QR code hung the app.
        [Test, Timeout(2000)]
        public void RejectsLengthThatWrapsNegative()
        {
            var looping = new byte[] { (9 << 3) | 2, 0xFA, 0xFF, 0xFF, 0xFF, 0x0F };
            Assert.IsFalse(ViewerParamsCodec.TryDecode(looping, out var viewer));
            Assert.IsNull(viewer);
        }

        [Test]
        public void RejectsLengthPastTheEnd()
        {
            // The vendor string claims 100 bytes; 3 follow.
            Assert.IsFalse(ViewerParamsCodec.TryDecode(new byte[] { (1 << 3) | 2, 100, 0x41, 0x42, 0x43 }, out _));
        }

        [Test]
        public void RejectsDataWithoutFieldOfView()
        {
            // Only the vendor and model strings.
            var namesOnly = k_CardboardV1Bytes.Take(28).ToArray();
            Assert.IsFalse(ViewerParamsCodec.TryDecode(namesOnly, out _));
        }

        [Test]
        public void RejectsConfigUrlWithoutParams()
        {
            Assert.IsFalse(ViewerParamsCodec.TryDecodeUrl("https://google.com/cardboard/cfg", out _));
            Assert.IsFalse(ViewerParamsCodec.TryDecodeUrl("https://google.com/cardboard/cfg?x=1", out _));
        }
    }
}
