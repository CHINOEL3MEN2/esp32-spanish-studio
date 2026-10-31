"""Spanish variant of prepare.py: build a tokenizer and uint16 token bins from the
Spanish translation of TinyStories (igorshilov/tinystories_spanish, column
`text_es`).

Everything downstream of the download is prepare.py unchanged; this module only
swaps the data source and the output folder (data/tinystories-es/), so the
English bins and tokenizer are never overwritten.

--ascii folds accents, n-tilde and inverted punctuation to plain ASCII. The
device displays only printable ASCII (display.h skips every other byte), so a
model trained on accented text would lose letters on the screen.

Needs pyarrow to read the parquet shards.
"""

import argparse
import os
import sys
import unicodedata

import requests

from research.tinystories import prepare

DATASET_NAME = "tinystories-es"
REPO_URL = ("https://huggingface.co/datasets/igorshilov/tinystories_spanish/"
            "resolve/main/data/train-{index:05d}-of-00008.parquet")
EOT = "<|endoftext|>"

_PUNCT = str.maketrans({
    "¿": "", "¡": "", "“": '"', "”": '"', "«": '"', "»": '"',
    "‘": "'", "’": "'", "—": "-", "–": "-", "…": "...", " ": " ",
})


def fold_ascii(text):
    text = unicodedata.normalize("NFKD", text.translate(_PUNCT))
    text = "".join(c for c in text if not unicodedata.combining(c))
    return text.encode("ascii", "ignore").decode("ascii")


def download_shard(index, dest_dir):
    dest = dest_dir / f"train-{index:05d}.parquet"
    if dest.exists() and dest.stat().st_size > 0:
        print(f"already have {dest}")
        return dest
    url = REPO_URL.format(index=index)
    print(f"downloading {url}", flush=True)
    part = dest.with_suffix(".part")
    got = 0
    with requests.get(url, stream=True, timeout=60) as r:
        r.raise_for_status()
        total = int(r.headers.get("content-length", 0))
        with open(part, "wb") as f:
            for chunk in r.iter_content(chunk_size=1 << 20):
                f.write(chunk)
                got += len(chunk)
                if total and got % (25 << 20) < (1 << 20):
                    print(f"  {got / 1e6:.0f}/{total / 1e6:.0f}MB", flush=True)
    if total and got != total:
        part.unlink()
        raise SystemExit(f"incomplete download of shard {index}: {got} of {total} bytes")
    os.replace(part, dest)
    return dest


def build_raw_text(shards, max_bytes, ascii_only, max_chars=0):
    try:
        import pyarrow.parquet as pq
    except ImportError:
        raise SystemExit("pyarrow is required: uv pip install pyarrow")

    raw = prepare.RAW
    if raw.exists() and raw.stat().st_size > 0:
        print(f"already have {raw}")
        return
    shard_dir = raw.parent
    written = stories = 0
    tmp = raw.with_suffix(".part")
    with open(tmp, "w", encoding="utf-8", newline="\n") as out:
        for index in range(shards):
            path = download_shard(index, shard_dir)
            for batch in pq.ParquetFile(path).iter_batches(columns=["text_es"], batch_size=2048):
                for story in batch.column("text_es").to_pylist():
                    if not story or not story.strip():
                        continue
                    story = fold_ascii(story) if ascii_only else story
                    story = story.strip()
                    if EOT in story:
                        continue
                    # The device context is 256 tokens: keep only stories that can finish inside it.
                    if max_chars and len(story) > max_chars:
                        continue
                    chunk = story + EOT
                    out.write(chunk)
                    written += len(chunk.encode("utf-8"))
                    stories += 1
                    if written >= max_bytes:
                        break
                if written >= max_bytes:
                    break
            if written >= max_bytes:
                break
    os.replace(tmp, raw)
    print(f"wrote {raw}: {stories:,} stories, {written / 1e6:.0f}MB", flush=True)


def main():
    ap = argparse.ArgumentParser(description=__doc__, add_help=False)
    ap.add_argument("--shards", type=int, default=1,
                    help="parquet shards to download, 1..8 (~265k stories each)")
    ap.add_argument("--max-mb", type=int, default=300,
                    help="cap on text kept, in MiB (prepare.py uses 300)")
    ap.add_argument("--max-chars", type=int, default=0,
                    help="drop stories longer than this many characters (0 keeps all)")
    ap.add_argument("--ascii", action="store_true",
                    help="fold to printable ASCII so the device display can show it")
    own, rest = ap.parse_known_args()
    if not 1 <= own.shards <= 8:
        raise SystemExit("--shards must be 1..8")

    data = prepare.DATA.parent / DATASET_NAME
    prepare.DATA = data
    prepare.RAW = data / "raw" / ("es-ascii.txt" if own.ascii else "es-utf8.txt")
    prepare.RAW.parent.mkdir(parents=True, exist_ok=True)
    prepare.download = lambda: build_raw_text(own.shards, own.max_mb * 1024 * 1024, own.ascii, own.max_chars)

    sys.argv = [sys.argv[0], *rest]
    return prepare.main()


if __name__ == "__main__":
    sys.exit(main())
