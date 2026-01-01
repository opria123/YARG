using System.Collections.Generic;
using UnityEngine;
using YARG.Menu;

namespace YARG.Networking.Abstraction
{
    /// <summary>
    /// Helper class for managing menu navigation state during scene transitions.
    /// Used to restore the correct menu stack after transitioning from gameplay to menu.
    /// </summary>
    public static class MenuNavigationHelper
    {
        private static readonly List<MenuManager.Menu> _menuNavigationAfterSceneLoad = new();

        /// <summary>
        /// Sets the ordered list of menus to navigate to after the Menu scene loads.
        /// Used when host quits song and wants to restore the multiplayer navigation stack.
        /// </summary>
        public static void SetMenuNavigationAfterSceneLoad(params MenuManager.Menu[] targetMenus)
        {
            _menuNavigationAfterSceneLoad.Clear();

            if (targetMenus != null)
            {
                foreach (var menu in targetMenus)
                {
                    if (menu == MenuManager.Menu.None)
                    {
                        continue;
                    }

                    _menuNavigationAfterSceneLoad.Add(menu);
                }
            }

            var route = _menuNavigationAfterSceneLoad.Count > 0
                ? string.Join(" > ", _menuNavigationAfterSceneLoad)
                : "None";

            Debug.Log($"[MenuNavigationHelper] Set menu navigation after scene load: {route}");
        }

        /// <summary>
        /// Gets and clears the ordered menu navigation list set by SetMenuNavigationAfterSceneLoad.
        /// Called by MenuManager after Menu scene loads.
        /// </summary>
        public static List<MenuManager.Menu> GetAndClearMenuNavigationAfterSceneLoad()
        {
            if (_menuNavigationAfterSceneLoad.Count == 0)
            {
                return new List<MenuManager.Menu>();
            }

            var result = new List<MenuManager.Menu>(_menuNavigationAfterSceneLoad);
            _menuNavigationAfterSceneLoad.Clear();
            return result;
        }
        
        /// <summary>
        /// Check if there are pending menu navigations.
        /// </summary>
        public static bool HasPendingNavigation => _menuNavigationAfterSceneLoad.Count > 0;
        
        /// <summary>
        /// Clear any pending navigation without processing it.
        /// </summary>
        public static void Clear()
        {
            _menuNavigationAfterSceneLoad.Clear();
        }
    }
}
