# -*- coding: utf-8 -*-
"""Git Sync 커밋 도구: 저장소에서 바로 로컬 커밋을 만듭니다. 푸시는 하지 않습니다(gitsync_push.py).

사용법 (Unity 프로젝트 폴더에서, 또는 --project로 지정):
    python gitsync_commit.py --dry-run                       바뀐 파일과 커밋 설명 미리보기
    python gitsync_commit.py                                 바뀐 파일 전체를 로컬 커밋
    python gitsync_commit.py --files-from 목록.txt            목록의 파일만 커밋 (한 줄에 경로 하나, 저장소 기준)
    python gitsync_commit.py --title "로그인 화면 고치기"       작업 내용을 직접 씀
    python gitsync_commit.py --status-json                   파일별 상태를 JSON으로 출력 (Unity 커밋 창이 사용)

작업 내용(커밋 설명)을 정하는 순서:
    1) 설정 workLogEntriesDir의 작업 기록 중 저장소에 아직 없는 항목의 제목
    2) --title로 직접 쓴 제목
    3) 설정 autoSummary가 "claude"면 `claude -p`로 바뀐 내용 자동 요약 (30초 안에 못 받으면 4로)
    4) 파일 종류·이름으로 만든 규칙 요약

멈추는 경우: 브랜치가 아닌 커밋(detached HEAD), 병합·리베이스·bisect 등이 끝나지 않음, 고르지 않은 파일이 이미 스테이징돼 있음, 커밋할 파일이 없음.
도중에 실패하면 스테이징을 실행 전 상태로 되돌림. 이미 스테이징된 삭제(git rm --cached)는 다시 올리지 않고 그대로 커밋.
주의만 출력(멈추지 않음): Packages/manifest.json에서 패키지가 빠짐, 설정 repo에 PC 절대 경로.
"""
import argparse
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile

import gitsync_common as common

ENTRY_FILE_RE = re.compile(r"entry(\d+)\.md$")
CLAUDE_TIMEOUT_SECONDS = 30
DIFF_LIMIT_CHARS = 6000
SUMMARY_MAX_LINES = 2
SUMMARY_MAX_CHARS = 100


# ───────── 작업 기록 ─────────

def new_entries(project, repo, settings):
    """작업 기록 폴더에서 저장소(HEAD)에 아직 없는 entryNNN.md의 (번호, 제목) 목록."""
    folder_setting = (settings.get("workLogEntriesDir") or "").strip()
    if not folder_setting:
        return []
    folder = folder_setting if os.path.isabs(folder_setting) else os.path.join(project, folder_setting)
    if not os.path.isdir(folder):
        return []
    rel_folder = os.path.relpath(folder, repo).replace("\\", "/")
    committed = set()
    if common.has_head(repo) and not rel_folder.startswith(".."):
        committed = {p.rsplit("/", 1)[-1] for p in
                     common.run_git(repo, "ls-tree", "-r", "--name-only", "HEAD", "--", rel_folder).splitlines()}
    result = []
    for name in os.listdir(folder):
        m = ENTRY_FILE_RE.fullmatch(name)
        if not m or name in committed:
            continue
        with open(os.path.join(folder, name), encoding="utf-8-sig") as f:
            first = f.readline().strip()
        title = re.sub(r"^#+\s*", "", first)
        title = re.sub(r"^\d+\.\s*", "", title)
        title = re.sub(r"\s*\(\d{4}-\d{2}-\d{2}[^)]*\)\s*$", "", title)
        result.append((int(m.group(1)), title or name))
    return sorted(result)


# ───────── 자동 요약 (claude) ─────────

