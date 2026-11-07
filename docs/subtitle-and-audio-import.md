# Subtitle placement and MP4 audio import

Acceptance: the media bin and timeline context menus offer SRT import at the red
playhead. SRT zero maps to the captured playhead tick, preserving cue gaps,
durations, ordering and leading silence. Ordinary menu import retains absolute
SRT times. One revision-guarded batch creates a subtitle track and extends the
sequence when needed; Undo/Redo includes the entire import.

Caption editing exposes an explicit “Style all captions” entry and “Select all”
button. All means all captions in the active sequence, including disabled tracks.
Only edited fields change through existing native PatchCaptionStyles commands.
Other sequences, individual positions, text and timing are preserved.

The Windows picker and media-bin file drop prepare audio-bearing MP4s as video
plus a separate PCM WAV asset. Infrastructure owns FFmpeg invocation and probing;
the UI registers both assets in a single ordinary batch. The original MP4 is
unchanged and retains its existing video-only evaluation semantics. WAVs decode
the first audio stream from its beginning, preserving its sample rate/channels,
including any audio tail. This is asset registration, not automatic placement.

Generated WAVs use unique immutable paths under the user's Kachinco application
data `imported-audio` directory. Failed preparation/commit removes only its own
unpublished file; successful files survive Undo for Redo and project reopening.
Never overwrite a source or reuse an unverified same-named WAV. Extraction failure
is reported and commits neither asset. Silent MP4s import as video only. There is
no project schema, native evaluator or MCP contract change; existing MCP media
registration semantics remain unchanged. This supersedes ADR 0006's metadata-only
audio behavior for Windows MP4 imports.

Validation: shifted SRT batch/history/overflow tests, real MP4/WAV extraction,
silent/corrupt input and prepared-file lifetime tests, Windows all-caption UI
checks. Physical Windows right-click, listening and font appearance remain manual.

## Windowsでの操作

- 素材欄またはタイムラインの空白を右クリック → **SRTから字幕クリップを作成（再生ヘッド位置）…**
  → SRTを選択。SRTの0秒が再生ヘッドに一致するため、最初の字幕までの待ち時間も残る。
- **字幕 → 全字幕のスタイルを一括変更…** → フォント・文字色を変更。
  通常の字幕編集画面の **全字幕を選択してスタイルを一括変更** からも操作できる。
- 音声付きMP4をファイル選択または素材欄へのドロップで読み込むと、映像素材と
  `元の名前 — audio.wav` が登録される。音声素材をAトラックへ置いて使用する。
  WAVの実体は `%LOCALAPPDATA%\FLAMORIS\Kachinco\imported-audio` に保持される。


## Caption clip text editing

The UI calls the position indicator 再生ヘッド / Playhead. The context menu can
create a three-second caption clip there without a file picker; SRT import creates
ordinary caption clips whose text can also be changed in the upper-right inspector.
The inspector supports multiline text, Apply / Ctrl+Enter and Escape to reload.
Each apply is one native UpdateCaption batch and preserves identity, timing,
enabled state and style. Selection changes apply valid drafts before switching;
invalid drafts remain visible. Revision/document guards reject stale writes.

Ctrl+S / Save As commits any valid pending inspector text before taking the saved
project snapshot. Pending text participates in the existing unsaved-change prompt.
SRT export also commits pending text, then writes a chosen output file. Project
files retain styling; SRT retains text/timing only. The source SRT is never silently
overwritten. No persistence schema or MCP changes are needed.

操作：字幕クリップを選択 → 右上の「字幕クリップの文章」を編集 →「字幕を適用」。
Ctrl+Sでプロジェクトに保存。「字幕 → SRTを書き出す…」で編集後のSRTを保存できる。
