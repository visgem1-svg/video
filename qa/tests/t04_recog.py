"""Recognitions: live snapshots (broken images after retries?), filters, search, history."""
import sys, time, re
sys.path.insert(0, __file__.rsplit("\\",1)[0].rsplit("/",1)[0])
from common import *
s = Session(); p = s.page
s.goto("/recognitions"); time.sleep(25)
IMG = "() => [...document.querySelectorAll('img')].map(i => ({src:i.src.split('/api/')[1], ok:i.complete && i.naturalWidth>0}))"
imgs = p.evaluate(IMG); print("live imgs", len(imgs), "broken", [i for i in imgs if not i["ok"]][:10])
print("cards", p.evaluate("document.querySelectorAll('img').length"), "text", p.inner_text("main")[:300].replace("\n"," | "))
s.shot("t04-live-after25s")
# filters
for b in ["Plates","Faces","All"]:
    p.locator("button", has_text=re.compile("^"+b+"$")).click(); time.sleep(1.5)
    print(b, p.evaluate("[...document.querySelectorAll('main img')].length"), p.inner_text("main")[150:400].replace("\n"," | "))
p.locator("button", has_text=re.compile("^Plates$")).click()
inp = p.get_by_placeholder("Search plate / name"); inp.fill("PL8REC"); time.sleep(1.5)
print("search PL8REC ->", p.inner_text("main")[150:500].replace("\n"," | "))
inp.fill("lena"); p.locator("button", has_text=re.compile("^All$")).click(); time.sleep(1.5)
print("search lena ->", p.inner_text("main")[150:500].replace("\n"," | "))
inp.fill("zzzz"); time.sleep(1); print("search zzzz ->", p.inner_text("main")[150:400].replace("\n"," | ")); inp.fill("")
p.locator("button", has_text="History").click(); time.sleep(4); s.shot("t04-history")
imgs = p.evaluate(IMG); print("history imgs", len(imgs), "broken", [i for i in imgs if not i["ok"]][:10])
print(p.inner_text("main")[:700].replace("\n"," | "))
s.report(); s.close()
