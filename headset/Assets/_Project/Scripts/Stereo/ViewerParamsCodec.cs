using System;
using System.Collections.Generic;
using System.Text;

namespace CardboardXR.Stereo
{
    // Reads viewer parameters from the Cardboard DeviceParams protobuf (proto2 wire format) and from
    // the URLs viewer QR codes hold, following the Cardboard SDK's rules
    // (sdk/qrcode/android/.../CardboardParamsUtils.java):
    //   https://g.co/cardboard                  the original Cardboard, i.e. the v1 parameters
    //   https://google.com/cardboard/cfg?p=...  parameters as URL-safe base64
    // Anything else (goo.gl and other short links) must be resolved by following redirects first.
    public static class ViewerParamsCodec
    {
        const int k_WireVarint = 0;
        const int k_WireFixed64 = 1;
        const int k_WireLengthDelimited = 2;
        const int k_WireFixed32 = 5;

        // Like the SDK: add https to scheme-less URLs and upgrade http to https.
        public static string NormalizeUrl(string url)
        {
            url = url.Trim();
            if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                return "https://" + url.Substring("http://".Length);
            return url.Contains("://") ? url : "https://" + url;
        }

        // True when the URL describes a viewer by itself, so no redirects need following.
        public static bool IsViewerUrl(string url) =>
            Uri.TryCreate(NormalizeUrl(url), UriKind.Absolute, out var uri) &&
            (IsOriginalCardboard(uri) || IsConfigUrl(uri));

        public static bool TryDecodeUrl(string url, out ViewerParams viewer)
        {
            viewer = null;
            if (!Uri.TryCreate(NormalizeUrl(url), UriKind.Absolute, out var uri))
                return false;

            if (IsOriginalCardboard(uri))
            {
                viewer = ViewerParams.CardboardV1;
                return true;
            }

            if (!IsConfigUrl(uri))
                return false;

            foreach (var pair in uri.Query.TrimStart('?').Split('&'))
            {
                if (!pair.StartsWith("p=", StringComparison.Ordinal))
                    continue;
                var bytes = DecodeBase64Url(Uri.UnescapeDataString(pair.Substring(2)));
                return bytes != null && TryDecode(bytes, out viewer);
            }

            return false;
        }

        static bool IsOriginalCardboard(Uri uri) =>
            uri.Scheme == Uri.UriSchemeHttps && uri.Host == "g.co" &&
            uri.AbsolutePath == "/cardboard" && uri.Query.Length == 0;

        // The SDK only accepts google.com; www.google.com is allowed too since some viewers print it.
        static bool IsConfigUrl(Uri uri) =>
            uri.Scheme == Uri.UriSchemeHttps && (uri.Host == "google.com" || uri.Host == "www.google.com") &&
            uri.AbsolutePath == "/cardboard/cfg";

        public static bool TryDecode(byte[] data, out ViewerParams viewer)
        {
            viewer = new ViewerParams();
            var fieldOfView = new List<float>();
            var coefficients = new List<float>();
            var position = 0;

            try
            {
                while (position < data.Length)
                {
                    var key = ReadVarint(data, ref position);
                    var field = (int)(key >> 3);
                    var wireType = (int)(key & 7);
                    switch (field, wireType)
                    {
                        case (1, k_WireLengthDelimited):
                            viewer.vendor = ReadString(data, ref position);
                            break;
                        case (2, k_WireLengthDelimited):
                            viewer.model = ReadString(data, ref position);
                            break;
                        case (3, k_WireFixed32):
                            viewer.screenToLensDistance = ReadFloat(data, ref position);
                            break;
                        case (4, k_WireFixed32):
                            viewer.interLensDistance = ReadFloat(data, ref position);
                            break;
                        case (5, k_WireLengthDelimited):
                            ReadPackedFloats(data, ref position, fieldOfView);
                            break;
                        case (5, k_WireFixed32):
                            fieldOfView.Add(ReadFloat(data, ref position));
                            break;
                        case (6, k_WireFixed32):
                            viewer.trayToLensDistance = ReadFloat(data, ref position);
                            break;
                        case (7, k_WireLengthDelimited):
                            ReadPackedFloats(data, ref position, coefficients);
                            break;
                        case (7, k_WireFixed32):
                            coefficients.Add(ReadFloat(data, ref position));
                            break;
                        case (11, k_WireVarint):
                            viewer.verticalAlignment = (ViewerParams.VerticalAlignment)ReadVarint(data, ref position);
                            break;
                        case (12, k_WireVarint):
                            viewer.primaryButton = (ViewerParams.ButtonType)ReadVarint(data, ref position);
                            break;
                        default:
                            SkipField(data, ref position, wireType);
                            break;
                    }
                }
            }
            catch (Exception e) when (e is IndexOutOfRangeException or ArgumentException or FormatException)
            {
                viewer = null;
                return false;
            }

            // The fields distortion rendering can't work without.
            if (fieldOfView.Count != 4 || viewer.screenToLensDistance <= 0f || viewer.interLensDistance <= 0f)
            {
                viewer = null;
                return false;
            }

            viewer.leftEyeFieldOfViewAngles = fieldOfView.ToArray();
            viewer.distortionCoefficients = coefficients.ToArray();
            return true;
        }

        static byte[] DecodeBase64Url(string value)
        {
            var base64 = value.Replace('-', '+').Replace('_', '/');
            base64 = base64.PadRight(base64.Length + (4 - base64.Length % 4) % 4, '=');
            try
            {
                return Convert.FromBase64String(base64);
            }
            catch (FormatException)
            {
                return null;
            }
        }

        static ulong ReadVarint(byte[] data, ref int position)
        {
            ulong value = 0;
            for (var shift = 0; shift < 64; shift += 7)
            {
                var b = data[position++];
                value |= (ulong)(b & 0x7f) << shift;
                if ((b & 0x80) == 0)
                    return value;
            }
            throw new FormatException("Varint is too long.");
        }

        static float ReadFloat(byte[] data, ref int position)
        {
            if (position + 4 > data.Length)
                throw new IndexOutOfRangeException();
            // Protobuf fixed32 values are little-endian.
            var bits = data[position] | data[position + 1] << 8 | data[position + 2] << 16 | data[position + 3] << 24;
            position += 4;
            return BitConverter.Int32BitsToSingle(bits);
        }

        // A length-delimited field's length, checked against what's left: a QR code's data comes from
        // anywhere, and a length that wrapped negative would move backwards and never finish.
        static int ReadLength(byte[] data, ref int position)
        {
            var length = ReadVarint(data, ref position);
            if (length > (ulong)(data.Length - position))
                throw new FormatException("Field runs past the end of the data.");
            return (int)length;
        }

        static string ReadString(byte[] data, ref int position)
        {
            var length = ReadLength(data, ref position);
            var value = Encoding.UTF8.GetString(data, position, length);
            position += length;
            return value;
        }

        static void ReadPackedFloats(byte[] data, ref int position, List<float> values)
        {
            var length = ReadLength(data, ref position);
            var end = position + length;
            while (position < end)
                values.Add(ReadFloat(data, ref position));
        }

        static void SkipField(byte[] data, ref int position, int wireType)
        {
            switch (wireType)
            {
                case k_WireVarint:
                    ReadVarint(data, ref position);
                    break;
                case k_WireFixed64:
                    position += 8;
                    break;
                case k_WireLengthDelimited:
                    position += ReadLength(data, ref position);
                    break;
                case k_WireFixed32:
                    position += 4;
                    break;
                default:
                    throw new FormatException($"Unsupported wire type {wireType}.");
            }
        }
    }
}
