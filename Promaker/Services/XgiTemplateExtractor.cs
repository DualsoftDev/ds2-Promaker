using System;
using System.IO;
using System.Reflection;

namespace Promaker.Services;

/// <summary>
/// AAStoPLC.dll 의 EmbeddedResource "AAStoPLC.XGI_Template.xml" 을
/// 사용자 AppData 경로에 추출 — 외부에 노출되는 사본 없음.
/// </summary>
internal static class XgiTemplateExtractor
{
    private const string ResourceName = "AAStoPLC.XGI_Template.xml";

    /// <summary>destPath 가 이미 존재하면 skip, 없으면 임베디드 리소스로부터 새로 작성.</summary>
    public static bool ExtractIfMissing(string destPath)
    {
        if (string.IsNullOrEmpty(destPath)) return false;
        if (File.Exists(destPath)) return true;

        try
        {
            var dir = Path.GetDirectoryName(destPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            // AAStoPLC 어셈블리는 Plc.Xgi 호환 shim 타입(SignalPipelineV2.SignalRow)으로 식별.
            var asm = typeof(Plc.Xgi.SignalPipelineV2.SignalRow).Assembly;
            using var stream = asm.GetManifestResourceStream(ResourceName);
            if (stream == null)
                return false;

            // 임시 파일에 다 쓴 뒤 이름을 바꾼다 — 복사 중 실패하면 반쯤 쓴 파일이 남고, 다음부터는 File.Exists 에 걸려
            // 다시 추출하지 않아 태그 마법사가 깨진 템플릿을 계속 읽었다.
            var tempPath = destPath + ".tmp";
            try
            {
                using (var file = File.Create(tempPath))
                    stream.CopyTo(file);
                File.Move(tempPath, destPath, overwrite: true);
            }
            finally
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }
            return true;
        }
        catch
        {
            return false;
        }
    }
}