def claude_summary(name_status, diff_text):
    """`claude -p`로 한두 줄 한국어 요약을 받습니다. 없거나 실패·시간 초과면 None."""
    exe = os.environ.get("GITSYNC_CLAUDE") or shutil.which("claude")
    if not exe:
        print("자동 요약: claude 명령을 찾지 못해 규칙 요약으로 씁니다.", file=sys.stderr)
        return None
    prompt = (
        "다음은 Git 커밋에 들어갈 변경입니다. 무슨 작업인지 한국어로 한두 줄 요약하세요.\n"
        "규칙: 요약 문장만 출력. 한 줄에 작업 하나, 줄마다 60자 이내. "
        "머리말·따옴표·목록 기호·마크다운·추가 설명 없이. 파일 경로를 늘어놓지 말 것.\n\n"
        "[바뀐 파일]\n" + "\n".join(name_status[:200]) + "\n\n"
        "[차이 앞부분]\n" + diff_text[:DIFF_LIMIT_CHARS]
    )
    try:
        proc = subprocess.Popen([exe, "-p", "--output-format", "text"],
                                stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                                text=True, encoding="utf-8", errors="replace", cwd=tempfile.gettempdir())
    except OSError as e:
        print(f"자동 요약: claude 실행 실패({e}), 규칙 요약으로 씁니다.", file=sys.stderr)
        return None
    try:
        out, err = proc.communicate(prompt, timeout=CLAUDE_TIMEOUT_SECONDS)
    except subprocess.TimeoutExpired:
        common.kill_tree(proc)
        print(f"자동 요약: {CLAUDE_TIMEOUT_SECONDS}초 안에 답이 없어 규칙 요약으로 씁니다.", file=sys.stderr)
        return None
    if proc.returncode != 0:
        reason = (err or out or "").strip().splitlines()
        print(f"자동 요약: claude 오류({proc.returncode}" + (f", {reason[0][:120]}" if reason else "")
              + "), 규칙 요약으로 씁니다.", file=sys.stderr)
        return None
    lines = []
    for line in out.splitlines():
        text = re.sub(r"^\s*([-*•]|\d+[.)])\s*", "", line).strip().strip('"\'`')
        if text:
            lines.append(text[:SUMMARY_MAX_CHARS])
        if len(lines) >= SUMMARY_MAX_LINES:
            break
    return lines or None


def decide_work_items(project, repo, settings, title, name_status, diff_text, allow_auto):
    """(작업 내용 목록, 출처) — 작업 기록 → 직접 쓴 제목 → 자동 요약 → 규칙 요약."""
    entries = new_entries(project, repo, settings)
    if entries:
        return [f"{n}. {t}" for n, t in entries], "작업 기록"
    if title and title.strip():
        return [title.strip()], "직접 쓴 제목"
    if allow_auto and str(settings.get("autoSummary", "off")).lower() == "claude":
        lines = claude_summary(name_status, diff_text)
        if lines:
            return lines, "자동 요약"
    return [common.rule_summary(name_status)], "규칙 요약"


# ───────── 스테이징 ─────────

def read_paths(list_file):
    with open(list_file, encoding="utf-8-sig") as f:
        paths = [p.strip().replace("\\", "/") for p in f if p.strip()]
    for p in paths:
        if os.path.isabs(p) or ".." in p.split("/"):
            common.fail(f"저장소 기준 상대 경로가 아닙니다: {p}")
    return paths


def split_z(text):
    return [p for p in text.split("\0") if p]


def stage(repo, paths, env=None):
    """paths(None이면 전체)를 인덱스에 올리고 (name-status 줄 목록, 차이 앞부분)을 돌려줍니다.

    이미 스테이징된 삭제(git rm --cached 로 추적만 해제한 파일)는 다시 올리지 않고 그대로 커밋합니다.
    """
    def git(*args, input_text=None, ok_codes=(0,)):
        r = subprocess.run(["git", "-c", "core.quotepath=false", "-C", repo, *args],
                           capture_output=True, text=True, encoding="utf-8", errors="replace", env=env,
                           input=input_text)
        if r.returncode not in ok_codes:
            common.fail(f"git {' '.join(args)} 실패:\n{r.stderr.strip()}")
        return r.stdout

    untracked_by_user = set(split_z(git("diff", "--cached", "--name-only", "--no-renames", "--diff-filter=D", "-z")))
    if paths is None:
        git("add", "-A")
    else:
        tracked = set(split_z(git("ls-files", "-z")))
        usable = []
        for p in paths:
            if p in untracked_by_user:
                continue  # 이미 스테이징된 삭제: 그대로 둠
            if os.path.exists(os.path.join(repo, p)) or p in tracked:
                usable.append(p)
            else:
                print(f"건너뜀(파일도 기록도 없음): {p}", file=sys.stderr)
        # 추적하지 않는 파일 중 .gitignore에 걸린 것은 git이 올리기를 거부함 → 건너뜀
        new_files = [p for p in usable if p not in tracked]
        if new_files:
            ignored = set(split_z(git("check-ignore", "-z", "--stdin", input_text="\0".join(new_files) + "\0", ok_codes=(0, 1))))
            for p in sorted(ignored):
                print(f"건너뜀(.gitignore에 걸린 새 파일): {p}", file=sys.stderr)
            usable = [p for p in usable if p not in ignored]
        if usable:
            with tempfile.NamedTemporaryFile("w", encoding="utf-8", suffix=".txt", delete=False) as tmp:
                tmp.write("\n".join(usable) + "\n")
            try:
                git("add", "-A", f"--pathspec-from-file={tmp.name}")
            finally:
                os.remove(tmp.name)
    # add -A가 추적 해제를 되살렸으면 다시 뺌 (파일은 작업 폴더에 그대로)
    if untracked_by_user:
        revived = sorted(untracked_by_user & set(split_z(git("ls-files", "-z"))))
        if revived:
            git("update-index", "--force-remove", "-z", "--stdin", input_text="\0".join(revived) + "\0")
    name_status = [r for r in git("diff", "--cached", "--name-status", "-M").splitlines() if r.strip()]
    diff_text = git("diff", "--cached", "--stat") + "\n" + git("diff", "--cached", "-M")[:DIFF_LIMIT_CHARS]
    return name_status, diff_text


