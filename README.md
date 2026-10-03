# FindEverything

파일·폴더 이름과 날짜를 미리 색인하고, 검색할 때는 원본 디스크를 다시 탐색하지 않는 C# 엔진입니다. Windows의 대용량 디스크와 공유 폴더를 우선 대상으로 하며, WPF 앱과 서버 API에서 같은 엔진을 사용하도록 탐색·저장·검색을 분리했습니다.

현재는 공통 엔진과 검증용 CLI가 있습니다. WPF 화면, 서버 API, 예약 실행, 자동 변경 감지는 이후 단계입니다.

## 현재 기능

- 파일·폴더 이름, 경로, 파일 크기, 생성일·수정일 수집. 파일 내용은 열지 않습니다.
- 탐색할 루트 선택, 폴더 이름·이름 Regex·경로·파일 패턴 제외, JSON 설정, 링크·정션 탐색 방지.
- 스캔별 제외 경로·사유·건수와 반복 폴더 이름·관찰 빈도 보고.
- 전체 탐색과 지정한 하위 폴더만 갱신하는 탐색, 진행률과 취소.
- 비용이 큰 폴더를 명시적 규칙·이전 탐색 이력·작업 예산으로 보류하고, 필요할 때 해당 범위만 탐색.
- 호스트가 전달한 변경 폴더 목록에서 제외 범위·중복·부모에 포함되는 작업을 제거하는 갱신 계획 API.
- 한 번에 하나의 탐색, 처리 속도 제한, 폴더별 대기, 배치 저장.
- SQLite 색인에서 이름 부분일치, 파일·폴더 구분, 생성일·수정일 범위 검색과 페이지 조회.
- 탐색 실패 시 기존 항목 보존. 취소한 탐색은 검색 색인에 반영하지 않습니다.

불필요한 폴더를 탐색 전에 제외하는 것이 가장 큰 절약입니다. `cache` 폴더를 제외하면 내부로 들어가지 않습니다. 반면 `*.tmp` 파일 제외는 해당 파일의 색인 저장을 줄이지만, 그 파일이 있는 폴더 목록을 읽는 비용까지 없애지는 않습니다. 임시·숨김·시스템 항목은 자동으로 제외하지 않으며, 사용자가 규칙을 정합니다.

## 디스크 접근 원칙

엔진은 원본의 파일 생성·수정·삭제 작업을 수행하지 않습니다. 색인 DB와 작업 파일은 탐색 대상 밖에 저장해야 하며, 가능하면 색인기를 실행하는 컴퓨터의 별도 SSD를 사용하세요. 네트워크 공유 폴더에 SQLite DB를 두고 여러 클라이언트가 직접 열기보다는, 추후 API를 통해 조회하는 구성을 권합니다.

기본 제한은 관찰한 항목 기준 약 2,000개/초와 폴더 진입 전 5ms 대기입니다. HDD 처리량이나 IOPS를 측정해 제한하는 기능은 아니므로 실제 장비에서 더 낮게 조정할 수 있습니다. 읽기에도 디스크·네트워크 부하는 생기며, 하드웨어 부하나 고장 가능성이 0이라고 보장하지 않습니다.

DB 경로가 원본 루트 안에 있거나 설정 경로에 일반적인 심볼릭 링크·정션이 있으면 거부합니다. Windows 장치 네임스페이스 경로도 지원하지 않습니다. Windows의 UNC 경로와 네트워크 드라이브로 식별되는 DB 위치는 거부하며, 다른 OS의 네트워크 마운트를 모두 식별하는 기능은 없습니다. 서로 다른 드라이브 문자가 같은 물리 디스크를 가리키는지까지 자동 확인하지는 않습니다.

## 개발 환경

.NET SDK **10.0.401**을 기준으로 `net10.0`을 사용합니다. `global.json`은 같은 SDK 계열의 최신 패치를 허용합니다. 엔진과 CLI는 Windows 전용 UI에 의존하지 않아 Linux에서도 개발·검증할 수 있습니다.

이 클라우드 환경에서 설치한 SDK를 사용하려면 저장소 루트에서 다음을 실행합니다.

