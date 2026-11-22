import itertools
import json
import os
import re
import sys
import uuid
from dataclasses import dataclass
from pathlib import Path
from typing import Callable, Iterable, Mapping


DATASET_REPOSITORY = "nandhakumarms/qualc-gutenberg-en"
DATASET_REVISION = "main"
EXPECTED_DOCUMENTS = 10_048
MINIMUM_TEXT_BYTES = 2 * 1024 * 1024 * 1024
PARQUET_BATCH_SIZE = 16
DOWNLOAD_CHUNK_SIZE = 8 * 1024 * 1024
DOWNLOAD_PROGRESS_INTERVAL = 64 * 1024 * 1024
FULL_MARKER_NAME = ".full-corpus.json"
SHARD_NAMES = tuple(
    f"train-{index:05d}-of-00008.parquet"
    for index in range(8)
)


@dataclass(frozen=True)
class ProcessingResult:
    rows_seen: int
    written: int
    reused: int
    limit_reached: bool
    manifest: dict[str, int]


@dataclass(frozen=True)
class CorpusStats:
    document_count: int
    total_bytes: int


def stable_filename(book_id: object) -> str:
    value = str(book_id).strip()

    if re.fullmatch(r"[0-9]+", value) is None:
        raise ValueError(f"Invalid Gutenberg book ID: {book_id!r}")

    return f"{int(value)}.txt"


def prepare_rows(
    rows: Iterable[Mapping[str, object]],
    output_directory: Path,
    max_documents: int | None = None,
    progress: Callable[[str], None] | None = None,
) -> ProcessingResult:
    if max_documents is not None and max_documents <= 0:
        raise ValueError("max_documents must be greater than zero")

    output_directory.mkdir(parents=True, exist_ok=True)
    selected_rows = rows if max_documents is None else itertools.islice(
        rows,
        max_documents,
    )
    rows_seen = 0
    written = 0
    reused = 0
    manifest: dict[str, int] = {}

    for row in selected_rows:
        if "id" not in row or "text" not in row:
            raise ValueError("A dataset row is missing id or text")

        filename = stable_filename(row["id"])

        if filename in manifest:
            raise ValueError(f"Duplicate Gutenberg book ID in shard: {row['id']!r}")

        text = row["text"]

        if not isinstance(text, str) or not text:
            raise ValueError(f"Book {row['id']!r} has empty or invalid text")

        encoded_text = text.encode("utf-8")
        destination = output_directory / filename

        if destination.is_file() and destination.stat().st_size == len(encoded_text):
            reused += 1
        else:
            atomic_write(destination, encoded_text)
            written += 1

        manifest[filename] = len(encoded_text)
        rows_seen += 1

        if progress is not None and (rows_seen <= 10 or rows_seen % 100 == 0):
            progress(
                f"Prepared {rows_seen} documents "
                f"({written} written, {reused} reused)."
            )

    return ProcessingResult(
        rows_seen=rows_seen,
        written=written,
        reused=reused,
        limit_reached=max_documents is not None and rows_seen >= max_documents,
        manifest=manifest,
    )


def atomic_write(destination: Path, content: bytes) -> None:
    destination.parent.mkdir(parents=True, exist_ok=True)
    temporary = destination.with_name(
        f".{destination.name}.tmp-{os.getpid()}-{uuid.uuid4().hex}"
    )

    try:
        with temporary.open("xb") as output:
            output.write(content)
            output.flush()
            os.fsync(output.fileno())

        os.replace(temporary, destination)
    finally:
        temporary.unlink(missing_ok=True)


def atomic_write_json(destination: Path, value: object) -> None:
    content = json.dumps(value, ensure_ascii=False, indent=2).encode("utf-8")
    atomic_write(destination, content)


def corpus_stats(output_directory: Path) -> CorpusStats:
    document_count = 0
    total_bytes = 0

    if not output_directory.exists():
        return CorpusStats(0, 0)

    for path in output_directory.glob("*.txt"):
        if path.is_file() and re.fullmatch(r"[0-9]+\.txt", path.name):
            document_count += 1
            total_bytes += path.stat().st_size

    return CorpusStats(document_count, total_bytes)


