using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using LuaTable = Obf_R.Obf_ff;
using LuaFunction = Obf_R.Obf_Ef;
using LuaEnv = Obf_R.Obf_Cf;

namespace Ro3Emu.ClientHooks
{
    /// <summary>
    /// Hero Saga: 9 skill slots and 9 consumable slots on the PC bars (Classic and Modern modes; the mobile layout keeps
    /// its 6). Everything the original 6 slots do works the same on slots 7-9 because they ARE the original widgets:
    /// <list type="bullet">
    /// <item>Input: the rebindable actions NormalSkill_07..09 and ShortCut_7..9 are added to the "RO3" source map of the
    /// client's InputActionAsset before the key settings (KeyBoardInfo.OverrideBindingJson) are applied, so
    /// InputSystemManager builds them into its runtime map, saves their rebinding with the others and shows their key
    /// names. Lua sees them through enum_InputKey.</item>
    /// <item>HUD skill bar (UI_Sys_Main_JoyStickSkillWidget_PC, Normal mode): the PC overrides of JoystickSkillModeConfig
    /// (SetBasicInfoPC / InitSkillSlotsPC / RefreshSkillInfoPC of JoyStickSkillNormalMixin) are wrapped: three copies of
    /// slot 6's GameObject get their own UI_Sys_Main_JoyStickSkillItemWidget controllers, keys and data. Bar slots 7-9 are
    /// the server's SkillWheel keys 101-103 (SkillService.WheelKey) because wheel keys 7-14 belong to the skill queue,
    /// mounts, pet and gameplay slots; skills are put on them by dragging, exactly like slots 1-6 (SkillLoad socket 6-8).</item>
    /// <item>HUD consumable bar (UI_Sys_Main_PropsWidget): three copies of item 6 get UI_Sys_Main_PropsItemWidget
    /// controllers for slots 7-9 (ShortcutBarSave/Use slot 6-8) with keys ShortCut_7..9.</item>
    /// <item>Settings → Shortcut keys → Battle (UI_Sys_Setup_ButtonWidget): rows "B. skill 7-9" and the consumable rows
    /// 7-9 are added to the left list and their preview slots to the right side, in the order the panel pairs rows with
    /// preview slots (the panel indexes both lists with the same number).</item>
    /// <item>Auto battle (LC_AutoAttackSkillHelper) also considers the skills on slots 7-9.</item>
    /// </list>
    /// The longer bars stay centred on the screen; the auto-potion and auto-attack buttons that sit after the last slot
    /// move along with them.
    /// </summary>
    public static class HotbarExpansion
    {
        internal const int BaseSlots = 6;
        internal const int Extra = 3;
        internal const int ExtraWheelKeyBase = 100;

        internal static readonly string[] SkillActions = { "NormalSkill_07", "NormalSkill_08", "NormalSkill_09" };
        internal static readonly string[] ShortcutActions = { "ShortCut_7", "ShortCut_8", "ShortCut_9" };
        // Default keys: skills continue after E like the reference layout (- = '), consumables use free F keys.
        private static readonly string[] SkillDefaults = { "<Keyboard>/minus", "<Keyboard>/equals", "<Keyboard>/backquote" };
        private static readonly string[] ShortcutDefaults = { "<Keyboard>/f5", "<Keyboard>/f6", "<Keyboard>/f7" };

        private const string Tag = "[Ro3Emu.Hotbar] ";
        private const string MarkField = "ro3emu_hotbar"; // on our widget controllers and on patched class tables

        // ---------------------------------------------------------------------------------------------------------
        // Delegates pushed to Lua. Trailing params: xLua rejects a reflected call with more Lua arguments than
        // parameters, and some callers pass extra values (multiple returns, event payloads).
        public delegate void SelfMethod(LuaTable self, params object[] rest);
        public delegate object SelfFunc(LuaTable self, params object[] rest);
        public delegate void RegisterButtonMethod(LuaTable self, object click, object chosen, object text, object key, params object[] rest);
        public delegate void SetDataMethod(LuaTable self, object data, object index, params object[] rest);
        public delegate void RebindMethod(LuaTable self, object mode, params object[] rest);
        public delegate void WidgetCallback(LuaTable widget, params object[] rest);

        // Kept alive here: Lua holds pushed delegates only weakly.
        private static readonly SelfMethod SetBasicInfoWrapper = SetBasicInfoPC, InitSkillSlotsWrapper = InitSkillSlotsPC,
            RefreshSkillInfoWrapper = RefreshSkillInfoPC, PropsInitWrapper = PropsInitItemWidgets,
            GenerateButtonsWrapper = SetupGenerateButtonNeeded, LoadSkillDataWrapper = SetupLoadSkillData,
            CloseUnuseWrapper = SetupCloseUnuseButton;
        private static readonly SelfFunc DefaultAutoSlotsWrapper = DefaultAutoBattleSlots;
        private static readonly RegisterButtonMethod RegisterButtonWrapper = SetupRegisterButton;
        private static readonly SetDataMethod ButtonListSetDataWrapper = ButtonListSetData;
        private static readonly RebindMethod RebindClickWrapper = SkillItemRebindClick;
        private static readonly WidgetCallback CanPopTipCallback = (w, rest) => Invoke(w, "SetCanPopSkillTip", true);

        private static LuaEnv _env;
        private static LuaTable _g;
        private static LuaTable _skillBar; // the PC skill bar controller with slots 7-9 (DevDriver state)
        private static LuaFunction _origSetBasic, _origInit, _origRefresh, _origPropsInit, _origGenerate, _origRegister,
            _origLoadSkillData, _origCloseUnuse, _origListSetData, _origRebind, _origDefaultAuto;

        // =========================================================================================================
        // Input actions
        // =========================================================================================================

