using System;
using System.IO;
using System.Text;
using UnityEngine;

namespace TeamElarin.GitSync.Editor
{
    /// <summary>
    /// 프로젝트 공용 설정 (<c>ProjectSettings/GitSyncSettings.json</c>, Git에 올려 팀이 같이 씀).<br/>
    /// 파이썬 도구(<c>Tools~/gitsync_common.py</c>)도 같은 파일·같은 키를 읽습니다.
    /// </summary>
    /// <remarks>작성일: 2026-11-11<br/>수정일: 2026-11-11 (설정 파일 위치를 프로젝트 루트에서 ProjectSettings로)</remarks>
    public class GitSyncSettings
    {
        #region [ 공개 프로퍼티 (Public Properties) ]

        /// <summary>Git 저장소 폴더. 비우면 프로젝트 폴더가 들어 있는 저장소. 상대 경로는 프로젝트 폴더 기준.</summary>
        public string Repo { get; set; } = string.Empty;

        /// <summary>푸시할 브랜치 이름.</summary>
        public string Branch { get; set; } = "main";

        /// <summary>푸시할 원격 이름.</summary>
        public string Remote { get; set; } = "origin";

        /// <summary>푸시 주기(분). 다음 작업을 시작한 순간부터 셉니다.</summary>
        public int PushIntervalMinutes { get; set; } = 60;

        /// <summary>푸시할 때 개별 커밋을 작업 내용·파일 수를 합산한 묶음 커밋 1개로 합쳐 그것만 올릴지.</summary>
        public bool BundleCommit { get; set; } = true;

        /// <summary>작업 기록 항목(entryNNN.md) 폴더. 비우면 작업 기록 연동을 끕니다. 상대 경로는 프로젝트 폴더 기준.</summary>
        public string WorkLogEntriesDir { get; set; } = string.Empty;

        /// <summary>작업 내용을 claude로 자동 요약할지 (<c>off</c> / <c>claude</c>).</summary>
        public string AutoSummary { get; set; } = AUTO_SUMMARY_OFF;

        /// <summary>설정 파일 전체 경로.</summary>
        public static string FilePath => Path.Combine(ProjectRoot, "ProjectSettings", FILE_NAME);

        /// <summary>Unity 프로젝트 폴더 (Assets의 부모).</summary>
        public static string ProjectRoot => Directory.GetParent(Application.dataPath).FullName;

        #endregion

        #region [ 상수 (Constants) ]

        /// <summary>자동 요약 끔.</summary>
        public const string AUTO_SUMMARY_OFF = "off";

        /// <summary>claude 명령으로 자동 요약.</summary>
        public const string AUTO_SUMMARY_CLAUDE = "claude";

        private const string FILE_NAME = "GitSyncSettings.json";

        #endregion

        #region [ JSON 형식 (JSON Shape) ]

        // 파이썬 도구와 키 이름을 맞추기 위한 저장 형식 (JsonUtility는 필드 이름을 그대로 키로 씀)
        [Serializable]
        private class FileData
        {
            public string repo = string.Empty;
            public string branch = "main";
            public string remote = "origin";
            public int pushIntervalMinutes = 60;
            public bool bundleCommit = true;
            public string workLogEntriesDir = string.Empty;
            public string autoSummary = AUTO_SUMMARY_OFF;
        }

        #endregion

        #region [ 공개 메서드 (Public Methods) ]

        /// <summary>
        /// 설정 파일을 읽습니다. 없거나 읽지 못하면 기본값을 돌려줍니다.
        /// </summary>
        public static GitSyncSettings Load()
        {
            var settings = new GitSyncSettings();
            if (!File.Exists(FilePath))
                return settings;

            try
            {
                FileData data = JsonUtility.FromJson<FileData>(File.ReadAllText(FilePath, Encoding.UTF8));
                if (data == null)
                    return settings;

                settings.Repo = data.repo ?? string.Empty;
                settings.Branch = string.IsNullOrWhiteSpace(data.branch) ? "main" : data.branch;
                settings.Remote = string.IsNullOrWhiteSpace(data.remote) ? "origin" : data.remote;
                settings.PushIntervalMinutes = data.pushIntervalMinutes > 0 ? data.pushIntervalMinutes : 60;
                settings.BundleCommit = data.bundleCommit;
                settings.WorkLogEntriesDir = data.workLogEntriesDir ?? string.Empty;
                settings.AutoSummary = string.IsNullOrWhiteSpace(data.autoSummary) ? AUTO_SUMMARY_OFF : data.autoSummary;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Git Sync] 설정 파일을 읽지 못해 기본값을 씁니다: " + e.Message);
            }
            return settings;
        }

        /// <summary>
        /// 설정 파일에 저장합니다 (UTF-8, BOM 없음).
        /// </summary>
        public void Save()
        {
            var data = new FileData
            {
                repo = Repo.Trim(),
                branch = Branch.Trim(),
                remote = Remote.Trim(),
                pushIntervalMinutes = Mathf.Max(1, PushIntervalMinutes),
                bundleCommit = BundleCommit,
                workLogEntriesDir = WorkLogEntriesDir.Trim(),
                autoSummary = AutoSummary,
            };
            File.WriteAllText(FilePath, JsonUtility.ToJson(data, true) + "\n", new UTF8Encoding(false));
        }

        #endregion
    }
}
