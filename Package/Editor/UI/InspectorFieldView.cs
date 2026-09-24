using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using UnityGitTool.Diff;
using UnityGitTool.Yaml;

namespace UnityGitTool
{
    internal enum RowChangeKind { None, Added, Modified, Removed }

    /// <summary>Renders one component/document (and, recursively, its field tree) in the
    /// Inspector-style before/after columns. Dictionaries whose keys are exactly {x,y,z[,w]}
    /// or {r,g,b,a} render as Inspector-style vector/color rows (Unity always serializes
    /// Vector/Quaternion/Color with those exact key sets); {fileID[,guid,type]} renders as a
    /// compact object reference; everything else is a generic nested group.</summary>
    internal static class InspectorFieldView
    {
        // Same bookkeeping fields UnityYamlDiff ignores when diffing — keeping both in sync
        // matters: if the diff engine ever flagged one of these as changed, the UI would show
        // a "modified" badge with no visible row, since they're always hidden here.
        private static readonly HashSet<string> SkipKeys = UnityYamlDiff.IgnoredKeys;

        private static readonly string[] Vec3Keys = { "x", "y", "z" };
        private static readonly string[] Vec4Keys = { "x", "y", "z", "w" };
        private static readonly string[] ColorKeys = { "r", "g", "b", "a" };

        // Empty-string values still need a visible row — without an explicit min-height a
        // Label with no text collapses to zero height instead of showing an empty box.
        private const float RowMinHeight = 18f;

        private static void StyleFoldoutLabel(Foldout foldout)
        {
            var label = foldout.Q<Toggle>()?.Q<Label>();
            if (label == null) return;
            label.style.fontSize = 10;
            label.style.opacity = 0.6f;
            label.style.unityFontStyleAndWeight = FontStyle.Bold;
        }

        /// <summary>Whether a component differs between revisions at all — either the whole
        /// component was added/removed, or it survived but has field-level changes inside.</summary>
        public static bool HasChanges(RowChangeKind kind, Dictionary<string, FieldChange> changesByPath) =>
            kind != RowChangeKind.None || changesByPath is { Count: > 0 };

        /// <summary>Every custom script serializes as a generic "MonoBehaviour" document — the
        /// actual class name isn't stored in the YAML at all, only a guid pointing at the script
        /// asset (<c>m_Script</c>). Resolve that guid against the current project's
        /// AssetDatabase to show the real script name instead of "MonoBehaviour" on every one.</summary>
        private static string ResolveComponentTitle(UnityYamlDocument doc)
        {
            if (doc.RootKey != "MonoBehaviour") return doc.RootKey;

            var path = ResolveScriptPath(doc);
            return string.IsNullOrEmpty(path) ? "Missing Script" : System.IO.Path.GetFileNameWithoutExtension(path);
        }

        /// <summary>Resolves a MonoBehaviour document's script asset path via its <c>m_Script</c>
        /// guid — shared with <see cref="DiffIcons"/> so the header title and its icon agree on
        /// which script they're describing. Null for anything that isn't a MonoBehaviour, or
        /// whose script guid no longer resolves to an asset (deleted/missing script).</summary>
        public static string ResolveScriptPath(UnityYamlDocument doc)
        {
            if (doc == null || doc.RootKey != "MonoBehaviour") return null;

            if (doc.Fields.TryGetValue("m_Script", out var script) &&
                script is Dictionary<string, object> scriptMap &&
                scriptMap.TryGetValue("guid", out var guidValue))
            {
                var guid = guidValue?.ToString();
                var path = string.IsNullOrEmpty(guid) ? null : AssetDatabase.GUIDToAssetPath(guid);
                if (!string.IsNullOrEmpty(path)) return path;
            }

            return null;
        }

        // A pale, direction-agnostic tint — distinct from the diff-status colors so it never
        // gets confused with "this row changed", just "your mouse is over this row (and its
        // counterpart in the other column)".
        private static readonly Color RowHoverBg = new(0.3f, 0.55f, 1f, 0.12f);

