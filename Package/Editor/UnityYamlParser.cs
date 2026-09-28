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

        /// <summary>Source-line range (start inclusive, end exclusive, matching the parser's own
        /// index semantics) of each top-level field under this document's <see cref="TypeName"/> key
        /// — <see cref="UnityYamlWriter"/>'s only hook back into the raw text. Not populated for
        /// nested values (a Vector3's x/y/z, a list item, ...); write-back only ever replaces a whole
        /// top-level field's block, never reaches inside one.</summary>
        public Dictionary<string, LineSpan> FieldSpans = new();

        /// <summary>Line index one past this document's last top-level field (or right after the
        /// `&lt;TypeName&gt;:` line if it has none) — where a brand-new field (one that exists on the
        /// other side but not here) gets appended by <see cref="UnityYamlWriter"/>. Field order inside
        /// a YAML mapping has no semantic meaning to Unity, so appending instead of preserving the
        /// other side's original position is safe.</summary>
        public int BodyEndLine;

        /// <summary>Line index of this document's own `--- !u!&lt;classId&gt; &amp;&lt;fileId&gt;`
        /// header — together with <see cref="BodyEndLine"/>, the document's whole span
        /// [<see cref="HeaderLine"/>, <see cref="BodyEndLine"/>) that <see cref="UnityYamlWriter"/>
        /// removes entirely to delete this object (a whole GameObject or component, not just one of
        /// its fields).</summary>
        public int HeaderLine;
    }

    /// <summary>A half-open source-line range: <see cref="Start"/> inclusive, <see cref="End"/>
    /// exclusive — same convention as the parser's own <c>index</c> cursor, so a span can be sliced
    /// directly as <c>lines[Start..End]</c>.</summary>
    internal readonly struct LineSpan
    {
        public readonly int Start;
        public readonly int End;
        public LineSpan(int start, int end) { Start = start; End = end; }
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
                    HeaderLine = index,
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

                    SkipBlank(lines, ref index);
                    // A document's own body is always a mapping in practice (Unity never emits a bare
                    // sequence directly under "<TypeName>:") — only that shape gets span tracking; the
                    // fallback keeps parsing correct for anything else, just without write-back support.
                    if (index < lines.Length && !IsSequenceLine(lines[index].TrimStart()))
                    {
                        var bodyIndent = CountIndent(lines[index]);
                        var (fields, spans) = ParseMappingWithSpans(lines, ref index, bodyIndent);
                        document.Fields = fields;
                        document.FieldSpans = spans;
                    }
                    else if (ParseNode(lines, ref index) is Dictionary<string, object> fallbackFields)
                    {
                        document.Fields = fallbackFields;
                    }
                    document.BodyEndLine = index;
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
                        var map = new Dictionary<string, object> { [key] = value.Length == 0 ? null : ParseScalar(value) };

                        // A genuine multi-key list item (e.g. PrefabInstance's own m_Modifications —
                        // `- target: {...}` followed by sibling `propertyPath:`/`value:`/`objectReference:`
                        // lines indented two spaces past the `-`) continues as more keys of this SAME
                        // mapping, not a new sequence item or the enclosing mapping's next field. Without
                        // this, the outer loop above sees the first continuation line's indent (> this
                        // sequence's own indent) not matching `indent`, breaks immediately, and the
                        // document is considered over right there — silently truncating everything after
                        // it (component/GameObject content this list's OWNER document — the whole
                        // PrefabInstance — needs preserved when copied verbatim, e.g. by
                        // UnityYamlWriter's restore path).
                        SkipBlank(lines, ref index);
                        if (index < lines.Length && CountIndent(lines[index]) > indent && !IsSequenceLine(lines[index].TrimStart()))
                        {
                            var itemIndent = CountIndent(lines[index]);
                            foreach (var kv in ParseMapping(lines, ref index, itemIndent))
                                map[kv.Key] = kv.Value;
                        }

                        list.Add(map);
                    }
                    else
                    {
                        list.Add(ParseScalar(rest));
                    }
                }
            }
            return list;
        }

        /// <summary>Same walk as <see cref="ParseMapping"/>, used only for a document's top-level
        /// field block, additionally recording each key's <see cref="LineSpan"/>. Deliberately a
        /// separate method rather than a flag on <see cref="ParseMapping"/> itself: nested mappings
        /// (inside a Vector3, a list item, ...) have no meaningful span to record — write-back only
        /// ever replaces a whole top-level field — so duplicating this one level keeps that general,
        /// widely-reused recursive parser untouched and exactly as before.</summary>
        private static (Dictionary<string, object> Fields, Dictionary<string, LineSpan> Spans) ParseMappingWithSpans(
            string[] lines, ref int index, int indent)
        {
            var map = new Dictionary<string, object>();
            var spans = new Dictionary<string, LineSpan>();
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
                var fieldStart = index;
                index++;

                if (valuePart.Length > 0)
                {
                    map[key] = ParseScalar(valuePart);
                    spans[key] = new LineSpan(fieldStart, index);
                    continue;
                }

                SkipBlank(lines, ref index);
                if (index < lines.Length)
                {
                    var childIndent = CountIndent(lines[index]);
                    if (childIndent > indent)
                    {
                        // A PrefabInstance's m_Modification block gets its own span-tracked parse
                        // (merged in under "m_Modification.<subkey>") rather than the plain span-less
                        // ParseNodeAt every other nested mapping gets — see UnityYamlWriter's
                        // write-back, which needs to splice a single override or removal-list entry
                        // independently, not just the whole m_Modification blob at once.
                        if (key == "m_Modification")
                        {
                            var (subFields, subSpans) = ParseModificationBlock(lines, ref index, childIndent);
                            map[key] = subFields;
                            spans[key] = new LineSpan(fieldStart, index);
                            foreach (var (subKey, subSpan) in subSpans)
                                spans[$"{key}.{subKey}"] = subSpan;
                            continue;
                        }
                        map[key] = ParseNodeAt(lines, ref index, childIndent);
                        spans[key] = new LineSpan(fieldStart, index);
                        continue;
                    }
                    if (childIndent == indent && IsSequenceLine(lines[index].TrimStart()))
                    {
                        map[key] = ParseSequence(lines, ref index, indent);
                        spans[key] = new LineSpan(fieldStart, index);
                        continue;
                    }
                }
                map[key] = null;
                spans[key] = new LineSpan(fieldStart, index);
            }
            return (map, spans);
        }

        /// <summary>Same shape as <see cref="ParseMappingWithSpans"/>, used only for a PrefabInstance's
        /// <c>m_Modification:</c> block — every immediate child field (<c>m_TransformParent</c>,
        /// <c>m_RemovedComponents</c>, <c>m_RemovedGameObjects</c>, <c>m_AddedGameObjects</c>,
        /// <c>m_AddedComponents</c>, <c>m_Modifications</c>) gets its own span so
        /// <see cref="UnityYamlWriter"/> can edit one of them without touching the others, unlike a
        /// plain nested value (see <see cref="ParseNodeAt"/>). <c>m_Modifications</c> itself gets
        /// further special-cased (see <see cref="ParseModificationEntries"/>): each override entry
        /// inside it is a distinct editable unit (one property override), not a single list to
        /// splice as a whole.</summary>
        private static (Dictionary<string, object> Fields, Dictionary<string, LineSpan> Spans) ParseModificationBlock(
            string[] lines, ref int index, int indent)
        {
            var map = new Dictionary<string, object>();
            var spans = new Dictionary<string, LineSpan>();
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
                var fieldStart = index;
                index++;

                if (valuePart.Length > 0)
                {
                    map[key] = ParseScalar(valuePart);
                    spans[key] = new LineSpan(fieldStart, index);
                    continue;
                }

                SkipBlank(lines, ref index);
                if (index < lines.Length)
                {
                    var childIndent = CountIndent(lines[index]);
                    if (childIndent == indent && IsSequenceLine(lines[index].TrimStart()))
                    {
                        if (key == "m_Modifications")
                        {
                            var (items, entrySpans) = ParseModificationEntries(lines, ref index, indent);
                            map[key] = items;
                            spans[key] = new LineSpan(fieldStart, index);
                            foreach (var (entryKey, entrySpan) in entrySpans)
                                spans[$"{key}::{entryKey}"] = entrySpan;
                        }
                        else
                        {
                            map[key] = ParseSequence(lines, ref index, indent);
                            spans[key] = new LineSpan(fieldStart, index);
                        }
                        continue;
                    }
                    if (childIndent > indent)
                    {
                        map[key] = ParseNodeAt(lines, ref index, childIndent);
                        spans[key] = new LineSpan(fieldStart, index);
                        continue;
                    }
                }
                map[key] = null;
                spans[key] = new LineSpan(fieldStart, index);
            }
            return (map, spans);
        }

        /// <summary>Walks <c>m_Modifications</c>' own entries the same way <see cref="ParseSequence"/>
        /// does (including its "multi-key list item" continuation — see that method's own comment),
        /// but additionally records each entry's <see cref="LineSpan"/> under a key derived from its
        /// content (<c>target.fileID</c> + <c>propertyPath</c>) rather than its position, so the same
        /// key resolves independently on the A-side and B-side documents being diffed — an entry
        /// added, removed, or reordered between revisions still matches by identity, the same
        /// assumption <see cref="UnityYamlWriter"/>'s field-splice write-back already makes for a
        /// <see cref="MockRow.Key"/> lookup. An entry missing either half of that identity (malformed,
        /// or a shape this doesn't recognize) still parses into the list — just isn't independently
        /// addressable for write-back, same "best effort" fallback as everywhere else in this parser.</summary>
        private static (List<object> Items, List<(string Key, LineSpan Span)> EntrySpans) ParseModificationEntries(
            string[] lines, ref int index, int indent)
        {
            var list = new List<object>();
            var entrySpans = new List<(string, LineSpan)>();
            while (true)
            {
                SkipBlank(lines, ref index);
                if (index >= lines.Length || CountIndent(lines[index]) != indent) break;
                var trimmed = lines[index].TrimStart();
                if (!IsSequenceLine(trimmed)) break;

                var itemStart = index;
                var rest = trimmed.Length > 1 ? trimmed.Substring(1).TrimStart() : "";
                index++;

                var entry = new Dictionary<string, object>();
                if (rest.Length == 0)
                {
                    if (ParseNode(lines, ref index) is Dictionary<string, object> parsed) entry = parsed;
                }
                else
                {
                    var colon = FindKeyColon(rest);
                    if (colon >= 0)
                    {
                        var key = rest.Substring(0, colon).Trim();
                        var value = rest.Substring(colon + 1).Trim();
                        entry[key] = value.Length == 0 ? null : ParseScalar(value);

                        SkipBlank(lines, ref index);
                        if (index < lines.Length && CountIndent(lines[index]) > indent && !IsSequenceLine(lines[index].TrimStart()))
                        {
                            var itemIndent = CountIndent(lines[index]);
                            foreach (var kv in ParseMapping(lines, ref index, itemIndent))
                                entry[kv.Key] = kv.Value;
                        }
                    }
                }

                list.Add(entry);

                var targetId = entry.TryGetValue("target", out var targetRaw) && targetRaw is Dictionary<string, object> targetMap &&
                    targetMap.TryGetValue("fileID", out var fid) ? fid as string : null;
                var propertyPath = entry.TryGetValue("propertyPath", out var pp) ? pp as string : null;
                if (targetId != null && propertyPath != null)
                    entrySpans.Add(($"{targetId}::{propertyPath}", new LineSpan(itemStart, index)));
            }
            return (list, entrySpans);
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
