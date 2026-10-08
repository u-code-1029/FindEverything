# 스캔 프로필 서버

`FindEverything.Server`는 ASP.NET Core REST API입니다. 한 서버가 등록한 드라이브를 탐색하고, 여러 데스크톱은 서버의 SQLite 색인에서 검색 결과를 받습니다. 검색 요청은 원본 드라이브를 탐색하거나 스캔 작업을 자동 등록하지 않습니다.

이번 구현은 서버와 REST 계약입니다. Angular 22 이상의 사용자 검색 화면·관리자 운영 화면과 WPF 클라이언트는 포함하지 않습니다. 두 화면이 사용할 검색·프로필·예약·작업 API를 분리했습니다.

## 빌드와 시작 설정

저장소 루트에서 .NET SDK 10.0.401을 사용합니다. 클라우드에 설치한 SDK를 사용하는 Linux 셸에서는 먼저 `source scripts/dotnet-env.sh`를 실행하세요. Windows에서 SDK가 PATH에 있으면 필요 없습니다.

```sh
dotnet restore FindEverything.sln --locked-mode
dotnet build FindEverything.sln -c Release --no-restore
dotnet test FindEverything.sln -c Release --no-build --no-restore
```

다음은 PowerShell에서 서버를 시작하는 예입니다. 원본 `D:\Data`와 데이터 `C:\FindEverythingServerData`를 실제 경로로 바꾸세요. 서로 다른 드라이브 문자여도 같은 물리 디스크일 수 있습니다.

```powershell
# 토큰은 로컬 메모리에 보관하고 서버에는 SHA-256 해시만 설정합니다.
function New-ApiCredential {
    $bytes = New-Object byte[] 32
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    $rng.GetBytes($bytes)
    $rng.Dispose()
    $token = [Convert]::ToBase64String($bytes)
    $sha = [System.Security.Cryptography.SHA256]::Create()
    $hash = [BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($token))).Replace('-', '')
    $sha.Dispose()
    [pscustomobject]@{ Token = $token; Hash = $hash }
}

$adminCredential = New-ApiCredential
$readerCredential = New-ApiCredential
$env:FindEverything__DataDirectory = 'C:\FindEverythingServerData'
$env:FindEverything__Sources__0__Id = 'shared-data'
$env:FindEverything__Sources__0__RootPath = 'D:\Data'
$env:Authentication__Mode = 'ApiKey'
$env:Authentication__Users__0__Subject = 'admin'
$env:Authentication__Users__0__TokenSha256 = $adminCredential.Hash
$env:Authentication__Users__0__IsAdministrator = 'true'
$env:Authentication__Users__1__Subject = 'desktop-reader'
$env:Authentication__Users__1__TokenSha256 = $readerCredential.Hash
$env:Authentication__Users__1__IsAdministrator = 'false'

$serverProcess = Start-Process dotnet -ArgumentList @(
    'run', '--project', 'src/FindEverything.Server', '-c', 'Release',
    '--no-build', '--no-launch-profile', '--', '--urls', 'http://127.0.0.1:5080'
) -PassThru
```

인증 키를 고정된 값으로 저장소에 저장하지 마세요.

기본 설정에는 유효한 키나 등록한 소스가 없습니다. 환경변수는 프로세스 시작 시 읽습니다. 예시는 서버를 별도 프로세스로 시작하므로 현재 PowerShell 세션에서 아래 API를 호출할 수 있습니다. 서버 콘솔에서 `Ctrl+C`로 종료하면 데이터는 남습니다. 다른 클라이언트에 인증 키를 전달할 때는 신뢰할 수 있는 비밀정보 저장소를 사용하세요.

`http://127.0.0.1:5080`은 로컬 개발용입니다. 다른 데스크톱에 공개할 때는 HTTPS와 적절한 방화벽·호스트 설정을 적용하세요. 인증 없는 `/health`는 연결 확인용이며 소스 경로나 색인 내용을 반환하지 않습니다.

