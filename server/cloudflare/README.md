# 밀레시안 장부 · Cloudflare 시세 서버

시장 통계의 개발 구성과 운영 적용 절차는 [시장 통계 문서](../../docs/MARKET_STATISTICS.md)에 있습니다. 기본 배포는 기존 교역 구성이고, 시장 구성은 별도 `wrangler.market.jsonc`이며 현재 통계 조회만 활성화하고 정기 수집은 비활성입니다.

밀레시안 장부의 시세 요청을 처리하는 Cloudflare Worker와 Durable Object 코드입니다. 앱은 공개 주소로 품목 이름과 페이지 커서만 보내며, 넥슨 API 키는 Cloudflare Secret `NEXON_API_KEY`에만 보관합니다. 앱 사용자에게 서버 설치나 API 키 입력은 필요하지 않습니다.

현재 앱에 포함된 공개 주소:

```text
https://restless-bread-9002milesianledger-api.jang9610.workers.dev
```

이 폴더에는 Worker 소스와 공개 배포 설정만 포함합니다. `node_modules/`·`.wrangler/`·`.dry-run/`, 실제 `.dev.vars`와 Secret은 Git과 데스크톱 ZIP에서 제외합니다. `server/src/`는 [Node.js 참고 구현](../README.md)이며 현재 Cloudflare 서비스와 별개입니다.

## 파일 구성

| 파일 | 역할 |
| --- | --- |
| `worker.mjs` | Worker 진입점, `AuctionCoordinator`, 117종 허용 품목 |
| `worker.test.mjs`, `market.test.mjs` | 외부 네트워크 없는 프록시·SQLite 수집 검증 |
| `wrangler.jsonc` | Worker 이름·일반 변수·Durable Object binding과 SQLite 저장소 등록 |
| `package.json` | Wrangler 버전과 npm 명령 |
| `worker-tools.ps1` | npm·npx 대신 Node.js를 직접 사용하는 실행 도우미 |
| `01-Check.cmd`, `02-Login.cmd`, `03-Deploy.cmd` | Windows에서 도우미를 실행하는 진입점 |
| `.dev.vars.example` | 로컬용 빈 비밀 설정 예시 |

## 새 PC와 새 Git 체크아웃에서 준비하기

