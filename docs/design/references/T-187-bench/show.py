"""Filter/print the harness ledger. usage: python show.py [fx] [substring] [tab]"""
import json
import sys

sys.stdout.reconfigure(encoding="utf-8")
fx = sys.argv[1] if len(sys.argv) > 1 and sys.argv[1] != "-" else None
sub = sys.argv[2] if len(sys.argv) > 2 and sys.argv[2] != "-" else None
tab = sys.argv[3] if len(sys.argv) > 3 else None
path = r"C:\Users\priva\AppData\Local\Temp\claude\D--Programing-claude-prompt-root-master\2c9ff7ab-8298-4632-b799-b6600a56a1a9\scratchpad\t187-bench\results\timings.jsonl"
with open(path, encoding="utf-8") as f:
    for line in f:
        d = json.loads(line)
        if fx and d["fx"] != fx:
            continue
        if sub and sub not in d["op"]:
            continue
        if tab and d["tab"] != tab:
            continue
        print(f'{d["tab"]:6} {d["op"][:60]:60} {d["fx"]:5} {d["mode"]:5} #{d["run"]} {d["s"]:8.3f} {d["note"] or ""}')
