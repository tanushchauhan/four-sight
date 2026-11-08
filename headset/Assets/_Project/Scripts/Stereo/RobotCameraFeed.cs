using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

namespace CardboardXR.Stereo
{
    // The robot's camera, drawn through the lenses. The Pi serves MJPEG on local Wi-Fi
    // (GET /stream, multipart/x-mixed-replace, one JPEG per part). There is one picture, so both
    // eyes see it from the same point, the way passthrough does. Launch with the stream URL:
    //   CARDBOARDXR_stream=http://192.168.1.20:8080/stream
    // A home-screen launch has no URL and leaves this off.
    public class RobotCameraFeed : MonoBehaviour
    {
        const int k_MaxJpegBytes = 2 * 1024 * 1024;

        StereoRig m_Rig;
        bool m_PreviousMonoEyes;
        Renderer m_Screen;
        Texture2D m_Texture;
        readonly object m_Lock = new();
        byte[] m_Pending;
        int m_Applied;
        volatile bool m_Stop;
        Thread m_Thread;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Install()
        {
            var url = LaunchOptions.GetString("stream", null);
            if (string.IsNullOrEmpty(url))
                return;
            var rig = FindAnyObjectByType<StereoRig>();
            if (rig == null)
            {
                Debug.LogError("[Robot] No stereo rig, so the camera stream cannot be shown.");
                return;
            }
            var feed = new GameObject("Robot Camera").AddComponent<RobotCameraFeed>();
            feed.Open(rig, url);
        }

        void Open(StereoRig rig, string url)
        {
            m_Rig = rig;
            m_PreviousMonoEyes = rig.MonoEyes;
            transform.SetParent(rig.transform, false);
            transform.localPosition = new Vector3(0f, 0f, 1.5f);
            transform.localRotation = Quaternion.Euler(0f, 180f, 0f);

            var shader = Shader.Find("Universal Render Pipeline/Unlit");
            if (shader == null)
                shader = Shader.Find("Unlit/Texture");
            var material = new Material(shader) { name = "Robot Camera" };
            var screen = GameObject.CreatePrimitive(PrimitiveType.Quad);
            Destroy(screen.GetComponent<Collider>());
            screen.name = "Robot Camera Screen";
            screen.transform.SetParent(transform, false);
            m_Screen = screen.GetComponent<Renderer>();
            m_Screen.sharedMaterial = material;
            m_Screen.enabled = false;
            m_Texture = new Texture2D(2, 2, TextureFormat.RGB24, false) { name = "Robot Camera" };
            material.SetTexture("_BaseMap", m_Texture);
            material.SetTexture("_MainTex", m_Texture);

            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "http")
            {
                Debug.LogError($"[Robot] Stream URL must be http: {url}");
                return;
            }
            rig.MonoEyes = true;
            Debug.Log($"[Robot] Opening {uri}");
            m_Thread = new Thread(() => ReadForever(uri)) { IsBackground = true, Name = "Robot camera" };
            m_Thread.Start();
        }

        void OnDestroy()
        {
            m_Stop = true;
            if (m_Rig != null)
                m_Rig.MonoEyes = m_PreviousMonoEyes;
            if (m_Texture != null)
                Destroy(m_Texture);
        }

        void Update()
        {
            byte[] jpeg;
            lock (m_Lock)
            {
                jpeg = m_Pending;
                m_Pending = null;
            }
            if (jpeg == null || !m_Texture.LoadImage(jpeg, markNonReadable: true))
                return;
            m_Applied++;
            var aspect = m_Texture.width / (float)m_Texture.height;
            // The quad sits 1.5 m ahead. Size it to the picture's shape, about the lens height.
            var height = 2f * 1.5f * Mathf.Tan(40f * Mathf.Deg2Rad);
            m_Screen.transform.localScale = new Vector3(height * aspect, height, 1f);
            m_Screen.enabled = true;
            if (m_Applied == 1)
                Debug.Log($"[Robot] First frame {m_Texture.width}x{m_Texture.height}");
        }

        void ReadForever(Uri uri)
        {
            while (!m_Stop)
            {
                try
                {
                    ReadOnce(uri);
                }
                catch (Exception e) when (!m_Stop)
                {
                    Debug.LogWarning($"[Robot] {e.Message}");
                }
                if (!m_Stop)
                    Thread.Sleep(1000);
            }
        }

        void ReadOnce(Uri uri)
        {
            using var client = new TcpClient();
            client.ReceiveTimeout = 5000;
            client.SendTimeout = 5000;
            client.Connect(uri.Host, uri.Port == -1 ? 80 : uri.Port);
            using var stream = client.GetStream();
            var path = string.IsNullOrEmpty(uri.PathAndQuery) ? "/stream" : uri.PathAndQuery;
            var request = Encoding.ASCII.GetBytes($"GET {path} HTTP/1.0\r\nHost: {uri.Host}\r\n\r\n");
            stream.Write(request, 0, request.Length);
            var reader = new BinaryReader(stream, Encoding.ASCII);
            var status = ReadLine(reader);
            if (status == null || !status.Contains("200"))
                throw new IOException(status ?? "no status line");
            string boundary = "frame";
            while (true)
            {
                var line = ReadLine(reader);
                if (line == null)
                    throw new IOException("stream closed");
                if (line.Length == 0)
                    break;
                const string marker = "boundary=";
                var at = line.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                if (at >= 0)
                    boundary = line.Substring(at + marker.Length).Trim().Trim('"');
            }
            var boundaryLine = "--" + boundary;
            while (!m_Stop)
            {
                string line;
                do
                {
                    line = ReadLine(reader);
                    if (line == null)
                        return;
                }
                while (!line.StartsWith(boundaryLine, StringComparison.Ordinal));
                if (line.StartsWith(boundaryLine + "--", StringComparison.Ordinal))
                    return;

                var length = -1;
                while (true)
                {
                    line = ReadLine(reader);
                    if (line == null)
                        return;
                    if (line.Length == 0)
                        break;
                    if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)
                        && int.TryParse(line.Substring("Content-Length:".Length).Trim(), out var parsed))
                        length = parsed;
                }
                if (length <= 0 || length > k_MaxJpegBytes)
                    throw new IOException($"bad frame length {length}");
                var jpeg = reader.ReadBytes(length);
                if (jpeg.Length != length || jpeg[0] != 0xFF || jpeg[1] != 0xD8)
                    throw new IOException("frame was not a JPEG");
                lock (m_Lock)
                    m_Pending = jpeg;
            }
        }

        static string ReadLine(BinaryReader reader)
        {
            var bytes = new List<byte>(64);
            while (true)
            {
                int value;
                try
                {
                    value = reader.ReadByte();
                }
                catch (EndOfStreamException)
                {
                    return bytes.Count == 0 ? null : Encoding.ASCII.GetString(bytes.ToArray());
                }
                if (value == '\n')
                {
                    if (bytes.Count > 0 && bytes[bytes.Count - 1] == '\r')
                        bytes.RemoveAt(bytes.Count - 1);
                    return Encoding.ASCII.GetString(bytes.ToArray());
                }
                bytes.Add((byte)value);
            }
        }
    }
}
