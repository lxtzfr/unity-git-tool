using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace UnityGitTool
{
    /// <summary>One `--- !u!&lt;classId&gt; &amp;&lt;fileId&gt;` document from a Unity scene/prefab
    /// YAML file — a GameObject, a Transform, a MonoBehaviour, etc. <see cref="FileId"/> is Unity's
    /// stable per-object identifier within the file, used to match the "same" object across two
    /// revisions even if every field on it changed. <see cref="Fields"/> is the parsed body under
    /// the single <see cref="TypeName"/> key Unity always wraps a document's data in.</summary>
    internal sealed class GitYamlDocument
    {
        public int ClassId;
        public long FileId;
        public string TypeName;
        public Dictionary<string, object> Fields = new();

        /// <summary>A "stripped" document is a placeholder Unity emits so something inside this file
        /// can point at an object that actually lives in a nested prefab — it carries none of that
        /// object's real data (no m_Name, no components), only bookkeeping fields pointing at the
        /// prefab source. Never meaningful to show as changed content.</summary>
        public bool Stripped;
    }

    /// <summary>
    /// Parses the subset of YAML Unity actually writes for scenes/prefabs — enough to diff it, not
    /// a general-purpose YAML parser (no anchors/aliases, no multi-line scalars, no comments). A
    /// parsed value is one of: <see cref="string"/> (raw scalar text, numbers included — callers
    /// convert as needed via <see cref="UnityYamlValueConverter"/>), <see cref="Dictionary{TKey,TValue}"/>
    /// of string to object (a mapping), or <see cref="List{T}"/> of object (a sequence).
    /// </summary>
    internal static class UnityYamlParser
    {
        private static readonly Regex DocumentHeader = new(@"^--- !u!(\d+) &(-?\d+)( stripped)?", RegexOptions.Compiled);

        public static List<GitYamlDocument> Parse(string yaml)
        {
            var documents = new List<GitYamlDocument>();
            if (string.IsNullOrEmpty(yaml)) return documents;

            var lines = yaml.Replace("\r\n", "\n").Split('\n');
            var index = 0;

            while (index < lines.Length)
            {
                var header = DocumentHeader.Match(lines[index]);
                if (!header.Success) { index++; continue; }

                var document = new GitYamlDocument
                {
                    ClassId = int.Parse(header.Groups[1].Value),
                    FileId = long.Parse(header.Groups[2].Value),
                    Stripped = header.Groups[3].Success,
                };
                index++;

                // The line right after the header is "<TypeName>:" — everything indented under it
                // is that document's actual data, wrapped one level deep.
                SkipBlank(lines, ref index);
                if (index < lines.Length && CountIndent(lines[index]) == 0 && !DocumentHeader.IsMatch(lines[index]))
                {
                    var typeLine = lines[index].TrimEnd();
                    var colon = typeLine.IndexOf(':');
                    document.TypeName = colon > 0 ? typeLine.Substring(0, colon).Trim() : typeLine.Trim();
                    index++;

                    if (ParseNode(lines, ref index) is Dictionary<string, object> fields)
                        document.Fields = fields;
                }

                documents.Add(document);
            }

            return documents;
        }

        // ------------------------------------------------------------------------- block parsing --

        private static object ParseNode(string[] lines, ref int index)
        {
            SkipBlank(lines, ref index);
            if (index >= lines.Length) return null;
            return ParseNodeAt(lines, ref index, CountIndent(lines[index]));
        }

        private static object ParseNodeAt(string[] lines, ref int index, int indent)
        {
            SkipBlank(lines, ref index);
            if (index >= lines.Length || CountIndent(lines[index]) != indent) return null;

            return IsSequenceLine(lines[index].TrimStart())
                ? ParseSequence(lines, ref index, indent)
                : ParseMapping(lines, ref index, indent);
        }

        private static List<object> ParseSequence(string[] lines, ref int index, int indent)
        {
            var list = new List<object>();
            while (true)
            {
                SkipBlank(lines, ref index);
                if (index >= lines.Length || CountIndent(lines[index]) != indent) break;
                var trimmed = lines[index].TrimStart();
                if (!IsSequenceLine(trimmed)) break;

                var rest = trimmed.Length > 1 ? trimmed.Substring(1).TrimStart() : "";
                index++;

                if (rest.Length == 0)
                {
                    list.Add(ParseNode(lines, ref index));
                }
                else if (rest[0] is '{' or '[' or '"' or '\'')
                {
                    list.Add(ParseScalar(rest));
                }
                else
                {
                    var colon = FindKeyColon(rest);
                    if (colon >= 0)
                    {
                        var key = rest.Substring(0, colon).Trim();
                        var value = rest.Substring(colon + 1).Trim();
                        // Unity only ever emits single-key item maps for the cases this tool reads
                        // (`- component: {fileID: X}`) — a genuine multi-key list item would need
                        // its remaining keys read from further indented lines, which isn't needed here.
                        list.Add(new Dictionary<string, object> { [key] = value.Length == 0 ? null : ParseScalar(value) });
                    }
                    else
                    {
                        list.Add(ParseScalar(rest));
                    }
                }
            }
            return list;
        }

        private static Dictionary<string, object> ParseMapping(string[] lines, ref int index, int indent)
        {
            var map = new Dictionary<string, object>();
            while (true)
            {
                SkipBlank(lines, ref index);
                if (index >= lines.Length || CountIndent(lines[index]) != indent) break;
                var trimmed = lines[index].TrimStart();
                if (IsSequenceLine(trimmed) || DocumentHeader.IsMatch(lines[index])) break;

                var colon = FindKeyColon(trimmed);
                if (colon < 0) break;
                var key = trimmed.Substring(0, colon).Trim();
                var valuePart = trimmed.Substring(colon + 1).Trim();
                index++;

                if (valuePart.Length > 0)
                {
                    map[key] = ParseScalar(valuePart);
                    continue;
                }

                // Nested value: either an indented child block, or a sequence written at the SAME
                // indent as this key (Unity's style for list-valued fields like `m_Component:`).
                SkipBlank(lines, ref index);
                if (index < lines.Length)
                {
                    var childIndent = CountIndent(lines[index]);
                    if (childIndent > indent)
                    {
                        map[key] = ParseNodeAt(lines, ref index, childIndent);
                        continue;
                    }
                    if (childIndent == indent && IsSequenceLine(lines[index].TrimStart()))
                    {
                        map[key] = ParseSequence(lines, ref index, indent);
                        continue;
                    }
                }
                map[key] = null;
            }
            return map;
        }

        // ------------------------------------------------------------------------- scalars/flow --

        private static object ParseScalar(string s)
        {
            s = s.Trim();
            if (s.Length == 0) return null;
            if (s[0] == '{') return ParseFlowMap(s);
            if (s[0] == '[') return ParseFlowList(s);
            if (s.Length >= 2 && (s[0] == '"' || s[0] == '\'') && s[^1] == s[0]) return s.Substring(1, s.Length - 2);
            return s;
        }

        private static Dictionary<string, object> ParseFlowMap(string s)
        {
            var map = new Dictionary<string, object>();
            var inner = StripOuter(s, '{', '}');
            if (inner.Length == 0) return map;
            foreach (var part in SplitTopLevel(inner, ','))
            {
                var colon = part.IndexOf(':');
                if (colon < 0) continue;
                map[part.Substring(0, colon).Trim()] = ParseScalar(part.Substring(colon + 1));
            }
            return map;
        }

        private static List<object> ParseFlowList(string s)
        {
            var list = new List<object>();
            var inner = StripOuter(s, '[', ']');
            if (inner.Length == 0) return list;
            foreach (var part in SplitTopLevel(inner, ','))
                list.Add(ParseScalar(part));
            return list;
        }

        private static string StripOuter(string s, char open, char close)
        {
            s = s.Trim();
            if (s.Length >= 2 && s[0] == open && s[^1] == close) s = s.Substring(1, s.Length - 2);
            return s.Trim();
        }

        /// <summary>Splits on <paramref name="separator"/> at depth 0 — skips separators inside
        /// nested `{}`/`[]` or quotes, so a flow value's own commas don't get sliced.</summary>
        private static List<string> SplitTopLevel(string s, char separator)
        {
            var parts = new List<string>();
            var depth = 0;
            var quote = (char)0;
            var start = 0;
            for (var i = 0; i < s.Length; i++)
            {
                var c = s[i];
                if (quote != 0)
                {
                    if (c == quote) quote = (char)0;
                    continue;
                }
                if (c == '"' || c == '\'') { quote = c; continue; }
                if (c is '{' or '[') { depth++; continue; }
                if (c is '}' or ']') { depth--; continue; }
                if (c == separator && depth == 0)
                {
                    parts.Add(s.Substring(start, i - start));
                    start = i + 1;
                }
            }
            parts.Add(s.Substring(start));
            return parts;
        }

        // ------------------------------------------------------------------------- line helpers --

        private static bool IsSequenceLine(string trimmed) => trimmed == "-" || trimmed.StartsWith("- ");

        private static int CountIndent(string line)
        {
            var i = 0;
            while (i < line.Length && line[i] == ' ') i++;
            return i;
        }

        private static void SkipBlank(string[] lines, ref int index)
        {
            while (index < lines.Length && string.IsNullOrWhiteSpace(lines[index])) index++;
        }

        /// <summary>Finds the colon separating a mapping key from its value — the first colon
        /// followed by a space or end-of-line, ignoring colons inside a quoted key (Unity never
        /// quotes keys in practice, but a value's stray colon inside a string is still possible).</summary>
        private static int FindKeyColon(string s)
        {
            var quote = (char)0;
            for (var i = 0; i < s.Length; i++)
            {
                var c = s[i];
                if (quote != 0)
                {
                    if (c == quote) quote = (char)0;
                    continue;
                }
                if (c == '"' || c == '\'') { quote = c; continue; }
                if (c == ':' && (i == s.Length - 1 || s[i + 1] == ' ')) return i;
            }
            return -1;
        }
    }
}
