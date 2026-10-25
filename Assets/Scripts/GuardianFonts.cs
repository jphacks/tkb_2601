using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace Guardian.UnitySim
{
    /// <summary>
    /// The Japanese font for on-screen panels and signs. Windows keeps the installed Yu Gothic / Meiryo. On a
    /// Mac, fonts made from the OS (Hiragino and the rest are .ttc collections) fail to load and IMGUI draws
    /// other text ("Gizmos") in place of the labels, so a Mac, or any machine without one of the listed
    /// fonts, uses Noto Sans JP from Assets/Art/Fonts/Resources. Hand fonts back with Release, not Destroy:
    /// the bundled font is a shared asset.
    /// Unity 6 draws IMGUI text with TextCore. A font TextCore cannot open makes it log "Unable to load font
    /// face" with a stack trace on every repaint (gigabytes of Editor.log in one Play), so each font is opened
    /// once here first and one that fails is never handed out.
    /// </summary>
    public static class GuardianFonts
    {
        private static readonly string[] Preferred =
        {
            "Yu Gothic UI", "Yu Gothic", "Meiryo", "MS Gothic",                                // Windows
            "Noto Sans CJK JP", "Noto Sans JP", "IPAexGothic", "IPAGothic", "TakaoGothic",     // Linux
            "Arial"
        };
        private const string BundledPath = "NotoSansJP-Regular";
        private static string[] installed;
        private static Font bundled, builtin;
        private static bool bundledTried;
        // FontEngine.LoadFontFace(Font), looked up so this compiles whichever TextCore modules the project has.
        private static readonly MethodInfo loadFace = Type.GetType("UnityEngine.TextCore.LowLevel.FontEngine, UnityEngine.TextCoreFontEngineModule")
            ?.GetMethod("LoadFontFace", new[] { typeof(Font) });

        public static Font Create(int size)
        {
            bool mac = Application.platform == RuntimePlatform.OSXEditor || Application.platform == RuntimePlatform.OSXPlayer;
            if (!mac)
            {
                installed ??= Pick();
                if (installed.Length > 0)
                {
                    Font os = Font.CreateDynamicFontFromOSFont(installed, size);
                    if (Opens(os)) return os;
                    Discard(os);
                    installed = Array.Empty<string>();  // the same names would fail again for the next panel
                }
            }
            if (!bundledTried)
            {
                bundledTried = true;
                Font font = Resources.Load<Font>(BundledPath);
                if (font != null && Opens(font)) bundled = font;
                else Debug.LogWarning("[GuardianFonts] " + BundledPath + " could not be opened; panels use Unity's built-in font");
            }
            if (bundled != null) return bundled;
            if (builtin == null) builtin = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            return builtin;
        }

        /// <summary>Destroys a font made by Create, unless it is one of the shared assets.</summary>
        public static void Release(Font font)
        {
            if (font != bundled && font != builtin) Discard(font);
        }

        private static bool Opens(Font font)
        {
            if (font == null) return false;
            if (loadFace == null) return true;  // no TextCore font engine: IMGUI draws with the font directly
            return Convert.ToInt32(loadFace.Invoke(null, new object[] { font })) == 0;  // FontEngineError.Success
        }

        private static void Discard(Font font)
        {
            if (font == null) return;
            if (Application.isPlaying) UnityEngine.Object.Destroy(font); else UnityEngine.Object.DestroyImmediate(font);
        }

        private static string[] Pick()
        {
            string[] have = Font.GetOSInstalledFontNames();
            var picked = new List<string>();
            // Some systems list a family with its weight ("Hiragino Sans W3"), so a name followed by a space also counts.
            foreach (string name in Preferred)
                foreach (string h in have)
                    if ((string.Equals(h, name, StringComparison.OrdinalIgnoreCase) || h.StartsWith(name + " ", StringComparison.OrdinalIgnoreCase))
                        && !picked.Contains(h)) picked.Add(h);
            return picked.ToArray();
        }
    }
}
