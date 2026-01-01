using UnityEngine;

namespace YARG.Networking.Utilities
{
    /// <summary>
    /// Extension methods for Unity UI components to reduce null-check boilerplate.
    /// </summary>
    public static class UIExtensions
    {
        /// <summary>
        /// Safely sets the active state of a GameObject, handling null.
        /// </summary>
        /// <param name="obj">The GameObject (can be null).</param>
        /// <param name="active">The desired active state.</param>
        public static void SafeSetActive(this GameObject? obj, bool active)
        {
            if (obj != null)
            {
                obj.SetActive(active);
            }
        }
        
        /// <summary>
        /// Safely sets the active state of a Component's GameObject, handling null.
        /// </summary>
        /// <param name="component">The Component (can be null).</param>
        /// <param name="active">The desired active state.</param>
        public static void SafeSetActive(this Component? component, bool active)
        {
            if (component != null)
            {
                component.gameObject.SetActive(active);
            }
        }
        
        /// <summary>
        /// Batch sets multiple GameObjects to the same active state.
        /// </summary>
        /// <param name="active">The desired active state.</param>
        /// <param name="objects">The GameObjects to set (nulls are ignored).</param>
        public static void SetActiveAll(bool active, params GameObject?[] objects)
        {
            foreach (var obj in objects)
            {
                obj.SafeSetActive(active);
            }
        }
        
        /// <summary>
        /// Batch sets multiple Components' GameObjects to the same active state.
        /// </summary>
        /// <param name="active">The desired active state.</param>
        /// <param name="components">The Components to set (nulls are ignored).</param>
        public static void SetActiveAll(bool active, params Component?[] components)
        {
            foreach (var component in components)
            {
                component.SafeSetActive(active);
            }
        }
    }
}