### Windows 통합 인증

Windows 사용자 인증을 연결할 때는 `Authentication__Mode=Negotiate`와 `Authentication__AdminIds__0=DOMAIN\admin`처럼 관리자 주체를 등록합니다. `/api/me`가 반환하는 `subject`와 같은 문자열을 프로필 `readerIds`에 넣으세요. 주체 비교는 대소문자를 구분합니다. API key 모드에서는 `Users[].isAdministrator`로 관리자 권한을 지정합니다.

Negotiate는 Windows 인증 연결을 제공하지만, 그 사용자로 원본 파일을 읽는 impersonation이나 NTFS ACL 동기화를 수행하지 않습니다. 실제 도메인·Kerberos/SPN·IIS 프록시 구성은 배포 환경에서 검증해야 합니다. 개발용 API key 예시와 Windows 통합 인증은 별도의 인증 모드입니다.

Negotiate의 `POST`·`PUT`·`PATCH`·`DELETE` 요청은 `X-FindEverything-Request: 1` 헤더가 필요합니다. 브라우저에서 다른 출처의 서버에 연결할 때는 `withCredentials: true`와 서버의 명시적인 `Cors:AllowedOrigins` 설정이 필요합니다. API key 모드에서는 Bearer 헤더를 사용합니다. 기본 CORS 허용 목록은 비어 있으며 `Cors__AllowedOrigins__0=https://search.example.com`처럼 출처만 등록합니다. 경로·와일드카드는 사용하지 않습니다. 실제 Angular 로그인·배포 연결은 프런트엔드 단계에서 검증합니다.

## REST API

API key 모드의 요청에는 `Authorization: Bearer <token>`을 전달합니다. JSON 필드와 enum은 camelCase입니다. 알 수 없는 JSON 필드, 중복 필드, 숫자 enum과 512KiB를 넘는 본문은 거부합니다. 사용자 프로필 목록·검색·출력·pending은 `limit` 1~1,000과 0 이상의 `offset`을 받습니다. 관리자 프로필·예약·작업 목록은 `limit` 1~200과 `offset` 0~1,000,000을 받습니다. API 응답은 `Cache-Control: no-store`를 사용합니다.

| 대상 | 메서드·경로 | 기능 |
|---|---|---|
| 사용자 | `GET /api/me` | 인증 주체·관리자 여부 |
| 사용자 | `GET /api/profiles` | 조회 권한이 있는 프로필 목록 |
| 사용자 | `GET /api/profiles/{id}/status` | 게시 시각·상태·항목 수·오류 수·pending 여부 |
| 사용자 | `GET`, `POST /api/profiles/{id}/search` | 프로필 색인 검색 |
| 사용자 | `GET /api/profiles/{id}/export` | 검색 페이지 JSON·CSV 출력 |
| 관리자 | `GET /api/admin/sources` | 시작 설정에 등록한 소스 목록 |
| 관리자 | `GET`, `POST /api/admin/profiles` | 프로필 목록·생성 |
| 관리자 | `GET`, `PUT`, `DELETE /api/admin/profiles/{id}` | 프로필 상세·수정·삭제 |
| 관리자 | `GET`, `POST /api/admin/schedules` | 예약 목록·생성 |
| 관리자 | `GET`, `PUT`, `DELETE /api/admin/schedules/{id}` | 예약 상세·수정·삭제 |
| 관리자 | `GET`, `POST /api/admin/jobs` | 이력 목록·스캔 등록 |
| 관리자 | `GET /api/admin/jobs/{id}` | 진행 상태·설정 스냅샷·보고서 |
| 관리자 | `POST /api/admin/jobs/{id}/cancel` | 작업 취소 요청 |
| 관리자 | `POST /api/admin/profiles/{id}/scan` | 특정 프로필 스캔 등록 |
| 관리자 | `GET /api/admin/profiles/{id}/pending` | 색인에 저장된 보류 범위 |

