# 시장 통계와 공통 시세 데이터

시장 통계는 공식 경매장의 판매 완료 내역과 현재 매물을 서버에서 수집하고, 모든 앱이 같은 집계본을 내려받는 기능입니다. 앱의 검색·정렬·교역 비용 계산마다 넥슨 API를 호출하지 않습니다. 이미지와 제작법 DB 없이 동작하며 새 시장 품목은 대체 아이콘으로 표시합니다.

이 문서는 개발 브랜치의 구현과 운영 설정을 설명합니다. 정기 수집이나 구버전 시세 경로의 전환 여부는 실제 배포 변수로 결정됩니다. 문서에 기능이 있다는 사실만으로 운영 활성화나 새로운 GitHub Release가 이루어진 것은 아닙니다. 배포 및 시범 실행 결과는 해당 [배포 기록](releases/)에서 확인합니다.

## 데이터 흐름

```text
20분 Cron → MarketCollector의 Alarm 작업 큐
                       ↓ 서버가 페이지별 요청
                AuctionCoordinator
                공통 호출 예산·초당 제한
                       ↓
             NEXON auction/history·list
                       ↓
          SQLite 청크 저장·정확한 거래 ID 중복 제거
                       ↓ 수집 완료 후
          24시간·7일 통계 + 교역 시세 사전 계산
                       ↓
            버전 manifest + 불변 gzip 파일
                       ↓
         Cloudflare 지역 캐시 → 모든 앱이 공유
                       ↓
          앱 로컬 저장·검색·정렬·교역 계산
```

별도 D1·Queues 서비스 대신 SQLite Durable Object에 저장소와 Alarm 작업 큐를 함께 둡니다. 캐시가 없어지더라도 SQLite의 게시본을 읽으며, 사용자 조회를 이유로 넥슨 원본을 다시 수집하지 않습니다.

| 파일 | 역할 |
| --- | --- |
| `market-core.mjs` | 공식 응답 검증, 기본 주기, 조회 조건 |
| `market-schema.mjs`, `market-chunks.mjs` | SQLite 구조, 이름 사전과 묶음 인코딩 |
| `market-store.mjs` | 거래 ID 중복 제거, 페이지 저장, 완료본 집계, SQL 예산 |
| `market-snapshot.mjs` | gzip·SHA-256·불변 버전 저장·원자적 manifest 전환 |
| `market-worker.mjs` | Cron·Alarm·공개 읽기 API·관리자 시범 실행 |
| `wrangler.market.jsonc` | 운영 Worker의 시장 binding·예약·변수 배포 |
| `src/MarketSnapshot.cs` | 앱 공통 다운로드·검증·로컬 캐시 |
| `src/MarketUi.cs` | 시장 통계 화면과 로컬 검색·정렬 |
| `src/AuctionCore.cs` | 공통 시세로 교역 재료의 가격 반영 |

서버 파일은 `server/cloudflare/`에 있습니다.

## 앱 조회와 공통 배포본

교역의 **전체 시세 갱신**과 **구매품목만 갱신**은 서버가 미리 만든 공통 버전을 확인합니다. 새 버전이면 gzip 파일 하나를 받아 해당 버튼의 대상 품목에 가격을 반영합니다. 대상이 117종이어도 품목마다 넥슨을 호출하지 않습니다.

시장 통계는 창을 열거나 **통계 갱신**을 눌렀을 때 버전을 확인합니다. 이후 검색어·정렬·기간·페이지 변경은 내려받은 데이터에서 처리합니다. 메인 앱 시작이나 체크리스트 변경이 정기 수집을 시작하지 않습니다.

1. `GET /v1/market/manifest`에 이전 버전의 `If-None-Match`를 보냅니다.
2. `304`이거나 같은 버전이면 파일을 다시 받지 않습니다.
3. 새 버전일 때만 `/v1/market/snapshots/<sha256>.json.gz`를 내려받습니다.
4. 압축 크기·해제 크기·SHA-256·데이터 형식을 검증하고 manifest와 본문을 함께 로컬에 저장합니다. 다운로드 실패나 잘못된 파일은 기존 정상 데이터를 덮어쓰지 않습니다.

파일은 `Content-Type: application/gzip`이며 `Content-Encoding`을 붙이지 않습니다. 앱이 압축 파일 자체의 해시를 검사한 뒤 한 번만 해제합니다. 서로 다른 서버의 로컬 캐시는 구분되며 앱 폴더의 `data/market-snapshots/`에 저장합니다. 교역 가격 캐시는 기존 `data/auction-cache.json`을 사용합니다.

이는 **버전이 바뀐 경우에만 완성된 파일을 내려받는 방식**입니다. 이전 파일에 패치를 적용하는 바이트 단위 차분 전송은 구현하지 않습니다. 같은 앱 안에서 동시에 요청한 다운로드는 공유합니다.

