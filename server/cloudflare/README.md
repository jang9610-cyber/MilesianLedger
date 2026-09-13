# 밀레시안 장부 · Cloudflare 공통 시세 서버

공식 경매장을 정기 수집하고 완료된 시세·통계 파일을 모든 앱에 제공하는 Worker입니다. 실제 넥슨 키는 Cloudflare Secret에만 두며 앱·Git·배포 ZIP에 포함하지 않습니다. 상세 구조와 예산 계산은 [시장 통계 문서](../../docs/MARKET_STATISTICS.md)에 있습니다.

이 문서는 구현과 설정 방법입니다. 정기 수집 및 구버전 시세 경로의 공통 데이터 전환은 아래 배포 변수로 결정되며, 현재 운영 상태를 고정해서 선언하지 않습니다. 실제 적용 결과는 [배포 기록](../../docs/releases/)을 확인합니다.

공개 주소:

```text
https://restless-bread-9002milesianledger-api.jang9610.workers.dev
```

## 요청과 데이터 흐름

```text
Cron → MarketCollector Alarm → AuctionCoordinator → NEXON API
              ↓
       SQLite 페이지 청크
              ↓ 완료본 집계
     manifest + 불변 gzip 파일
              ↓ 지역 캐시
     앱 공통 다운로드·로컬 조회
```

새 앱의 교역 시세 갱신과 시장 통계 화면은 버전 manifest를 확인한 뒤 변경됐을 때만 gzip 파일을 내려받습니다. 검색·정렬·페이지 이동·교역 가격 계산은 앱 로컬 데이터로 수행합니다. 서버의 공개 읽기 API가 수집을 시작하지 않습니다.

`SHARED_MARKET_QUOTES_ENABLED=true`이면 기존 베타의 `/auction`·`/v1/auction/list`도 완료된 공통 게시본만 읽습니다. 117종 허용 이름과 구버전 별칭을 유지하며 공통 데이터가 없다고 넥슨을 추가 호출하지 않습니다. 이 변수가 꺼져 있으면 기존 품목별 프록시가 유지됩니다.

## 파일

| 파일 | 역할 |
| --- | --- |
| `worker.mjs` | 공개 진입점, 구버전 경로, 공통 넥슨 요청 코디네이터 |
| `market-worker.mjs` | 정기 작업·Alarm·관리자 인증·공개 시장 API |
| `market-store.mjs`, `market-schema.mjs`, `market-chunks.mjs` | SQLite 묶음 저장, ID 중복 제거, 완료본 집계·예산 |
| `market-snapshot.mjs` | gzip·해시·버전·원자적 게시 |
| `*.test.mjs` | 실제 SQLite와 모의 upstream을 사용하는 오프라인 검증 |
| `wrangler.market.jsonc` | 시장 기능이 포함된 Worker 배포 설정 |
| `package.json` | Wrangler 의존성 및 검증 명령 |

`server/src/`는 [Node.js 자체 호스팅 참고 구현](../README.md)이며 이 Worker와 별개입니다. `node_modules/`, `.wrangler/`, `.dry-run*/`, 실제 `.dev.vars`와 개인 Secret은 Git·데스크톱 ZIP에서 제외합니다.

## 개발·배포

Node.js 24를 권장합니다. 시장 테스트는 Node의 내장 SQLite 모듈을 사용합니다. 새 체크아웃의 이 폴더에서 의존성을 설치한 다음 검증합니다.

```powershell
npm install
npm test
npm run check:market
```

검증은 실제 넥슨 API를 호출하지 않습니다. `check:market`은 원격을 변경하지 않는 dry-run입니다. 운영 Worker 계정에 로그인한 후 아래 명령으로 시장 설정 전체를 배포합니다.

```powershell
npx wrangler login
npx wrangler deploy --config wrangler.market.jsonc
```

시장 Worker는 **`wrangler.market.jsonc` 하나로 배포**합니다. `npm run deploy`, `worker-tools.ps1 deploy`, `03-Deploy.cmd` 모두 같은 설정을 사용합니다. 이전 교역 전용 설정 파일은 제거하여 재배포할 때 시장 binding·예약이 빠지는 것을 방지합니다.

