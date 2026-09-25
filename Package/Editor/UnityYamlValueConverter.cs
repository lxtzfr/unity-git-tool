using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;

namespace UnityGitTool
{
    /// <summary>
    /// Turns a raw <see cref="UnityYamlParser"/> value (string / Dictionary&lt;string,object&gt; /
    /// List&lt;object&gt;) into the CLR type <see cref="FieldFactory"/> knows how to render —
    /// <see cref="Vector3"/> for an `{x,y,z}` map, <see cref="Color"/> for `{r,g,b,a}`, a boxed
    /// <see cref="int"/>/<see cref="float"/> for a numeric scalar, and so on. Best-effort: a shape
    /// this doesn't recognize (curves, gradients, object references with a guid) falls back to a
    /// readable string rather than failing — there's no schema telling this which field is which
    /// Unity type, only what the value looks like.
    /// </summary>
    internal static class UnityYamlValueConverter
    {
        public static object ToDisplayValue(object raw)
        {
            switch (raw)
            {
                case null:
                    return null;
                case string s:
                    return ToScalar(s);
                case Dictionary<string, object> map:
                    return ToStructured(map);
                case List<object> list:
                    return list.Select(ToDisplayValue).ToArray();
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

        private static object ToStructured(Dictionary<string, object> map)
        {
            if (HasKeys(map, "x", "y", "z", "w")) return new Quaternion(F(map, "x"), F(map, "y"), F(map, "z"), F(map, "w"));
            if (HasKeys(map, "x", "y", "z")) return new Vector3(F(map, "x"), F(map, "y"), F(map, "z"));
            if (HasKeys(map, "x", "y")) return new Vector2(F(map, "x"), F(map, "y"));
            if (HasKeys(map, "r", "g", "b")) return new Color(F(map, "r"), F(map, "g"), F(map, "b"), map.ContainsKey("a") ? F(map, "a") : 1f);

            // A bare `{fileID: N}` (optionally with guid/type) is an asset/object reference — this
            // tool doesn't resolve guids to asset paths yet, so show the raw pointer instead of
            // silently dropping it.
            if (map.ContainsKey("fileID"))
            {
                var fileId = map["fileID"] as string ?? "0";
                return fileId == "0" ? "(none)" : map.ContainsKey("guid") ? $"guid:{map["guid"]} fileID:{fileId}" : $"fileID:{fileId}";
            }

            return string.Join(", ", map.Select(kv => $"{kv.Key}: {ToDisplayValue(kv.Value)}"));
        }

        private static bool HasKeys(Dictionary<string, object> map, params string[] keys) =>
            map.Count == keys.Length && keys.All(map.ContainsKey);

        private static float F(Dictionary<string, object> map, string key) =>
            map.TryGetValue(key, out var v) && v is string s && float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ? f : 0f;
    }
}
