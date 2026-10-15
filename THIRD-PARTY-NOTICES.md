# Third-party notices

ScweenSpit includes work from the projects below. Their licence terms are reproduced in full, as
those licences require.

---

## claude-usage-widget

The claude.ai usage strip (`ClaudeUsage.cs`, `UsageStrip.cs`) is derived from
**claude-usage-widget** by Niccolò Sabato — <https://github.com/niccolo-sabato/claude-usage-widget>.

What is derived from it:

- Which claude.ai endpoints carry the usage figures, and the browser-shaped request headers the
  edge in front of them expects.
- Reading Claude Code's OAuth token from `~/.claude/.credentials.json` (`claudeAiOauth.accessToken`)
  and querying `https://api.anthropic.com/api/oauth/usage` with `anthropic-beta: oauth-2025-04-20`
  and a 15-minute pause on HTTP 429, falling back between OAuth and the browser `sessionKey`.
- How an organisation is chosen for an account that belongs to several, and that `/api/bootstrap`
  reports the one the browser last used.
- The shape of the usage reply: the `five_hour` and `seven_day` buckets, and that the weekly
  per-model limit moved out of `seven_day_sonnet` into the `limits` list.
- That claude.ai rotates the session cookie mid-session, and that the replacement has to be written
  back — including the checks that stop a cleared cookie from destroying a working key.
- The colour scale the bars are drawn on, and the percentages it steps at.

The implementation is not a copy: the original is a Python/tkinter application that draws its own
window, and this is C# that paints into a bar which is already running.

```
MIT License

Copyright (c) 2026 Niccolò Sabato

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

---

## AntigravityQuota & antigravity-usage

The Antigravity quota strip (`AntigravityUsage.cs`) adapts local language-server protocol details
from **AntigravityQuota** by Henrik-3 (<https://github.com/Henrik-3/AntigravityQuota>) and
**antigravity-usage** by skainguyen1412 (<https://github.com/skainguyen1412/antigravity-usage>).

What is derived from them:

- Authenticating with Google OAuth and querying `https://cloudcode-pa.googleapis.com/v1internal:loadCodeAssist`
  and `https://cloudcode-pa.googleapis.com/v1internal:fetchAvailableModels` to read model quotas
  without Antigravity running locally.
- Locating the running Antigravity `language_server` process and reading `--csrf_token` and
  `--extension_server_port` from its command line as a local fallback.
- Querying the Connect-RPC `GetUserStatus` endpoint on
  `/exa.language_server_pb.LanguageServerService/GetUserStatus` with `Connect-Protocol-Version: 1`
  and `X-Codeium-Csrf-Token`.
- Reading per-model quota windows (`remainingFraction`, `resetTime`, `isExhausted`) from
  `models.*.quotaInfo` and `userStatus.cascadeModelConfigData.clientModelConfigs`.

```
MIT License

Copyright (c) 2025 Henrik-3
Copyright (c) 2026 Khai Nguyen (skainguyen1412)

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

---

ScweenSpit is not affiliated with, endorsed by, or sponsored by Anthropic or Google. "Claude" and
"Antigravity" are trademarks of their respective owners, used here only to say which service the
usage figures come from.
