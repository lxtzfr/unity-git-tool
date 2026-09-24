using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using YamlDotNet.RepresentationModel;

namespace UnityGitTool.Yaml
{
    /// <summary>
    /// Parses the multi-document YAML that Unity emits for text-serialized assets
    /// (scenes, prefabs, materials, ScriptableObjects) using YamlDotNet. Each document
    /// is expected to look like:
    /// <code>
    /// --- !u!1 &amp;330585543
    /// GameObject:
    ///   m_Name: Main Camera
    ///   ...
    /// </code>
    /// </summary>
    public static class UnityYamlParser
    {
        // File extensions Unity text-serializes with this YAML shape. Scripts, images, and
        // other binary/plain-text assets show up in a git diff too but TryParse would just
        // reject or no-op on them, so the file list can filter on this instead of fetching
        // and parsing every changed file's content up front.
        private static readonly HashSet<string> DiffableExtensions = new(System.StringComparer.OrdinalIgnoreCase)
        {
            ".unity", ".prefab", ".asset", ".mat", ".physicsMaterial", ".physicsMaterial2D",
            ".controller", ".anim", ".overrideController", ".mask", ".playable", ".signal",
            ".mixer", ".guiskin", ".fontsettings", ".preset", ".shadervariants", ".terrainlayer",
            ".lighting", ".spriteatlas", ".spriteatlasv2", ".renderTexture", ".cubemap",
            ".flare", ".brush", ".giparams",
        };

        /// <summary>Whether <paramref name="path"/>'s extension is one Unity text-serializes
        /// with this YAML shape — a fast pre-filter for the file list, not a guarantee
        /// (a project can force binary serialization for any of these).</summary>
        public static bool IsLikelyDiffable(string path) => DiffableExtensions.Contains(Path.GetExtension(path));

        // Unity appends " stripped" to the document marker for prefab-instance overrides
        // (e.g. "--- !u!1001 &100100000 stripped"). That trailing word isn't valid YAML at
        // that position and makes YamlDotNet throw while scanning the *whole* stream, not
        // just that document. It's stripped out before parsing and the flag is re-attached
        // afterwards by matching on fileId.
        private static readonly Regex StrippedMarkerRegex =
            new Regex(@"^(--- !u!\d+ &-?\d+) stripped\s*$", RegexOptions.Multiline);

        /// <summary>
        /// Parses Unity's multi-document YAML text. Returns false (with <paramref name="error"/>
        /// set and <paramref name="documents"/> empty) if the text isn't parseable YAML at all
        /// (e.g. a binary asset or a corrupted blob) rather than throwing. Individual documents
        /// that don't match Unity's expected shape (single root key with a "!u!&lt;classId&gt;"
        /// tag) are silently skipped, since a scene/prefab can freely mix in unrelated document
        /// shapes without that being an error worth surfacing.
        /// </summary>
        public static bool TryParse(string yamlText, out List<UnityYamlDocument> documents, out string error)
        {
            documents = new List<UnityYamlDocument>();
            error = null;

            if (string.IsNullOrEmpty(yamlText))
            {
                return true;
            }

            var strippedFileIds = FindStrippedFileIds(yamlText);
            var sanitizedText = StrippedMarkerRegex.Replace(yamlText, "$1");

            YamlStream stream;
            try
            {
                stream = new YamlStream();
                using (var reader = new StringReader(sanitizedText))
                {
                    stream.Load(reader);
                }
            }
            catch (YamlDotNet.Core.YamlException e)
            {
                error = $"Invalid YAML: {e.Message}";
                return false;
            }

            foreach (var document in stream.Documents)
            {
                if (document.RootNode is not YamlMappingNode root || root.Children.Count != 1)
                {
                    continue;
                }

                if (!TryParseClassId(root.Tag.Value, out var classId))
                {
                    continue;
                }

                if (!long.TryParse(root.Anchor.Value, out var fileId))
                {
                    continue;
                }

                var entry = System.Linq.Enumerable.First(root.Children);
                var rootKey = ((YamlScalarNode)entry.Key).Value;

                documents.Add(new UnityYamlDocument
                {
                    ClassId = classId,
                    FileId = fileId,
                    RootKey = rootKey,
                    Fields = ConvertMapping(entry.Value as YamlMappingNode),
                    Stripped = strippedFileIds.Contains(fileId),
                });
            }

            return true;
        }

        private static HashSet<long> FindStrippedFileIds(string yamlText)
        {
            var fileIds = new HashSet<long>();
            foreach (Match match in StrippedMarkerRegex.Matches(yamlText))
            {
                var headerMatch = Regex.Match(match.Groups[1].Value, @"&(?<fileId>-?\d+)$");
                if (headerMatch.Success)
                {
                    fileIds.Add(long.Parse(headerMatch.Groups["fileId"].Value));
                }
            }

            return fileIds;
        }

        private const string UnityTagPrefix = "tag:unity3d.com,2011:";

        /// <summary>
        /// Converts a Unity class-id tag into its numeric id. YamlDotNet resolves the
        /// "!u!123" shorthand written in the file into "tag:unity3d.com,2011:123".
        /// </summary>
        private static bool TryParseClassId(string tag, out int classId)
        {
            classId = 0;
            if (string.IsNullOrEmpty(tag) || !tag.StartsWith(UnityTagPrefix))
            {
                return false;
            }

            return int.TryParse(tag.Substring(UnityTagPrefix.Length), out classId);
        }

        private static Dictionary<string, object> ConvertMapping(YamlMappingNode node)
        {
            var map = new Dictionary<string, object>();
            if (node == null)
            {
                return map;
            }

            foreach (var kv in node.Children)
            {
                map[((YamlScalarNode)kv.Key).Value] = ConvertNode(kv.Value);
            }

            return map;
        }

        private static object ConvertNode(YamlNode node)
        {
            switch (node)
            {
                case YamlScalarNode scalar:
                    return scalar.Value;
                case YamlMappingNode mapping:
                    return ConvertMapping(mapping);
                case YamlSequenceNode sequence:
                    var list = new List<object>();
                    foreach (var item in sequence.Children)
                    {
                        list.Add(ConvertNode(item));
                    }

                    return list;
                default:
                    return null;
            }
        }
    }
}
