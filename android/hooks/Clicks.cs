using System;
using System.Reflection;
using UnityEngine;

namespace Ro3Android.Hooks
{
    /// <summary>
    /// Port adjustment (inferred, not the client's data): UI elements named "*_ClickEvent" are the ones the game's Lua
    /// wires a click to (View.m_kCLICKEVENT_*, UIClickEventNew). Some mobile prefabs ship them with raycastTarget off on
    /// the element and on every graphic under it (the main HUD's function entries: Eventos, Temporada, Prelúdio), so a
    /// touch lands on the item's background, the click bubbles up from there and never reaches the handler: the buttons
    /// did nothing. Called from Materials.FixLoaded for every prefab the game's loader hands out, before it is shown.
    /// </summary>
    public static class Clicks
    {
        private static readonly Type Graphic = Type.GetType("UnityEngine.UI.Graphic, UnityEngine.UI");
        private static readonly PropertyInfo RaycastTarget = Graphic?.GetProperty("raycastTarget");
        private static int _fixed;

        public static void EnableDeadClickTargets(GameObject prefab)
        {
            if (Graphic == null || RaycastTarget == null) return;
            foreach (var t in prefab.GetComponentsInChildren<Transform>(true))
            {
                if (!t.name.EndsWith("_ClickEvent", StringComparison.Ordinal)) continue;
                var own = t.GetComponent(Graphic);
                if (own == null || (bool)RaycastTarget.GetValue(own)) continue;
                bool anyTarget = false;
                foreach (var g in t.GetComponentsInChildren(Graphic, true))
                    if ((bool)RaycastTarget.GetValue(g)) { anyTarget = true; break; }
                if (anyTarget) continue;
                RaycastTarget.SetValue(own, true);
                if (_fixed++ < 30) Debug.Log($"[Ro3Android] click target on: {prefab.name}/{t.name}");
            }
        }
    }
}