인증 실패는 `401`, 관리자 권한이 없는 운영 API 호출은 `403`입니다. 사용자에게 보이지 않는 프로필의 조회는 없는 프로필과 같이 `404`를 반환합니다. 잘못된 입력은 `400`, 버전·작업 충돌은 `409`, 대기열 한도는 `429`입니다. 오류는 Problem Details 응답이며 원본 경로가 포함된 내부 예외를 사용자 응답에 그대로 넣지 않습니다.

### 프로필 생성과 첫 스캔

앞에서 서버를 시작한 PowerShell 세션에서 실행합니다. `Archive`는 보류해 두고 선택한 경우 상세 탐색하는 예입니다. 보류가 필요 없다면 `deferral`을 생략하세요.

```powershell
$baseUrl = 'http://127.0.0.1:5080'
$adminHeaders = @{ Authorization = "Bearer $($adminCredential.Token)" }
$readerHeaders = @{ Authorization = "Bearer $($readerCredential.Token)" }

$profileDefinition = @{
    name = '공유 자료'
    sourceId = 'shared-data'
    relativeRoot = '.'
    enabled = $true
    readerIds = @('desktop-reader')
    options = @{
        excludedDirectoryNameRegexes = @(
            @{ pattern = 'cache|temp'; matchMode = 'full'; ignoreCase = $true },
            @{ pattern = 'backup'; matchMode = 'partial'; ignoreCase = $true }
        )
        excludedPaths = @('Private')
        excludedFilePatterns = @('*.tmp')
        maxEntriesPerSecond = 1000
        directoryDelay = '00:00:00.010'
        deferral = @{ directoryNames = @('Archive') }
    }
    output = @{
        columns = @('name', 'fullPath', 'kind', 'sizeBytes', 'modifiedUtc', 'coveragePending')
        sortBy = 'modified'
        sortDirection = 'descending'
        defaultPageSize = 100
        format = 'json'
    }
}
$profile = Invoke-RestMethod -Method Post -Uri "$baseUrl/api/admin/profiles" `
    -Headers $adminHeaders -ContentType 'application/json; charset=utf-8' `
    -Body ($profileDefinition | ConvertTo-Json -Depth 30)

$accepted = Invoke-RestMethod -Method Post -Uri "$baseUrl/api/admin/jobs" `
    -Headers $adminHeaders -ContentType 'application/json' `
    -Body (@{ profileId = $profile.id } | ConvertTo-Json)
$jobId = $accepted.job.id

Invoke-RestMethod -Uri "$baseUrl/api/admin/jobs/$jobId" -Headers $adminHeaders
```

스캔 등록은 `202 Accepted`와 `{ "job": { ... }, "coalesced": false }`를 반환합니다. 같은 진행 작업을 재사용한 경우 `coalesced`가 `true`입니다. 등록 성공이 탐색 완료를 의미하지는 않습니다. 작업 상태는 `queued`, `running`, `completed`, `partial`, `deferred`, `cancelled`, `failed`, `interrupted` 중 하나입니다. 완료 후 `report.diagnostics`에서 제외 경로·사유별 건수·반복 이름을 확인할 수 있습니다.

### 한 시간 간격 예약

```powershell
$scheduleDefinition = @{
    profileId = $profile.id
    name = '한 시간마다 갱신'
    kind = 'interval'
    enabled = $true
    anchorUtc = [DateTimeOffset]::UtcNow.AddHours(1).ToString('O')
    intervalMinutes = 60
}
$schedule = Invoke-RestMethod -Method Post -Uri "$baseUrl/api/admin/schedules" `
    -Headers $adminHeaders -ContentType 'application/json; charset=utf-8' `
    -Body ($scheduleDefinition | ConvertTo-Json)
```

