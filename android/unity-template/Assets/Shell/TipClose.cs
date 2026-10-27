using System;
using UnityEngine;

namespace Ro3Android
{
    /// <summary>
    /// Tips that could not be closed (inferred port fix). Outside PCMode the client has no hover, so a tip panel closes
    /// through its close button behind the tip (UI_Com_Tips_NormalPanelCtrl binds View m_kBTN_Close, Root/Close_Btn, to
    /// CloseWithTipSystem). The port loads the PC prefabs, where that button is a 0x0 ExtendButton with no graphic, so a
    /// tap never reached it and the open tip kept the screen. Each tip panel's zero-sized close button is stretched over
    /// the panel with a transparent raycast Image; it is the tip's sibling before it, so the tip itself stays on top.
    /// Checked every 0.2 s: tips are opened by the game's Lua at any time.
    /// </summary>
    public class TipClose : MonoBehaviour
    {
        private const string UiNode = "GameMain/NodeRoot/UINode";
        private const float Interval = 0.2f;

        private static readonly Type UiImage = Type.GetType("UnityEngine.UI.Image, UnityEngine.UI");
        private static readonly Type Graphic = Type.GetType("UnityEngine.UI.Graphic, UnityEngine.UI");
        private static readonly System.Reflection.PropertyInfo ImageColor = UiImage?.GetProperty("color");

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Install()
        {
            var go = new GameObject("Ro3TipClose");
            DontDestroyOnLoad(go);
            go.AddComponent<TipClose>();
        }

        private float _next;
        private int _logged;

        private void Update()
        {
            if (Time.realtimeSinceStartup < _next) return;
            _next = Time.realtimeSinceStartup + Interval;
            try
            {
                var node = GameObject.Find(UiNode);
                if (node == null) return;
                foreach (Transform panel in node.transform)
                    if (panel.gameObject.activeInHierarchy && IsTipPanel(panel.name)) FixCloseButtons(panel);
            }
            catch (Exception e)
            {
                if (_logged++ < 3) Debug.LogWarning($"[Ro3Android] tip close failed: {e.Message}");
            }
        }

        private static bool IsTipPanel(string name) =>
            name.IndexOf("Tip", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("Popup", StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>Only a close button with no graphic at all: a real "X" has its icon, and may measure 0x0 before its first
        /// layout. Once stretched it has the Image, so it is skipped from then on.</summary>
        private static void FixCloseButtons(Transform panel)
        {
            foreach (var child in panel.GetComponentsInChildren<RectTransform>(true))
            {
                if (child.name.IndexOf("Close", StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (child.rect.width > 1 || child.rect.height > 1 || child.GetComponent("ExtendButton") == null) continue;
                if (child.GetComponent(Graphic) != null) continue;
                Stretch(child);
                Debug.Log($"[Ro3Android] tip close: {panel.name}/{child.name} stretched over the panel");
            }
        }

        private static void Stretch(RectTransform button)
        {
            button.anchorMin = Vector2.zero;
            button.anchorMax = Vector2.one;
            button.sizeDelta = Vector2.zero;
            button.anchoredPosition = Vector2.zero;
            if (UiImage == null) return;
            var image = button.GetComponent(UiImage) ?? button.gameObject.AddComponent(UiImage);
            ImageColor?.SetValue(image, new Color(0, 0, 0, 0));
        }
    }
}