```bash
source scripts/dotnet-env.sh
```

이미 .NET SDK가 PATH에 있는 Windows 개발 환경에서는 위 단계가 필요 없습니다.

```bash
dotnet restore FindEverything.sln --locked-mode
dotnet build FindEverything.sln -c Release --no-restore
dotnet test FindEverything.sln -c Release --no-build
```

CI에는 Windows와 Ubuntu의 복원·빌드·테스트를 구성했습니다. 이 클라우드 작업의 실행 환경은 Linux이며, 원격 CI 결과는 아직 확인하지 않았습니다.

## CLI 예시

아래는 PowerShell에서 실행하는 예입니다. `D:\Data`가 실제 탐색 대상인지, `C:\FindEverythingIndex`가 적절한 DB 위치인지 먼저 자신의 경로로 바꾸세요. 이 예의 C:와 D:가 서로 다른 물리 디스크라는 뜻은 아닙니다.

```powershell
dotnet run --project src/FindEverything.Cli -c Release --no-build -- scan --root 'D:\Data' --database 'C:\FindEverythingIndex\data.db' --exclude-dir 'cache' --exclude-path 'D:\Data\Temp' --exclude-file '*.tmp' --max-entries-per-second 1000 --directory-delay-ms 10
```

`--exclude-dir cache`는 어느 깊이에서든 이름이 `cache`인 폴더를 제외합니다. 특정 폴더 하나만 제외하려면 `--exclude-path`를 사용합니다. 각 제외 옵션은 여러 번 지정할 수 있습니다. 경로 제외는 탐색 루트 안의 경로로 지정합니다.

검색은 이미 만들어진 DB만 조회하며, 원본 디스크를 탐색하지 않습니다.

```powershell
dotnet run --project src/FindEverything.Cli -c Release --no-build -- search --database 'C:\FindEverythingIndex\data.db' --name '보고서' --kind file --modified-from '2026-01-01T00:00:00Z' --modified-before '2027-01-01T00:00:00Z' --limit 50
```

날짜는 시간대가 명시된 ISO 형식을 사용합니다. 시작값은 포함하고 종료값은 포함하지 않습니다. 예를 들어 한국 시각의 하루를 검색하려면 시작·종료를 각각 `2026-01-01T00:00:00+09:00`, `2026-01-02T00:00:00+09:00`으로 지정합니다.

특정 하위 폴더만 다시 읽으려면 최초 루트를 유지하고 `--scope`를 지정합니다. 적용해야 할 제외 규칙도 다시 전달합니다.

```powershell
dotnet run --project src/FindEverything.Cli -c Release --no-build -- scan --root 'D:\Data' --scope 'D:\Data\Projects\Alpha' --database 'C:\FindEverythingIndex\data.db' --exclude-dir 'cache' --exclude-path 'D:\Data\Temp' --exclude-file '*.tmp'
```

규칙은 DB에 영구 저장하지 않습니다. `--config`로 같은 JSON 설정을 재사용하거나 각 옵션을 다시 전달합니다. 루트 전체에 적용하는 제외 정책을 바꿨다면 새 규칙으로 전체 루트를 갱신해야 다른 하위 폴더의 기존 색인에도 반영됩니다. 특정 폴더를 새로 제외한 경우에는 그 폴더의 부모나 전체 루트를 갱신해야 폴더 자체의 기존 검색 항목까지 정리됩니다.

### 폴더 이름 Regex와 JSON 설정

Regex는 **폴더 이름 하나**에 적용합니다. 전체 경로나 파일 이름에는 적용하지 않습니다. `full`은 이름 전체가 일치해야 하고, `partial`은 이름 중 일부만 일치해도 제외합니다. 제외된 폴더 내부는 열지 않으며, `--scope`로 그 아래를 지정하거나 `--on-demand`를 사용해도 제외 규칙은 유지됩니다.

```powershell
dotnet run --project src/FindEverything.Cli -c Release --no-build -- scan --root 'D:\Data' --database 'C:\FindEverythingIndex\data.db' --exclude-dir-regex 'cache(-\d+)?' --exclude-dir-regex-partial '임시|temp'
```