매일 예약은 `kind: "daily"`, `localTime: "02:30:00"`, `timeZoneId: "Asia/Seoul"`을 사용하며 `intervalMinutes`를 생략합니다. 한 번 예약은 `kind: "once"`와 미래의 `anchorUtc`만 사용합니다. 새 예약을 등록하거나 수정하면 현재 시각 뒤의 다음 실행 시각을 계산합니다.

프로필·예약의 `PUT` 본문은 `{ "expectedRevision": 1, "definition": { ... } }` 형태이며 `definition`에는 수정할 전체 정의를 넣습니다. 삭제는 `DELETE .../{id}?expectedRevision=1`로 요청합니다. 출력 설정만 바꿀 때도 프로필 정의의 `output`을 수정합니다.

### 검색과 출력

```powershell
$searchBody = @{
    searchText = '보고서 프로젝트'
    kind = 'file'
    minSizeBytes = 1024
    sortBy = 'modified'
    sortDirection = 'descending'
    limit = 50
    offset = 0
}
$results = Invoke-RestMethod -Method Post -Uri "$baseUrl/api/profiles/$($profile.id)/search" `
    -Headers $readerHeaders -ContentType 'application/json; charset=utf-8' `
    -Body ($searchBody | ConvertTo-Json)

Invoke-RestMethod -Uri "$baseUrl/api/profiles/$($profile.id)/status" -Headers $readerHeaders
Invoke-WebRequest -Uri "$baseUrl/api/profiles/$($profile.id)/export?format=csv&limit=100" `
    -Headers $readerHeaders -OutFile '.\search-page.csv'
```

`searchText`는 이름 또는 전체 경로에 모든 단어가 포함되어야 하는 리터럴 검색입니다. `nameContains`는 이름만 부분일치합니다. 각각 최대 4,096자이며 `searchText`는 공백으로 나눈 최대 64개 단어를 받습니다. 이 외에 `maxSizeBytes`, `createdFromUtc`, `createdBeforeUtc`, `modifiedFromUtc`, `modifiedBeforeUtc`를 받을 수 있습니다. 날짜 범위는 시작 포함·종료 제외이며 시간대가 있는 ISO 8601 값을 사용하세요. `sortBy`는 `name`, `path`, `kind`, `size`, `created`, `modified`이고 `sortDirection`은 `ascending`·`descending`입니다.

검색 응답은 `profileId`, `hasIndex`, 선택한 열의 `entries`, `totalCount`, `hasMore`, `hasPendingScopes`, `limit`, `offset`을 반환합니다. 아직 게시한 색인이 없으면 `hasIndex: false`와 빈 결과를 반환합니다. 결과가 비어도 보류 범위가 있으면 `hasPendingScopes`로 알 수 있습니다. `coveragePending` 열을 선택하면 기존 결과가 보류 범위 안에 있는지도 표시합니다. 오류로 생긴 모든 미확인 범위를 뜻하지는 않으므로 상태의 오류 수와 관리자 보고서도 함께 확인해야 합니다.

예시 응답 구조입니다. 식별자·시각은 실제 응답값으로 대체합니다.

```json
{
  "profileId": "<profile-id>",
  "hasIndex": true,
  "entries": [
    {
      "name": "보고서.txt",
      "fullPath": "D:\\Data\\Projects\\보고서.txt",
      "kind": "file",
      "sizeBytes": 2048,
      "modifiedUtc": "<UTC ISO 8601>",
      "coveragePending": false
    }
  ],
  "totalCount": 1,
  "hasMore": false,
  "hasPendingScopes": false,
  "limit": 50,
  "offset": 0
}
```

여러 페이지를 요청하는 사이 새 스캔이 게시되면 결과의 순서·개수가 바뀔 수 있습니다. 여러 요청을 같은 색인 버전에 고정하는 세션·커서 기능은 현재 없습니다. 대량 결과를 수집하는 클라이언트는 상태의 마지막 게시 시각을 확인하고 갱신 중에는 다시 조회할 수 있도록 설계하세요.

`export`도 검색과 같은 조건을 받습니다. 위 CSV 예시는 조건 없이 첫 페이지를 저장하므로, 같은 검색을 출력하려면 GET 쿼리 문자열에 같은 조건을 전달하세요. `format`을 생략하면 프로필 출력 형식을 사용합니다. CSV의 전체 개수·다음 페이지·pending·색인 유무는 `X-Total-Count`, `X-Has-More`, `X-Has-Pending-Scopes`, `X-Has-Index` 응답 헤더에 있습니다. 한 요청은 최대 1,000개입니다. CSV는 스프레드시트 수식으로 해석될 수 있는 문자열 앞에 `'`를 추가하므로 JSON의 원래 문자열과 달라질 수 있습니다.

