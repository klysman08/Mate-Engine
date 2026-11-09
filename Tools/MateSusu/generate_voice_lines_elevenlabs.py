"""Pre-generate Susu's voice lines with ElevenLabs (Eleven v4) and save them as WAV files.

Usage (from the repo root):
    python Tools/MateSusu/generate_voice_lines_elevenlabs.py                 # only missing files
    python Tools/MateSusu/generate_voice_lines_elevenlabs.py --force         # regenerate everything
    python Tools/MateSusu/generate_voice_lines_elevenlabs.py --only ohayou,oyasumi --force
    python Tools/MateSusu/generate_voice_lines_elevenlabs.py --dry-run       # show what would be sent + char cost
    python Tools/MateSusu/generate_voice_lines_elevenlabs.py --try-voice <voice_id> --only ohayou --out VoiceSamples/elevenlabs

Reads from .env:
    ELEVENLABS_API_KEY        (required)
    ELEVENLABS_VOICE_ID       Susu's voice (required unless --try-voice)
    ELEVENLABS_MODEL          default eleven_v4
    ELEVENLABS_STABILITY      default 0.45  (lower = more expressive, higher = steadier)
    ELEVENLABS_SIMILARITY     default 0.8   (closeness to the reference voice)
    ELEVENLABS_SEED           default 7     (best-effort reproducibility)

Phrases come from Assets/StreamingAssets/MateSusu/Voices/<pack>/phrases.json. Each phrase's "voiceText"
(Japanese text with v4 audio tags such as [giggles], [whispers], [sighs] and "…" pauses) is what gets
spoken; "text" is the clean caption and is used when voiceText is missing.
Eleven v4 has no SSML/<break>, style or speed controls: pacing comes from punctuation, ellipses and tags.
"""
import argparse
import json
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
import wave
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
API = "https://api.elevenlabs.io"
RATE = 24000  # pcm_24000 works on every plan (44.1 kHz PCM needs Pro)


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


def request(key, method, path, body=None, query=None):
    url = API + path + ("?" + urllib.parse.urlencode(query) if query else "")
    data = json.dumps(body).encode("utf-8") if body is not None else None
    req = urllib.request.Request(url, data=data, method=method,
                                 headers={"xi-api-key": key, "Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=180) as resp:
        return resp.read()


def remaining_chars(key):
    try:
        s = json.loads(request(key, "GET", "/v1/user/subscription"))
        return s["character_limit"] - s["character_count"]
    except Exception:
        return None


def synthesize(key, voice_id, model, text, stability, similarity, seed, normalize_ja):
    body = {
        "text": text,
        "model_id": model,
        "language_code": "ja",
        "voice_settings": {"stability": stability, "similarity_boost": similarity},
        "seed": seed,
    }
    if normalize_ja:
        body["apply_language_text_normalization"] = True
    return request(key, "POST", f"/v1/text-to-speech/{voice_id}", body, {"output_format": f"pcm_{RATE}"})


def trim_silence(pcm: bytes, threshold=180, keep_ms=120) -> bytes:
    """Cut long leading/trailing silence, keeping a short natural tail (breaths stay inside)."""
    n = len(pcm) // 2
    if n == 0:
        return pcm
    samples = memoryview(pcm).cast("h")
    start, end = 0, n - 1
    while start < n and abs(samples[start]) < threshold:
        start += 1
    while end > start and abs(samples[end]) < threshold:
        end -= 1
    pad = RATE * keep_ms // 1000
    start, end = max(0, start - pad), min(n, end + pad)
    return pcm[start * 2:end * 2]


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--pack", default="ja_elevenlabs", help="folder under StreamingAssets/MateSusu/Voices")
    ap.add_argument("--force", action="store_true")
    ap.add_argument("--only", default="", help="comma-separated phrase ids")
    ap.add_argument("--dry-run", action="store_true")
    ap.add_argument("--try-voice", default="", help="voice id to audition instead of ELEVENLABS_VOICE_ID")
    ap.add_argument("--out", default="", help="output folder (default: the pack folder)")
    ap.add_argument("--suffix", default="", help="appended to output file names, e.g. _yui")
    ap.add_argument("--normalize-ja", action="store_true", help="apply_language_text_normalization (Japanese only)")
    args = ap.parse_args()
    sys.stdout.reconfigure(encoding="utf-8")

    env = read_env(ROOT / ".env")
    key = env.get("ELEVENLABS_API_KEY")
    if not key:
        sys.exit("No ELEVENLABS_API_KEY in .env")
    voice = args.try_voice or env.get("ELEVENLABS_VOICE_ID", "")
    if not voice and not args.dry_run:
        sys.exit("No ELEVENLABS_VOICE_ID in .env (or pass --try-voice)")
    model = env.get("ELEVENLABS_MODEL", "eleven_v4")
    stability = float(env.get("ELEVENLABS_STABILITY", "0.45"))
    similarity = float(env.get("ELEVENLABS_SIMILARITY", "0.8"))
    seed = int(env.get("ELEVENLABS_SEED", "7"))

    folder = ROOT / "Assets" / "StreamingAssets" / "MateSusu" / "Voices" / args.pack
    out_dir = Path(args.out) if args.out else folder
    if not out_dir.is_absolute():
        out_dir = ROOT / out_dir
    out_dir.mkdir(parents=True, exist_ok=True)
    phrases = json.loads((folder / "phrases.json").read_text(encoding="utf-8"))["phrases"]
    only = [s.strip() for s in args.only.split(",") if s.strip()]
    if only:
        unknown = set(only) - {p["id"] for p in phrases}
        if unknown:
            sys.exit("Unknown ids: " + ", ".join(sorted(unknown)))

    todo = []
    for p in phrases:
        if only and p["id"] not in only:
            continue
        out = out_dir / f"{p['id']}{args.suffix}.wav"
        if out.exists() and not args.force:
            continue
        todo.append((p, out))

    cost = sum(len(p.get("voiceText") or p["text"]) for p, _ in todo)
    left = remaining_chars(key)
    print(f"{len(todo)} line(s), ~{cost} characters; account has {left if left is not None else '?'} left. "
          f"model={model} stability={stability} similarity={similarity}")
    if args.dry_run:
        for p, out in todo:
            print(f"  {p['id']:<16} {len(p.get('voiceText') or p['text']):>4}  {p.get('voiceText') or p['text']}")
        return
    if left is not None and cost > left:
        sys.exit("Not enough characters left on the ElevenLabs account; use --only to pick lines.")

    for p, out in todo:
        text = p.get("voiceText") or p["text"]
        for attempt in range(3):
            try:
                pcm = synthesize(key, voice, model, text, stability, similarity, seed, args.normalize_ja)
                break
            except urllib.error.HTTPError as e:
                msg = e.read()[:300]
                if e.code in (429, 409, 500, 503) and attempt < 2:
                    time.sleep(3 * (attempt + 1))
                    continue
                print(f"FAIL  {p['id']}: {e.code} {msg!r}")
                pcm = None
                break
        if not pcm:
            continue
        pcm = trim_silence(pcm)
        with wave.open(str(out), "wb") as w:
            w.setnchannels(1)
            w.setsampwidth(2)
            w.setframerate(RATE)
            w.writeframes(pcm)
        print(f"ok    {p['id']}: {len(pcm) / 2 / RATE:.1f}s -> {out.relative_to(ROOT)}")


if __name__ == "__main__":
    main()
