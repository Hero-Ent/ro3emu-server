using System;
using TMPro;
using UnityEngine;

namespace Ro3Emu.ClientHooks
{
    /// <summary>
    /// Fixes in the NPC dialogue UI, installed once per LuaEnv from LuaPatches.Tick.
    /// <list type="bullet">
    /// <item>Speaker names. UI_Sys_NpcDialogPanelCtrl.RefreshPanel names the speaker of each line from NPCConfig
    /// [m_kCurrentDialogue._iTalkNPCID]._kName (m_kTTXT_NpcName); nothing comes from the server. A few DialogueConfig
    /// rows (Config/DataConfig/DialogueConfig) carry the wrong _iTalkNPCID, so the previous speaker's name stays on the
    /// line (#bug-reports). Before the original RefreshPanel runs, such a row gets the right speaker with rawset (the
    /// field is the row's own, so the config's newIndexFunc is not involved). Rows are matched by _iID, _iSortID, the
    /// speaker they carry and their text id, so a client whose data differs is left alone. Fixed from RefreshPanel, not
    /// once at install, because Lua_DBManager:ReLoadAllCfg replaces the config tables (PassChallenges).</item>
    /// <item>Hero Saga: the interaction key ("F", enum_InputKey.Interactive_1) on the NPC option list
    /// (UI_Sys_NpcDialogListWidgetCtrl.OnOpen sets View.m_kTTXT_KeyPosition) is drawn larger. The label's transform is
    /// scaled, not its font size, so TextFit (which shrinks overflowing labels) leaves it alone.</item>
    /// </list>
    /// </summary>
    public static class NpcDialogFixes
    {
        /// <summary>One wrong speaker: DialogueConfig[Dialogue][SortId] carries Wrong for text Text; Right is meant.</summary>
        private readonly struct SpeakerFix
        {
            public readonly long Dialogue, SortId, Wrong, Right, Text;
            public SpeakerFix(long dialogue, long sortId, long wrong, long right, long text)
            { Dialogue = dialogue; SortId = sortId; Wrong = wrong; Right = right; Text = text; }
        }

        private static readonly SpeakerFix[] SpeakerFixes =
        {
            // "Anomalous Arrival" (task 10108020, leads to "Anomaly in the Pyramid"): line 11, "Why are you looking at me?
            // Didn't the queen order you to give me your full cooperation?", shows Professor Ryan's portrait
            // (npcdialog_rimg_professor_idle) but names the Church Knight (400247): Professor Ryan is 400010.
            new SpeakerFix(1010802001, 11, 400247, 400010, 10370001628),
            // "Oak Inn" (task 10108240): line 2, "A room! Put it on that stinky old Professor's tab! He sent us.", is
            // Bobo's but names the Inn Attendant (100034). 400025 is the speaker the config's own default row gives
            // Bobo's lines (NPCConfig 400025: Bobo, _kDialoguePhoto npcdialog_rimg_bobo_idle).
            new SpeakerFix(1010824001, 2, 100034, 400025, 10370001681),
        };

        /// <summary>How much larger the interaction key label is drawn.</summary>
        internal const float KeyLabelScale = 1.4f;

        private const string Marker = "__ro3emu_npcdialog_fixes";

        /// <summary>self:Method(...)</summary>
        public delegate void VarMethod(Obf_R.Obf_ff self, params object[] rest);

        private static Obf_R.Obf_Ef _refreshPanel, _listOnOpen, _rawset;
        // Kept alive here: Lua holds the pushed delegates only weakly through xLua's object pool.
        private static readonly LuaPatches.SelfMethod RefreshPanelWrapper = RefreshPanel;
        private static readonly VarMethod ListOnOpenWrapper = ListOnOpen;