### 보류 범위 탐색과 취소

```powershell
Invoke-RestMethod -Uri "$baseUrl/api/admin/profiles/$($profile.id)/pending?limit=100" -Headers $adminHeaders

$onDemand = Invoke-RestMethod -Method Post -Uri "$baseUrl/api/admin/profiles/$($profile.id)/scan" `
    -Headers $adminHeaders -ContentType 'application/json' `
    -Body (@{ relativeScope = 'Archive'; onDemand = $true } | ConvertTo-Json)

Invoke-RestMethod -Method Post -Uri "$baseUrl/api/admin/jobs/$($onDemand.job.id)/cancel" -Headers $adminHeaders
```

`relativeScope`는 프로필 루트 기준 상대 경로입니다. pending 응답의 절대 경로를 그대로 요청 값으로 전달하지 마세요. 사용자 검색 API는 스캔을 시작하지 않으며 on-demand도 관리자 작업입니다.

## 저장 구조와 실행 범위

- **소스**는 서버 시작 설정에 등록한 `id`와 `rootPath`입니다. API 사용자가 임의의 절대 경로를 탐색하도록 허용하지 않습니다.
- **프로필**은 소스, 소스 안의 상대 루트, 엔진의 제외·보류·속도 제한, 출력 정의, 조회 가능한 사용자 ID를 저장합니다.
- **작업**은 등록 시점의 프로필과 경로를 고정합니다. 이후 프로필을 편집해도 이미 등록한 작업의 설정은 바뀌지 않습니다.
- **예약**은 프로필 전체 루트의 작업을 등록합니다. 지정 시각 한 번, 일정 간격, 시간대별 매일 실행을 지원합니다.
- **출력 정의**는 검색 결과 열, 기본 정렬, 기본 페이지 크기와 JSON·CSV 형식을 정합니다. 자동 파일 생성이나 전체 결과 일괄 내보내기는 수행하지 않습니다.

서버 데이터 디렉터리에는 프로필·예약·작업·스캔 보고서를 저장하는 `server.db`와 프로필별 `indexes/<profile-id>.db`가 있습니다. 원본 드라이브 밖의 로컬 디스크에 두세요. 가능하면 원본 HDD와 별개의 SSD를 사용합니다. Windows에서 UNC·네트워크 드라이브의 데이터 디렉터리는 거부합니다. 다른 OS의 모든 네트워크 마운트를 판별하거나 실제 물리 디스크가 다른지 확인하는 기능은 없습니다.

한 데이터 디렉터리를 사용하는 서버는 하나만 실행할 수 있습니다. 또한 서버 전체에서 스캔 작업을 한 번에 하나씩 수행하여 여러 프로필의 작업이 원본 디스크를 동시에 읽는 일을 줄입니다. 기본 대기열 한도는 100개입니다. 같은 프로필 버전·범위·on-demand 설정의 대기 또는 실행 작업을 다시 요청하면 기존 작업을 반환합니다. 다른 범위의 작업을 자동으로 합치지는 않습니다.

이 조정은 이 서버의 작업에만 적용됩니다. 다른 PC의 CLI나 별도 서버가 같은 공유 폴더를 읽는 것은 막지 못합니다. 같은 위치를 서로 다른 소스로 등록했는지도 자동 판별하지 않으므로, 소스 ID를 관리자가 통일해야 합니다.

