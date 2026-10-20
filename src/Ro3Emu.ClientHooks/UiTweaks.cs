using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UI;

namespace Ro3Emu.ClientHooks
{
    /// <summary>
    /// Small layout changes to the client's UI that the signed Lua and bundles cannot carry, applied from the per-frame
    /// pump while a view is open.
    /// <list type="bullet">
    /// <item>Mode selection ("Select Mode", first world entry and Settings → PC Mode): the Mobile column (in development
    /// in this build) is removed and Classic PC / Modern PC are centred, keeping the original spacing.</item>
    /// <item>Login keyboard navigation: TAB cycles between account and password; ENTER triggers login button (login screen only).</item>
    /// <item>Announcement window: ensures closed beta text and discord links point to Hero Saga.</item>
    /// </list>
    /// </summary>
    public static class UiTweaks
    {
        private static readonly string[] ModeWidgets = { "UI_Sys_SetModeWidget_PC(Clone)", "UI_Sys_SetModeWidget(Clone)" };
        private const string ModeList = "Root/Content_RTransform/Pop_Center/Root/Pan_Content/Pan_BgMask/Content_RTransform";
        private const string MobileColumn = "PCMobile_RTransform";

        private static readonly HashSet<int> Done = new HashSet<int>();
        private static int _frame;