def create_http_session():
    import requests
    from requests.adapters import HTTPAdapter
    from urllib3.util.retry import Retry

    retry = Retry(
        total=5,
        connect=5,
        read=5,
        status=5,
        backoff_factor=1,
        status_forcelist=(429, 500, 502, 503, 504),
        allowed_methods=frozenset({"GET"}),
        respect_retry_after_header=True,
    )
    session = requests.Session()
    adapter = HTTPAdapter(max_retries=retry)
    session.mount("https://", adapter)
    session.headers["User-Agent"] = "ParallelSearch-CorpusPreparer/1.0"
    return session


def download_shard(session, shard_name: str, download_directory: Path) -> Path:
    download_directory.mkdir(parents=True, exist_ok=True)
    destination = download_directory / shard_name
    partial = destination.with_suffix(destination.suffix + ".part")

    if destination.is_file() and destination.stat().st_size > 0:
        print(f"Using previously downloaded shard {shard_name}.", flush=True)
        return destination

    existing_bytes = partial.stat().st_size if partial.exists() else 0
    headers = {"Range": f"bytes={existing_bytes}-"} if existing_bytes else {}
    url = (
        "https://huggingface.co/datasets/"
        f"{DATASET_REPOSITORY}/resolve/{DATASET_REVISION}/data/{shard_name}"
        "?download=true"
    )
    print(
        f"Downloading {shard_name}"
        + (f" (resuming at {format_bytes(existing_bytes)})" if existing_bytes else "")
        + "...",
        flush=True,
    )

    with session.get(
        url,
        stream=True,
        headers=headers,
        timeout=(30, 120),
        allow_redirects=True,
    ) as response:
        if response.status_code == 416 and existing_bytes:
            total = parse_range_total(response.headers.get("Content-Range"))

            if total == existing_bytes:
                os.replace(partial, destination)
                return destination

            partial.unlink(missing_ok=True)
            print(
                f"Discarding incompatible partial download for {shard_name}.",
                flush=True,
            )
            response.close()
            return download_shard(session, shard_name, download_directory)

        response.raise_for_status()
        append = response.status_code == 206 and existing_bytes > 0
        mode = "ab" if append else "wb"
        completed_bytes = existing_bytes if append else 0
        response_bytes = int(response.headers.get("Content-Length", "0"))
        expected_bytes = completed_bytes + response_bytes if response_bytes else None
        next_report = completed_bytes + DOWNLOAD_PROGRESS_INTERVAL

        with partial.open(mode) as output:
            for chunk in response.iter_content(chunk_size=DOWNLOAD_CHUNK_SIZE):
                if not chunk:
                    continue

                output.write(chunk)
                completed_bytes += len(chunk)

                if completed_bytes >= next_report:
                    print_download_progress(shard_name, completed_bytes, expected_bytes)
                    next_report = completed_bytes + DOWNLOAD_PROGRESS_INTERVAL

            output.flush()
            os.fsync(output.fileno())

    if expected_bytes is not None and partial.stat().st_size != expected_bytes:
        raise IOError(
            f"Incomplete download for {shard_name}: "
            f"expected {expected_bytes} bytes, got {partial.stat().st_size}."
        )

    os.replace(partial, destination)
    print_download_progress(shard_name, destination.stat().st_size, expected_bytes)
    return destination


def parse_range_total(content_range: str | None) -> int | None:
    if content_range is None:
        return None

    match = re.fullmatch(r"bytes \*/([0-9]+)", content_range)
    return int(match.group(1)) if match else None


def print_download_progress(
    shard_name: str,
    completed_bytes: int,
    expected_bytes: int | None,
) -> None:
    if expected_bytes:
        percent = completed_bytes * 100 / expected_bytes
        print(
            f"Downloaded {shard_name}: {format_bytes(completed_bytes)} "
            f"of {format_bytes(expected_bytes)} ({percent:.1f}%).",
            flush=True,
        )
    else:
        print(
            f"Downloaded {shard_name}: {format_bytes(completed_bytes)}.",
            flush=True,
        )


