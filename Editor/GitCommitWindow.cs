using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using ProcessResult = TeamElarin.GitSync.Editor.GitSyncTools.ProcessResult;

namespace TeamElarin.GitSync.Editor
{
    /// <summary>
    /// Git 커밋 창 (Tools → Git → 커밋 창)<br/>
    /// 저장소의 바뀐 파일을 보여 주고 고른 파일만 로컬 커밋합니다. 아래쪽에서 푸시 카운트를 보고 푸시합니다.<br/>
    /// 실제 일은 패키지의 파이썬 도구(<c>gitsync_commit.py</c>, <c>gitsync_push.py</c>)가 합니다.
    /// </summary>
    /// <remarks>작성일: 2026-11-11 (엘라린 GitCommitWindow를 패키지용으로 일반화, 푸시 기능 추가)<br/>수정일: 2026-11-12 (열기·커밋·푸시 전에 환경 확인)<br/>수정일: 2026-11-12 (지금 브랜치·진행 중 작업·첫 푸시 표시, 브랜치가 다르면 푸시 막음)<br/>수정일: 2026-11-12 (추적 해제 표시, 패키지 빠짐 경고·확인)</remarks>
    public class GitCommitWindow : EditorWindow
    {
        #region [ 상수 (Constants) ]

        private const string MENU_PATH = "Tools/Git/커밋 창";
        private const float KIND_LABEL_WIDTH = 34f;
        private const int DIALOG_TEXT_LIMIT = 1500;

        #endregion

        #region [ 내부 상태 데이터 (Internal State Data) ]

        private enum TaskKind
        {
            Status,
            PushStatus,
            Commit,
            PushStart,
            PushPreview,
            Push,
        }

        [Serializable]
        private class StatusData
        {
            public string project;
            public string repo;
            public string projectPrefix;
            public string branch;
            public string currentBranch;  // 비어 있으면 브랜치가 아닌 커밋(detached HEAD)
            public string operation;      // 진행 중인 병합·리베이스 등 (없으면 빈 문자열)
            public int behind;
            public string autoSummary;
            public string[] removedPackages; // Packages/manifest.json에서 빠지는 패키지 (HEAD 대비)
            public EntryData[] entries;
            public FileData[] files;
        }

        [Serializable]
        private class EntryData
        {
            public int number;
            public string title;
        }

        [Serializable]
        private class FileData
        {
            public string path;
            public string kind; // added | modified | deleted | renamed | untracked(추적 해제: 파일은 남기고 Git에서만 뺌)
        }

        [Serializable]
        private class PushStatusData
        {
            public string lastPush;
            public string timerStart;
            public int elapsedMinutes;
            public int intervalMinutes;
            public bool isDue;
            public int pendingCommits;
            public string remote;
            public string branch;
            public string currentBranch;
            public string operation;
            public bool firstPush;        // 원격에 브랜치가 없어 묶지 않고 기록 그대로 올림
            public bool bundleCommit;
        }

        private StatusData _status;
        private PushStatusData _pushStatus;
        private readonly HashSet<string> _selectedPaths = new HashSet<string>();
        private HashSet<string> _knownPaths = new HashSet<string>(); // 마지막으로 읽은 목록 (새로 생긴 파일 판별용)
        private string _filter = string.Empty;
        private string _title = string.Empty;
        private string _log = string.Empty;
        private Vector2 _fileScroll;
        private Vector2 _logScroll;

        private Task<ProcessResult> _runningTask;  // 실행 중인 파이썬 (창이 멈추지 않도록 백그라운드에서 실행)
        private TaskKind _runningKind;
        private string _commitListFile;            // 커밋할 경로 목록 임시 파일

        private GUIStyle _addedStyle;
        private GUIStyle _modifiedStyle;
        private GUIStyle _deletedStyle;
        private GUIStyle _dueStyle;

        #endregion

        #region [ 유니티 생명주기 (Unity Lifecycle) ]

        private void OnEnable()
        {
            RefreshStatus();
        }

