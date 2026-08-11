using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;
using VisualGitDiff.Diff;
using VisualGitDiff.Git;
using VisualGitDiff.Yaml;

namespace VisualGitDiff
{
    /// <summary>
    /// Main diff window: pick two revisions, see which files changed, then drill into
    /// GameObjects/documents for the selected file with an Inspector-style before/after
    /// view. Layout mirrors the UI mock (<see cref="VisualGitDiffMockWindow"/>) but every
    /// value here comes from the real git/parse/diff pipeline
    /// (<see cref="GitFileReader"/> / <see cref="UnityYamlParser"/> / <see cref="UnityYamlDiff"/>).
    /// Row rendering is delegated to <see cref="SelectableList{T}"/> (file/object lists) and
    /// <see cref="InspectorFieldView"/> (before/after field trees).
    /// </summary>
    public class VisualGitDiffWindow : EditorWindow
    {
        private RevisionMenu _revisionAPicker;
        private RevisionMenu _revisionBPicker;
        private Label _statusLabel;
        private VisualElement _statusBar;

        private Label _objectColumnTitle;
        private Label _beforeColumnTitle;
        private Label _afterColumnTitle;
        private ScrollView _beforeColumn;
        private ScrollView _afterColumn;

        private SelectableList<GitFileReader.ChangedFile> _fileList;
        private SelectableList<ObjectGroup> _objectList;

        private bool _onlyChanges;
        private bool _hideIncompatibleFiles = true;
        private bool _syncScroll;
        private bool _syncingScroll;
        private ObjectGroup _selectedGroup;

        private string _repoRoot;
        private List<GitFileReader.ChangedFile> _changedFiles = new();
        private List<ObjectGroup> _currentGroups = new();

        [MenuItem("Window/Visual Git Diff/Diff Window")]
        private static void Open()
        {
            var window = GetWindow<VisualGitDiffWindow>("Visual Git Diff");
            window.minSize = new Vector2(1040, 480);
        }

        private void CreateGUI()
        {
            _repoRoot = GitFileReader.FindRepositoryRoot(Application.dataPath);

            var root = rootVisualElement;
            root.Add(BuildToolbar());
            root.Add(BuildStatusBar());

            var splitOuter = new TwoPaneSplitView(0, 210, TwoPaneSplitViewOrientation.Horizontal) { style = { flexGrow = 1 } };
            root.Add(splitOuter);

            var filePane = new VisualElement { style = { flexGrow = 1 } };
            filePane.Add(BuildColumnTitle("Files"));
            _fileList = new SelectableList<GitFileReader.ChangedFile>(new ScrollView { style = { flexGrow = 1 } });
            filePane.Add(_fileList.Container);
            splitOuter.Add(filePane);

            var splitMid = new TwoPaneSplitView(0, 220, TwoPaneSplitViewOrientation.Horizontal) { style = { flexGrow = 1 } };
            splitOuter.Add(splitMid);

            var objectPane = new VisualElement { style = { flexGrow = 1 } };
            _objectColumnTitle = BuildColumnTitle("Objects");
            objectPane.Add(_objectColumnTitle);
            _objectList = new SelectableList<ObjectGroup>(new ScrollView { style = { flexGrow = 1 } });
            objectPane.Add(_objectList.Container);
            splitMid.Add(objectPane);

            var splitInner = new TwoPaneSplitView(0, 380, TwoPaneSplitViewOrientation.Horizontal) { style = { flexGrow = 1 } };
            splitMid.Add(splitInner);

            _beforeColumn = BuildInspectorColumn("Before", out var beforePane, out _beforeColumnTitle);
            splitInner.Add(beforePane);

            _afterColumn = BuildInspectorColumn("After", out var afterPane, out _afterColumnTitle);
            splitInner.Add(afterPane);

            SetupScrollSync();
        }

        /// <summary>Mirrors vertical scroll position between the Before/After columns while
        /// <see cref="_syncScroll"/> is on. Guarded by <see cref="_syncingScroll"/> since setting
        /// one column's offset triggers its own valueChanged, which would otherwise bounce
        /// straight back into the other column.</summary>
        private void SetupScrollSync()
        {
            _beforeColumn.verticalScroller.valueChanged += v => MirrorScroll(_afterColumn, v);
            _afterColumn.verticalScroller.valueChanged += v => MirrorScroll(_beforeColumn, v);
        }

        private void MirrorScroll(ScrollView target, float verticalValue)
        {
            if (!_syncScroll || _syncingScroll) return;
            _syncingScroll = true;
            target.scrollOffset = new Vector2(target.scrollOffset.x, verticalValue);
            _syncingScroll = false;
        }

