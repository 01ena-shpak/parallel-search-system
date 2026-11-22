import os
import sys
import tempfile
import unittest
from pathlib import Path
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parent))

import prepare_corpus

TEST_DIRECTORY = Path(__file__).resolve().parent


class CorpusPreparationTests(unittest.TestCase):
    def test_stable_filename_uses_original_numeric_id(self):
        self.assertEqual("42.txt", prepare_corpus.stable_filename(42))
        self.assertEqual("10048.txt", prepare_corpus.stable_filename("10048"))

        with self.assertRaises(ValueError):
            prepare_corpus.stable_filename("../42")

    def test_prepare_rows_saves_utf8_text_atomically(self):
        with tempfile.TemporaryDirectory(dir=TEST_DIRECTORY) as temporary_directory:
            output = Path(temporary_directory)
            text = "English text with Unicode: café"

            result = prepare_corpus.prepare_rows(
                [{"id": 7, "text": text}],
                output,
            )

            self.assertEqual(text.encode("utf-8"), (output / "7.txt").read_bytes())
            self.assertEqual(1, result.written)
            self.assertEqual([], list(output.glob("*.tmp-*")))

    def test_prepare_rows_reuses_completed_document(self):
        with tempfile.TemporaryDirectory(dir=TEST_DIRECTORY) as temporary_directory:
            output = Path(temporary_directory)
            rows = [{"id": 9, "text": "Already complete"}]
            prepare_corpus.prepare_rows(rows, output)
            original_mtime = (output / "9.txt").stat().st_mtime_ns

            with mock.patch(
                "prepare_corpus.os.replace",
                wraps=os.replace,
            ) as replace:
                result = prepare_corpus.prepare_rows(rows, output)

            self.assertEqual(0, result.written)
            self.assertEqual(1, result.reused)
            self.assertEqual(original_mtime, (output / "9.txt").stat().st_mtime_ns)
            replace.assert_not_called()

    def test_sample_mode_stops_after_ten_real_rows_without_full_marker(self):
        with tempfile.TemporaryDirectory(dir=TEST_DIRECTORY) as temporary_directory:
            output = Path(temporary_directory)
            consumed = 0

            def rows():
                nonlocal consumed
                for book_id in range(1, 101):
                    consumed += 1
                    yield {"id": book_id, "text": f"Book {book_id}"}

            result = prepare_corpus.prepare_rows(
                rows(),
                output,
                max_documents=10,
            )

            self.assertEqual(10, consumed)
            self.assertEqual(10, len(list(output.glob("*.txt"))))
            self.assertTrue(result.limit_reached)
            self.assertFalse((output / ".full-corpus.json").exists())


if __name__ == "__main__":
    unittest.main()
