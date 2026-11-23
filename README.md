
# Parallel Search System

Parallel Search System — курсова робота з паралельних обчислень: вебзастосунок для паралельної індексації та пошуку в англомовних текстових документах. Система будує власний потокобезпечний інвертований індекс, виконує keyword-пошук і може розширювати запит через локальну модель Ollama.

## Технології

- C# і .NET 10;
- ASP.NET Core Minimal API та WebSocket;
- власні `InvertedIndex` і `CustomThreadPool` на основі масивів, зв’язаних вузлів, `Thread` і `Monitor`;
- HTML, CSS і JavaScript без сторонніх frontend-фреймворків;
- консольний клієнт C#;
- Ollama з моделлю `qwen2.5:0.5b`;
- QualC Gutenberg English Corpus із Hugging Face — 10048 книг;
- Docker Compose, xUnit і Python 3.

## Архітектура

| Компонент | Призначення |
|---|---|
| `ParallelSearch.Core` | Власний інвертований індекс, пул потоків і паралельна побудова спільного індексу. |
| `ParallelSearch.Api` | REST API, WebSocket-пошук, стан поточного індексу та інтеграція з Ollama. |
| `ParallelSearch.Cli` | Команди `health`, `stats`, `index` і `search`, які звертаються до REST API. |
| Web UI | Статична сторінка з побудовою індексу, статистикою, WebSocket-пошуком, етапами LLM і пагінацією результатів. |
| `ollama` | Окремий сервіс генерації додаткових пошукових термінів. |
| `ollama-model-loader` | Одноразово завантажує `qwen2.5:0.5b` у постійний Docker volume. |
| `corpus-prepare` | Потоково завантажує Parquet-файли датасету, створює окремий UTF-8 `.txt` для кожної книги та повторно використовує готові файли. |

Підготовлений корпус зберігається у volume `corpus-data`, модель — у `ollama-data`. API монтує корпус лише для читання. Новий індекс і його snippets публікуються як один snapshot тільки після успішної побудови.

## Запуск через Docker Compose

Потрібні Docker Desktop і достатньо вільного місця для датасету, розпакованих текстів, моделі та Docker volumes.

```powershell
docker compose up
```

Під час першого запуску Compose:

1. завантажує та перетворює реальний Gutenberg-корпус;
2. запускає Ollama;
3. завантажує модель `qwen2.5:0.5b`;
4. після успішної підготовки залежностей запускає API.

Повторні запуски використовують дані з volumes і не повинні без потреби перезаписувати завершені книги або повторно завантажувати модель. Після змін у Dockerfile можна виконати `docker compose up --build`.

> `docker compose up` запускає сервіси, але не будує інвертований індекс. Побудову потрібно окремо запустити через Web UI, REST API або CLI.

Вебінтерфейс доступний за адресою <http://localhost:8080/>. Перевірка API та Ollama:

```powershell
Invoke-RestMethod -Uri http://localhost:8080/api/health
```

Очікувана відповідь API має `status = "ok"`; поле `ollama` показує реальну доступність Ollama.

## Підготовка та обмеження корпусу

Compose підтримує дві незалежні змінні:

- `CORPUS_MAX_DOCUMENTS` — обмежує кількість справжніх книг, які готує `corpus-prepare`. Наприклад, значення `10` призначене для швидкої перевірки завантаження. Такий запуск не позначає корпус як повний.
- `CORPUS_MAX_INDEX_DOCUMENTS` — обмежує кількість уже підготовлених `.txt`, які API бере для індексації. Вибір файлів має стабільний порядок. Якщо змінну не задано, індексуються всі документи.

Приклад безпечного запуску з десятьма підготовленими книгами та індексацією не більше десяти документів:

```powershell
$env:CORPUS_MAX_DOCUMENTS = "10"
$env:CORPUS_MAX_INDEX_DOCUMENTS = "10"
docker compose up
```

Щоб продовжити підготовку до повного корпусу та індексувати всі книги, приберіть обмеження і запустіть Compose з тим самим volume:

```powershell
Remove-Item Env:CORPUS_MAX_DOCUMENTS -ErrorAction SilentlyContinue
Remove-Item Env:CORPUS_MAX_INDEX_DOCUMENTS -ErrorAction SilentlyContinue
docker compose up
```