        /// <summary>Called every frame; looks for the views every 10th frame (GameObject.Find only sees active objects).</summary>
        public static void Tick()
        {
            Diagnostics.AutoDump();
            TextFit.Tick();
            HandleLoginKeyboard(); // Must be checked EVERY frame for responsive keyboard input!
            if (++_frame % 10 != 0) return;
            try
            {
                foreach (var name in ModeWidgets)
                {
                    var widget = GameObject.Find(name);
                    if (widget != null && Done.Add(widget.GetInstanceID())) HideMobileMode(widget.transform);
                }
                foreach (var name in StallWindows)
                {
                    var window = GameObject.Find(name);
                    if (window != null && Done.Add(window.GetInstanceID())) WidenStallName(window.transform);
                }
                var uiroot = GameObject.Find("GameMain/NodeRoot/UINode");
                if (uiroot != null)
                {
                    for (int i = 0; i < uiroot.transform.childCount; i++)
                    {
                        var child = uiroot.transform.GetChild(i);
                        if (!child.gameObject.activeInHierarchy) continue;

                        if (child.name.IndexOf("Login", StringComparison.OrdinalIgnoreCase) >= 0 && Done.Add(child.gameObject.GetInstanceID()))
                        {
                            Debug.Log("[Ro3Emu.UiTweaks] Login panel active: " + child.name);
                            foreach (var raw in child.GetComponentsInChildren<RawImage>(true))
                                Debug.Log($"[Ro3Emu.UiTweaks] Login RawImage: {raw.name} tex={(raw.texture != null ? raw.texture.name + " (" + raw.texture.width + "x" + raw.texture.height + ")" : "null")}");
                            foreach (var img in child.GetComponentsInChildren<Image>(true))
                                Debug.Log($"[Ro3Emu.UiTweaks] Login Image: {img.name} sprite={(img.sprite != null ? img.sprite.name : "null")} tex={(img.sprite?.texture != null ? img.sprite.texture.name : "null")}");
                        }

                        if (child.name.IndexOf("Announcement", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            TweakAnnouncement(child);
                        }
                    }
                }
                if (Done.Count > 128) Done.Clear();
                QuitButton.Tick();
                LuaPatches.Tick();
                PassChallenges.Tick();
                PassButton.Tick();
            }
            catch (Exception e)
            {
                Debug.LogError("[Ro3Emu.UiTweaks] " + e);
            }
        }

        /// <summary>
        /// Handles TAB (cycle focus between account and password) and ENTER (submit login) strictly on the login screen.
        /// Does nothing once the player is in character select or in the game world.
        /// </summary>
        private static void HandleLoginKeyboard()
        {
            try
            {
                var uiroot = GameObject.Find("GameMain/NodeRoot/UINode");
                if (uiroot == null) return;

                Transform loginPanel = null;
                for (int i = 0; i < uiroot.transform.childCount; i++)
                {
                    var child = uiroot.transform.GetChild(i);
                    if (!child.gameObject.activeInHierarchy) continue;

                    // If any modal (announcement, server selection, loading) is open, do not handle login keys
                    if (child.name.IndexOf("Announcement", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        child.name.IndexOf("ChoiceServer", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        child.name.IndexOf("Loading", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return;
                    }

                    if (child.name.IndexOf("LoginPanel", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        child.name.IndexOf("LoginWidget", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        loginPanel = child;
                    }
                }

                if (loginPanel == null) return;

                bool tabPressed = Input.GetKeyDown(KeyCode.Tab);
                bool enterPressed = Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter);

                if (!tabPressed && !enterPressed) return;

                var tmpInputs = loginPanel.GetComponentsInChildren<TMPro.TMP_InputField>(true)
                    .Where(f => f != null && f.gameObject.activeInHierarchy && f.interactable)
                    .OrderByDescending(f => f.transform.position.y)
                    .ToList();

                var uiInputs = tmpInputs.Count == 0
                    ? loginPanel.GetComponentsInChildren<InputField>(true)
                        .Where(f => f != null && f.gameObject.activeInHierarchy && f.interactable)
                        .OrderByDescending(f => f.transform.position.y)
                        .ToList()
                    : null;

                if (tabPressed)
                {
                    if (tmpInputs.Count >= 2)
                    {
                        int currentIndex = -1;
                        for (int i = 0; i < tmpInputs.Count; i++)
                        {
                            if (tmpInputs[i].isFocused)
                            {
                                currentIndex = i;
                                break;
                            }
                        }

                        int nextIndex;
                        if (currentIndex >= 0)
                        {
                            nextIndex = (currentIndex + 1) % tmpInputs.Count;
                            tmpInputs[currentIndex].DeactivateInputField();
                        }
                        else
                        {
                            nextIndex = (!string.IsNullOrEmpty(tmpInputs[0].text) && string.IsNullOrEmpty(tmpInputs[1].text)) ? 1 : 0;
                        }

                        tmpInputs[nextIndex].Select();
                        tmpInputs[nextIndex].ActivateInputField();
                        tmpInputs[nextIndex].caretPosition = tmpInputs[nextIndex].text.Length;
                    }
                    else if (uiInputs != null && uiInputs.Count >= 2)
                    {
                        int currentIndex = -1;
                        for (int i = 0; i < uiInputs.Count; i++)
                        {
                            if (uiInputs[i].isFocused)
                            {
                                currentIndex = i;
                                break;
                            }
                        }

                        int nextIndex;
                        if (currentIndex >= 0)
                        {
                            nextIndex = (currentIndex + 1) % uiInputs.Count;
                            uiInputs[currentIndex].DeactivateInputField();
                        }
                        else
                        {
                            nextIndex = (!string.IsNullOrEmpty(uiInputs[0].text) && string.IsNullOrEmpty(uiInputs[1].text)) ? 1 : 0;
                        }

                        uiInputs[nextIndex].Select();
                        uiInputs[nextIndex].ActivateInputField();
                        uiInputs[nextIndex].caretPosition = uiInputs[nextIndex].text.Length;
                    }
                }

                if (enterPressed)
                {
                    TriggerLoginButton(loginPanel);
                }
            }
            catch (Exception e)
            {
                Debug.LogError("[Ro3Emu.UiTweaks] HandleLoginKeyboard: " + e);
            }
        }

        private static void TriggerLoginButton(Transform loginPanel)
        {
            Button targetButton = null;
            var buttons = loginPanel.GetComponentsInChildren<Button>(true);

            // 1. By text
            foreach (var btn in buttons)
            {
                if (!btn.gameObject.activeInHierarchy || !btn.interactable) continue;
                var tmp = btn.GetComponentInChildren<TMPro.TMP_Text>(true);
                var txt = btn.GetComponentInChildren<Text>(true);
                string label = (tmp != null ? tmp.text : (txt != null ? txt.text : "")) ?? "";
                if (label.IndexOf("Entrar", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    label.IndexOf("Login", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    label.IndexOf("Start", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    targetButton = btn;
                    break;
                }
            }

            // 2. By GameObject name
            if (targetButton == null)
            {
                foreach (var btn in buttons)
                {
                    if (!btn.gameObject.activeInHierarchy || !btn.interactable) continue;
                    string name = btn.gameObject.name;
                    if (name.IndexOf("Login", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        name.IndexOf("Enter", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        name.IndexOf("Start", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        targetButton = btn;
                        break;
                    }
                }
            }

            // 3. Fallback: first interactable button
            if (targetButton == null)
            {
                targetButton = buttons.FirstOrDefault(b => b.gameObject.activeInHierarchy && b.interactable);
            }

            if (targetButton != null)
            {
                Debug.Log("[Ro3Emu.UiTweaks] Enter pressed -> triggering login button: " + targetButton.gameObject.name);
                targetButton.onClick.Invoke();

                try
                {
                    var eventData = new UnityEngine.EventSystems.PointerEventData(UnityEngine.EventSystems.EventSystem.current)
                    {
                        button = UnityEngine.EventSystems.PointerEventData.InputButton.Left
                    };
                    UnityEngine.EventSystems.ExecuteEvents.Execute(targetButton.gameObject, eventData, UnityEngine.EventSystems.ExecuteEvents.pointerClickHandler);
                    UnityEngine.EventSystems.ExecuteEvents.Execute(targetButton.gameObject, eventData, UnityEngine.EventSystems.ExecuteEvents.submitHandler);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning("[Ro3Emu.UiTweaks] ExecuteEvents: " + ex.Message);
                }
            }
            else
            {
                // Fallback for custom click handlers
                foreach (var clickHandler in loginPanel.GetComponentsInChildren<UnityEngine.EventSystems.IPointerClickHandler>(true))
                {
                    var mb = clickHandler as MonoBehaviour;
                    if (mb == null || !mb.gameObject.activeInHierarchy) continue;
                    string name = mb.gameObject.name;
                    if (name.IndexOf("Login", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        name.IndexOf("Enter", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        name.IndexOf("Start", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        var eventData = new UnityEngine.EventSystems.PointerEventData(UnityEngine.EventSystems.EventSystem.current)
                        {
                            button = UnityEngine.EventSystems.PointerEventData.InputButton.Left
                        };
                        clickHandler.OnPointerClick(eventData);
                        break;
                    }
                }
            }
        }

        private static void TweakAnnouncement(Transform window)
        {
            foreach (var tmp in window.GetComponentsInChildren<TMPro.TMP_Text>(true))
            {
                if (string.IsNullOrEmpty(tmp.text)) continue;
                string t = tmp.text;
                bool changed = false;
                if (t.IndexOf("Obrigado por participar do Closed Beta de Ragnarok Online 3.", StringComparison.Ordinal) >= 0)
                {
                    t = t.Replace("Obrigado por participar do Closed Beta de Ragnarok Online 3.", "Closed Beta do Hero Saga RO3");
                    changed = true;
                }
                else if (t.IndexOf("Closed Beta de Ragnarok Online 3", StringComparison.Ordinal) >= 0)
                {
                    t = t.Replace("Closed Beta de Ragnarok Online 3", "Closed Beta do Hero Saga RO3");
                    changed = true;
                }
                if (t.IndexOf("https://discord.com/invite/ro3global", StringComparison.Ordinal) >= 0)
                {
                    t = t.Replace("https://discord.com/invite/ro3global", "https://discord.com/invite/mnUBY54jAe");
                    changed = true;
                }
                else if (t.IndexOf("ro3global", StringComparison.Ordinal) >= 0)
                {
                    t = t.Replace("ro3global", "mnUBY54jAe");
                    changed = true;
                }
                if (changed) tmp.text = t;
            }

            foreach (var txt in window.GetComponentsInChildren<Text>(true))
            {
                if (string.IsNullOrEmpty(txt.text)) continue;
                string t = txt.text;
                bool changed = false;
                if (t.IndexOf("Obrigado por participar do Closed Beta de Ragnarok Online 3.", StringComparison.Ordinal) >= 0)
                {
                    t = t.Replace("Obrigado por participar do Closed Beta de Ragnarok Online 3.", "Closed Beta do Hero Saga RO3");
                    changed = true;
                }
                else if (t.IndexOf("Closed Beta de Ragnarok Online 3", StringComparison.Ordinal) >= 0)
                {
                    t = t.Replace("Closed Beta de Ragnarok Online 3", "Closed Beta do Hero Saga RO3");
                    changed = true;
                }
                if (t.IndexOf("https://discord.com/invite/ro3global", StringComparison.Ordinal) >= 0)
                {
                    t = t.Replace("https://discord.com/invite/ro3global", "https://discord.com/invite/mnUBY54jAe");
                    changed = true;
                }
                else if (t.IndexOf("ro3global", StringComparison.Ordinal) >= 0)
                {
                    t = t.Replace("ro3global", "mnUBY54jAe");
                    changed = true;
                }
                if (changed) txt.text = t;
            }
        }

        private static readonly string[] StallWindows = { "UI_Sys_StallWindow(Clone)", "UI_Sys_StallWindow_PC(Clone)" };

        /// <summary>
        /// The stall title input (UI_Sys_StallWindow m_kTINPUTFIELD_TInput) has characterLimit 10 in the prefab, sized for
        /// Chinese; it gets LuaPatches.StallNameMaxLength, the same limit the server applies.
        /// </summary>
        private static void WidenStallName(Transform window)
        {
            foreach (var input in window.GetComponentsInChildren<TMPro.TMP_InputField>(true))
                if (input.characterLimit > 0 && input.characterLimit < LuaPatches.StallNameMaxLength)
                {
                    Debug.Log("[Ro3Emu.UiTweaks] stall input " + input.name + ": limit " + input.characterLimit + " -> " + LuaPatches.StallNameMaxLength);
                    input.characterLimit = LuaPatches.StallNameMaxLength;
                }
        }

        private static void HideMobileMode(Transform widget)
        {
            var content = widget.Find(ModeList);
            if (content == null) return;
            Transform mobile = null;
            foreach (var t in content.GetComponentsInChildren<Transform>(true))
                if (t.name == MobileColumn) { mobile = t; break; }
            if (mobile == null) return;
            var row = mobile.parent;
            var columns = new List<RectTransform>();
            for (int i = 0; i < row.childCount; i++)
            {
                var c = row.GetChild(i) as RectTransform;
                if (c != null && c != mobile && c.gameObject.activeSelf) columns.Add(c);
            }
            float oldLeft = float.MaxValue, oldRight = float.MinValue;
            foreach (var c in columns) Edges(c, ref oldLeft, ref oldRight);
            Edges(mobile as RectTransform, ref oldLeft, ref oldRight);
            mobile.gameObject.SetActive(false);

            var group = row.GetComponent<HorizontalOrVerticalLayoutGroup>();
            if (group != null)
            {
                // A layout group re-flows the remaining columns; centre them in the row.
                group.childAlignment = TextAnchor.MiddleCenter;
                var rowRect = row as RectTransform;
                if (rowRect != null) LayoutRebuilder.ForceRebuildLayoutImmediate(rowRect);
            }
            else if (columns.Count > 0 && row is RectTransform rect)
            {
                // Hand-placed columns: move them together so their middle is the row's middle (spacing unchanged).
                float min = float.MaxValue, max = float.MinValue;
                foreach (var c in columns)
                {
                    float x = c.localPosition.x;
                    min = Math.Min(min, x + c.rect.xMin);
                    max = Math.Max(max, x + c.rect.xMax);
                }
                float shift = rect.rect.center.x - (min + max) / 2f;
                foreach (var c in columns) c.localPosition += new Vector3(shift, 0f, 0f);
            }
            // The close (right) and back (left) buttons sit at the edges of the column set: follow the new edges.
            float newLeft = float.MaxValue, newRight = float.MinValue;
            foreach (var c in columns) Edges(c, ref newLeft, ref newRight);
            var panel = widget.Find("Root/Content_RTransform/Pop_Center/Root/Pan_Content");
            if (panel != null && columns.Count > 0)
            {
                var close = panel.Find("Close_Btn");
                if (close != null) close.position += new Vector3(newRight - oldRight, 0f, 0f);
                var back = panel.Find("Return_Btn");
                if (back != null) back.position += new Vector3(newLeft - oldLeft, 0f, 0f);
            }
            Debug.Log("[Ro3Emu.UiTweaks] mode selection: Mobile removed, " + columns.Count + " column(s) centred" + (group != null ? " (layout group)" : ""));
        }

        private static readonly Vector3[] Corners = new Vector3[4];

        /// <summary>Widens [left, right] (world x) by the rect's corners.</summary>
        private static void Edges(RectTransform rect, ref float left, ref float right)
        {
            if (rect == null) return;
            rect.GetWorldCorners(Corners);
            left = Math.Min(left, Corners[0].x);
            right = Math.Max(right, Corners[2].x);
        }
    }
}
