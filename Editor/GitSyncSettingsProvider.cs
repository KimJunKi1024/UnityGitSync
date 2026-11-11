using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace TeamElarin.GitSync.Editor
{
    /// <summary>
    /// 설정 화면 (Edit → Project Settings → Git Sync, 또는 Tools → Git → 설정).<br/>
    /// 위쪽은 프로젝트 공용 설정(<c>ProjectSettings/GitSyncSettings.json</c>), 아래쪽은 이 PC만의 설정(EditorPrefs)입니다.
    /// </summary>
    /// <remarks>작성일: 2026-11-11</remarks>
    public class GitSyncSettingsProvider : SettingsProvider
    {
        #region [ 상수 (Constants) ]

        private const string SETTINGS_PATH = "Project/Git Sync";
        private const string MENU_PATH = "Tools/Git/설정";
        private static readonly string[] AUTO_SUMMARY_LABELS = { "끔 (파일 이름으로 요약)", "claude 명령으로 자동 요약" };

        #endregion

        #region [ 내부 상태 (Internal State) ]

        private GitSyncSettings _settings;
        private bool _isDirty;

        #endregion

        #region [ 생성 (Construction) ]

        private GitSyncSettingsProvider() : base(SETTINGS_PATH, SettingsScope.Project, new HashSet<string> { "Git", "커밋", "푸시" })
        {
        }

        #endregion

        #region [ 공개 메서드 (Public Methods) ]

        /// <summary>
        /// Project Settings에 이 화면을 등록합니다.
        /// </summary>
        [SettingsProvider]
        public static SettingsProvider Create()
        {
            return new GitSyncSettingsProvider();
        }

        /// <summary>
        /// 설정 화면을 엽니다.
        /// </summary>
        [MenuItem(MENU_PATH, false, 20)]
        public static void Open()
        {
            SettingsService.OpenProjectSettings(SETTINGS_PATH);
        }

        /// <summary>
        /// 화면이 열릴 때 설정 파일을 다시 읽습니다.
        /// </summary>
        public override void OnActivate(string searchContext, UnityEngine.UIElements.VisualElement rootElement)
        {
            _settings = GitSyncSettings.Load();
            _isDirty = false;
        }

        /// <summary>
        /// 설정 화면을 그립니다.
        /// </summary>
        public override void OnGUI(string searchContext)
        {
            if (_settings == null)
                _settings = GitSyncSettings.Load();

            EditorGUIUtility.labelWidth = 190f;
            DrawProjectSettings();
            EditorGUILayout.Space(12f);
            DrawLocalSettings();
        }

        #endregion

        #region [ 화면 그리기 (Drawing) ]

        private void DrawProjectSettings()
        {
            EditorGUILayout.LabelField("프로젝트 공용 설정 (ProjectSettings/GitSyncSettings.json, Git에 올라감)", EditorStyles.boldLabel);
            EditorGUI.BeginChangeCheck();

            _settings.Repo = EditorGUILayout.TextField(new GUIContent("저장소 폴더", "비우면 프로젝트 폴더가 들어 있는 Git 저장소. 상대 경로는 프로젝트 폴더 기준"), _settings.Repo);
            _settings.Remote = EditorGUILayout.TextField(new GUIContent("원격 이름", "푸시할 원격 (보통 origin)"), _settings.Remote);
            _settings.Branch = EditorGUILayout.TextField(new GUIContent("브랜치", "푸시할 브랜치 (보통 main)"), _settings.Branch);
            _settings.PushIntervalMinutes = EditorGUILayout.IntField(new GUIContent("푸시 주기 (분)", "다음 작업을 시작한 순간부터 이 시간이 지나면 푸시 차례"), _settings.PushIntervalMinutes);
            _settings.BundleCommit = EditorGUILayout.Toggle(new GUIContent("묶음 커밋 만들기", "마지막 푸시 뒤의 개별 커밋을 작업 내용·파일 수를 합산한 묶음 커밋 1개로 합쳐 그것만 올림 (개별 커밋은 로컬에도 남지 않음)"), _settings.BundleCommit);
            _settings.WorkLogEntriesDir = EditorGUILayout.TextField(new GUIContent("작업 기록 폴더", "entryNNN.md가 있는 폴더 (프로젝트 폴더 기준). 비우면 작업 기록 연동 끔"), _settings.WorkLogEntriesDir);

            int summaryIndex = _settings.AutoSummary == GitSyncSettings.AUTO_SUMMARY_CLAUDE ? 1 : 0;
            summaryIndex = EditorGUILayout.Popup(new GUIContent("작업 내용 자동 요약", "작업 기록·직접 쓴 제목이 없을 때 쓰는 방법. claude가 30초 안에 답하지 않으면 파일 이름 요약"), summaryIndex, AUTO_SUMMARY_LABELS);
            _settings.AutoSummary = summaryIndex == 1 ? GitSyncSettings.AUTO_SUMMARY_CLAUDE : GitSyncSettings.AUTO_SUMMARY_OFF;

            if (EditorGUI.EndChangeCheck())
                _isDirty = true;

            EditorGUILayout.HelpBox("작업 내용(커밋 설명)은 ① 작업 기록 새 항목 제목 → ② 커밋 창에 직접 쓴 제목 → ③ 자동 요약(켠 경우) → ④ 파일 종류·이름 요약 순서로 정합니다.", MessageType.None);

            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.FlexibleSpace();
                using (new EditorGUI.DisabledScope(!_isDirty))
                {
                    if (GUILayout.Button("되돌리기", GUILayout.Width(80f)))
                    {
                        _settings = GitSyncSettings.Load();
                        _isDirty = false;
                    }
                    if (GUILayout.Button("저장", GUILayout.Width(80f)))
                    {
                        _settings.Save();
                        _isDirty = false;
                    }
                }
            }
        }

        private static void DrawLocalSettings()
        {
            EditorGUILayout.LabelField("이 PC만의 설정 (EditorPrefs)", EditorStyles.boldLabel);

            string python = EditorGUILayout.DelayedTextField(new GUIContent("Python 경로", "python 3.8 이상. PATH에 있으면 python 그대로"), GitSyncPrefs.PythonPath);
            if (python != GitSyncPrefs.PythonPath)
                GitSyncPrefs.PythonPath = python;

            string claude = EditorGUILayout.DelayedTextField(new GUIContent("claude 경로", "자동 요약에만 씀. 비우면 PATH에서 찾음 (Unity가 PATH를 못 볼 때 claude.exe 전체 경로)"), GitSyncPrefs.ClaudePath);
            if (claude != GitSyncPrefs.ClaudePath)
                GitSyncPrefs.ClaudePath = claude;

            EditorGUILayout.Space(6f);
            EditorGUILayout.LabelField("명령줄 도구 위치", EditorStyles.boldLabel);
            EditorGUILayout.SelectableLabel("Library/GitSync/Tools/  (에디터를 켤 때마다 최신 도구로 복사됨)", EditorStyles.wordWrappedLabel);
        }

        #endregion
    }
}