def preview_stage(repo, paths):
    """실제 인덱스를 건드리지 않고 임시 인덱스에 올려 결과만 봅니다."""
    git_dir = common.run_git(repo, "rev-parse", "--absolute-git-dir").strip()
    fd, temp_index = tempfile.mkstemp(suffix=".index")
    os.close(fd)
    try:
        real_index = os.path.join(git_dir, "index")
        if os.path.isfile(real_index):
            shutil.copyfile(real_index, temp_index)
        else:
            os.remove(temp_index)
        env = dict(os.environ, GIT_INDEX_FILE=temp_index)
        return stage(repo, paths, env)
    finally:
        if os.path.exists(temp_index):
            os.remove(temp_index)


def check_staged_outside(repo, paths):
    """목록 밖 파일이 이미 스테이징돼 있으면 멈춤 (사용자가 따로 올린 변경이 섞이지 않도록)."""
    staged = [p for p in common.run_git(repo, "diff", "--cached", "--name-only", "--no-renames").splitlines() if p]
    if paths is not None:
        chosen = set(paths)
        staged = [p for p in staged if p not in chosen]
    if staged:
        common.fail("고르지 않은 파일이 이미 스테이징돼 있습니다. 스테이징을 비운 뒤 다시 실행하세요:\n  "
                    + "\n  ".join(staged[:20]))


# ───────── 명령 ─────────

def remote_behind(repo, settings):
    """원격이 몇 커밋 앞서 있는지 (확인 실패면 -1)."""
    remote, branch = settings["remote"], settings["branch"]
    if common.run_quiet(["git", "-C", repo, "fetch", "-q", remote], 20) != 0:
        return -1
    ref = f"refs/remotes/{remote}/{branch}"
    if subprocess.run(["git", "-C", repo, "rev-parse", "--verify", "-q", ref], capture_output=True).returncode != 0:
        return 0
    if not common.has_head(repo):
        return int(common.run_git(repo, "rev-list", "--count", ref).strip() or 0)
    return int(common.run_git(repo, "rev-list", "--count", f"HEAD..{ref}").strip() or 0)


def status_files(repo):
    """커밋 창 목록: [{"path", "kind"}]. kind: added | modified | deleted | renamed | untracked(추적 해제)."""
    kinds = {"A": "added", "?": "added", "D": "deleted", "R": "renamed"}
    files = {}
    staged_deleted = set()
    for row in common.run_git(repo, "status", "--porcelain=v1", "-uall", "--no-renames").splitlines():
        if len(row) < 4:
            continue
        code = row[:2].replace(" ", "") or "M"
        path = row[3:].split(" -> ")[-1].strip('"')
        if row[0] == "D":
            staged_deleted.add(path)
        if path in files:
            # git rm --cached 로 추적만 해제한 파일: "D " 와 "??" 두 줄로 나옴 → "추적 해제" 한 줄로
            if path in staged_deleted and code == "??":
                files[path]["kind"] = "untracked"
            continue
        files[path] = {"path": path, "kind": kinds.get(code[0], "modified")}
    return sorted(files.values(), key=lambda f: f["path"])


def is_pc_path(path):
    """PC마다 다른 절대 경로인지 (C:\\…, /Users/…, \\\\server\\…)."""
    return bool(re.match(r"^([A-Za-z]:[\\/]|[\\/])", path or ""))