## Windows 공유 드라이브

`W:` 같은 연결 드라이브 문자는 로그인 사용자와 세션에 따라 달라집니다. Windows 서비스·IIS 계정에는 같은 연결이 없을 수 있습니다. HDD가 붙은 Windows 장비에서 서버를 실행한다면 실제 로컬 경로를 등록하는 구성을 우선 권합니다. 다른 장비에서 실행할 경우 서버 계정이 읽을 수 있는 `\\server\share\...` 경로를 사용하세요.

API 인증 사용자와 실제 파일을 읽는 서버 OS 계정은 별개입니다. 서버 OS 계정은 등록한 원본을 읽을 권한이 있어야 하고, 데이터 디렉터리에 쓰기 권한이 있어야 합니다. 엔진은 원본 파일을 생성·수정·삭제하지 않습니다. 읽기 자체의 디스크·네트워크 부하는 남으며, 진행 중인 동기 OS·SMB 호출은 취소 요청으로 즉시 중단되지 않을 수 있습니다.

## 접근 범위

관리자는 소스 설정을 조회하고 프로필·출력·예약·작업을 관리할 수 있습니다. 일반 사용자는 자신의 ID가 `readerIds`에 포함된 프로필만 목록 조회·검색·상태 조회·출력할 수 있습니다. 빈 `readerIds`는 공개 프로필을 뜻하지 않습니다. 모든 비관리자에게 비공개입니다.

프로필의 `enabled: false`는 스캔을 제어하며 기존 색인의 조회 권한은 제거하지 않습니다. 조회를 차단하려면 `readerIds`에서 해당 사용자를 제거하세요. 탐색 제외 규칙과 출력 열도 사용자별 파일 접근 권한을 대신하지 않습니다.

접근 범위가 다른 폴더는 별도 프로필로 나누고 `relativeRoot`와 `readerIds`를 설정하세요. 프로필 안에서는 승인된 사용자가 전체 검색 색인을 볼 수 있습니다. 서버는 Windows NTFS·SMB ACL을 사용자별로 자동 해석하거나 항목마다 권한을 검사하지 않습니다. 실제 파일 권한과 일치하는 범위를 관리자가 설정해야 합니다. 작업 보고서의 제외 경로·반복 이름·오류·pending 목록은 관리자 API에서만 제공합니다.

인증 키와 프로필 접근 목록은 요청마다 확인합니다. `readerIds`에서 사용자를 제거하면 이후 요청의 접근이 차단됩니다. 이미 클라이언트에 전달한 데이터는 회수할 수 없습니다. 인증 키 변경은 현재 구현의 시작 설정에 적용하고 서버를 재시작합니다.

## 프로필 설정의 의미

`relativeRoot`는 등록한 소스 기준 상대 경로입니다. `options.excludedPaths`와 `options.deferral.paths`는 프로필 루트 기준 상대 경로입니다. `../`로 루트를 벗어나는 경로, 절대 경로와 장치 경로는 거부합니다. 프로필 생성·편집·작업 등록에서는 문자열 기준 경로 검사를 사용하며 원본에 접근하지 않습니다. 링크·정션 여부는 실제 작업 실행 직전과 엔진 탐색 시 검사하고, 허용하지 않는 경로면 작업이 실패합니다. 원본이 오프라인이어도 기존 색인 조회와 프로필 권한 수정이 가능합니다. CLI JSON 설정 파일 기준 경로와 혼동하지 마세요.

`options`는 엔진의 `ScanOptions`입니다. 폴더 이름 Regex의 `full`·`partial`, 이름·경로·파일 패턴 제외, 처리율·폴더 대기, 보류 조건·작업 예산을 그대로 적용합니다. `onDemand: true`는 보류 규칙과 작업 예산만 우회하며 영구 제외·처리율·링크 검사·취소는 유지합니다.