        private void Update()
        {
            if (_runningTask == null || !_runningTask.IsCompleted)
                return;

            ProcessResult result = _runningTask.IsFaulted
                ? new ProcessResult { ExitCode = -1, Error = _runningTask.Exception.GetBaseException().Message }
                : _runningTask.Result;
            _runningTask = null;

            switch (_runningKind)
            {
                case TaskKind.Status: OnStatusFinished(result); break;
                case TaskKind.PushStatus: OnPushStatusFinished(result); break;
                case TaskKind.Commit: OnCommitFinished(result); break;
                case TaskKind.PushPreview: OnPushPreviewFinished(result); break;
                default: OnPushActionFinished(result); break;
            }

            Repaint();
        }

        private void OnGUI()
        {
            InitStyles();
            bool isBusy = _runningTask != null;

            DrawToolbar(isBusy);
            DrawRepoInfo();
            DrawBlockers();
            DrawFileList(isBusy);
            DrawCommitArea(isBusy);
            DrawPushArea(isBusy);
            DrawLog();
        }

        #endregion

        #region [ 메뉴 (Menu) ]

        /// <summary>
        /// 커밋 창을 엽니다.
        /// </summary>
        [MenuItem(MENU_PATH, false, 0)]
        public static void Open()
        {
            // 커밋에 필요한 것(git·Python·도구·저장소)이 빠져 있으면 환경 확인 창을 먼저 띄움. 원격이 없어도 열림
            if (!GitSyncEnvironmentCheck.EnsureReadyOrShowWindow(false))
                return;

            GitCommitWindow window = GetWindow<GitCommitWindow>("Git 커밋");
            window.minSize = new Vector2(540f, 480f);
        }

        #endregion

        #region [ 화면 그리기 (Drawing) ]

        private void DrawToolbar(bool isBusy)
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                using (new EditorGUI.DisabledScope(isBusy))
                {
                    if (GUILayout.Button("새로 고침", EditorStyles.toolbarButton, GUILayout.Width(70f)))
                        RefreshStatus();
                    if (GUILayout.Button("모두 선택", EditorStyles.toolbarButton, GUILayout.Width(70f)))
                        SetAllVisibleSelected(true);
                    if (GUILayout.Button("모두 해제", EditorStyles.toolbarButton, GUILayout.Width(70f)))
                        SetAllVisibleSelected(false);
                }

                GUILayout.Space(8f);
                GUILayout.Label("찾기", GUILayout.Width(28f));
                _filter = EditorGUILayout.TextField(_filter, EditorStyles.toolbarSearchField);

