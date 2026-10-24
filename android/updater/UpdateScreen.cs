using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Ro3Android.Updater
{
    /// <summary>
    /// The first screen of the app (Ro3Android.GameInit adds it): brings the game data in the app's folder to the
    /// channel's release, then calls <see cref="OnReady"/>, which starts the game. Configured by Resources/Ro3Patch.txt
    /// (setup-project.ps1 -PatchRepo); files named patch-repo / patch-channel in the app's data folder override it for
    /// tests. Without a repository the screen steps aside (data pushed with push-data.ps1).
    /// </summary>
    public sealed class UpdateScreen : MonoBehaviour
    {
        public Action OnReady;

        enum View { Checking, Confirm, Working, ApkRequired, Error, Done }

        volatile View _view = View.Checking;
        volatile string _message = "Procurando atualizações…", _error = "";
        string _repoUrl, _channelName, _publicKey, _root, _appVersion;
        bool _offlinePlayable, _optionalApk, _wifi;
        Installer _installer;
        Repository _repo;
        Plan _plan;
        Channel _channel;
        TaskCompletionSource<bool> _confirm;
        readonly CancellationTokenSource _cts = new CancellationTokenSource();
        long _free;
        bool _verifyAll;

        // Speed (an average over the last seconds) and the notification, kept by NotifyLoop's thread.
        volatile float _speed;
        volatile bool _quit;
        Thread _notifier;
        bool _android, _askedNotifications;

        Texture2D _bg, _barBack, _barFill, _button;
        GUIStyle _title, _text, _small, _buttonStyle;

        void Start()
        {
            _root = Application.persistentDataPath;
            _appVersion = Application.version;
            var config = Resources.Load<TextAsset>("Ro3Patch");
            if (config != null)
            {
                foreach (var line in config.text.Split('\n'))
                {
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string key = line.Substring(0, eq).Trim(), value = line.Substring(eq + 1).Trim();
                    if (key == "repo") _repoUrl = value;
                    else if (key == "channel") _channelName = value;
                    else if (key == "key") _publicKey = value;
                }
            }
            _repoUrl = Override("patch-repo") ?? _repoUrl;
            _channelName = Override("patch-channel") ?? _channelName ?? "android-live";
            if (string.IsNullOrEmpty(_repoUrl))
            {
                Debug.Log("[Ro3Android] updater: no patch repository configured, using the data already on the phone");
                Finish();
                return;
            }
            Debug.Log($"[Ro3Android] updater: {_repoUrl} channel {_channelName}, app {_appVersion}");
            Screen.sleepTimeout = SleepTimeout.NeverSleep;
            _repo = new Repository(new Uri(_repoUrl));
            _installer = new Installer(_root, _repo) { Log = m => Debug.Log("[Ro3Android] updater: " + m) };
            _verifyAll = File.Exists(Path.Combine(_root, "patch-verify"));
            if (_verifyAll) File.Delete(Path.Combine(_root, "patch-verify"));
            _android = Application.platform == RuntimePlatform.Android;
            Android.Enabled = _android;
            _notifier = new Thread(NotifyLoop) { IsBackground = true, Name = "ro3-update-notify" };
            _notifier.Start();
            Begin();
        }

        string Override(string name)
        {
            try
            {
                string path = Path.Combine(_root, name);
                return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
            }
            catch (IOException) { return null; }
        }

        void Begin()
        {
            _view = View.Checking;
            _message = "Procurando atualizações…";
            _offlinePlayable = _installer.IsComplete() && _installer.QuickVerify().Count == 0;
            _wifi = Application.internetReachability == NetworkReachability.ReachableViaLocalAreaNetwork;
            _free = Android.FreeSpace(_root);
            _installer.FreeSpace = _ => _free;
            bool verifyAll = _verifyAll;
            _verifyAll = false;
            Task.Run(() => RunAsync(verifyAll));
        }

        async Task RunAsync(bool verifyAll)
        {
            try
            {
                try
                {
                    _channel = await _repo.GetChannelAsync(_channelName, _cts.Token);
                }
                catch (Exception e) when (!(e is UpdateException))
                {
                    Debug.LogWarning("[Ro3Android] updater: channel: " + e);
                    Fail("Não foi possível falar com o servidor de atualização. Verifique sua conexão.");
                    return;
                }
                if (!string.IsNullOrEmpty(_publicKey) && !_channel.VerifySignature(_publicKey))
                {
                    _offlinePlayable = false;
                    Fail("A atualização recebida não tem uma assinatura válida e foi recusada.");
                    return;
                }
                if (_channel.Apk != null && Older(_appVersion, _channel.Apk.MinVersion))
                {
                    _message = $"Esta versão do app ({_appVersion}) é antiga demais. Instale a versão {_channel.Apk.Version} para continuar.";
                    _view = View.ApkRequired;
                    return;
                }
                _optionalApk = _channel.Apk != null && Older(_appVersion, _channel.Apk.Version);

                if (!verifyAll && _installer.IsComplete(_channel) && _installer.QuickVerify().Count == 0)
                {
                    _view = View.Done;
                    return;
                }
                _message = "Conferindo os arquivos do jogo…";
                _plan = await _installer.PrepareAsync(_channel, verifyAll, _cts.Token);
                // Ask before big downloads, and before any on mobile data.
                if (_plan.DownloadBytes > 0 && (!_wifi || _plan.DownloadBytes > (500L << 20)))
                {
                    _confirm = new TaskCompletionSource<bool>();
                    _view = View.Confirm;
                    if (!await _confirm.Task) return;
                }
                _view = View.Working;
                await _installer.ApplyAsync(_plan, _cts.Token);
                _view = View.Done;
            }
            catch (OperationCanceledException) { }
            catch (UpdateException e) { Fail(e.Message); }
            catch (Exception e)
            {
                Debug.LogError("[Ro3Android] updater: " + e);
                Fail("A atualização falhou: " + e.Message);
            }
        }

        void Fail(string message)
        {
            _error = message;
            _view = View.Error;
        }

        static bool Older(string current, string required)
        {
            if (string.IsNullOrEmpty(required)) return false;
            return Version.TryParse(current, out var a) && Version.TryParse(required, out var b) && a < b;
        }

        void Update()
        {
            if (_installer == null) return;
            // Android 13+ asks once; without the permission the service still runs, only its notification is hidden.
            if (_view == View.Working && !_askedNotifications) { _askedNotifications = true; Android.AskNotificationPermission(); }
            if (_view == View.Done) Finish();
        }

        /// <summary>
        /// The notification and the speed, on a thread of their own: Unity stops Update while the app is in the
        /// background, which is where a 20 GB download spends most of its time. Also posts the "done" notice when the
        /// download ends out of sight.
        /// </summary>
        void NotifyLoop()
        {
            if (_android) AndroidJNI.AttachCurrentThread();
            bool on = false;
            try
            {
                var clock = System.Diagnostics.Stopwatch.StartNew();
                double last = 0;
                long lastDone = 0, lastNotify = -10000;
                while (!_quit)
                {
                    Thread.Sleep(500);
                    var status = _installer.Status;
                    double now = clock.Elapsed.TotalSeconds;
                    if (now - last >= 1)
                    {
                        long done = Interlocked.Read(ref status.Done);
                        float instant = (float)((done - lastDone) / (now - last));
                        _speed = _speed <= 0 ? instant : _speed * 0.7f + instant * 0.3f;
                        lastDone = done;
                        last = now;
                    }
                    bool working = _view == View.Working || status.Stage == Stage.Scanning;
                    if (working && !on) { on = true; Android.StartService("Hero Saga", "Baixando o jogo…"); }
                    if (on && working && clock.ElapsedMilliseconds - lastNotify >= 2000)
                    {
                        lastNotify = clock.ElapsedMilliseconds;
                        Android.UpdateService(Headline(), Percent());
                    }
                    if (on && !working)
                    {
                        on = false;
                        if (_view == View.Done) Android.FinishService("Hero Saga", "Download concluído. Toque para jogar.");
                        else if (_view == View.Error) Android.FinishService("Hero Saga", "O download parou. Abra o jogo para continuar.");
                        else Android.StopService();
                    }
                }
            }
            catch (Exception e) { Debug.LogWarning("[Ro3Android] updater: notifications: " + e.Message); }
            finally
            {
                if (on) Android.StopService();
                if (_android) AndroidJNI.DetachCurrentThread();
            }
        }

        int Percent()
        {
            var s = _installer.Status;
            long total = Interlocked.Read(ref s.Total);
            return total <= 0 ? -1 : (int)Math.Min(100, Interlocked.Read(ref s.Done) * 100 / total);
        }

        string Headline()
        {
            var s = _installer.Status;
            long done = Interlocked.Read(ref s.Done), total = Interlocked.Read(ref s.Total);
            if (s.Stage == Stage.Scanning) return $"Conferindo arquivos: {Installer.Format(done)} de {Installer.Format(total)}";
            string eta = _speed > 1024 && total > done ? " · faltam " + Duration((total - done) / _speed) : "";
            return $"{Installer.Format(Math.Min(done, total))} de {Installer.Format(total)} · {Installer.Format((long)_speed)}/s{eta}";
        }

        static string Duration(double seconds)
        {
            if (seconds < 60) return "menos de 1 min";
            if (seconds < 3600) return $"{(int)(seconds / 60)} min";
            return $"{(int)(seconds / 3600)} h {(int)(seconds % 3600 / 60)} min";
        }

        void Finish()
        {
            if (!enabled) return;
            enabled = false;
            StopNotifier();
            // The player is here: the "done" notice has served its purpose.
            Android.ClearDone();
            Screen.sleepTimeout = SleepTimeout.SystemSetting;
            _repo?.Dispose();
            Debug.Log("[Ro3Android] updater: data ready, starting the game");
            var ready = OnReady;
            Destroy(gameObject);
            ready?.Invoke();
        }

        void StopNotifier()
        {
            _quit = true;
            _notifier?.Join(2000);
            _notifier = null;
        }

        void OnApplicationQuit()
        {
            _cts.Cancel();
            StopNotifier();
        }

        void OnDestroy() => StopNotifier();

        // ---- drawing (IMGUI: nothing of the game's UI exists before its data does) ----

        static Texture2D Solid(Color c)
        {
            var t = new Texture2D(1, 1);
            t.SetPixel(0, 0, c);
            t.Apply();
            return t;
        }

        void Styles(float scale)
        {
            if (_bg != null) return;
            _bg = Solid(new Color32(16, 20, 30, 255));
            _barBack = Solid(new Color32(44, 52, 70, 255));
            _barFill = Solid(new Color32(236, 170, 60, 255));
            _button = Solid(new Color32(236, 170, 60, 255));
            _title = new GUIStyle(GUI.skin.label) { fontSize = (int)(42 * scale), fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
            _title.normal.textColor = new Color32(246, 214, 150, 255);
            _text = new GUIStyle(GUI.skin.label) { fontSize = (int)(22 * scale), alignment = TextAnchor.MiddleCenter, wordWrap = true };
            _text.normal.textColor = Color.white;
            _small = new GUIStyle(_text) { fontSize = (int)(16 * scale) };
            _small.normal.textColor = new Color32(160, 170, 190, 255);
            _buttonStyle = new GUIStyle(GUI.skin.button) { fontSize = (int)(22 * scale), fontStyle = FontStyle.Bold };
            _buttonStyle.normal.background = _button;
            _buttonStyle.active.background = _barFill;
            _buttonStyle.hover.background = _button;
            _buttonStyle.normal.textColor = _buttonStyle.hover.textColor = _buttonStyle.active.textColor = new Color32(30, 24, 12, 255);
        }

        void OnGUI()
        {
            if (_installer == null) return;
            float scale = Mathf.Max(0.6f, Mathf.Min(Screen.width / 1280f, Screen.height / 720f));
            Styles(scale);
            GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), _bg);
            float w = Mathf.Min(Screen.width - 64 * scale, 900 * scale), x = (Screen.width - w) / 2, y = Screen.height * 0.22f;
            GUI.Label(new Rect(x, y, w, 60 * scale), "Hero Saga", _title);
            y += 90 * scale;

            switch (_view)
            {
                case View.Checking:
                    Line(ref y, x, w, scale, _message, _text);
                    if (_installer.Status.Stage == Stage.Scanning) Progress(ref y, x, w, scale);
                    break;
                case View.Confirm:
                    Line(ref y, x, w, scale, $"Precisamos baixar {Installer.Format(_plan.DownloadBytes)} ({_plan.Install.Count} arquivos).", _text);
                    Line(ref y, x, w, scale, $"Espaço livre no aparelho: {Installer.Format(_free)}." + (_wifi ? "" : " Você está usando dados móveis: prefira o Wi-Fi."), _small);
                    if (Button(ref y, x, w, scale, _wifi ? "Baixar" : "Baixar mesmo assim")) _confirm.TrySetResult(true);
                    break;
                case View.Working:
                    Line(ref y, x, w, scale, $"Baixando: arquivo {Interlocked.Read(ref _installer.Status.FilesDone)} de {Interlocked.Read(ref _installer.Status.FilesTotal)}", _text);
                    Progress(ref y, x, w, scale);
                    Line(ref y, x, w, scale, "Pode bloquear a tela ou usar outro app: o download continua (veja a notificação).", _small);
                    break;
                case View.ApkRequired:
                    Line(ref y, x, w, scale, _message, _text);
                    if (Button(ref y, x, w, scale, "Baixar o app novo")) Application.OpenURL(ApkUrl());
                    break;
                case View.Error:
                    Line(ref y, x, w, scale, _error, _text);
                    if (Button(ref y, x, w, scale, "Tentar de novo")) Begin();
                    if (_offlinePlayable && Button(ref y, x, w, scale, "Jogar mesmo assim")) _view = View.Done;
                    if (Button(ref y, x, w, scale, "Verificar todos os arquivos")) { _verifyAll = true; Begin(); }
                    break;
            }
            if (_optionalApk && _view != View.ApkRequired)
            {
                float by = Screen.height - 110 * scale;
                GUI.Label(new Rect(x, by, w, 40 * scale), $"Nova versão do app disponível ({_channel.Apk.Version}).", _small);
                if (GUI.Button(new Rect(x + w / 2 - 160 * scale, by + 40 * scale, 320 * scale, 50 * scale), "Baixar app novo", _buttonStyle)) Application.OpenURL(ApkUrl());
            }
            GUI.Label(new Rect(0, Screen.height - 36 * scale, Screen.width - 16 * scale, 30 * scale), "app " + _appVersion + (_channel != null ? " · dados " + _channel.Version : ""), new GUIStyle(_small) { alignment = TextAnchor.MiddleRight });
        }

        string ApkUrl() => _channel.Apk.Url.Contains("://") ? _channel.Apk.Url : _repo.Resolve(_channel.Apk.Url).AbsoluteUri;

        void Line(ref float y, float x, float w, float scale, string text, GUIStyle style)
        {
            float h = style.CalcHeight(new GUIContent(text), w) + 12 * scale;
            GUI.Label(new Rect(x, y, w, h), text, style);
            y += h;
        }

        void Progress(ref float y, float x, float w, float scale)
        {
            int percent = Percent();
            var bar = new Rect(x, y + 8 * scale, w, 22 * scale);
            GUI.DrawTexture(bar, _barBack);
            if (percent > 0) GUI.DrawTexture(new Rect(bar.x, bar.y, bar.width * percent / 100f, bar.height), _barFill);
            y += 44 * scale;
            Line(ref y, x, w, scale, (percent >= 0 ? percent + "% · " : "") + Headline(), _small);
        }

        bool Button(ref float y, float x, float w, float scale, string label)
        {
            bool clicked = GUI.Button(new Rect(x + w / 2 - 220 * scale, y + 10 * scale, 440 * scale, 64 * scale), label, _buttonStyle);
            y += 84 * scale;
            return clicked;
        }
    }

    /// <summary>The Java side (Assets/Plugins/Android/UpdateService.java): a foreground service that keeps the process
    /// alive and the CPU and Wi-Fi awake while downloading, with the progress in a notification. Callable from
    /// UpdateScreen.NotifyLoop's thread (attached to the JVM) as well as the main thread.</summary>
    static class Android
    {
        const string Service = "com.ro3emu.updater.UpdateService";
        public static volatile bool Enabled;

        static void Call(string method, params object[] args)
        {
            if (!Enabled) return;
            try
            {
                using (var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                using (var activity = player.GetStatic<AndroidJavaObject>("currentActivity"))
                using (var context = activity.Call<AndroidJavaObject>("getApplicationContext"))
                using (var service = new AndroidJavaClass(Service))
                {
                    var all = new object[args.Length + 1];
                    all[0] = context;
                    Array.Copy(args, 0, all, 1, args.Length);
                    service.CallStatic(method, all);
                }
            }
            catch (Exception e) { Debug.LogWarning("[Ro3Android] updater: " + method + ": " + e.Message); }
        }

        /// <summary>Main thread only (Unity's permission API).</summary>
        public static void AskNotificationPermission()
        {
            if (Enabled && !UnityEngine.Android.Permission.HasUserAuthorizedPermission("android.permission.POST_NOTIFICATIONS"))
                UnityEngine.Android.Permission.RequestUserPermission("android.permission.POST_NOTIFICATIONS");
        }

        public static void StartService(string title, string text) => Call("start", title, text);

        public static void UpdateService(string text, int percent) => Call("update", text, percent);

        public static void StopService() => Call("stop");

        public static void FinishService(string title, string text) => Call("finish", title, text);

        public static void ClearDone() => Call("clearDone");

        public static long FreeSpace(string path)
        {
            if (!Enabled) return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))).AvailableFreeSpace;
            try
            {
                using (var stat = new AndroidJavaObject("android.os.StatFs", path)) return stat.Call<long>("getAvailableBytes");
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Ro3Android] updater: StatFs: " + e.Message);
                return long.MaxValue;
            }
        }
    }
}
