using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace Guardian.UnitySim
{
    /// <summary>Japanese-capable font selection with a bundled OFL fallback.</summary>
    public static class GuardianFonts
    {
        private static readonly string[] Preferred =
        {
            "Yu Gothic UI", "Yu Gothic", "Meiryo", "MS Gothic",
            "Noto Sans CJK JP", "Noto Sans JP", "IPAexGothic", "IPAGothic", "TakaoGothic"
        };
        private const string BundledPath="NotoSansJP-Regular";
        private static readonly MethodInfo LoadFace=Type.GetType(
            "UnityEngine.TextCore.LowLevel.FontEngine, UnityEngine.TextCoreFontEngineModule")
            ?.GetMethod("LoadFontFace",new[]{typeof(Font)});
        private static string[] installed;
        private static Font bundled,builtin;
        private static bool bundledTried;

        public static Font Create(int size)
        {
            bool mac=Application.platform==RuntimePlatform.OSXEditor || Application.platform==RuntimePlatform.OSXPlayer;
            if (!mac)
            {
                installed=installed??Pick();
                if (installed.Length>0)
                {
                    Font os=Font.CreateDynamicFontFromOSFont(installed,size);
                    if (Opens(os)) return os;
                    Discard(os); installed=Array.Empty<string>();
                }
            }
            if (!bundledTried)
            {
                bundledTried=true;
                Font candidate=Resources.Load<Font>(BundledPath);
                if (candidate!=null && Opens(candidate)) bundled=candidate;
                else Debug.LogWarning("[GuardianFonts] Japanese fallback font could not be opened");
            }
            if (bundled!=null) return bundled;
            if (builtin==null) builtin=Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            return builtin;
        }

        public static void Release(Font font)
        {
            if (font!=bundled && font!=builtin) Discard(font);
        }

        private static bool Opens(Font font)
        {
            if (font==null) return false;
            if (!font.HasCharacter('日') || !font.HasCharacter('本') || !font.HasCharacter('語')) return false;
            if (LoadFace==null) return true;
            try { return Convert.ToInt32(LoadFace.Invoke(null,new object[]{font}))==0; }
            catch (TargetInvocationException) { return false; }
        }

        private static void Discard(Font font)
        {
            if (font==null) return;
            if (Application.isPlaying) UnityEngine.Object.Destroy(font); else UnityEngine.Object.DestroyImmediate(font);
        }

        private static string[] Pick()
        {
            string[] available=Font.GetOSInstalledFontNames();
            var result=new List<string>();
            foreach (string preferred in Preferred)
                foreach (string candidate in available)
                    if ((string.Equals(candidate,preferred,StringComparison.OrdinalIgnoreCase)
                        || candidate.StartsWith(preferred+" ",StringComparison.OrdinalIgnoreCase))
                        && !result.Contains(candidate)) result.Add(candidate);
            return result.ToArray();
        }
    }
}
