# Kachinco production slice — Windows acceptance

## Start

1. Extract the portable ZIP into a writable folder; launch `Kachinco.App.exe`.
   .NET is included by the portable publish. Source builds require .NET 10 SDK.
2. Install FFmpeg separately and put `ffmpeg.exe` and `ffprobe.exe` on PATH.
   Kachinco does not download or bundle these executables.
3. Recipe generation additionally requires Python 3 (`python` on Windows PATH).
   The distributed `recipe-worker.py` must remain beside the app.
4. Import MOV/MP4 video or WAV/MP3/M4A audio directly from the welcome screen, then create a landscape or
   portrait sequence; alternatively use File > New. Save as `.fkproj`.
   Source video/audio and generated MOV files remain external assets; keep them.

## Ordinary editing

- Import MOV/MP4 video or WAV/MP3/M4A audio; drag from the media bin onto a video/audio row.
- A drop places the full asset at the pointer's snapped time and extends sequence
  duration if necessary. One Undo reverses both changes. Empty-project default is
  8 seconds; Sequence > duration can change it without clipping existing items.
- Timeline header owns zoom, fit and snapping. Fit a one-hour sequence and verify
  both ends are visible. Drag clip bodies to move; edges trim. During each kind of
  drag, press Escape and verify the preview returns to its original range and Undo
  history is unchanged. Split uses the cursor.
- Add tracks from Sequence menu; duplicate from Clip menu. Inspector exposes
  enabled, position, scale, rotation, opacity, Normal/Screen, gain and mute.
- Subtitle menu edits captions and imports/exports SRT. Captions render bottom
  center in Yu Gothic with an outline. V1 files load and save as V2, preserving
  existing media/track/clip/caption identities. Back up before opening in old builds.

## Preview and output

- Play (Space) starts at the current playhead and prepares a short PCM/video window.
  Ruler/red-playhead drag updates the viewer; `フレーム更新` requests the current frame.
  Full / 1/2 / 1/4 is transient preview resolution. No whole-sequence preview MP4 is made.
- The actual output-device sample position drives playback. Scrub seeks silently and pauses;
  pause/resume retains the device queue, stop resets to zero. Affected edits locally re-prime;
  unrelated cached video survives audio edits. Missing media/device/decode failures are visible.
- File > export creates H.264/AAC MP4. Progress window supports cancellation.
  Existing output is replaced only after successful encoding; source paths cannot
  be used as output. Preview/export are flattened onto black. Generated MOV retains
  alpha. MOV/MP4 embedded audio is not mixed; import WAV/MP3/M4A separately.