def iter_parquet_rows(parquet_path: Path):
    import pyarrow.parquet as parquet

    parquet_file = parquet.ParquetFile(parquet_path)
    required_columns = {"id", "text"}

    if not required_columns.issubset(parquet_file.schema_arrow.names):
        raise ValueError(
            f"Shard {parquet_path.name} does not contain id and text columns."
        )

    for batch in parquet_file.iter_batches(
        batch_size=PARQUET_BATCH_SIZE,
        columns=["id", "text"],
        use_threads=False,
    ):
        yield from batch.to_pylist()


def shard_marker_path(marker_directory: Path, shard_name: str) -> Path:
    return marker_directory / f"{shard_name}.json"


def is_shard_complete(
    marker_directory: Path,
    output_directory: Path,
    shard_name: str,
) -> bool:
    files = read_shard_manifest(marker_directory, shard_name)

    if files is None:
        return False

    for filename, expected_size in files.items():
        path = output_directory / filename
        if not path.is_file() or path.stat().st_size != expected_size:
            return False

    return True


def read_shard_manifest(
    marker_directory: Path,
    shard_name: str,
) -> dict[str, int] | None:
    marker_path = shard_marker_path(marker_directory, shard_name)

    try:
        marker = json.loads(marker_path.read_text(encoding="utf-8"))
        if marker.get("shard") != shard_name:
            return None

        files = marker.get("files")
        if not isinstance(files, dict) or not files:
            return None

        for filename, expected_size in files.items():
            if (
                not isinstance(filename, str)
                or re.fullmatch(r"[0-9]+\.txt", filename) is None
                or not isinstance(expected_size, int)
                or expected_size <= 0
            ):
                return None

        return files
    except (OSError, ValueError, TypeError, json.JSONDecodeError):
        return None


def write_shard_marker(
    marker_directory: Path,
    shard_name: str,
    result: ProcessingResult,
) -> None:
    marker_directory.mkdir(parents=True, exist_ok=True)
    atomic_write_json(
        shard_marker_path(marker_directory, shard_name),
        {
            "shard": shard_name,
            "documents": result.rows_seen,
            "files": result.manifest,
        },
    )


def all_shards_complete(marker_directory: Path, output_directory: Path) -> bool:
    return all(
        is_shard_complete(marker_directory, output_directory, shard_name)
        for shard_name in SHARD_NAMES
    )


def cleanup_temporary_files(directory: Path) -> None:
    if not directory.exists():
        return

    for path in directory.glob("*.tmp-*"):
        if path.is_file():
            path.unlink()


def prepare_corpus(
    output_directory: Path,
    download_directory: Path,
    max_documents: int | None,
) -> CorpusStats:
    output_directory.mkdir(parents=True, exist_ok=True)
    download_directory.mkdir(parents=True, exist_ok=True)
    marker_directory = output_directory / ".shards"
    cleanup_temporary_files(output_directory)
    cleanup_temporary_files(marker_directory)

    initial_stats = corpus_stats(output_directory)
    print(
        f"Existing corpus: {initial_stats.document_count} documents, "
        f"{format_bytes(initial_stats.total_bytes)} of UTF-8 text.",
        flush=True,
    )

    if max_documents is not None and initial_stats.document_count >= max_documents:
        print(
            f"Sample mode already has at least {max_documents} real documents; "
            "nothing to download.",
            flush=True,
        )
        return initial_stats

    if max_documents is None and all_shards_complete(
        marker_directory,
        output_directory,
    ):
        validate_and_mark_full_corpus(output_directory, marker_directory)
        return corpus_stats(output_directory)

    session = create_http_session()

    try:
        for shard_name in SHARD_NAMES:
            if max_documents is None and is_shard_complete(
                marker_directory,
                output_directory,
                shard_name,
            ):
                print(f"Shard {shard_name} is already complete; skipping.", flush=True)
                continue

            parquet_path = download_shard(session, shard_name, download_directory)
            print(f"Reading {shard_name} in small batches...", flush=True)
            result = prepare_rows(
                iter_parquet_rows(parquet_path),
                output_directory,
                max_documents=max_documents,
                progress=lambda message: print(message, flush=True),
            )

            if max_documents is None:
                write_shard_marker(marker_directory, shard_name, result)
                print(
                    f"Completed {shard_name}: {result.rows_seen} rows, "
                    f"{result.written} written, {result.reused} reused.",
                    flush=True,
                )

            parquet_path.unlink(missing_ok=True)

            if max_documents is not None:
                if not result.limit_reached:
                    raise RuntimeError(
                        f"Shard {shard_name} contained fewer than "
                        f"{max_documents} documents."
                    )
                break
    finally:
        session.close()

    stats = corpus_stats(output_directory)

    if max_documents is not None:
        if stats.document_count < max_documents:
            raise RuntimeError(
                f"Sample preparation produced only {stats.document_count} documents; "
                f"expected at least {max_documents}."
            )

        print(
            f"SAMPLE MODE COMPLETE: {stats.document_count} real documents, "
            f"{format_bytes(stats.total_bytes)}. Full-corpus marker was not created.",
            flush=True,
        )
        return stats

    if not all_shards_complete(marker_directory, output_directory):
        raise RuntimeError("Not all eight dataset shards were prepared successfully.")

    validate_and_mark_full_corpus(output_directory, marker_directory)
    return stats


