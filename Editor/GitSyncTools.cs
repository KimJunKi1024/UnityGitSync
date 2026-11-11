using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using UnityEditor;
using Debug = UnityEngine.Debug;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace TeamElarin.GitSync.Editor
{
    /// <summary>
    /// 패키지의 파이썬 도구(<c>Tools~</c>)를 찾아 실행합니다.<br/>
    /// 에디터가 켜질 때 도구를 <c>Library/GitSync/Tools/</c>에 복사해 두어, 명령줄(Claude Code 등)에서도 늘 같은 경로로 실행할 수 있게 합니다.
    /// </summary>
    /// <remarks>작성일: 2026-11-11</remarks>
    [InitializeOnLoad]
    public static class GitSyncTools
    {
        #region [ 공개 프로퍼티 (Public Properties) ]

        /// <summary>명령줄용 도구 복사본 폴더 (<c>Library/GitSync/Tools</c>).</summary>
        public static string LibraryToolsDir => Path.Combine(GitSyncSettings.ProjectRoot, "Library", "GitSync", "Tools");

        #endregion

        #region [ 상수 (Constants) ]

        /// <summary>커밋 도구 파일 이름.</summary>
        public const string COMMIT_SCRIPT = "gitsync_commit.py";

        /// <summary>푸시 도구 파일 이름.</summary>
        public const string PUSH_SCRIPT = "gitsync_push.py";

        private const string TOOLS_FOLDER = "Tools~";

        #endregion

        #region [ 실행 결과 (Process Result) ]

        /// <summary>
        /// 파이썬 도구 실행 결과.
        /// </summary>
        public struct ProcessResult
        {
            /// <summary>종료 코드 (실행 자체가 실패하면 -1).</summary>
            public int ExitCode;

            /// <summary>표준 출력.</summary>
            public string Output;

            /// <summary>표준 오류.</summary>
            public string Error;
        }

        #endregion

        #region [ 초기화 (Initialization) ]

        static GitSyncTools()
        {
            EditorApplication.delayCall += CopyToolsToLibrary;
        }

        #endregion

        #region [ 공개 메서드 (Public Methods) ]

        /// <summary>
        /// 도구 원본 폴더 (설치된 패키지 안의 <c>Tools~</c>). 못 찾으면 빈 문자열.
        /// </summary>
        public static string GetToolsSourceDir()
        {
            PackageInfo package = PackageInfo.FindForAssembly(typeof(GitSyncTools).Assembly);
            if (package != null)
            {
                string dir = Path.Combine(package.resolvedPath, TOOLS_FOLDER);
                if (Directory.Exists(dir))
                    return dir;
            }

            // 패키지가 아니라 Assets 아래에 복사해 넣은 경우: 이 파일 기준으로 찾음
            string editorDir = Path.GetDirectoryName(GetThisFilePath());
            string fallback = string.IsNullOrEmpty(editorDir) ? string.Empty : Path.Combine(Path.GetDirectoryName(editorDir), TOOLS_FOLDER);
            return Directory.Exists(fallback) ? fallback : string.Empty;
        }

        /// <summary>
        /// 도구를 <c>Library/GitSync/Tools/</c>에 복사합니다. 내용이 같은 파일은 건너뜁니다.
        /// </summary>
        public static void CopyToolsToLibrary()
        {
            string source = GetToolsSourceDir();
            if (string.IsNullOrEmpty(source))
                return;

            try
            {
                Directory.CreateDirectory(LibraryToolsDir);
                foreach (string file in Directory.GetFiles(source, "*.py"))
                {
                    string target = Path.Combine(LibraryToolsDir, Path.GetFileName(file));
                    byte[] bytes = File.ReadAllBytes(file);
                    if (File.Exists(target) && AreSame(bytes, File.ReadAllBytes(target)))
                        continue;
                    File.WriteAllBytes(target, bytes);
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Git Sync] 도구를 Library에 복사하지 못했습니다: " + e.Message);
            }
        }

        /// <summary>
        /// 파이썬 도구를 백그라운드에서 실행합니다. 프로젝트 폴더를 <c>--project</c>로 넘기고, 출력은 UTF-8로 받습니다.
        /// </summary>
        /// <param name="scriptName"><see cref="COMMIT_SCRIPT"/> 또는 <see cref="PUSH_SCRIPT"/></param>
        /// <param name="arguments">추가 인자 (이미 따옴표 처리된 문자열)</param>
        public static Task<ProcessResult> RunAsync(string scriptName, string arguments)
        {
            string projectRoot = GitSyncSettings.ProjectRoot;
            string toolsDir = GetToolsSourceDir();
            string python = GitSyncPrefs.PythonPath;
            string claude = GitSyncPrefs.ClaudePath;

            if (string.IsNullOrEmpty(toolsDir))
                return Task.FromResult(new ProcessResult { ExitCode = -1, Error = "패키지의 Tools~ 폴더를 찾지 못했습니다." });

            string script = Path.Combine(toolsDir, scriptName);
            string allArguments = Quote(script) + " --project " + Quote(projectRoot) + (string.IsNullOrEmpty(arguments) ? string.Empty : " " + arguments);

            return Task.Run(() =>
            {
                var info = new ProcessStartInfo(python, allArguments)
                {
                    WorkingDirectory = projectRoot,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                };
                info.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
                info.EnvironmentVariables["PYTHONUTF8"] = "1";
                if (!string.IsNullOrEmpty(claude))
                    info.EnvironmentVariables["GITSYNC_CLAUDE"] = claude;

                try
                {
                    using (Process process = Process.Start(info))
                    {
                        Task<string> errorTask = process.StandardError.ReadToEndAsync();
                        string output = process.StandardOutput.ReadToEnd();
                        process.WaitForExit();
                        return new ProcessResult { ExitCode = process.ExitCode, Output = output, Error = errorTask.Result };
                    }
                }
                catch (Exception e)
                {
                    return new ProcessResult { ExitCode = -1, Error = $"파이썬을 실행하지 못했습니다({python}): {e.Message}\n설정(Tools → Git → 설정)에서 Python 경로를 확인하세요." };
                }
            });
        }

        /// <summary>
        /// Windows 명령줄 규칙에 맞게 인자를 따옴표로 감쌉니다.
        /// </summary>
        public static string Quote(string value)
        {
            // 안쪽 따옴표는 \" , 끝의 \는 닫는 따옴표를 가리지 않게 두 번
            string escaped = (value ?? string.Empty).Replace("\"", "\\\"");
            if (escaped.EndsWith("\\", StringComparison.Ordinal))
                escaped += "\\";
            return "\"" + escaped + "\"";
        }

        #endregion

        #region [ 내부 도우미 (Helpers) ]

        private static string GetThisFilePath([CallerFilePath] string path = "")
        {
            return path;
        }

        private static bool AreSame(byte[] a, byte[] b)
        {
            if (a.Length != b.Length)
                return false;
            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] != b[i])
                    return false;
            }
            return true;
        }

        #endregion
    }
}