manifest의 지역 캐시는 15초, 버전 파일은 24시간입니다. 파일의 생성 시각과 거래·매물 수집 시각은 다운로드 시각으로 바꾸지 않습니다. 통계의 24시간·7일 범위도 게시본의 생성 시각을 기준으로 합니다.

## 수집·정합성 규칙

- 거래 내역은 20분 간격으로 시작하도록 구성합니다. 공식 API의 최근 1시간 범위가 겹치므로 정확한 `auction_buy_id`로 중복을 제거합니다. 같은 품목·가격·수량이어도 거래 ID가 다르면 별도 거래입니다.
- 매물의 기본 시작 간격은 60분입니다. `MARKET_LIST_INTERVAL_MINUTES`로 60분 이상을 지정할 수 있습니다. 실제 전체 조회 페이지 수와 예산을 측정한 뒤 운영 간격을 정합니다. 부하에 따른 자동 간격 조정이 구현된 것은 아닙니다.
- 이름·카테고리 필터 없이 공식 API를 조회하고 `next_cursor=null`까지 이어갑니다. 페이지당 최대 500건이며 Alarm 한 번에 거래·매물을 번갈아 최대 40페이지 처리합니다. 실행 시작 8초가 지나면 새 요청을 시작하지 않고 다음 Alarm으로 이어갑니다. 이미 진행 중인 요청은 12초 제한 안에서 저장을 마칩니다.
- 페이지 청크와 다음 커서는 하나의 SQLite 트랜잭션에 저장합니다. 재시작·Alarm 재전달·같은 페이지 재처리로 중복 합산하지 않습니다.
- **거래와 매물 모두 끝까지 완료한 수집 구간만 게시·집계합니다.** 부분 수집·실패·시범 페이지 상한은 기존 완료본을 대체하지 않습니다. 실패한 거래 구간의 ID는 나중의 정상 재수집을 중복으로 제거하지 않습니다.
- 24시간·7일 통계와 교역 시세를 사전 계산하고 gzip 청크를 저장한 뒤, 같은 트랜잭션 안에서 현재 버전 포인터를 변경합니다. 읽는 앱에는 이전 정상 버전 또는 다음 정상 버전이 제공됩니다.
- 이전 버전 파일은 생성 후 최소 24시간 동안 보관합니다. 만료된 파일은 `410`, 아직 게시본이 없으면 `503`입니다. 파일 다운로드 실패 시 앱은 기존 검증본을 유지합니다.
- 거래 수집 15분, 매물 수집 50분 또는 10,000페이지 초과는 미완료입니다. 반복 커서·잘못된 응답도 완료로 처리하지 않습니다. 일시 오류는 제한적으로 재시도하고 인증·예산 오류는 해당 실행을 종료합니다.
- 전체 매물은 페이지를 읽는 동안 변동할 수 있어 특정 한 순간의 무누락 현황을 보장하지 않습니다. `coverage=completed_scans_only`는 페이지 순회를 완료한 수집본이라는 뜻입니다.

거래 기록은 최근 8일을 보관하여 24시간·7일 통계를 만듭니다. 오래된 청크·실패 구간·이전 매물·커서를 제한된 묶음으로 정리합니다. 수집 시작 전의 기록은 소급하지 않으며, 공식 조회 범위를 벗어난 장애 구간도 복원할 수 없습니다.

## 무료 플랜의 예산

넥슨 호출은 `global-v1` AuctionCoordinator에서 직렬화하며 최근 24시간 예산과 초당 속도를 모든 서버 수집·기존 직접 프록시 조회가 공유합니다. 실패한 요청도 예약한 호출량에 포함합니다.

**`UPSTREAM_REQUESTS_PER_24H=10000`은 이 서버의 운영 예산이며 넥슨 발급 키의 한도가 아닙니다.** 구현상 최대 10,000회/24시간·5회/초이며, 백그라운드는 그중 80%까지만 사용합니다. 공통 시세 전환 이후에도 이 보호 한도를 자동으로 늘리지 않습니다. 같은 키를 외부에서 사용하는 호출은 이 서버가 집계하지 못합니다.

거래 20분·매물 60분이면 재시도를 제외한 하루 호출량은 다음과 같습니다.

```text
72 × 거래 전체 조회 페이지 수 + 24 × 매물 전체 조회 페이지 수
```

이 합계가 백그라운드 예산 8,000회보다 작아야 해당 주기를 지속할 수 있습니다. SQL 예산·수집 소요 시간도 별도로 만족해야 합니다. 모자랄 때는 거래 이력의 1시간 조회 범위를 고려하면서 매물 간격을 늘리거나 수집 범위를 다시 결정합니다. 한도에서 중단된 것을 전체 수집 완료라고 표시하지 않습니다.

