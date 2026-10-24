using System;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace GitToolbar
{
    /// <summary>
    /// Updates an editor tooltip that is already on screen. Unity only reads an element's tooltip when the hover starts,
    /// so changing it while hovering does nothing until the next hover.
    /// Relies on the internal TooltipView; if a Unity update changes it, this silently does nothing (re-hover still works).
    /// </summary>
    static class ToolbarTooltip
    {
        const BindingFlags k_Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;

        static readonly Type s_TooltipViewType = typeof(Editor).Assembly.GetType("UnityEditor.TooltipView");

        /// <summary>
        /// If the open tooltip is showing oldText, replaces it with newText in place. Returns whether it did.
        /// </summary>
        public static bool ReplaceIfShowing(string oldText, string newText)
        {
            if (string.IsNullOrEmpty(oldText) || s_TooltipViewType == null) return false;

            try
            {
                var view = s_TooltipViewType.GetField("s_guiView", k_Flags)?.GetValue(null) as UnityEngine.Object;
                if (view == null) return false;

                var shown = (s_TooltipViewType.GetField("m_tooltip", k_Flags)?.GetValue(view) as GUIContent)?.text;
                if (shown == null || !shown.StartsWith(oldText, StringComparison.Ordinal)) return false;

                // Keep whatever Unity appended (e.g. "Hold Ctrl to drag and move").
                var suffix = shown.Substring(oldText.Length);
                var hoverRect = (Rect)s_TooltipViewType.GetField("m_hoverRect", k_Flags).GetValue(view);
                var hostView = s_TooltipViewType.GetField("hostView", k_Flags).GetValue(view);

                s_TooltipViewType.GetMethod("Show", k_Flags)?.Invoke(null, new[] { newText + suffix, hoverRect, hostView });
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