        private static ScrollView BuildInspectorColumn(string title, out VisualElement pane, out Label titleLabel)
        {
            pane = new VisualElement { style = { flexGrow = 1 } };
            titleLabel = BuildColumnTitle(title);
            pane.Add(titleLabel);
            var scroll = new ScrollView { style = { flexGrow = 1, paddingTop = 4 } };
            pane.Add(scroll);
            return scroll;
        }

        private static Label BuildColumnTitle(string title) => new(title)
        {
            style =
            {
                unityFontStyleAndWeight = FontStyle.Bold, fontSize = 10, opacity = 0.6f,
                paddingLeft = 8, paddingTop = 6, paddingBottom = 4,
                borderBottomWidth = 1, borderBottomColor = new Color(0, 0, 0, 0.15f),
            },
        };

        private VisualElement BuildToolbar()
        {
            var toolbar = new Toolbar { style = { flexDirection = FlexDirection.Row, alignItems = Align.FlexEnd } };

            GitFileReader.TryListBranches(_repoRoot, out var branches, out _);
            GitFileReader.TryListRecentCommits(_repoRoot, 20, out var commits, out _);
            branches ??= new List<string>();
            commits ??= new List<GitFileReader.CommitInfo>();

            // Defaults straight to "what did I change so far" — HEAD vs the working tree —
            // since that's the most common thing to check before a commit; pick a specific
            // branch/commit on either side for a real revision-to-revision comparison instead.
            _revisionAPicker = new RevisionMenu("Before", "HEAD", branches, commits, _repoRoot);
            _revisionBPicker = new RevisionMenu("After", GitFileReader.WorkingTreeRevision, branches, commits, _repoRoot);

            var swap = new ToolbarButton(() =>
            {
                var a = _revisionAPicker.Value;
                _revisionAPicker.SetValue(_revisionBPicker.Value);
                _revisionBPicker.SetValue(a);
            })
            { text = "⇄", style = { marginBottom = 2 } };

            var compareButton = new ToolbarButton(OnCompareClicked) { text = "Compare", style = { marginBottom = 2 } };

            var onlyChangesToggle = new ToolbarToggle { text = "Only changes", value = _onlyChanges, style = { marginBottom = 2 } };
            onlyChangesToggle.RegisterValueChangedCallback(evt =>
            {
                _onlyChanges = evt.newValue;
                if (_selectedGroup != null) SelectObject(_selectedGroup);
            });

            var hideIncompatibleToggle = new ToolbarToggle { text = "Hide incompatible files", value = _hideIncompatibleFiles, style = { marginBottom = 2 } };
            hideIncompatibleToggle.RegisterValueChangedCallback(evt =>
            {
                _hideIncompatibleFiles = evt.newValue;
                RefreshFileList();
            });

            var syncScrollToggle = new ToolbarToggle { text = "Sync scroll", value = _syncScroll, style = { marginBottom = 2 } };
            syncScrollToggle.RegisterValueChangedCallback(evt => _syncScroll = evt.newValue);

            toolbar.Add(_revisionAPicker);
            toolbar.Add(_revisionBPicker);
            toolbar.Add(swap);
            toolbar.Add(compareButton);
            toolbar.Add(BuildToolbarSeparator());
            toolbar.Add(onlyChangesToggle);
            toolbar.Add(BuildToolbarSeparator());
            toolbar.Add(hideIncompatibleToggle);
            toolbar.Add(BuildToolbarSeparator());
            toolbar.Add(syncScrollToggle);
            return toolbar;
        }

        private static VisualElement BuildToolbarSeparator()
        {
            return new VisualElement
            {
                style =
                {
                    width = 1,
                    marginLeft = 4, marginRight = 4, marginBottom = 2,
                    backgroundColor = new Color(0, 0, 0, 0.2f),
                },
            };
        }

        private VisualElement BuildStatusBar()
        {
            _statusBar = new VisualElement
            {
                style =
                {
                    display = DisplayStyle.None,
                    flexDirection = FlexDirection.Row, alignItems = Align.Center,
                    paddingLeft = 12, paddingRight = 12, paddingTop = 4, paddingBottom = 4,
                    borderBottomWidth = 1, borderBottomColor = new Color(0, 0, 0, 0.2f),
                },
            };
            _statusLabel = new Label { style = { opacity = 0.8f } };
            _statusBar.Add(_statusLabel);
            return _statusBar;
        }

        private void SetStatus(string message)
        {
            _statusLabel.text = message;
            _statusBar.style.display = string.IsNullOrEmpty(message) ? DisplayStyle.None : DisplayStyle.Flex;
        }

        // -------------------------------------------------------------------------- files --