def read_head_file(repo, rel_path):
    r = subprocess.run(["git", "-C", repo, "show", f"HEAD:{rel_path}"], capture_output=True)
    return r.stdout.decode("utf-8-sig", errors="replace") if r.returncode == 0 else None


def removed_packages(repo, prefix):
    """Packages/manifest.json의 dependencies 중 HEAD에는 있고 작업 폴더에서는 빠진 패키지 이름."""
    rel = (prefix + "/" if prefix else "") + "Packages/manifest.json"
    before = read_head_file(repo, rel)
    if before is None:
        return []
    try:
        old = json.loads(before).get("dependencies") or {}
        path = os.path.join(repo, rel)
        if not os.path.isfile(path):
            return sorted(old)
        with open(path, encoding="utf-8-sig") as f:
            new = json.load(f).get("dependencies") or {}
    except (OSError, ValueError, AttributeError):
        return []
    return sorted(k for k in old if k not in new)


def commit_warnings(repo, prefix, name_status):
    """커밋할 파일 중 팀에 문제가 될 수 있는 것 (멈추지는 않음)."""
    base = prefix + "/" if prefix else ""
    paths = {row.split("\t")[-1] for row in name_status}
    warnings = []
    if base + "Packages/manifest.json" in paths:
        gone = removed_packages(repo, prefix)
        if gone:
            warnings.append("Packages/manifest.json에서 패키지가 빠집니다: " + ", ".join(gone)
                            + "\n  의도한 것이 아니면 manifest.json을 커밋 목록에서 빼세요.")
    settings_rel = base + common.SETTINGS_FILE.replace("\\", "/")
    if settings_rel in paths:
        try:
            with open(os.path.join(repo, settings_rel), encoding="utf-8-sig") as f:
                repo_value = str(json.load(f).get("repo") or "").strip().strip("\"'")
        except (OSError, ValueError, AttributeError):
            repo_value = ""
        if is_pc_path(repo_value):
            warnings.append(f"{settings_rel}의 repo에 이 PC의 경로가 들어 있습니다: {repo_value}"
                            "\n  팀원 PC에서는 저장소를 잘못 찾습니다. 설정의 '저장소 폴더'를 비우거나 상대 경로로 바꾸세요.")
    return warnings


def print_status_json(project, repo, prefix, settings):
    files = status_files(repo)
    data = {
        "project": project,
        "repo": repo,
        "projectPrefix": prefix,
        "branch": settings["branch"],
        "currentBranch": common.current_branch(repo),
        "operation": common.operation_in_progress(repo),
        "behind": remote_behind(repo, settings),
        "autoSummary": str(settings.get("autoSummary", "off")),
        "entries": [{"number": n, "title": t} for n, t in new_entries(project, repo, settings)],
        "removedPackages": removed_packages(repo, prefix),
        "files": files,
    }
    print(json.dumps(data, ensure_ascii=False))


def run_commit(project, repo, settings, paths, title, dry_run, allow_auto):
    if dry_run:
        operation = common.operation_in_progress(repo)
        if operation:
            print(f"주의: {operation}이(가) 진행 중이라 실제 커밋은 멈춥니다.", file=sys.stderr)
        if not common.current_branch(repo):
            print("주의: 브랜치가 아닌 커밋(detached HEAD)이라 실제 커밋은 멈춥니다.", file=sys.stderr)
        name_status, diff_text = preview_stage(repo, paths)
    else:
        common.fail_if_in_progress(repo, "커밋")
        common.fail_if_detached(repo, "커밋")
        check_staged_outside(repo, paths)
        backup = backup_index(repo)
        head_before = head_commit(repo)
        try:
            commit_staged(project, repo, settings, paths, title, allow_auto)
        except BaseException:
            # 커밋 전에 실패하면 인덱스를 실행 전 상태로 (추적 해제·스테이징이 흐트러지지 않게).
            # 커밋이 이미 만들어졌으면 되돌리지 않음 (되돌리면 방금 커밋한 내용을 거꾸로 돌리는 변경이 스테이징됨)
            if head_commit(repo) == head_before:
                restore_index(repo, backup)
            raise
        finally:
            if backup and os.path.exists(backup):
                os.remove(backup)
        return
    if not name_status:
        print("바뀐 파일이 없어 커밋하지 않았습니다.")
        return
    preview_or_commit(project, repo, settings, name_status, diff_text, title, allow_auto, True)


def head_commit(repo):
    """지금 HEAD 커밋 (첫 커밋 전이면 빈 문자열)."""
    return common.run_git(repo, "rev-parse", "--verify", "-q", "HEAD", check=False).strip()