`output.columns`는 `name`, `fullPath`, `parentPath`, `kind`, `sizeBytes`, `createdUtc`, `modifiedUtc`, `coveragePending` 중 중복 없이 1~8개를 지정합니다. JSON 항목과 CSV 열은 이 정의를 사용하며, 기본 페이지 크기는 1~1,000입니다. 검색 API는 항상 JSON을 반환하고 `output.format`은 export의 기본 형식으로 사용합니다.

프로필마다 별도의 색인 DB를 사용합니다. 겹치는 프로필은 서로 다른 접근 범위나 제외 설정을 유지할 수 있지만, 같은 원본 범위를 반복해서 읽고 중복 저장할 수 있습니다. 사용자마다 프로필을 만들기보다 접근 범위와 스캔 정책이 같은 사용자를 하나의 프로필에 묶으세요.

프로필 수정과 예약 수정은 `expectedRevision`을 요구합니다. 조회한 버전을 전달하고, 충돌하면 최신 설정을 조회하여 다시 수정하세요. 검색에 영향을 주는 프로필 설정을 바꾸면 새 설정에 따른 전체 스캔이 필요합니다. 편집만으로 기존 검색 항목이 자동으로 재분류되지는 않습니다.

생성한 프로필의 `sourceId`와 `relativeRoot`는 변경할 수 없습니다. 다른 범위를 색인하려면 새 프로필을 만드세요. 프로필을 삭제하려면 해당 프로필의 대기·실행 작업을 먼저 취소하고 종료해야 합니다. 삭제하면 연결된 예약도 삭제하지만 과거 작업 이력과 색인 파일은 관리자 보관용으로 남습니다. 이력과 고아 색인의 자동 보관 기간·정리 기능은 현재 없습니다.

## 갱신과 운영

약 한 시간 갱신이 필요하다면 60분 간격 예약을 출발점으로 사용할 수 있습니다. 예약 시각은 작업 등록 시각이며 완료 시각을 보장하지 않습니다. 앞선 작업, 느린 SMB 응답, 예산·pending·접근 오류 때문에 확인 시점은 더 오래될 수 있습니다. 상태 응답의 마지막 게시 시각·상태·오류·pending과 작업 이력을 함께 확인하세요. 전체 스캔 완료도 동시 수정 중인 파일시스템의 한 시점 스냅샷을 뜻하지 않습니다.

예약에서 지난 실행 시각을 모두 따라잡으려고 반복 스캔하지 않습니다. 서버가 중단되었거나 작업이 길어진 경우 밀린 실행은 한 번으로 모으고 다음 미래 시각으로 진행합니다. 예약 자체가 저장되어도 활성화된 프로필과 예약이 없으면 서버 시작 시 원본을 자동으로 스캔하지 않습니다.

예약의 `anchorUtc`는 오프셋이 `Z` 또는 `+00:00`인 UTC 시각으로 지정합니다. `once`는 그 시각에 한 번 등록하고 다음 시각이 없어집니다. `interval`은 그 시각을 기준으로 `intervalMinutes` 간격을 유지하며 밀린 횟수는 건너뜁니다. `daily`는 `localTime`과 IANA `timeZoneId`를 사용하고 `anchorUtc` 이전에는 실행하지 않습니다. 서머타임 전환으로 현지 시각이 존재하지 않는 날은 건너뛰며, 같은 시각이 두 번 존재하는 날은 이른 UTC 시각에 한 번 실행합니다.

비활성화된 예약은 처리하지 않습니다. 프로필이 비활성화된 동안에는 해당 예약의 지난 시각을 남겨 두고, 다시 활성화하면 한 번 등록한 뒤 다음 시각으로 진행합니다. 대기열이 가득 차면 예약 시각을 소비하지 않고 다음 확인 때 다시 시도합니다.

