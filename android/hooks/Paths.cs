using UnityEngine;

namespace Ro3Android.Hooks
{
    /// <summary>
    /// Where the game finds its StreamingAssets on Android. The PC client's StreamingAssets (the 24 GB VFS included) does
    /// not fit in an APK, so it is pushed to the app's external files folder instead
    /// (/sdcard/Android/data/com.ro3emu.client/files/StreamingAssets) and every game call to
    /// Application.streamingAssetsPath lands here (android\tools\AndroidPatch.cs). Elsewhere it is the engine's own path.
    /// </summary>
    public static class Paths
    {
        private static string _streamingAssets;

        public static string StreamingAssets()
        {
            if (_streamingAssets == null)
            {
                _streamingAssets = Application.platform == RuntimePlatform.Android
                    ? Application.persistentDataPath + "/StreamingAssets"
                    : Application.streamingAssetsPath;
                Debug.Log("[Ro3Android] streamingAssetsPath -> " + _streamingAssets);
            }
            return _streamingAssets;
        }
    }
}
