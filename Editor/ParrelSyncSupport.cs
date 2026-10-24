using System;
using System.Reflection;

namespace GitToolbar
{
    /// <summary>
    /// Optional ParrelSync integration (https://github.com/VeriorPies/ParrelSync).
    /// ParrelSync is never referenced directly: it's looked up by reflection, so the tool compiles and works without it.
    /// </summary>
    static class ParrelSyncSupport
    {
        /// <summary>
        /// True when this editor is a ParrelSync clone. Always false when ParrelSync isn't installed.
        /// </summary>
        public static bool IsClone()
        {
            try
            {
                var clonesManager = Type.GetType("ParrelSync.ClonesManager, ParrelSync");
                var isClone = clonesManager?.GetMethod("IsClone", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
                return isClone != null && (bool)isClone.Invoke(null, null);
            }
            catch (Exception)
            {
                // A ParrelSync version with a different API - treat as not a clone.
                return false;
            }
        }
    }
}