`npm`·`npx`가 PATH에 없고 사용할 Node.js와 Wrangler 의존성은 있는 경우 같은 작업을 직접 실행할 수 있습니다.

```powershell
node --test "*.test.mjs"
node node_modules/wrangler/bin/wrangler.js deploy --dry-run --config wrangler.market.jsonc --outdir .dry-run-market
node node_modules/wrangler/bin/wrangler.js login
node node_modules/wrangler/bin/wrangler.js deploy --config wrangler.market.jsonc
```

기존 Worker 이름과 Durable Object binding·클래스 이름을 유지합니다. 코드만 대시보드 Edit code에 붙이는 것으로 binding·저장소·예약이 구성되지는 않습니다. 설정 파일에 명시한 변수는 `keep_vars=true`라도 배포할 때 적용되므로 대시보드의 중지 설정을 덮어쓰지 않는지 확인합니다.

로컬 실행:

```powershell
npm run dev:market
```

## 운영 변수

| 설정 | 종류 | 역할 |
| --- | --- | --- |
| `NEXON_API_KEY` | Secret | 넥슨 인증, 값은 Cloudflare에서만 관리 |
| `MARKET_ADMIN_TOKEN` | Secret | 시범 수집 관리자 인증 |
| `AUCTION_ENABLED` | 텍스트 | `true`일 때 넥슨 요청 허용 |
| `UPSTREAM_REQUESTS_PER_24H` | 텍스트 | 서버 전체의 최근 24시간 호출 예산, 구성값 10000 |
| `UPSTREAM_REQUESTS_PER_SECOND` | 텍스트 | 넥슨 호출 속도, 기본·최대 5 |
| `MARKET_ENABLED` | 텍스트 | 시장 Collector 및 API 사용 |
| `MARKET_SCHEDULE_ENABLED` | 텍스트 | `true`일 때 신규 정기 수집 시작 |
| `MARKET_LIST_INTERVAL_MINUTES` | 텍스트 | 매물 시작 간격, 기본·최소 60분 |
| `MARKET_PAGES_PER_ALARM` | 텍스트 | Alarm당 최대 페이지 수, 기본·최대 40, 새 요청 시작 8초 제한 |
| `SHARED_MARKET_QUOTES_ENABLED` | 텍스트 | 구버전 품목별 조회도 공통 데이터로 응답 |
| `AUCTION_COORDINATOR` | DO binding | 공통 upstream 예산과 직렬 요청 |
| `MARKET_COLLECTOR` | DO binding | 묶음 DB·수집 체크포인트·공통 게시본 |

키를 교체할 때 앱의 키 입력이나 재배포는 필요하지 않습니다. 앱에는 공개 URL만 있습니다. 별도 서버를 만들면 Worker 이름과 앱 `data/auction-proxy.json`의 `BaseUrl`을 변경합니다.

정기 시작 비활성은 `MARKET_SCHEDULE_ENABLED=false`입니다. 이미 진행 중인 Alarm도 중지하려면 `MARKET_ENABLED=false` 또는 upstream 중지 설정을 사용합니다. 전체 시장 기능을 끄면 공개 시장 읽기도 중단되므로 이 경우 앱은 이전 로컬 데이터를 사용합니다.

## 무료 플랜과 수집 기준

거래 내역은 20분 기본 주기, 매물은 지정한 주기로 수집합니다. 거래 API의 최근 1시간 범위를 겹쳐 읽고 정확한 거래 ID를 비교합니다. 실제 매물 페이지 수·소요 시간·SQL 사용량을 확인한 뒤 매물 간격을 정하며, 자동 부하 조정은 구현하지 않습니다.

**10,000회/24시간은 서버 운영 예산이며 넥슨 키의 공식 한도가 아닙니다.** 시장 수집은 이 중 80%까지 사용합니다. 현재 20분 거래·60분 매물이면 하루 `72 × 거래 페이지 수 + 24 × 매물 페이지 수`에 재시도가 더해집니다. 예산 안에서 끝까지 완료 가능한지 검증해야 합니다.

