using System;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace CardboardXR.UI
{
    // A Material 3 slider for showing and setting a position, such as a video's: an inactive track,
    // the active part up to the value, and a handle. Clicking anywhere along it reports that point's
    // fraction (Clicked); the gaze pointer gives the clicked point's world position. The handle grows
    // a little while the gaze is on it. Built by M3.Slider.
    public class M3Slider : MonoBehaviour, IPointerClickHandler, IPointerEnterHandler, IPointerExitHandler
    {
        [SerializeField] RectTransform m_Track;
        [SerializeField] RectTransform m_Active;
        [SerializeField] RectTransform m_Handle;
        [SerializeField] float m_HandleSize = 20f;

        float m_Value;
        bool m_Hovered;

        // The clicked point's fraction of the way along, from 0 at the left.
        public event Action<float> Clicked;

        public float Value
        {
            get => m_Value;
            set
            {
                m_Value = Mathf.Clamp01(value);
                Layout();
            }
        }

        public void Initialize(RectTransform track, RectTransform active, RectTransform handle, float handleSize)
        {
            m_Track = track;
            m_Active = active;
            m_Handle = handle;
            m_HandleSize = handleSize;
            Layout();
        }

        void Layout()
        {
            if (m_Track == null)
                return;
            var width = m_Track.rect.width > 0f ? m_Track.rect.width : m_Track.sizeDelta.x;
            m_Active.sizeDelta = new Vector2(Mathf.Max(m_Active.sizeDelta.y, width * m_Value), m_Active.sizeDelta.y);
            var size = m_HandleSize * (m_Hovered ? 1.2f : 1f);
            m_Handle.sizeDelta = new Vector2(size, size);
            m_Handle.anchoredPosition = new Vector2(-width / 2f + width * m_Value, 0f);
        }

        public void OnPointerClick(PointerEventData eventData)
        {
            var local = m_Track.InverseTransformPoint(eventData.pointerCurrentRaycast.worldPosition);
            var rect = m_Track.rect;
            Clicked?.Invoke(Mathf.Clamp01((local.x - rect.xMin) / rect.width));
        }

        public void OnPointerEnter(PointerEventData eventData)
        {
            m_Hovered = true;
            Layout();
        }

        public void OnPointerExit(PointerEventData eventData)
        {
            m_Hovered = false;
            Layout();
        }
    }
}
