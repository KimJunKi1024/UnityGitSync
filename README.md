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
- Git 2.25 이상 (명령줄 `git`이 PATH에 있어야 함)
- Python 3.8 이상 (추가 패키지 없음, 표준 라이브러리만)
- (선택) 자동 요약을 쓰려면 Claude Code 명령줄 `claude`

## 설치
Unity에서 **Window → Package Manager → 왼쪽 위 + → Add package from git URL…** 에 다음을 넣습니다.

```
https://github.com/KimJunKi1024/UnityGitSync.git
```

특정 버전으로 고정하려면 끝에 `#v1.0.1`처럼 태그를 붙입니다.
저장소가 비공개(Private)이면 설치하는 PC에 GitHub 로그인(Git 자격 증명)이 되어 있어야 합니다.

또는 프로젝트의 `Packages/manifest.json`의 `dependencies`에 직접 넣어도 됩니다.

```json
"com.teamelarin.gitsync": "https://github.com/KimJunKi1024/UnityGitSync.git"
```

## 설정
**Tools → Git → 설정** (또는 Edit → Project Settings → Git Sync)

| 항목 | 저장 위치 | 기본값 | 설명 |
|---|---|---|---|
| 저장소 폴더 | `ProjectSettings/GitSyncSettings.json` | 비움 | 비우면 프로젝트 폴더가 들어 있는 Git 저장소. 프로젝트가 저장소의 하위 폴더여도 됨 |
| 원격 이름 / 브랜치 | 〃 | `origin` / `main` | 푸시할 곳. 지금 브랜치가 이 브랜치일 때만 푸시함 |
| 푸시 주기 (분) | 〃 | 60 | 카운트 시작부터 이 시간이 지나면 푸시 차례 |
| 묶음 커밋 만들기 | 〃 | 켬 | 마지막 푸시 뒤의 개별 커밋을 작업 내용·파일 수를 합산한 묶음 커밋 1개로 합쳐 그것만 올림 (개별 커밋은 로컬에도 남지 않음). 끄면 개별 커밋을 그대로 올림 |
| 작업 기록 폴더 | 〃 | 비움(끔) | `entryNNN.md` 폴더(프로젝트 기준). 새 항목의 첫 줄 제목을 작업 내용으로 씀 |
| 작업 내용 자동 요약 | 〃 | 끔 | `claude`로 바꾸면 바뀐 내용을 claude가 요약 |
| Python 경로 | EditorPrefs (PC별) | 비움(자동) | 비우면 차례로 실행해 3.8 이상을 찾음 (Windows: `python` → `py` → `python3`, macOS·Linux: `python3` → `python` → Homebrew 경로) |
| claude 경로 | EditorPrefs (PC별) | 비움(PATH) | Unity가 PATH를 못 볼 때 `claude.exe` 전체 경로 |

`ProjectSettings/GitSyncSettings.json`에 저장되며 **Git에 올려 팀이 같이 씁니다.**

### 작업 내용을 정하는 순서
1. 작업 기록 폴더에 저장소에 아직 없는 새 항목이 있으면 그 제목 (`- 334. 제목`)
2. 커밋 창(또는 `--title`)에 직접 쓴 제목
3. 자동 요약이 켜져 있으면 `claude -p`의 한두 줄 요약 (30초 안에 못 받거나 실패하면 4로)
4. 파일 종류·이름 요약 (스크립트·씬·프리팹·이미지·기타)

작업 기록 항목 첫 줄은 `### 334. 제목 (2026-11-11 PM 5:24)` 형식을 읽습니다(번호·날짜 괄호는 빼고 제목만).