        private void OnCompareClicked()
        {
            if (_repoRoot == null)
            {
                SetStatus("Could not find a .git directory above the project.");
                return;
            }

            if (!GitFileReader.TryListChangedFilesWithStatus(_repoRoot, _revisionAPicker.Value, _revisionBPicker.Value, out var files, out var error))
            {
                SetStatus($"git diff failed: {error}");
                return;
            }

            _changedFiles = files;
            SetStatus(files.Count == 0 ? "No changed files." : "");

            RefreshColumnTitle(_beforeColumnTitle, "Before", _revisionAPicker.Value);
            RefreshColumnTitle(_afterColumnTitle, "After", _revisionBPicker.Value);

            _objectList.Clear();
            _beforeColumn.Clear();
            _afterColumn.Clear();

            RefreshFileList();
        }

        private void RefreshFileList()
        {
            var visibleFiles = _hideIncompatibleFiles
                ? _changedFiles.Where(f => UnityYamlParser.IsLikelyDiffable(f.Path)).ToList()
                : _changedFiles;

            _fileList.SetItems(visibleFiles, BuildFileRow, file => SelectFile(file.Path), FlexDirection.Column, Align.Stretch);

            if (visibleFiles.Count > 0)
            {
                _fileList.Select(visibleFiles[0]);
            }
            else
            {
                _objectList.Clear();
                _beforeColumn.Clear();
                _afterColumn.Clear();
            }
        }

        private void RefreshColumnTitle(Label titleLabel, string prefix, string revision)
        {
            if (GitFileReader.IsWorkingTree(revision))
            {
                titleLabel.text = $"{prefix} — Working Tree";
                return;
            }

            var text = $"{prefix} — {revision}";
            if (GitFileReader.TryGetCommitInfo(_repoRoot, revision, out var commit, out _))
            {
                text = $"{prefix} — {revision} ({commit.Hash} {commit.Subject})";
            }
            titleLabel.text = text;
        }

        private static VisualElement BuildFileRow(GitFileReader.ChangedFile file)
        {
            var path = file.Path;
            var name = path.Substring(path.LastIndexOf('/') + 1);
            var dir = path.Length > name.Length ? path[..^(name.Length + 1)] : "";

            var content = new VisualElement { style = { flexDirection = FlexDirection.Column, alignItems = Align.Stretch, flexGrow = 1 } };

            var nameRow = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center } };
            nameRow.Add(DiffIcons.BuildIcon(DiffIcons.GetFileIcon(path)));
            nameRow.Add(new Label(name) { style = { unityFontStyleAndWeight = FontStyle.Bold } });
            nameRow.Add(new VisualElement { style = { flexGrow = 1 } });
            nameRow.Add(DiffTheme.BuildBadge(file.Status));
            content.Add(nameRow);