        public static VisualElement BuildComponentSection(
            UnityYamlDocument doc, RowChangeKind kind, Dictionary<string, FieldChange> changesByPath, bool onlyChanges,
            Dictionary<string, List<VisualElement>> rowHoverRegistry)
        {
            var hasFieldChanges = kind == RowChangeKind.None && changesByPath is { Count: > 0 };

            Color? accent = kind switch
            {
                RowChangeKind.Added => DiffTheme.AddedColor,
                RowChangeKind.Removed => DiffTheme.RemovedColor,
                _ when hasFieldChanges => DiffTheme.ModifiedColor,
                _ => null,
            };
            var baseBg = new Color(0.5f, 0.5f, 0.5f, 0.05f);
            var borderColor = accent ?? new Color(0, 0, 0, 0.2f);
            const int borderWidth = 1;

            var container = new VisualElement
            {
                style =
                {
                    marginBottom = 10, marginLeft = 8, marginRight = 8, paddingBottom = 6,
                    backgroundColor = baseBg,
                    borderTopWidth = borderWidth, borderBottomWidth = borderWidth, borderLeftWidth = borderWidth, borderRightWidth = borderWidth,
                    borderTopColor = borderColor, borderBottomColor = borderColor,
                    borderLeftColor = borderColor, borderRightColor = borderColor,
                    borderTopLeftRadius = 4, borderTopRightRadius = 4, borderBottomLeftRadius = 4, borderBottomRightRadius = 4,
                    overflow = Overflow.Hidden,
                },
            };

            var headerRow = new VisualElement
            {
                style =
                {
                    flexDirection = FlexDirection.Row, alignItems = Align.Center,
                    paddingLeft = 6, paddingTop = 3, paddingBottom = 3,
                },
            };

            var foldArrow = new Label("▾") { style = { fontSize = 9, opacity = 0.6f, marginRight = 4, width = 10 } };
            headerRow.Add(foldArrow);

            headerRow.Add(DiffIcons.BuildIcon(DiffIcons.GetDocumentIcon(doc)));

            var titleLabel = new Label(ResolveComponentTitle(doc)) { style = { unityFontStyleAndWeight = FontStyle.Bold, fontSize = 11 } };
            if (accent.HasValue) titleLabel.style.color = accent.Value;
            headerRow.Add(titleLabel);
            if (kind != RowChangeKind.None)
            {
                headerRow.Add(new Label(kind == RowChangeKind.Added ? "component added" : "component removed")
                {
                    style = { fontSize = 9, opacity = 0.7f, color = accent!.Value, marginLeft = 6 },
                });
            }
            else if (hasFieldChanges)
            {
                headerRow.Add(new Label("modified")
                {
                    style = { fontSize = 9, opacity = 0.7f, color = accent!.Value, marginLeft = 6 },
                });
            }

            // A stripped placeholder is a distinct overridden object inside a nested prefab
            // instance (see PrefabOverrideHydrator) — several can share the same component type
            // under one group, so flag it to avoid reading as duplicate/empty sections. When its
            // own source reference no longer resolves to anything (see SourcePrefabResolver.
            // IsUnresolvedReference), it's likely stale project data — a dangling override left
            // over from a nested prefab that got restructured/unpacked without this override ever
            // being cleaned up — so call that out distinctly instead of the generic label, since
            // it reads very differently ("ignore/clean this up" vs. "this is legitimate content").
            if (doc.Stripped)
            {
                var strippedLabel = SourcePrefabResolver.IsUnresolvedReference(doc)
                    ? new Label("unresolved override — target no longer exists")
                    {
                        style =
                        {
                            fontSize = 9, marginLeft = 6, opacity = 0.85f,
                            color = DiffTheme.RemovedColor, unityFontStyleAndWeight = FontStyle.Bold,
                        },
                    }
                    : new Label("nested prefab override") { style = { fontSize = 9, opacity = 0.5f, marginLeft = 6 } };

                headerRow.Add(strippedLabel);
            }

            container.Add(headerRow);

            // A wholly added/removed component has no per-field diff data (there is nothing
            // to compare against on the other side), so every prop is force-tinted to match
            // instead of looking up an (empty) changesByPath.
            RowChangeKind? forcedKind = kind is RowChangeKind.Added or RowChangeKind.Removed ? kind : null;

            var body = new VisualElement();
            BuildFieldRows(body, doc.Fields, "", changesByPath ?? new Dictionary<string, FieldChange>(), forcedKind, onlyChanges, doc.FileId, rowHoverRegistry);
            container.Add(body);

            headerRow.RegisterCallback<ClickEvent>(_ =>
            {
                var collapsed = body.style.display == DisplayStyle.None;
                body.style.display = collapsed ? DisplayStyle.Flex : DisplayStyle.None;
                container.style.paddingBottom = collapsed ? 6 : 0;
                foldArrow.text = collapsed ? "▾" : "▸";
            });

            return container;
        }