Змінні також можна записати у локальний `.env`, який не додається до Git. `CORPUS_MAX_DOCUMENTS` не слід використовувати як заміну `CORPUS_MAX_INDEX_DOCUMENTS`: перша змінює підготовку даних, друга — тільки розмір індексу.

## Побудова індексу

У Web UI задайте додатну кількість потоків і натисніть **«Побудувати»**. Сторінка покаже кількість документів, час і фактично використану кількість потоків.

Той самий запит через PowerShell:

```powershell
$body = @{ threads = 4 } | ConvertTo-Json
Invoke-RestMethod -Method Post `
  -Uri http://localhost:8080/api/index `
  -ContentType "application/json" `
  -Body $body
```

Під час повної індексації контролюйте доступну RAM. Для пробного запуску встановіть `CORPUS_MAX_INDEX_DOCUMENTS`, наприклад у `100`, перед `docker compose up`.

## CLI

CLI запускається локально через .NET SDK і стандартно звертається до `http://localhost:8080`:

```powershell
dotnet run --project .\src\ParallelSearch.Cli -- health
dotnet run --project .\src\ParallelSearch.Cli -- stats
dotnet run --project .\src\ParallelSearch.Cli -- index --threads 4
dotnet run --project .\src\ParallelSearch.Cli -- search parallel computing
```

Іншу адресу API можна передати перед командою або задати змінною `PARALLEL_SEARCH_API_URL`:

```powershell
dotnet run --project .\src\ParallelSearch.Cli -- --api-url http://localhost:8080 search history of science
```

CLI використовує лише REST API та не дублює індексацію або пошукові алгоритми.

## API

Усі REST-відповіді мають формат JSON.

| Метод і шлях | Запит | Успішна відповідь |
|---|---|---|
| `GET /api/health` | — | `status`, `ollama` |
| `POST /api/index` | `{"threads":4}` | `status`, `documents_indexed`, `time_ms` |
| `POST /api/search` | `{"query":"parallel computing","mode":"keyword"}` | `results` із `doc_id`, `score`, `snippet`; `time_ms` |
| `GET /api/stats` | — | `documents`, `index_build_time_ms`, `threads_used` |

REST-пошук виконує keyword-пошук без звернення до Ollama. Багатослівний запит використовує OR; результати сортуються за score, потім за `doc_id`.

WebSocket `ws://localhost:8080/ws/search` приймає одне повідомлення:

```json
{"query":"parallel computing"}
```

Сервер послідовно надсилає етапи `query_expansion`, `keyword_search` і `done`. Якщо Ollama недоступна, `query_expansion` містить помилку та порожній список додаткових термінів, після чого пошук продовжується з початковим запитом.

## Тести

Для локальних .NET-перевірок потрібен .NET 10 SDK:

```powershell
dotnet restore .\ParallelSearch.sln
dotnet build .\ParallelSearch.sln --no-restore
dotnet test .\ParallelSearch.sln --no-build
```

Ізольовані тести підготовки корпусу не завантажують реальний датасет:

```powershell
python -m unittest discover -s .\tools\corpus -p "test_*.py"
```

## Benchmark

Benchmark не має сторонніх Python-залежностей і вимірює побудову індексу для 1, 2, 4 і 8 потоків, а потім REST keyword-пошук для 1, 2, 4 і 8 одночасних клієнтів:

```powershell
python .\tools\benchmark.py `
  --api-url http://localhost:8080 `
  --requests 100 `
  --output .\benchmark-results.csv
```

Скрипт записує тільки фактичні вимірювання у CSV. Він сам чотири рази викликає `POST /api/index`, тому перед запуском слід встановити безпечне значення `CORPUS_MAX_INDEX_DOCUMENTS` і переконатися, що API працює. Пошукова частина використовує REST API та останній успішно побудований реальний індекс, тому час Ollama не входить у throughput.

Додаткові параметри можна переглянути командою:

```powershell
python .\tools\benchmark.py --help
```

## Зупинка

Зупинити сервіси зі збереженням корпусу та моделі:

```powershell
docker compose down
```

Команда нижче також видаляє named volumes:

```powershell
docker compose down -v
```

Використовуйте `down -v` обережно: буде видалено підготовлений Gutenberg-корпус і модель Ollama, тому під час наступного запуску їх доведеться завантажувати знову.
