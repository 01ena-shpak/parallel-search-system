
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

Кожна книга зберігається окремим UTF-8 файлом `<Gutenberg ID>.txt`, наприклад `1342.txt`. Усередині API-контейнера каталог доступний лише для читання за шляхом `/data/corpus/documents`; стабільним `doc_id` у пошуковій відповіді є відносний шлях до `.txt` від цього каталогу.

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

CLI використовує лише REST API, а команда `search` виконує keyword-пошук без Ollama. CLI не дублює індексацію або пошукові алгоритми. Розширення запиту через Ollama доступне у Web UI, який використовує WebSocket.

## API

REST API працює з JSON. Поля, позначені як обов’язкові, мають бути присутні у запиті; назви полів відповідають фактичній серіалізації сервера.

### `GET /api/health`

Параметрів немає. Недоступність, неуспішна HTTP-відповідь або тайм-аут Ollama не спричиняють помилку API.

| HTTP | Поле відповіді | Тип | Обов’язкове | Значення |
|---|---|---|---|---|
| `200 OK` | `status` | `string` | так | Завжди `"ok"`. |
| `200 OK` | `ollama` | `boolean` | так | `true`, якщо health-перевірка Ollama успішна; інакше `false`. |

```json
{"status":"ok","ollama":true}
```

### `POST /api/index`

| Напрям | Поле | Тип | Обов’язкове | Умова або значення |
|---|---|---|---|---|
| Запит | `threads` | `integer` | так | Додатне число робочих потоків. |
| Відповідь `200` | `status` | `string` | так | `"ok"`. |
| Відповідь `200` | `documents_indexed` | `integer` | так | Кількість успішно проіндексованих документів. |
| Відповідь `200` | `time_ms` | `number` | так | Час побудови індексу в мілісекундах. |

```json
{"threads":4}
```

```json
{"status":"ok","documents_indexed":10048,"time_ms":92731.42}
```

| HTTP-код | Коли повертається | Тіло |
|---|---|---|
| `200 OK` | Індекс успішно побудовано й опубліковано. | Контракт успішної відповіді вище. |
| `400 Bad Request` | `threads <= 0`. | `{"error":"threads must be greater than zero."}` |
| `500 Internal Server Error` | Помилка конфігурації корпусу, недоступний каталог або помилка читання/побудови. | `{"error":"<повідомлення базового винятку>"}` |

Після помилки побудови попередній успішний snapshot індексу та статистики залишається доступним.

### `POST /api/search`

| Напрям | Поле | Тип | Обов’язкове | Умова або значення |
|---|---|---|---|---|
| Запит | `query` | `string` | так | Непорожній пошуковий запит. |
| Запит | `mode` | `string` | так | Точне значення `"keyword"`. |
| Відповідь `200` | `results` | `array<object>` | так | Масив результатів; до першої індексації він порожній. |
| `results[]` | `doc_id` | `string` | так | Стабільний відносний шлях документа. |
| `results[]` | `score` | `number` | так | Частка унікальних слів запиту, знайдених у документі. |
| `results[]` | `snippet` | `string` | так | Збережений фрагмент документа довжиною до 200 символів. |
| Відповідь `200` | `time_ms` | `number` | так | Час пошуку в мілісекундах. |

```json
{"query":"parallel computing","mode":"keyword"}
```

```json
{"results":[{"doc_id":"1342.txt","score":0.5,"snippet":"Text fragment..."}],"time_ms":3.21}
```

| HTTP-код | Коли повертається | Тіло |
|---|---|---|
| `200 OK` | Запит валідний, зокрема коли збігів немає. | Контракт успішної відповіді вище. |
| `400 Bad Request` | `query` відсутній, порожній або складається з пробілів. | `{"error":"query must not be empty."}` |
| `400 Bad Request` | `mode` відсутній або не дорівнює `"keyword"`. | `{"error":"mode must be 'keyword'."}` |

REST-пошук не звертається до Ollama. Багатослівний запит використовує OR, повторення слів не впливає на score, а результати сортуються за score спадно, потім за `doc_id`.

### `GET /api/stats`

Параметрів немає.

| HTTP | Поле відповіді | Тип | Обов’язкове | Значення |
|---|---|---|---|---|
| `200 OK` | `documents` | `integer` | так | Кількість документів в останньому успішному індексі. |
| `200 OK` | `index_build_time_ms` | `number` | так | Час останньої успішної побудови в мілісекундах. |
| `200 OK` | `threads_used` | `integer` | так | Кількість потоків останньої успішної побудови. |

До першої індексації всі три значення дорівнюють нулю:

```json
{"documents":0,"index_build_time_ms":0,"threads_used":0}
```

### WebSocket `/ws/search`

Web UI підключається до `ws://localhost:8080/ws/search`. Звичайний HTTP-запит без WebSocket upgrade отримує `400 Bad Request` без JSON-контракту. Після підключення клієнт надсилає одне текстове JSON-повідомлення; `query` — обов’язковий непорожній `string`:

```json
{"query":"parallel computing"}
```

За успішної обробки сервер послідовно надсилає три повідомлення.

`query_expansion`: `stage` — `string`, `data.original` — початковий `string`, `data.expanded_terms` — масив додаткових `string` (не більше п’яти):

```json
{
  "stage": "query_expansion",
  "data": {
    "original": "parallel computing",
    "expanded_terms": ["concurrency", "multithreading"]
  }
}
```

`keyword_search`: `data.results` має той самий склад полів і типи, що й REST-пошук; `data.time_ms` — `number`:

```json
{
  "stage": "keyword_search",
  "data": {
    "results": [
      {"doc_id":"1342.txt","score":0.5,"snippet":"Text fragment..."}
    ],
    "time_ms": 3.21
  }
}
```

`done` містить тільки поле `stage`, після чого сервер нормально закриває вихід WebSocket:

```json
{"stage":"done"}
```

Якщо Ollama повертає HTTP-помилку, некоректний JSON, не має очікуваного поля або перевищує тайм-аут, сервер виконує контрольований fallback. `expanded_terms` стає порожнім, `data.error` містить текст помилки (`string`), а потім пошук продовжується лише з початковим запитом і сервер надсилає `keyword_search` та `done`:

```json
{
  "stage": "query_expansion",
  "data": {
    "original": "parallel computing",
    "expanded_terms": [],
    "error": "<текст помилки>"
  }
}
```

Некоректний JSON, порожній `query` або бінарне повідомлення не запускають пошук. Сервер надсилає `stage = "error"`, де `error` — непорожній `string`, і закриває WebSocket зі статусом `InvalidPayloadData`:

```json
{"stage":"error","error":"query must not be empty."}
```

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