두 Regex 옵션은 각각 여러 번 지정할 수 있습니다. 기본 대소문자 처리는 기존 이름 제외와 같이 Windows에서는 무시하고 다른 OS에서는 구분합니다. JSON 규칙의 `ignoreCase`로 개별 설정할 수 있습니다.

다음 내용을 `scan-options.json`에 저장합니다. JSON에서는 Regex의 `\`를 `\\`로 이스케이프합니다.

```json
{
  "excludedDirectoryNames": ["node_modules", "obj"],
  "excludedDirectoryNameRegexes": [
    { "pattern": "cache(-\\d+)?", "matchMode": "full", "ignoreCase": true },
    { "pattern": "임시|temp", "matchMode": "partial" }
  ],
  "excludedPaths": [],
  "excludedFilePatterns": ["*.tmp"],
  "maxEntriesPerSecond": 1000,
  "directoryDelay": "00:00:00.010",
  "maxRecordedExclusions": 1000,
  "maxTrackedDirectoryNames": 4096,
  "deferral": { "directoryNames": ["Archive"] }
}
```

```powershell
dotnet run --project src/FindEverything.Cli -c Release --no-build -- scan --root 'D:\Data' --database 'C:\FindEverythingIndex\data.db' --config '.\scan-options.json' --exclude-path 'D:\Data\Private'
dotnet run --project src/FindEverything.Cli -c Release --no-build -- scan --root 'D:\Data' --scope 'D:\Data\Archive' --database 'C:\FindEverythingIndex\data.db' --config '.\scan-options.json' --on-demand
```

JSON은 `ScanOptions`의 camelCase 필드를 사용합니다. 생략한 필드는 기존 기본값을 사용하고, `matchMode` 기본값은 `full`입니다. 시간 값은 `TimeSpan` 문자열입니다. JSON의 제외·보류 상대 경로는 **설정 파일이 있는 폴더 기준**, CLI 경로는 현재 작업 폴더 기준입니다. CLI에서 추가한 목록은 JSON 목록에 합쳐지고, 지정한 수치 옵션은 해당 JSON 값에 우선합니다. 설정은 매 스캔에만 적용되며 다음 실행에 자동 상속되지 않습니다.

알 수 없는 필드, 중복 JSON 필드, 잘못된 타입·Regex·루트 밖 경로는 DB 쓰기 전에 거부합니다. Regex 매칭은 한 번당 100ms로 제한하며, 시간 초과는 탐색 실패로 처리하여 이번 임시 결과 전체를 버리고 기존 색인·보류 정보를 보존합니다.

### 스캔별 진단과 통계

각 `scan`의 JSON 응답에는 `scanId`와 `diagnostics`가 함께 반환됩니다. 진단 보고서는 DB 이력으로 저장하지 않으므로 필요하면 표준 출력을 파일로 저장합니다.

| 필드 | 의미 |
|---|---|
| `excludedPaths` | 실제 제외한 경로, 종류, 사유(`path`, `directoryName`, `directoryNameRegex`, `filePattern`), 매칭 규칙 |
| `exclusions` | 사유별 건수와 제외 폴더·파일 수. 여러 규칙과 일치해도 한 번만 집계 |
| `omittedExcludedPaths` | 상세 경로 기록 한도를 넘어 생략된 건수. 전체 제외 통계는 계속 집계 |
| `repeatedDirectoryNames` | 두 번 이상 관찰한 동일 폴더 이름, 관찰 횟수, 이름별 최대 3개 예시 경로 |
| `untrackedDirectoryNameOccurrences` | 이름 종류 한도 때문에 새 이름을 추적하지 못한 관찰 건수 |

사유 우선순위는 경로 → 정확한 폴더 이름 → Regex → 파일 패턴입니다. Regex 간에는 설정 순서를 사용합니다. 반복 이름은 OS의 경로 대소문자 규칙으로 묶고, 관찰한 제외·보류·링크 폴더도 포함합니다. 전체 루트 자체는 제외하고 실제 관찰한 하위 범위 폴더는 포함합니다. 제외된 상위 폴더 때문에 건너뛴 scope는 제외 건수 1로 기록하며, 열지 않은 폴더 이름을 관찰했다고 집계하지 않습니다.

상세 제외 경로는 기본 1,000개, 추적하는 서로 다른 폴더 이름은 기본 4,096종으로 제한합니다. `maxRecordedExclusions`(0~10,000), `maxTrackedDirectoryNames`(0~100,000)로 조정할 수 있습니다. 이름 한도에 도달해도 이미 추적 중인 이름의 횟수는 계속 정확히 증가합니다. 결과는 횟수 내림차순·이름순이며, 한도 안에 처음 관찰한 이름을 대상으로 하므로 전체 트리의 상위 빈도 순위는 아닙니다.

이 통계는 실제 읽은 범위의 관찰값입니다. 제외·보류 폴더 아래의 미탐색 항목 수나 절약한 시간을 추정하지 않습니다. 취소·부분 완료에서도 관찰한 진단은 반환되지만, 검색 색인 반영 여부는 기존 완료 상태 규칙을 따릅니다.

결과는 표준 출력에 JSON으로, 진행률·오류는 표준 오류에 출력합니다. `Ctrl+C`로 취소할 수 있습니다. 종료 코드는 성공 `0`, 잘못된 입력·실패 `1`, 부분 탐색 `2`, 보류 범위가 있는 탐색 `3`, 취소 `130`입니다. SMB 요청 등 이미 진행 중인 OS 호출은 반환될 때까지 취소가 지연될 수 있습니다.

## 느린 폴더는 보류하고 필요할 때 탐색하기

보류 기능은 기본으로 꺼져 있습니다. 영구 제외는 검색 대상에서 빼는 규칙이고, **보류는 아직 확인하지 않은 범위를 남겨 두는 규칙**입니다. 보류된 범위의 기존 검색 항목은 유지하며, 새 항목은 그 범위를 실제로 탐색하기 전까지 알 수 없습니다.

아래는 `Archive` 폴더와 지연이 큰 작업 경로를 미리 보류하고, 정상 탐색 이력으로 선별한 하위 폴더 중 10,000개 항목 이상 또는 20초 이상인 범위도 보류하는 예입니다. 이번 작업에는 5,000개 항목·10초의 예산을 함께 지정합니다.

```powershell
dotnet run --project src/FindEverything.Cli -c Release --no-build -- scan --root 'D:\Data' --database 'C:\FindEverythingIndex\data.db' --exclude-dir 'cache' --defer-dir 'Archive' --defer-path 'D:\Data\RemoteWork' --defer-over-entries 10000 --defer-over-seconds 20 --entry-budget 5000 --time-budget-seconds 10
```

첫 탐색에서 이력이 없는 폴더의 크기·소요 시간은 미리 알 수 없습니다. 항목 수를 세기 위해 별도의 전체 탐색을 먼저 하지 않습니다. 이 경우 명시적 보류 규칙이나 작업 예산이 도움이 됩니다. 이후 정상적으로 탐색한 개별 폴더·하위 구조의 이력을 DB에 저장하여 다음 작업에 활용합니다. 시간 이력에는 대기·속도 제한·DB 저장 지연도 포함되므로 디스크 자체의 속도나 다음 완료 시간을 보장하지 않습니다.

예산에 도달하면 이번 탐색 범위 전체를 보류로 남겨 미확인 부분을 보존합니다. 이때 이미 읽은 가지의 과거 항목도 보수적으로 남을 수 있습니다. 이미 진행 중인 OS·SMB 요청을 강제로 끊지는 못하므로, 10초 예산이 작업을 정확히 10초 안에 끝낸다는 뜻은 아닙니다.

보류 목록은 디스크를 탐색하지 않고 DB에서 조회합니다.

```powershell
dotnet run --project src/FindEverything.Cli -c Release --no-build -- pending --database 'C:\FindEverythingIndex\data.db' --root 'D:\Data' --limit 50 --offset 0
```

목록에서 필요한 범위를 선택해 `--scope`와 `--on-demand`로 탐색할 수 있습니다. `--on-demand`는 값 없이 지정하며 보류 규칙·작업 예산만 우회합니다. 영구 제외, 속도 제한, 링크 검사와 취소는 유지합니다. 원래의 제외 옵션 또는 같은 `--config`를 다시 전달해야 합니다.

```powershell
dotnet run --project src/FindEverything.Cli -c Release --no-build -- scan --root 'D:\Data' --scope 'D:\Data\Archive' --database 'C:\FindEverythingIndex\data.db' --on-demand --exclude-dir 'cache'
```

요청 시 탐색은 저장된 중단 위치에서 이어가는 대신 선택한 범위의 현재 상태를 처음부터 확인합니다. 보류된 부모의 작은 하위 폴더 하나만 탐색해도 부모 전체가 확인되는 것은 아닙니다. 부모의 보류를 해소하려면 그 범위 전체를 정상 탐색해야 하며, 그전에는 새로 확인한 하위 항목도 보류 범위에 속한다고 표시될 수 있습니다. 보류된 폴더가 삭제되었다면 부모나 전체 루트의 정상 탐색으로 그 상태를 정리할 수 있습니다.

검색 요청은 보류 범위 탐색을 자동 실행하지 않습니다. 검색 결과의 `hasPendingScopes`는 결과가 비어도 미확인 범위가 있음을 알리고, `coveragePending`은 반환된 기존 항목이 보류 범위와 연결됨을 표시합니다.

이 표시는 의도적으로 보류한 범위에 대한 정보입니다. 접근 오류나 연결 실패로 생긴 부분 탐색까지 모두 나타내는 것은 아니므로, 탐색 보고서의 오류와 완료 상태도 확인해야 합니다.

## 갱신의 의미와 현재 한계

탐색 중에는 기존 검색 색인을 유지하고 수집 결과를 임시 영역에 저장합니다. 완료 후 DB 트랜잭션으로 반영합니다.

| 탐색 결과 | 색인 반영 |
|---|---|
| 정상 완료 | 해당 탐색 범위의 새 결과를 반영하고, 그 범위에서 더 이상 관찰되지 않은 기존 항목 제거 |
| 보류 범위 있음 | 확인한 항목과 보류 목록 반영. 보류 범위의 기존 항목을 유지하고, 정상 확인한 나머지 범위만 정리 |
| 부분 완료 | 확인한 항목만 추가·갱신. 관찰하지 못한 기존 항목을 삭제하거나 기존 보류를 해소하지 않음 |
| 취소 | 이번 탐색 결과·보류 목록·비용 이력 미반영 |

네트워크 단절이나 권한 오류를 파일 삭제로 취급하지 않습니다. 그 대신 부분 탐색 후에는 오래된 결과가 남을 수 있으므로 오류 보고를 확인하고 재탐색해야 합니다. 정상 완료도 여러 사람이 수정 중인 파일시스템의 한 시점 스냅샷을 뜻하지는 않습니다.

이름이 Unicode 코드 포인트 기준 3글자 이상이면 FTS5 trigram 색인으로 후보를 줄이고 실제 부분일치를 확인합니다. 1~2글자 검색도 지원하지만 색인을 순회하는 대체 경로를 사용하므로 많은 파일에서 느릴 수 있습니다.

현재 변경 알림과 NTFS USN 저널은 구현하지 않았습니다. 수동 전체 탐색 또는 하위 폴더 갱신이 필요합니다. NTFS 서버의 저널을 다른 PC가 SMB 경로만으로 읽을 수는 없습니다.

엔진의 `RefreshPlanner.Plan`은 호출하는 프로그램이 이미 알고 있는 변경 **폴더 경로**를 받아 중복 작업을 합칩니다. 파일시스템에 접근하거나 변경을 자동 감지하지 않으며, 현재 CLI의 `--scope` 명령과 별도로 제공하는 API입니다.

구조·알고리즘, 제외 규칙 예시와 다음 단계는 [엔진 설계](docs/engine-design.md)에 정리했습니다.
