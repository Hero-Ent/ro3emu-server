using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.UI;

namespace Ro3Emu.ClientHooks
{
    /// <summary>
    /// Developer test driver, off unless the client's environment has RO3EMU_DEVDRIVER=&lt;folder&gt;. Every 10th frame
    /// it runs the lines of &lt;folder&gt;\cmd.txt (then deletes it) and appends results to &lt;folder&gt;\out.txt, so UI
    /// changes can be checked on a client nobody is looking at (automated runs, a second desktop):
    /// shot &lt;name&gt; | login &lt;account&gt; &lt;password&gt; | clickname &lt;name&gt; | clickat &lt;x&gt; &lt;y&gt; | drag &lt;x1&gt; &lt;y1&gt; &lt;x2&gt; &lt;y2&gt; |
    /// mousedrag &lt;x1&gt; &lt;y1&gt; &lt;x2&gt; &lt;y2&gt; (real mouse device) |
    /// key &lt;Key&gt; | dump &lt;name part&gt; [depth] | find &lt;name part&gt; | actions &lt;name part&gt; | texts &lt;name part&gt;.
    /// Positions are screen pixels from the bottom-left (Unity screen space).
    /// </summary>
    public static class DevDriver
    {
        private static readonly string Folder = Environment.GetEnvironmentVariable("RO3EMU_DEVDRIVER");
        private static int _frame;
        private static readonly Queue<Action> Pending = new Queue<Action>();
        private static int _waitFrames;

        public static void Tick()
        {
            if (string.IsNullOrEmpty(Folder)) return;
            try
            {
                if (_frame == 0)
                {
                    Application.runInBackground = true;
                    if (InputSystem.settings != null) InputSystem.settings.backgroundBehavior = InputSettings.BackgroundBehavior.IgnoreFocus;
                    Out("driver on, screen " + Screen.width + "x" + Screen.height);
                }
                _frame++;
                if (_waitFrames > 0) { _waitFrames--; return; }
                if (Pending.Count > 0) { Pending.Dequeue()(); return; }
                if (_frame % 10 != 0) return;
                string cmd = System.IO.Path.Combine(Folder, "cmd.txt");
                if (!File.Exists(cmd)) return;
                var lines = File.ReadAllLines(cmd);
                File.Delete(cmd);
                foreach (var raw in lines)
                {
                    var line = raw.Trim();
                    if (line.Length == 0) continue;
                    try { Run(line); }
                    catch (Exception e) { Out("! " + line + ": " + e.GetType().Name + ": " + e.Message); }
                }
            }
            catch (Exception e) { Debug.LogWarning("[Ro3Emu.DevDriver] " + e); }
        }

        private static void Run(string line)
        {
            var p = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            switch (p[0])
            {
                case "shot":
                    string file = System.IO.Path.Combine(Folder, (p.Length > 1 ? p[1] : "shot") + ".png");
                    ScreenCapture.CaptureScreenshot(file);
                    Out("shot " + file);
                    break;
                case "login": Login(p[1], p[2]); break;
                case "clickname": ClickName(string.Join(" ", p.Skip(1))); break;
                case "clickat": ClickAt(new Vector2(float.Parse(p[1]), float.Parse(p[2]))); break;
                case "drag": Drag(new Vector2(float.Parse(p[1]), float.Parse(p[2])), new Vector2(float.Parse(p[3]), float.Parse(p[4]))); break;
                case "mousedrag": MouseDrag(new Vector2(float.Parse(p[1]), float.Parse(p[2])), new Vector2(float.Parse(p[3]), float.Parse(p[4]))); break;
                case "simdrag": SimDrag(new Vector2(float.Parse(p[1]), float.Parse(p[2])), new Vector2(float.Parse(p[3]), float.Parse(p[4]))); break;
                case "key": PressKey(Enum.Parse<Key>(p[1], true)); break;
                case "dump": DumpByName(p[1], p.Length > 2 ? int.Parse(p[2]) : 3); break;
                case "find": Find(p[1]); break;
                case "actions": Actions(p.Length > 1 ? p[1] : ""); break;
                case "texts": Texts(p[1]); break;
                case "wait": _waitFrames = int.Parse(p[1]); break;
                case "hotbar":
                    if (p[1] == "skill") Out(HotbarExpansion.DevSkillLoad(int.Parse(p[2]), int.Parse(p[3])));
                    else if (p[1] == "item") Out(HotbarExpansion.DevShortcutSave(int.Parse(p[2]), int.Parse(p[3])));
                    else Out(HotbarExpansion.DevState());
                    break;
                default: Out("? " + line); break;
            }
        }