def validate_and_mark_full_corpus(
    output_directory: Path,
    marker_directory: Path,
) -> None:
    stats = corpus_stats(output_directory)
    failures = []
    source_files: dict[str, int] = {}

    for shard_name in SHARD_NAMES:
        files = read_shard_manifest(marker_directory, shard_name)

        if files is None:
            failures.append(f"missing or invalid manifest for {shard_name}")
            continue

        for filename, size in files.items():
            if filename in source_files:
                failures.append(f"duplicate source ID {filename[:-4]}")
            else:
                source_files[filename] = size

    if len(source_files) != EXPECTED_DOCUMENTS:
        failures.append(
            f"manifests contain {len(source_files)} unique IDs; "
            f"expected {EXPECTED_DOCUMENTS}"
        )

    if stats.document_count != EXPECTED_DOCUMENTS:
        failures.append(
            f"found {stats.document_count} real .txt files; expected {EXPECTED_DOCUMENTS}"
        )

    if stats.total_bytes < MINIMUM_TEXT_BYTES:
        failures.append(
            f"only {format_bytes(stats.total_bytes)} of UTF-8 text; "
            f"required at least {format_bytes(MINIMUM_TEXT_BYTES)}"
        )

    if failures:
        raise RuntimeError("Full corpus validation failed: " + "; ".join(failures))

    atomic_write_json(
        output_directory / FULL_MARKER_NAME,
        {
            "dataset": DATASET_REPOSITORY,
            "revision": DATASET_REVISION,
            "documents": stats.document_count,
            "utf8_bytes": stats.total_bytes,
            "complete": True,
        },
    )
    print(
        f"FULL CORPUS COMPLETE: {stats.document_count} documents, "
        f"{format_bytes(stats.total_bytes)} of UTF-8 text.",
        flush=True,
    )


def parse_max_documents(value: str | None) -> int | None:
    if value is None or not value.strip():
        return None

    try:
        parsed = int(value)
    except ValueError as exception:
        raise ValueError("CORPUS_MAX_DOCUMENTS must be a positive integer") from exception

    if parsed <= 0:
        raise ValueError("CORPUS_MAX_DOCUMENTS must be a positive integer")

    return parsed


def format_bytes(value: int) -> str:
    units = ("B", "KiB", "MiB", "GiB", "TiB")
    size = float(value)

    for unit in units:
        if size < 1024 or unit == units[-1]:
            return f"{size:.2f} {unit}"
        size /= 1024

    raise AssertionError("unreachable")


def main() -> int:
    output_directory = Path(
        os.environ.get("CORPUS_OUTPUT_DIR", "/data/documents")
    )
    download_directory = Path(
        os.environ.get("CORPUS_DOWNLOAD_DIR", "/data/downloads")
    )
    max_documents = parse_max_documents(
        os.environ.get("CORPUS_MAX_DOCUMENTS")
    )
    mode = (
        f"sample ({max_documents} documents)"
        if max_documents is not None
        else "full (all eight shards)"
    )
    print(f"Starting QualC Gutenberg preparation in {mode} mode.", flush=True)
    prepare_corpus(output_directory, download_directory, max_documents)
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except Exception as exception:
        print(f"ERROR: corpus preparation failed: {exception}", file=sys.stderr, flush=True)
        raise SystemExit(1)
