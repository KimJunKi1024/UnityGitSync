# -*- coding: utf-8 -*-
"""Git Sync 푸시 도구 (시간 규칙 + 묶음 커밋).

푸시 규칙 (Documentation~/Git 운영 가이드.md)
- 작업 중에는 설정한 시간(pushIntervalMinutes, 기본 60분)마다 1번 푸시. 시간이 됐을 때 작업 중이면 그 작업을 끝낸 뒤 푸시.
- 시간은 푸시 직후가 아니라, 푸시 뒤 '다음 작업을 시작하는 순간'(--start)부터 셈.
- bundleCommit이 켜져 있으면(기본) 마지막 푸시 뒤의 개별 커밋을 작업 내용·파일 수를 합산한 묶음 커밋 1개로 합쳐 그것만 올림.
  원격 기록에는 푸시마다 묶음 1개만 남고, 개별 커밋은 로컬에도 남기지 않음(푸시가 성공한 뒤에 로컬 브랜치를 옮김).
  커밋하지 않은 작업 폴더 변경은 그대로 둠. 이미 원격에 올라간 지난 커밋은 건드리지 않음(강제 푸시 없음).

사용법 (Unity 프로젝트 폴더에서, 또는 --project로 지정):
    python gitsync_push.py --start        작업을 시작할 때: 카운트가 멈춰 있으면 지금부터 셈
    python gitsync_push.py --status       카운트 시작 시각·지난 시간·푸시할 차례인지
    python gitsync_push.py --dry-run      묶음 커밋 설명 미리보기 (아무것도 바꾸지 않음)
    python gitsync_push.py                (묶음 커밋 →) git push → 카운트 멈춤
    python gitsync_push.py --status-json  상태를 JSON으로 (Unity 커밋 창이 사용)

상태 파일: <프로젝트>/Library/GitSync/push_state.json (PC마다 따로, Library라 Git에 안 올라감)
멈추는 경우: 지금 브랜치가 설정의 브랜치가 아님, 병합·리베이스 등이 끝나지 않음, 원격에 이 PC에 없는 커밋이 있음,
           이미 스테이징된 변경이 있음, 올릴 커밋이 없음. 강제 푸시는 하지 않음.
첫 푸시(원격에 브랜치가 없음)는 묶지 않고 기존 기록을 그대로 올림.
"""
import argparse
import datetime
import json
import os
import subprocess
import tempfile
import time

import gitsync_common as common

FETCH_TIMEOUT_SECONDS = 60
FETCH_RETRY_DELAY_SECONDS = 3
FETCH_LOGIN_TIMEOUT_SECONDS = 120  # 다시 시도할 때는 GitHub 로그인 창이 뜰 수 있어 넉넉히
LOG_PREVIEW_LIMIT = 20


def state_path(project):
    return os.path.join(project, "Library", "GitSync", "push_state.json")


def load_state(project):
    path = state_path(project)
    if os.path.isfile(path):
        try:
            with open(path, encoding="utf-8") as f:
                data = json.load(f)
            return {"last_push": data.get("last_push"), "timer_start": data.get("timer_start")}
        except (OSError, ValueError):
            pass
    return {"last_push": None, "timer_start": None}


def save_state(project, state):
    path = state_path(project)
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w", encoding="utf-8") as f:
        json.dump(state, f, ensure_ascii=False, indent=2)


def now():
    return datetime.datetime.now().replace(microsecond=0)


