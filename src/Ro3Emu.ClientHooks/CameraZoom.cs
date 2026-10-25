using System;
using UnityEngine;

namespace Ro3Emu.ClientHooks
{
    /// <summary>
    /// Hero Saga: the camera zooms out 30 % farther than the client's own limit, and the zoom the player chose is kept from
    /// map to map and from one session to the next.
    /// <para>
    /// The player's camera is RD_CameraController_LockTr (module "rd_cameracontroller_locktr"). Its distance is
    /// m_kParam._fDistance (the camera config row's) plus m_fNowScale, the player's own offset: the mouse wheel calls
    /// Scale(delta) -> ChangeCameraDistance(delta), which moves m_fNowScale by delta * _fZoomSpeed, clamps it to
    /// [-_fZoomInLimit, _fZoomOutLimit] (offsets from _fDistance, not distances) and to [_fZoomRangeL, _fZoomRangeR], and
    /// sets m_fNowDistance; ScaleDone eases it on with _fInertia. A script's own change passes a table as the second
    /// argument (iCameraType, iSourceId, bAdd). Every camera change (ResetStatus, on a map change or a new camera config)
    /// and every new possession (PossessBegin) sets m_fNowScale back to 0: the zoom the player had chosen was lost.
    /// </para>
    /// <para>
    /// The limits: a camera config row (Config/DataConfig/Camera, module "camera": its m_kValues by _iId) keeps only the
    /// values that differ from the module's defaults (its metatable's __index) and refuses new keys
    /// (ConfigDefine.newIndexFunc only logs "Config can't add new key-value pair"), so _fZoomOutLimit is written with Lua's
    /// rawset, on the rows that let the player zoom out at all: the farthest distance (_fDistance + _fZoomOutLimit) becomes
    /// 30 % greater (the default camera: 55 + 65 = 120, now 156). A controller copies its row into m_kParam whenever a camera
    /// config is applied, so the raised limit holds from the next camera change on.
    /// </para>
    /// <para>
    /// The kept zoom: the methods are replaced through the class table (BaseClass's __newindex writes the shared vtbl, as
    /// in LuaPatches.PatchCountdowns) by C# wrappers that call the original. After the player's ChangeCameraDistance or
    /// ScaleDone, m_fNowScale is remembered (PlayerPrefs "Ro3Emu.CameraZoomScale", so it survives a restart); after
    /// ResetStatus or PossessBegin it is put back and applied with ChangeCameraDistance(0), which clamps it to the new
    /// camera's limits.
    /// </para>
    /// </summary>
    public static class CameraZoom
    {
        /// <summary>How much farther the camera may go: the user asked for "at least 30 % more".</summary>
        internal const double ZoomOutFactor = 1.3;
        private const string PrefKey = "Ro3Emu.CameraZoomScale";
        private const string Marker = "__ro3emu_kept_zoom";

        /// <summary>self:Method(a)</summary>
        public delegate void Method1(Obf_R.Obf_ff self, object a);
        /// <summary>self:Method(a, b)</summary>
        public delegate void Method2(Obf_R.Obf_ff self, object a, object b);
        /// <summary>self:Method(a, b, c)</summary>
        public delegate void Method3(Obf_R.Obf_ff self, object a, object b, object c);

        private static Obf_R.Obf_Ef _change, _scaleDone, _resetStatus, _possessBegin;
        // Kept alive here: Lua holds the pushed delegates only weakly through xLua's object pool.
        private static readonly Method2 ChangeWrapper = ChangeCameraDistance;
        private static readonly Method1 ScaleDoneWrapper = ScaleDone, PossessWrapper = PossessBegin;
        private static readonly Method3 ResetWrapper = ResetStatus;
        private static bool _loaded, _has;
        private static double _scale;

        /// <summary>Once per LuaEnv, from LuaPatches.Tick.</summary>
        public static void Install(Obf_R.Obf_ff global, Func<string, Obf_R.Obf_ff> module)
        {
            RaiseLimits(global, module("camera"));
            KeepZoom(module("rd_cameracontroller_locktr"));
        }

