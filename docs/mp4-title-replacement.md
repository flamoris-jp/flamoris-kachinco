# 完成MP4の先頭1フレームを差し替える / Replace an exported MP4's first frame

1. **タイトル画像 → 完成MP4の先頭フレームを差し替え…** を開く。
2. 完成MP4を選び、選択済みタイトル画像を使うか、別のPNG/JPEG/WebPを選ぶ。
3. 実際の出力サイズに合わせた先頭画像と、再圧縮・コピーするフレーム数を確認する。
4. **別MP4として保存…** から新しい名前で保存する。

元MP4と編集中のプロジェクトは変更しません。先頭フレームの「追加」ではなく「置き換え」なので、動画のフレーム数と長さ、音声の開始位置は変わりません。タイトル画像を1フレームとして見せる枠がすでにある場合に向いています。先頭に重要な映像がある動画では、その1フレームが失われる点をプレビューで確認してください。

画像は縦横比を保って中央に収め、余白と透明部分を黒にします。YouTubeに別途アップロードするサムネイルやMP4のカバーアートとは異なり、動画そのものの先頭画像を変更します。

映像は次の適切なキーフレームまでを再圧縮し、その後をコピーします。冒頭の再圧縮区間では画質が少し変わることがあります。音声は再圧縮しません。コピー可能な区切りがない短い動画等では、映像全体の再圧縮になることを保存前に表示します。通常のタイムライン書き出し処理は実行しません。

対象はH.264/AVC、8bit yuv420p、正方形ピクセル、プログレッシブ、先頭時刻0のCFR MP4です。映像1トラック、AAC音声は0または1トラック、偶数の寸法（最大4096×4096）、最大120fps。回転・可変フレームレート・別コーデック・追加トラック・不連続な時刻は対応外です。検証に失敗した場合は完成ファイルを保存せず、理由を表示します。キャンセル時も元動画は残ります。

## English

Choose **Title image → Replace first frame in completed MP4…**, select the completed
MP4 and an image (or reuse the current project's title selection), review the fitted
preview and encoding/copy range, then save under a new filename. This replaces frame
zero without insertion or project edits. It re-encodes the opening GOP with libx264
fast/CRF 18 and copies later video and original AAC. The opening can lose some quality;
if no suitable later IDR exists, the preview explicitly reports full-video encoding.
Cancellation/failure does not publish incomplete output or overwrite input files.

MP4の総尺ヘッダーにはミリ秒単位の丸めがあり、再muxで1ms程度の表記差が生じる場合があります。実際のフレーム数・表示時刻・音声時刻は維持します。NVIDIA書き出しとCPU書き出しで異なるデコード時刻の開始位置も、接続前に合わせます。

See [ADR 0023](decisions/0023-exported-mp4-first-frame.md) for supported input and validation.