- Follow [Issue #9 hands-on](windows-issue9.md) for current scrub/playback/thumbnail acceptance
  and Windows measurements. Hardware decoding/proxy management are not implemented.

## Clapper / Recipe

- Open Clapper / Recipe, enter name/range and optional point/rectangle in canvas
  pixels. Save. Named Clappers appear on the timeline ruler.
- Select a saved Clapper and enter e.g.:

```python
text(text="グエー", x=0, y=100, vx=180, size=64)
particles(count=24, x=100, y=200, vx=80, vy=-20, size=5)
```

- `検証` runs the restricted compiler. `生成 / 再生成` asks for a **new** MOV file.
  The proof supports up to 10 seconds, 128 calls and 2000 total primitives.
  `x/y` are relative to the Clapper origin, `vx/vy` pixels/second. Rectangle
  geometry clips drawing. Seed controls the deterministic particle layout.
- Generated output becomes an ordinary clip in one transaction. Select the saved
  Recipe to regenerate the same asset lineage. Existing clip positions/source
  ranges survive; incompatible shorter replacements fail. Old generated files are
  retained for Undo. Arbitrary imports/loops/attributes/calls are rejected.

## MCP

- 起動時のMCPは無効です。プロジェクトを開いてから「AI接続」で
  「読み取り専用で接続」または「編集を許可して接続」を選びます。
- 「接続情報をコピー」で得られる一時JSONの `command` / `args` / `env` を、
  ローカルstdioクライアントの起動に渡します。`mcp/Flamoris.Mcp.Bridge.exe` を使い、
  capabilityは `FLAMORIS_MCP_CAPABILITY` 環境変数だけで渡してください。
  capabilityを引数・設定ファイルへ保存しないでください。同時接続は1つです。
  正常なアイドル接続は時間切れになりません。緑はendpoint使用可能、接続中表示は
  認証済みbridge、AI処理中表示はforeground requestを表します。
- 読み取り専用は照会・Clapper解決・制限付きRecipe検証、編集許可は通常の
  型付き編集と共有Undo/Redoを追加します。ファイル操作は別の許可が必要です。
  今回は外部MCPによる素材登録・再リンク・Recipe生成・書き出しを無効にしています。
  素材の取り込み、生成、書き出しは通常のUIから利用できます。
- 停止・権限変更・Open/New・終了で古い接続は失効します。同じファイルを開き直す
  場合も再有効化が必要です。古い接続情報ウィンドウは閉じます。クリップボードや
  クライアント設定に残る古い接続情報は無効なので、新しい接続情報に差し替えてください。
- 読み込み失敗やファイル選択のキャンセルでは接続を維持します。読み込んだ文書を
  適用する直前に失効するため、その時点でrevision競合しても接続は無効になります。
- 接続先はWindowsの同一ユーザー／昇格境界に制限し、サーバーのWindows APIに
  remote-client rejectionを指定します。LANやクラウドから直接接続する機能ではありません。
- Core 1.1.0 / official C# SDK 2.2.0を使用します。呼び出しは `input` と `guard`
  を持つCore標準形式に変更されています。`mcp.context` でruntime/document/revisionを
  確認してください。Int64は十進文字列、時間は35,280,000 ticks/secondです。
  stale guardや応答消失時は再照会し、勝手に再実行しないでください。

### Automated package acceptance

From a fresh Windows checkout with the test driver's .NET SDK available:

```powershell
dotnet build Kachinco.slnx -c Release
dotnet run --project test/Kachinco.WindowsSmoke/Kachinco.WindowsSmoke.csproj -c Release --no-build
./product/packaging/publish-windows.ps1 -OutputDirectory artifacts/mcp-acceptance
dotnet run --project test/Kachinco.WindowsSmoke/Kachinco.WindowsSmoke.csproj -c Release --no-build -- --packaged artifacts/mcp-acceptance/Kachinco-win-x64
```

The driver uses Windows UI Automation and the official SDK. The **separate published
editor and bridge** receive System32-only PATH, so ordinary attachment/edit/history
needs no developer .NET, Node, Python or FFmpeg. Existing worker/media tests run
separately with their declared dependencies. The official MCP SDK is now a required production dependency. Package tests must never ship in the
bundle; normal CI continues to upload ZIPs only on manual runs, retaining 3 days.

## Human acceptance — not performed by CI

- [ ] Dragged clips land on the intended row/time at multiple zoom/scroll levels.
- [ ] Wheel/scroll, long-clip placement and pane sizes feel natural at actual DPI.
- [ ] Normal/Screen overlay looks correct; text is legible in both presets.
- [ ] Playback pause/seek and WAV synchronization are perceptually correct.
- [ ] Caption edit/import/export and save/reopen retain timing and text.
- [ ] A-1 + グエー generates; regenerate after moving/trimming the generated clip.
- [ ] Undo/Redo and reopen preserve authoring/provenance; old source files still exist.
- [ ] MCPで字幕／トラックを編集し、画面で確認してCtrl+Z／Ctrl+Yを押す。
- [ ] 日本語／英語の許可表示、接続コピー、100/125/150/200% DPIを確認する。
- [ ] 停止・権限降格・同じ文書の再Open後に古いクライアントから照会／編集できない。
- [ ] 別Windowsユーザー・昇格差・別マシンからのnamed-pipe接続が拒否される。
- [ ] Cancel preview/export/Recipe and close/reopen without stale dialogs or media.

Automated evidence is recorded in the PR; startup checks are not visual acceptance.
Run the focused [Issue #7 editor checklist](windows-issue7.md) for startup, clip
visualization, family styling, geometry, coordinate mapping and transport feedback.
Phase 9 remains needs-driven for performance and external integrations. Kinetai,
AudioAnalyzer, unrestricted Python, extra effects and broader media formats are
not implemented by this slice.

## Issue #15 media acceptance

The portable package does not bundle FFmpeg/ffprobe. Use the external executables
on PATH from the Start section; no pre-conversion or different MP4 editing path.

- [ ] Pick MOV, MP4, WAV, MP3 and M4A (including uppercase extensions / spaces).
- [ ] Drop one supported file from Explorer into the media bin; invalid files show
  a diagnostic without changing Project/history. Multi-file and direct external
  timeline drops are not supported in this slice.
- [ ] Media bin / inspector show Video or Audio plus the actual extension.
- [ ] Drag each registered asset onto its matching timeline row; scrub/play and
  verify thumbnails or waveforms. MOV/MP4 embedded audio remains excluded.
- [ ] Relink MOV to MP4 and WAV to MP3/M4A of sufficient duration. Confirm clip
  placement and identities remain, including Undo/Redo and save/reopen.
- [ ] A corrupt file, audio renamed to MP4, or incompatible/too-short replacement
  produces a clear error; old media and timeline remain intact.

The existing packaged smoke now imports five real generated formats through the
published picker using a PATH restricted to System32 and the installed media-tool
directories. It inspects the same session over read-only MCP and tests UI Undo/Redo
and invalid-file rejection. This is separate from the unchanged System32-only MCP
runtime proof. Physical pointer/DPI/playback perception is still manual acceptance.
