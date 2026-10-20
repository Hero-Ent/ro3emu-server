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
                if (url.IndexOf("ro3global", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    url = url.Replace("https://discord.com/invite/ro3global", "https://discord.com/invite/mnUBY54jAe")
                             .Replace("https://discord.gg/ro3global", "https://discord.com/invite/mnUBY54jAe")
                             .Replace("ro3global", "mnUBY54jAe");
                }
            }
            Debug.Log("[Ro3Emu.UrlRedirect] Opening URL: " + url);
            Application.OpenURL(url);
        }
    }
}
