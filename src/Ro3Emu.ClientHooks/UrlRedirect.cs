using System;
using UnityEngine;

namespace Ro3Emu.ClientHooks
{
    public static class UrlRedirect
    {
        public static void OpenURL(string url)
        {
            if (!string.IsNullOrEmpty(url))
            {
                if (url.Contains("ro3global", StringComparison.OrdinalIgnoreCase))
                {
                    url = url.Replace("https://discord.com/invite/ro3global", "https://discord.com/invite/herosagammo")
                             .Replace("https://discord.gg/ro3global", "https://discord.com/invite/herosagammo")
                             .Replace("ro3global", "herosagammo");
                }
            }
            Debug.Log("[Ro3Emu.UrlRedirect] Opening URL: " + url);
            Application.OpenURL(url);
        }
    }
}
