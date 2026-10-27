using System;
using System.Reflection;
using UnityEngine;

namespace Ro3Android
{
    /// <summary>
    /// Port adjustments to the mobile HUD (inferred, not the client's layout): its Portuguese text is wider than the
    /// Chinese the layout was drawn for.
    /// <list type="bullet">
    /// <item>The function entries next to the minimap (Eventos, Temporada, Prelúdio): each item is 96 px wide with no
    /// space between items (LoopList.contentSpace 0) but its label (NameFunction_TTxt) 114 px, so the names ran into each
    /// other. The list gets a gap between items, and the label the item's width less a margin and no wrapping, so the
    /// text's own auto-size shrinks it to fit.</item>
    /// <item>The status bar (clock, ping, FPS: UI_Sys_Main_Bar_TimeWidget MainStatusBar) sat in the bottom-left corner,
    /// under the UID notice and the experience bar's "BASE:0%". It is placed to the right of the avatar's level line
    /// (AvatarModule_Btn/Transf_Info, "Lv. 99 CL. 40", whose job level runs past that box's right edge); only its
    /// position changes, not its parent, so a HUD the game rebuilds on a scene change keeps working.</item>
    /// <item>Skills: the mobile UI_Sys_Main_MainMenuWidget prefab has no Skill button (its view's m_kCLICKEVENT_Skill,
    /// m_kRTRANSFORM_Skill are missing), so the skill window could not be opened on Android. A copy of the Bag button
    /// is placed left of it, labelled "Skills" with the skill function's icon (FunctionConfig 10 _kIcon
    /// main_btn_menu_skill), and runs what the PC button runs: UI_Sys_Main_MainMenuWidgetBaseCtrl binds
    /// m_kCLICKEVENT_Skill to openFuncId enum_FunctionType.SkillGrowth, opened through lc_functionhelper.OpenFunctionPanel.</item>
    /// <item>Attributes: the mobile HUD has no entry to the character window either, where the stat points are spent.
    /// A second copy, left of the Skills one (icon main_icon_skillpoint02 of FunctionConfig 22 "Status"), opens
    /// enum_FunctionType.SubPlayerAttr (2202), which LC_FunctionHelper maps to UI_Sys_RolePanel's RoleAttrPanel tab, and
    /// once its UI_Sys_RoleAttributesWidgetCtrl is up calls its OpenAddAttribute, what the PC window does for the jump
    /// parameter "attrpoint" (UI_Sys_RoleWindowCtrl): the point distribution, UI_Sys_RoleAddAttributesWidget.</item>
    /// <item>The attributes tab's own entry to the distribution (View m_kRTRANSFORM_AddAttribute, its m_kBTN_AddAttribute
    /// bound to OnClickAddAttribute) is in the mobile prefab but left inactive, so it is switched on.</item>
    /// </list>
    /// Checked every 2 s: the HUD's widgets are created and replaced by the game's Lua at any time.
    /// </summary>
    public class HudLayout : MonoBehaviour
    {
        private const string SafeScreen = "GameMain/NodeRoot/UINode/UI_Sys_MainPanel(Clone)/Root/Group_MobileRoot_RTransform/UI_Sys_MainWidget(Clone)/Root/Group_SafeScreen/";
        private const string FunctionList = SafeScreen + "Transf_R/Transf_RTop/Anim_RTop/FunctionEntry_RTransform/UI_Sys_Main_FunctionEntryWidget(Clone)/Root/Function_ScrollRect_LoopList";
        private const string LevelLine = SafeScreen + "Transf_L/Transf_Avatar/Anim_AvatarInfo/AvatarInfo_RTransform/UI_Sys_Main_AvatarInfoWidget(Clone)/Root/AvatarModule_Btn/Transf_Info";
        private const string JobLevel = LevelLine + "/Txt_Job/JobLevel_TTxt";
        private const string StatusBar = SafeScreen + "Transf_Mid/Main_Bar_Time_RTransform/UI_Sys_Main_Bar_TimeWidget(Clone)/MainStatusBar";
        private const string BagButton = SafeScreen + "Transf_R/Transf_RTop/Anim_RTop/MainMenu_RTransform/UI_Sys_Main_MainMenuWidget(Clone)/Root/LeftGroup_RTransform/Bag_RTransform";
        private const float ButtonGap = 8f;

        /// <summary>A copy of the Bag button: its slot counts leftwards from the Bag, the icon is a sprite of the game's
        /// atlases drawn at the given scale (an icon that fills more of its box than the Bag's is drawn smaller).</summary>
        private sealed class MenuButton
        {
            public string Name, Label, Icon, Lua;
            /// <summary>Run after <see cref="Lua"/> until it returns "ok" (the panel it opened loads asynchronously).</summary>
            public string Then;
            public int Slot;
            public float IconScale;
        }

