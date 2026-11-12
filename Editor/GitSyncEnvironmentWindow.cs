using UnityEditor;
using UnityEngine;
using Item = TeamElarin.GitSync.Editor.GitSyncEnvironmentCheck.Item;
using Report = TeamElarin.GitSync.Editor.GitSyncEnvironmentCheck.Report;

namespace TeamElarin.GitSync.Editor
{
    /// <summary>
    /// 환경 확인 창 (Tools → Git → 환경 확인).<br/>
    /// 항목마다 ✔/✖와 한 줄 설명을 보여 주고, 실패 항목에는 해결 방법과 버튼을 둡니다.
    /// 커밋 창을 처음 열 때와 커밋·푸시 직전에 필수 항목이 빠져 있으면 이 창이 먼저 뜹니다.
    /// </summary>
    /// <remarks>작성일: 2026-11-12</remarks>
    public class GitSyncEnvironmentWindow : EditorWindow
    {
        #region [ 상수 (Constants) ]

        private const string MENU_PATH = "Tools/Git/환경 확인";
        private const float MARK_WIDTH = 22f;
        private const float BUTTON_WIDTH = 110f;

        #endregion

        #region [ 내부 상태 (Internal State) ]

        private Report _report;
        private Vector2 _scroll;
        private GUIStyle _okStyle;
        private GUIStyle _failStyle;
        private GUIStyle _warnStyle;

        #endregion

        #region [ 메뉴 (Menu) ]

        /// <summary>
        /// 환경 확인 창을 열고 바로 검사합니다.
        /// </summary>
        [MenuItem(MENU_PATH, false, 10)]
        public static void Open()
        {
            Open(null);
        }

        /// <summary>
        /// 이미 검사한 결과로 창을 엽니다 (null이면 새로 검사).
        /// </summary>
        public static void Open(Report report)
        {
            GitSyncEnvironmentWindow window = GetWindow<GitSyncEnvironmentWindow>(true, "Git Sync 환경 확인");
            window.minSize = new Vector2(560f, 320f);
            window._report = report ?? GitSyncEnvironmentCheck.Run();
            window.Repaint();
        }

        #endregion

        #region [ 유니티 생명주기 (Unity Lifecycle) ]

        private void OnEnable()
        {
            if (_report == null)
                _report = GitSyncEnvironmentCheck.Run();
        }

        private void OnGUI()
        {
            InitStyles();
            DrawHeader();

            using (var scroll = new EditorGUILayout.ScrollViewScope(_scroll))
            {
                _scroll = scroll.scrollPosition;
                foreach (Item item in _report.Items)
                    DrawItem(item);
            }

            DrawFooter();
        }

        #endregion

        #region [ 화면 그리기 (Drawing) ]

        private void DrawHeader()
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                if (GUILayout.Button("다시 확인", EditorStyles.toolbarButton, GUILayout.Width(70f)))
                    _report = GitSyncEnvironmentCheck.Run();
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("설정", EditorStyles.toolbarButton, GUILayout.Width(40f)))
                    GitSyncSettingsProvider.Open();
            }

            EditorGUILayout.Space(4f);
            if (_report.IsReady)
            {
                EditorGUILayout.HelpBox(_report.FailCount == 0
                    ? "필요한 프로그램과 설정이 모두 갖춰져 있습니다. 커밋 창을 쓸 수 있습니다."
                    : "필수 항목은 모두 갖춰져 있습니다. 아래 선택 항목은 해당 기능을 쓸 때만 필요합니다.", MessageType.Info);
            }
            else
            {
                EditorGUILayout.HelpBox("✖ 표시된 필수 항목을 해결해야 커밋·푸시를 할 수 있습니다. 해결한 뒤 '다시 확인'을 누르세요.", MessageType.Error);
            }
            EditorGUILayout.Space(4f);
        }

        private void DrawItem(Item item)
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUIStyle markStyle = item.IsOk ? _okStyle : (item.IsRequired ? _failStyle : _warnStyle);
                    GUILayout.Label(item.IsOk ? "✔" : "✖", markStyle, GUILayout.Width(MARK_WIDTH));
                    GUILayout.Label(item.Name, EditorStyles.boldLabel, GUILayout.Width(110f));
                    GUILayout.Label(item.Detail ?? string.Empty, EditorStyles.wordWrappedLabel);
                }

                if (item.IsOk || string.IsNullOrEmpty(item.Fix))
                    return;

                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Space(MARK_WIDTH + 4f);
                    GUILayout.Label("→ " + item.Fix, EditorStyles.wordWrappedMiniLabel);
                    if (item.Action != GitSyncEnvironmentCheck.FixAction.None)
                    {
                        if (GUILayout.Button(GitSyncEnvironmentCheck.GetActionLabel(item.Action), GUILayout.Width(BUTTON_WIDTH)))
                        {
                            GitSyncEnvironmentCheck.Execute(item.Action);
                            // 설정이 바뀌었을 수 있으니 다음 그리기에서 바로 다시 검사
                            EditorApplication.delayCall += () => { if (this != null) { _report = GitSyncEnvironmentCheck.Run(); Repaint(); } };
                        }
                    }
                }
            }
        }

        private void DrawFooter()
        {
            EditorGUILayout.Space(4f);
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label("검사는 창을 열 때와 '다시 확인'을 누를 때 실행됩니다. 결과는 저장하지 않습니다.", EditorStyles.miniLabel);
                GUILayout.FlexibleSpace();
                using (new EditorGUI.DisabledScope(!_report.IsReady))
                {
                    if (GUILayout.Button("커밋 창 열기", GUILayout.Width(BUTTON_WIDTH)))
                    {
                        GitCommitWindow.Open();
                        Close();
                    }
                }
            }
            EditorGUILayout.Space(4f);
        }

        private void InitStyles()
        {
            if (_okStyle != null)
                return;

            _okStyle = new GUIStyle(EditorStyles.boldLabel) { normal = { textColor = new Color(0.35f, 0.75f, 0.35f) }, alignment = TextAnchor.MiddleCenter };
            _failStyle = new GUIStyle(EditorStyles.boldLabel) { normal = { textColor = new Color(0.9f, 0.35f, 0.35f) }, alignment = TextAnchor.MiddleCenter };
            _warnStyle = new GUIStyle(EditorStyles.boldLabel) { normal = { textColor = new Color(0.9f, 0.7f, 0.2f) }, alignment = TextAnchor.MiddleCenter };
        }

        #endregion
    }
}