        private static void RaiseLimits(Obf_R.Obf_ff global, Obf_R.Obf_ff camera)
        {
            var values = camera?.Obf_RuA<Obf_R.Obf_ff>("m_kValues");
            var next = global?.Obf_RuA<Obf_R.Obf_Ef>("next");
            var rawset = global?.Obf_RuA<Obf_R.Obf_Ef>("rawset");
            if (values == null || next == null || rawset == null)
            {
                Debug.LogWarning("[Ro3Emu.CameraZoom] camera config not found (module, m_kValues, next or rawset missing)");
                return;
            }
            int changed = 0;
            object key = null;
            for (int guard = 0; guard < 10000; guard++)
            {
                var step = next.Obf_HuA(values, key);
                if (step == null || step.Length < 2 || step[0] == null) break;
                key = step[0];
                if (!(step[1] is Obf_R.Obf_ff row)) continue;
                // The limit is an offset from _fDistance: the farthest distance goes up by the factor.
                double distance = Number(row.Obf_RuA<object>("_fDistance")), zoomOut = Number(row.Obf_RuA<object>("_fZoomOutLimit"));
                if (double.IsNaN(distance) || double.IsNaN(zoomOut) || zoomOut <= 0) continue;
                rawset.Obf_HuA(row, "_fZoomOutLimit", (distance + zoomOut) * ZoomOutFactor - distance);
                changed++;
            }
            Debug.Log("[Ro3Emu.CameraZoom] farthest zoom x" + ZoomOutFactor + " on " + changed + " camera config(s)");
        }

        private static void KeepZoom(Obf_R.Obf_ff controller)
        {
            if (controller == null) { Debug.LogWarning("[Ro3Emu.CameraZoom] rd_cameracontroller_locktr not found: zoom not kept"); return; }
            if (!_loaded)
            {
                _loaded = true;
                _has = PlayerPrefs.HasKey(PrefKey);
                if (_has) _scale = PlayerPrefs.GetFloat(PrefKey);
            }
            if (controller.Obf_RuA<object>(Marker) != null) return; // this LuaEnv's class is wrapped already
            _change = controller.Obf_RuA<Obf_R.Obf_Ef>("ChangeCameraDistance");
            _scaleDone = controller.Obf_RuA<Obf_R.Obf_Ef>("ScaleDone");
            _resetStatus = controller.Obf_RuA<Obf_R.Obf_Ef>("ResetStatus");
            _possessBegin = controller.Obf_RuA<Obf_R.Obf_Ef>("PossessBegin");
            if (_change == null || _resetStatus == null)
            {
                Debug.LogWarning("[Ro3Emu.CameraZoom] RD_CameraController_LockTr methods not found: zoom not kept");
                return;
            }
            controller.Obf_luA<string, Method2>("ChangeCameraDistance", ChangeWrapper);
            if (_scaleDone != null) controller.Obf_luA<string, Method1>("ScaleDone", ScaleDoneWrapper);
            controller.Obf_luA<string, Method3>("ResetStatus", ResetWrapper);
            if (_possessBegin != null) controller.Obf_luA<string, Method1>("PossessBegin", PossessWrapper);
            controller.Obf_luA<string, object>(Marker, true);
            Debug.Log("[Ro3Emu.CameraZoom] the player's zoom is kept" + (_has ? " (saved offset " + _scale.ToString("0.##") + ")" : ""));
        }

        private static void ChangeCameraDistance(Obf_R.Obf_ff self, object delta, object param)
        {
            _change?.Obf_HuA(self, delta, param);
            if (param == null) Remember(self); // the player's wheel; a script passes its own parameters
        }

        private static void ScaleDone(Obf_R.Obf_ff self, object delta)
        {
            _scaleDone?.Obf_HuA(self, delta);
            Remember(self);
        }

        private static void ResetStatus(Obf_R.Obf_ff self, object a, object b, object c)
        {
            _resetStatus?.Obf_HuA(self, a, b, c);
            Restore(self);
        }

        private static void PossessBegin(Obf_R.Obf_ff self, object unit)
        {
            _possessBegin?.Obf_HuA(self, unit);
            Restore(self);
        }

        private static void Remember(Obf_R.Obf_ff self)
        {
            try
            {
                double scale = Number(self?.Obf_RuA<object>("m_fNowScale"));
                if (double.IsNaN(scale) || (_has && Math.Abs(scale - _scale) < 0.01)) return;
                _scale = scale;
                _has = true;
                PlayerPrefs.SetFloat(PrefKey, (float)scale);
            }
            catch (Exception e) { Debug.LogWarning("[Ro3Emu.CameraZoom] remember: " + e.Message); }
        }

        /// <summary>Puts the kept offset back; ChangeCameraDistance(0) clamps it to this camera's limits and applies it.</summary>
        private static void Restore(Obf_R.Obf_ff self)
        {
            if (!_has || self == null || _change == null) return;
            try
            {
                self.Obf_luA<string, object>("m_fNowScale", _scale);
                _change.Obf_HuA(self, 0.0, null);
            }
            catch (Exception e) { Debug.LogWarning("[Ro3Emu.CameraZoom] restore: " + e.Message); }
        }

        private static double Number(object value)
        {
            switch (value)
            {
                case double d: return d;
                case float f: return f;
                case long l: return l;
                case int i: return i;
                default: return double.NaN;
            }
        }
    }
}