        private static void Out(string text)
        {
            File.AppendAllText(System.IO.Path.Combine(Folder, "out.txt"), DateTime.Now.ToString("HH:mm:ss ") + text + "\n");
        }

        private static void Login(string account, string password)
        {
            var inputs = UnityEngine.Object.FindObjectsOfType<TMPro.TMP_InputField>()
                .Where(f => f.gameObject.activeInHierarchy).OrderByDescending(f => f.transform.position.y).ToList();
            if (inputs.Count < 2) { Out("login: " + inputs.Count + " input field(s)"); return; }
            inputs[0].text = account;
            inputs[1].text = password;
            Out("login: fields set (" + inputs[0].name + ", " + inputs[1].name + ")");
        }

        private static void ClickName(string name)
        {
            var target = Resources.FindObjectsOfTypeAll<Transform>()
                .Where(t => t.gameObject.scene.name != null && t.gameObject.activeInHierarchy && t.name == name).FirstOrDefault();
            if (target == null) { Out("clickname: " + name + " not found"); return; }
            var rt = target as RectTransform;
            var cam = Camera(target);
            Vector2 screen = rt != null ? RectTransformUtility.WorldToScreenPoint(cam, rt.TransformPoint(rt.rect.center)) : (Vector2)target.position;
            Out("clickname " + name + " at " + screen.ToString("F0"));
            ClickAt(screen);
        }

        private static UnityEngine.Camera Camera(Transform t)
        {
            var canvas = t.GetComponentInParent<Canvas>();
            if (canvas == null) return null;
            canvas = canvas.rootCanvas;
            return canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
        }

        private static List<RaycastResult> Hits(Vector2 screen)
        {
            var data = new PointerEventData(EventSystem.current) { position = screen };
            var hits = new List<RaycastResult>();
            EventSystem.current.RaycastAll(data, hits);
            return hits;
        }

        private static void ClickAt(Vector2 screen)
        {
            var hits = Hits(screen);
            if (hits.Count == 0) { Out("clickat " + screen + ": nothing"); return; }
            var go = hits[0].gameObject;
            var data = new PointerEventData(EventSystem.current)
            {
                position = screen, pressPosition = screen, button = PointerEventData.InputButton.Left, clickCount = 1,
                pointerCurrentRaycast = hits[0], pointerPressRaycast = hits[0],
            };
            var down = ExecuteEvents.ExecuteHierarchy(go, data, ExecuteEvents.pointerDownHandler);
            data.pointerPress = down ?? ExecuteEvents.GetEventHandler<IPointerClickHandler>(go);
            data.rawPointerPress = go;
            Pending.Enqueue(() =>
            {
                ExecuteEvents.Execute(data.pointerPress, data, ExecuteEvents.pointerUpHandler);
                var click = ExecuteEvents.GetEventHandler<IPointerClickHandler>(go);
                if (click != null) ExecuteEvents.Execute(click, data, ExecuteEvents.pointerClickHandler);
                Out("clickat " + screen.ToString("F0") + " -> " + Path(go.transform) + " (click " + (click != null ? click.name : "none") + ")");
            });
        }

