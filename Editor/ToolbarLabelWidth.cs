using System;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace GitToolbar
{
    /// <summary>
    /// Unity's toolbar stylesheet caps element labels at 120px (with an ellipsis). There is no public API to change it,
    /// so this finds a specific element's label in the main toolbar and removes the cap.
    /// It relies on internal editor types; if a Unity update changes them, it silently does nothing and the label is just truncated again.
    /// </summary>
    static class ToolbarLabelWidth
    {
        const string k_LabelClass = "unity-editor-toolbar-element__label";
        const BindingFlags k_Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        static readonly Type s_GuiViewType = typeof(Editor).Assembly.GetType("UnityEditor.GUIView");
        static readonly Type s_ToolbarType = typeof(Editor).Assembly.GetType("UnityEditor.Toolbar");

        /// <summary>
        /// Removes the max-width of the label inside the toolbar element registered under elementPath.
        /// Cheap when the label was already found and is still alive.
        /// </summary>
        public static void Unclamp(string elementPath, ref TextElement cachedLabel)
        {
            try
            {
                if (cachedLabel == null || cachedLabel.panel == null)
                {
                    cachedLabel = FindLabel(elementPath.Replace(" ", string.Empty));
                }

                if (cachedLabel != null && cachedLabel.style.maxWidth.keyword != StyleKeyword.None)
                {
                    cachedLabel.style.maxWidth = StyleKeyword.None;
                }
            }
            catch (Exception)
            {
                // Internal toolbar layout changed - keep Unity's default truncation.
                cachedLabel = null;
            }
        }

        static TextElement FindLabel(string overlayName)
        {
            if (s_GuiViewType == null || s_ToolbarType == null) return null;

            var windowBackendProperty = s_GuiViewType.GetProperty("windowBackend", k_Flags);
            if (windowBackendProperty == null) return null;

            foreach (var toolbar in Resources.FindObjectsOfTypeAll(s_ToolbarType))
            {
                var backend = windowBackendProperty.GetValue(toolbar);
                var root = backend?.GetType().GetProperty("visualTree", k_Flags)?.GetValue(backend) as VisualElement;

                // The toolbar names each element's container after its path, without spaces.
                var container = root?.Q(overlayName);
                var label = container?.Q<TextElement>(className: k_LabelClass);
                if (label != null) return label;
            }
            return null;
        }
    }
}
