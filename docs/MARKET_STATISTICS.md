# 시장 통계 · 개발 단계

시장 통계는 공식 경매장 API의 판매 완료 내역과 현재 매물을 수집해 품목별 판매량·거래 건수·거래 금액·매물 수량을 제공합니다. 이미지와 제작법 DB 없이 동작하며, 이미지 필드는 `null`로 유지합니다. 수집 작업과 공개 조회를 분리하므로 앱의 통계 갱신은 넥슨 API를 호출하지 않습니다.

현재 단계는 **운영 Worker 반영 및 제한 시범 수집 검증 단계**입니다. 정기 수집은 `MARKET_SCHEDULE_ENABLED=false`로 유지합니다. 베타 Release를 교체하지 않으며 `feature/market-statistics`에서 개발합니다. 일별 추이 그래프, 관심 품목, 제작 수익 계산은 후속 범위입니다.

## 구성

```text
Cron (20분) → MarketCollector의 영속 작업 큐 / Alarm
                         ↓ 페이지별 조회
                 AuctionCoordinator
                 기존 교역 조회와 동일한 예산·속도 제한
                         ↓
                 NEXON list / history
                         ↓
               MarketCollector SQLite DB
                         ↓
           조회 결과 캐시 → /v1/market/* → 앱
```

초기 구성에서는 별도 Queues·D1 서비스 대신 **SQLite Durable Object 하나**에 DB와 Alarm 작업 큐를 함께 둡니다. Worker의 지역별 메모리를 DB로 쓰지 않습니다. DB 규모·조회 부하를 실제로 측정한 뒤 시간별 집계 테이블 또는 별도 저장소로 확장할 수 있습니다.

| 파일 | 역할 |
| --- | --- |
| `server/cloudflare/market-core.mjs` | 공식 응답 검증, 조회 조건, 주기 |
| `server/cloudflare/market-schema.mjs` | SQLite 테이블·색인 |
| `server/cloudflare/market-store.mjs` | 거래 중복 제거, 페이지 원자 저장, 통계 SQL |
| `server/cloudflare/market-worker.mjs` | 예약 작업·Alarm·공개 읽기 API |
| `server/cloudflare/wrangler.market.jsonc` | 시장 기능용 binding·예약 설정, 정기 수집 비활성 |
| `src/MarketUi.cs` | 개발용 시장 통계 창, 조회·검색·정렬·페이지 이동 |

## 수집 규칙

- `history`는 20분마다 시작합니다. 공식 API는 최근 1시간 내역만 제공하므로 겹쳐서 수집하고 `auction_buy_id` 기본키로 중복을 제거합니다. 가격·수량·이름이 같아도 거래 ID가 다르면 각각 집계합니다.
- `list`는 1시간마다 시작합니다. 이름·카테고리 필터 없이 공식 API의 목록을 조회하고 `next_cursor`가 `null`이 될 때까지 이어갑니다. 페이지별 최대 500건입니다. 전체 범위 응답과 소요 호출량은 운영 시범 수집에서 확인해야 합니다.
- Alarm 한 번에 각 스트림 한 페이지를 처리합니다. 받은 페이지의 데이터와 다음 커서를 같은 SQLite 트랜잭션에 저장합니다. 작업이 재실행되어도 중복 합산하지 않습니다.
- 매물은 완료한 수집본만 공개합니다. 중간 실패·반복 커서·시간 제한으로 끝난 작업은 이전 성공본을 대체하지 않습니다. 판매 내역은 정상 저장한 페이지까지 계속 누적하며, 완료 상태와 수집 오류를 별도로 표시합니다.
- 전체 매물 조회에는 시간이 걸리므로 한 시점의 원자적인 시장 스냅샷을 보장하지 않습니다. 수집 중 매물 변동에 따른 차이가 있을 수 있습니다. `listing_count`는 수집 결과의 매물 행 수입니다.
- 거래 수집 15분, 매물 수집 50분 또는 10,000페이지를 넘으면 해당 작업을 미완료로 기록합니다. 잘못된 응답은 저장하지 않습니다. 일시 오류는 최대 3회 재시도하고, 인증·예산 오류는 해당 작업을 종료합니다.
- 예약 및 Alarm은 `MARKET_ENABLED=false`에서 넥슨을 호출하지 않습니다. 예약 시작에는 별도로 `MARKET_SCHEDULE_ENABLED=true`가 필요합니다. 일반 공개 조회에는 수집 시작·재시도·원본 프록시 경로가 없습니다. 별도 관리자 Secret으로 인증한 시범 수집 경로만 제한된 실행을 시작할 수 있습니다.

## 호출 예산과 보관

시장 수집도 기존 `global-v1` AuctionCoordinator를 거칩니다. 실패 요청을 포함해 최근 24시간 예산과 초당 제한을 공유합니다. 백그라운드 수집은 공용 예산의 80%에 도달하면 멈춰 나머지를 교역 조회에 남깁니다.