                if (isBusy)
                    GUILayout.Label(GetBusyLabel(), GUILayout.Width(70f));
                if (GUILayout.Button("패널", EditorStyles.toolbarButton, GUILayout.Width(40f)))
                    GitSyncEnvironmentWindow.Open();
                if (GUILayout.Button("설정", EditorStyles.toolbarButton, GUILayout.Width(40f)))
                    GitSyncSettingsProvider.Open();
            }
        }

        private void DrawRepoInfo()
        {
            if (_status == null)
                return;

            string current = string.IsNullOrEmpty(_status.currentBranch) ? "브랜치 아님" : _status.currentBranch;
            EditorGUILayout.LabelField("저장소", _status.repo + "  (지금 " + current + " · 푸시 " + _status.branch + ")");
            if (!string.IsNullOrEmpty(_status.operation))
                EditorGUILayout.HelpBox($"{_status.operation}이(가) 아직 끝나지 않았습니다. 마무리하거나(--continue) 취소(--abort)한 뒤 커밋·푸시하세요.", MessageType.Error);
            if (!IsOnPushBranch(_status.currentBranch, _status.branch))
                EditorGUILayout.HelpBox(GetBranchWarning(_status.currentBranch, _status.branch), MessageType.Warning);
            if (_status.removedPackages != null && _status.removedPackages.Length > 0)
                EditorGUILayout.HelpBox("Packages/manifest.json에서 패키지가 빠집니다: " + string.Join(", ", _status.removedPackages)
                                        + "\n의도한 것이 아니면 manifest.json을 커밋하지 마세요.", MessageType.Warning);
            if (_status.behind > 0)
                EditorGUILayout.HelpBox($"원격에 이 PC에 없는 커밋이 {_status.behind}개 있습니다. 푸시 전에 먼저 받아서(Pull) 합치세요.", MessageType.Warning);

            if (_status.entries != null && _status.entries.Length > 0)
            {
                var sb = new StringBuilder("작업 내용으로 들어갈 새 작업 기록:");
                foreach (EntryData entry in _status.entries)
                    sb.Append("\n- ").Append(entry.number).Append(". ").Append(entry.title);
                EditorGUILayout.HelpBox(sb.ToString(), MessageType.None);
            }
        }

        // 커밋을 막거나 주의가 필요한 상태 표시
        private void DrawBlockers()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                EditorGUILayout.HelpBox("플레이 모드에서는 커밋할 수 없습니다.", MessageType.Error);
            if (EditorApplication.isCompiling)
                EditorGUILayout.HelpBox("스크립트 컴파일 중입니다.", MessageType.Warning);

            int errorCount = GetConsoleErrorCount();
            if (errorCount > 0)
                EditorGUILayout.HelpBox($"콘솔에 에러가 {errorCount}개 있습니다. 에러를 정리한 뒤 커밋하세요.", MessageType.Error);

            List<string> dirtyScenes = GetDirtySceneNames();
            if (dirtyScenes.Count > 0)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.HelpBox("저장하지 않은 씬: " + string.Join(", ", dirtyScenes) + "\n저장해야 목록과 커밋에 반영됩니다.", MessageType.Warning);
                    if (GUILayout.Button("씬 저장", GUILayout.Width(70f), GUILayout.Height(38f)))
                    {
                        EditorSceneManager.SaveOpenScenes();
                        RefreshStatus();
                    }
                }
            }
        }

        private void DrawFileList(bool isBusy)
        {
            if (_status == null || _status.files == null)
            {
                GUILayout.FlexibleSpace();
                return;
            }

            EditorGUILayout.LabelField($"바뀐 파일 {_status.files.Length}개 · 선택 {_selectedPaths.Count}개", EditorStyles.boldLabel);
            if (_status.files.Length == 0)
                EditorGUILayout.HelpBox("커밋하지 않은 변경이 없습니다.", MessageType.Info);

            using (new EditorGUI.DisabledScope(isBusy))
            using (var scroll = new EditorGUILayout.ScrollViewScope(_fileScroll, GUILayout.ExpandHeight(true)))
            {
                _fileScroll = scroll.scrollPosition;

                foreach (FileData file in _status.files)
                {
                    if (!IsVisible(file.path))
                        continue;

                    using (new EditorGUILayout.HorizontalScope())
                    {
                        bool isSelected = _selectedPaths.Contains(file.path);
                        bool newSelected = EditorGUILayout.Toggle(isSelected, GUILayout.Width(16f));
                        if (newSelected != isSelected)
                            SetSelectedWithMeta(file.path, newSelected);

                        GUILayout.Label(GetKindLabel(file.kind), GetKindStyle(file.kind), GUILayout.Width(KIND_LABEL_WIDTH));

                        if (GUILayout.Button(file.path, EditorStyles.label))
                            PingAsset(file.path);
                    }
                }
            }
        }

        private void DrawCommitArea(bool isBusy)
        {
            EditorGUILayout.LabelField("작업 내용 (직접 쓰기, 선택)");
            _title = EditorGUILayout.TextField(_title);
            EditorGUILayout.LabelField(GetTitleHint(), EditorStyles.miniLabel);

            bool canCommit = !isBusy && _status != null && _selectedPaths.Count > 0 && string.IsNullOrEmpty(_status.operation)
                             && !string.IsNullOrEmpty(_status.currentBranch)
                             && !EditorApplication.isPlayingOrWillChangePlaymode && !EditorApplication.isCompiling
                             && GetConsoleErrorCount() == 0;

            using (new EditorGUI.DisabledScope(!canCommit))
            {
                if (GUILayout.Button($"선택한 {_selectedPaths.Count}개 파일 로컬 커밋", GUILayout.Height(30f)))
                    Commit();
            }
        }

        private void DrawPushArea(bool isBusy)
        {
            EditorGUILayout.Space(6f);
            EditorGUILayout.LabelField("푸시", EditorStyles.boldLabel);
            if (_pushStatus == null)
            {
                EditorGUILayout.LabelField("푸시 상태를 읽는 중이거나 읽지 못했습니다.", EditorStyles.miniLabel);
                return;
            }

            string timer = string.IsNullOrEmpty(_pushStatus.timerStart)
                ? "카운트 멈춤 (작업을 시작할 때 '카운트 시작')"
                : $"카운트 {FormatTime(_pushStatus.timerStart)}부터 · {_pushStatus.elapsedMinutes}분 / {_pushStatus.intervalMinutes}분";
            EditorGUILayout.LabelField(timer, _pushStatus.isDue ? _dueStyle : EditorStyles.label);
            if (_pushStatus.isDue)
                EditorGUILayout.LabelField("푸시할 차례입니다. 지금 작업을 끝낸 뒤 푸시하세요.", _dueStyle);
            EditorGUILayout.LabelField($"올릴 커밋 {_pushStatus.pendingCommits}개 → {_pushStatus.remote}/{_pushStatus.branch}"
                                       + GetBundleLabel()
                                       + (string.IsNullOrEmpty(_pushStatus.lastPush) ? string.Empty : " · 마지막 푸시 " + FormatTime(_pushStatus.lastPush)),
                                       EditorStyles.miniLabel);

            bool isOnPushBranch = IsOnPushBranch(_pushStatus.currentBranch, _pushStatus.branch);
            bool hasOperation = !string.IsNullOrEmpty(_pushStatus.operation);
            if (!isOnPushBranch)
                EditorGUILayout.LabelField($"지금 브랜치가 {_pushStatus.branch}가 아니라 푸시할 수 없습니다.", _dueStyle);

            using (new EditorGUILayout.HorizontalScope())
            using (new EditorGUI.DisabledScope(isBusy))
            {
                using (new EditorGUI.DisabledScope(!string.IsNullOrEmpty(_pushStatus.timerStart)))
                {
                    if (GUILayout.Button("카운트 시작", GUILayout.Height(24f)))
                        RunUserTask(TaskKind.PushStart, GitSyncTools.PUSH_SCRIPT, "--start", false);
                }
                using (new EditorGUI.DisabledScope(_pushStatus.pendingCommits == 0 || !isOnPushBranch || hasOperation))
                {
                    if (GUILayout.Button("푸시…", GUILayout.Height(24f)))
                        RunUserTask(TaskKind.PushPreview, GitSyncTools.PUSH_SCRIPT, "--dry-run", true);
                }
            }
        }

        private void DrawLog()
        {
            if (string.IsNullOrEmpty(_log))
                return;

            using (var scroll = new EditorGUILayout.ScrollViewScope(_logScroll, GUILayout.Height(110f)))
            {
                _logScroll = scroll.scrollPosition;
                EditorGUILayout.SelectableLabel(_log, EditorStyles.wordWrappedLabel, GUILayout.ExpandHeight(true));
            }
        }

        private void InitStyles()
        {
            if (_addedStyle != null)
                return;

            _addedStyle = new GUIStyle(EditorStyles.miniBoldLabel) { normal = { textColor = new Color(0.35f, 0.75f, 0.35f) } };
            _modifiedStyle = new GUIStyle(EditorStyles.miniBoldLabel) { normal = { textColor = new Color(0.9f, 0.7f, 0.2f) } };
            _deletedStyle = new GUIStyle(EditorStyles.miniBoldLabel) { normal = { textColor = new Color(0.9f, 0.35f, 0.35f) } };
            _dueStyle = new GUIStyle(EditorStyles.boldLabel) { normal = { textColor = new Color(0.95f, 0.6f, 0.2f) } };
        }

        private string GetBusyLabel()
        {
            switch (_runningKind)
            {
                case TaskKind.Commit: return "커밋 중…";
                case TaskKind.PushPreview: return "확인 중…";
                case TaskKind.Push: return "푸시 중…";
                default: return "읽는 중…";
            }
        }

        // 푸시 방식 안내: 첫 푸시(원격에 브랜치 없음)는 묶지 않고 기록 그대로 올림
        private string GetBundleLabel()
        {
            if (!_pushStatus.bundleCommit)
                return string.Empty;
            return _pushStatus.firstPush ? " (첫 푸시: 묶지 않고 기록 그대로 올림)" : " (묶음 커밋 1개로 합쳐 올림)";
        }

        private static bool IsOnPushBranch(string currentBranch, string pushBranch)
        {
            return !string.IsNullOrEmpty(currentBranch) && currentBranch == pushBranch;
        }

        private static string GetBranchWarning(string currentBranch, string pushBranch)
        {
            return string.IsNullOrEmpty(currentBranch)
                ? $"지금 브랜치가 아닌 커밋(detached HEAD)에 있어 커밋·푸시를 막습니다 (여기서 만든 커밋은 브랜치를 옮기면 어디에도 속하지 않음). git checkout {pushBranch} 로 돌아오거나 git checkout -b 새이름 으로 브랜치를 만드세요."
                : $"지금 브랜치 '{currentBranch}'은(는) 설정의 푸시 브랜치 '{pushBranch}'와 다릅니다. 커밋은 지금 브랜치에 되고, 푸시는 {pushBranch}로 옮긴 뒤에만 할 수 있습니다.";
        }

        // 직접 쓴 제목이 언제 쓰이는지 안내 (작업 기록 → 직접 쓴 제목 → 자동 요약 → 규칙 요약)
        private string GetTitleHint()
        {
            if (_status != null && _status.entries != null && _status.entries.Length > 0)
                return "새 작업 기록 제목이 먼저 쓰입니다.";
            if (_status != null && _status.autoSummary == GitSyncSettings.AUTO_SUMMARY_CLAUDE)
                return "비우면 claude가 바뀐 내용을 요약합니다 (30초 안에 못 받으면 파일 이름으로 요약).";
            return "비우면 파일 종류·이름으로 요약합니다.";
        }

        #endregion

        #region [ 목록 선택 (Selection) ]

        private bool IsVisible(string path)
        {
            return string.IsNullOrEmpty(_filter) || path.IndexOf(_filter, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void SetAllVisibleSelected(bool isSelected)
        {
            if (_status == null || _status.files == null)
                return;

            foreach (FileData file in _status.files)
            {
                if (IsVisible(file.path))
                    SetSelected(file.path, isSelected);
            }
        }

        // 파일과 짝 .meta를 함께 고르거나 뺌 (한쪽만 올라가면 다른 PC에서 GUID가 어긋남)
        private void SetSelectedWithMeta(string path, bool isSelected)
        {
            SetSelected(path, isSelected);

            string pair = path.EndsWith(".meta", StringComparison.Ordinal) ? path.Substring(0, path.Length - 5) : path + ".meta";
            foreach (FileData file in _status.files)
            {
                if (file.path == pair)
                {
                    SetSelected(pair, isSelected);
                    break;
                }
            }
        }

        private void SetSelected(string path, bool isSelected)
        {
            if (isSelected)
                _selectedPaths.Add(path);
            else
                _selectedPaths.Remove(path);
        }

        // 저장소 기준 경로 → 프로젝트 기준 경로로 바꿔 Project 창에서 보여 줌
        private void PingAsset(string repoPath)
        {
            string prefix = _status == null || string.IsNullOrEmpty(_status.projectPrefix) ? string.Empty : _status.projectPrefix + "/";
            if (!repoPath.StartsWith(prefix, StringComparison.Ordinal))
                return;

            string path = repoPath.Substring(prefix.Length);
            if (path.EndsWith(".meta", StringComparison.Ordinal))
                path = path.Substring(0, path.Length - 5);
            if (!path.StartsWith("Assets/", StringComparison.Ordinal))
                return;

            UnityEngine.Object asset = AssetDatabase.LoadMainAssetAtPath(path);
            if (asset != null)
                EditorGUIUtility.PingObject(asset);
        }

        private static string GetKindLabel(string kind)
        {
            switch (kind)
            {
                case "added": return "추가";
                case "deleted": return "삭제";
                case "renamed": return "이동";
                case "untracked": return "해제";
                default: return "수정";
            }
        }

        private GUIStyle GetKindStyle(string kind)
        {
            switch (kind)
            {
                case "added": return _addedStyle;
                case "deleted":
                case "untracked": return _deletedStyle;
                default: return _modifiedStyle;
            }
        }

        #endregion

        #region [ 실행 (Run Tasks) ]

        private void RunTask(TaskKind kind, string script, string arguments)
        {
            _runningKind = kind;
            _runningTask = GitSyncTools.RunAsync(script, arguments);
        }

        // 사용자가 누른 커밋·푸시 동작: 필요한 환경이 빠져 있으면 실행하지 않고 환경 확인 창을 띄움
        private void RunUserTask(TaskKind kind, string script, string arguments, bool isPush)
        {
            if (!GitSyncEnvironmentCheck.EnsureReadyOrShowWindow(isPush))
                return;

            RunTask(kind, script, arguments);
        }

        private void RefreshStatus()
        {
            if (_runningTask != null)
                return;

            RunTask(TaskKind.Status, GitSyncTools.COMMIT_SCRIPT, "--status-json");
        }

        private void OnStatusFinished(ProcessResult result)
        {
            if (result.ExitCode != 0)
            {
                _status = null;
                _log = "목록을 읽지 못했습니다.\n" + result.Error + result.Output;
            }
            else
            {
                ApplyStatus(result);
            }

            RunTask(TaskKind.PushStatus, GitSyncTools.PUSH_SCRIPT, "--status-json");
        }

        private void ApplyStatus(ProcessResult result)
        {
            try
            {
                StatusData status = JsonUtility.FromJson<StatusData>(result.Output);
                bool isFirstLoad = _status == null;
                var previous = new HashSet<string>(_selectedPaths);
                _status = status;
                if (_status.files == null)
                    _status.files = new FileData[0];

                // 처음 열 때는 모두 선택, 다시 읽을 때는 이전 선택을 유지하고 새로 생긴 파일만 선택
                var currentPaths = new HashSet<string>();
                _selectedPaths.Clear();
                foreach (FileData file in _status.files)
                {
                    if (isFirstLoad || previous.Contains(file.path) || !_knownPaths.Contains(file.path))
                        _selectedPaths.Add(file.path);
                    currentPaths.Add(file.path);
                }
                _knownPaths = currentPaths;

                if (!string.IsNullOrEmpty(result.Error))
                    _log = result.Error;
            }
            catch (Exception e)
            {
                _status = null;
                _log = "목록 형식을 읽지 못했습니다: " + e.Message + "\n" + result.Output;
            }
        }

        private void OnPushStatusFinished(ProcessResult result)
        {
            if (result.ExitCode != 0)
            {
                _pushStatus = null;
                _log = (string.IsNullOrEmpty(_log) ? string.Empty : _log + "\n") + "푸시 상태를 읽지 못했습니다.\n" + result.Error;
                return;
            }

            try
            {
                _pushStatus = JsonUtility.FromJson<PushStatusData>(result.Output);
            }
            catch (Exception e)
            {
                _pushStatus = null;
                _log = "푸시 상태 형식을 읽지 못했습니다: " + e.Message;
            }
        }

        private void Commit()
        {
            if (!GitSyncEnvironmentCheck.EnsureReadyOrShowWindow(false))
                return;

            List<string> dirtyScenes = GetDirtySceneNames();
            if (dirtyScenes.Count > 0)
            {
                int choice = EditorUtility.DisplayDialogComplex("저장하지 않은 씬",
                    "저장하지 않은 씬이 있습니다: " + string.Join(", ", dirtyScenes) + "\n저장하지 않으면 마지막으로 저장한 내용이 커밋됩니다.",
                    "저장 후 계속", "취소", "저장하지 않고 계속");
                if (choice == 1)
                    return;
                if (choice == 0)
                    EditorSceneManager.SaveOpenScenes();
            }

            // manifest.json에서 패키지가 빠지는 변경은 다른 PC에서 받아 온 실수일 수 있으니 한 번 더 확인
            string manifestPath = (string.IsNullOrEmpty(_status.projectPrefix) ? string.Empty : _status.projectPrefix + "/") + "Packages/manifest.json";
            if (_selectedPaths.Contains(manifestPath) && _status.removedPackages != null && _status.removedPackages.Length > 0
                && !EditorUtility.DisplayDialog("패키지가 빠집니다",
                    "Packages/manifest.json에서 다음 패키지가 빠집니다:\n- " + string.Join("\n- ", _status.removedPackages)
                    + "\n\n의도한 것이 아니면 '취소'를 누르고 manifest.json 체크를 해제하세요.", "그대로 커밋", "취소"))
                return;

            if (!EditorUtility.DisplayDialog("로컬 커밋", $"선택한 {_selectedPaths.Count}개 파일을 로컬 커밋합니다.\n푸시는 하지 않습니다.", "커밋", "취소"))
                return;

            AssetDatabase.SaveAssets();

            var paths = new List<string>(_selectedPaths);
            paths.Sort(StringComparer.Ordinal);
            _commitListFile = Path.Combine(Path.GetTempPath(), "gitsync_commit_" + Guid.NewGuid().ToString("N") + ".txt");
            File.WriteAllLines(_commitListFile, paths, new UTF8Encoding(false));

            string titleArg = string.IsNullOrWhiteSpace(_title) ? string.Empty : " --title " + GitSyncTools.Quote(_title.Trim());
            RunTask(TaskKind.Commit, GitSyncTools.COMMIT_SCRIPT, "--files-from " + GitSyncTools.Quote(_commitListFile) + titleArg);
        }

        private void OnCommitFinished(ProcessResult result)
        {
            if (!string.IsNullOrEmpty(_commitListFile) && File.Exists(_commitListFile))
                File.Delete(_commitListFile);
            _commitListFile = null;

            _log = (result.ExitCode == 0 ? string.Empty : "커밋하지 못했습니다.\n") + result.Output + result.Error;
            if (result.ExitCode == 0)
            {
                _title = string.Empty;
                _selectedPaths.Clear();
                _status = null;
            }

            RunTask(TaskKind.Status, GitSyncTools.COMMIT_SCRIPT, "--status-json");
        }

        // 미리보기(--dry-run) 결과를 보여 주고 확인을 받으면 실제 푸시
        private void OnPushPreviewFinished(ProcessResult result)
        {
            if (result.ExitCode != 0)
            {
                _log = "푸시할 수 없습니다.\n" + result.Error + result.Output;
                return;
            }

            string preview = result.Output.Length > DIALOG_TEXT_LIMIT ? result.Output.Substring(0, DIALOG_TEXT_LIMIT) + "\n…" : result.Output;
            string warning = _pushStatus != null && !_pushStatus.isDue ? "아직 푸시 주기가 되지 않았습니다.\n\n" : string.Empty;
            if (EditorUtility.DisplayDialog("푸시", warning + preview, "푸시", "취소"))
                RunTask(TaskKind.Push, GitSyncTools.PUSH_SCRIPT, string.Empty);
        }

        private void OnPushActionFinished(ProcessResult result)
        {
            _log = (result.ExitCode == 0 ? string.Empty : "실패했습니다.\n") + result.Output + result.Error;
            RunTask(TaskKind.PushStatus, GitSyncTools.PUSH_SCRIPT, "--status-json");
        }

        #endregion

        #region [ 공용 도우미 (Helpers) ]

        private static string FormatTime(string isoTime)
        {
            return DateTime.TryParse(isoTime, out DateTime time) ? time.ToString("MM-dd HH:mm") : isoTime;
        }

        private static List<string> GetDirtySceneNames()
        {
            var names = new List<string>();
            for (int i = 0; i < EditorSceneManager.sceneCount; i++)
            {
                var scene = EditorSceneManager.GetSceneAt(i);
                if (scene.isDirty)
                    names.Add(string.IsNullOrEmpty(scene.name) ? "(이름 없음)" : scene.name);
            }
            return names;
        }

        // 콘솔 에러 수 (Unity 내부 API라 못 찾으면 0)
        private static int GetConsoleErrorCount()
        {
            Type logEntries = typeof(UnityEditor.Editor).Assembly.GetType("UnityEditor.LogEntries");
            MethodInfo getCounts = logEntries != null ? logEntries.GetMethod("GetCountsByType", BindingFlags.Static | BindingFlags.Public) : null;
            if (getCounts == null)
                return 0;

            object[] counts = { 0, 0, 0 };
            getCounts.Invoke(null, counts);
            return (int)counts[0];
        }

        #endregion
    }
}
