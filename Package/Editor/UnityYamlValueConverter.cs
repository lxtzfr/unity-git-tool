using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace UnityGitTool
{
    /// <summary>
    /// Turns a raw <see cref="UnityYamlParser"/> value (string / Dictionary&lt;string,object&gt; /
    /// List&lt;object&gt;) into the CLR type <see cref="FieldFactory"/> knows how to render —
    /// <see cref="Vector3"/> for an `{x,y,z}` map, <see cref="Color"/> for `{r,g,b,a}`, a boxed
    /// <see cref="int"/>/<see cref="float"/> for a numeric scalar, and so on. Best-effort: a shape
    /// this doesn't recognize (curves, gradients) falls back to a readable string rather than
    /// failing — there's no schema telling this which field is which Unity type, only what the
    /// value looks like.
    /// </summary>
    internal static class UnityYamlValueConverter
    {
        /// <summary><paramref name="byId"/>, when given, resolves a local `{fileID: N}` reference (no
        /// guid — points at another object in this same file/revision) to that object's name/type
        /// instead of a bare number. Must be the byId map for the SAME side (A or B) this value came
        /// from — a fileID is only meaningful within its own revision's document graph.</summary>
        public static object ToDisplayValue(object raw, Dictionary<long, GitYamlDocument> byId = null)
        {
            switch (raw)
            {
                case null:
                    return null;
                case string s:
                    return ToScalar(s);
                case Dictionary<string, object> map:
                    return ToStructured(map, byId);
                case List<object> list:
                    return list.Select(v => ToDisplayValue(v, byId)).ToArray();
                default:
                    return raw.ToString();
            }
        }

        private static object ToScalar(string s)
        {
            if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i)) return i;
            if (float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var f)) return f;
            return s;
        }

        private static object ToStructured(Dictionary<string, object> map, Dictionary<long, GitYamlDocument> byId)
        {
            if (HasKeys(map, "x", "y", "z", "w")) return new Quaternion(F(map, "x"), F(map, "y"), F(map, "z"), F(map, "w"));
            if (HasKeys(map, "x", "y", "z")) return new Vector3(F(map, "x"), F(map, "y"), F(map, "z"));
            if (HasKeys(map, "x", "y")) return new Vector2(F(map, "x"), F(map, "y"));
            if (HasKeys(map, "r", "g", "b")) return new Color(F(map, "r"), F(map, "g"), F(map, "b"), map.ContainsKey("a") ? F(map, "a") : 1f);

            // A bare `{fileID: N}` (optionally with guid/type) is an asset/object reference.
            if (map.ContainsKey("fileID")) return DescribeReference(map, byId);

            return string.Join(", ", map.Select(kv => $"{kv.Key}: {ToDisplayValue(kv.Value, byId)}"));
        }

        private static object DescribeReference(Dictionary<string, object> map, Dictionary<long, GitYamlDocument> byId)
        {
            var fileId = map["fileID"] as string ?? "0";
            if (fileId == "0") return "(none)";

            // A guid means this points at a whole other asset file — resolve it through the asset
            // database rather than the local document graph. Loading the actual asset (not just its
            // path) lets FieldFactory render a real, clickable ObjectField instead of inert text —
            // only falls back to a filename string if the asset can't be loaded (e.g. deleted, or a
            // sub-asset inside a composite file this only resolves the main representation of).
            if (map.TryGetValue("guid", out var guidValue) && guidValue is string guid)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrEmpty(path)) return $"guid:{guid}";
                var asset = AssetDatabase.LoadMainAssetAtPath(path);
                return asset != null ? asset : Path.GetFileName(path);
            }

            // No guid: it points at another object inside this same file — look it up by FileId in
            // this revision's own document graph rather than showing a meaningless raw number.
            if (byId != null && long.TryParse(fileId, out var id) && byId.TryGetValue(id, out var target))
                return DescribeLocalObject(target, byId);

            return $"fileID:{fileId}";
        }

        private static object DescribeLocalObject(GitYamlDocument document, Dictionary<long, GitYamlDocument> byId)
        {
            // A "stripped" placeholder (see GitYamlDocument.Stripped) has no m_Name/m_GameObject of
            // its own — it's a pointer into a nested prefab instance. Its m_CorrespondingSourceObject
            // (guid + local fileID within that prefab file) is exactly what GlobalObjectId needs to
            // resolve the EXACT sub-object (identifierType 3 = "source asset object") — not just the
            // prefab's root — so e.g. a script field pointing at a nested "Body" component resolves
            // to that Body instance, same as it would in Unity's own Inspector.
            if (document.Stripped &&
                document.Fields.TryGetValue("m_CorrespondingSourceObject", out var sourceRef) &&
                sourceRef is Dictionary<string, object> sourceMap &&
                sourceMap.TryGetValue("guid", out var sourceGuid) && sourceGuid is string guid)
            {
                var sourceFileId = sourceMap.TryGetValue("fileID", out var sfid) ? sfid as string : null;
                if (sourceFileId != null && GlobalObjectId.TryParse($"GlobalObjectId_V1-3-{guid}-{sourceFileId}-0", out var goid))
                {
                    var precise = GlobalObjectId.GlobalObjectIdentifierToObjectSlow(goid);
                    if (precise != null) return precise;
                }

                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (!string.IsNullOrEmpty(path))
                {
                    var asset = AssetDatabase.LoadMainAssetAtPath(path);
                    if (asset != null) return asset;
                    return $"{Path.GetFileNameWithoutExtension(path)} ({document.TypeName})";
                }
            }

            if (document.TypeName == "GameObject")
                return document.Fields.TryGetValue("m_Name", out var name) && name is string n ? n : "GameObject";

            // A component: "<owning GameObject's name> (<component type>)" when the owner resolves,
            // else just the component type.
            if (document.Fields.TryGetValue("m_GameObject", out var ownerRef) &&
                ownerRef is Dictionary<string, object> ownerMap &&
                ownerMap.TryGetValue("fileID", out var ownerFileId) && ownerFileId is string ownerFileIdStr &&
                long.TryParse(ownerFileIdStr, out var ownerId) && byId.TryGetValue(ownerId, out var owner) &&
                owner.Fields.TryGetValue("m_Name", out var ownerName) && ownerName is string ownerNameStr)
            {
                return $"{ownerNameStr} ({document.TypeName})";
            }
            return document.TypeName ?? "(unknown)";
        }

        private static bool HasKeys(Dictionary<string, object> map, params string[] keys) =>
            map.Count == keys.Length && keys.All(map.ContainsKey);

        private static float F(Dictionary<string, object> map, string key) =>
            map.TryGetValue(key, out var v) && v is string s && float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ? f : 0f;
    }
}