설정의 **10,000회/24시간은 앱 서버의 운영 예산이며 넥슨 발급 키 한도가 아닙니다.** 현재 구현의 상한은 10,000회/일·5회/초입니다. 이 예산으로 시장 전체를 매번 끝까지 수집할 수 있다고 보장하지 않습니다. 한도를 먼저 크게 올리기보다 시범 수집의 페이지 수·시간·저장량을 확인해야 합니다. 같은 키를 사용하는 외부 서비스의 호출량은 이 서버가 알 수 없습니다.

거래 원장은 최근 8일을 보관하여 24시간·7일 통계를 계산합니다. 만료 기록과 이전 매물은 작업 후 최대 2,000행씩 정리합니다. 실패한 작업도 유지·정리 대상입니다. 원장 삭제 후에도 통계가 영구 보존되는 구조는 아닙니다. 초기에는 실측을 위한 구조이며, 대량 수집 장기 운영 전에 저장소 크기와 정리 처리량, 조회 CPU 시간을 측정해야 합니다. SQLite 저장 한도·과금은 사용 중인 Cloudflare 플랜을 기준으로 확인합니다.

첫 데이터 수집 이전의 24시간·7일 기록을 소급 채우지 않습니다. 장애가 1시간을 넘어가면 놓친 거래를 공식 API로 복원할 수 없습니다. `coverage=observed_api_records`는 관측 데이터라는 의미이며 무누락 보장이 아닙니다.

## 통계 의미

- 판매 수량: 거래 기록의 `item_count` 합계.
- 거래 건수: 중복 제거한 거래 ID 수. 판매 수량과 다릅니다.
- 거래 금액: 개당 가격 × 수량의 합계. 안전하게 표현할 수 없는 정수 범위는 `null`로 반환합니다.
- 평균 거래 단가: 수량 가중 평균. 중앙값이 아닙니다.
- 매물 수량: 최근 완료한 매물 수집본의 수량 합계. 완료본이 없으면 `null`, 완료본에 해당 품목이 없으면 `0`입니다.
- 가격 비교: 지정된 재료·소모품 분류이며 옵션이 없는 기록만 이름 기준으로 비교합니다. 옵션 품목이나 장비가 섞이면 평균·최저 단가를 숨깁니다. 거래량·금액은 이름·분류 기준 합계입니다.
- 이미지: `image_url=null`; 출처와 제공 방법이 정해진 뒤 연결합니다.

## 공개 조회

모두 GET이며 SQL 조건은 매개변수로 바인딩합니다. 조회는 수집·재시도·넥슨 호출을 유발하지 않습니다.

```text
GET /v1/market/status
GET /v1/market/rankings?window=24h&sort=quantity&limit=50&offset=0
GET /v1/market/rankings?window=7d&sort=gold&q=거미줄
```

`window`: `24h`, `7d`. `sort`: `quantity`, `trades`, `gold`, `supply`. `category`는 정확한 분류 이름, `q`는 품목 이름의 부분 검색입니다. 최대 100행, offset 최대 10,000입니다. 결과에는 마지막 완료·최근 실행 상태·수집 시작 시각·오래된 데이터 여부가 포함됩니다. 통계 캐시는 최대 32개 조건·30초이며 새 페이지가 저장되면 비웁니다.

## Cloudflare 적용 순서

1. 이 개발 브랜치의 `server/cloudflare`에서 의존성을 설치하고 검증합니다. 기존 Worker 편집기에 파일 하나를 복사하는 방식은 사용하지 않습니다.

   ```powershell
   npm ci
   npm test
   npm run check:market
   ```

2. `wrangler.market.jsonc`의 Worker 이름과 기존 `AuctionCoordinator` 설정이 운영 서버와 일치하는지 확인합니다. `MARKET_ENABLED=false`, `MARKET_SCHEDULE_ENABLED=false`를 유지한 상태로 아래 명령을 실행하면 새 SQLite binding과 예약 설정을 함께 배포합니다. 기존 `NEXON_API_KEY` Secret을 사용하며 새 키를 코드나 앱에 넣지 않습니다.

   ```powershell
   npx wrangler deploy --config wrangler.market.jsonc
   ```

3. 정기 수집을 시작할 때 `wrangler.market.jsonc`의 `MARKET_ENABLED`와 `MARKET_SCHEDULE_ENABLED`를 모두 `true`로 바꾸고 같은 명령으로 배포합니다. 이후 예약 시점부터 서버가 수집하므로 PC를 켜둘 필요가 없습니다. 이 단계부터 Cloudflare 자원과 공용 넥슨 호출 예산을 사용합니다.
4. `/v1/market/status`에서 완료 여부·페이지 수·실패 코드를 확인합니다. 인증·한도 오류나 시간 초과가 발생하면 전체 수집이 된 것으로 판단하지 않습니다. `MARKET_ENABLED=false`로 되돌려 재배포하면 신규 수집을 중지합니다.
5. 한 번의 수집량과 비용·저장량을 확인하고, 예산·저장 방식·수집 범위를 확정한 뒤 다음 앱 릴리스에 포함합니다.

