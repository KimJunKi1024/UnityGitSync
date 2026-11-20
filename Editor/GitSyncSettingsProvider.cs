using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace TeamElarin.GitSync.Editor
{
    /// <summary>
    /// 설정 화면 (Edit → Project Settings → Git Sync, 또는 Tools → Git → 설정).<br/>
    /// 위쪽은 프로젝트 공용 설정(<c>ProjectSettings/GitSyncSettings.json</c>), 아래쪽은 이 PC만의 설정(EditorPrefs)입니다.
    /// </summary>
    /// <remarks>작성일: 2026-11-11<br/>수정일: 2026-11-12 (환경 확인 창 열기 버튼)<br/>수정일: 2026-11-12 (Python 경로 자동 탐색 안내, 저장소 폴더 절대 경로 경고, 자동 요약 안내)<br/>수정일: 2026-11-15 (자동 체크 제외 경로 편집 및 기본값 복원 UI 추가)<br/>수정일: 2026-11-20 (저장소 폴더·원격·브랜치에 쉬운 설명, 이 PC에서 확인한 값, 자동으로 채우기)</remarks>
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
        private GitProbe _probe;          // 이 PC에서 확인한 저장소·원격·브랜치 (화면을 열 때·저장할 때·'다시 확인' 때 갱신)
        private static GUIStyle _okStyle;
        private static GUIStyle _failStyle;

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
            _probe = null;
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
            EditorGUILayout.HelpBox("이 칸들은 프로젝트를 만든 사람이 한 번 정해 두면 Git으로 팀 모두에게 함께 내려옵니다.\n"
                                    + "팀원은 아무것도 바꾸지 않아도 됩니다. 칸 아래의 '이 PC에서 확인' 줄이 모두 ✔이면 그대로 두세요.", MessageType.Info);
            if (_probe == null)
                _probe = GitProbe.Run(_settings);

            EditorGUI.BeginChangeCheck();

            _settings.Repo = GitSyncSettings.CleanPath(EditorGUILayout.TextField(new GUIContent("저장소 폴더", "비우면 프로젝트 폴더가 들어 있는 Git 저장소. 상대 경로는 프로젝트 폴더 기준"), _settings.Repo));
            DrawHelp("작업 파일이 들어 있는 Git 폴더(GitHub Desktop으로 Clone한 폴더)입니다. 비워 두세요. 비워 두면 이 Unity 프로젝트가 들어 있는 폴더를 자동으로 찾습니다.");
            if (Path.IsPathRooted(_settings.Repo))
                EditorGUILayout.HelpBox("이 PC의 경로입니다. 이 설정은 Git으로 팀과 공유되므로 다른 PC에서는 저장소를 잘못 찾습니다.\n비워 두거나(자동으로 찾음) 프로젝트 기준 상대 경로(예: ..)를 쓰세요.", MessageType.Warning);
            DrawCheck(_probe.RepoOk, _probe.RepoText);

            _settings.Remote = EditorGUILayout.TextField(new GUIContent("원격 이름", "푸시할 원격 (보통 origin)"), _settings.Remote);
            DrawHelp("GitHub에 있는 팀 저장소를 이 PC에서 부르는 별명입니다. GitHub Desktop으로 Clone했다면 항상 origin이니 origin 그대로 두세요.");
            DrawCheck(_probe.RemoteOk, _probe.RemoteText);

            _settings.Branch = EditorGUILayout.TextField(new GUIContent("브랜치", "푸시할 브랜치 (보통 main)"), _settings.Branch);
            DrawHelp("팀이 함께 작업을 올리는 줄기 이름입니다. 보통 main이며 팀이 정한 이름 그대로 두세요. GitHub Desktop 위쪽 'Current branch'가 이 이름과 같아야 푸시할 수 있습니다.");
            DrawCheck(_probe.BranchOk, _probe.BranchText);

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.PrefixLabel(" ");
                if (GUILayout.Button(new GUIContent("이 PC에서 다시 확인", "저장소 폴더·원격·브랜치를 이 PC의 Git으로 다시 확인합니다"), GUILayout.Width(130f)))
                    _probe = GitProbe.Run(_settings);
                if (GUILayout.Button(new GUIContent("자동으로 채우기", "저장소 폴더는 비우고, 원격과 브랜치는 이 PC의 Git에서 찾은 값(GitHub 저장소의 기본 브랜치)으로 채웁니다. 처음 설정할 때만 씁니다."), GUILayout.Width(110f)))
                {
                    _settings.Repo = string.Empty;
                    if (_probe.SuggestedRemote.Length > 0)
                        _settings.Remote = _probe.SuggestedRemote;
                    if (_probe.SuggestedBranch.Length > 0)
                        _settings.Branch = _probe.SuggestedBranch;
                    GUI.FocusControl(null);
                    GUI.changed = true;
                }
            }
            EditorGUILayout.Space(4f);

            _settings.PushIntervalMinutes = EditorGUILayout.IntField(new GUIContent("푸시 주기 (분)", "다음 작업을 시작한 순간부터 이 시간이 지나면 푸시 차례"), _settings.PushIntervalMinutes);
            _settings.BundleCommit = EditorGUILayout.Toggle(new GUIContent("묶음 커밋 만들기", "마지막 푸시 뒤의 개별 커밋을 작업 내용·파일 수를 합산한 묶음 커밋 1개로 합쳐 그것만 올림 (개별 커밋은 로컬에도 남지 않음)"), _settings.BundleCommit);
            _settings.WorkLogEntriesDir = GitSyncSettings.CleanPath(EditorGUILayout.TextField(new GUIContent("작업 기록 폴더", "entryNNN.md가 있는 폴더 (프로젝트 폴더 기준). 비우면 작업 기록 연동 끔"), _settings.WorkLogEntriesDir));

            int summaryIndex = _settings.AutoSummary == GitSyncSettings.AUTO_SUMMARY_CLAUDE ? 1 : 0;
            summaryIndex = EditorGUILayout.Popup(new GUIContent("작업 내용 자동 요약", "작업 기록·직접 쓴 제목이 없을 때 쓰는 방법. claude가 30초 안에 답하지 않으면 파일 이름 요약"), summaryIndex, AUTO_SUMMARY_LABELS);
            _settings.AutoSummary = summaryIndex == 1 ? GitSyncSettings.AUTO_SUMMARY_CLAUDE : GitSyncSettings.AUTO_SUMMARY_OFF;
            if (summaryIndex == 1)
                EditorGUILayout.HelpBox("PC마다 Claude Code(claude 명령)가 있어야 합니다. 없는 PC에서는 파일 이름 요약으로 대신합니다 (Git Sync 패널에서 확인).", MessageType.None);

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
                        _probe = GitProbe.Run(_settings);
                    }
                }
            }
        }

        private static void DrawLocalSettings()
        {
            EditorGUILayout.LabelField("이 PC만의 설정 (EditorPrefs)", EditorStyles.boldLabel);

            string python = EditorGUILayout.DelayedTextField(new GUIContent("Python 경로", "Python 3.8 이상. 비우면 자동으로 찾음 (Windows: python → py → python3, macOS·Linux: python3 → python)"), GitSyncPrefs.PythonPathSetting);
            if (python != GitSyncPrefs.PythonPathSetting)
                GitSyncPrefs.PythonPathSetting = python;
            if (GitSyncPrefs.IsPythonAuto)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.PrefixLabel(" ");
                    GUILayout.Label("비워 두면 자동으로 찾습니다. 지금: " + GitSyncPrefs.PythonPath, EditorStyles.miniLabel);
                }
            }

            string claude = EditorGUILayout.DelayedTextField(new GUIContent("claude 경로", "자동 요약에만 씀. 비우면 PATH에서 찾음 (Unity가 PATH를 못 볼 때 claude.exe 전체 경로)"), GitSyncPrefs.ClaudePath);
            if (claude != GitSyncPrefs.ClaudePath)
                GitSyncPrefs.ClaudePath = claude;

            DrawUncheckedPathsSetting();

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.PrefixLabel(" ");
                if (GUILayout.Button(new GUIContent("Git Sync 패널 열기", "git·Python·claude·저장소·설정이 이 PC에 갖춰졌는지 실제로 실행해 확인하고, 빠진 프로그램을 설치"), GUILayout.Width(140f)))
                    GitSyncEnvironmentWindow.Open();
            }

            EditorGUILayout.Space(6f);
            EditorGUILayout.LabelField("명령줄 도구 위치", EditorStyles.boldLabel);
            EditorGUILayout.SelectableLabel("Library/GitSync/Tools/  (에디터를 켤 때마다 최신 도구로 복사됨)", EditorStyles.wordWrappedLabel);
        }

        // 칸 아래 쉬운 설명 (마우스를 올리지 않아도 보이도록 항상 표시)
        private static void DrawHelp(string text)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.PrefixLabel(" ");
                GUILayout.Label(text, EditorStyles.wordWrappedMiniLabel);
            }
        }

        // 이 PC에서 확인한 결과 한 줄 (✔ 초록 / ✖ 빨강)
        private static void DrawCheck(bool isOk, string text)
        {
            if (_okStyle == null)
            {
                _okStyle = new GUIStyle(EditorStyles.wordWrappedMiniLabel) { normal = { textColor = new Color(0.35f, 0.75f, 0.35f) } };
                _failStyle = new GUIStyle(EditorStyles.wordWrappedMiniLabel) { normal = { textColor = new Color(0.9f, 0.4f, 0.35f) } };
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.PrefixLabel(" ");
                GUILayout.Label((isOk ? "✔ 이 PC에서 확인: " : "✖ 이 PC에서 확인: ") + text, isOk ? _okStyle : _failStyle);
            }
            EditorGUILayout.Space(4f);
        }

        private static void DrawUncheckedPathsSetting()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.PrefixLabel(new GUIContent(
                    "자동 체크 제외 경로",
                    "커밋 창을 열거나 '모두 선택'을 누를 때 자동으로 체크하지 않을 경로입니다(한 줄에 하나, 프로젝트 기준 상대 경로). 폴더는 끝에 / 를 붙입니다."));
                using (new EditorGUILayout.VerticalScope())
                {
                    string raw = GitSyncPrefs.UncheckedPathsText;
                    string edited = EditorGUILayout.TextArea(raw, GUILayout.MinHeight(56f));
                    if (edited != raw)
                        GitSyncPrefs.UncheckedPathsText = edited;

                    using (new EditorGUILayout.HorizontalScope())
                    {
                        GUILayout.Label("한 줄에 하나씩 입력 · 폴더는 끝에 / (직접 클릭하면 커밋 가능)", EditorStyles.miniLabel);
                        GUILayout.FlexibleSpace();
                        using (new EditorGUI.DisabledScope(GitSyncPrefs.IsDefaultUncheckedPaths))
                        {
                            if (GUILayout.Button("기본값 복원", EditorStyles.miniButton, GUILayout.Width(80f)))
                            {
                                GitSyncPrefs.ResetUncheckedPaths();
                                GUI.FocusControl(null);
                            }
                        }
                    }
                }
            }
        }

        #endregion

        #region [ 이 PC에서 확인 (Git Probe) ]

        /// <summary>
        /// 설정의 저장소 폴더·원격 이름·브랜치를 이 PC의 Git으로 확인한 결과. 문구는 개발자가 아니어도 알 수 있게 씁니다.
        /// </summary>
        private class GitProbe
        {
            public bool RepoOk;
            public string RepoText = string.Empty;
            public bool RemoteOk;
            public string RemoteText = string.Empty;
            public bool BranchOk;
            public string BranchText = string.Empty;
            public string SuggestedRemote = string.Empty; // '자동으로 채우기' 값
            public string SuggestedBranch = string.Empty;

            public static GitProbe Run(GitSyncSettings settings)
            {
                var probe = new GitProbe();
                string git = GitSyncTools.GitExe;
                string project = GitSyncSettings.ProjectRoot;
                string output, error;

                // 저장소 폴더
                string start = string.IsNullOrEmpty(settings.Repo) ? project : Path.GetFullPath(Path.Combine(project, settings.Repo));
                int code = GitSyncTools.RunProcess(git, "rev-parse --show-toplevel", start, out output, out error);
                if (code == -1)
                {
                    probe.RepoText = "git을 찾지 못했습니다. Tools → Git → Git Sync 패널에서 [Git 설치]를 누르세요.";
                    probe.RemoteText = probe.BranchText = "git을 설치한 뒤 '이 PC에서 다시 확인'을 누르세요.";
                    return probe;
                }
                if (code != 0 || string.IsNullOrWhiteSpace(output) || !Directory.Exists(start))
                {
                    probe.RepoText = string.IsNullOrEmpty(settings.Repo)
                        ? "이 Unity 프로젝트가 Git 폴더 안에 있지 않습니다. GitHub Desktop의 File → Clone repository로 팀 저장소를 받고, 그 안의 Unity 프로젝트를 여세요."
                        : $"적어 둔 폴더({settings.Repo})가 Git 폴더가 아닙니다. 칸을 비우세요(자동으로 찾음).";
                    probe.RemoteText = probe.BranchText = "저장소 폴더를 먼저 해결하세요.";
                    return probe;
                }
                string repo = output.Trim().Replace('/', Path.DirectorySeparatorChar);
                probe.RepoOk = true;
                probe.RepoText = "찾은 Git 폴더 " + repo;

                // 원격
                GitSyncTools.RunProcess(git, "remote -v", repo, out output, out error);
                var remotes = new Dictionary<string, string>();
                foreach (string line in output.Split(new[] { '\r', '\n' }, System.StringSplitOptions.RemoveEmptyEntries))
                {
                    string[] parts = line.Split(new[] { '\t', ' ' }, System.StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2 && !remotes.ContainsKey(parts[0]))
                        remotes[parts[0]] = parts[1];
                }
                probe.SuggestedRemote = remotes.ContainsKey("origin") ? "origin" : (remotes.Count > 0 ? new List<string>(remotes.Keys)[0] : string.Empty);
                if (remotes.Count == 0)
                    probe.RemoteText = "이 Git 폴더는 GitHub와 연결돼 있지 않습니다. 팀 저장소를 GitHub Desktop으로 Clone해서 쓰거나, 처음 만드는 사람이면 GitHub Desktop의 Publish repository를 누르세요. (로컬 커밋은 할 수 있습니다)";
                else if (remotes.ContainsKey(settings.Remote))
                {
                    probe.RemoteOk = true;
                    probe.RemoteText = $"{settings.Remote} → {remotes[settings.Remote]}";
                }
                else
                    probe.RemoteText = $"이 PC에는 '{settings.Remote}'라는 원격이 없습니다. 있는 원격: {string.Join(", ", remotes.Keys)}. 칸에 {probe.SuggestedRemote}을(를) 적고 저장하세요.";

                // 브랜치: GitHub 저장소의 기본 브랜치를 추천값으로
                string remoteDefault = string.Empty;
                if (probe.SuggestedRemote.Length > 0
                    && GitSyncTools.RunProcess(git, "symbolic-ref --short -q " + GitSyncTools.Quote("refs/remotes/" + probe.SuggestedRemote + "/HEAD"), repo, out output, out error) == 0)
                {
                    string head = output.Trim();
                    int slash = head.IndexOf('/');
                    remoteDefault = slash >= 0 ? head.Substring(slash + 1) : head;
                }
                code = GitSyncTools.RunProcess(git, "symbolic-ref --short -q HEAD", repo, out output, out error);
                string current = code == 0 ? output.Trim() : string.Empty;
                probe.SuggestedBranch = remoteDefault.Length > 0 ? remoteDefault : (current.Length > 0 ? current : "main");

                bool remoteHasBranch = probe.RemoteOk
                    && GitSyncTools.RunProcess(git, "rev-parse --verify -q " + GitSyncTools.Quote("refs/remotes/" + settings.Remote + "/" + settings.Branch), repo, out output, out error) == 0;
                string missingNote = probe.RemoteOk && !remoteHasBranch && remoteDefault.Length > 0 && remoteDefault != settings.Branch
                    ? $" GitHub 저장소에는 '{settings.Branch}' 브랜치가 없고 기본 브랜치는 '{remoteDefault}'입니다. 팀이 새 브랜치를 쓰기로 한 게 아니면 칸에 {remoteDefault}을(를) 적으세요."
                    : string.Empty;

                if (current.Length == 0)
                    probe.BranchText = $"지금 특정 커밋을 보고 있는 상태(브랜치 아님)입니다. GitHub Desktop 위쪽 Current branch에서 '{settings.Branch}'를 고르세요." + missingNote;
                else if (current != settings.Branch)
                    probe.BranchText = $"지금 GitHub Desktop의 Current branch는 '{current}'입니다. 푸시하려면 Current branch에서 '{settings.Branch}'를 고르세요. (로컬 커밋은 지금도 할 수 있습니다)" + missingNote;
                else
                {
                    probe.BranchOk = missingNote.Length == 0;
                    probe.BranchText = $"지금 Current branch도 '{current}'입니다." + missingNote;
                }
                return probe;
            }
        }

        #endregion
    }
}