        /// <summary>Once per LuaEnv, from LuaPatches.Tick.</summary>
        public static void Install(Obf_R.Obf_ff global, Func<string, Obf_R.Obf_ff> module)
        {
            _rawset = global?.Obf_RuA<Obf_R.Obf_Ef>("rawset");
            var panel = module("ui_sys_npcdialogpanelctrl");
            if (panel != null && panel.Obf_RuA<object>(Marker) == null)
            {
                _refreshPanel = panel.Obf_RuA<Obf_R.Obf_Ef>("RefreshPanel");
                if (_refreshPanel != null) panel.Obf_luA<string, LuaPatches.SelfMethod>("RefreshPanel", RefreshPanelWrapper);
                panel.Obf_luA<string, object>(Marker, true);
            }
            var list = module("ui_sys_npcdialoglistwidgetctrl");
            if (list != null && list.Obf_RuA<object>(Marker) == null)
            {
                _listOnOpen = list.Obf_RuA<Obf_R.Obf_Ef>("OnOpen");
                if (_listOnOpen != null) list.Obf_luA<string, VarMethod>("OnOpen", ListOnOpenWrapper);
                list.Obf_luA<string, object>(Marker, true);
            }
            Debug.Log("[Ro3Emu.NpcDialog] speaker fixes " + (_refreshPanel != null ? "on" : "NOT on (RefreshPanel not found)")
                + "; interaction key x" + KeyLabelScale + (_listOnOpen != null ? "" : " NOT on (OnOpen not found)"));
        }

        private static void RefreshPanel(Obf_R.Obf_ff self)
        {
            try { FixSpeaker(self?.Obf_RuA<Obf_R.Obf_ff>("m_kCurrentDialogue")); }
            catch (Exception e) { Debug.LogWarning("[Ro3Emu.NpcDialog] speaker fix: " + e.GetType().Name + ": " + e.Message); }
            _refreshPanel?.Obf_HuA(self);
        }

        private static void FixSpeaker(Obf_R.Obf_ff row)
        {
            if (row == null || _rawset == null) return;
            long id = LuaPatches.ToLong(row.Obf_RuA<object>("_iID"));
            foreach (var fix in SpeakerFixes)
            {
                if (fix.Dialogue != id) continue;
                if (LuaPatches.ToLong(row.Obf_RuA<object>("_iSortID")) != fix.SortId) continue;
                if (LuaPatches.ToLong(row.Obf_RuA<object>("_iTalkNPCID")) != fix.Wrong) continue;
                var text = row.Obf_RuA<Obf_R.Obf_ff>("_iText");
                if (text == null || LuaPatches.ToLong(text.Obf_ruA<long, object>(1)) != fix.Text) continue;
                _rawset.Obf_HuA(row, "_iTalkNPCID", fix.Right);
                Debug.Log("[Ro3Emu.NpcDialog] dialogue " + fix.Dialogue + " line " + fix.SortId + ": speaker " + fix.Wrong + " -> " + fix.Right);
            }
        }

        private static void ListOnOpen(Obf_R.Obf_ff self, params object[] rest)
        {
            var args = new object[(rest?.Length ?? 0) + 1];
            args[0] = self;
            if (rest != null) Array.Copy(rest, 0, args, 1, rest.Length);
            _listOnOpen?.Obf_HuA(args);
            try { EnlargeKeyLabel(self); }
            catch (Exception e) { Debug.LogWarning("[Ro3Emu.NpcDialog] interaction key: " + e.GetType().Name + ": " + e.Message); }
        }

        /// <summary>View.m_kTTXT_KeyPosition is a Lua_TMPTextNew wrapper; its C# label is the wrapper's component.</summary>
        private static void EnlargeKeyLabel(Obf_R.Obf_ff self)
        {
            if (!(self?.Obf_LuA<object>("View.m_kTTXT_KeyPosition.component") is TMP_Text label) || label == null) return;
            var t = label.rectTransform;
            if (Mathf.Abs(t.localScale.x - 1f) > 0.01f) return; // scaled already (widgets are pooled) or by the prefab
            t.localScale = new Vector3(KeyLabelScale, KeyLabelScale, t.localScale.z);
        }
    }
}
