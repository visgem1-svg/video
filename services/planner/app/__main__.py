import os

import uvicorn

if __name__ == "__main__":
    uvicorn.run("app.main:app_factory", factory=True, host="0.0.0.0",
                port=int(os.getenv("PORT", "8010")), workers=1, log_level="warning")
