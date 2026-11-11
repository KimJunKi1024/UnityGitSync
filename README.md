# Git Sync (Unity 커밋·푸시 도구)

Unity 프로젝트 저장소에서 바로 **고른 파일만 로컬 커밋**하고, **정해진 시간마다 묶음 커밋 1개로 푸시**하는 도구입니다.
커밋 설명은 형식이 고정되어 있어 기록이 한눈에 보입니다.

```
2026-11-11 PM 5:24

작업 내용
- 스크립트 2개 수정: PlayerController, UnitGenerator / 씬 Village 수정

파일: 추가 1, 수정 2
총 작업한 내용: 3개
```

운영 규칙은 [`Documentation~/Git 운영 가이드.md`](Documentation~/Git%20운영%20가이드.md)에 있습니다.

## 필요한 것
- Unity 2021.3 이상
- Git (명령줄 `git`이 PATH에 있어야 함)
- Python 3.8 이상
- (선택) 자동 요약을 쓰려면 Claude Code 명령줄 `claude`

## 설치
Unity에서 **Window → Package Manager → 왼쪽 위 + → Add package from git URL…** 에 다음을 넣습니다.

```
https://github.com/KimJunKi1024/UnityGitSync.git
```

특정 버전으로 고정하려면 끝에 `#v1.0.0`처럼 태그를 붙입니다.
저장소가 비공개(Private)이면 설치하는 PC에 GitHub 로그인(Git 자격 증명)이 되어 있어야 합니다.

또는 프로젝트의 `Packages/manifest.json`의 `dependencies`에 직접 넣어도 됩니다.

```json
"com.teamelarin.gitsync": "https://github.com/KimJunKi1024/UnityGitSync.git"
```

## 설정
**Tools → Git → 설정** (또는 Edit → Project Settings → Git Sync)

| 항목 | 저장 위치 | 기본값 | 설명 |
|---|---|---|---|
| 저장소 폴더 | `GitSyncSettings.json` | 비움 | 비우면 프로젝트 폴더가 들어 있는 Git 저장소. 프로젝트가 저장소의 하위 폴더여도 됨 |
| 원격 이름 / 브랜치 | 〃 | `origin` / `main` | 푸시할 곳 |
| 푸시 주기 (분) | 〃 | 60 | 카운트 시작부터 이 시간이 지나면 푸시 차례 |
| 묶음 커밋 만들기 | 〃 | 켬 | 마지막 푸시 뒤의 개별 커밋을 작업 내용·파일 수를 합산한 묶음 커밋 1개로 합쳐 그것만 올림 (개별 커밋은 로컬에도 남지 않음). 끄면 개별 커밋을 그대로 올림 |
| 작업 기록 폴더 | 〃 | 비움(끔) | `entryNNN.md` 폴더(프로젝트 기준). 새 항목의 첫 줄 제목을 작업 내용으로 씀 |
| 작업 내용 자동 요약 | 〃 | 끔 | `claude`로 바꾸면 바뀐 내용을 claude가 요약 |
| Python 경로 | EditorPrefs (PC별) | `python` | |
| claude 경로 | EditorPrefs (PC별) | 비움(PATH) | Unity가 PATH를 못 볼 때 `claude.exe` 전체 경로 |

`GitSyncSettings.json`은 프로젝트 루트에 생기며 **Git에 올려 팀이 같이 씁니다.**

### 작업 내용을 정하는 순서
1. 작업 기록 폴더에 저장소에 아직 없는 새 항목이 있으면 그 제목 (`- 334. 제목`)
2. 커밋 창(또는 `--title`)에 직접 쓴 제목
3. 자동 요약이 켜져 있으면 `claude -p`의 한두 줄 요약 (30초 안에 못 받거나 실패하면 4로)
4. 파일 종류·이름 요약 (스크립트·씬·프리팹·이미지·기타)

작업 기록 항목 첫 줄은 `### 334. 제목 (2026-11-11 PM 5:24)` 형식을 읽습니다(번호·날짜 괄호는 빼고 제목만).

## 쓰는 법 (Unity)
**Tools → Git → 커밋 창**
- 커밋하지 않은 변경을 파일별로 보여 줍니다. 체크한 파일만 로컬 커밋합니다(짝 `.meta`는 함께 체크됨).
- 플레이 모드·컴파일 중·콘솔 에러가 있으면 커밋 버튼이 꺼집니다. 저장하지 않은 씬이 있으면 저장할지 묻습니다.
- 원격이 앞서 있으면 경고를 보여 줍니다.
- 아래쪽 **푸시**: 카운트 상태·올릴 커밋 수를 보여 주고, **카운트 시작** / **푸시…**(미리보기 확인 후 푸시) 버튼이 있습니다.

## 쓰는 법 (명령줄, Claude Code 등)
에디터를 한 번 켜면 도구가 `Library/GitSync/Tools/`에 복사됩니다. 프로젝트 폴더에서:

```
python Library/GitSync/Tools/gitsync_commit.py --dry-run                 # 바뀐 파일과 커밋 설명 미리보기
python Library/GitSync/Tools/gitsync_commit.py --files-from 목록.txt      # 목록(저장소 기준 경로, 한 줄에 하나)의 파일만 커밋
python Library/GitSync/Tools/gitsync_commit.py --title "로그인 화면 고치기" # 작업 내용 직접 쓰기
python Library/GitSync/Tools/gitsync_commit.py                           # 바뀐 파일 전체 커밋

python Library/GitSync/Tools/gitsync_push.py --start      # 작업 시작 때 (카운트가 멈춰 있으면 지금부터)
python Library/GitSync/Tools/gitsync_push.py --status     # 지난 시간·푸시 차례
python Library/GitSync/Tools/gitsync_push.py --dry-run    # 묶음 커밋 설명 미리보기
python Library/GitSync/Tools/gitsync_push.py              # 개별 커밋을 묶음 1개로 합쳐 푸시 → 카운트 멈춤
```

푸시는 묶음 커밋을 먼저 원격에 올리고, 성공한 뒤에만 로컬 브랜치를 그 커밋으로 옮깁니다(`git reset --soft`). 푸시가 실패하면 로컬은 그대로이고, 커밋하지 않은 변경은 어느 경우에도 건드리지 않습니다.

다른 폴더에서 실행할 때는 `--project <Unity 프로젝트 폴더>`를 붙입니다.

## 멈추는 경우
- 커밋: 고르지 않은 파일이 이미 스테이징돼 있음, 커밋할 파일이 없음
- 푸시: 원격에 이 PC에 없는 커밋이 있음(먼저 Pull), 스테이징된 변경이 있음, 올릴 커밋이 없음, 원격 확인 실패
- 강제 푸시는 하지 않습니다.

## 주의
- Unity 기본 `.gitignore`처럼 `Library/`가 Git에서 빠져 있어야 합니다(카운트 상태·도구 복사본이 거기 있음).
- 커밋 메시지에 공동 작성자·AI 표시 줄을 넣지 않습니다.

## 폴더 구성
```
package.json
Editor/                  커밋 창, 설정 화면, 도구 실행 (에디터 전용 어셈블리)
Tools~/                  파이썬 도구 (Unity가 가져오지 않는 폴더)
  gitsync_commit.py      로컬 커밋
  gitsync_push.py        시간 규칙 + 묶음 커밋 푸시
  gitsync_common.py      설정·형식 공용
Documentation~/          운영 가이드
```
