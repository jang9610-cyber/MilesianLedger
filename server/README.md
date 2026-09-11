# 밀레시안 장부 · Node.js 프록시 참고 구현

**앱 배포본의 시세 서버는 [Cloudflare Worker](cloudflare/README.md)입니다.** 이 문서는 자체 호스팅용 Node.js 참고 구현을 설명합니다. `server/src/`를 실행해도 현재 Cloudflare Worker를 배포하거나 변경하지 않습니다.

앱은 공개 프록시 주소로 품목 이름과 페이지 커서만 보냅니다. Node 참고 서버를 별도로 운영하면 서버 환경 변수의 NEXON API 키로 공식 경매장 API를 조회하고, 필요한 시세 필드만 앱에 돌려줍니다. 키를 앱·배포 ZIP·저장소에 넣지 않습니다.

이 참고 구현은 Node.js 기본 모듈만 사용하며 `npm install`은 필요하지 않습니다. Node.js 24 LTS를 권장합니다. 아래 파일 저장소·프로세스 잠금·IP별 요청 제한 설명은 Node 구현에 해당하며 Cloudflare Worker에는 적용되지 않습니다.

## 요청 계약

```text
GET /v1/auction/list?item_name=가는%20실뭉치&cursor=
GET /health
```

- `item_name`: [item-names.json](item-names.json)에 있는 정확한 이름. NPC 전용 구매 재료는 제외합니다.
- `cursor`: 첫 페이지에서는 생략하거나 빈 문자열. 다음 페이지에서는 반환된 커서를 그대로 URL 인코딩합니다. 최대 2,048자이며 제어 문자와 공백은 허용하지 않습니다.
- 다른 경로·추가 매개변수·중복 매개변수·GET 이외 요청·본문이 있는 요청은 거절합니다.
- 외부 URL을 받지 않습니다. 공식 호출 대상은 코드에 고정된 `https://open.api.nexon.com/mabinogi/v1/auction/list`뿐이며 리디렉션을 따라가지 않습니다.

성공 응답 예시:

```json
{
  "auction_item": [
    { "item_name": "가는 실뭉치", "auction_price_per_unit": 1200, "item_count": 30 }
  ],
  "next_cursor": null,
  "fetched_at": "2026-09-11T00:00:00.000Z"
}
```

`fetched_at`은 이 서버가 공식 응답을 받은 UTC 시각입니다. 캐시를 사용하면 최초 조회 시각을 유지합니다. 공식 경매장의 내부 데이터 갱신 시각을 뜻하지는 않습니다. `/health`는 연결 확인용 `{"status":"ok"}`만 반환하고, API 호출이나 키·예산 상태 확인을 하지 않습니다.

실패는 항상 `{"error":{"code":"고정 코드","message":"정제된 안내"}}`입니다. 공식 오류 본문·예외 내용·헤더를 그대로 전달하거나 로그로 남기지 않습니다.

| HTTP | code | 의미 |
| --- | --- | --- |
| 400 | `PROXY_INVALID_REQUEST`, `PROXY_ITEM_NOT_ALLOWED` | 요청 형식 또는 품목 이름 오류 |
| 404 / 405 | `PROXY_NOT_FOUND`, `PROXY_METHOD_NOT_ALLOWED` | 지원하지 않는 경로 또는 메서드 |
| 429 | `PROXY_RATE_LIMIT` | IP 또는 서버 전체의 짧은 시간 요청 제한 |
| 429 | `PROXY_QUOTA_EXCEEDED` | 서버 전체의 24시간 호출 예산 소진 |
| 429 | `PROXY_UPSTREAM_LIMIT` | 공식 서비스의 요청 제한 응답 |
| 503 | `PROXY_NOT_CONFIGURED`, `PROXY_UPSTREAM_AUTH` | 서버 설정 또는 공식 API 인증 확인 필요 |
| 503 | `PROXY_BUSY`, `PROXY_QUOTA_UNAVAILABLE` | 처리량 제한 또는 예산 저장소 오류 |
| 502 / 504 | `PROXY_UPSTREAM_ERROR`, `PROXY_INVALID_RESPONSE`, `PROXY_TIMEOUT` | 연결·응답·시간 초과 오류 |

429에는 `Retry-After` 초를 함께 보냅니다. 서버는 재시도나 주기적 갱신을 실행하지 않습니다. 사용자가 앱의 갱신 버튼을 다시 눌러야 새 요청이 시작됩니다.

## 로컬 확인

저장소 루트에서 실행합니다.

```powershell
node --test server/test/*.test.mjs
Copy-Item server/.env.example server/.env
node --env-file=server/.env server/src/server.mjs
```

`.env`의 키가 비어 있으면 조회는 `503 PROXY_NOT_CONFIGURED`로 종료되고 공식 API를 호출하지 않습니다. `/health`는 열립니다. 기본 바인딩은 로컬 `127.0.0.1:8787`입니다. 환경 변수를 호스팅 서비스에서 주입하면 `node server/src/server.mjs`로 실행합니다. 테스트는 가짜 응답과 임시 예산 파일을 사용하며 실제 API와 키를 사용하지 않습니다.

## 서버 설정

