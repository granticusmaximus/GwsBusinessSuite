#!/usr/bin/env python3
"""Reports CMS content that a Markdown<->HTML WYSIWYG round-trip could not preserve.

The canvas inline editor serializes HTML back to Markdown and only knows:
  p br strong/b em/i u s/del h1-h3 ul/ol/li blockquote pre/code a hr
Markdig here runs UseAdvancedExtensions(), so content MAY legally contain much more
(tables, footnotes, task lists, math, images, definition lists). Anything this prints is
content that click-to-type editing would damage, and therefore needs an inspector fallback.

Usage
-----
Local:
    python3 scripts/scan-cms-markdown.py src/GwsBusinessSuite.Web/gws-suite.db

Production (reads a throwaway copy; never touches the live database). Set GWS_DROPLET to
the deploy target - it is deliberately not committed here, because this repository is public
and the origin host sits behind Cloudflare:

    export GWS_DROPLET=root@<droplet-host>
    scp scripts/scan-cms-markdown.py "$GWS_DROPLET":/tmp/
    ssh "$GWS_DROPLET" 'cd /opt/gwssuite \
      && docker compose cp gwssuite:/app/data/gws-suite.db /tmp/prod-cms.db \
      && python3 /tmp/scan-cms-markdown.py /tmp/prod-cms.db; \
      rm -f /tmp/prod-cms.db'

Exit code is 0 regardless of findings - this is a report, not a gate.
"""
import json, re, sqlite3, sys

DB = sys.argv[1] if len(sys.argv) > 1 else "/app/data/gws-suite.db"

# Only these props are rendered through Markdig (CmsBlockHtmlRenderer.Markdown.ToHtml).
MD_PROPS = {
    "hero": ["subline"], "paragraph": ["text"], "richtext": ["content"],
    "card": ["body"], "testimonial": ["quote"],
}

RISKS = [
    ("table",           re.compile(r"^\s*\|.*\|\s*$", re.M)),
    ("table-separator", re.compile(r"^\s*\|?\s*:?-{3,}", re.M)),
    ("footnote",        re.compile(r"\[\^[^\]]+\]")),
    ("task-list",       re.compile(r"^\s*[-*]\s+\[[ xX]\]", re.M)),
    ("math",            re.compile(r"\$\$|\\\(|\\\[")),
    ("image",           re.compile(r"!\[[^\]]*\]\(")),
    ("heading-h4plus",  re.compile(r"^#{4,}\s", re.M)),
    ("definition-list", re.compile(r"^:\s{1,3}\S", re.M)),
    ("custom-container",re.compile(r"^:::", re.M)),
    ("abbreviation",    re.compile(r"^\*\[[^\]]+\]:", re.M)),
    ("reference-link",  re.compile(r"^\[[^\]]+\]:\s*\S", re.M)),
    ("raw-html",        re.compile(r"<[a-zA-Z][^>]*>")),
]

def texts(blocks):
    """Yield (widget_type, prop, value) for every Markdig-rendered prop."""
    try:
        doc = json.loads(blocks or "{}")
    except Exception:
        return
    for section in (doc.get("sections") or []):
        for col in (section.get("columns") or []):
            for w in (col.get("widgets") or []):
                wt, props = w.get("widgetType"), (w.get("props") or {})
                for key in MD_PROPS.get(wt, []):
                    if props.get(key):
                        yield wt, key, props[key]
                if wt == "accordion":
                    try:
                        for i, item in enumerate(json.loads(props.get("itemsJson") or "[]")):
                            if item.get("answer"):
                                yield wt, f"itemsJson[{i}].answer", item["answer"]
                    except Exception:
                        pass

con = sqlite3.connect(DB)
cols = {r[1] for r in con.execute("PRAGMA table_info(CmsPages)")}
fields = ["Id", "Title", "Slug", "BlocksJson"] + (["DraftBlocksJson"] if "DraftBlocksJson" in cols else [])
rows = con.execute(f"SELECT {','.join(fields)} FROM CmsPages WHERE TrashedAt IS NULL").fetchall()

total_props = affected = 0
found = {}
for row in rows:
    pid, title, slug = row[0], row[1], row[2]
    for blocks in row[3:]:
        for wt, prop, val in texts(blocks):
            total_props += 1
            hits = [name for name, rx in RISKS if rx.search(val)]
            if hits:
                affected += 1
                print(f"  {title} (/{slug})  {wt}.{prop}  -> {', '.join(hits)}")
                for h in hits:
                    found[h] = found.get(h, 0) + 1

print()
print(f"pages scanned            : {len(rows)}")
print(f"markdown props scanned   : {total_props}")
print(f"props needing a fallback : {affected}")
print(f"constructs found         : {found if found else 'none - inline editing is lossless for all current content'}")