def index_path(repo):
    path = common.run_git(repo, "rev-parse", "--git-path", "index").strip()
    return path if os.path.isabs(path) else os.path.join(repo, path)


def backup_index(repo):
    """인덱스 파일 복사본 경로 (인덱스가 없으면 None)."""
    source = index_path(repo)
    if not os.path.isfile(source):
        return None
    fd, backup = tempfile.mkstemp(suffix=".index")
    os.close(fd)
    shutil.copyfile(source, backup)
    return backup


def restore_index(repo, backup):
    target = index_path(repo)
    if backup and os.path.isfile(backup):
        shutil.copyfile(backup, target)
    elif os.path.isfile(target):
        os.remove(target)  # 실행 전에는 인덱스가 없었음
    print("실패해서 스테이징을 실행 전 상태로 되돌렸습니다.", file=sys.stderr)


def commit_staged(project, repo, settings, paths, title, allow_auto):
    name_status, diff_text = stage(repo, paths)
    if not name_status:
        print("바뀐 파일이 없어 커밋하지 않았습니다.")
        return
    preview_or_commit(project, repo, settings, name_status, diff_text, title, allow_auto, False)


def preview_or_commit(project, repo, settings, name_status, diff_text, title, allow_auto, dry_run):
    prefix = os.path.relpath(project, repo).replace("\\", "/")
    prefix = "" if prefix == "." or prefix.startswith("..") else prefix
    for warning in commit_warnings(repo, prefix, name_status):
        print("주의: " + warning, file=sys.stderr)

    items, source = decide_work_items(project, repo, settings, title, name_status, diff_text, allow_auto)
    message = common.build_message(items, name_status)
    names = {"A": "추가", "M": "수정", "D": "삭제", "R": "이동"}
    print(f"커밋할 파일 {len(name_status)}개" + (" (시험 실행, 저장소는 바꾸지 않음)" if dry_run else ""))
    for row in name_status:
        cols = row.split("\t")
        print(f"  {names.get(cols[0][:1], '수정')}: {cols[-1]}")
    print(f"작업 내용 출처: {source}")
    if dry_run:
        print("\n--- 커밋 설명 미리보기 ---\n" + message)
        return

    with tempfile.NamedTemporaryFile("w", encoding="utf-8", suffix=".txt", delete=False) as tmp:
        tmp.write(message)
    try:
        common.run_git(repo, "commit", "-q", "-F", tmp.name)
    finally:
        os.remove(tmp.name)
    print(f"로컬 커밋 완료: {common.run_git(repo, 'log', '-1', '--format=%h %s').strip()} (푸시는 gitsync_push.py)")
    print("\n" + message)


def main():
    common.setup_console()
    ap = argparse.ArgumentParser(description="Git Sync 커밋 도구 (로컬 커밋만)")
    ap.add_argument("--project", default="", help="Unity 프로젝트 폴더 (기본: 현재 폴더)")
    ap.add_argument("--repo", default="", help="Git 저장소 폴더 (기본: 설정 repo → 프로젝트가 들어 있는 저장소)")
    ap.add_argument("--files-from", default="", help="이 목록 파일의 경로만 커밋 (저장소 기준, 한 줄에 하나)")
    ap.add_argument("--title", default="", help="작업 내용을 직접 씀 (작업 기록 새 항목이 있으면 그쪽이 먼저)")
    ap.add_argument("--no-auto", action="store_true", help="이번에는 claude 자동 요약을 쓰지 않음")
    ap.add_argument("--dry-run", action="store_true", help="미리보기만 (저장소를 바꾸지 않음)")
    ap.add_argument("--status-json", action="store_true", help="파일별 상태·새 작업 기록을 JSON으로 출력")
    a = ap.parse_args()

    project_dir = os.path.abspath(a.project or os.getcwd())
    settings = common.load_settings(project_dir)
    project, repo, prefix = common.resolve_paths(project_dir, settings, a.repo)

    if a.status_json:
        print_status_json(project, repo, prefix, settings)
        return
    print(f"프로젝트: {project}\n저장소: {repo}")
    paths = read_paths(a.files_from) if a.files_from else None
    if paths is not None and not paths:
        common.fail("커밋할 파일이 없습니다.")
    run_commit(project, repo, settings, paths, a.title, a.dry_run, not a.no_auto)


if __name__ == "__main__":
    main()
