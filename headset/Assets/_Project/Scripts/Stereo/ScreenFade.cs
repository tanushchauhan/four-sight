using System.Collections;
using UnityEngine;

namespace CardboardXR.Stereo
{
    // Fades the whole view to black and back, for moving between scenes: a black quad just past the
    // eyes' near clip, held with the head, drawn over everything (CardboardXR/Fade) in both eyes and
    // in the single-view mode alike. Hidden while clear, so it costs nothing then. DemoManager fades
    // out before unloading a scene and in once the next has started. Put it on the quad, under the head.
    [RequireComponent(typeof(MeshRenderer))]
    public class ScreenFade : MonoBehaviour
    {
        static readonly int k_AlphaId = Shader.PropertyToID("_Alpha");

        MeshRenderer m_Renderer;
        MaterialPropertyBlock m_Block;
        float m_Alpha;

        // How black the view is, 0 (clear) to 1.
        public float Alpha
        {
            get => m_Alpha;
            set
            {
                m_Alpha = Mathf.Clamp01(value);
                m_Block.SetFloat(k_AlphaId, m_Alpha);
                m_Renderer.SetPropertyBlock(m_Block);
                m_Renderer.enabled = m_Alpha > 0f;
            }
        }

        void Awake()
        {
            m_Renderer = GetComponent<MeshRenderer>();
            m_Block = new MaterialPropertyBlock();
            Alpha = 0f;
        }

        // Eases to an opacity over a time, in seconds of real time.
        public IEnumerator FadeTo(float alpha, float seconds)
        {
            var from = m_Alpha;
            for (var t = 0f; t < seconds; t += Time.unscaledDeltaTime)
            {
                Alpha = Mathf.Lerp(from, alpha, Mathf.SmoothStep(0f, 1f, t / seconds));
                yield return null;
            }
            Alpha = alpha;
        }
    }
}
