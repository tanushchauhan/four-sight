using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;

namespace CardboardXR.Stereo.Tests
{
    // Records the pointer events it receives, in order. Runs in edit mode so ExecuteEvents treats it
    // as enabled.
    [ExecuteAlways]
    public class PointerEventRecorder : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler,
        IPointerDownHandler, IPointerUpHandler, IPointerClickHandler
    {
        public readonly List<string> events = new();

        public void OnPointerEnter(PointerEventData eventData) => events.Add("enter");
        public void OnPointerExit(PointerEventData eventData) => events.Add("exit");
        public void OnPointerDown(PointerEventData eventData) => events.Add("down");
        public void OnPointerUp(PointerEventData eventData) => events.Add("up");
        public void OnPointerClick(PointerEventData eventData) => events.Add("click");

        public override string ToString() => string.Join(" ", events);
    }
}
