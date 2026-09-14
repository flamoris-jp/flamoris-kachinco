using System.Globalization;

namespace Kachinco.App;

// Japanese-first presentation strings; no domain messages or persistent labels are translated here.
internal static class EditorText
{
    public static CultureInfo Culture { get; set; } = CultureInfo.GetCultureInfo("ja-JP");
    public static string Choose(string japanese, string english) => Culture.TwoLetterISOLanguageName == "ja" ? japanese : english;
    public static string ImportGuidance => Choose("MOV / WAVを読み込んで始めましょう。保存済みのプロジェクトも開けます。", "Import MOV / WAV to start, or open a saved project.");
    public static string SequenceGuidance => Choose("素材を読み込みました。横長または縦長のシーケンスを作成してください。", "Media is ready. Create a landscape or portrait sequence.");
    public static string PlacementGuidance => Choose("素材を映像・音声トラックへドラッグするか、「配置」を押してください。", "Drag media onto a video/audio track, or choose Place.");
    public static string EditGuidance => Choose("本体をドラッグして移動・両端で長さを調整。Spaceで再生。", "Drag the body to move or the edges to trim. Space plays/pauses.");
    public static string Preparing => Choose("プレビューを準備中", "Preparing preview");
    public static string Rendering => Choose("プレビューを描画中", "Rendering preview");
    public static string Playing => Choose("再生中", "Playing");
    public static string Paused => Choose("一時停止", "Paused");
    public static string Stopped => Choose("停止", "Stopped");
    public static string Failed => Choose("プレビューに失敗しました", "Failed");
    public static string VisualLoading => Choose("表示を作成中…", "Building visualization…");
    public static string VisualFailed => Choose("表示を作成できません", "Visualization unavailable");
}