        /// <summary>Adds every non-skipped field as a row and reports whether at least one was
        /// added — callers use that to drop a nested group entirely when it turns out empty
        /// under the "only changes" filter.</summary>
        private static bool BuildFieldRows(
            VisualElement parent, Dictionary<string, object> fields, string basePath, Dictionary<string, FieldChange> changesByPath,
            RowChangeKind? forcedKind, bool onlyChanges, long fileId, Dictionary<string, List<VisualElement>> rowHoverRegistry)
        {
            var any = false;
            foreach (var key in fields.Keys.OrderBy(k => k))
            {
                if (SkipKeys.Contains(key)) continue;
                var path = basePath.Length == 0 ? key : $"{basePath}/{key}";
                any |= BuildValueRow(parent, ObjectNames.NicifyVariableName(key), path, fields[key], changesByPath, forcedKind, onlyChanges, fileId, rowHoverRegistry);
            }
            return any;
        }

        private static bool BuildValueRow(
            VisualElement parent, string label, string path, object value, Dictionary<string, FieldChange> changesByPath,
            RowChangeKind? forcedKind, bool onlyChanges, long fileId, Dictionary<string, List<VisualElement>> rowHoverRegistry)
        {
            if (value is Dictionary<string, object> map)
            {
                var keys = new HashSet<string>(map.Keys);

                if (keys.SetEquals(Vec3Keys) || keys.SetEquals(Vec4Keys))
                {
                    var axes = keys.SetEquals(Vec4Keys) ? Vec4Keys : Vec3Keys;
                    var vectorKind = forcedKind ?? (axes.Any(axis => LookupChangeKind($"{path}/{axis}", changesByPath) != RowChangeKind.None) ? RowChangeKind.Modified : RowChangeKind.None);
                    if (onlyChanges && vectorKind == RowChangeKind.None) return false;
                    var row = BuildVectorRow(label, map, path, changesByPath, axes, forcedKind);
                    RegisterRowHover(rowHoverRegistry, fileId, path, row);
                    parent.Add(row);
                    return true;
                }

                if (keys.SetEquals(ColorKeys))
                {
                    var colorKind = ResolveColorKind(path, changesByPath, forcedKind);
                    if (onlyChanges && colorKind == RowChangeKind.None) return false;
                    var row = BuildColorRow(label, map, colorKind);
                    RegisterRowHover(rowHoverRegistry, fileId, path, row);
                    parent.Add(row);
                    return true;
                }

                if (map.ContainsKey("fileID") && map.Count <= 3)
                {
                    var refKind = forcedKind ?? LookupChangeKind(path, changesByPath);
                    if (onlyChanges && refKind == RowChangeKind.None) return false;
                    var fileId2 = map["fileID"]?.ToString() ?? "0";
                    var text = fileId2 == "0" ? "None" : map.TryGetValue("guid", out var guid) ? $"fileID: {fileId2}, guid: {guid}" : $"fileID: {fileId2}";
                    var row = BuildSimpleRow(label, text, path, changesByPath, forcedKind);
                    RegisterRowHover(rowHoverRegistry, fileId, path, row);
                    parent.Add(row);
                    return true;
                }

                var section = new VisualElement { style = { marginLeft = 10 } };
                var sectionTitle = new Label(label) { style = { unityFontStyleAndWeight = FontStyle.Bold, fontSize = 10, opacity = 0.6f, marginTop = 4 } };
                section.Add(sectionTitle);
                var hasContent = BuildFieldRows(section, map, path, changesByPath, forcedKind, onlyChanges, fileId, rowHoverRegistry);
                if (onlyChanges && !hasContent) return false;
                parent.Add(section);
                return true;
            }

            if (value is List<object> list)
            {
                var foldout = new Foldout { text = $"{label} [{list.Count}]", value = true, style = { marginLeft = 10, marginTop = 4 } };
                StyleFoldoutLabel(foldout);
                var hasContent = false;
                for (var i = 0; i < list.Count; i++)
                {
                    hasContent |= BuildValueRow(foldout, $"[{i}]", $"{path}/[{i}]", list[i], changesByPath, forcedKind, onlyChanges, fileId, rowHoverRegistry);
                }
                if (onlyChanges && !hasContent) return false;
                parent.Add(foldout);
                return true;
            }

            var kind = forcedKind ?? LookupChangeKind(path, changesByPath);
            if (onlyChanges && kind == RowChangeKind.None) return false;
            var simpleRow = BuildSimpleRow(label, value?.ToString() ?? "", path, changesByPath, forcedKind);
            RegisterRowHover(rowHoverRegistry, fileId, path, simpleRow);
            parent.Add(simpleRow);
            return true;
        }

