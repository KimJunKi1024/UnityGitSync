using System.IO;
using UnityEditor;
using UnityEngine;
using Item = TeamElarin.GitSync.Editor.GitSyncEnvironmentCheck.Item;
using Report = TeamElarin.GitSync.Editor.GitSyncEnvironmentCheck.Report;

namespace TeamElarin.GitSync.Editor
{
    /// <summary>
    /// Git Sync 패널 (Tools → Git → Git Sync 패널).<br/>
    /// 패키지 버전, 필요한 프로그램(git·Python·claude)과 이 프로젝트 항목의 상태를 ✔/✖로 보여 주고, 빠진 프로그램은 [설치] 버튼으로 설치를 돕습니다.
    /// 아래쪽에서 커밋 창·설정·문서를 엽니다.<br/>
    /// 패키지를 처음 설치·업데이트했거나 커밋에 필요한 것이 빠져 있으면 에디터를 켤 때 한 번 자동으로 뜹니다.
    /// 커밋 창을 열 때와 커밋·푸시 직전에 필요한 것이 빠져 있어도 이 패널이 먼저 뜹니다.
    /// </summary>
    /// <remarks>작성일: 2026-11-12<br/>수정일: 2026-11-12 (환경 확인 창을 패널로: 버전·프로그램/프로젝트 구분·설치 버튼·문서 버튼·자동 표시)</remarks>
    [InitializeOnLoad]
    public class GitSyncEnvironmentWindow : EditorWindow
    {
        #region [ 상수 (Constants) ]

        private const string MENU_PATH = "Tools/Git/Git Sync 패널";
        private const string WINDOW_TITLE = "Git Sync";
        private const string REPOSITORY_URL = "https://github.com/KimJunKi1024/UnityGitSync";
        private const string SESSION_CHECKED_KEY = "GitSync.PanelAutoChecked";
        private const string SEEN_VERSION_KEY_PREFIX = "GitSync.SeenVersion.";
        private const float MARK_WIDTH = 22f;
        private const float NAME_WIDTH = 100f;
        private const float BUTTON_WIDTH = 110f;
        private const float FOOTER_BUTTON_HEIGHT = 24f;

        #endregion

        #region [ 내부 상태 (Internal State) ]

        private Report _report;
        private Vector2 _scroll;
        private GUIStyle _titleStyle;
        private GUIStyle _okStyle;
        private GUIStyle _failStyle;
        private GUIStyle _warnStyle;

        #endregion

        #region [ 초기화 (Initialization) ]

        static GitSyncEnvironmentWindow()
        {
            EditorApplication.delayCall += ShowIfNeeded;
        }

        #endregion

        #region [ 유니티 생명주기 (Unity Lifecycle) ]

        private void OnGUI()
        {
            // Open(report)는 창을 만든 직후 결과를 넣으므로, 여기서는 결과가 없을 때(에디터 재시작으로 복원된 창 등)만 검사
            if (_report == null)
                _report = GitSyncEnvironmentCheck.Run();

            InitStyles();
            DrawHeader();
            DrawStatus();

            using (var scroll = new EditorGUILayout.ScrollViewScope(_scroll))
            {
                _scroll = scroll.scrollPosition;
                DrawSection("필요한 프로그램", true);
                DrawSection("이 프로젝트", false);
            }

            DrawFooter();
        }

        #endregion

        #region [ 메뉴 (Menu) ]

        /// <summary>
        /// 패널을 열고 바로 검사합니다.
        /// </summary>
        [MenuItem(MENU_PATH, false, 10)]
        public static void Open()
        {
            Open(null);
        }

        /// <summary>
        /// 이미 검사한 결과로 패널을 엽니다 (null이면 새로 검사).
        /// </summary>
        public static void Open(Report report)
        {
            GitSyncEnvironmentWindow window = GetWindow<GitSyncEnvironmentWindow>(true, WINDOW_TITLE);
            window.minSize = new Vector2(580f, 520f);
            window._report = report ?? GitSyncEnvironmentCheck.Run();
            window.Repaint();
        }