| 환경 변수 | 기본값 | 역할 |
| --- | --- | --- |
| `NEXON_API_KEY` | 빈 값 | 호스팅 서비스의 비밀 값 저장소에만 등록할 키 |
| `HOST` / `PORT` | `127.0.0.1` / `8787` | HTTP 수신 주소. 컨테이너에서는 필요에 따라 `0.0.0.0` 사용 |
| `UPSTREAM_REQUESTS_PER_24H` | `500` | 모든 이용자가 공유하는 최근 24시간 공식 호출 예산 |
| `UPSTREAM_REQUESTS_PER_SECOND` | `5` | 서버 전체의 초당 공식 호출 제한 |
| `CLIENT_REQUESTS_PER_MINUTE` | `600` | 연결 IP별 분당 요청 제한. 캐시 요청·잘못된 요청도 포함 |
| `CACHE_TTL_SECONDS` | `60` | 같은 품목·커서의 성공 응답을 재사용하는 시간. `0`은 캐시 끄기 |
| `RUNTIME_DIRECTORY` | `server/.runtime` | 예산 파일을 보존할 경로. 절대 경로 또는 `server/` 기준 상대 경로 |

**기본 500회는 이 프로젝트가 정한 보수적인 서버 예산입니다. 넥슨이 발급한 키의 실제 한도라는 뜻이 아닙니다.** 앱 한 번의 갱신에서 허용하는 500회와도 별개입니다. 운영 키의 공식 할당량과 이용자 수를 확인한 다음 서버 값을 조정해야 합니다. 같은 키를 다른 서비스에서 사용하면 그 호출량은 이 서버가 알 수 없습니다.

정상 응답은 최대 60초간 공유하고 동시에 들어온 같은 요청은 한 번으로 합칩니다. 캐시 적중은 공식 호출 예산을 쓰지 않습니다. 캐시는 기본 최대 256개·8 MiB이며, 공식 요청 최대 동시 8개, 응답 본문 최대 1 MiB, 요청 시간 최대 12초로 제한합니다. 성공하지 못한 공식 호출도 예산에서 차감합니다.

## Node 참고 서버를 별도로 배포할 때

1. 지속적인 저장 공간을 제공하는 Node 서버 한 인스턴스와 HTTPS 도메인을 준비합니다. 프록시 프로세스 앞의 역프록시에서 TLS를 처리하고 외부에는 HTTPS 주소만 공개합니다.
2. 호스팅 서비스의 비밀 값 저장소에 `NEXON_API_KEY`를 등록합니다. `.env`를 공개 저장소나 앱 ZIP에 포함하지 않습니다.
3. `.runtime`을 지속 볼륨에 연결하고, 실제 공식 할당량에 맞게 서버 예산을 설정합니다.
4. HTTPS 앞단에서 요청 속도·동시 연결·본문 크기를 제한합니다. 이 서버는 `X-Forwarded-For` 등 클라이언트가 위조할 수 있는 헤더를 신뢰하지 않고 연결 IP만 사용합니다. 역프록시 뒤에서는 모든 요청이 같은 IP로 보일 수 있으므로 실제 이용자 IP별 제한은 앞단에서 설정해야 합니다.
5. 앱에 공개 HTTPS 기본 URL을 설정합니다. 앱에는 API 키나 공용 비밀 토큰을 추가하지 않습니다. 하위 경로로 서비스할 경우 역프록시가 그 접두사를 제거하여 이 서버의 `/v1/auction/list`로 연결해야 합니다.

프록시 URL은 공개 정보입니다. URL을 안다고 공식 API 키를 얻을 수는 없지만, 누구나 공개 엔드포인트에 요청해 공용 할당량을 소모할 수 있습니다. 현재는 품목 허용 목록·속도 제한·공유 예산으로 범위를 제한합니다. 공개 배포 규모가 커지면 서비스 앞단의 봇 차단이나 이용자별 인증·할당량을 추가하는 것이 필요합니다. 앱에 동일한 비밀 토큰을 내장하는 방식은 토큰을 추출할 수 있으므로 인증 대책으로 삼지 않습니다.

예산 파일은 호출 **직전** 기록하고, 기록 실패 시 공식 API를 호출하지 않습니다. 재시작해도 최근 24시간 예약 내역을 이어 씁니다. `.lock`으로 같은 파일을 쓰는 중복 프로세스를 거절합니다. 비정상 종료로 잠금이 남으면 실제 서버 프로세스가 모두 종료되었는지 확인한 후 잠금 파일만 정리해야 하며, 예산 JSON은 지우지 않습니다. 저장 디렉터리를 삭제하거나 임시 디스크로 운영하면 이 보장은 유지되지 않습니다.

이 Node 참고 구현은 **단일 서버 인스턴스용**입니다. 서버리스·다중 인스턴스·여러 리전에 배포할 때에는 파일 저장소를 그대로 사용하지 말고, 외부 저장소의 원자적 예산 예약과 분산 속도 제한·캐시로 바꿔야 합니다. 현재 운영 중인 Cloudflare 구현은 공통 Durable Object에 예산을 보관합니다. Node 구현을 다른 환경에 배포할 때는 해당 환경에 맞는 저장소를 별도로 구성하세요.

## 공식 문서

- [NEXON Open API 요청 인증](https://openapi.nexon.com/guide/request-api/)
- [마비노기 경매장 목록 API](https://openapi.nexon.com/game/mabinogi/?id=33)
- [Node.js 기본 fetch](https://nodejs.org/api/globals.html#fetch)
- [Node.js HTTP 서버](https://nodejs.org/api/http.html)
- [Node.js 환경 파일 실행 옵션](https://nodejs.org/api/cli.html#--env-filefile)
