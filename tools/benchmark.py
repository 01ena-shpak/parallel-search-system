import argparse
import csv
import json
import socket
import sys
import time
from concurrent.futures import ThreadPoolExecutor, as_completed
from pathlib import Path
from urllib.error import HTTPError, URLError
from urllib.parse import urljoin, urlparse
from urllib.request import Request, urlopen


INDEX_THREAD_COUNTS = (1, 2, 4, 8)
CLIENT_COUNTS = (1, 2, 4, 8)
DEFAULT_QUERIES = (
    "love",
    "history",
    "world",
    "time",
    "war",
    "life",
    "man",
    "woman",
)
CSV_FIELDS = (
    "phase",
    "documents",
    "threads",
    "index_time_ms",
    "index_wall_time_ms",
    "clients",
    "requested_queries",
    "completed_queries",
    "errors",
    "total_time_seconds",
    "requests_per_second",
    "error_details",
)


class BenchmarkError(RuntimeError):
    pass


def request_json(
    api_url: str,
    method: str,
    path: str,
    timeout: float,
    payload: dict | None = None,
) -> dict:
    url = urljoin(api_url.rstrip("/") + "/", path.lstrip("/"))
    data = None if payload is None else json.dumps(payload).encode("utf-8")
    headers = {"Accept": "application/json"}

    if data is not None:
        headers["Content-Type"] = "application/json"

    request = Request(url, data=data, headers=headers, method=method)

    try:
        with urlopen(request, timeout=timeout) as response:
            body = response.read().decode("utf-8")
    except HTTPError as error:
        body = error.read().decode("utf-8", errors="replace")
        try:
            message = json.loads(body).get("error", body)
        except json.JSONDecodeError:
            message = body
        raise BenchmarkError(f"HTTP {error.code}: {message}") from error
    except (URLError, TimeoutError, socket.timeout) as error:
        raise BenchmarkError(f"Request failed: {error}") from error

    try:
        result = json.loads(body)
    except json.JSONDecodeError as error:
        raise BenchmarkError("API returned invalid JSON") from error

    if not isinstance(result, dict):
        raise BenchmarkError("API returned a non-object JSON response")

    return result


def empty_row(phase: str) -> dict[str, object]:
    return {field: "" for field in CSV_FIELDS} | {"phase": phase}


def run_index_benchmarks(
    api_url: str,
    timeout: float,
    writer: csv.DictWriter,
    output_file,
) -> tuple[int | None, int | None, bool]:
    last_documents = None
    last_threads = None
    had_errors = False

    for threads in INDEX_THREAD_COUNTS:
        print(f"Index benchmark: {threads} thread(s)...", flush=True)
        started = time.perf_counter()
        row = empty_row("index")
        row["threads"] = threads

        try:
            response = request_json(
                api_url,
                "POST",
                "/api/index",
                timeout,
                {"threads": threads},
            )
            elapsed = time.perf_counter() - started
            documents = response.get("documents_indexed")
            index_time_ms = response.get("time_ms")

            if response.get("status") != "ok" or not isinstance(documents, int):
                raise BenchmarkError("Index API returned an unexpected response")

            row.update(
                documents=documents,
                index_time_ms=index_time_ms,
                index_wall_time_ms=round(elapsed * 1000, 3),
                errors=0,
                total_time_seconds=round(elapsed, 6),
            )
            last_documents = documents
            last_threads = threads
        except BenchmarkError as error:
            elapsed = time.perf_counter() - started
            had_errors = True
            row.update(
                index_wall_time_ms=round(elapsed * 1000, 3),
                errors=1,
                total_time_seconds=round(elapsed, 6),
                error_details=str(error),
            )
            print(f"  ERROR: {error}", file=sys.stderr, flush=True)

        writer.writerow(row)
        output_file.flush()

    return last_documents, last_threads, had_errors


def execute_search(
    api_url: str,
    timeout: float,
    query: str,
) -> str | None:
    try:
        response = request_json(
            api_url,
            "POST",
            "/api/search",
            timeout,
            {"query": query, "mode": "keyword"},
        )

        if not isinstance(response.get("results"), list):
            raise BenchmarkError("Search API returned an unexpected response")

        return None
    except BenchmarkError as error:
        return str(error)