        private static void Drag(Vector2 from, Vector2 to)
        {
            var hits = Hits(from);
            if (hits.Count == 0) { Out("drag: nothing at " + from); return; }
            var go = hits[0].gameObject;
            var data = new PointerEventData(EventSystem.current)
            {
                position = from, pressPosition = from, button = PointerEventData.InputButton.Left,
                pointerCurrentRaycast = hits[0], pointerPressRaycast = hits[0],
            };
            data.pointerPress = ExecuteEvents.ExecuteHierarchy(go, data, ExecuteEvents.pointerDownHandler);
            var drag = ExecuteEvents.GetEventHandler<IDragHandler>(go);
            data.pointerDrag = drag;
            const int steps = 10;
            Pending.Enqueue(() =>
            {
                data.dragging = true;
                if (drag != null) ExecuteEvents.Execute(drag, data, ExecuteEvents.beginDragHandler);
            });
            for (int i = 1; i <= steps; i++)
            {
                var pos = Vector2.Lerp(from, to, i / (float)steps);
                Pending.Enqueue(() =>
                {
                    data.delta = pos - data.position;
                    data.position = pos;
                    var h = Hits(pos);
                    data.pointerCurrentRaycast = h.Count > 0 ? h[0] : default;
                    if (drag != null) ExecuteEvents.Execute(drag, data, ExecuteEvents.dragHandler);
                });
            }
            Pending.Enqueue(() =>
            {
                var h = Hits(to);
                string target = "nothing";
                if (h.Count > 0)
                {
                    data.pointerCurrentRaycast = h[0];
                    var dropped = ExecuteEvents.ExecuteHierarchy(h[0].gameObject, data, ExecuteEvents.dropHandler);
                    target = Path(h[0].gameObject.transform) + " (drop " + (dropped != null ? dropped.name : "none") + ")";
                }
                if (data.pointerPress != null) ExecuteEvents.Execute(data.pointerPress, data, ExecuteEvents.pointerUpHandler);
                if (drag != null) ExecuteEvents.Execute(drag, data, ExecuteEvents.endDragHandler);
                Out("drag " + Path(go.transform) + " -> " + target);
            });
        }