        private static readonly FieldInfo DefaultSettingField = typeof(InputSystemManager).GetField("defaultSetting",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        private static InputActionAsset _injected;
        private static int _runtimeChecks;

        /// <summary>
        /// From UiTweaks.Tick (every 10th frame). Adds the actions to the asset's "RO3" map as soon as the asset is
        /// loaded (InputSystemManager copies that map into "RO3Current" on every SwitchActionMap, then applies the
        /// saved overrides, which may name our actions). Rebuilds the runtime map once if it was built before.
        /// </summary>
        internal static void TickInput()
        {
            try
            {
                var manager = HappyEngine.Base.SingletonMonoBehaviour<InputSystemManager>.Instance;
                if (manager == null || DefaultSettingField == null) return;
                if (!(DefaultSettingField.GetValue(manager) is InputActionAsset asset)) return;
                if (!ReferenceEquals(asset, _injected))
                {
                    var ro3 = asset.FindActionMap("RO3");
                    if (ro3 == null) return;
                    int added = AddActions(ro3, SkillActions, SkillDefaults) + AddActions(ro3, ShortcutActions, ShortcutDefaults);
                    _injected = asset;
                    _runtimeChecks = 0;
                    Debug.Log(Tag + "input: " + added + " action(s) added to RO3 (" + string.Join(", ", SkillActions) + ", "
                        + string.Join(", ", ShortcutActions) + ")");
                }
                if (_runtimeChecks < 3) CheckRuntimeMap(manager);
            }
            catch (Exception e) { Once("input: " + e.GetType().Name + ": " + e.Message); }
        }

        private static int AddActions(InputActionMap map, string[] names, string[] paths)
        {
            var missing = new List<int>();
            for (int i = 0; i < names.Length; i++) if (map.FindAction(names[i]) == null) missing.Add(i);
            if (missing.Count == 0) return 0;
            // An asset accepts new actions only while none of its maps is enabled (InputSystemManager keeps RO3_Fixed on).
            var enabled = new List<InputActionMap>();
            var maps = map.asset != null ? (IEnumerable<InputActionMap>)map.asset.actionMaps : new[] { map };
            foreach (var m in maps) if (m.enabled) { enabled.Add(m); m.Disable(); }
            try
            {
                foreach (int i in missing) map.AddAction(names[i], InputActionType.Button, paths[i]);
            }
            finally
            {
                foreach (var m in enabled) m.Enable();
            }
            return missing.Count;
        }

        /// <summary>The runtime map ("RO3Current") lacks our actions when it was built before the injection.</summary>
        private static void CheckRuntimeMap(InputSystemManager manager)
        {
            InputActionMap current = null;
            foreach (var field in typeof(InputSystemManager).GetFields(BindingFlags.Instance | BindingFlags.NonPublic))
                if (field.FieldType == typeof(InputActionMap) && field.GetValue(manager) is InputActionMap map && map.name == "RO3Current")
                { current = map; break; }
            if (current == null) return; // not built yet: it will include the actions
            _runtimeChecks++;
            if (current.FindAction(SkillActions[0]) != null) { _runtimeChecks = 3; return; }
            // Same call LC_DAL_Setting._processKeyBoardInfo makes, with the player's saved keys when they are known.
            bool fashion = true;
            string json = null;
            var info = KeyBoardInfo();
            if (info != null)
            {
                json = info.Obf_RuA<object>("OverrideBindingJson") as string;
                object mode = info.Obf_RuA<object>("KeyBoardMode");
                object fashionMode = _g?.Obf_LuA<object>("enum_KeyBoardMode.Fashion");
                if (mode != null && fashionMode != null) fashion = LuaPatches.ToLong(mode) == LuaPatches.ToLong(fashionMode);
            }
            manager.Obf_Dn(fashion, string.IsNullOrEmpty(json) ? null : json);
            Debug.Log(Tag + "input: runtime map rebuilt with the new actions (" + (info != null ? "saved keys" : "defaults") + ")");
        }

        private static LuaTable KeyBoardInfo()
        {
            try
            {
                var assets = _g?.Obf_RuA<LuaTable>("LC_AssetManagerIns");
                return First(Invoke(assets, "GetAssetData", "KeyBoardInfo")) as LuaTable;
            }
            catch { return null; }
        }

        // =========================================================================================================
        // Lua installation (once per LuaEnv, from LuaPatches.Tick)
        // =========================================================================================================

        internal static void Install(LA_LuaManager manager, LuaEnv env, LuaTable global, Func<string, LuaTable> module)
        {
            _env = env;
            _g = global;
            _origSetBasic = _origInit = _origRefresh = _origPropsInit = _origGenerate = _origRegister = null;
            _origLoadSkillData = _origCloseUnuse = _origListSetData = _origRebind = _origDefaultAuto = null;
            _labels.Clear();
            var done = new List<string>();
            Try(done, "enum_InputKey", () => AddInputKeys(global));
            Try(done, "skill bar", () => PatchSkillBar(global, module("joystickskillmodeconfig")));
            Try(done, "skill slot clicks", () =>
            {
                var item = module("ui_sys_main_joystickskillitemwidgetbasectrl");
                _origRebind = Wrap(item, "RebindClickEvent", RebindClickWrapper);
                return _origRebind != null;
            });
            Try(done, "consumable bar", () =>
            {
                var props = module("ui_sys_main_propswidgetctrl");
                _origPropsInit = Wrap(props, "_initItemWidgets", PropsInitWrapper);
                return _origPropsInit != null;
            });
            Try(done, "key settings", () =>
            {
                var setup = module("ui_sys_setup_buttonwidgetctrl");
                _origGenerate = Wrap(setup, "_generateButtonNeeded", GenerateButtonsWrapper);
                _origRegister = Wrap(setup, "_registerButton", RegisterButtonWrapper);
                _origLoadSkillData = Wrap(setup, "_loadSkillData", LoadSkillDataWrapper);
                _origCloseUnuse = Wrap(setup, "_closeUnuseButton", CloseUnuseWrapper);
                var row = module("ui_sys_setup_button_listwidgetctrl");
                _origListSetData = Wrap(row, "SetData", ButtonListSetDataWrapper);
                return _origGenerate != null && _origRegister != null && _origListSetData != null;
            });
            Try(done, "auto battle", () =>
            {
                var auto = global.Obf_RuA<LuaTable>("LC_AutoAttackSkillHelper") ?? module("lc_autoattackskillhelper");
                _origDefaultAuto = Wrap(auto, "GetDefaultAutoBattleSlotList", DefaultAutoSlotsWrapper);
                return _origDefaultAuto != null;
            });
            Debug.Log(Tag + "installed: " + string.Join(", ", done));
        }

        private static void Try(List<string> done, string what, Func<bool> action)
        {
            try { done.Add(what + (action() ? "" : " (not found)")); }
            catch (Exception e) { done.Add(what + " (failed)"); Debug.LogWarning(Tag + what + ": " + e.GetType().Name + ": " + e.Message); }
        }

        /// <summary>Replaces table.name by the delegate, returning the original Lua function (null when absent).</summary>
        private static LuaFunction Wrap<T>(LuaTable table, string name, T wrapper) where T : Delegate
        {
            if (table == null) return null;
            var original = table.Obf_RuA<object>(name) as LuaFunction;
            if (original == null) return null;
            table.Obf_luA<string, T>(name, wrapper);
            return original;
        }

        private static bool AddInputKeys(LuaTable global)
        {
            var keys = global.Obf_RuA<LuaTable>("enum_InputKey");
            if (keys == null) return false;
            foreach (var name in SkillActions) keys.Obf_luA<string, string>(name, name);
            foreach (var name in ShortcutActions) keys.Obf_luA<string, string>(name, name);
            return true;
        }

        // =========================================================================================================
        // HUD skill bar
        // =========================================================================================================

        private static bool PatchSkillBar(LuaTable global, LuaTable config)
        {
            if (config == null) return false;
            object normal = global.Obf_LuA<object>("enum_JoystickSkillMode.Normal");
            var get = config.Obf_RuA<object>("Get") as LuaFunction;
            if (normal == null || get == null) return false;
            // Get returns a copy of the mode's config; its tPCOverrides is the shared table OnOpen copies from.
            var normalConfig = First(get.Obf_HuA(normal)) as LuaTable;
            var overrides = normalConfig?.Obf_RuA<LuaTable>("tPCOverrides");
            if (overrides == null) return false;
            _origSetBasic = Wrap(overrides, "kSetBasicInfo", SetBasicInfoWrapper);
            _origInit = Wrap(overrides, "kInitSkillSlots", InitSkillSlotsWrapper);
            _origRefresh = Wrap(overrides, "kRefreshSkillInfo", RefreshSkillInfoWrapper);
            return _origSetBasic != null && _origInit != null && _origRefresh != null;
        }

        private static bool IsExtended(LuaTable ctrl) => ctrl != null && ctrl.Obf_RuA<object>(MarkField) is bool b && b;

        /// <summary>SetBasicInfoPC: six keys, then ours for slots 7-9 (PC UI only).</summary>
        private static void SetBasicInfoPC(LuaTable self, params object[] rest)
        {
            _origSetBasic?.Obf_HuA(self);
            try
            {
                self.Obf_luA<string, bool>(MarkField, false);
                if (!IsPcUi()) return;
                var keys = self.Obf_RuA<LuaTable>("m_kSkillKeyList");
                if (keys == null) return;
                for (int k = 0; k < Extra; k++) keys.Obf_luA<long, string>(BaseSlots + 1 + k, SkillActions[k]);
                self.Obf_luA<string, bool>(MarkField, true);
            }
            catch (Exception e) { Once("SetBasicInfoPC: " + e.GetType().Name + ": " + e.Message); }
        }

        /// <summary>InitSkillSlotsPC: the six original slots, then slots 7-9 on copies of slot 6's GameObject.</summary>
        private static void InitSkillSlotsPC(LuaTable self, params object[] rest)
        {
            List<RectTransform> clones = null;
            try
            {
                // Copied before the original binds slot 6 so the copies start from the prefab's state.
                if (IsExtended(self))
                {
                    var view = self.Obf_RuA<LuaTable>("View");
                    var source = ViewRect(view, "m_kRTRANSFORM_UI_Com_JoyStickSkillItemWidget5");
                    if (source != null) clones = EnsureClones(source, "Ro3Emu_SkillSlot", BaseSlots + 1);
                    else Once("skill bar: slot 6 not found");
                }
            }
            catch (Exception e) { Once("skill bar clone: " + e.GetType().Name + ": " + e.Message); clones = null; }

            _origInit?.Obf_HuA(self);
            if (clones == null) { TryUnmark(self); return; }
            try
            {
                var views = self.Obf_RuA<LuaTable>("m_kViewList");
                var slots = self.Obf_RuA<LuaTable>("m_kSkillSlots");
                var widgetName = First(Invoke(self, "_getSlotWidgetName")) as string;
                if (views == null || slots == null || string.IsNullOrEmpty(widgetName)) { TryUnmark(self); return; }
                for (int k = 0; k < Extra; k++)
                {
                    int index = BaseSlots + 1 + k;
                    var wrapper = NewRectWrapper(clones[k], self);
                    views.Obf_luA<long, LuaTable>(index, wrapper);
                    var widget = CreateWidget(widgetName, self, wrapper, CanPopTipCallback);
                    if (widget == null) throw new InvalidOperationException("CreateWidgetInstance returned nil for slot " + index);
                    widget.Obf_luA<string, bool>(MarkField, true);
                    slots.Obf_luA<long, LuaTable>(index, widget);
                }
                self.Obf_luA<string, long>("m_iSlotNum", BaseSlots + Extra);
                _skillBar = self;
                LayoutSkillBar(clones);
                Note("skill bar: slots 7-9 created (" + widgetName + ")");
            }
            catch (Exception e)
            {
                Once("skill bar slots: " + e.GetType().Name + ": " + e.Message);
                TryUnmark(self);
            }
        }

        private static void TryUnmark(LuaTable self)
        {
            try
            {
                if (!IsExtended(self)) return;
                self.Obf_luA<string, bool>(MarkField, false);
                // Without slot widgets the extra keys must not be bound (SkillBind indexes m_kSkillSlots).
                var keys = self.Obf_RuA<LuaTable>("m_kSkillKeyList");
                for (int k = 0; k < Extra; k++) keys?.Obf_luA<long, object>(BaseSlots + 1 + k, null);
            }
            catch { }
        }

        /// <summary>RefreshSkillInfoPC: slots 1-6, then 7-9 from wheel keys 101-103.</summary>
        private static void RefreshSkillInfoPC(LuaTable self, params object[] rest)
        {
            _origRefresh?.Obf_HuA(self);
            if (!IsExtended(self)) return;
            try
            {
                var slots = self.Obf_RuA<LuaTable>("m_kSkillSlots");
                var map = SkillSlotMap();
                for (int k = 0; k < Extra; k++)
                {
                    int index = BaseSlots + 1 + k;
                    var widget = slots?.Obf_ruA<long, object>(index) as LuaTable;
                    if (widget == null) continue;
                    var slot = map?.Obf_ruA<long, object>(ExtraWheelKeyBase + 1 + k) as LuaTable;
                    long skill = slot != null ? LuaPatches.ToLong(slot.Obf_RuA<object>("skillConfigId")) : 0;
                    if (skill > 0)
                    {
                        Invoke(self, "_updateSlotSkill", (long)index, null, skill, slot.Obf_RuA<object>("mode") ?? 0L,
                            slot.Obf_RuA<object>("isHighlight") is bool h && h, !(slot.Obf_RuA<object>("enable") is bool en) || en);
                    }
                    else
                    {
                        // Empty: SetData with no skill shows the empty frame and keeps the slot index for drag and drop.
                        var data = _env.Obf_wTA();
                        data.Obf_luA<string, long>("onlySkillId", 0);
                        data.Obf_luA<string, LuaTable>("skillData", _env.Obf_wTA());
                        data.Obf_luA<string, bool>("bHighLight", false);
                        data.Obf_luA<string, bool>("bEnable", true);
                        data.Obf_luA<string, long>("highlightEndTimeMs", 0);
                        data.Obf_luA<string, bool>("bSameInteractInPC", false);
                        Invoke(widget, "SetData", data, (long)index);
                        Invoke(widget, "SetShowState", true);
                    }
                }
            }
            catch (Exception e) { Once("skill bar refresh: " + e.GetType().Name + ": " + e.Message); }
        }

        /// <summary>LC_System_Skill:GetSlotToSkillIdMap() (whellSkills by wheel key).</summary>
        private static LuaTable SkillSlotMap()
        {
            var systems = _g.Obf_RuA<LuaTable>("LC_SystemManager");
            var instance = First(Invoke(systems, "GetInstance")) as LuaTable;
            var skillSystem = First(Invoke(instance, "GetSystem", _g.Obf_LuA<object>("enum_SystemType.SkillSystem"))) as LuaTable;
            return First(Invoke(skillSystem, "GetSlotToSkillIdMap")) as LuaTable;
        }

        /// <summary>
        /// Classic mode casts slots 1-7 with a double click (their index is in the item's click list, 7 being the skill
        /// queue's wheel key); 8 and 9 would get a single click, so they are rebound as if they were slot 1.
        /// </summary>
        private static void SkillItemRebindClick(LuaTable self, object mode, params object[] rest)
        {
            object index = null;
            bool swap = false;
            try
            {
                if (IsExtended(self))
                {
                    index = self.Obf_RuA<object>("m_iIndex");
                    long i = LuaPatches.ToLong(index);
                    swap = i > BaseSlots + 1 && i <= BaseSlots + Extra;
                    if (swap) self.Obf_luA<string, long>("m_iIndex", 1);
                }
            }
            catch { swap = false; }
            try { _origRebind?.Obf_HuA(self, mode); }
            finally { if (swap) self.Obf_luA<string, object>("m_iIndex", index); }
        }

        // =========================================================================================================
        // HUD consumable bar
        // =========================================================================================================

        private static void PropsInitItemWidgets(LuaTable self, params object[] rest)
        {
            List<RectTransform> clones = null;
            try
            {
                if (IsPcUi())
                {
                    var source = ViewRect(self.Obf_RuA<LuaTable>("View"), "m_kRTRANSFORM_PropsItem06");
                    if (source != null) clones = EnsureClones(source, "Ro3Emu_PropsItem", BaseSlots + 1);
                }
            }
            catch (Exception e) { Once("consumable bar clone: " + e.GetType().Name + ": " + e.Message); clones = null; }

            _origPropsInit?.Obf_HuA(self);
            if (clones == null) return;
            try
            {
                var list = self.Obf_RuA<LuaTable>("m_kItemWidgetList");
                string widgetName = _g.Obf_LuA<string>("UINames.UI_Sys_Main_PropsItemWidget");
                if (list == null || string.IsNullOrEmpty(widgetName)) return;
                for (int k = 0; k < Extra; k++)
                {
                    int index = BaseSlots + 1 + k;
                    // The prefab names each item's InputBinder action; the copies carry item 6's.
                    var binder = clones[k].GetComponentInChildren<InputBinder>(true);
                    if (binder != null) binder.Obf_xM(ShortcutActions[k]);
                    var widget = CreateWidget(widgetName, self, NewRectWrapper(clones[k], self), null);
                    if (widget == null) throw new InvalidOperationException("CreateWidgetInstance returned nil for item " + index);
                    widget.Obf_luA<string, bool>(MarkField, true);
                    Invoke(widget, "SetSlotIndex", (long)index);
                    // SetSlotIndex knows ShortCut_1..6 only: give the slot its key and bind it.
                    var keys = widget.Obf_RuA<LuaTable>("m_kActionKeyList");
                    if (keys != null)
                    {
                        keys.Obf_luA<long, string>(index, ShortcutActions[k]);
                        Invoke(widget, "_keyBind");
                    }
                    list.Obf_luA<long, LuaTable>(index, widget);
                }
                LayoutPropsBar(clones);
                Note("consumable bar: items 7-9 created");
            }
            catch (Exception e) { Once("consumable bar items: " + e.GetType().Name + ": " + e.Message); }
        }

        // =========================================================================================================
        // Settings → shortcut keys → battle
        // =========================================================================================================

        /// <summary>Before the panel builds its left list: our rows (once per panel controller) and preview copies.</summary>
        private static void SetupGenerateButtonNeeded(LuaTable self, params object[] rest)
        {
            try { if (IsPcUi()) AddBattleRows(self); }
            catch (Exception e) { Once("settings rows: " + e.GetType().Name + ": " + e.Message); }
            _origGenerate?.Obf_HuA(self);
        }

        private static void AddBattleRows(LuaTable self)
        {
            var list = self.Obf_RuA<LuaTable>("m_kBattleButtonList");
            var checks = self.Obf_RuA<LuaTable>("m_kCheckBattleFunctionList");
            if (list == null || checks == null) return;
            int count = list.Obf_b;
            int skill6 = -1, shortcut6 = -1;
            for (int i = 1; i <= count; i++)
            {
                var key = (list.Obf_ruA<long, object>(i) as LuaTable)?.Obf_RuA<object>("kInputKey") as string;
                if (key == SkillActions[0] || key == ShortcutActions[0]) return; // already added
                if (key == "NormalSkill_06") skill6 = i;
                if (key == "ShortCut_6") shortcut6 = i;
            }
            if (skill6 < 0 || shortcut6 < 0) { Once("settings rows: NormalSkill_06 / ShortCut_6 rows not found"); return; }
            // The rows show only in the PC UI, like the original consumable rows (their check function).
            object pcOnly = checks.Obf_RuA<object>("ShortCut_1");
            var insert = _g.Obf_LuA<LuaFunction>("table.insert");
            // Consumables first: inserting the skills moves the consumable rows down.
            for (int k = 0; k < Extra; k++)
                insert.Obf_HuA(list, (long)(shortcut6 + 1 + k), NewRow(self, ShortcutActions[k], 27200));
            for (int k = 0; k < Extra; k++)
                insert.Obf_HuA(list, (long)(skill6 + 1 + k), NewRow(self, SkillActions[k], 27038));
            foreach (var key in SkillActions) checks.Obf_luA<string, object>(key, pcOnly);
            foreach (var key in ShortcutActions) checks.Obf_luA<string, object>(key, pcOnly);
            EnsureSetupPreviewCopies(self);
        }

        /// <summary>generateParam(key) with the label word of slot 6 (our label replaces it in the row's SetData).</summary>
        private static LuaTable NewRow(LuaTable self, string key, long labelOf6)
        {
            var row = First(Invoke(self, "generateParam", key)) as LuaTable;
            row?.Obf_luA<string, long>("iInputKeyName", labelOf6);
            return row;
        }

        /// <summary>Row label: slot 6's own text with 7, 8 or 9 ("B. skill 6" -> "B. skill 7").</summary>
        private static void ButtonListSetData(LuaTable self, object data, object index, params object[] rest)
        {
            _origListSetData?.Obf_HuA(self, data, index);
            try
            {
                if (!(data is LuaTable row) || !(row.Obf_RuA<object>("kInputKey") is string key)) return;
                string label = Label(key);
                if (label == null) return;
                var text = self.Obf_LuA<LuaTable>("View.m_kTTXT_SetUp");
                Invoke(text, "SetText", label);
            }
            catch (Exception e) { Once("settings row label: " + e.GetType().Name + ": " + e.Message); }
        }

        private static readonly Dictionary<string, string> _labels = new Dictionary<string, string>();

        private static string Label(string key)
        {
            int k = Array.IndexOf(SkillActions, key);
            bool skill = k >= 0;
            if (!skill) k = Array.IndexOf(ShortcutActions, key);
            if (k < 0) return null;
            if (_labels.TryGetValue(key, out var cached)) return cached;
            string six = Word(skill ? 27038 : 27200);
            string label;
            int digit = six?.LastIndexOf('6') ?? -1;
            if (digit >= 0) label = six.Substring(0, digit) + (BaseSlots + 1 + k) + six.Substring(digit + 1);
            else label = (skill ? "B. skill " : "Atalho ") + (BaseSlots + 1 + k);
            _labels[key] = label;
            return label;
        }

        private static string Word(long id)
        {
            try
            {
                var db = _g.Obf_RuA<LuaTable>("Lua_DBManager");
                var words = (First(Invoke(db, "GetInstance")) as LuaTable)?.Obf_RuA<LuaTable>("m_kCFG_UIWords");
                return First(Invoke(words, "GetWordNew", id)) as string;
            }
            catch { return null; }
        }

        /// <summary>
        /// The right side pairs preview slot n with left row n (m_kRightChooseImg / m_kBattleLeftNumberList), so the
        /// preview slots 7-9 are registered right after slot 6 / consumable 6.
        /// </summary>
        private static void SetupRegisterButton(LuaTable self, object click, object chosen, object text, object key, params object[] rest)
        {
            _origRegister?.Obf_HuA(self, click, chosen, text, key);
            try
            {
                if (!(key is string name) || !IsPcUi()) return;
                if (name == "NormalSkill_06")
                {
                    // _loadButtons registers m_kNormalSkillButtons with ipairs: appending now makes it register
                    // NormalSkill_07..09 itself (enum_InputKey has them).
                    var buttons = self.Obf_RuA<LuaTable>("m_kNormalSkillButtons");
                    if (buttons == null || buttons.Obf_b != BaseSlots) return;
                    var copies = PreviewCopies(self, "m_kRTRANSFORM_Normal6", "Ro3Emu_Normal");
                    if (copies == null) return;
                    string widgetName = _g.Obf_LuA<string>("UINames.UI_Sys_Setup_SkillWidget");
                    for (int k = 0; k < Extra; k++)
                    {
                        var widget = CreateWidget(widgetName, self, NewRectWrapper(copies[k], self), null);
                        if (widget == null) return;
                        buttons.Obf_luA<long, LuaTable>(BaseSlots + 1 + k, widget);
                    }
                }
                else if (name == "ShortCut_6")
                {
                    var buttons = self.Obf_RuA<LuaTable>("m_kShortcutBarButtons");
                    if (buttons == null || buttons.Obf_b != BaseSlots) return;
                    var copies = PreviewCopies(self, "m_kRTRANSFORM_Props06", "Ro3Emu_Props");
                    if (copies == null) return;
                    string widgetName = _g.Obf_LuA<string>("UINames.UI_Sys_Setup_SkillWidget");
                    for (int k = 0; k < Extra; k++)
                    {
                        var widget = CreateWidget(widgetName, self, NewRectWrapper(copies[k], self), null);
                        if (widget == null) return;
                        buttons.Obf_luA<long, LuaTable>(BaseSlots + 1 + k, widget);
                        var view = widget.Obf_RuA<LuaTable>("View");
                        _origRegister.Obf_HuA(self, view?.Obf_RuA<object>("m_kCLICKEVENT_JoystickItem"),
                            view?.Obf_RuA<object>("m_kIMG_Chosen"), view?.Obf_RuA<object>("m_kTTXT_KeyPosition"), ShortcutActions[k]);
                    }
                }
            }
            catch (Exception e) { Once("settings preview: " + e.GetType().Name + ": " + e.Message); }
        }

        /// <summary>_loadSkillData: icons of preview slots 7-9 from wheel keys 101-103; the expand slot as before.</summary>
        private static void SetupLoadSkillData(LuaTable self, params object[] rest)
        {
            _origLoadSkillData?.Obf_HuA(self);
            try
            {
                var buttons = self.Obf_RuA<LuaTable>("m_kNormalSkillButtons");
                if (buttons == null || buttons.Obf_b != BaseSlots + Extra) return;
                var map = SkillSlotMap();
                for (int k = 0; k < Extra; k++)
                {
                    var slot = map?.Obf_ruA<long, object>(ExtraWheelKeyBase + 1 + k) as LuaTable;
                    object skill = slot?.Obf_RuA<object>("skillConfigId");
                    Invoke(buttons.Obf_ruA<long, object>(BaseSlots + 1 + k) as LuaTable, "SetSkill", skill);
                }
                // The original shows the 7th wheel skill on the expand slot (#normal + 1).
                var expand = self.Obf_RuA<LuaTable>("m_kExpandSkillButton");
                if (expand != null)
                {
                    var systems = _g.Obf_RuA<LuaTable>("LC_SystemManager");
                    var instance = First(Invoke(systems, "GetInstance")) as LuaTable;
                    var skillSystem = First(Invoke(instance, "GetSystem", _g.Obf_LuA<object>("enum_SystemType.SkillSystem"))) as LuaTable;
                    var list = First(Invoke(skillSystem, "GetSkillList")) as LuaTable;
                    Invoke(expand, "SetSkill", list?.Obf_ruA<long, object>(BaseSlots + 1));
                }
            }
            catch (Exception e) { Once("settings preview icons: " + e.GetType().Name + ": " + e.Message); }
        }

        /// <summary>After the original hides the unused preview slots: lay out the longer preview rows.</summary>
        private static void SetupCloseUnuseButton(LuaTable self, params object[] rest)
        {
            _origCloseUnuse?.Obf_HuA(self);
            try { if (IsPcUi()) LayoutSetupPreview(self); }
            catch (Exception e) { Once("settings preview layout: " + e.GetType().Name + ": " + e.Message); }
        }

        private static void EnsureSetupPreviewCopies(LuaTable self)
        {
            PreviewCopies(self, "m_kRTRANSFORM_Normal6", "Ro3Emu_Normal");
            PreviewCopies(self, "m_kRTRANSFORM_Props06", "Ro3Emu_Props");
        }

        private static List<RectTransform> PreviewCopies(LuaTable self, string sixField, string prefix)
        {
            var six = ViewRect(self.Obf_RuA<LuaTable>("View"), sixField);
            if (six == null) { Once("settings preview: " + sixField + " not found"); return null; }
            return EnsureClones(six, prefix, BaseSlots + 1);
        }

        // =========================================================================================================
        // Auto battle
        // =========================================================================================================

        private static object DefaultAutoBattleSlots(LuaTable self, params object[] rest)
        {
            var result = First(_origDefaultAuto?.Obf_HuA(self)) as LuaTable;
            try
            {
                if (result == null || !IsPcUi()) return result;
                int count = result.Obf_b;
                for (int i = 1; i <= count; i++)
                    if (LuaPatches.ToLong(result.Obf_ruA<long, object>(i)) == ExtraWheelKeyBase + 1) return result;
                for (int k = 0; k < Extra; k++) result.Obf_luA<long, long>(count + 1 + k, ExtraWheelKeyBase + 1 + k);
            }
            catch (Exception e) { Once("auto battle slots: " + e.GetType().Name + ": " + e.Message); }
            return result;
        }

        // =========================================================================================================
        // Layout
        // =========================================================================================================

        // The slot rows are layout groups aligned left: the copies extend them to the right by themselves. Each bar's
        // container in the HUD's Pan_Bottom then moves left by half the growth so the longer bar stays centred on the
        // screen, like the original 6 slots. What sits after the 6th slot (auto-potion, auto-attack, mount skills, the
        // pet divider) moves right by the other half, so it follows the 9th slot at its original distance; what sits
        // before the 1st slot (gameplay and derived skills) moves left with the bar. Positions are re-applied when the
        // game moves them (template switches) and restored when the copies are not shown.
        private static readonly string[] AfterPropsBar = { "Main_DrugWidget_RTransform" };
        private static readonly string[] AfterSkillBar = { "AutoFight_RTransform", "Main_MountSkill_RTransform", "PetSkillLine_Img" };
        private static readonly string[] BeforePropsBar = { "Main_PlaySkill_RTransform" };
        private static readonly string[] BeforeSkillBar = { "Main_DerivedSkill_RTransform" };
        private static RectTransform _propsSix, _propsNine, _skillSix, _skillNine;
        private static readonly Dictionary<int, KeyValuePair<Vector2, Vector2>> _placed = new Dictionary<int, KeyValuePair<Vector2, Vector2>>();

        private static void LayoutSkillBar(List<RectTransform> clones)
        {
            _skillSix = clones[0].parent.GetChild(Mathf.Max(0, clones[0].GetSiblingIndex() - 1)) as RectTransform;
            _skillNine = clones[Extra - 1];
            DumpOnce("skill bar", clones[0].parent.parent?.parent?.parent);
        }

        private static void LayoutPropsBar(List<RectTransform> clones)
        {
            _propsSix = clones[0].parent.GetChild(Mathf.Max(0, clones[0].GetSiblingIndex() - 1)) as RectTransform;
            _propsNine = clones[Extra - 1];
            DumpOnce("consumable bar", clones[0].parent.parent?.parent?.parent);
        }

        /// <summary>From UiTweaks.Tick (every 10th frame).</summary>
        internal static void TickLayout()
        {
            try
            {
                FollowBar(_propsSix, _propsNine, BeforePropsBar, AfterPropsBar, null);
                FollowBar(_skillSix, _skillNine, BeforeSkillBar, AfterSkillBar, "Pic_PetSkillLine");
            }
            catch (Exception e) { Once("layout: " + e.GetType().Name + ": " + e.Message); }
        }

        private static readonly Vector3[] Corners = new Vector3[4];

        private static void FollowBar(RectTransform six, RectTransform nine, string[] before, string[] after, string innerDivider)
        {
            if (six == null || nine == null) return; // destroyed with their panel (Unity null)
            var bottom = Ancestor(six, "Pan_Bottom");
            if (bottom == null) return;
            // The bar's container: the child of Pan_Bottom that holds the slots (Props_RTransform, JoyStickSkill_Custom).
            Transform container = six;
            while (container.parent != null && container.parent != bottom) container = container.parent;
            if (container.parent != bottom || !(container is RectTransform bar)) return;
            // Pan_Bottom units between the 6th and the 9th slot; 0 when the copies are hidden (restores positions).
            // Measured between two slots of the same bar, so moving the container does not change it.
            float offset = 0;
            if (nine.gameObject.activeInHierarchy && six.gameObject.activeInHierarchy)
                offset = Mathf.Max(0, RightEdge(bottom, nine) - RightEdge(bottom, six));
            float half = offset / 2;
            Place(bar, -half);
            foreach (var name in before)
                if (bottom.Find(name) is RectTransform left) Place(left, -half);
            foreach (var name in after)
                if (bottom.Find(name) is RectTransform right) Place(right, half);
            // A frame the player placed in Edit layout (or a bar the player moved) is not shifted above, so the grown bar
            // could cover the buttons after it (players' report, 22.10). One that overlaps the bar's row goes right of the
            // 9th slot; once clear it is left alone, so a saved layout does not drift (inferred placement).
            if (offset > 0)
                foreach (var name in after)
                    if (bottom.Find(name) is RectTransform right) KeepClear(bottom, six.parent.GetChild(0) as RectTransform, nine, right);
            // The bar's own divider is anchored to the middle of the grown row, which the growth moved right by half
            // and the container left by half: the other half puts it after the 9th slot.
            if (innerDivider != null && Ancestor(six, "Pan_Cell") is RectTransform cell && cell.Find(innerDivider) is RectTransform divider)
                Place(divider, half);
        }

        private const float ClearGap = 6f;

        /// <summary>Moves <paramref name="element"/> (a child of <paramref name="space"/>) right of <paramref name="last"/>
        /// when it overlaps the row from <paramref name="first"/> to <paramref name="last"/>.</summary>
        private static void KeepClear(Transform space, RectTransform first, RectTransform last, RectTransform element)
        {
            if (first == null || element.parent != space || !element.gameObject.activeInHierarchy || !last.gameObject.activeInHierarchy) return;
            first.GetWorldCorners(Corners);
            float rowLeft = space.InverseTransformPoint(Corners[0]).x;
            last.GetWorldCorners(Corners);
            Vector3 rowMin = space.InverseTransformPoint(Corners[0]), rowMax = space.InverseTransformPoint(Corners[2]);
            element.GetWorldCorners(Corners);
            Vector3 min = space.InverseTransformPoint(Corners[0]), max = space.InverseTransformPoint(Corners[2]);
            if (max.y <= rowMin.y || min.y >= rowMax.y || max.x <= rowLeft || min.x >= rowMax.x) return;
            float push = rowMax.x + ClearGap - min.x;
            if (push > 0.5f) element.anchoredPosition += new Vector2(push, 0);
        }
        private static float RightEdge(Transform space, RectTransform rect)
        {
            rect.GetWorldCorners(Corners);
            return space.InverseTransformPoint(Corners[2]).x;
        }

        private static Transform Ancestor(Transform t, string name)
        {
            for (var p = t.parent; p != null; p = p.parent) if (p.name == name) return p;
            return null;
        }

        /// <summary>
        /// Puts the element <paramref name="dx"/> right of its own position. HUD frames the player can drag in
        /// Settings → Edit layout (UILayoutPlugin) get a new default position instead: the editor saves a frame whose
        /// position differs from its default as a custom one (it would be shifted again on the next login), and a
        /// frame the player placed keeps that place.
        /// </summary>
        private static void Place(RectTransform rect, float dx)
        {
            var plugin = PluginDefaultPos != null ? rect.GetComponent<UILayoutPlugin>() : null;
            if (plugin != null) { PlacePlugin(plugin, rect, dx); return; }
            int id = rect.GetInstanceID();
            var current = rect.anchoredPosition;
            var original = current;
            if (_placed.TryGetValue(id, out var placed) && (current - placed.Value).sqrMagnitude < 0.01f) original = placed.Key;
            var target = original + new Vector2(dx, 0);
            if ((current - target).sqrMagnitude > 0.01f) rect.anchoredPosition = target;
            _placed[id] = new KeyValuePair<Vector2, Vector2>(original, target);
        }

        private static readonly FieldInfo PluginDefaultPos = typeof(UILayoutPlugin).GetField("defaultAnchorPos",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        private static readonly Dictionary<int, Vector2> _pluginDefaults = new Dictionary<int, Vector2>();

        private static void PlacePlugin(UILayoutPlugin plugin, RectTransform rect, float dx)
        {
            int id = plugin.GetInstanceID();
            var current = (Vector2)PluginDefaultPos.GetValue(plugin);
            if (!_pluginDefaults.TryGetValue(id, out var original))
            {
                original = current; // the prefab's default
                _pluginDefaults[id] = original;
                Note("layout: " + plugin.PluginName + " is an editable HUD frame (" + rect.name + ")");
            }
            var target = original + new Vector2(dx, 0);
            if ((current - target).sqrMagnitude < 0.01f) return;
            PluginDefaultPos.SetValue(plugin, target);
            // At its previous default, i.e. not placed by the player (no saved override): follow the new default.
            if ((rect.anchoredPosition - current).sqrMagnitude < 0.01f) rect.anchoredPosition = target;
        }

        private static void LayoutSetupPreview(LuaTable self)
        {
            var view = self.Obf_RuA<LuaTable>("View");
            var normal6 = ViewRect(view, "m_kRTRANSFORM_Normal6");
            var props6 = ViewRect(view, "m_kRTRANSFORM_Props06");
            if (normal6 != null) DumpOnce("setup", normal6.parent?.parent?.parent);
            // Only rows that got their copies (AddBattleRows / SetupRegisterButton).
            if (props6 != null && props6.parent.Find("Ro3Emu_Props" + (BaseSlots + 1)) != null)
                FitPreviewRow(props6.parent as RectTransform, "MainShortcutsItem_RTransform", null);
            if (normal6 != null && normal6.parent.Find("Ro3Emu_Normal" + (BaseSlots + 1)) != null)
                FitPreviewRow(normal6.parent as RectTransform, "AutoFight_RTransform", "Line");
        }

        private struct PreviewOriginal
        {
            public float FirstLeft, Right6, AutoX, LineX;
            public int Columns;
            public Vector2 Size;
        }

        private static readonly Dictionary<int, PreviewOriginal> _preview = new Dictionary<int, PreviewOriginal>();

        /// <summary>
        /// A settings preview row (a GridLayoutGroup of 7 columns and the auto button after it) with 9 slots: one line,
        /// scaled down so slots and button keep their spacing inside the row's original width. <paramref name="divider"/>
        /// is a separator after the slots, a sibling of the row's panel.
        /// </summary>
        private static void FitPreviewRow(RectTransform grid, string autoName, string divider)
        {
            var layout = grid != null ? grid.GetComponent<GridLayoutGroup>() : null;
            var row = grid != null ? grid.parent as RectTransform : null;
            var auto = row != null ? row.Find(autoName) as RectTransform : null;
            if (layout == null || auto == null) return;
            var line = divider != null && row.parent != null ? row.parent.Find(divider) as RectTransform : null;
            float cell = layout.cellSize.x, spacing = layout.spacing.x, pad = layout.padding.left;
            int id = grid.GetInstanceID();
            if (!_preview.TryGetValue(id, out var o))
            {
                // Row-local x (the row's pivot is 0): edges of slot 1 and 6, the button's and the divider's centres.
                o.Size = grid.sizeDelta;
                o.Columns = layout.constraintCount;
                float gridLeft = grid.localPosition.x - grid.pivot.x * grid.rect.width;
                o.FirstLeft = gridLeft + pad;
                o.Right6 = o.FirstLeft + BaseSlots * cell + (BaseSlots - 1) * spacing;
                o.AutoX = auto.localPosition.x;
                o.LineX = line != null ? line.localPosition.x - row.localPosition.x : 0;
                _preview[id] = o;
            }
            int slots = BaseSlots + Extra;
            float width9 = slots * cell + (slots - 1) * spacing;
            float autoHalf = auto.rect.width / 2;
            float span = o.AutoX + autoHalf - o.FirstLeft;               // slot 1 .. end of the button, before
            float natural = width9 + (o.AutoX + autoHalf - o.Right6);    // the same with 9 slots at full size
            float s = Mathf.Min(1f, span / natural);

            if (layout.constraint != GridLayoutGroup.Constraint.Flexible && layout.constraintCount < slots) layout.constraintCount = slots;
            grid.sizeDelta = new Vector2(pad + width9 + layout.padding.right, o.Size.y);
            grid.localScale = new Vector3(s, s, 1);
            float gridLeftNew = o.FirstLeft - s * pad;
            MoveLocalX(grid, gridLeftNew + grid.pivot.x * grid.rect.width * s);
            auto.localScale = new Vector3(s, s, 1);
            MoveLocalX(auto, o.FirstLeft + s * (width9 + (o.AutoX - o.Right6)));
            if (line != null) MoveLocalX(line, row.localPosition.x + o.FirstLeft + s * (width9 + (o.LineX - o.Right6)));
            LayoutRebuilder.MarkLayoutForRebuild(grid);
        }

        private static void MoveLocalX(RectTransform rect, float x)
        {
            float dx = x - rect.localPosition.x;
            if (Mathf.Abs(dx) > 0.01f) rect.anchoredPosition += new Vector2(dx, 0);
        }

        // =========================================================================================================
        // DevDriver commands (RO3EMU_DEVDRIVER only): the calls a drop on a slot makes, and the bars' state.
        // =========================================================================================================

        /// <summary>LC_SkillDevelopServer SkillLoad (Load) of the skill on wheel key <paramref name="fromKey"/> into bar slot <paramref name="slot"/>.</summary>
        internal static string DevSkillLoad(int slot, int fromKey)
        {
            var source = SkillSlotMap()?.Obf_ruA<long, object>(fromKey) as LuaTable;
            long id = source != null ? LuaPatches.ToLong(source.Obf_RuA<object>("skillConfigId")) : 0;
            if (id <= 0) return "no skill on wheel key " + fromKey;
            var args = _env.Obf_wTA();
            var info = _env.Obf_wTA();
            info.Obf_luA<long, long>(1, id / 100);
            info.Obf_luA<long, long>(2, id % 100);
            args.Obf_luA<long, object>(1, _g.Obf_LuA<object>("Enum_LoadOperation.Load"));
            args.Obf_luA<long, bool>(2, true);
            args.Obf_luA<long, long>(3, slot);
            args.Obf_luA<long, LuaTable>(4, info);
            var server = First(Invoke(_g.Obf_RuA<LuaTable>("ServerLocation"), "GetServer", "LC_SkillDevelopServer"));
            _g.Obf_RuA<LuaFunction>("pcall").Obf_HuA(server, "SkillLoad", args);
            return "SkillLoad slot " + slot + " skill " + id / 100 + " lv " + id % 100;
        }

        /// <summary>LC_ShortcutBarService.SaveSlot(slot, guid of the item on consumable slot <paramref name="from"/>).</summary>
        internal static string DevShortcutSave(int slot, int from)
        {
            var assets = _g.Obf_RuA<LuaTable>("LC_AssetManagerIns");
            var data = First(Invoke(assets, "GetAssetData", "ShortcutBarData")) as LuaTable;
            var entry = data?.Obf_ruA<long, object>(from) as LuaTable;
            object guid = entry?.Obf_RuA<object>("iItemGuid");
            if (LuaPatches.ToLong(guid) <= 0) return "no item on consumable slot " + from;
            var service = _g.Obf_LuA<LuaTable>("package.loaded.lc_shortcutbarservice");
            (service?.Obf_RuA<object>("SaveSlot") as LuaFunction)?.Obf_HuA((long)slot, guid);
            return "SaveSlot " + slot + " guid " + guid;
        }

        internal static string DevState()
        {
            var triggers = new StringBuilder();
            try
            {
                var check = First(Invoke(_g.Obf_RuA<LuaTable>("LC_AssetManagerIns"), "GetAssetData", "DragTriggerCheckData")) as LuaTable;
                foreach (var t in UnityEngine.Object.FindObjectsOfType<UIDragTrigger>())
                {
                    if (!(t.name.Contains("JoystickItem") || t.name.Contains("Props"))) continue;
                    bool registered = check?.Obf_ruA<long, object>(t.GetInstanceID()) != null;
                    var p = t.transform;
                    string path = t.name;
                    for (int i = 0; i < 4 && p.parent != null; i++) { p = p.parent; path = p.name + "/" + path; }
                    triggers.Append("\n  trigger ").Append(registered ? "registered " : "NOT registered ").Append(path);
                }
            }
            catch (Exception e) { triggers.Append("\n  triggers: " + e.Message); }
            try
            {
                // Each bar slot: its index, skill, and whether its drop check accepts skill 1's skill (as a drag from the skill window).
                var slots = _skillBar?.Obf_RuA<LuaTable>("m_kSkillSlots");
                var probe = _env.Obf_wTA();
                var kData = _env.Obf_wTA();
                long sample = LuaPatches.ToLong((SkillSlotMap()?.Obf_ruA<long, object>(2) as LuaTable)?.Obf_RuA<object>("skillConfigId"));
                kData.Obf_luA<string, long>("iSkillId", sample / 100);
                kData.Obf_luA<string, long>("iLevel", sample % 100);
                kData.Obf_luA<string, long>("iSlotIndex", 0);
                probe.Obf_luA<string, object>("iItemType", _g.Obf_LuA<object>("enum_DragItemType.Skill"));
                probe.Obf_luA<string, LuaTable>("kData", kData);
                for (int i = 1; i <= BaseSlots + Extra; i++)
                {
                    var w = slots?.Obf_ruA<long, object>(i) as LuaTable;
                    if (w == null) { triggers.Append("\n  slot ").Append(i).Append(": no widget"); continue; }
                    object accepts = First(Invoke(w, "_checkDragItem", probe));
                    var trigger = w.Obf_LuA<LuaTable>("View.m_kDRAGTRIGGER_JoystickItem");
                    triggers.Append("\n  slot ").Append(i).Append(": index=").Append(w.Obf_RuA<object>("m_iIndex") ?? "nil")
                        .Append(" skill=").Append(w.Obf_RuA<object>("m_iOnlySkillId") ?? "nil")
                        .Append(" accepts=").Append(accepts ?? "nil")
                        .Append(" checkFunc=").Append(trigger?.Obf_RuA<object>("m_kCheckFunc") != null)
                        .Append(" endFunc=").Append(trigger?.Obf_RuA<object>("m_kProgressFunc") != null);
                }
            }
            catch (Exception e) { triggers.Append("\n  slots: " + e.GetType().Name + ": " + e.Message); }
            var sb = new StringBuilder("wheel:");
            var map = SkillSlotMap();
            foreach (int key in new[] { 1, 2, 3, 4, 5, 6, 101, 102, 103 })
                sb.Append(' ').Append(key).Append('=').Append(LuaPatches.ToLong((map?.Obf_ruA<long, object>(key) as LuaTable)?.Obf_RuA<object>("skillConfigId")));
            var data = First(Invoke(_g.Obf_RuA<LuaTable>("LC_AssetManagerIns"), "GetAssetData", "ShortcutBarData")) as LuaTable;
            sb.Append("\nconsumables:");
            for (int i = 1; i <= BaseSlots + Extra; i++)
                sb.Append(' ').Append(i).Append('=').Append(LuaPatches.ToLong((data?.Obf_ruA<long, object>(i) as LuaTable)?.Obf_RuA<object>("iItemGuid")));
            return sb.Append(triggers).ToString();
        }

        // =========================================================================================================
        // Lua / Unity helpers
        // =========================================================================================================

        /// <summary>Copies of <paramref name="source"/> named prefix+number, right after it; existing copies are reused.</summary>
        private static List<RectTransform> EnsureClones(RectTransform source, string prefix, int firstNumber)
        {
            var parent = source.parent;
            var result = new List<RectTransform>(Extra);
            for (int k = 0; k < Extra; k++)
            {
                string name = prefix + (firstNumber + k);
                var copy = parent.Find(name) as RectTransform;
                if (copy == null)
                {
                    var go = UnityEngine.Object.Instantiate(source.gameObject, parent, false);
                    go.name = name;
                    copy = (RectTransform)go.transform;
                }
                copy.SetSiblingIndex(source.GetSiblingIndex() + 1 + k);
                if (!copy.gameObject.activeSelf) copy.gameObject.SetActive(true);
                result.Add(copy);
            }
            return result;
        }

        /// <summary>A view component wrapper (UIRectTransformNew) for a GameObject the view binder does not know.</summary>
        private static LuaTable NewRectWrapper(RectTransform rect, LuaTable ctrl)
        {
            var cls = _g.Obf_RuA<LuaTable>("UIRectTransformNew");
            var wrapper = First((cls?.Obf_RuA<object>("New") as LuaFunction)?.Obf_HuA()) as LuaTable;
            if (wrapper == null) throw new InvalidOperationException("UIRectTransformNew.New failed");
            Invoke(wrapper, "Init", rect, ctrl);
            Invoke(wrapper, "_flush");
            return wrapper;
        }

        private static LuaTable CreateWidget(string uiName, LuaTable parent, LuaTable view, Delegate callback)
        {
            var managerClass = _g.Obf_RuA<LuaTable>("Lua_UIManagerNew");
            var manager = First(Invoke(managerClass, "GetInstance")) as LuaTable;
            return First(Invoke(manager, "CreateWidgetInstance", uiName, parent, view, callback)) as LuaTable;
        }

        private static RectTransform ViewRect(LuaTable view, string field)
        {
            var wrapper = view?.Obf_RuA<object>(field) as LuaTable;
            return First(Invoke(wrapper, "GetTransform")) as RectTransform;
        }

        private static bool IsPcUi()
        {
            try
            {
                var modes = _g?.Obf_RuA<LuaTable>("LC_OperationModeManager");
                long mode = LuaPatches.ToLong(First(Invoke(modes, "GetUIModeType")));
                long pc = LuaPatches.ToLong(_g?.Obf_LuA<object>("enum_GameOperatingMode.PCMode"));
                return pc != long.MinValue && mode == pc;
            }
            catch { return false; }
        }

        private static object[] Invoke(LuaTable target, string method, params object[] args)
        {
            if (target == null) return null;
            var function = target.Obf_RuA<object>(method) as LuaFunction;
            if (function == null) return null;
            var all = new object[args.Length + 1];
            all[0] = target;
            Array.Copy(args, 0, all, 1, args.Length);
            return function.Obf_HuA(all);
        }

        private static object First(object[] values) => values != null && values.Length > 0 ? values[0] : null;

        private static readonly HashSet<string> Reported = new HashSet<string>();
        private static void Once(string message) { if (Reported.Add(message)) Debug.LogWarning(Tag + message); }
        private static void Note(string message) { if (Reported.Add(message)) Debug.Log(Tag + message); }

        private static readonly HashSet<string> Dumped = new HashSet<string>();

        /// <summary>The widget and its ancestors' children with their rectangles, once per kind (layout evidence).</summary>
        private static void DumpOnce(string kind, Transform root)
        {
            if (root == null || !Dumped.Add(kind)) return;
            var sb = new StringBuilder(Tag + "layout " + kind + ":");
            Dump(sb, root, 0, 4);
            var parent = root.parent;
            if (parent != null)
            {
                sb.Append("\n  -- siblings under ").Append(parent.name);
                for (int i = 0; i < parent.childCount; i++) Dump(sb, parent.GetChild(i), 1, 0);
                if (parent.parent != null)
                {
                    sb.Append("\n  -- siblings under ").Append(parent.parent.name);
                    for (int i = 0; i < parent.parent.childCount; i++) Dump(sb, parent.parent.GetChild(i), 1, 1);
                }
            }
            Debug.Log(sb.ToString());
        }

        internal static void Dump(StringBuilder sb, Transform t, int depth, int maxDepth)
        {
            sb.Append('\n').Append(' ', depth * 2 + 2).Append(t.name);
            if (!t.gameObject.activeSelf) sb.Append(" [off]");
            if (t is RectTransform r)
            {
                var corners = new Vector3[4];
                r.GetWorldCorners(corners);
                sb.Append(" ap=").Append(r.anchoredPosition.ToString("F0")).Append(" sz=").Append(r.sizeDelta.ToString("F0"))
                  .Append(" rect=").Append(r.rect.size.ToString("F0")).Append(" amin=").Append(r.anchorMin.ToString("F2"))
                  .Append(" amax=").Append(r.anchorMax.ToString("F2")).Append(" piv=").Append(r.pivot.ToString("F2"))
                  .Append(" scale=").Append(r.localScale.x.ToString("F2"))
                  .Append(" world=[").Append(corners[0].x.ToString("F0")).Append(',').Append(corners[0].y.ToString("F0"))
                  .Append("..").Append(corners[2].x.ToString("F0")).Append(',').Append(corners[2].y.ToString("F0")).Append(']');
            }
            var group = t.GetComponent<HorizontalOrVerticalLayoutGroup>();
            if (group != null)
                sb.Append(" layout(").Append(group.GetType().Name).Append(" sp=").Append(group.spacing.ToString("F0"))
                  .Append(" ctrlW=").Append(group.childControlWidth).Append(" pad=").Append(group.padding.left).Append('/').Append(group.padding.right).Append(')');
            var grid = t.GetComponent<GridLayoutGroup>();
            if (grid != null)
                sb.Append(" grid(cell=").Append(grid.cellSize.ToString("F0")).Append(" sp=").Append(grid.spacing.ToString("F0"))
                  .Append(' ').Append(grid.constraint).Append(':').Append(grid.constraintCount).Append(')');
            var element = t.GetComponent<LayoutElement>();
            if (element != null)
                sb.Append(" elem(pref=").Append(element.preferredWidth.ToString("F0")).Append(" min=").Append(element.minWidth.ToString("F0"))
                  .Append(" ignore=").Append(element.ignoreLayout).Append(')');
            var fitter = t.GetComponent<ContentSizeFitter>();
            if (fitter != null) sb.Append(" fitter(").Append(fitter.horizontalFit).Append(')');
            if (depth >= maxDepth) return;
            for (int i = 0; i < t.childCount; i++) Dump(sb, t.GetChild(i), depth + 1, maxDepth);
        }
    }
}