## 쓰는 법 (Unity)
**Tools → Git → Git Sync 패널** (처음 쓰는 PC에서 먼저)
- 패키지 버전과 함께 **필요한 프로그램**(git 2.25 이상·Python 3.8 이상·claude는 선택)과 **이 프로젝트** 항목(도구·Git 저장소·설정 파일·원격과 지금 브랜치)을 실제로 실행해 ✔/✖로 보여 줍니다.
- 빠진 프로그램에는 **[Git 설치]·[Python 설치]** 버튼이 있습니다. Windows에서 winget이 있으면 확인 후 새 창에서 설치하고, 없거나 macOS면 다운로드 페이지를 엽니다. 표준 위치에 설치하면 Unity를 다시 켜지 않고 '다시 확인'만 눌러도 찾습니다.
- 항목은 '커밋에 필요'(빨강)와 '푸시에 필요'·선택(주황)으로 나뉩니다. 원격이 없거나 첫 커밋 전인 새 저장소, 다른 브랜치에 있을 때도 로컬 커밋은 할 수 있습니다.
- 커밋에 필요한 것이 빠져 있으면 **커밋 창·커밋·푸시를 쓸 수 없고**, 누르면 이 패널이 대신 뜹니다. 패키지를 처음 설치·업데이트했을 때도 에디터를 켜면 한 번 자동으로 뜹니다.
- 아래쪽에 커밋 창·설정·사용법·운영 가이드·변경 기록·GitHub 버튼이 있습니다.

**Tools → Git → 커밋 창**
- 커밋하지 않은 변경을 파일별로 보여 줍니다. 체크한 파일만 로컬 커밋합니다(짝 `.meta`는 함께 체크됨).
- 플레이 모드·컴파일 중·콘솔 에러가 있으면 커밋 버튼이 꺼집니다. 저장하지 않은 씬이 있으면 저장할지 묻습니다.
- 원격이 앞서 있으면 경고를 보여 줍니다.
- `git rm --cached`로 추적만 해제한 파일은 **해제**로 한 줄에 보이고, 체크하면 파일은 PC에 남긴 채 Git에서만 빼는 커밋이 됩니다(`.gitignore`에 걸린 파일도 됨).
- `Packages/manifest.json`에서 패키지가 빠지는 변경이 있으면 경고하고, 그 파일을 커밋할 때 한 번 더 확인합니다.
- 커밋 도중 실패하면 스테이징을 실행 전 상태로 되돌립니다.
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

원격에 브랜치가 아직 없는 **첫 푸시는 묶지 않고 기존 기록을 그대로** 올립니다(이 도구로 만들지 않은 지난 커밋까지 하나로 합쳐 버리지 않도록). 다음 푸시부터 묶습니다.

다른 폴더에서 실행할 때는 `--project <Unity 프로젝트 폴더>`를 붙입니다.

## 멈추는 경우
- 커밋: 브랜치가 아닌 커밋(detached HEAD)에 있음, 병합·리베이스·체리픽·bisect 등이 끝나지 않음, 고르지 않은 파일이 이미 스테이징돼 있음, 커밋할 파일이 없음
- 푸시: 지금 브랜치가 설정의 브랜치가 아님(detached HEAD 포함), 병합·리베이스·bisect 등이 끝나지 않음, 원격에 이 PC에 없는 커밋이 있음(먼저 Pull), 스테이징된 변경이 있음, 올릴 커밋이 없음, 원격 확인 실패·시간 초과(60초)
- 원격에는 올라갔는데 로컬 브랜치를 옮기지 못하면, 직접 실행할 `git reset --soft <커밋>` 명령을 알려 줍니다.
- 강제 푸시는 하지 않습니다.

## 주의
- Unity 기본 `.gitignore`처럼 `Library/`가 Git에서 빠져 있어야 합니다(카운트 상태·도구 복사본이 거기 있음).
- 설정의 **저장소 폴더**는 Git으로 팀과 공유됩니다. 이 PC의 절대 경로를 넣으면 다른 PC에서 저장소를 잘못 찾으므로 비워 두세요(설정 화면·패널·커밋 때 경고).
- 명령줄 도구도 커밋할 때 패키지 빠짐·저장소 폴더 절대 경로를 `주의:`로 알려 줍니다(멈추지는 않음). 먼저 `--dry-run`으로 확인하세요.
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