서버 개발·배포에는 **Node.js 22 이상**이 필요합니다. [Node.js 공식 다운로드](https://nodejs.org/en/download)에서 LTS를 설치한 뒤 새 PowerShell을 열고 이 README가 있는 `server/cloudflare` 폴더로 이동합니다.

Git에는 의존성을 포함하지 않으므로 **새 체크아웃에서는 먼저 `npm install`을 실행**해야 합니다. 도우미는 Node를 직접 실행하지만 Wrangler를 자동으로 설치하지는 않습니다.

```powershell
npm install
powershell -NoProfile -ExecutionPolicy Bypass -File .\worker-tools.ps1 test
powershell -NoProfile -ExecutionPolicy Bypass -File .\worker-tools.ps1 check
```

의존성이 이미 설치된 폴더에서는 `npm install`을 반복할 필요가 없습니다. `npm`·`npx` 명령이 PATH에 없어도 아래 도우미 명령을 사용할 수 있습니다. 도우미는 설치된 `node.exe`를 먼저 찾고, 없으면 `%USERPROFILE%\.cache\codex-runtimes\codex-primary-runtime\dependencies\node\bin\node.exe`를 확인합니다. 이 대체 경로는 해당 런타임이 설치된 PC에서만 존재합니다.

`test`는 Node 기본 테스트 모듈만 사용하므로 **Wrangler 의존성을 설치하지 않아도** 실행할 수 있습니다. Node도 찾지 못하는 경우에는 먼저 Node.js를 설치하세요.

## 기존 Worker에 배포하기

앞의 `test`와 `check`가 통과한 뒤 운영할 Worker를 소유한 Cloudflare 계정으로 로그인합니다.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\worker-tools.ps1 login
powershell -NoProfile -ExecutionPolicy Bypass -File .\worker-tools.ps1 deploy
```

- `test`: 가짜 넥슨 응답과 메모리 저장소로 오프라인 검증합니다.
- `check`: Wrangler의 배포 사전 검사입니다. `.dry-run`에 로컬 결과를 만들며 원격 Worker를 변경하지 않습니다.
- `login`: 브라우저에서 사용자가 Cloudflare 계정을 인증합니다.
- `deploy`: `wrangler.jsonc`에 지정한 Worker에 새 버전을 배포하고 binding·SQLite 저장소 등록·일반 변수를 적용합니다.

현재 Worker 이름은 `restless-bread-9002milesianledger-api`입니다. 기존 서비스를 갱신할 때는 이 이름과 클래스·binding 이름을 유지하세요. 다른 계정에서 별도 서비스를 만들 경우 자신의 Worker 이름과 앱의 공개 `BaseUrl`을 함께 설정합니다.

**현재 배포 설정은 `AUCTION_ENABLED=true`입니다.** `deploy`는 이 일반 변수도 반영하므로 대시보드에서 조회를 일시 중단했더라도 로컬 설정이 `true`이면 다시 활성화될 수 있습니다. 운영 상태를 바꿀 때 대시보드와 `wrangler.jsonc`의 같은 값을 함께 맞추세요. `keep_vars`를 사용해도 파일에 명시한 변수 값은 배포에 반영됩니다.

새 서비스에 아직 키를 등록하지 않았다면 `AUCTION_ENABLED=false`로 구성하고 Cloudflare Settings → Variables and Secrets에 `NEXON_API_KEY`를 Secret으로 등록한 뒤 활성화합니다. 키 값은 `worker.mjs`, `wrangler.jsonc`, Git 파일에 넣지 않습니다. 같은 공개 주소를 유지하면 서버의 키를 교체해도 앱을 다시 배포할 필요는 없습니다.

코드와 binding·저장소 등록은 함께 필요합니다. 대시보드 Edit code에 코드만 붙여넣는 것으로 최초 구성이 끝나지는 않습니다. 로컬 실행은 다음 명령을 사용합니다.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\worker-tools.ps1 dev
```

새 SQLite Durable Object의 원격 Preview에는 제약이 있으므로 로컬 실행 또는 실제 배포 주소로 확인합니다.

## Cloudflare 설정

| 이름 | 종류 | 현재 배포 설정 |
| --- | --- | --- |
| `NEXON_API_KEY` | Secret | Cloudflare에서만 관리, 저장소에 값 없음 |
| `AUCTION_ENABLED` | 일반 텍스트 | `true` |
| `UPSTREAM_REQUESTS_PER_24H` | 일반 텍스트 | `10000` |
| `AUCTION_COORDINATOR` | Durable Object binding | `AuctionCoordinator` 클래스 |

Worker의 모든 조회는 이름 `global-v1`인 공통 Durable Object로 전달됩니다. 최근 24시간의 실제 넥슨 호출 예약을 저장소에 먼저 기록하며, 실패한 요청도 예산에 포함합니다. 현재 배포의 10,000회는 서버의 공용 운영 예산이며 넥슨이 발급한 실제 키 한도나 앱 한 번의 갱신 상한을 뜻하지 않습니다. 다른 서비스에서 같은 키를 쓰는 호출량은 이 서버가 알 수 없습니다. 변수 미설정 시 코드 기본값은 500회이며, 현재 구현의 설정 허용 범위는 하루 1~10,000회·초당 1~5회입니다. 운영 예산을 올려도 기존 요청 기록은 유지합니다. 공식 서비스 키의 전체 한도를 처리하는 대규모 수집 서버와는 처리 범위가 다릅니다.

## 연결 확인

```text
https://restless-bread-9002milesianledger-api.jang9610.workers.dev/health
```

`/health`는 정상 기동 시 HTTP 200과 `{"status":"ok"}`를 반환하며 넥슨을 호출하지 않습니다. 이 응답만으로 넥슨 인증이나 잔여 한도를 판정할 수는 없습니다.

실제 경매장 응답은 앱의 갱신 버튼 또는 다음 경로로 확인합니다. 이 요청은 캐시가 없으면 공용 호출 예산을 사용합니다.

```text
https://restless-bread-9002milesianledger-api.jang9610.workers.dev/v1/auction/list?item_name=가는%20실뭉치
```

성공 응답에는 `auction_item`, `next_cursor`, `fetched_at`이 있습니다. 매물이 없으면 정상 응답의 `auction_item`이 빈 목록일 수 있습니다.

**조회 비활성 또는 키·binding 미설정** 상태에서는 HTTP 503과 아래 오류가 반환됩니다. 활성화된 서비스의 성공 응답과 구분하세요.

```json
{
  "error": {
    "code": "PROXY_NOT_CONFIGURED",
    "message": "경매장 연결이 아직 설정되지 않았습니다."
  }
}
```

판정에는 HTTP 상태와 `error.code`를 사용합니다. 넥슨 인증·공용 예산·상위 서비스 한도 오류도 각각 정해진 코드로 전달하며 앱은 자동 재시도하지 않습니다. Cloudflare 자체 차단 응답은 Worker나 넥슨의 응답과 구분해 확인합니다.

## 앱 연결과 서버 동작

앱의 `data/auction-proxy.json`에는 공개 기본 주소만 넣습니다. `/auction`이나 `/v1/auction/list`를 뒤에 붙이지 않습니다. 앱이 경로를 추가합니다. 이 배포판은 위 공개 주소가 이미 설정되어 있습니다.

Worker는 GET, 지정 경로, `item_name`·`cursor`만 받습니다. 미스릴광석 계열의 이전 검색명 2개는 공식 검색명으로 변환하고, 응답에는 요청한 이름을 유지하여 이전 앱과 호환합니다. `/v1/auction/list`와 호환 경로 `/auction`을 지원하며 허용된 경매장 품목 117종만 조회합니다. NPC 구매 재료는 제외하고 임의 URL이나 요청자의 인증 헤더를 넥슨에 전달하지 않습니다.

같은 품목·커서의 성공 응답은 60초간 재사용하며 최초 `fetched_at` 시각을 유지합니다. 메모리 캐시는 객체 재시작 시 사라질 수 있지만 저장된 호출 예산은 이어집니다. 넥슨 요청은 직렬화하고 기본 초당 최대 5회로 제한합니다. 대기열·대기 시간·응답 크기도 제한하며 주기적 수집이나 자동 재시도는 없습니다.

공개 주소 자체는 앱 사용자 인증을 제공하지 않습니다. 공용 예산과 품목 제한은 모든 요청에 적용되며 운영 규모에 맞는 이용자별 제한은 Cloudflare 앞단에서 별도로 구성할 수 있습니다. 넥슨 오류 원문이나 Secret 값은 응답·로그에 넣지 않습니다. 넥슨의 알려진 검색 조건 오류(`OPENAPI00003`, `OPENAPI00004`)는 `400 PROXY_ITEM_QUERY_REJECTED`로 구분하여 앱이 다음 품목을 조회할 수 있게 합니다. 인증·한도·점검·알 수 없는 서비스 오류는 전체 중단 대상으로 유지합니다.

앱이 갱신을 중지하면 추가 페이지 조회를 멈춥니다. 이미 서버에 전달된 요청의 즉시 중단 여부는 연결 처리에 따라 달라질 수 있습니다.

## 검증과 허용 품목 갱신

Worker 오프라인 테스트는 가짜 넥슨 응답과 메모리 저장소를 사용해 요청 검사·캐시·호출 예산·오류 처리를 검증합니다. 실제 넥슨 키나 API 호출은 사용하지 않습니다.

앱의 품목명이나 제작법을 바꾸면 저장소 루트에서 다음 명령으로 `server/item-names.json`과 Worker의 `ITEM_NAMES`를 함께 갱신하고, Worker를 다시 배포합니다.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\update-proxy-items.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\update-proxy-items.ps1 -Check
```

## 공식 문서

- [Cloudflare Secrets](https://developers.cloudflare.com/workers/configuration/secrets/)
- [Durable Object 클래스·연결 설정](https://developers.cloudflare.com/durable-objects/get-started/)
- [SQLite 저장소 등록](https://developers.cloudflare.com/durable-objects/reference/durable-objects-migrations/)
- [SQLite Durable Object의 원격 Preview 제약](https://developers.cloudflare.com/durable-objects/reference/environments/#remote-development)