        /// <summary>Registers a leaf row so hovering it also highlights its counterpart in the
        /// other column (same document fileId + field path). The callback closes over the same
        /// list instance stored in the registry, so it still sees the twin row added later by
        /// the second column's render pass, even though this row's own callback was wired first.</summary>
        private static void RegisterRowHover(Dictionary<string, List<VisualElement>> registry, long fileId, string path, VisualElement row)
        {
            if (registry == null) return;

            var key = $"{fileId}:{path}";
            if (!registry.TryGetValue(key, out var group))
            {
                registry[key] = group = new List<VisualElement>();
            }
            group.Add(row);

            row.RegisterCallback<MouseEnterEvent>(_ => SetRowHighlighted(group, true));
            row.RegisterCallback<MouseLeaveEvent>(_ => SetRowHighlighted(group, false));
        }

        private static void SetRowHighlighted(List<VisualElement> rows, bool highlighted)
        {
            foreach (var row in rows)
            {
                row.style.backgroundColor = highlighted ? RowHoverBg : Color.clear;
            }
        }

        private static VisualElement BuildSimpleRow(string label, string value, string path, Dictionary<string, FieldChange> changesByPath, RowChangeKind? forcedKind)
        {
            var (bg, _) = RowColors(forcedKind ?? LookupChangeKind(path, changesByPath));

            var wrapper = new VisualElement
            {
                style =
                {
                    flexDirection = FlexDirection.Row, alignItems = Align.Center,
                    paddingLeft = 6, paddingRight = 6, paddingTop = 3, paddingBottom = 3,
                },
            };
            wrapper.Add(new Label(label) { style = { width = 140, opacity = 0.75f, fontSize = 11 } });
            wrapper.Add(new Label(value)
            {
                style =
                {
                    flexGrow = 1, fontSize = 11, minHeight = RowMinHeight, paddingLeft = 6, paddingRight = 6, paddingTop = 2, paddingBottom = 2,
                    backgroundColor = bg == Color.clear ? new Color(0.5f, 0.5f, 0.5f, 0.08f) : bg,
                    borderTopLeftRadius = 3, borderTopRightRadius = 3, borderBottomLeftRadius = 3, borderBottomRightRadius = 3,
                },
            });
            return wrapper;
        }

        private static VisualElement BuildVectorRow(string label, Dictionary<string, object> map, string path, Dictionary<string, FieldChange> changesByPath, string[] axes, RowChangeKind? forcedKind)
        {
            var wrapper = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, paddingLeft = 6, paddingTop = 3, paddingBottom = 3 } };
            wrapper.Add(new Label(label) { style = { width = 140, opacity = 0.75f, fontSize = 11 } });

            foreach (var axis in axes)
            {
                var axisKind = forcedKind ?? LookupChangeKind($"{path}/{axis}", changesByPath);
                var (axisBg, _) = RowColors(axisKind);
                var axisChanged = axisKind != RowChangeKind.None;
                var box = new VisualElement
                {
                    style =
                    {
                        flexDirection = FlexDirection.Row, alignItems = Align.Center, marginRight = 6, minHeight = RowMinHeight,
                        backgroundColor = axisChanged ? axisBg : new Color(0.5f, 0.5f, 0.5f, 0.08f),
                        borderTopLeftRadius = 3, borderTopRightRadius = 3, borderBottomLeftRadius = 3, borderBottomRightRadius = 3,
                        paddingLeft = 4, paddingRight = 4, paddingTop = 2, paddingBottom = 2,
                    },
                };
                box.Add(new Label(axis.ToUpperInvariant()) { style = { fontSize = 9, opacity = 0.5f, marginRight = 3 } });
                box.Add(new Label(map.TryGetValue(axis, out var v) ? v?.ToString() ?? "" : "") { style = { fontSize = 11 } });
                wrapper.Add(box);
            }