def run_search_benchmarks(
    api_url: str,
    timeout: float,
    request_count: int,
    queries: tuple[str, ...],
    documents: int,
    index_threads: int,
    writer: csv.DictWriter,
    output_file,
) -> bool:
    had_errors = False

    for clients in CLIENT_COUNTS:
        print(
            f"Search benchmark: {clients} client(s), "
            f"{request_count} request(s)...",
            flush=True,
        )
        started = time.perf_counter()
        errors = []

        with ThreadPoolExecutor(max_workers=clients) as executor:
            futures = [
                executor.submit(
                    execute_search,
                    api_url,
                    timeout,
                    queries[index % len(queries)],
                )
                for index in range(request_count)
            ]

            for future in as_completed(futures):
                error = future.result()
                if error is not None:
                    errors.append(error)

        elapsed = time.perf_counter() - started
        completed = request_count - len(errors)
        requests_per_second = completed / elapsed if elapsed > 0 else 0.0
        row = empty_row("search")
        row.update(
            documents=documents,
            threads=index_threads,
            clients=clients,
            requested_queries=request_count,
            completed_queries=completed,
            errors=len(errors),
            total_time_seconds=round(elapsed, 6),
            requests_per_second=round(requests_per_second, 3),
            error_details=" | ".join(errors[:5]),
        )
        writer.writerow(row)
        output_file.flush()
        had_errors = had_errors or bool(errors)

    return had_errors


def parse_arguments() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Benchmark ParallelSearch indexing and REST keyword search.",
    )
    parser.add_argument(
        "--api-url",
        default="http://localhost:8080",
        help="ParallelSearch API base URL.",
    )
    parser.add_argument(
        "--requests",
        type=int,
        default=100,
        help="Search requests per concurrency level.",
    )
    parser.add_argument(
        "--output",
        type=Path,
        default=Path("benchmark-results.csv"),
        help="Output CSV path.",
    )
    parser.add_argument(
        "--query",
        action="append",
        dest="queries",
        help="Search query; repeat to provide multiple real-index queries.",
    )
    parser.add_argument(
        "--index-timeout",
        type=float,
        default=1800,
        help="Timeout in seconds for each index build.",
    )
    parser.add_argument(
        "--search-timeout",
        type=float,
        default=30,
        help="Timeout in seconds for each search request.",
    )
    return parser.parse_args()


def validate_arguments(arguments: argparse.Namespace) -> tuple[str, ...]:
    parsed_url = urlparse(arguments.api_url)

    if parsed_url.scheme not in {"http", "https"} or not parsed_url.netloc:
        raise ValueError("--api-url must be an absolute HTTP or HTTPS URL")

    if arguments.requests <= 0:
        raise ValueError("--requests must be greater than zero")

    if arguments.index_timeout <= 0 or arguments.search_timeout <= 0:
        raise ValueError("Timeouts must be greater than zero")

    queries = tuple(arguments.queries or DEFAULT_QUERIES)

    if any(not query.strip() for query in queries):
        raise ValueError("Queries must not be empty")

    return queries


def main() -> int:
    arguments = parse_arguments()

    try:
        queries = validate_arguments(arguments)
    except ValueError as error:
        print(f"ERROR: {error}", file=sys.stderr)
        return 2

    arguments.output.parent.mkdir(parents=True, exist_ok=True)
    had_errors = False

    with arguments.output.open("w", newline="", encoding="utf-8") as output_file:
        writer = csv.DictWriter(output_file, fieldnames=CSV_FIELDS)
        writer.writeheader()
        documents, index_threads, index_errors = run_index_benchmarks(
            arguments.api_url,
            arguments.index_timeout,
            writer,
            output_file,
        )
        had_errors = had_errors or index_errors

        if documents is None or index_threads is None:
            print(
                "ERROR: no successful index build; search benchmark skipped.",
                file=sys.stderr,
            )
            return 1

        try:
            stats = request_json(
                arguments.api_url,
                "GET",
                "/api/stats",
                arguments.search_timeout,
            )
            current_documents = stats.get("documents")
            if current_documents != documents:
                raise BenchmarkError(
                    "Current index statistics do not match the last successful build"
                )
        except BenchmarkError as error:
            print(f"ERROR: {error}", file=sys.stderr)
            return 1

        had_errors = run_search_benchmarks(
            arguments.api_url,
            arguments.search_timeout,
            arguments.requests,
            queries,
            documents,
            index_threads,
            writer,
            output_file,
        ) or had_errors

    print(f"Benchmark results written to {arguments.output}.", flush=True)
    return 1 if had_errors else 0


if __name__ == "__main__":
    raise SystemExit(main())