SQLite에는 거래별 SQL 행 대신 페이지 묶음·이름 사전·정확한 ID·집계를 저장합니다. UTC 날짜별 수집·게시 내부 예산은 쓰기 60,000행·읽기 3,000,000행입니다. Cloudflare 무료 한도인 쓰기 100,000행·읽기 5,000,000행보다 여유를 두어 코디네이터·Alarm·공개 읽기·관리 작업을 고려합니다. 인덱스·삭제도 쓰기이며 예약량을 보수적으로 계산하므로 내부 예산이 먼저 소진될 수 있습니다. 다른 Worker 사용량까지 포함한 계정 전체 제한기는 아닙니다.

Worker 요청 100,000회/일, DO 요청·실행 시간·저장 용량도 별도 한도입니다. 캐시는 데이터센터마다 저장되고 영구 복제 저장소가 아닙니다. 사용자 증가로 넥슨 호출은 늘리지 않더라도 서버 다운로드 요청은 늘어납니다. [공식 무료 한도](https://developers.cloudflare.com/durable-objects/platform/pricing/), [실행·저장 제한](https://developers.cloudflare.com/durable-objects/platform/limits/), [Cache API](https://developers.cloudflare.com/workers/runtime-apis/cache/)

거래·매물 모두 완료한 수집 구간만 공개합니다. 오류·부분 수집·페이지 상한은 이전 정상 게시본을 대체하지 않습니다. 이미지가 없는 시장 품목은 대체 아이콘을 사용합니다. 수집 시작 전과 공식 API 범위를 벗어난 장애 구간을 소급 복원하지 않습니다.

## 공개 읽기와 관리자 검증

```text
GET /v1/market/manifest
GET /v1/market/snapshots/<sha256>.json.gz
GET /v1/market/status
GET /v1/market/rankings?window=24h&sort=quantity
```

manifest의 ETag가 같으면 304를 반환합니다. 버전 파일은 `application/gzip`으로 제공하며 `Content-Encoding`을 붙이지 않습니다. SHA-256은 압축 파일 자체의 해시입니다. 캐시 만료 시에도 SQLite의 불변 게시본을 읽으며 새 원본 수집을 실행하지 않습니다. 상세 필드와 로컬 캐시 규칙은 [시장 통계 문서](../../docs/MARKET_STATISTICS.md#앱-조회와-공통-배포본)에 있습니다.

```text
POST /v1/market/admin/pilot?id=<unique-operation-id>&pages=4
GET /v1/market/admin/metrics
Authorization: Bearer <MARKET_ADMIN_TOKEN>
```

관리자 Secret은 무작위 32바이트를 64자리 소문자 16진수로 만든 값입니다. 넥슨 키와 다르며 앱·Git·로그·URL에 넣지 않습니다. 시범 상한은 각 스트림 1~2,000페이지, 동일 ID 재요청은 중복 실행하지 않습니다. metrics로 완료 여부·호출에 필요한 페이지 수·청크·SQL 예산·DB 크기·게시 버전을 확인합니다. 미완료 표본을 전체 시장 통계라고 해석하지 않습니다.

## 검증·허용 품목

`npm test`는 모든 `*.test.mjs`를 실행합니다. gzip 해시·manifest 전환·이전 파일 보존·중복 제거·부분 수집·저장 실패·예산·구버전 무호출을 오프라인으로 검증합니다. 앱 검증은 저장소 루트의 `scripts/test-market-snapshot.ps1`, `scripts/test-market-ui.ps1`을 사용합니다.

교역 재료 이름을 바꾸면 저장소 루트에서 허용 목록을 함께 갱신합니다. 이 목록은 구버전 교역 API의 공개 입력 제한이며 전체 시장 수집 범위를 117종으로 제한하지 않습니다.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/update-proxy-items.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/update-proxy-items.ps1 -Check
```

Data based on NEXON Open API.
