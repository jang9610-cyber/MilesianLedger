# 개발 브랜치 안내

## 기본 브랜치

| 브랜치 | 역할 | 변경 반영 기준 |
| --- | --- | --- |
| `main` | 배포 가능한 소스의 기준 | 계산·화면 검증과 배포 패키지 확인을 마친 변경 |
| `develop` | 다음 버전의 개발 통합 | 완료한 기능과 수정 |

일상적인 개발은 `develop`에서 시작합니다. `main`에는 배포 준비를 마친 변경을 반영합니다. 두 브랜치는 처음에는 같은 커밋을 가리키며, 이후 개발 내용을 커밋하면 이력이 나뉩니다.

브랜치는 소스 이력을 구분합니다. 같은 작업 폴더에서 브랜치를 전환해도 Git에서 제외한 `dist/` 빌드와 개인 진행 상태가 브랜치별로 분리되거나 자동으로 다시 빌드되지는 않습니다. 특정 브랜치의 앱이 필요하면 해당 브랜치로 전환한 뒤 빌드하세요.

## 작업별 브랜치

작업을 시작할 때 필요한 브랜치를 만들고, 병합한 뒤 정리합니다. 미리 빈 기능 브랜치를 여러 개 유지하지 않습니다.

| 이름 형식 | 시작점 | 병합 대상 | 용도 |
| --- | --- | --- | --- |
| `feature/작업명` | `develop` | `develop` | 기능 추가 |
| `fix/작업명` | `develop` | `develop` | 일반 오류 수정 |
| `hotfix/작업명` | `main` | `main`, 이후 `develop` | 배포판의 긴급 수정 |

작업명은 `feature/preset-export`처럼 짧은 영문과 하이픈으로 작성합니다. 앱과 Cloudflare 서버는 같은 저장소에서 관리하며, 서로 맞물리는 요청·응답 변경은 같은 작업 브랜치에 포함합니다. 별도의 영구 서버 브랜치는 두지 않습니다.

## 개발과 배포 흐름

1. `develop`에서 작업 브랜치를 만듭니다. 간단한 문서 정리는 `develop`에서 바로 진행할 수 있습니다.
2. 변경과 관련된 검증을 실행한 뒤 커밋하고 `develop`에 병합합니다.
3. 배포 준비 시 계산·화면 검증을 실행하고, `main`에 변경을 반영한 뒤 배포 패키지를 만듭니다.
4. 배포 ZIP은 GitHub Releases 등 별도 배포 항목에 첨부합니다. 소스 브랜치에 실행 파일이나 개인 데이터를 포함하지 않습니다.

```powershell
# 개발 기준 브랜치로 전환
git switch develop

# 계산과 화면 검증
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\test.ps1 -IncludeUi

# 현재 브랜치의 배포 ZIP 생성
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\package.ps1
```

Worker를 변경한 경우에는 `server/cloudflare/worker-tools.ps1 test`로 서버 검증도 실행합니다. Git 브랜치를 만들거나 병합하는 것만으로 GitHub 업로드나 Cloudflare 배포가 실행되지는 않습니다. 원격 저장소 업로드와 Worker 배포는 각각 별도 작업입니다.