프로필을 비활성화하면 새 작업을 받지 않고 아직 시작하지 않은 작업도 시작 전에 취소합니다. 이미 실행 중인 작업은 별도의 취소 API로 중단을 요청하세요. 예약을 비활성화하거나 삭제해도 이전에 등록한 작업은 자동 취소하지 않습니다.

서버 재시작 후 대기 중인 작업은 계속 처리합니다. 이전 프로세스에서 실행 중이던 작업은 `interrupted`로 남기고 자동 재실행하지 않습니다. 종료 시 실행 작업을 취소하고 기존 색인을 보존합니다. HTTP 요청이나 클라이언트 연결이 끝나도 이미 등록한 작업은 취소하지 않습니다. 대기 작업 취소는 바로 반영하지만 실행 작업은 `cancellationRequested` 상태를 거쳐 엔진이 실제로 종료될 때 완료됩니다.

검색 중 스캔이 수행되어도 기존 색인을 조회할 수 있습니다. 정상 완료·부분 완료·보류의 반영 규칙은 [엔진 설명](../README.md#갱신의-의미와-현재-한계)과 같습니다. 취소 작업은 이번 임시 결과를 게시하지 않습니다. 오류가 난 미확인 항목을 삭제로 처리하지 않습니다. 작업 이력에는 적용한 설정 스냅샷과 제외·반복 이름 통계를 포함한 보고서가 함께 남습니다.

엔진의 진단 기록 한도 외에 서버 이력 저장에도 한도를 적용합니다. 오류 1,000개, pending 상세 1,024개, 제외 경로 1,000개, 반복 이름 1,000개를 상한으로 시작하고 보고서가 너무 크면 상세 목록을 더 줄입니다. `reportTruncated: true`이면 저장된 상세가 생략되었음을 뜻합니다. 집계 건수와 진행 통계는 유지하고 생략한 제외 경로는 `omittedExcludedPaths`에 포함합니다. 현재 보류 범위는 보고서의 축약된 목록 대신 별도의 pending API에서 조회하세요. 통계를 보완하려고 원본을 추가 탐색하지 않습니다.

스캔 속도 비교에서는 같은 범위·처리율·예산·오류 조건인지 확인해야 합니다. 제외된 폴더의 내부 항목 수나 실제 절약한 디스크 I/O는 알 수 없습니다. 반복 이름 통계는 후보를 찾는 관측값이며, 해당 폴더가 불필요하다는 판정은 아닙니다.

현재는 자동 변경 감지·USN 저널·여러 서버의 분산 작업 조정이 없습니다. 사용자별 Windows ACL 연동, 실제 Windows 서비스·IIS·HTTPS 배포, 실물 HDD·SMB 부하 및 동시 수정 환경은 별도 검증이 필요합니다. 서버에는 Angular 화면이 포함되지 않으며, 브라우저 앱의 로그인과 인증 키 보관 방식은 프런트엔드 단계에서 정해야 합니다.

운영 데이터를 백업할 때는 서버가 완전히 종료된 후 데이터 디렉터리 전체를 함께 복사하세요. 실행 중인 SQLite DB 파일 하나만 복사하는 방식은 WAL의 내용을 놓칠 수 있습니다. 백업에는 검색 경로와 관리자 보고서가 포함되므로 서버 데이터와 같은 접근 제한을 적용해야 합니다.

## 검증 범위

Linux 환경에서 잠금파일 복원과 Release 빌드를 완료했으며 경고·오류는 0개입니다. 테스트는 엔진 143개와 서버 57개, 총 200개 통과했고 Windows 전용 2개는 건너뛰었습니다. 서버와 서버 테스트의 NuGet 취약성 검사에서는 사용한 소스 기준 알려진 취약 패키지가 없었습니다.

이 결과는 실제 Windows 통합 인증·NTFS ACL·SMB·HDD의 운영 검증을 대체하지 않습니다. 실물 공유 드라이브의 부하·지연·권한·네트워크 단절과 Windows 서비스·IIS 배포는 별도로 확인해야 합니다.
