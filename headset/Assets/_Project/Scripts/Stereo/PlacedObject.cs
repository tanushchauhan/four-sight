using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.XR.ARFoundation;

namespace CardboardXR.Stereo
{
    // An object the user placed on a real surface. Tapping it removes it and its anchor. Goes on the
    // AnchorPlacer's object prefab, on the GameObject with the collider.
    public class PlacedObject : MonoBehaviour, IPointerClickHandler
    {
        public void OnPointerClick(PointerEventData eventData)
        {
            var anchor = GetComponentInParent<ARAnchor>();
            var placer = GetComponentInParent<AnchorPlacer>();
            if (anchor != null && placer != null)
                placer.Remove(anchor);
        }
    }
}
