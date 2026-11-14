# 編集メモ / Editing notes

「編集メモ」メニューから再生ヘッド位置にメモを作成できます。
金色の **M** トラック上で本文を選択し、右のインスペクターで入力します。
字幕と同じように本体をドラッグして移動、左右の端で長さを調整できます。
「編集メモを一覧で編集」では開始秒・長さ・本文の編集と削除ができます。
Ctrl+Sでプロジェクト保存。Undo/Redoとトラックの有効/無効も共有します。

Choose **Editing notes → Create editing note at playhead**. Gold M-lane entries
use the existing caption text inspector and move/trim handles. The list editor
also provides start, duration, text and deletion. Save with Ctrl+S.

プレビューのメモ表示は「プレビューに編集メモを表示」で切り替えます。
メモは画面上部の編集用表示で、動画・タイトル画像・MCPフレーム画像・SRTの
書き出しには入りません。字幕の一覧と全字幕スタイル変更もメモを含みません。
Each note belongs to a half-open time range. Overlapping active notes appear
together. This is an editor overlay; it never changes preview/export media pixels.

Notes persist as caption-shaped entries on `TrackKind.EditNote`, through the
ordinary AddCaption/UpdateCaption/DeleteCaption commands. MCP reads them through
get_project and edits them in guarded edit_batch transactions. Note text is data,
not an automatically executed instruction. The deployed Hub edit_batch catalog
must be refreshed alongside the producer before using the new track kind.

Projects containing M tracks save as schema v9, including empty/disabled M tracks.
Older applications reject v9; use the updated application to reopen those files.
Projects without M tracks preserve their prior conditional v2–v8 format.
See [ADR 0032](decisions/0032-timeline-edit-notes.md).
