using System.Collections.Generic;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;
using UnityEngine.UIElements;
using UnityGitTool.Yaml;

namespace UnityGitTool
{
    /// <summary>Resolves the same small icons Unity itself uses (Project window, Hierarchy,
    /// Inspector) for files, GameObjects, and components, so the diff window's three list/tree
    /// columns read like the editor windows they mirror instead of plain text rows.</summary>
    internal static class DiffIcons
    {
        // EditorGUIUtility.IconContent allocates a new GUIContent per call — components repeat
        // constantly across a scene (every GameObject has a Transform), so cache by root key.
        private static readonly Dictionary<string, Texture> ComponentIconCache = new();

        // Which source-prefab-asset icon a PrefabInstance row should use, by guid — a plain
        // Regular prefab and a Prefab Variant/imported-model instance all serialize as the same
        // "PrefabInstance" root key, so this can only be told apart by resolving the actual
        // referenced asset (see ResolvePrefabInstanceIcon), not from the YAML document alone.
        private static readonly Dictionary<string, Texture> PrefabAssetIconByGuid = new();

        private const float IconSize = 16f;
        private const float OverlaySize = 9f;

        public static Image BuildIcon(Texture texture) => new()
        {
            image = texture,
            scaleMode = ScaleMode.ScaleToFit,
            style = { width = IconSize, height = IconSize, marginRight = 4, flexShrink = 0 },
        };

        /// <summary>Same as <see cref="BuildIcon"/>, plus a small "+"/"-" badge flush in the
        /// bottom-right corner — matching how Unity's own Hierarchy marks a GameObject that's a
        /// genuine per-instance override (<c>m_AddedGameObjects</c>/<c>m_RemovedGameObjects</c>),
        /// never merely because a row happens to read Added/Removed in this diff (see
        /// <see cref="Diff.ObjectGroup.IsAddedGameObjectOverride"/>). Pass a null
        /// <paramref name="overlay"/> to get a plain icon.</summary>
        public static VisualElement BuildIcon(Texture texture, Texture overlay)
        {
            if (overlay == null) return BuildIcon(texture);

            var container = new VisualElement
            {
                style = { width = IconSize, height = IconSize, marginRight = 4, flexShrink = 0, position = Position.Relative },
            };
            container.Add(new Image { image = texture, scaleMode = ScaleMode.ScaleToFit, style = { width = IconSize, height = IconSize } });
            container.Add(new Image
            {
                image = overlay,
                scaleMode = ScaleMode.ScaleToFit,
                style = { width = OverlaySize, height = OverlaySize, position = Position.Absolute, bottom = 0, right = 0 },
            });
            return container;
        }

        /// <summary>The small corner badge for a genuine per-instance Added/Removed override (see
        /// <see cref="Diff.ObjectGroup.IsAddedGameObjectOverride"/>/<c>IsRemovedGameObjectOverride</c>)
        /// — null for anything else.</summary>
        public static Texture GetOverrideOverlay(bool isAdded, bool isRemoved)
        {
            if (isAdded) return GetCachedBuiltinIcon("PrefabOverlayAdded", "PrefabOverlayAdded Icon");
            if (isRemoved) return GetCachedBuiltinIcon("PrefabOverlayRemoved", "PrefabOverlayRemoved Icon");
            return null;
        }

        /// <summary>Extension-based file icon (Scene, Prefab, Material, ...) — looked up by
        /// filename only, so it still resolves for a file that doesn't exist at this revision
        /// (deleted / not yet present in the working tree).</summary>
        public static Texture GetFileIcon(string path)
        {
            var name = path.Substring(path.LastIndexOf('/') + 1);
            return InternalEditorUtility.GetIconForFile(name);
        }

        /// <summary>Icon for a parsed YAML document — a GameObject, a built-in component
        /// (Transform, Rigidbody, ...), or a custom script's own icon for MonoBehaviours.</summary>
        public static Texture GetDocumentIcon(UnityYamlDocument doc) => GetDocumentIcon(doc?.RootKey, doc);

        private static Texture GetDocumentIcon(string rootKey, UnityYamlDocument doc)
        {
            if (string.IsNullOrEmpty(rootKey)) return GetGenericIcon();

            if (rootKey == "MonoBehaviour")
            {
                var scriptIcon = ResolveScriptIcon(doc);
                if (scriptIcon != null) return scriptIcon;
                return GetCachedBuiltinIcon("cs Script", "cs Script Icon");
            }

            if (rootKey == "PrefabInstance") return ResolvePrefabInstanceIcon(doc);

            return GetCachedBuiltinIcon(rootKey, rootKey + " Icon");
        }

        /// <summary>A "PrefabInstance" document only says which source asset it instantiates
        /// (<c>m_SourcePrefab</c>'s guid) — it never records whether that asset is a Regular
        /// prefab, a Prefab Variant, or an imported model (FBX) root, so the row's icon has to
        /// resolve the actual referenced asset via <see cref="PrefabUtility.GetPrefabAssetType"/>
        /// to tell them apart, same as Unity's own Hierarchy/Project windows do.</summary>
        private static Texture ResolvePrefabInstanceIcon(UnityYamlDocument doc)
        {
            if (doc?.Fields.TryGetValue("m_SourcePrefab", out var sp) != true ||
                sp is not Dictionary<string, object> spMap ||
                spMap.TryGetValue("guid", out var g) != true ||
                g?.ToString() is not { Length: > 0 } guid)
            {
                return GetCachedBuiltinIcon("PrefabInstance", "Prefab Icon");
            }

            if (PrefabAssetIconByGuid.TryGetValue(guid, out var cached)) return cached;

            var iconName = "Prefab Icon";
            var assetPath = AssetDatabase.GUIDToAssetPath(guid);
            if (!string.IsNullOrEmpty(assetPath))
            {
                var asset = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
                if (asset != null)
                {
                    iconName = PrefabUtility.GetPrefabAssetType(asset) switch
                    {
                        PrefabAssetType.Variant => "PrefabVariant Icon",
                        PrefabAssetType.Model => "PrefabModel Icon",
                        _ => "Prefab Icon",
                    };
                }
            }

            var icon = EditorGUIUtility.FindTexture(iconName) ?? GetGenericIcon();
            PrefabAssetIconByGuid[guid] = icon;
            return icon;
        }

        // EditorGUIUtility.IconContent logs an "Unable to load the icon" warning whenever the
        // name doesn't resolve — which happens constantly here since GetDocumentIcon feeds it
        // every YAML root key verbatim (e.g. scene-singleton objects like RenderSettings,
        // LightmapSettings, NavMeshSettings have no matching built-in icon). FindTexture does
        // the same built-in lookup but returns null silently instead of logging.
        private static Texture GetCachedBuiltinIcon(string cacheKey, string iconName)
        {
            if (ComponentIconCache.TryGetValue(cacheKey, out var cached)) return cached;

            var icon = EditorGUIUtility.FindTexture(iconName) ?? GetGenericIcon();
            ComponentIconCache[cacheKey] = icon;
            return icon;
        }

        private static Texture GetGenericIcon() => GetCachedBuiltinIcon("__generic", "GameObject Icon");

        /// <summary>A script's custom icon (set via the inspector on the .cs asset) if it has
        /// one, resolved the same way <see cref="InspectorFieldView"/> resolves the script's
        /// display name — through <c>m_Script</c>'s guid.</summary>
        private static Texture ResolveScriptIcon(UnityYamlDocument doc)
        {
            var path = InspectorFieldView.ResolveScriptPath(doc);
            return string.IsNullOrEmpty(path) ? null : AssetDatabase.GetCachedIcon(path);
        }
    }
}