        /// <summary>The opened UI_Sys_RolePanel's attributes widget gets OpenAddAttribute; "wait" while it is loading.</summary>
        private const string OpenAddAttribute = @"
for _, stack in pairs(Lua_UIManagerNewIns.m_kPanelManager.m_kUILayerStackMap) do
  for _, entry in pairs(stack) do
    if type(entry) == 'table' and entry.ui_type == 'ui_sys_rolepanel' and entry.target and entry.target.widgetList then
      for _, widget in pairs(entry.target.widgetList) do
        if widget.attrWidget then widget.attrWidget:OpenAddAttribute() return 'ok' end
      end
    end
  end
end
return 'wait'";

        private const float ThenTimeout = 5f;
        private static string _then;
        private static float _thenUntil;
        private const string AttributesTab = "UI_Sys_RoleAttrWidget(Clone)";
        private const string AddAttribute = "AddAttribute_RTransform";

        private static readonly MenuButton[] MenuButtons =
        {
            new MenuButton
            {
                Name = "Ro3Skill_RTransform", Slot = 1, Label = "Skills", Icon = "main_btn_menu_skill", IconScale = 1f,
                Lua = "require('lc_functionhelper').OpenFunctionPanel(enum_FunctionType.SkillGrowth)",
            },
            new MenuButton
            {
                // main_icon_skillpoint02 fills more of its 128 px box than the Bag's icon: drawn smaller to match.
                Name = "Ro3Status_RTransform", Slot = 2, Label = "Atributos", Icon = "main_icon_skillpoint02", IconScale = 0.75f,
                Lua = "require('lc_functionhelper').OpenFunctionPanel(enum_FunctionType.SubPlayerAttr)",
                Then = OpenAddAttribute,
            },
        };

        private static readonly Type UiImage = Type.GetType("UnityEngine.UI.Image, UnityEngine.UI");

        private const float LabelMargin = 4f;
        private const float ItemGap = 24f;
        private const float StatusBarGap = 24f;
        private const BindingFlags AnyInstance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        private static readonly Type TmpText = Type.GetType("TMPro.TMP_Text, Unity.TextMeshPro");
        private static readonly PropertyInfo WordWrapping = TmpText?.GetProperty("enableWordWrapping");
        private static readonly PropertyInfo TmpTextValue = TmpText?.GetProperty("text");

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Install()
        {
            var go = new GameObject("Ro3HudLayout");
            DontDestroyOnLoad(go);
            go.AddComponent<HudLayout>();
        }

        private float _next;
        private int _logged;

        private void Update()
        {
            RunThen();
            if (Time.realtimeSinceStartup < _next) return;
            _next = Time.realtimeSinceStartup + 2;
            try
            {
                FitFunctionLabels();
                PlaceStatusBar();
                foreach (var button in MenuButtons) AddMenuButton(button);
                ShowAddAttribute();
            }
            catch (Exception e)
            {
                if (_logged++ < 3) Debug.LogWarning($"[Ro3Android] HUD layout failed: {e.Message}");
            }
        }

        private static void OnMenuClick(MenuButton button)
        {
            LuaExec.Run(button.Lua, button.Name);
            _then = button.Then;
            _thenUntil = Time.realtimeSinceStartup + ThenTimeout;
        }

        /// <summary>A button's follow-up, tried every 10th frame until it returns "ok", fails (null) or times out.</summary>
        private static void RunThen()
        {
            if (_then == null || Time.frameCount % 10 != 0) return;
            var result = Time.realtimeSinceStartup > _thenUntil ? null : LuaExec.Run(_then, "hud follow-up");
            if (result != "wait") _then = null;
        }

        private static void ShowAddAttribute()
        {
            var tab = GameObject.Find(AttributesTab);
            if (tab == null) return;
            foreach (var child in tab.GetComponentsInChildren<Transform>(true))
                if (child.name == AddAttribute && !child.gameObject.activeSelf) child.gameObject.SetActive(true);
        }

        private static void FitFunctionLabels()
        {
            var list = GameObject.Find(FunctionList);
            var content = list?.transform.Find("Viewport/Content");
            if (content == null) return;
            SpaceItems(list);
            foreach (Transform item in content)
            {
                var label = item.Find("Root/SelectFunction_RTransform/NameFunction_TTxt") as RectTransform;
                if (label == null) continue;
                float width = ((RectTransform)item).rect.width - LabelMargin;
                if (Mathf.Abs(label.sizeDelta.x - width) > 0.5f) label.sizeDelta = new Vector2(width, label.sizeDelta.y);
                var text = TmpText == null ? null : label.GetComponent(TmpText);
                if (text != null && WordWrapping != null && (bool)WordWrapping.GetValue(text)) WordWrapping.SetValue(text, false);
            }
        }

        /// <summary>The game's LoopList (Assembly-CSharp): contentSpace is the gap between items, isLayoutDirty makes it
        /// place them again.</summary>
        private static void SpaceItems(GameObject list)
        {
            var loop = list.GetComponent("LoopList");
            if (loop == null) return;
            var type = loop.GetType();
            var space = type.GetField("contentSpace", AnyInstance);
            if (space == null || space.FieldType != typeof(Vector2)) return;
            if (((Vector2)space.GetValue(loop)).x >= ItemGap) return;
            space.SetValue(loop, new Vector2(ItemGap, 0));
            type.GetField("isLayoutDirty", AnyInstance)?.SetValue(loop, true);
        }