            content.Add(new Label(dir) { style = { fontSize = 10, opacity = 0.6f } });
            return content;
        }

        private void SelectFile(string relativePath)
        {
            var revisionA = _revisionAPicker.Value;
            var revisionB = _revisionBPicker.Value;

            var hasBefore = GitFileReader.TryGetFileAtRevision(_repoRoot, revisionA, relativePath, out var contentBefore, out var errorBefore);
            var hasAfter = GitFileReader.TryGetFileAtRevision(_repoRoot, revisionB, relativePath, out var contentAfter, out var errorAfter);

            if (!hasBefore && !hasAfter)
            {
                _objectList.Clear();
                _beforeColumn.Clear();
                _afterColumn.Clear();
                _beforeColumn.Add(new Label($"Could not read either revision.\n{errorBefore}\n{errorAfter}"));
                return;
            }

            var parsedBefore = UnityYamlParser.TryParse(contentBefore ?? "", out var docsBefore, out var parseErrorBefore);
            var parsedAfter = UnityYamlParser.TryParse(contentAfter ?? "", out var docsAfter, out var parseErrorAfter);
            if (!parsedBefore || !parsedAfter)
            {
                _objectList.Clear();
                _beforeColumn.Clear();
                _afterColumn.Clear();
                _beforeColumn.Add(new Label($"YAML parse failed.\n{parseErrorBefore}\n{parseErrorAfter}"));
                return;
            }

            var isSceneLike = docsBefore.Concat(docsAfter).Any(d => d.RootKey == "GameObject");
            _objectColumnTitle.text = isSceneLike ? "GameObjects" : "Documents";

            // Fill in stripped nested-prefab placeholders with their real overridden values,
            // then give every other overridden component (ones with no placeholder at all, e.g.
            // a Rigidbody whose mass changed) a virtual section of its own too — both before
            // diffing/grouping, so every stage sees the same field data a non-stripped document
            // would have. See PrefabOverrideHydrator.
            PrefabOverrideHydrator.Hydrate(docsBefore);
            PrefabOverrideHydrator.Hydrate(docsAfter);
            PrefabOverrideHydrator.SynthesizeMissingOverrides(docsBefore, docsAfter);

            var diffs = UnityYamlDiff.Compare(docsBefore, docsAfter);
            _currentGroups = ObjectGrouper.BuildGroups(docsBefore, docsAfter, diffs);

            var categorySelector = isSceneLike ? (Func<ObjectGroup, string>)(g => CategoryLabel(g.Category)) : null;
            _objectList.SetItems(_currentGroups, BuildObjectRow, SelectObject, FlexDirection.Row, Align.Center, categorySelector);
            if (_currentGroups.Count > 0)
            {
                _objectList.Select(_currentGroups[0]);
            }
            else
            {
                _beforeColumn.Clear();
                _afterColumn.Clear();
            }
        }

        // ----------------------------------------------------------- objects column -------

        private static string CategoryLabel(GroupCategory category) => category switch
        {
            GroupCategory.GameObjects => "GameObjects",
            GroupCategory.SceneSettings => "Scene Settings",
            _ => "Other",
        };

        private static VisualElement BuildObjectRow(ObjectGroup group)
        {
            var badge = group.Status switch
            {
                DocumentChangeType.Added => "A",
                DocumentChangeType.Removed => "R",
                DocumentChangeType.Modified => "M",
                _ => "",
            };

            var content = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, flexGrow = 1, marginLeft = group.Depth * 14 } };
            var overlay = DiffIcons.GetOverrideOverlay(group.IsAddedGameObjectOverride, group.IsRemovedGameObjectOverride);
            content.Add(DiffIcons.BuildIcon(DiffIcons.GetDocumentIcon(group.Anchor), overlay));
            content.Add(new Label(group.DisplayName));
            if (badge.Length > 0)
            {
                content.Add(new VisualElement { style = { flexGrow = 1 } });
                content.Add(DiffTheme.BuildBadge(badge));
            }
            return content;
        }

        private void SelectObject(ObjectGroup group)
        {
            _selectedGroup = group;

            // Shared across both columns so hovering a row on one side highlights its
            // counterpart (same document + field path) on the other — see RegisterRowHover.
            var rowHoverRegistry = new Dictionary<string, List<VisualElement>>();
            RenderColumn(_beforeColumn, group, group.BeforeDocs, group.AfterDocs, "does not exist yet at this revision", _onlyChanges, rowHoverRegistry);
            RenderColumn(_afterColumn, group, group.AfterDocs, group.BeforeDocs, "removed at this revision", _onlyChanges, rowHoverRegistry);
        }

        // ---------------------------------------------------------- inspector rendering ----

        private static void RenderColumn(
            ScrollView column, ObjectGroup group, List<UnityYamlDocument> docs, List<UnityYamlDocument> otherSideDocs, string missingReason, bool onlyChanges,
            Dictionary<string, List<VisualElement>> rowHoverRegistry)
        {
            column.Clear();

            if (docs.Count == 0)
            {
                // A synthesized, untouched GameObject (see ObjectGrouper.SynthesizeSourceHierarchy)
                // has no document on EITHER side — it exists unchanged, just wasn't serialized by
                // Unity since nothing about it was overridden. missingReason ("doesn't exist yet" /
                // "removed") would be actively wrong here, so show the same neutral message both
                // columns would use if there were docs but no diffs.
                var message = group.Status == DocumentChangeType.Unchanged ? "no changes" : missingReason;
                var empty = new VisualElement { style = { alignItems = Align.Center, paddingTop = 50 } };
                empty.Add(new Label($"({message})") { style = { opacity = 0.4f, fontSize = 11 } });
                column.Add(empty);
                return;
            }

            var otherFileIds = otherSideDocs.Select(d => d.FileId).ToHashSet();
            var diffByFileId = group.DocDiffs.ToDictionary(d => d.FileId);
            var rendered = 0;

            foreach (var doc in docs)
            {
                var existsOnOtherSide = otherFileIds.Contains(doc.FileId);
                var sectionKind = existsOnOtherSide ? RowChangeKind.None
                    : (docs == group.BeforeDocs ? RowChangeKind.Removed : RowChangeKind.Added);

                Dictionary<string, FieldChange> changesByPath = null;
                if (existsOnOtherSide && diffByFileId.TryGetValue(doc.FileId, out var docDiff))
                {
                    changesByPath = docDiff.FieldChanges.ToDictionary(fc => fc.Path);
                }

                if (onlyChanges && !InspectorFieldView.HasChanges(sectionKind, changesByPath)) continue;

                column.Add(InspectorFieldView.BuildComponentSection(doc, sectionKind, changesByPath, onlyChanges, rowHoverRegistry));
                rendered++;
            }

            if (rendered == 0)
            {
                var empty = new VisualElement { style = { alignItems = Align.Center, paddingTop = 50 } };
                empty.Add(new Label("(no changes)") { style = { opacity = 0.4f, fontSize = 11 } });
                column.Add(empty);
            }
        }
    }
}
