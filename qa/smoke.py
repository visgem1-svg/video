"""Smoke test: open the local dev app in headless Chromium and report basic health."""
from pathlib import Path

from playwright.sync_api import sync_playwright

URL = "http://localhost:5173"
QA_DIR = Path(__file__).resolve().parent
SHOT = QA_DIR / "screenshots" / "smoke-home.png"


def main() -> int:
    console_errors: list[str] = []
    failed: list[str] = []

    with sync_playwright() as p:
        browser = p.chromium.launch(
            headless=True,
            args=[
                "--use-fake-ui-for-media-stream",
                "--autoplay-policy=no-user-gesture-required",
            ],
        )
        page = browser.new_page()

        page.on(
            "console",
            lambda msg: console_errors.append(msg.text) if msg.type == "error" else None,
        )
        page.on("pageerror", lambda exc: console_errors.append(f"pageerror: {exc}"))
        page.on(
            "response",
            lambda resp: failed.append(f"{resp.status} {resp.url}") if resp.status >= 400 else None,
        )
        page.on(
            "requestfailed",
            lambda req: failed.append(f"FAILED {req.url} ({req.failure})"),
        )

        page.goto(URL, wait_until="domcontentloaded")
        SHOT.parent.mkdir(parents=True, exist_ok=True)
        page.screenshot(path=str(SHOT), full_page=True)
        title = page.title()
        browser.close()

    print(f"title: {title!r}")
    print(f"console_errors: {len(console_errors)}")
    for line in console_errors:
        print(f"  - {line}")
    print(f"failed_requests: {len(failed)}")
    for line in failed:
        print(f"  - {line}")
    print(f"screenshot: {SHOT}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