            return wrapper;
        }

        private static RowChangeKind ResolveColorKind(string path, Dictionary<string, FieldChange> changesByPath, RowChangeKind? forcedKind) =>
            forcedKind ?? (LookupChangeKind(path, changesByPath) != RowChangeKind.None
                ? RowChangeKind.Modified
                : (ColorKeys.Any(axis => LookupChangeKind($"{path}/{axis}", changesByPath) != RowChangeKind.None) ? RowChangeKind.Modified : RowChangeKind.None));

        private static VisualElement BuildColorRow(string label, Dictionary<string, object> map, RowChangeKind kind)
        {
            var (bg, _) = RowColors(kind);

            var wrapper = new VisualElement
            {
                style =
                {
                    flexDirection = FlexDirection.Row, alignItems = Align.Center,
                    paddingLeft = 6, paddingRight = 6, paddingTop = 3, paddingBottom = 3,
                },
            };
            wrapper.Add(new Label(label) { style = { width = 140, opacity = 0.75f, fontSize = 11 } });

            var valueBox = new VisualElement
            {
                style =
                {
                    flexDirection = FlexDirection.Row, alignItems = Align.Center, flexGrow = 1, minHeight = RowMinHeight,
                    paddingLeft = 6, paddingRight = 6, paddingTop = 2, paddingBottom = 2,
                    backgroundColor = bg == Color.clear ? new Color(0.5f, 0.5f, 0.5f, 0.08f) : bg,
                    borderTopLeftRadius = 3, borderTopRightRadius = 3, borderBottomLeftRadius = 3, borderBottomRightRadius = 3,
                },
            };
            wrapper.Add(valueBox);

            var swatch = new VisualElement
            {
                style =
                {
                    width = 40, height = 16, borderTopLeftRadius = 2, borderTopRightRadius = 2, borderBottomLeftRadius = 2, borderBottomRightRadius = 2,
                    borderTopWidth = 1, borderBottomWidth = 1, borderLeftWidth = 1, borderRightWidth = 1,
                    borderTopColor = new Color(0, 0, 0, 0.3f), borderBottomColor = new Color(0, 0, 0, 0.3f),
                    borderLeftColor = new Color(0, 0, 0, 0.3f), borderRightColor = new Color(0, 0, 0, 0.3f),
                    marginRight = 6,
                },
            };
            float F(string k) => map.TryGetValue(k, out var v) && float.TryParse(v?.ToString(), out var f) ? f : 0f;
            swatch.style.backgroundColor = new Color(F("r"), F("g"), F("b"), 1f);
            valueBox.Add(swatch);
            valueBox.Add(new Label($"({map.GetValueOrDefault("r")}, {map.GetValueOrDefault("g")}, {map.GetValueOrDefault("b")}, {map.GetValueOrDefault("a")})")
            {
                style = { fontSize = 10, opacity = 0.7f },
            });
            return wrapper;
        }

        private static (Color bg, Color fg) RowColors(RowChangeKind kind) => kind switch
        {
            RowChangeKind.Added => (DiffTheme.AddedBg, DiffTheme.AddedColor),
            RowChangeKind.Modified => (DiffTheme.ModifiedBg, DiffTheme.ModifiedColor),
            RowChangeKind.Removed => (DiffTheme.RemovedBg, DiffTheme.RemovedColor),
            _ => (Color.clear, Color.clear),
        };

        private static RowChangeKind LookupChangeKind(string path, Dictionary<string, FieldChange> changesByPath)
        {
            if (!changesByPath.TryGetValue(path, out var change))
            {
                return RowChangeKind.None;
            }

            return change.Type switch
            {
                FieldChangeType.Added => RowChangeKind.Added,
                FieldChangeType.Removed => RowChangeKind.Removed,
                _ => RowChangeKind.Modified,
            };
        }
    }
}