        private static void AddMenuButton(MenuButton button)
        {
            var bag = GameObject.Find(BagButton)?.transform as RectTransform;
            if (bag == null) return;
            if (bag.parent.Find(button.Name) is Transform existing)
            {
                // The icon's atlas may not have been loaded when the button was made.
                SetIcon(existing.Find(IconPath), button);
                return;
            }
            var copy = (RectTransform)Instantiate(bag.gameObject, bag.parent, false).transform;
            copy.name = button.Name;
            copy.anchoredPosition = bag.anchoredPosition - new Vector2(button.Slot * (bag.rect.width + ButtonGap), 0);
            // The game's ClickEvent (Happyengine.UI.Framework, a Selectable) raises its _OnClick UnityEvent on a tap
            // (OnPointerClick -> OnEvent(Click)). The Bag's Lua adds its handler at run time, which a copy does not
            // inherit, so the copy's _OnClick only gets ours. Its keyboard binding (InputBinder) stays with the Bag.
            var click = copy.Find("Bag_ClickEvent");
            var clickEvent = click == null ? null : click.GetComponent("ClickEvent");
            var onClick = clickEvent?.GetType().GetField("_OnClick", AnyInstance)?.GetValue(clickEvent) as UnityEngine.Events.UnityEvent;
            if (onClick == null) { Destroy(copy.gameObject); Debug.LogWarning("[Ro3Android] HUD: no ClickEvent._OnClick on the Bag button"); return; }
            if (click.GetComponent("InputBinder") is Component binder) Destroy(binder);
            foreach (var child in copy.GetComponentsInChildren<Transform>(true))
                if (child.name.IndexOf("RedDot", StringComparison.OrdinalIgnoreCase) >= 0) child.gameObject.SetActive(false);
            onClick.AddListener(() => OnMenuClick(button));
            SetLabel(copy.Find("Bag_ClickEvent/BagNameFunction_TTxt"), button.Label);
            SetIcon(copy.Find(IconPath), button);
            Debug.Log($"[Ro3Android] HUD: {button.Label} button added next to the Bag");
        }

        /// <summary>The label's LangTransUtils_TMP (the game's localizer) writes the Bag's text back over ours, so the copy
        /// loses it first (DestroyImmediate: a deferred Destroy would still run it this frame).</summary>
        private static void SetLabel(Transform label, string value)
        {
            if (label == null) return;
            if (label.GetComponent("LangTransUtils_TMP") is Component localizer) DestroyImmediate(localizer);
            var text = TmpText == null ? null : label.GetComponent(TmpText);
            if (text != null) TmpTextValue?.SetValue(text, value);
        }

        private const string IconPath = "Bag_ClickEvent/Root/BagIconFunction_Img";

        private static void SetIcon(Transform icon, MenuButton button)
        {
            var image = icon == null || UiImage == null ? null : icon.GetComponent(UiImage);
            if (image == null) return;
            var property = UiImage.GetProperty("sprite");
            if ((property.GetValue(image) as Sprite)?.name == button.Icon) return;
            icon.localScale = new Vector3(button.IconScale, button.IconScale, 1);
            var sprite = FindSprite(button.Icon);
            if (sprite != null) property.SetValue(image, sprite);
        }

        private static readonly System.Collections.Generic.Dictionary<string, Sprite> Sprites = new System.Collections.Generic.Dictionary<string, Sprite>();

        private static Sprite FindSprite(string name)
        {
            if (Sprites.TryGetValue(name, out var cached) && cached != null) return cached;
            foreach (var sprite in Resources.FindObjectsOfTypeAll<Sprite>())
                if (sprite.name == name) return Sprites[name] = sprite;
            return null;
        }

        private static void PlaceStatusBar()
        {
            var bar = GameObject.Find(StatusBar)?.transform as RectTransform;
            var line = GameObject.Find(LevelLine)?.transform as RectTransform;
            if (bar == null || line == null) return;
            // The bar's left edge, vertically centred, just right of the level line or of the job level when it runs
            // past it (all in the same canvas).
            var corners = new Vector3[4];
            line.GetWorldCorners(corners);
            float right = corners[2].x;
            if (GameObject.Find(JobLevel)?.transform is RectTransform job)
            {
                var jobCorners = new Vector3[4];
                job.GetWorldCorners(jobCorners);
                right = Mathf.Max(right, jobCorners[2].x);
            }
            float scale = line.lossyScale.x;
            var target = new Vector3(right + StatusBarGap * scale, (corners[0].y + corners[1].y) / 2, bar.position.z);
            var size = bar.rect.size;
            var pivotOffset = new Vector3((bar.pivot.x * size.x) * bar.lossyScale.x, (bar.pivot.y - 0.5f) * size.y * bar.lossyScale.y, 0);
            var position = target + pivotOffset;
            if ((bar.position - position).sqrMagnitude > 1e-6f) bar.position = position;
        }
    }
}
