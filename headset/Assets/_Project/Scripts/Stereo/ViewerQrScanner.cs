using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unity.Collections;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;
using ZXing;
using ZXing.Common;

namespace CardboardXR.Stereo
{
    // Looks for a QR code in ARCore's camera images and reports its text. Decodes the image's
    // luminance plane on a worker thread a few times a second, and keeps an RGB preview texture
    // for the scan screen.
    public class ViewerQrScanner : MonoBehaviour
    {
        [SerializeField] ARCameraManager m_CameraManager;
        [SerializeField] float m_ScanInterval = 0.2f;
        [SerializeField] int m_PreviewWidth = 640;

        // Only touched by one decode task at a time.
        readonly BarcodeReaderGeneric m_Reader = new()
        {
            AutoRotate = false,
            Options = new DecodingOptions
            {
                PossibleFormats = new List<BarcodeFormat> { BarcodeFormat.QR_CODE },
                TryHarder = true,
            },
        };

        Task<string> m_Decode;
        float m_NextScan;
        byte[] m_Luminance;
        Texture2D m_Preview;

        public event Action<string> CodeFound;

        public Texture Preview => m_Preview;

        // A decode still running when scanning stops finishes on its own; its result is dropped.
        void OnDisable() => m_Decode = null;

        void OnDestroy()
        {
            if (m_Preview != null)
                Destroy(m_Preview);
        }

        void Update()
        {
            if (m_Decode != null && m_Decode.IsCompleted)
            {
                var text = m_Decode.Status == TaskStatus.RanToCompletion ? m_Decode.Result : null;
                m_Decode = null;
                if (!string.IsNullOrEmpty(text))
                    CodeFound?.Invoke(text);
            }

            if (!m_CameraManager.TryAcquireLatestCpuImage(out var image))
                return;

            using (image)
            {
                UpdatePreview(image);
                if (m_Decode != null || Time.unscaledTime < m_NextScan)
                    return;

                m_NextScan = Time.unscaledTime + m_ScanInterval;
                CopyLuminance(image);
                var luminance = m_Luminance;
                var width = image.width;
                var height = image.height;
                m_Decode = Task.Run(() => Decode(luminance, width, height));
            }
        }

        string Decode(byte[] luminance, int width, int height)
        {
            var source = new PlanarYUVLuminanceSource(luminance, width, height, 0, 0, width, height, false);
            return m_Reader.Decode(source)?.Text;
        }

        // Plane 0 of ARCore and ARKit camera images is 8-bit luminance, which is all QR decoding needs.
        void CopyLuminance(XRCpuImage image)
        {
            var plane = image.GetPlane(0);
            var size = image.width * image.height;
            if (m_Luminance == null || m_Luminance.Length != size)
                m_Luminance = new byte[size];

            for (var row = 0; row < image.height; row++)
                NativeArray<byte>.Copy(plane.data, row * plane.rowStride, m_Luminance, row * image.width, image.width);
        }

        void UpdatePreview(XRCpuImage image)
        {
            var divisor = Mathf.Max(1, Mathf.CeilToInt(image.width / (float)m_PreviewWidth));
            var conversion = new XRCpuImage.ConversionParams
            {
                inputRect = new RectInt(0, 0, image.width, image.height),
                outputDimensions = new Vector2Int(image.width / divisor, image.height / divisor),
                outputFormat = TextureFormat.RGBA32,
                // CPU images start at the top row; textures start at the bottom.
                transformation = XRCpuImage.Transformation.MirrorX,
            };

            var size = conversion.outputDimensions;
            if (m_Preview == null || m_Preview.width != size.x || m_Preview.height != size.y)
            {
                if (m_Preview != null)
                    Destroy(m_Preview);
                m_Preview = new Texture2D(size.x, size.y, TextureFormat.RGBA32, false) { name = "QR Preview" };
            }

            image.Convert(conversion, m_Preview.GetRawTextureData<byte>());
            m_Preview.Apply(false);
        }
    }
}