        private static readonly MethodInfo BeginDragMethod = typeof(UIDragButton).GetMethod("BeginDrag", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        /// <summary>
        /// The game's own drag and drop (UIDragButton.BeginDrag, PressDragMovement's trigger search and OnEndDrag)
        /// without the physical mouse button PressDragMovement polls: the drop lands where a player's would.
        /// </summary>
        private static void SimDrag(Vector2 from, Vector2 to)
        {
            var hits = Hits(from);
            UIDragButton source = null;
            foreach (var h in hits) if ((source = h.gameObject.GetComponentInParent<UIDragButton>()) != null) break;
            if (source == null || BeginDragMethod == null) { Out("simdrag: no drag button at " + from); return; }
            var ped = new PointerEventData(EventSystem.current) { position = from, pressPosition = from, button = PointerEventData.InputButton.Left };
            BeginDragMethod.Invoke(source, new object[] { ped });
            var move = UnityEngine.Object.FindObjectsOfType<PressDragMovement>().FirstOrDefault();
            if (move == null) { Out("simdrag: drag did not start on " + Path(source.transform)); return; }
            move.enabled = false; // its LateUpdate would end the drag: no physical button is down
            UIDragTrigger trigger = null;
            Pending.Enqueue(() =>
            {
                foreach (var h in Hits(to))
                {
                    if (h.gameObject.transform.IsChildOf(move.transform)) continue;
                    trigger = h.gameObject.GetComponentInParent<UIDragTrigger>();
                    break;
                }
                var enter = new PointerEventData(EventSystem.current) { position = to };
                if (trigger != null) { trigger.OnPointerEnter(enter); move.preTrigger = trigger; }
            });
            Pending.Enqueue(() =>
            {
                var end = new PointerEventData(EventSystem.current) { position = to };
                move.OnEndDrag(end);
                Out("simdrag " + Path(source.transform) + " -> " + (trigger != null ? Path(trigger.transform) : "no trigger"));
            });
        }

        private static void PressKey(Key key)
        {
            var keyboard = Keyboard.current;
            if (keyboard == null) { Out("key: no keyboard"); return; }
            InputSystem.QueueStateEvent(keyboard, new KeyboardState(key));
            Pending.Enqueue(() => { });
            Pending.Enqueue(() => { });
            Pending.Enqueue(() => { InputSystem.QueueStateEvent(keyboard, new KeyboardState()); Out("key " + key); });
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        /// <summary>
        /// A drag with the real mouse device (Input System events) and the window's mouse messages (the legacy Input
        /// that PressDragMovement reads), so the game's UI module sees a real drag.
        /// </summary>
        private static void MouseDrag(Vector2 from, Vector2 to)
        {
            var mouse = Mouse.current;
            if (mouse == null) { Out("mousedrag: no mouse"); return; }
            var hwnd = System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle;
            void State(Vector2 pos, bool down, uint message)
            {
                var s = new MouseState { position = pos };
                if (down) s = s.WithButton(MouseButton.Left, true);
                InputSystem.QueueStateEvent(mouse, s);
                if (hwnd != IntPtr.Zero && message != 0)
                {
                    int x = Mathf.RoundToInt(pos.x), y = Screen.height - Mathf.RoundToInt(pos.y);
                    PostMessage(hwnd, message, (IntPtr)(down ? 1 : 0), (IntPtr)((y << 16) | (x & 0xFFFF)));
                }
            }
            const uint Move = 0x200, Down = 0x201, Up = 0x202;
            State(from, false, Move);
            for (int i = 0; i < 3; i++) Pending.Enqueue(() => State(from, false, Move));
            Pending.Enqueue(() => State(from, true, Down));
            for (int i = 0; i < 5; i++) Pending.Enqueue(() => State(from, true, Move));
            const int steps = 20;
            for (int i = 1; i <= steps; i++)
            {
                var pos = Vector2.Lerp(from, to, i / (float)steps);
                Pending.Enqueue(() => State(pos, true, Move));
                Pending.Enqueue(() => State(pos, true, 0));
            }
            for (int i = 0; i < 6; i++) Pending.Enqueue(() => State(to, true, Move));
            Pending.Enqueue(() =>
            {
                State(to, false, Up);
                var h = Hits(to);
                Out("mousedrag " + from.ToString("F0") + " -> " + to.ToString("F0") + " over " + (h.Count > 0 ? Path(h[0].gameObject.transform) : "nothing")
                    + " (window " + (hwnd != IntPtr.Zero) + ")");
            });
        }

        private static void DumpByName(string part, int depth)
        {
            int n = 0;
            foreach (var t in Resources.FindObjectsOfTypeAll<Transform>())
            {
                if (t.gameObject.scene.name == null || !t.gameObject.activeInHierarchy) continue;
                if (t.name.IndexOf(part, StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (++n > 6) break;
                var sb = new StringBuilder("dump " + Path(t) + ":");
                HotbarExpansion.Dump(sb, t, 0, depth);
                Out(sb.ToString());
            }
            if (n == 0) Out("dump " + part + ": none");
        }

        private static void Find(string part)
        {
            var sb = new StringBuilder("find " + part + ":");
            int n = 0;
            foreach (var t in Resources.FindObjectsOfTypeAll<Transform>())
            {
                if (t.gameObject.scene.name == null || !t.gameObject.activeInHierarchy) continue;
                if (t.name.IndexOf(part, StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (++n > 80) break;
                var rt = t as RectTransform;
                string pos = "";
                if (rt != null)
                {
                    var s = RectTransformUtility.WorldToScreenPoint(Camera(t), rt.TransformPoint(rt.rect.center));
                    pos = " screen=" + s.ToString("F0") + " size=" + rt.rect.size.ToString("F0");
                }
                sb.Append("\n  ").Append(Path(t)).Append(pos);
            }
            Out(sb.ToString());
        }

        private static void Texts(string part)
        {
            var sb = new StringBuilder("texts " + part + ":");
            foreach (var t in Resources.FindObjectsOfTypeAll<Transform>())
            {
                if (t.gameObject.scene.name == null || !t.gameObject.activeInHierarchy) continue;
                if (t.name.IndexOf(part, StringComparison.OrdinalIgnoreCase) < 0) continue;
                foreach (var text in t.GetComponentsInChildren<TMPro.TMP_Text>())
                    if (!string.IsNullOrEmpty(text.text)) sb.Append("\n  ").Append(Path(text.transform)).Append(" = ").Append(text.text);
                break;
            }
            Out(sb.ToString());
        }

        private static void Actions(string part)
        {
            var manager = HappyEngine.Base.SingletonMonoBehaviour<InputSystemManager>.Instance;
            var sb = new StringBuilder("actions " + part + ":");
            foreach (var field in typeof(InputSystemManager).GetFields(BindingFlags.Instance | BindingFlags.NonPublic))
            {
                if (field.FieldType != typeof(InputActionMap) || !(field.GetValue(manager) is InputActionMap map) || map.name != "RO3Current") continue;
                foreach (var action in map.actions)
                    if (action.name.Contains(part, StringComparison.OrdinalIgnoreCase))
                        sb.Append("\n  ").Append(action.name).Append(" = ").Append(string.Join(" | ", action.bindings.Select(b => b.effectivePath)))
                          .Append(" (\"").Append(manager.Obf_ln(action.name)).Append("\")");
            }
            Out(sb.ToString());
        }

        private static string Path(Transform t)
        {
            var sb = new StringBuilder(t.name);
            for (var q = t.parent; q != null; q = q.parent) sb.Insert(0, q.name + "/");
            return sb.ToString();
        }
    }
}
