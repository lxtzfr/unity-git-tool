using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UnityGitTool
{
    /// <summary>Selecting a node in any tab selects its object in Unity (Hierarchy / Project / Inspector),
    /// behind one toggle shared by every tab and kept across sessions.</summary>
    internal static class EditorSelectionSync
    {
        private const string PrefKey = "UnityGitTool.SyncSelection";

        public static bool Enabled
        {
            get => EditorPrefs.GetBool(PrefKey, true);
            set
            {
                EditorPrefs.SetBool(PrefKey, value);
                Debug.Log($"[UnityGitTool] Sync selection with Unity: {(value ? "on" : "off")}");
            }
        }

        /// <summary>Falls back to the file itself (a scene not loaded, an object only in another revision)
        /// when <paramref name="fallbackAssetPath"/> is given.</summary>
        public static void Select(Object target, string fallbackAssetPath = null)
        {
            if (!Enabled) return;
            if (target == null && fallbackAssetPath != null) target = AssetDatabase.LoadMainAssetAtPath(fallbackAssetPath);
            if (target == null) return;
            Selection.activeObject = target;
            EditorGUIUtility.PingObject(target);
        }

        /// <summary>The object a saved file declares under <paramref name="fileId"/> (0: the file's main
        /// asset). A scene object only exists while that scene is loaded; null otherwise, or when the
        /// object is only in another revision.</summary>
        public static Object Find(string assetPath, long fileId)
        {
            if (fileId == 0) return AssetDatabase.LoadMainAssetAtPath(assetPath);
            var guid = AssetDatabase.AssetPathToGUID(assetPath);
            if (assetPath.EndsWith(".unity")) return FindInScene(assetPath, guid, fileId, 0);
            return UnityYamlValueConverter.ObjectsByLocalId(guid, assetPath).GetValueOrDefault(fileId);
        }

        /// <summary>The object of prefab instance <paramref name="instanceId"/> that comes from
        /// <paramref name="sourceObject"/> in its source asset (the instance root when that is the source's root).</summary>
        public static Object FindInInstance(string assetPath, long instanceId, Object sourceObject)
        {
            if (sourceObject == null || !AssetDatabase.TryGetGUIDAndLocalFileIdentifier(sourceObject, out string _, out long sourceId)) return null;
            var guid = AssetDatabase.AssetPathToGUID(assetPath);
            // A scene's GlobalObjectId names an instance object by (source id, instance id); a prefab
            // file stores it under the combined id.
            if (assetPath.EndsWith(".unity")) return FindInScene(assetPath, guid, sourceId, instanceId);
            return UnityYamlValueConverter.ObjectsByLocalId(guid, assetPath).GetValueOrDefault((sourceId ^ instanceId) & long.MaxValue);
        }

        public static Object FindInstanceRoot(string assetPath, long instanceId, string sourceGuid) =>
            string.IsNullOrEmpty(sourceGuid) ? null
                : FindInInstance(assetPath, instanceId, AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GUIDToAssetPath(sourceGuid)));

        private static Object FindInScene(string assetPath, string guid, long objectId, long prefabId)
        {
            if (!SceneManager.GetSceneByPath(assetPath).isLoaded) return null;
            return GlobalObjectId.TryParse($"GlobalObjectId_V1-2-{guid}-{(ulong)objectId}-{(ulong)prefabId}", out var id)
                ? GlobalObjectId.GlobalObjectIdentifierToObjectSlow(id)
                : null;
        }
    }
}