Cloudflare 무료 SQLite Durable Objects는 하루 **쓰기 100,000행·읽기 5,000,000행**을 제공합니다. 개별 거래를 SQL 한 행씩 저장하는 대신 최대 500개 페이지를 이름 사전·거래 ID·집계 청크로 묶습니다. 수집 청크는 900,000바이트 이하, 게시용 gzip 청크는 256KiB 단위로 나누어 SQLite 행 크기 제한을 피합니다.

수집·게시 작업에는 UTC 날짜별 **쓰기 60,000행·읽기 3,000,000행의 내부 예산**을 둡니다. 작업 전 예약하고 실제 SQL 커서의 `rowsRead/rowsWritten`을 반영합니다. 네이티브 계측이 확인된 정상 완료 작업은 실제 사용량에 쓰기 4회·읽기 2회의 여유분을 더해 정산합니다. 중간에 종료되었거나 정확한 계측이 없는 작업은 예약량을 전부 유지합니다. 코디네이터의 예산 저장, Alarm, 공개 읽기, 관리·정리 등에 사용할 여유를 남기는 구조입니다. 계정 전체 사용량을 실시간 측정하거나 다른 Worker까지 통제하는 제한기는 아닙니다.

인덱스 갱신·삭제·KV 저장·Alarm 설정도 사용량에 영향을 줍니다. 무료 플랜의 Worker 요청 100,000회/일, DO 요청·실행 시간·저장 용량 제한도 별도입니다. 공통 수집으로 사용자 수에 따른 **넥슨 호출 증가**를 줄이지만 Cloudflare의 다운로드 요청까지 무료 무제한이 되는 것은 아닙니다.

Cache API는 데이터센터별 캐시입니다. 자동으로 모든 지역에 복제되는 영구 DB가 아니며 캐시 적중에도 Worker가 실행됩니다. 캐시 유무와 관계없이 정상 게시본은 SQLite에서 다시 제공할 수 있어야 합니다. 운영 전에 페이지 수·SQL 사용량·DB 크기·gzip 크기·수집 시간을 확인합니다.

## 통계의 의미

| 항목 | 의미 |
| --- | --- |
| 판매 수량 | 중복 제거한 거래의 수량 합계 |
| 거래 건수 | 서로 다른 거래 ID 수 |
| 거래 금액 | 개당 가격 × 수량의 합계 |
| 평균 거래 단가 | 수량 가중 평균, 중앙값 아님 |
| 매물 수량·건수 | 최근 완료한 매물 수집본의 수량 합계·행 수 |
| 가격 비교 | 지정한 재료·소모품 분류 중 옵션 없는 기록만 이름 기준 비교 |
| 이미지 | `image_url=null`, 대체 아이콘 표시 |

이름이 같은 장비도 옵션에 따라 가치가 다르므로 단가 비교를 숨길 수 있습니다. 교역 시세는 기존 의미와 같이 해당 이름의 최저 매물 단가를 사용합니다. 총수량 전체를 그 최저 단가에 살 수 있다는 뜻은 아닙니다. 합계가 안전한 정수 범위를 벗어나면 알 수 없는 값으로 처리합니다. 완료한 매물본이 없으면 공급량은 미확인이고, 정상 완료본에 품목이 없으면 매물 0입니다.

## 공개 API와 설정

```text
GET /v1/market/manifest
GET /v1/market/snapshots/<sha256>.json.gz
GET /v1/market/status
GET /v1/market/rankings?window=24h&sort=quantity&limit=50&offset=0
GET /v1/auction/list?item_name=거미줄
GET /auction?item_name=거미줄
```

`rankings`는 도구·호환용 서버 조회입니다. 저장된 게시본에서 필터링하며 수집 SQL 집계를 매번 실행하지 않습니다. 새 앱은 게시 파일을 내려받아 로컬에서 검색합니다. `window`는 `24h/7d`, `sort`는 `quantity/trades/gold/supply`, 검색은 `q`, 분류는 `category`이며 최대 100개·offset 10,000입니다.

| 변수 | 동작 |
| --- | --- |
| `MARKET_ENABLED=true` | 시장 API와 Collector 사용 허용 |
| `MARKET_SCHEDULE_ENABLED=true` | Cron에서 신규 정기 수집 시작 |
| `MARKET_LIST_INTERVAL_MINUTES` | 매물 시작 간격, 기본 60분·최소 60분 |
| `MARKET_PAGES_PER_ALARM` | 실행 한 번의 최대 페이지 수, 기본·최대 40, 새 요청 시작은 8초까지 |
| `SHARED_MARKET_QUOTES_ENABLED=true` | 구버전 `/auction`·`/v1/auction/list`도 공통 게시본만 읽음 |
| `AUCTION_ENABLED=true` | AuctionCoordinator의 넥슨 요청 허용 |
| `NEXON_API_KEY` | Cloudflare Secret, 넥슨 인증 |
| `MARKET_ADMIN_TOKEN` | Cloudflare Secret, 관리자 시범 실행 인증 |

