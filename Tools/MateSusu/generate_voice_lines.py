"""Pre-generate Susu's voice lines with OpenRouter TTS and save them as WAV files.

Usage (from the repo root):
    python Tools/MateSusu/generate_voice_lines.py            # only missing files
    python Tools/MateSusu/generate_voice_lines.py --force    # regenerate everything
    python Tools/MateSusu/generate_voice_lines.py --lang ja --only ohayou,oyasumi

Reads API key, model, voice and base direction from .env (same keys the app uses) and the
phrase list from Assets/StreamingAssets/MateSusu/Voices/<lang>/phrases.json.
"""
import argparse
import json
import re
import sys
import urllib.error
import urllib.request
import wave
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
ENDPOINT = "https://openrouter.ai/api/v1/audio/speech"


def read_env(path: Path) -> dict:
    values = {}
    if not path.exists():
        return values
    for raw in path.read_text(encoding="utf-8").splitlines():
        line = raw.strip()
        if not line or line.startswith("#") or "=" not in line:
            continue
        key, val = line.split("=", 1)
        key, val = key.strip(), val.strip()
        if len(val) >= 2 and val[0] in "\"'" and val[-1] == val[0]:
            val = val[1:-1]
        values[key] = val
    return values


def synthesize(key, model, text, voice, instructions):
    body = {"model": model, "input": text, "response_format": "pcm"}
    if voice:
        body["voice"] = voice
    if instructions:
        body["instructions"] = instructions
    req = urllib.request.Request(
        ENDPOINT,
        data=json.dumps(body).encode("utf-8"),
        headers={
            "Authorization": "Bearer " + key,
            "Content-Type": "application/json",
            "X-Title": "MateSusu",
        },
    )
    with urllib.request.urlopen(req, timeout=120) as resp:
        ctype = resp.headers.get("Content-Type", "")
        data = resp.read()
    m = re.search(r"rate=(\d+)", ctype)
    rate = int(m.group(1)) if m else 24000
    return data, rate


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--lang", default="ja")
    ap.add_argument("--force", action="store_true")
    ap.add_argument("--only", default="", help="comma-separated phrase ids")
    args = ap.parse_args()

    env = read_env(ROOT / ".env")
    key = env.get("OPENROUTER_API_KEY") or env.get("API_KEY")
    if not key:
        sys.exit("No API key in .env")
    model = env.get("OPENROUTER_TTS_MODEL", "fish-audio/drama-3")
    voice = env.get("OPENROUTER_TTS_VOICE_JA" if args.lang == "ja" else "OPENROUTER_TTS_VOICE_EN", "")
    base = env.get("OPENROUTER_TTS_INSTRUCTIONS", "cute, cheerful, affectionate young girl, soft and sweet")

    folder = ROOT / "Assets" / "StreamingAssets" / "MateSusu" / "Voices" / args.lang
    phrases = json.loads((folder / "phrases.json").read_text(encoding="utf-8"))["phrases"]
    only = {s.strip() for s in args.only.split(",") if s.strip()}

    for p in phrases:
        if only and p["id"] not in only:
            continue
        out = folder / (p["id"] + ".wav")
        if out.exists() and not args.force:
            print(f"skip  {p['id']} (exists)")
            continue
        direction = ", ".join(x for x in (base, p.get("direction", "")) if x)
        try:
            pcm, rate = synthesize(key, model, p["text"], voice, direction)
        except urllib.error.HTTPError as e:
            print(f"FAIL  {p['id']}: {e.code} {e.read()[:200]!r}")
            continue
        with wave.open(str(out), "wb") as w:
            w.setnchannels(1)
            w.setsampwidth(2)
            w.setframerate(rate)
            w.writeframes(pcm)
        print(f"ok    {p['id']}: {len(pcm) / 2 / rate:.1f}s @ {rate} Hz -> {out.relative_to(ROOT)}")


if __name__ == "__main__":
    main()