        #endregion

        #region [ 화면 그리기 (Drawing) ]

        private void DrawHeader()
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Label("Git Sync", _titleStyle);
                    GUILayout.FlexibleSpace();
                    string version = GitSyncTools.PackageVersion;
                    GUILayout.Label(string.IsNullOrEmpty(version) ? string.Empty : "v" + version, EditorStyles.boldLabel);
                }
                GUILayout.Label("고른 파일만 로컬 커밋하고, 정해진 시간마다 묶음 커밋 1개로 푸시합니다.", EditorStyles.wordWrappedMiniLabel);
            }

            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                if (GUILayout.Button("다시 확인", EditorStyles.toolbarButton, GUILayout.Width(70f)))
                    _report = GitSyncEnvironmentCheck.Run();
                GUILayout.FlexibleSpace();
                GUILayout.Label("검사는 패널을 열 때와 '다시 확인'을 누를 때 실행됩니다.", EditorStyles.miniLabel);
            }
        }

        private void DrawStatus()
        {
            EditorGUILayout.Space(4f);
            if (!_report.IsReadyForCommit)
                EditorGUILayout.HelpBox("빨간 ✖ 항목을 해결해야 커밋·푸시를 할 수 있습니다. 그전에는 커밋 창과 커밋·푸시 버튼이 이 패널을 대신 엽니다.", MessageType.Error);
            else if (!_report.IsReadyForPush)
                EditorGUILayout.HelpBox("로컬 커밋은 할 수 있습니다. '푸시에 필요' 항목을 해결하면 푸시도 할 수 있습니다.", MessageType.Warning);
            else if (_report.FailCount == 0)
                EditorGUILayout.HelpBox("필요한 프로그램과 설정이 모두 갖춰져 있습니다. 커밋·푸시를 할 수 있습니다.", MessageType.Info);
            else
                EditorGUILayout.HelpBox("커밋·푸시에 필요한 항목은 모두 갖춰져 있습니다. 주황 ✖ 선택 항목은 해당 기능을 쓸 때만 필요합니다.", MessageType.Info);
            EditorGUILayout.Space(2f);
        }

        private void DrawSection(string title, bool isProgram)
        {
            EditorGUILayout.LabelField(title, EditorStyles.boldLabel);
            foreach (Item item in _report.Items)
            {
                if (item.IsProgram == isProgram)
                    DrawItem(item);
            }
            EditorGUILayout.Space(6f);
        }

        private void DrawItem(Item item)
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    // 빨강: 커밋을 막음, 주황: 푸시만 막거나 선택 항목
                    GUIStyle markStyle = item.IsOk ? _okStyle : (item.IsRequired && !item.IsPushOnly ? _failStyle : _warnStyle);
                    GUILayout.Label(item.IsOk ? "✔" : "✖", markStyle, GUILayout.Width(MARK_WIDTH));
                    GUILayout.Label(item.Name, EditorStyles.boldLabel, GUILayout.Width(NAME_WIDTH));
                    string tag = item.IsOk ? string.Empty : (!item.IsRequired ? "[선택] " : (item.IsPushOnly ? "[푸시에 필요] " : string.Empty));
                    GUILayout.Label(tag + (item.Detail ?? string.Empty), EditorStyles.wordWrappedLabel);
                }

                if (item.IsOk || string.IsNullOrEmpty(item.Fix))
                    return;

                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Space(MARK_WIDTH + 4f);
                    GUILayout.Label("→ " + item.Fix, EditorStyles.wordWrappedMiniLabel);
                    if (item.Action != GitSyncEnvironmentCheck.FixAction.None
                        && GUILayout.Button(GitSyncEnvironmentCheck.GetActionLabel(item.Action), GUILayout.Width(BUTTON_WIDTH)))
                    {
                        GitSyncEnvironmentCheck.Execute(item.Action);
                        // 설정이 바뀌었을 수 있으니 다음 그리기에서 바로 다시 검사
                        EditorApplication.delayCall += () => { if (this != null) { _report = GitSyncEnvironmentCheck.Run(); Repaint(); } };
                    }
                }
            }
        }

        private void DrawFooter()
        {
            EditorGUILayout.Space(4f);
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(!_report.IsReadyForCommit))
                {
                    if (GUILayout.Button("커밋 창 열기", GUILayout.Height(FOOTER_BUTTON_HEIGHT)))
                    {
                        GitCommitWindow.Open();
                        Close();
                        GUIUtility.ExitGUI();
                    }
                }
                if (GUILayout.Button("설정", GUILayout.Height(FOOTER_BUTTON_HEIGHT)))
                    GitSyncSettingsProvider.Open();
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("사용법 (README)", GUILayout.Height(FOOTER_BUTTON_HEIGHT)))
                    OpenPackageFile("README.md");
                if (GUILayout.Button("운영 가이드", GUILayout.Height(FOOTER_BUTTON_HEIGHT)))
                    OpenPackageFile(Path.Combine("Documentation~", "Git 운영 가이드.md"));
                if (GUILayout.Button("변경 기록", GUILayout.Height(FOOTER_BUTTON_HEIGHT)))
                    OpenPackageFile("CHANGELOG.md");
                if (GUILayout.Button("GitHub", GUILayout.Height(FOOTER_BUTTON_HEIGHT)))
                    Application.OpenURL(REPOSITORY_URL);
            }
            EditorGUILayout.Space(4f);
        }

        private void InitStyles()
        {
            if (_okStyle != null)
                return;

            _titleStyle = new GUIStyle(EditorStyles.boldLabel) { fontSize = 20 };
            _okStyle = new GUIStyle(EditorStyles.boldLabel) { normal = { textColor = new Color(0.35f, 0.75f, 0.35f) }, alignment = TextAnchor.MiddleCenter };
            _failStyle = new GUIStyle(EditorStyles.boldLabel) { normal = { textColor = new Color(0.9f, 0.35f, 0.35f) }, alignment = TextAnchor.MiddleCenter };
            _warnStyle = new GUIStyle(EditorStyles.boldLabel) { normal = { textColor = new Color(0.9f, 0.7f, 0.2f) }, alignment = TextAnchor.MiddleCenter };
        }

        #endregion

        #region [ 내부 도우미 (Helpers) ]

        // 에디터를 켤 때 한 번: 이 프로젝트에서 처음 보는 패키지 버전이거나 커밋에 필요한 것이 빠져 있으면 패널을 띄움
        private static void ShowIfNeeded()
        {
            if (Application.isBatchMode || SessionState.GetBool(SESSION_CHECKED_KEY, false))
                return;
            SessionState.SetBool(SESSION_CHECKED_KEY, true);

            string versionKey = SEEN_VERSION_KEY_PREFIX + Hash128.Compute(GitSyncSettings.ProjectRoot).ToString();
            string version = GitSyncTools.PackageVersion;
            bool isNewVersion = !string.IsNullOrEmpty(version) && EditorPrefs.GetString(versionKey, string.Empty) != version;
            if (isNewVersion)
                EditorPrefs.SetString(versionKey, version);

            Report report = GitSyncEnvironmentCheck.Run();
            if (isNewVersion || !report.IsReadyForCommit)
                Open(report);
        }

        private static void OpenPackageFile(string relativePath)
        {
            string path = Path.Combine(GitSyncTools.PackageRoot, relativePath);
            if (File.Exists(path))
                EditorUtility.OpenWithDefaultApp(path);
            else
                Application.OpenURL(REPOSITORY_URL);
        }

        #endregion
    }
}