공통 교역 모드에서는 허용 품목 117종 및 구버전 이름 별칭을 유지하고, 데이터가 없어도 사용자 요청을 넥슨 직접 조회로 대체하지 않습니다. `SHARED_MARKET_QUOTES_ENABLED`가 꺼져 있으면 구버전 직접 프록시 경로가 남으므로 배포 시 전환 상태를 명시적으로 확인합니다.

정기 시작만 멈추려면 `MARKET_SCHEDULE_ENABLED=false`로 설정합니다. 이미 실행 중인 Alarm까지 넥슨 요청을 중지하려면 `MARKET_ENABLED=false` 또는 upstream 중지 설정을 사용합니다. 기능을 모두 끄면 서버 읽기도 차단될 수 있으므로 앱의 로컬 검증본과 이전 시세가 복구 수단입니다.

## 배포·시범 검증

시장 기능을 적용한 Worker는 **`wrangler.market.jsonc`**로 배포합니다. 이전 교역 전용 설정은 제거했고 `npm run deploy`와 Windows 배포 도우미도 같은 설정을 사용합니다. 파일에 지정한 변수는 `keep_vars`가 있어도 배포 시 적용됩니다.

Node.js 24 권장. `server/cloudflare` 폴더에서 실행합니다.

```powershell
npm install
npm test
npm run check:market
npx wrangler login
npx wrangler deploy --config wrangler.market.jsonc
```

처음 적용할 때는 정기 시작을 비활성화하고, 기존 Worker 이름·Durable Object 클래스·binding·Secret 연결을 확인합니다. 코드 하나만 Edit code에 붙이는 방식으로 구성하지 않습니다. 넥슨 키는 서버 Secret에만 두며 앱·Git·ZIP에 넣지 않습니다.

관리자에게만 다음 시범 경로를 허용합니다.

```text
POST /v1/market/admin/pilot?id=<unique-operation-id>&pages=4
GET /v1/market/admin/metrics
Authorization: Bearer <MARKET_ADMIN_TOKEN>
```

관리자 Secret은 무작위 32바이트의 64자리 소문자 16진수입니다. 넥슨 키와 별개이며 URL·로그·앱에 넣지 않습니다. 시범 `pages`는 스트림별 1~2,000페이지이고 같은 `id` 재요청은 새로운 실행을 만들지 않습니다. 상한에 닿으면 `limited/MARKET_PILOT_LIMIT`으로 끝나며 그 구간은 게시하지 않습니다.

metrics에서 DB 크기·완료 거래 수·청크 수·SQL 예산·게시 버전·실패 상태를 확인합니다. 실제 완주 페이지 수와 자원 소비가 정기 예산 안에 들어가는지 확인한 뒤 주기와 공통 교역 전환 변수를 정합니다. 새 앱 릴리스 게시 여부는 Worker 배포와 별도입니다.

## 로컬 검증

저장소 루트에서 실행합니다.

```powershell
node --test "server/cloudflare/*.test.mjs"
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/test.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/test-market-snapshot.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/test-market-ui.ps1
```

서버는 실제 SQLite와 모의 넥슨 응답으로 중복 제거·부분 수집·재시작·저장 실패 롤백·게시 원자성·gzip 해시·조건부 요청·구버전 무호출을 검증합니다. 앱 검증은 격리 캐시와 로컬 서버로 다운로드·무변경 재사용·취소·손상 파일·이전 데이터 보존을 확인합니다. 검증에 실제 넥슨 호출이나 개인 키가 필요하지 않습니다.

## 출처와 권리

- [NEXON 마비노기 공식 명세](https://openapi.nexon.com/static/api/mabinogi/36_ko_script20250410023004.yaml): 경매장 필드, 페이지 처리, 거래 조회 범위.
- [Cloudflare 예약 실행](https://developers.cloudflare.com/workers/configuration/cron-triggers/), [Durable Object Alarm](https://developers.cloudflare.com/durable-objects/api/alarms/): 주기 실행과 재전달.
- [Durable Objects 무료 한도](https://developers.cloudflare.com/durable-objects/platform/pricing/), [저장·실행 제한](https://developers.cloudflare.com/durable-objects/platform/limits/): SQL·요청·저장 용량.
- [Cache API](https://developers.cloudflare.com/workers/runtime-apis/cache/): 지역별 캐시와 조건부 요청.

게임 정보의 권리는 해당 권리자에게 있습니다. 시장 통계는 공식 API 응답으로 구성하며 라바뉴·위키의 이미지나 제작법을 추가 수집하지 않습니다.
