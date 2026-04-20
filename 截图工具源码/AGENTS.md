# 截图工具源码 Guide

This directory contains an independent Python screenshot utility used by the desktop app.

## Role and entrypoints

- The main tray-based tool is `lite_shot.py`.
- `screenshot_tool.py` appears to be an older/alternate implementation and should not be treated as the primary integration target unless the task explicitly requires it.
- The desktop app launches the screenshot tool through Python/Pythonw, not through .NET embedding.

## Integration facts

- `src/QuickReply.Desktop\Services\ScreenshotToolService.cs` looks for `lite_shot.py` and starts it with `pythonw` when possible.
- Runtime settings are written to `%AppData%\QuickReplyTool\ScreenshotTool\lite_shot_settings.json`.
- The repository also contains launcher helpers such as `launch_screenshot_tool.bat`, `启动截图工具.bat`, and `launch_screenshot_tool.vbs`.

## Dependency expectations

- Current source implies these Python dependencies:
  - `Pillow`
  - `PySide6`
  - `pywin32`
- `pywinauto` is optional enhancement code and is handled defensively in `lite_shot.py`.
- This repository does not currently provide a `requirements.txt`.
- `scripts\bootstrap-dev-env.ps1` does not install these Python packages.

## Change rules

- Keep this tool Windows-specific unless a task explicitly broadens platform scope.
- Preserve the single-instance, tray, hotkey, and save-directory behavior unless intentionally changing the product.
- Keep integration assumptions synchronized with `ScreenshotToolService` in the desktop app.
- If `lite_shot.py` or `ScreenshotToolService` changes behavior, do not stop at updating repository source only. Also verify the packaged/runtime copy that the user actually launches, such as `artifacts\QuickReplyTool-Portable-*\app\ScreenshotTool\lite_shot.py` and any installed runtime directory like `C:\Users\24173\QuickReplyTool\ScreenshotTool\lite_shot.py`.
- When hotkey, copy/save, or tray behavior changes, prefer updating the whole packaged desktop runtime rather than patching only one script file in isolation, so `QuickReplyTool.exe`, `ScreenshotTool`, and bundled `LocalCloudService` stay aligned.

## Validation

- For code changes here, at minimum verify Python import/runtime assumptions and launcher flow remain coherent.
- For behavior fixes, verify both repository source and the real packaged/runtime copy reflect the same change before closing the task.
- Do not claim the repository offers automated dependency installation unless that capability is actually added.