def elapsed_minutes(state):
    if not state["timer_start"]:
        return None
    return int((now() - datetime.datetime.fromisoformat(state["timer_start"])).total_seconds() // 60)


def remote_ref(settings):
    return f"refs/remotes/{settings['remote']}/{settings['branch']}"


def ref_exists(repo, ref):
    return subprocess.run(["git", "-C", repo, "rev-parse", "--verify", "-q", ref], capture_output=True).returncode == 0


def empty_tree(repo):
    return common.run_git(repo, "hash-object", "-t", "tree", "--stdin", input_text="").strip()


def pending_commits(repo, settings):
    """(원격에 없는 커밋 목록(오래된 순), 비교 기준) — 원격 브랜치가 아직 없으면 기준은 빈 트리."""
    if not common.has_head(repo):
        return [], None
    ref = remote_ref(settings)
    if ref_exists(repo, ref):
        return common.run_git(repo, "rev-list", "--reverse", f"{ref}..HEAD").split(), ref
    return common.run_git(repo, "rev-list", "--reverse", "HEAD").split(), empty_tree(repo)


def check_branch(repo, settings):
    """지금 브랜치가 설정의 푸시 브랜치인지 확인. 아니면 멈춤 (다른 브랜치의 작업이 푸시 브랜치로 올라가지 않도록)."""
    branch = settings["branch"]
    current = common.current_branch(repo)
    if not current:
        common.fail(f"지금 브랜치가 아니라 특정 커밋을 보고 있는 상태(detached HEAD)입니다. 푸시하지 않습니다.\n"
                    f"→ GitHub Desktop 위쪽 Current branch에서 '{branch}'를 고른 뒤 다시 푸시하세요.\n"
                    "  (이 상태에서 커밋한 것이 있으면 옮기기 전에 GitHub Desktop의 Branch → New branch로 먼저 보관하세요. 그냥 옮기면 그 커밋은 어느 브랜치에도 속하지 않습니다)\n"
                    f"  명령줄: git checkout {branch}")
    if current != branch:
        common.fail(f"지금 브랜치는 '{current}'이고, 팀이 올리는 브랜치는 '{branch}'입니다. 푸시하지 않습니다.\n"
                    f"→ GitHub Desktop 위쪽 Current branch에서 '{branch}'를 고른 뒤 다시 푸시하세요.\n"
                    f"  (팀이 '{current}' 브랜치에 올리기로 정했을 때만 Tools → Git → 설정의 '브랜치'를 {current}로 바꿉니다)\n"
                    f"  명령줄: git checkout {branch}")


def fetch_remote(repo, remote):
    """원격 정보를 받아 옵니다. 실패하면 잠시 뒤 한 번 더 시도하고, 그래도 실패·시간 초과면 원인과 함께 멈춤.

    GitHub Desktop이 백그라운드에서 같은 순간에 fetch하면 잠금 충돌로 실패할 수 있어 한 번 더 시도합니다.
    두 번째는 1.0.1처럼 로그인을 허용해 실행합니다 (비공개 저장소에서 Unity가 실행하면 첫 방식으로는 로그인을 못 받는 경우).
    """
    args = ["git", "-C", repo, "fetch", "-q", remote]
    code, error = common.run_quiet_with_error(args, FETCH_TIMEOUT_SECONDS)
    first = (code, error)
    if code not in (0, None):
        time.sleep(FETCH_RETRY_DELAY_SECONDS)
        code, error = common.run_quiet_with_error(args, FETCH_LOGIN_TIMEOUT_SECONDS, allow_login=True)
        if code not in (0, None) and not error:
            error = first[1]
    if code is None:
        common.fail(f"원격({remote}) 확인이 끝나지 않았습니다(GitHub 로그인 창이 떠 있거나 인터넷이 느림). 푸시하지 않습니다.\n"
                    "→ 로그인 창이 있으면 로그인하고, 인터넷 연결과 GitHub Desktop 로그인을 확인한 뒤 다시 푸시하세요.")
    if code != 0:
        hint = common.explain_remote_error(remote, error)
        lines = [line.strip() for line in error.splitlines() if line.strip()]
        detail = " / ".join(lines[-3:]) if lines else "(git이 오류 내용을 남기지 않았습니다)"
        common.fail(f"원격({remote})을 확인하지 못했습니다. 푸시하지 않습니다.\n"
                    + (f"→ {hint}\n" if hint else "→ 잠시 뒤 다시 푸시해 보고, 같으면 아래 git 오류를 알려 주세요.\n")
                    + f"git 오류(종료 코드 첫 시도 {first[0]}, 다시 시도 {code}): {detail}")


def bundle_message(repo, settings):
    """원격 확인 후 (묶음 커밋 설명, 올릴 커밋 수, 원격 브랜치가 있는지)."""
    remote, branch = settings["remote"], settings["branch"]
    common.fail_if_in_progress(repo, "푸시")
    check_branch(repo, settings)
    fetch_remote(repo, remote)
    ref = remote_ref(settings)
    has_remote = ref_exists(repo, ref)
    if has_remote:
        behind = common.run_git(repo, "rev-list", "--count", f"HEAD..{ref}").strip() if common.has_head(repo) else "1"
        if behind != "0":
            common.fail(f"원격({remote}/{branch})에 이 PC에 없는 커밋이 {behind}개 있습니다. "
                        "먼저 받아서(Pull) 합친 뒤 푸시하세요. (푸시하지 않음)")
    commits, base = pending_commits(repo, settings)
    if not commits:
        common.fail("올릴 커밋이 없습니다.")

    items = []
    for c in commits:
        for item in common.parse_work_items(common.run_git(repo, "log", "-1", "--format=%B", c)):
            if item not in items:
                items.append(item)
    name_status = [r for r in common.run_git(repo, "diff", "--name-status", "-M", base, "HEAD").splitlines() if r.strip()]
    if not items:
        items = [common.rule_summary(name_status)]
    return common.build_message(items, name_status), len(commits), has_remote


def cmd_status(project, settings):
    s = load_state(project)
    interval = int(settings["pushIntervalMinutes"])
    print(f"마지막 푸시: {s['last_push'] or '-'}")
    minutes = elapsed_minutes(s)
    if minutes is None:
        print("카운트: 멈춤 (다음 작업을 시작할 때 --start)")
        return
    print(f"카운트 시작: {s['timer_start']} / 지난 시간: {minutes}분")
    print("푸시할 차례입니다 (지금 작업이 끝나면 푸시)" if minutes >= interval else f"푸시까지 {interval - minutes}분 남음")


def cmd_status_json(project, repo, settings):
    s = load_state(project)
    minutes = elapsed_minutes(s)
    interval = int(settings["pushIntervalMinutes"])
    commits, _ = pending_commits(repo, settings)
    data = {
        "lastPush": s["last_push"] or "",
        "timerStart": s["timer_start"] or "",
        "elapsedMinutes": -1 if minutes is None else minutes,
        "intervalMinutes": interval,
        "isDue": minutes is not None and minutes >= interval,
        "pendingCommits": len(commits),
        "remote": settings["remote"],
        "branch": settings["branch"],
        "currentBranch": common.current_branch(repo),
        "operation": common.operation_in_progress(repo),
        "firstPush": not ref_exists(repo, remote_ref(settings)),
        "bundleCommit": bool(settings["bundleCommit"]),
    }
    print(json.dumps(data, ensure_ascii=False))


def cmd_start(project, settings):
    s = load_state(project)
    if s["timer_start"]:
        print(f"이미 카운트 중: {s['timer_start']}")
    else:
        s["timer_start"] = now().isoformat()
        save_state(project, s)
        print(f"카운트 시작: {s['timer_start']}")
    cmd_status(project, settings)


def cmd_push(project, repo, settings, dry_run):
    message, count, has_remote = bundle_message(repo, settings)
    # 원격에 브랜치가 없는 첫 푸시는 묶지 않음: 이 도구로 만들지 않은 기존 기록까지 하나로 합쳐 버리지 않도록
    bundle = bool(settings["bundleCommit"]) and has_remote
    remote, branch = settings["remote"], settings["branch"]
    target = f"{remote}/{branch}"
    if bundle:
        print(f"개별 커밋 {count}개 → 묶음 커밋 1개로 합쳐 올림 → {target}\n--- 묶음 커밋 설명 ---\n{message}")
    else:
        if settings["bundleCommit"]:
            print(f"원격에 {branch} 브랜치가 없어 첫 푸시는 묶지 않고 커밋 {count}개를 그대로 올립니다. 다음 푸시부터 묶습니다.")
        print(f"올릴 커밋 {count}개 → {target}" + ("" if settings["bundleCommit"] else " (묶음 커밋 끔, 개별 커밋 그대로)"))
        commits, _ = pending_commits(repo, settings)
        shown = commits[-LOG_PREVIEW_LIMIT:]
        for c in reversed(shown):
            print("  " + common.run_git(repo, "log", "-1", "--format=%h %s", c).strip())
        if len(commits) > len(shown):
            print(f"  … 외 {len(commits) - len(shown)}개")
    if dry_run:
        print("(미리보기: 아무것도 바꾸지 않음)")
        return
    if common.run_git(repo, "diff", "--cached", "--name-only").strip():
        common.fail("이미 스테이징된 변경이 있습니다. 스테이징을 비운 뒤 다시 실행하세요. (푸시하지 않음)")

    upstream = [] if has_remote else ["-u"]
    if bundle:
        # 개별 커밋을 합친 묶음 커밋 1개: 내용은 지금 HEAD와 같고 부모는 원격 브랜치 (첫 푸시는 묶지 않으므로 원격 브랜치가 늘 있음).
        # 먼저 묶음 커밋을 푸시하고, 성공한 뒤에만 로컬 브랜치를 옮김 → 푸시가 실패하면 로컬은 그대로.
        # 작업 폴더·스테이징은 건드리지 않음 (git reset --soft).
        with tempfile.NamedTemporaryFile("w", delete=False, encoding="utf-8", suffix=".txt") as f:
            f.write(message)
            path = f.name
        try:
            parent = ["-p", remote_ref(settings)]
            squashed = common.run_git(repo, "commit-tree", "HEAD^{tree}", *parent, "-F", path).strip()
        finally:
            os.remove(path)
        common.run_git(repo, "push", remote, f"{squashed}:refs/heads/{branch}")
        common.run_git(repo, "reset", "-q", "--soft", squashed, check=False)
        moved = common.run_git(repo, "rev-parse", "HEAD", check=False).strip() == squashed
        if moved:
            # 원격 브랜치는 있었지만 로컬 브랜치에 추적 설정이 없던 경우를 위해 (이미 있으면 같은 값)
            common.run_git(repo, "branch", "-q", f"--set-upstream-to={remote}/{branch}", check=False)
    else:
        common.run_git(repo, "push", *upstream, remote, f"HEAD:refs/heads/{branch}")
        moved = True
    s = load_state(project)
    s["last_push"] = now().isoformat()
    s["timer_start"] = None
    save_state(project, s)
    if not moved:
        # 원격에는 묶음 커밋이 올라갔는데 로컬 브랜치를 옮기지 못함: 그대로 두면 다음 푸시가 '원격이 앞서 있음'으로 멈춤
        common.fail(f"원격에는 묶음 커밋 {squashed[:7]}을(를) 올렸지만 로컬 브랜치를 그 커밋으로 옮기지 못했습니다.\n"
                    f"작업 폴더 변경은 그대로입니다. 저장소 폴더에서 다음을 실행하세요:\n  git reset --soft {squashed}")
    print(f"푸시 완료: {common.run_git(repo, 'log', '-1', '--format=%h %s').strip()}\n카운트 멈춤 (다음 작업을 시작할 때 --start)")


def main():
    common.setup_console()
    p = argparse.ArgumentParser(description="Git Sync 푸시 도구 (시간 규칙, 묶음 커밋)")
    p.add_argument("--project", default="", help="Unity 프로젝트 폴더 (기본: 현재 폴더)")
    p.add_argument("--repo", default="", help="Git 저장소 폴더 (기본: 설정 repo → 프로젝트가 들어 있는 저장소)")
    g = p.add_mutually_exclusive_group()
    g.add_argument("--start", action="store_true", help="작업 시작: 카운트가 멈춰 있으면 시작")
    g.add_argument("--status", action="store_true", help="카운트 상태 표시")
    g.add_argument("--status-json", action="store_true", help="상태를 JSON으로 출력")
    g.add_argument("--dry-run", action="store_true", help="묶음 커밋 설명 미리보기")
    a = p.parse_args()

    project_dir = os.path.abspath(a.project or os.getcwd())
    settings = common.load_settings(project_dir)
    if a.start:
        cmd_start(project_dir, settings)
        return
    if a.status:
        cmd_status(project_dir, settings)
        return
    project, repo, _ = common.resolve_paths(project_dir, settings, a.repo)
    if a.status_json:
        cmd_status_json(project, repo, settings)
    else:
        cmd_push(project, repo, settings, a.dry_run)


if __name__ == "__main__":
    main()
