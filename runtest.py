import json, sys, time, urllib.request, urllib.error, uuid

BASE = "http://localhost:5000"
MAX_WAIT = 1200  # hard ceiling for the poll loop

def post(path, body):
    req = urllib.request.Request(BASE+path, data=json.dumps(body).encode(),
                                 headers={"Content-Type":"application/json"}, method="POST")
    with urllib.request.urlopen(req) as r:
        return json.loads(r.read())

def get(path):
    """Returns parsed JSON, or None on 404 (result not saved yet)."""
    try:
        with urllib.request.urlopen(BASE+path) as r:
            return json.loads(r.read())
    except urllib.error.HTTPError as e:
        if e.code == 404:
            return None
        raise

def run(preset, files, ws, label):
    aid = str(uuid.uuid4())
    print(f"\n=== {label} :: preset={preset} files={len(files)} aid={aid} ===")
    post("/api/analysis/run", {
        "mode":"preset","presetKey":preset,"freeTextPrompt":None,
        "selectedFilePaths":files,"workspaceId":ws,"analysisId":aid})
    t0 = time.time()
    while True:
        time.sleep(6)
        el = time.time() - t0
        r = get(f"/api/analysis/{aid}")
        if r is None:
            print(f"  [{el:5.0f}s] (not saved yet)")
            if el > MAX_WAIT:
                print(f"  GAVE UP after {el:.0f}s — no result row (still going, cancelled w/o partial, or failed)")
                return None
            continue
        if r.get("completedAt"):
            fc = len(r.get("findings", []))
            print(f"  DONE in {el:.0f}s — {fc} findings")
            for i, f in enumerate(r["findings"]):
                print(f"    #{i+1} [{f['severity']}/{f.get('confidence','?')}] {f['title']}")
                print(f"        {f.get('filePath')}:{f.get('lineNumber')}")
            raw = r.get("rawLlmOutput", "")
            print(f"  raw output: {len(raw)} chars")
            print(f"  raw tail: ...{raw[-500:]!r}")
            return r
        print(f"  [{el:5.0f}s] saved but no completedAt (partial?)")
        if el > MAX_WAIT:
            print(f"  GAVE UP after {el:.0f}s")
            return r

if __name__ == "__main__":
    ws, preset, label = sys.argv[1], sys.argv[2], sys.argv[3]
    run(preset, sys.argv[4:], ws, label)
