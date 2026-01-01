using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace YARG.Helpers.UI
{
    /// <summary>
    /// Gives this ScrollRect priority over ListMenu-based scroll areas when the pointer is over it.
    /// When the pointer is over this ScrollRect, it signals to ListMenus that they should not scroll.
    /// Only add this component to the ScrollRect that should have priority (e.g., the sidebar).
    /// </summary>
    [RequireComponent(typeof(ScrollRect))]
    public class NestedScrollRect : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
    {
        /// <summary>
        /// Static flag that ListMenu checks to determine if it should scroll.
        /// When true, ListMenu scroll should be blocked because a NestedScrollRect is active.
        /// </summary>
        public static bool IsScrollingBlocked { get; private set; }
        
        private bool _isPointerOver;
        
        [SerializeField] private bool _debugLog;
        
        private void OnDisable()
        {
            if (_isPointerOver)
            {
                IsScrollingBlocked = false;
                _isPointerOver = false;
                if (_debugLog) Debug.Log($"[NestedScrollRect] Disabled, unblocking scroll");
            }
        }
        
        public void OnPointerEnter(PointerEventData eventData)
        {
            _isPointerOver = true;
            IsScrollingBlocked = true;
            if (_debugLog) Debug.Log($"[NestedScrollRect] Pointer entered: {gameObject.name}, blocking ListMenu scroll");
        }
        
        public void OnPointerExit(PointerEventData eventData)
        {
            _isPointerOver = false;
            IsScrollingBlocked = false;
            if (_debugLog) Debug.Log($"[NestedScrollRect] Pointer exited: {gameObject.name}, unblocking ListMenu scroll");
        }
    }
}