시장 기능을 적용한 이후에는 **`wrangler.market.jsonc`를 배포 설정으로 일관되게 사용**합니다. 기본 `wrangler.jsonc`는 기존 교역 서버 구성으로 남아 있으며 시장 예약/binding이 없습니다. 두 파일을 번갈아 배포하지 마세요.

## 로컬 검증

Node.js 24 권장. 시장 저장소 테스트는 Node의 SQLite 모듈을 사용합니다.

```powershell
node --test server/cloudflare/worker.test.mjs server/cloudflare/market.test.mjs
powershell -File scripts/test.ps1
powershell -File scripts/test-market-ui.ps1
```

UI 검증은 빌드의 공개 주소를 일시적으로 로컬 모의 서버로 바꾸고 종료 후 복원합니다. 격리된 진행 상태를 사용하며, 테스트 데이터가 포함된 밝은/어두운 화면은 `artifacts/market-ui`에 저장합니다. 테스트 도구·개인 상태·수집 DB는 배포 ZIP에 포함하지 않습니다.

## 출처

- [NEXON 마비노기 경매장 공식 명세](https://openapi.nexon.com/static/api/mabinogi/36_ko_script20250410023004.yaml): 필드, 페이지 처리, 최근 1시간 거래 내역, 평균 데이터 지연.
- [Cloudflare Cron Triggers](https://developers.cloudflare.com/workers/configuration/cron-triggers/), [Durable Objects Alarms](https://developers.cloudflare.com/durable-objects/api/alarms/): 예약 실행, 영속 작업 재시도.

게임 정보의 권리는 해당 권리자에게 있습니다. 이 기능은 공식 API 응답을 사용하며 라바뉴·위키의 이미지나 제작법을 추가 수집하지 않습니다.

## 제한 시범 수집 관리

`MARKET_ENABLED=true`, `MARKET_SCHEDULE_ENABLED=false`이면 통계를 읽을 수 있지만 Cron은 새 작업을 시작하지 않습니다. 관리자만 아래 경로로 한 번의 시범 실행을 시작할 수 있습니다. 인증되지 않은 요청은 401로 거부하며 넥슨을 호출하지 않습니다.

```text
POST /v1/market/admin/pilot?id=<unique-operation-id>&pages=4
GET /v1/market/admin/metrics
Authorization: Bearer <MARKET_ADMIN_TOKEN>
```

`MARKET_ADMIN_TOKEN`은 무작위 32바이트를 64자리 소문자 16진수로 만든 별도 Cloudflare Secret입니다. 넥슨 API 키와 다르며 앱·Git·URL에 넣지 않습니다. 이 PC에서는 Git에서 제외된 배포에 사용한 작업 폴더의 `server/cloudflare/.wrangler/market-admin-token`에 보관합니다. 이 파일은 Git 프로젝트로 복사되지 않습니다. 값은 화면이나 로그로 출력하지 않습니다.

`pages`는 스트림별 1~100페이지로 제한합니다. 같은 `id` 재요청은 기존 실행만 확인하며 새 수집을 만들지 않습니다. 완료 전 페이지 상한에 닿으면 `MARKET_PILOT_LIMIT`으로 종료합니다. 부분 매물을 전체 현황으로 공개하지 않으며, 정상 저장한 거래 내역만 관측 통계에 남습니다. 시범 모드에도 기존 API 예산·오류 재시도 제한이 적용됩니다.

관리자 metrics는 SQLite 파일 크기와 보관 거래 수, 매물 집계 행 수, 최근 실행 상태를 반환합니다. 일반 사용자의 조회 경로에는 원본 거래나 인증 값을 노출하지 않습니다.

## 무료 플랜 운영 상태

2026-09-13 현재 운영은 `MARKET_ENABLED=true`, `MARKET_SCHEDULE_ENABLED=false`입니다. 관리자 시범 수집과 통계 읽기는 가능하지만 정기 전체 수집은 실행하지 않습니다. 무료 플랜임이 확인되어 새 결제나 요금제 변경 없이 이 상태를 유지합니다.

Cloudflare SQLite Durable Objects의 무료 일일 쓰기 한도는 100,000행입니다. 테이블 외에 색인 갱신·삭제 등도 쓰기량에 영향을 주므로 넥슨 API 요청 횟수만으로 운영 가능 여부를 판단할 수 없습니다. 다음 단계는 전체 거래를 개별 SQL 행으로 계속 저장하는 방식을 대신할 묶음 저장·집계 구조를 검증하는 것입니다. 집계만 저장하더라도 최근 거래 ID의 중복 제거와 지연 도착 처리를 유지해야 합니다.

- [Cloudflare 공식 Durable Objects 가격·무료 한도](https://developers.cloudflare.com/durable-objects/platform/pricing/)
- [실제 배포·시범 실행 기록](releases/market-pilot-2026-09-13.md)
